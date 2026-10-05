using System.Collections.Concurrent;
using System.Security.Cryptography;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Core.OneDrive;
using CloudBay.Core.Transfers;

namespace CloudBay.Application;

public sealed partial class ClientController
{
    private readonly TransferBandwidthBudget _transferBandwidth = new();
    private TransferJobEngine? _cloudTransferEngine;
    private B2CloudStore? _cloudTransferB2;
    private readonly SemaphoreSlim _cloudTransferProviderGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _cloudTransferRuns = new(StringComparer.Ordinal);
    private readonly object _cloudTransferRunGate = new();
    private readonly ConcurrentDictionary<string, byte> _globallyPausedCloudTransfers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _policyPausedCloudTransfers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _userPausedCloudTransfers = new(StringComparer.Ordinal);
    private int _refreshingCloudPausePolicy;
    private readonly object _cloudPolicyPauseStorageGate = new();
    private string? _cloudPolicyPauseMessage;
    private int _cloudPauseGeneration;
    private readonly ConcurrentDictionary<string, SyncSnapshot> _cloudTransferSnapshots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cloudBackupStopGate = new(1, 1);
    private string[] _interruptedCloudTransfers = [];
    private int _recoveringCloudTransfers;
    public IReadOnlyList<TransferJobSnapshot> CloudTransferJobs => _cloudTransferEngine?.Snapshots() ?? [];

    private void InitializeCloudTransfers()
    {
        if (_settingsRecoveryError is not null) return;
        try
        {
            var journal = new TransferJobJournal(Path.Combine(_storage.DirectoryPath, "CloudTransfers", "jobs.sqlite"), new UserCheckpointProtector());
            _interruptedCloudTransfers = journal.RecoverableJobIds.ToArray();
            var limits = TransferLimits.For(Settings);
            _cloudTransferEngine = new(journal,
                ResolveTransferEndpoint, Math.Max(limits.Uploads, limits.Downloads));
            _cloudTransferEngine.Changed += CloudTransferChanged;
            _cloudTransferEngine.Activity += AddActivity;
            var autoPaused = _storage.LoadPolicyPausedCloudTransfers().Where(id => _cloudTransferEngine.Snapshots()
                .Any(job => job.Plan.Id == id && job.State == TransferJobState.Paused)).ToArray();
            foreach (var id in autoPaused) _policyPausedCloudTransfers[id] = 0;
            _interruptedCloudTransfers = _interruptedCloudTransfers.Concat(autoPaused).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var job in _cloudTransferEngine.Snapshots()) CloudTransferChanged(job);
            RestoreReleasedCloudBackupRoots();
            if (_storage.LoadCloudBackupStopIntent() is { } pending)
            {
                ValidateCloudBackupStopIntent(pending);
                // Protect the saved cloud source before native sync can start after a crash.
                ReleaseCloudBackupRoot(pending.Name);
            }
        }
        catch (Exception error)
        {
            if (_cloudTransferEngine is not null)
            {
                _cloudTransferEngine.Changed -= CloudTransferChanged;
                _cloudTransferEngine.Activity -= AddActivity;
                _cloudTransferEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _cloudTransferEngine = null;
            }
            if (File.Exists(Path.Combine(_storage.DirectoryPath, "cloud-backup-stop.json")) || File.Exists(Path.Combine(_storage.DirectoryPath, "cloud-backup-stopped.json")))
            {
                _settingsRecoveryError = error.Message;
                _rootSnapshot = new(ClientState.Attention, "Stopped cloud backup ownership needs recovery: " + error.Message);
                Snapshot = _rootSnapshot;
            }
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, "", "Cloud transfer recovery needs attention: " + error.Message));
        }
    }

    private ITransferEndpoint ResolveTransferEndpoint(TransferLocation location) => location.Provider switch
    {
        "onedrive" => new OneDriveTransferEndpoint(GetOneDriveClient(location.AccountId), location),
        "local" => new LocalTransferEndpoint(location),
        "b2" when !_disconnecting && _pendingDisconnectMode is null && _cloudTransferB2 is not null && location.AccountId == Settings.AccountId => new B2TransferEndpoint(_cloudTransferB2, location),
        _ => throw new IOException("Reconnect the original cloud account before continuing this job.")
    };

    private async Task EnsureCloudTransferProvidersAsync(CancellationToken ct)
    {
        if (_disconnecting || _pendingDisconnectMode is not null) throw new IOException("Finish disconnecting this B2 account before starting another transfer.");
        await _cloudTransferProviderGate.WaitAsync(ct);
        try
        {
            if (_cloudTransferB2 is not null) return;
            var credentials = _storage.LoadCredentials() ?? throw new IOException("Connect the B2 account before starting a cloud transfer.");
            var store = new B2CloudStore(bandwidthBudget: _transferBandwidth);
            try
            {
                ConfigureTransport(store, Settings);
                var account = await store.ConnectAsync(credentials, ct);
                if (account.AccountId != Settings.AccountId) throw new IOException("The B2 credential account no longer matches this saved transfer.");
                _cloudTransferB2 = store;
            }
            catch { store.Dispose(); throw; }
        }
        finally { _cloudTransferProviderGate.Release(); }
    }

    private async Task ResetB2CloudTransferConnectionAsync(CancellationToken ct)
    {
        var jobs = CloudTransferJobs.Where(job => (job.Plan.Source.Provider == "b2" || job.Plan.Destination.Provider == "b2") &&
            job.State is TransferJobState.Discovering or TransferJobState.Running).ToArray();
        await Task.WhenAll(jobs.Select(job => PauseCloudTransferAsync(job.Plan.Id)));
        await _cloudTransferProviderGate.WaitAsync(ct);
        try { _cloudTransferB2?.Dispose(); _cloudTransferB2 = null; }
        finally { _cloudTransferProviderGate.Release(); }
    }

    public async Task<TransferFolderPage> BrowseTransferFoldersAsync(TransferLocation location, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (location.Provider == "b2") await EnsureCloudTransferProvidersAsync(operation.Token);
        return await ResolveTransferEndpoint(location).BrowseFoldersAsync(cursor, operation.Token);
    }

    public async Task<string> StartCloudTransferAsync(TransferLocation source, TransferLocation destination,
        TransferOperation operation, TransferConflictPolicy conflicts, IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default)
    {
        EnsureSettingsHealthy();
        var engine = _cloudTransferEngine ?? throw new IOException("Cloud transfer checkpoints need recovery before another job can start.");
        if (source.Provider == destination.Provider) throw new IOException("Choose different source and destination providers.");
        ValidateNativeCloudMove(source, operation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (source.Provider == "b2" || destination.Provider == "b2") await EnsureCloudTransferProvidersAsync(linked.Token);
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source, destination, operation, conflicts,
            exclusions.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow);
        await engine.CreateAsync(plan, linked.Token);
        QueueCloudTransferRun(plan.Id);
        return plan.Id;
    }

    private async Task RecoverCloudTransfersAsync(CancellationToken ct)
    {
        if (_cloudTransferEngine is null || Interlocked.Exchange(ref _recoveringCloudTransfers, 1) != 0) return;
        try
        {
            if (_storage.LoadCloudBackupStopIntent() is { } pending)
            {
                ValidateCloudBackupStopIntent(pending);
                if (Settings.Backups.Any(folder => folder.Name.Equals(pending.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var review = await PreviewStopBackupAsync(pending.Name, pending.RestorePath, Core.Sync.BackupTransferMode.None, ct);
                    await DisableReviewedBackupAsync(review, ct);
                }
                if (!Windows.KnownFolderBackup.GetPath(pending.Name).Equals(pending.RestorePath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The interrupted cloud backup stop's Windows location changed. Review it before continuing.");
                ReleaseCloudBackupRoot(pending.Name);
                if (!CloudTransferJobs.Any(job => job.Plan.Id == pending.Plan.Id)) await _cloudTransferEngine.CreateAsync(pending.Plan, ct);
                _storage.ClearCloudBackupStopIntent();
                _interruptedCloudTransfers = _interruptedCloudTransfers.Append(pending.Plan.Id).Distinct(StringComparer.Ordinal).ToArray();
            }
            if (_interruptedCloudTransfers.Length == 0) return;
            var unfinished = new List<string>();
            foreach (var id in _interruptedCloudTransfers)
            {
                try
                {
                    var plan = CloudTransferJobs.Single(job => job.Plan.Id == id).Plan;
                    if (plan.Source.Provider == "b2" || plan.Destination.Provider == "b2") await EnsureCloudTransferProvidersAsync(ct);
                    QueueCloudTransferRun(id, resume: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    unfinished.Add(id);
                    AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, id, "Interrupted cloud transfer needs attention: " + error.Message));
                }
            }
            _interruptedCloudTransfers = unfinished.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, "", "Interrupted cloud transfer needs attention: " + error.Message)); }
        finally { Interlocked.Exchange(ref _recoveringCloudTransfers, 0); }
    }

    public async Task<string> StopBackupAndStartCloudTransferAsync(Windows.BackupRestoreReview reviewed,
        TransferLocation source, TransferLocation destination, TransferOperation operation, TransferConflictPolicy conflicts,
        IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
    {
        if (reviewed.TransferMode != Core.Sync.BackupTransferMode.None) throw new ArgumentException("Cloud backup stop changes only the Windows mapping.", nameof(reviewed));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _cloudBackupStopGate.WaitAsync(linked.Token);
        try
        {
            await EnsureCloudTransferProvidersAsync(linked.Token);
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source, destination, operation, conflicts, exclusions.ToArray(), DateTimeOffset.UtcNow);
            TransferValidation.ValidatePlan(plan);
            var intent = new ClientStorage.CloudBackupStopIntent(reviewed.Folder.Name, reviewed.DestinationPath, plan);
            ValidateCloudBackupStopIntent(intent);
            _storage.SaveCloudBackupStopIntent(intent);
            ReleaseCloudBackupRoot(intent.Name);
            await DisableReviewedBackupAsync(reviewed, linked.Token);
            await (_cloudTransferEngine ?? throw new IOException("Cloud transfer recovery needs attention.")).CreateAsync(plan, linked.Token);
            _storage.ClearCloudBackupStopIntent();
            QueueCloudTransferRun(plan.Id);
            return plan.Id;
        }
        finally { _cloudBackupStopGate.Release(); }
    }

    public async Task WaitForCloudTransferAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_cloudTransferRuns.TryGetValue(id, out var running)) await running.WaitAsync(cancellationToken);
        var job = CloudTransferJobs.Single(item => item.Plan.Id == id);
        if (job.State != TransferJobState.Completed) throw new IOException(job.Error ?? "Cloud transfer is " + job.State.ToString().ToLowerInvariant() + ". Existing Windows folder mapping was retained; finish the job in Activity before enabling this backup.");
    }

    private void ValidateCloudBackupStopIntent(ClientStorage.CloudBackupStopIntent intent)
    {
        var name = Core.Sync.PathRules.ValidateRelative(intent.Name);
        if (name.Contains('/') || intent.Plan.Source != GetB2TransferRoot(Settings.BucketId, Settings.BucketName, Settings.Prefix + name + "/") ||
            intent.Plan.Destination.Provider != "onedrive" || !Path.IsPathFullyQualified(intent.RestorePath) ||
            IsNested(intent.RestorePath, Settings.RootPath) || IsNested(Settings.RootPath, intent.RestorePath))
            throw new IOException("The saved cloud backup stop does not match this account and Windows folder.");
    }

    private void ValidateNativeCloudMove(TransferLocation source, TransferOperation operation)
    {
        if (operation != TransferOperation.Move || source.Provider != "b2" || source.AccountId != Settings.AccountId || source.ContainerId != Settings.BucketId) return;
        static bool Overlaps(string first, string second) => first.StartsWith(second, StringComparison.Ordinal) || second.StartsWith(first, StringComparison.Ordinal);
        var nativePrefixes = Settings.CustomBackups.Select(folder => folder.Prefix).Prepend(Settings.Prefix).ToArray();
        if (!nativePrefixes.Any(prefix => Overlaps(source.Path, prefix))) return;
        var released = _storage.LoadCloudBackupRelinquishedRoots().Any(root => root.RootPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase) &&
            source.Path.StartsWith(Settings.Prefix + root.RelativePath + "/", StringComparison.Ordinal) &&
            Settings.SelectedExclusions.Contains(new SelectedExclusion(root.RootPath, root.RelativePath, true)));
        if (!released) throw new IOException("This B2 source is still owned by native folder sync. Stop its folder backup and select the direct OneDrive transfer there, or choose Copy. CloudBay must relinquish this source before moving its cloud files.");
    }

    private void ReleaseCloudBackupRoot(string name)
    {
        var prior = _storage.LoadCloudBackupRelinquishedRoots();
        var existing = prior.SingleOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && item.RootPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase));
        var exclusion = new SelectedExclusion(Settings.RootPath, name, true);
        var alreadyPresent = Settings.SelectedExclusions.Contains(exclusion);
        // Persist ownership before settings so a crash cannot start native sync on this source.
        if (existing is null) _storage.SaveCloudBackupRelinquishedRoots(prior.Append(new ClientStorage.CloudBackupRelinquishedRoot(name, Settings.RootPath, name, !alreadyPresent)).ToArray());
        if (!alreadyPresent)
        {
            Settings = Settings with { SelectedExclusions = Settings.SelectedExclusions.Append(exclusion).ToList() };
            _storage.SaveSettings(Settings); _engine?.Configure(Settings);
        }
    }

    private void RestoreReleasedCloudBackupRoots()
    {
        foreach (var root in _storage.LoadCloudBackupRelinquishedRoots().Where(root => root.RootPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (root.Name != root.RelativePath || root.RelativePath.Contains('/') || root.RelativePath.Contains('\\'))
                throw new InvalidDataException("The stopped cloud backup ownership record has an invalid folder.");
            if (Settings.Backups.Any(folder => folder.Name.Equals(root.Name, StringComparison.OrdinalIgnoreCase))) continue;
            ReleaseCloudBackupRoot(root.Name);
        }
    }

    private void ReclaimCloudBackupRoot(string name)
    {
        var roots = _storage.LoadCloudBackupRelinquishedRoots();
        var reclaim = roots.Where(root => root.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && root.RootPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (reclaim.Length == 0) return;
        Settings = Settings with { SelectedExclusions = Settings.SelectedExclusions.Where(exclusion => !reclaim.Any(root => root.AddedExclusion &&
            exclusion == new SelectedExclusion(root.RootPath, root.RelativePath, true))).ToList() };
        _storage.SaveSettings(Settings);
        _storage.SaveCloudBackupRelinquishedRoots(roots.Except(reclaim).ToArray());
    }

    private void ValidateCloudBackupReclaim(string name)
    {
        var prefix = Settings.Prefix + Core.Sync.PathRules.ValidateRelative(name) + "/";
        if (CloudTransferJobs.Any(job => job.State != TransferJobState.Completed && job.Plan.Operation == TransferOperation.Move &&
            job.Plan.Source.Provider == "b2" && job.Plan.Source.AccountId == Settings.AccountId && job.Plan.Source.ContainerId == Settings.BucketId &&
            (job.Plan.Source.Path.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(job.Plan.Source.Path, StringComparison.Ordinal))))
            throw new IOException("Finish the B2 move in Activity before enabling this backup again. Its recoverable move still owns the cloud source.");
    }

    private AppSettings PreserveReleasedCloudBackupRoots(AppSettings settings)
    {
        var selected = settings.SelectedExclusions.ToList();
        foreach (var root in _storage.LoadCloudBackupRelinquishedRoots().Where(root => root.RootPath.Equals(settings.RootPath, StringComparison.OrdinalIgnoreCase) &&
            !settings.Backups.Any(folder => folder.Name.Equals(root.Name, StringComparison.OrdinalIgnoreCase))))
        {
            selected.RemoveAll(item => item is not null && string.Equals(item.RootPath, root.RootPath, StringComparison.OrdinalIgnoreCase) && item.RelativePath == root.RelativePath && item.IsFolder);
            selected.Add(new(root.RootPath, root.RelativePath, true));
        }
        return settings with { SelectedExclusions = selected };
    }

    public Task PauseCloudTransferAsync(string id)
    {
        _userPausedCloudTransfers[id] = 0;
        _policyPausedCloudTransfers.TryRemove(id, out _);
        SaveCloudPolicyPauses();
        _globallyPausedCloudTransfers.TryRemove(id, out _);
        return _cloudTransferEngine?.PauseAsync(id) ?? Task.CompletedTask;
    }
    public Task CancelCloudTransferAsync(string id)
    {
        _userPausedCloudTransfers[id] = 0;
        _policyPausedCloudTransfers.TryRemove(id, out _);
        SaveCloudPolicyPauses();
        _globallyPausedCloudTransfers.TryRemove(id, out _);
        return _cloudTransferEngine?.CancelAsync(id) ?? Task.CompletedTask;
    }
    public async Task ResumeCloudTransferAsync(string id, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var job = CloudTransferJobs.Single(item => item.Plan.Id == id);
        ValidateNativeCloudMove(job.Plan.Source, job.Plan.Operation);
        if (job.Plan.Source.Provider == "b2" || job.Plan.Destination.Provider == "b2") await EnsureCloudTransferProvidersAsync(linked.Token);
        if (_cloudTransferRuns.TryGetValue(id, out var previous))
        {
            await previous.WaitAsync(linked.Token);
            RemoveCloudTransferRun(id, previous);
        }
        _userPausedCloudTransfers.TryRemove(id, out _);
        QueueCloudTransferRun(id, resume: true);
    }

    private void QueueCloudTransferRun(string id, bool resume = false)
    {
        lock (_cloudTransferRunGate)
        {
        if (_cloudTransferEngine is null || _cloudTransferRuns.ContainsKey(id)) return;
        if (_userPausedCloudTransfers.ContainsKey(id)) return;
        var plan = CloudTransferJobs.Single(job => job.Plan.Id == id).Plan;
        ValidateNativeCloudMove(plan.Source, plan.Operation);
        var manuallyPaused = Interlocked.Read(ref _manualPauseUntilTicks) > DateTimeOffset.UtcNow.UtcTicks;
        var policyPauseReason = _cloudPauseReason(Settings);
        if (manuallyPaused || policyPauseReason is not null)
        {
            if (manuallyPaused) _globallyPausedCloudTransfers[id] = 0;
            else { _cloudPolicyPauseMessage = policyPauseReason; _policyPausedCloudTransfers[id] = 0; SaveCloudPolicyPauses(); }
            ObserveCloudControl(_cloudTransferEngine.PauseAsync(id));
            return;
        }
        var task = Task.Run(async () =>
        {
            try
            {
                if (resume) await _cloudTransferEngine.ResumeAsync(id, _lifetime.Token);
                else await _cloudTransferEngine.RunAsync(id, _lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, id, "Cloud transfer stopped: " + error.Message)); }
        });
        _cloudTransferRuns[id] = task;
        if (_policyPausedCloudTransfers.TryRemove(id, out _)) SaveCloudPolicyPauses();
        _ = task.ContinueWith(_ => { RemoveCloudTransferRun(id, task); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void RemoveCloudTransferRun(string id, Task expected)
    {
        lock (_cloudTransferRunGate)
            if (_cloudTransferRuns.TryGetValue(id, out var current) && ReferenceEquals(current, expected))
                _cloudTransferRuns.TryRemove(id, out _);
    }

    private void CloudTransferChanged(TransferJobSnapshot job)
    {
        if (job.State == TransferJobState.Completed && _policyPausedCloudTransfers.TryRemove(job.Plan.Id, out _)) SaveCloudPolicyPauses();
        if (job.State == TransferJobState.Cancelled)
        {
            if (_policyPausedCloudTransfers.TryRemove(job.Plan.Id, out _)) SaveCloudPolicyPauses();
            _cloudTransferSnapshots.TryRemove(job.Plan.Id, out _);
            PublishAggregate();
            NotifyChanged(force: true);
            return;
        }
        var live = job.State != TransferJobState.Completed;
        var state = job.State switch
        {
            TransferJobState.Attention => ClientState.Attention,
            TransferJobState.Paused or TransferJobState.Cancelled => ClientState.Paused,
            TransferJobState.Completed => ClientState.UpToDate,
            _ => ClientState.Syncing
        };
        var message = job.Error ?? (job.State == TransferJobState.Completed ? "Cloud transfer completed and verified" :
            job.State == TransferJobState.Discovering ? "Discovering cloud files" : "Cloud transfer " + job.State.ToString().ToLowerInvariant());
        if (job.State == TransferJobState.Paused && _policyPausedCloudTransfers.ContainsKey(job.Plan.Id) && _cloudPolicyPauseMessage is { } reason)
            message = reason;
        var kind = job.Plan.Destination.Provider == "local" ? ActivityKind.Download : ActivityKind.Upload;
        _cloudTransferSnapshots[job.Plan.Id] = new(state, message, live ? (int)Math.Min(int.MaxValue, job.QueuedFiles) : 0,
            TransferredBytes: live ? job.TransferredBytes : 0, TransferTotalBytes: live ? job.TotalBytes : 0)
        {
            UploadBytesPerSecond = live && kind == ActivityKind.Upload ? job.BytesPerSecond : 0,
            DownloadBytesPerSecond = live && kind == ActivityKind.Download ? job.BytesPerSecond : 0,
            ActiveTransfers = live ? job.Items.Count(item => item.State is TransferItemState.Transferring or TransferItemState.Verifying or TransferItemState.DeletingSource) : 0,
            QueuedTransfers = live ? (int)Math.Min(int.MaxValue, job.QueuedFiles) : 0,
            Transfers = live ? job.Items.Where(item => item.State is not (TransferItemState.Completed or TransferItemState.Skipped)).Select(item =>
                new TransferSnapshot(job.Plan.Id + ":" + item.Id, job.Plan.Source.DisplayName + " → " + job.Plan.Destination.DisplayName,
                    item.RelativePath, kind, job.State is TransferJobState.Paused or TransferJobState.Cancelled ? TransferPhase.Paused : item.State switch
                    {
                        TransferItemState.Verifying or TransferItemState.Verified or TransferItemState.DeletingSource => TransferPhase.Verifying,
                        TransferItemState.Transferring => kind == ActivityKind.Upload ? TransferPhase.Uploading : TransferPhase.Downloading,
                        TransferItemState.Attention => TransferPhase.Retrying,
                        _ => TransferPhase.Queued
                    }, item.Bytes, item.TotalBytes) { BytesPerSecond = item.BytesPerSecond }).ToArray() : []
        };
        PublishAggregate();
    }

    private void PauseAllCloudTransfers()
    {
        var jobs = CloudTransferJobs.Where(job => job.State is TransferJobState.Discovering or TransferJobState.Running).ToArray();
        foreach (var job in jobs) _globallyPausedCloudTransfers[job.Plan.Id] = 0;
        ObserveCloudControl(Task.WhenAll(jobs.Select(job => _cloudTransferEngine!.PauseAsync(job.Plan.Id))));
    }
    private void ResumeAllCloudTransfers(bool onlyGlobal = false)
    {
        var ids = onlyGlobal ? _globallyPausedCloudTransfers.Keys.ToArray() :
            CloudTransferJobs.Where(job => job.State == TransferJobState.Paused).Select(job => job.Plan.Id)
                .Concat(_globallyPausedCloudTransfers.Keys).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids)
        {
            _globallyPausedCloudTransfers.TryRemove(id, out _);
            ObserveCloudControl(ResumeCloudTransferAsync(id));
        }
    }
    private void ScheduleCloudTransferResume(TimeSpan? duration)
    {
        var generation = Interlocked.Increment(ref _cloudPauseGeneration);
        if (duration is not { } delay) return;
        ObserveCloudControl(Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _lifetime.Token);
                if (generation == Volatile.Read(ref _cloudPauseGeneration)) ResumeAllCloudTransfers(onlyGlobal: true);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }));
    }
    private async Task RefreshCloudPausePolicyAsync()
    {
        if (_cloudTransferEngine is null || Interlocked.Exchange(ref _refreshingCloudPausePolicy, 1) != 0) return;
        try
        {
            if (!CloudTransferJobs.Any(job => job.State is TransferJobState.Discovering or TransferJobState.Running) && _policyPausedCloudTransfers.IsEmpty) return;
            if (_cloudPauseReason(Settings) is { } pauseReason)
            {
                _cloudPolicyPauseMessage = pauseReason;
                foreach (var job in CloudTransferJobs.Where(job => job.State is TransferJobState.Discovering or TransferJobState.Running))
                {
                    _policyPausedCloudTransfers[job.Plan.Id] = 0;
                    SaveCloudPolicyPauses();
                    await _cloudTransferEngine.PauseAsync(job.Plan.Id);
                }
            }
            else if (Interlocked.Read(ref _manualPauseUntilTicks) <= DateTimeOffset.UtcNow.UtcTicks)
            {
                foreach (var id in _policyPausedCloudTransfers.Keys)
                {
                    await ResumeCloudTransferAsync(id, _lifetime.Token);
                }
            }
        }
        finally { Interlocked.Exchange(ref _refreshingCloudPausePolicy, 0); }
    }
    private void ObserveCloudControl(Task task) => _ = task.ContinueWith(done =>
    {
        AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, "", "Cloud transfer control needs attention: " + done.Exception!.GetBaseException().Message));
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private void SaveCloudPolicyPauses()
    {
        lock (_cloudPolicyPauseStorageGate)
            _storage.SavePolicyPausedCloudTransfers(_policyPausedCloudTransfers.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    private sealed class UserCheckpointProtector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, "CloudBay.TransferCheckpoint.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, "CloudBay.TransferCheckpoint.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    }
}

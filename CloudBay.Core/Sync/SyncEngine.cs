using System.Security.Cryptography;
using System.Threading.Channels;
using CloudBay.Core.Transfers;

namespace CloudBay.Core.Sync;

/// <summary>Reconciles complete local and remote snapshots against a durable last-synced baseline.</summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly ICloudStore _cloud;
    private readonly IPlaceholderService _placeholders;
    private readonly SyncManifest _manifest;
    private readonly string _recoveryPath;
    private readonly Action<ActivityEvent> _activity;
    private readonly Action<SyncSnapshot> _status;
    private readonly Func<string?> _policy;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _cycleCancellation;
    private FileSystemWatcher? _watcher;
    private Task? _background;
    private AppSettings _settings;
    private volatile bool _paused;
    private DateTimeOffset _nextStagingCleanup;
    private DateTimeOffset? _resumeAt;
    private readonly object _stateGate = new();
    private HashSet<string>? _pendingDeletionReview;
    private HashSet<string>? _approvedDeletionReview;
    private readonly object _progressGate = new();
    private readonly TransferTracker _transfers;
    private SyncSnapshot _snapshot = new(ClientState.Connecting, "Connecting to Backblaze B2");

    public SyncEngine(ICloudStore cloud, IPlaceholderService placeholders, SyncManifest manifest,
        AppSettings settings, string recoveryPath, Action<ActivityEvent> activity, Action<SyncSnapshot> status,
        Func<string?>? policy = null, string? rootDisplayName = null)
    {
        _cloud = cloud; _placeholders = placeholders; _manifest = manifest; _settings = settings;
        _recoveryPath = recoveryPath; _activity = activity; _status = status; _policy = policy ?? (() => null);
        _transfers = new(settings.RootPath, rootDisplayName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(settings.RootPath)));
    }

    public void Start()
    {
        if (_background is not null) return;
        _watcher = new FileSystemWatcher(_settings.RootPath) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024 };
        _watcher.Changed += (_, _) => Wake(); _watcher.Created += (_, _) => Wake();
        _watcher.Deleted += (_, _) => Wake(); _watcher.Renamed += (_, _) => Wake();
        _watcher.Error += (_, _) => Wake(); _watcher.EnableRaisingEvents = true;
        _background = Task.Run(BackgroundAsync);
        Wake();
    }

    public void Configure(AppSettings settings)
    {
        PathRules.ValidateSettings(settings);
        lock (_stateGate)
        {
            _settings = settings;
            // An in-flight reconciliation owns a snapshot of the old preferences. Stop
            // it before it can infer deletions or transfer newly excluded items.
            try { _cycleCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }
        Wake();
    }
    public bool IsPaused => _paused;
    /// <summary>Read after QuiesceAsync when reviewing removal of verified local copies.</summary>
    public IReadOnlyDictionary<string, SyncEntry> ReadVerifiedEntries() => _manifest.ReadAll();
    /// <summary>Refresh measured activity without starting a scan or transfer.</summary>
    public void RefreshTransferStatus()
    {
        lock (_progressGate)
            if (_snapshot.ActiveTransfers > 0) SetStatus(_snapshot);
    }
    public async Task QuiesceAsync(CancellationToken cancellationToken = default)
    {
        Pause();
        await _cycleGate.WaitAsync(cancellationToken);
        _cycleGate.Release();
    }
    public void Pause(TimeSpan? duration = null)
    {
        lock (_stateGate)
        {
            _paused = true; _resumeAt = duration.HasValue ? DateTimeOffset.UtcNow + duration : null;
            try { _cycleCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }
        _transfers.Pause();
        SetStatus(_snapshot with { State = ClientState.Paused, Message = "Sync paused" });
    }
    public void Resume() { lock (_stateGate) { _paused = false; _resumeAt = null; } Wake(); }
    public void ApproveDeletions()
    {
        lock (_stateGate)
            _approvedDeletionReview = _pendingDeletionReview is null ? null : new(_pendingDeletionReview, StringComparer.OrdinalIgnoreCase);
        Wake();
    }
    private void Wake()
    {
        if (_lifetime.IsCancellationRequested) return;
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException) { }
    }

    private async Task BackgroundAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(_settings.PollSeconds), _lifetime.Token);
                // Coalesce editor saves and rename storms; the complete scan also recovers lost watcher events.
                await Task.Delay(700, _lifetime.Token);
                await SyncNowAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
                if (_paused && _resumeAt is not null && DateTimeOffset.UtcNow >= _resumeAt) { _paused = false; _resumeAt = null; }
            var policy = _policy();
            if (_paused || policy is not null)
            {
                SetStatus(_snapshot with { State = ClientState.Paused, Message = policy ?? "Sync paused" }); return;
            }
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            lock (_stateGate) _cycleCancellation = cycle;
            await ReconcileAsync(cycle.Token);
        }
        catch (OperationCanceledException) when (_paused || _lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested ||
            _cycleCancellation?.IsCancellationRequested == true) { }
        catch (Exception exception)
        {
            var offline = exception is HttpRequestException;
            SetStatus(_snapshot with { State = offline ? ClientState.Offline : ClientState.Attention,
                Message = offline ? "Connection unavailable. Changes will retry automatically." : exception.Message });
            Record(ActivityKind.Error, "", offline ? "B2 connection unavailable; retry scheduled." : exception.Message);
        }
        finally { lock (_stateGate) _cycleCancellation = null; _cycleGate.Release(); }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var settings = _settings;
        if (!Directory.Exists(settings.RootPath)) throw new IOException("The sync folder is unavailable. No deletions were sent to B2.");
        if (DateTimeOffset.UtcNow >= _nextStagingCleanup)
        {
            await Task.Run(() => DownloadStaging.CleanupAbandoned(settings.RootPath, ct), ct);
            _nextStagingCleanup = DateTimeOffset.UtcNow.AddDays(1);
        }
        _transfers.Reset();
        SetStatus(_snapshot with { State = ClientState.Syncing, Pending = 0, Message = "Checking for changes" });
        var baseline = _manifest.ReadAll();
        var directoryBaseline = _manifest.ReadDirectories();
        var localSnapshot = await Task.Run(() => ScanLocal(settings, ct, "Checking local files"), ct);
        var scanIssues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ReportScanIssues(LocalSnapshot snapshot)
        {
            foreach (var issue in snapshot.Issues)
                if (scanIssues.Add(issue.Key)) Record(ActivityKind.Error, issue.Key, issue.Value);
        }
        ReportScanIssues(localSnapshot);
        var local = localSnapshot.Files;
        var remote = new Dictionary<string, CloudObject>(StringComparer.OrdinalIgnoreCase);
        var remoteDirectories = new Dictionary<string, CloudObject>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        SetStatus(_snapshot with { Message = "Checking Backblaze B2" });
        await foreach (var file in _cloud.ListCurrentAsync(settings.BucketId, settings.Prefix, ct))
        {
            if (file.Action != "upload" || file.Key == settings.Prefix) continue;
            var directory = file.Key.EndsWith('/');
            if (directory && file.Size != 0) throw new InvalidDataException("B2 contains a nonempty object whose name ends in '/'. It cannot be represented as a Windows folder.");
            string relative;
            try { relative = PathRules.FromKey(directory ? file.Key[..^1] : file.Key, settings.Prefix); }
            catch (InvalidDataException error) { invalid++; Record(ActivityKind.Error, file.Key, error.Message); continue; }
            if (PathRules.IsExcluded(relative, settings, isDirectory: directory)) continue;
            if (localSnapshot.IsUnavailable(relative)) continue;
            if (!(directory ? remoteDirectories : remote).TryAdd(relative, file))
                throw new InvalidDataException("B2 contains names that differ only by letter case. Resolve the collision before syncing to Windows.");
        }
        ValidateRemoteHierarchy(remote.Keys, remoteDirectories.Keys);
        ValidateMixedHierarchy(localSnapshot, remote.Keys, remoteDirectories.Keys);
        // Missing paths are deletion candidates only inside successfully inspected portions
        // of both snapshots. Unavailable subtrees retain their last verified cloud baseline.
        var deletions = baseline.Keys.Where(p => !PathRules.IsExcluded(p, settings) && !localSnapshot.IsUnavailable(p) &&
            (!local.ContainsKey(p) || !remote.ContainsKey(p))).Select(p => "F:" + p)
            .Concat(directoryBaseline.Keys.Where(p => !PathRules.IsExcluded(p, settings, isDirectory: true) && !localSnapshot.IsUnavailable(p) &&
                (!localSnapshot.Directories.Contains(p) || !remoteDirectories.ContainsKey(p))).Select(p => "D:" + p)).ToList();
        var massDelete = deletions.Count >= 10 && deletions.Count > (baseline.Count + directoryBaseline.Count) / 4;
        var review = deletions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool approved;
        lock (_stateGate)
        {
            approved = _approvedDeletionReview?.SetEquals(review) == true;
            _pendingDeletionReview = massDelete ? review : null;
            _approvedDeletionReview = null;
        }
        if (massDelete && !approved)
        {
            SetStatus(_snapshot with { State = ClientState.Attention,
                Message = $"Review required: {deletions.Count} items disappeared. Deletion sync is paused.", Pending = deletions.Count });
            return;
        }
        var uploads = new List<string>();
        var downloads = new List<(string Relative, CloudObject File)>();
        var errors = invalid + scanIssues.Count;
        SetStatus(_snapshot with { Message = "Comparing local and cloud files" });
        foreach (var relative in local.Keys.Concat(remote.Keys).Concat(baseline.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (PathRules.IsExcluded(relative, settings) || localSnapshot.IsUnavailable(relative)) continue;
            local.TryGetValue(relative, out var disk);
            remote.TryGetValue(relative, out var cloud);
            baseline.TryGetValue(relative, out var last);
            try
            {
                var path = PathRules.FullPath(settings.RootPath, relative);
                if (cloud is null)
                {
                    if (last is null && disk is not null) { uploads.Add(relative); continue; }
                    if (last is null) continue;
                    if (disk is not null)
                    {
                        if (IsLocallyChanged(ReadLocal(path), last))
                        {
                            var conflict = PreserveConflict(path, settings.RootPath);
                            uploads.Add(conflict);
                            Record(ActivityKind.Conflict, relative, "Cloud deletion conflicted with a local edit. The edit was preserved as a conflict copy.");
                        }
                        else PreserveBeforeDelete(path, relative);
                        Record(ActivityKind.Delete, relative, "Removed locally after cloud deletion; downloaded data retained in Recovery.");
                    }
                    _manifest.Remove(relative);
                    continue;
                }
                if (disk is null)
                {
                    if (last is not null && cloud.FileId == last.Remote.FileId)
                    {
                        // B2 hide markers preserve older versions; never permanently delete B2 versions here.
                        await _cloud.HideAsync(settings.BucketId, cloud.Key, ct);
                        _manifest.Remove(relative);
                        Record(ActivityKind.Delete, relative, "Moved to B2 version history after local deletion.");
                    }
                    else downloads.Add((relative, cloud));
                    continue;
                }
                if (last is null)
                {
                    if (disk.Hydrated && await TryAdoptRemoteAsync(relative, path, cloud, settings, ct))
                    {
                        // A completed B2 upload followed by a crash can leave no local baseline.
                        // Matching the locked bytes safely adopts the already uploaded cloud version.
                    }
                    else
                    {
                        if (disk.Hydrated) uploads.Add(PreserveConflict(path, settings.RootPath));
                        downloads.Add((relative, cloud));
                        Record(ActivityKind.Conflict, relative, "An existing local file differed from B2. Both copies were preserved.");
                    }
                    continue;
                }
                var changed = IsLocallyChanged(disk, last);
                if (cloud.FileId != last.Remote.FileId)
                {
                    if (changed)
                    {
                        if (disk.Hydrated && await TryAdoptRemoteAsync(relative, path, cloud, settings, ct)) continue;
                        uploads.Add(PreserveConflict(path, settings.RootPath));
                        Record(ActivityKind.Conflict, relative, "Local and cloud edits overlapped. Both copies were preserved.");
                    }
                    downloads.Add((relative, cloud));
                }
                else if (last.NativeMarkPending)
                {
                    // A completed, independently verified upload must not become a new B2
                    // version merely because Explorer metadata could not be committed.
                    if (!await TryFinishPendingNativeMarkAsync(last, settings, ct)) uploads.Add(relative);
                }
                else if (changed) uploads.Add(relative);
                else if (!settings.FilesOnDemand && !disk.Hydrated)
                    await _placeholders.SetPinAsync(path, PinMode.AlwaysAvailable, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                errors++;
                _transfers.Phase(relative, ActivityKind.Upload, TransferPhase.Retrying);
                Record(ActivityKind.Error, relative, error.Message);
            }
        }

        uploads = uploads.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _transfers.Queue(uploads.Select(path => (path, ActivityKind.Upload, File.Exists(PathRules.FullPath(settings.RootPath, path))
            ? new FileInfo(PathRules.FullPath(settings.RootPath, path)).Length : 0L)));
        _transfers.Queue(downloads.Where(item =>
        {
            var path = PathRules.FullPath(settings.RootPath, item.Relative);
            return !settings.FilesOnDemand || File.Exists(path) && !_placeholders.IsPlaceholder(path);
        }).Select(item => (item.Relative, ActivityKind.Download, item.File.Size)));
        var remaining = uploads.Count + downloads.Count;
        SetStatus(_snapshot with { Pending = remaining, Message = remaining == 0 ? "Checking folder changes" : $"Syncing {remaining} files" });
        var limits = TransferLimits.For(settings);
        void FinishTransfer(string relative, ActivityKind kind, Exception? error = null)
        {
            lock (_progressGate)
            {
                if (error is null) _transfers.Complete(relative, kind);
                else { Interlocked.Increment(ref errors); _transfers.Phase(relative, kind, TransferPhase.Retrying); }
                var left = Interlocked.Decrement(ref remaining);
                SetStatus(_snapshot with { Pending = Math.Max(0, left) });
            }
            if (error is not null) Record(ActivityKind.Error, relative, error.Message);
        }
        async Task TransferAsync(string relative, ActivityKind kind, Func<CancellationToken, Task> action, CancellationToken token)
        {
            try { await action(token); FinishTransfer(relative, kind); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { FinishTransfer(relative, kind, error); }
        }
        await Task.WhenAll(
            UploadPipelineAsync(uploads, settings, limits.Uploads, FinishTransfer, ct),
            Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = limits.Downloads, CancellationToken = ct },
                (item, token) => new ValueTask(TransferAsync(item.Relative, ActivityKind.Download, t => ApplyRemoteAsync(item.Relative, item.File, settings, t), token))));
        errors += await ReconcileDirectoriesAsync(localSnapshot, remoteDirectories, directoryBaseline, settings, ct);
        var final = _manifest.ReadAll();
        var finalSnapshot = await Task.Run(() => ScanLocal(settings, ct, "Checking Windows file status"), ct);
        var previousScanIssues = scanIssues.Count;
        ReportScanIssues(finalSnapshot);
        errors += scanIssues.Count - previousScanIssues;
        var localBytes = finalSnapshot.Files.Values.Where(f => f.Hydrated).Sum(f => f.Size);
        SetStatus(new(errors == 0 ? ClientState.UpToDate : ClientState.Attention,
            errors == 0 ? "Your files are up to date" : scanIssues.Count > 0
                ? $"{errors} items need attention. Readable folders were checked; unavailable paths were left unchanged. Check Activity for the affected paths."
                : $"{errors} items need attention. Failed changes will retry.",
            0, final.Count, final.Values.Sum(f => f.Remote.Size), localBytes, LastSync: DateTimeOffset.UtcNow));
    }

    private LocalSnapshot ScanLocal(AppSettings settings, CancellationToken ct, string phase)
    {
        // A linked root or ancestor cannot be treated as a partial scan: it could redirect
        // every later write outside this backup, so stop before consulting cloud deletions.
        for (var ancestor = Path.GetFullPath(settings.RootPath); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (new DirectoryInfo(ancestor).LinkTarget is not null)
                throw new IOException("The sync folder or a parent folder is linked. Choose its actual location; no deletions were sent to B2.");
        var snapshot = new LocalSnapshot();
        long inspectedFiles = 0, inspectedFolders = 0, reportedAt = 0;
        void ReportProgress(bool force = false)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!force && System.Diagnostics.Stopwatch.GetElapsedTime(reportedAt, now) < TimeSpan.FromMilliseconds(500)) return;
            reportedAt = now;
            SetStatus(_snapshot with { Pending = 0,
                Message = $"{phase}: {inspectedFiles:N0} {(inspectedFiles == 1 ? "file" : "files")}, " +
                    $"{inspectedFolders:N0} {(inspectedFolders == 1 ? "folder" : "folders")} checked" });
        }
        ReportProgress(force: true);
        var directories = new Stack<string>(); directories.Push(settings.RootPath);
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            var directoryRelative = Path.GetRelativePath(settings.RootPath, directory).Replace('\\', '/');
            if (directoryRelative != "." && snapshot.IsUnavailable(directoryRelative)) continue;
            inspectedFolders++;
            ReportProgress();
            try
            {
                // Recheck a queued folder before traversing it; a rename can replace it
                // with a link between discovery and enumeration.
                if (new DirectoryInfo(directory).LinkTarget is not null)
                    throw new IOException("Linked folders are not followed. Choose the folder's actual location for backup.");
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(settings.RootPath, child).Replace('\\', '/');
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(child);
                        if (PathRules.IsExcluded(relative, settings, isDirectory: (attributes & FileAttributes.Directory) != 0)) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                if (VerifiedTreeCopy.IsProtectedCompatibilityJunction(child))
                                {
                                    // Windows keeps these protected legacy aliases alongside
                                    // the actual folder. Never follow or reconcile their targets.
                                    snapshot.Block(relative); continue;
                                }
                                if (new DirectoryInfo(child).LinkTarget is not null)
                                    throw new IOException("Linked folders are not followed. Choose the folder's actual location for backup.");
                            }
                            PathRules.ValidateRelative(relative);
                        }
                        else
                        {
                            var placeholder = _placeholders.IsPlaceholder(child);
                            if ((attributes & FileAttributes.ReparsePoint) != 0 && !placeholder)
                                throw new IOException("Linked files are not followed. Back up the file from its actual location.");
                            PathRules.ValidateRelative(relative);
                            snapshot.Files.Add(relative, ReadLocal(child));
                            inspectedFiles++;
                            ReportProgress();
                            continue;
                        }
                    }
                    catch (Exception error) when (IsUnavailableScanError(error))
                    {
                        snapshot.Block(relative, error); continue;
                    }
                    if (!snapshot.Directories.Add(relative))
                        throw new InvalidDataException("Local folder names differ only by case and cannot be synchronized safely.");
                    directories.Push(child);
                }
            }
            catch (Exception error) when (IsUnavailableScanError(error))
            {
                if (directoryRelative == ".")
                    throw new IOException("The sync folder could not be scanned. No deletions were sent to B2. " + FileSystemError.Describe(error), error);
                // Enumeration may fail after yielding some entries. Protect the whole
                // folder, including those entries, rather than infer anything from a prefix.
                snapshot.Block(directoryRelative, error);
            }
        }
        snapshot.RemoveUnavailableEntries();
        ReportProgress(force: true);
        return snapshot;
    }

    private static bool IsUnavailableScanError(Exception error) => error is IOException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;

    private async Task<int> ReconcileDirectoriesAsync(LocalSnapshot snapshot, Dictionary<string, CloudObject> remote,
        IReadOnlyDictionary<string, SyncDirectoryEntry> baseline, AppSettings settings, CancellationToken ct)
    {
        var errors = 0;
        var local = snapshot.Directories;
        // Children first: remote folder removal only removes truly empty directories, never their data.
        var paths = local.Concat(remote.Keys).Concat(baseline.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !PathRules.IsExcluded(p, settings, isDirectory: true) && !snapshot.IsUnavailable(p))
            .OrderByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var checkedFolders = 0;
        long reportedAt = 0;
        void ReportProgress(bool force = false)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!force && System.Diagnostics.Stopwatch.GetElapsedTime(reportedAt, now) < TimeSpan.FromMilliseconds(500)) return;
            reportedAt = now;
            SetStatus(_snapshot with { Pending = 0, Message = $"Checking folder changes: {checkedFolders:N0} of {paths.Length:N0}" });
        }
        void ReportFolderOperation(string operation, string relative)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (checkedFolders != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(reportedAt, now) < TimeSpan.FromMilliseconds(500)) return;
            reportedAt = now;
            SetStatus(_snapshot with { Pending = 0, Message = $"{operation} {relative} ({checkedFolders + 1:N0} of {paths.Length:N0})" });
        }
        ReportProgress(force: true);
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            remote.TryGetValue(relative, out var cloud);
            baseline.TryGetValue(relative, out var last);
            try
            {
                var path = PathRules.FullPath(settings.RootPath, relative);
                var existedInSnapshot = local.Contains(relative);
                if (cloud is null)
                {
                    if (last is not null)
                    {
                        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                        {
                            Directory.Delete(path, recursive: false);
                            Record(ActivityKind.Delete, relative, "Removed an empty local folder after its cloud folder marker was deleted.");
                        }
                        _manifest.RemoveDirectory(relative);
                    }
                    else if (existedInSnapshot && Directory.Exists(path))
                    {
                        // Trailing-slash B2 markers retain otherwise invisible empty folders.
                        ReportFolderOperation("Backing up folder", relative);
                        await using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
                        var modified = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path));
                        var marker = await _cloud.UploadAsync(settings.BucketId, settings.Prefix + relative + "/", empty, 0,
                            "da39a3ee5e6b4b0d3255bfef95601890afd80709", modified, cancellationToken: ct);
                        _manifest.PutDirectory(new(relative, marker));
                        Record(ActivityKind.Backup, relative, "Folder backed up to B2, including empty folders.");
                    }
                    continue;
                }
                if (!existedInSnapshot && last is not null && cloud.FileId == last.Remote.FileId && !Directory.Exists(path))
                {
                    ReportFolderOperation("Syncing deleted folder", relative);
                    await _cloud.HideAsync(settings.BucketId, cloud.Key, ct);
                    _manifest.RemoveDirectory(relative);
                    Record(ActivityKind.Delete, relative, "Moved the deleted folder marker to B2 version history.");
                    continue;
                }
                if (File.Exists(path)) throw new IOException($"The cloud folder '{relative}' conflicts with a local file. The local file was retained.");
                // An unchanged folder is already represented in Windows and in the durable
                // baseline. Avoid one filesystem mutation and SQLite commit per folder on
                // every no-op poll, especially for large directory trees.
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                if (last?.Remote != cloud) _manifest.PutDirectory(new(relative, cloud));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { errors++; Record(ActivityKind.Error, relative, error.Message); }
            finally { checkedFolders++; ReportProgress(); }
        }
        ReportProgress(force: true);
        return errors;
    }

    private static void ValidateRemoteHierarchy(IEnumerable<string> files, IEnumerable<string> directories)
    {
        var fileNames = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Concat(directories))
        {
            for (var index = path.LastIndexOf('/'); index > 0; index = path.LastIndexOf('/', index - 1))
                if (fileNames.Contains(path[..index]))
                    throw new InvalidDataException("B2 contains a file whose name is also a parent folder. Resolve the cloud hierarchy collision before syncing.");
        }
        if (directories.Any(fileNames.Contains))
            throw new InvalidDataException("B2 contains a file and folder with the same Windows path. Resolve the collision before syncing.");
    }

    private static void ValidateMixedHierarchy(LocalSnapshot local, IEnumerable<string> remoteFiles, IEnumerable<string> remoteDirectories)
    {
        var folderNames = remoteDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in remoteFiles)
            if (local.Directories.Contains(path))
                throw new IOException($"The cloud file '{path}' conflicts with a local folder. Rename or move one copy before syncing; all local data was retained.");
        foreach (var path in remoteFiles.Concat(remoteDirectories))
        {
            if (folderNames.Contains(path) && local.Files.ContainsKey(path))
                throw new IOException($"The cloud folder '{path}' conflicts with a local file. Rename or move one copy before syncing; all local data was retained.");
            for (var index = path.LastIndexOf('/'); index > 0; index = path.LastIndexOf('/', index - 1))
                if (local.Files.ContainsKey(path[..index]))
                    throw new IOException($"A local file conflicts with a cloud parent folder for '{path}'. Rename or move one copy before syncing; all local data was retained.");
        }
    }

    private static bool IsLocallyChanged(LocalFile disk, SyncEntry baseline) =>
        disk.HasLocalChanges || disk.Size != baseline.LocalSize || disk.WriteUtc != baseline.LocalWriteUtc;

    private LocalFile ReadLocal(string path)
    {
        var info = new FileInfo(path);
        var state = _placeholders.GetFileState(path);
        return new(info.Length, info.LastWriteTimeUtc, !state.IsPlaceholder || state.IsHydrated, state.HasLocalChanges);
    }

    private async Task UploadPipelineAsync(IReadOnlyList<string> uploads, AppSettings settings, int workers,
        Action<string, ActivityKind, Exception?> finish, CancellationToken cancellationToken)
    {
        if (uploads.Count == 0) return;
        // A completed HTTP upload releases its network worker immediately. Verification consumes
        // a bounded queue separately, so short files keep feeding pooled connections while earlier
        // acknowledgments are checked. Backpressure bounds pending metadata and tasks.
        var pending = Channel.CreateBounded<UploadedLocal>(new BoundedChannelOptions(workers * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = workers == 1,
            AllowSynchronousContinuations = false
        });
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pipeline.Token;
        // Keep bounded, writer-denying prepared handles ready while the network is busy.
        // The shared hashing gate still limits disk work independently of upload slots.
        var ready = Channel.CreateBounded<PreparedLocal>(new BoundedChannelOptions(workers)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = workers == 1, SingleWriter = false });
        async Task PrepareAsync()
        {
            try
            {
                await Parallel.ForEachAsync(uploads, new ParallelOptions
                { MaxDegreeOfParallelism = Math.Min(2, workers), CancellationToken = token }, async (relative, ct) =>
                {
                    PreparedLocal? prepared = null;
                    try
                    {
                        prepared = await PrepareLocalAsync(relative, settings, ct);
                        if (prepared is null) finish(relative, ActivityKind.Upload, null);
                        else
                        {
                            SetPhase(relative, ActivityKind.Upload, TransferPhase.Queued);
                            await ready.Writer.WriteAsync(prepared, ct);
                            prepared = null; // The upload worker now owns the stable handle.
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { finish(relative, ActivityKind.Upload, error); }
                    finally { if (prepared is not null) await prepared.Source.DisposeAsync(); }
                });
            }
            catch { pipeline.Cancel(); throw; }
            finally { ready.Writer.TryComplete(); }
        }
        async Task SendAsync()
        {
            await foreach (var prepared in ready.Reader.ReadAllAsync(token))
            {
                try
                {
                    UploadedLocal uploaded;
                    await using (prepared.Source)
                        uploaded = await UploadPreparedLocalAsync(prepared, settings, token);
                    await pending.Writer.WriteAsync(uploaded, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { finish(prepared.Relative, ActivityKind.Upload, error); }
            }
        }
        async Task ProduceAsync()
        {
            try
            {
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => SendAsync()).Append(PrepareAsync()));
            }
            catch { pipeline.Cancel(); throw; }
            finally
            {
                pending.Writer.TryComplete();
                while (ready.Reader.TryRead(out var abandoned)) await abandoned.Source.DisposeAsync();
            }
        }
        async Task VerifyAsync()
        {
            try
            {
                await foreach (var uploaded in pending.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        await VerifyUploadedLocalAsync(uploaded, settings, token);
                        finish(uploaded.Relative, ActivityKind.Upload, null);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { finish(uploaded.Relative, ActivityKind.Upload, error); }
                }
            }
            catch { pipeline.Cancel(); throw; }
        }
        var verification = Enumerable.Range(0, Math.Min(workers, 8)).Select(_ => VerifyAsync()).ToArray();
        await Task.WhenAll(verification.Append(ProduceAsync()));
    }

    private async Task<PreparedLocal?> PrepareLocalAsync(string relative, AppSettings settings, CancellationToken ct)
    {
        var path = PathRules.FullPath(settings.RootPath, relative);
        if (!File.Exists(path)) { _transfers.Discard(relative, ActivityKind.Upload); return null; }
        if (_placeholders.IsPlaceholder(path) && !_placeholders.IsHydrated(path))
            await _placeholders.HydrateAsync(path, ct);
        // Deny concurrent writes for the hash and transfer so B2 receives the exact verified snapshot.
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Read metadata after acquiring the writer-denying handle; an editor may have saved between
        // the initial directory scan and this open.
        try
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            SetPhase(relative, ActivityKind.Upload, TransferPhase.Hashing);
            var sha1 = await NativeTransferAdapters.PrepareUploadChecksumAsync(_cloud, settings.BucketId, settings.Prefix + relative, source, source.Length, modified, ct);
            source.Position = 0;
            return new(relative, source, sha1, modified);
        }
        catch { await source.DisposeAsync(); throw; }
    }

    private async Task<UploadedLocal> UploadPreparedLocalAsync(PreparedLocal prepared, AppSettings settings, CancellationToken ct)
    {
        var (relative, source, sha1, modified) = prepared;
        SetPhase(relative, ActivityKind.Upload, TransferPhase.Uploading);
        var file = await NativeTransferAdapters.UploadPreparedAsync(_cloud, settings.BucketId, settings.Prefix + relative, source, source.Length,
            sha1, modified, new InlineProgress(p => UpdateProgress(relative, ActivityKind.Upload, p)), ct);
        var size = source.Length;
        SetPhase(relative, ActivityKind.Upload, TransferPhase.Verifying);
        // The handle closes when this method returns, before the acknowledgment waits in the
        // bounded queue. A subsequent edit is rechecked before native marking and stays dirty.
        return new(relative, file, size, modified);
    }

    private async Task VerifyUploadedLocalAsync(UploadedLocal uploaded, AppSettings settings, CancellationToken ct)
    {
        var (relative, file, size, modified) = uploaded;
        var path = PathRules.FullPath(settings.RootPath, relative);
        await NativeTransferAdapters.VerifyUploadAsync(_cloud, file, settings.BucketId, ct);
        // The cloud snapshot survives failure, cancellation, or a process exit during local
        // marking. It describes the locked uploaded bytes, never a newer user's save.
        var baseline = new SyncEntry(relative, file, size, modified) { NativeMarkPending = true };
        _manifest.Put(baseline);
        Record(ActivityKind.Upload, relative, "Uploaded and verified in Backblaze B2", file.Size);
        var after = new FileInfo(path);
        // A save immediately after upload remains dirty and will be sent by the next scan.
        if (after.Exists && after.Length == size && after.LastWriteTimeUtc == modified.UtcDateTime)
        {
            ShowPendingNativeMark(baseline, ActivityKind.Upload);
            await FinishNativeMarkAsync(baseline, settings, ct);
        }
    }

    private async Task ApplyRemoteAsync(string relative, CloudObject cloud, AppSettings settings, CancellationToken ct)
    {
        var path = PathRules.FullPath(settings.RootPath, relative);
        SetPhase(relative, ActivityKind.Download, TransferPhase.Downloading);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var metadataOnly = settings.FilesOnDemand && (!File.Exists(path) || _placeholders.IsPlaceholder(path));
        if (metadataOnly)
            await _placeholders.CreateOrUpdateAsync(path, cloud, true, ct);
        else
        {
            var original = File.Exists(path) ? ReadLocal(path) : null;
            await using var staging = await DownloadStaging.OpenAsync(settings.RootPath, relative, cloud, ct);
            var temporary = staging.Path;
            try
            {
                await NativeTransferAdapters.DownloadFileAsync(_cloud, cloud, staging.Stream, staging.Chunks, staging.CheckpointAsync,
                    new InlineProgress(p =>
                    {
                        if (p.TotalBytes > 0 && p.Bytes == p.TotalBytes) SetPhase(relative, ActivityKind.Download, TransferPhase.Verifying);
                        UpdateProgress(relative, ActivityKind.Download, p);
                    }), ct);
                await staging.Stream.DisposeAsync();
                File.SetLastWriteTimeUtc(temporary, cloud.ModifiedUtc.UtcDateTime);
                if (File.Exists(path))
                {
                    // Keep a writer-denying, delete-sharing handle across the final comparison and
                    // atomic rename. If an editor is writing, defer rather than overwrite it.
                    await using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    var current = ReadLocal(path);
                    if (original is null || current.HasLocalChanges && !original.HasLocalChanges ||
                        current.Size != original.Size || current.WriteUtc != original.WriteUtc)
                    {
                        var conflict = PreserveConflict(path, settings.RootPath);
                        Record(ActivityKind.Conflict, conflict, "A local edit arrived during download. The complete edited file was preserved and will be uploaded.");
                    }
                    else PreserveBeforeDelete(path, relative);
                }
                // Never overwrite: a file created after the atomic preservation remains intact.
                File.Move(temporary, path, overwrite: false);
                staging.ForgetCheckpoint();
                var baseline = new SyncEntry(relative, cloud, cloud.Size, cloud.ModifiedUtc) { NativeMarkPending = true };
                _manifest.Put(baseline);
                ShowPendingNativeMark(baseline, ActivityKind.Download);
                await FinishNativeMarkAsync(baseline, settings, ct);
            }
            catch (InvalidDataException) { if (staging.Stream.CanWrite) staging.DiscardCorruptBytes(); throw; }
        }
        if (!settings.FilesOnDemand) await _placeholders.SetPinAsync(path, PinMode.AlwaysAvailable, ct);
        SaveBaseline(relative, cloud);
        Record(metadataOnly ? ActivityKind.Information : ActivityKind.Download, relative,
            metadataOnly ? "Cloud file is available in Explorer" : "Downloaded and available offline",
            metadataOnly ? 0 : cloud.Size);
    }

    private void SaveBaseline(string relative, CloudObject file)
    {
        // Store the applied snapshot rather than re-reading a path which a user can edit immediately
        // after native marking. Such an edit must remain different from the synced baseline.
        _manifest.Put(new(relative, file, file.Size, file.ModifiedUtc));
    }

    private string PreserveConflict(string path, string root)
    {
        if (!File.Exists(path)) throw new IOException("The conflicting local file disappeared.");
        var suffix = $" (conflict {DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]})";
        var conflict = Path.Combine(Path.GetDirectoryName(path)!, PathRules.ConflictFileName(Path.GetFileName(path), suffix));
        // Same-directory rename is atomic and preserves edits made through an open handle. A
        // copy-then-delete sequence could discard a save which arrived after the copy finished.
        File.Move(path, conflict, overwrite: false);
        return Path.GetRelativePath(root, conflict).Replace('\\', '/');
    }

    private void PreserveBeforeDelete(string path, string relative)
    {
        // Keep recovery on the same volume as the sync root so preservation is an atomic rename,
        // even when the configured app-state directory is on a different drive.
        var needsRoot = _placeholders.IsPlaceholder(path) && !_placeholders.IsHydrated(path);
        var recovery = !needsRoot && string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(_recoveryPath), StringComparison.OrdinalIgnoreCase)
            ? _recoveryPath : Path.Combine(_settings.RootPath, ".cloudbay", "Recovery");
        var destination = Path.Combine(recovery, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6], relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(path, destination, overwrite: false);
    }

    private async Task<bool> TryAdoptRemoteAsync(string relative, string path, CloudObject cloud, AppSettings settings, CancellationToken ct)
    {
        if (cloud.Sha1 is not { Length: > 0 } || cloud.Sha1.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        await TransferResources.Hashing.WaitAsync(ct);
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (source.Length != cloud.Size) return false;
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(source, ct)).ToLowerInvariant();
            if (!sha1.Equals(cloud.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
            // The read handle denies writers while the matching bytes get the cloud timestamp.
            File.SetLastWriteTimeUtc(path, cloud.ModifiedUtc.UtcDateTime);
        }
        finally { TransferResources.Hashing.Release(); }
        var baseline = new SyncEntry(relative, cloud, cloud.Size, cloud.ModifiedUtc) { NativeMarkPending = true };
        _manifest.Put(baseline);
        ShowPendingNativeMark(baseline, ActivityKind.Upload);
        await FinishNativeMarkAsync(baseline, settings, ct);
        _transfers.Complete(relative, ActivityKind.Upload);
        return true;
    }

    private async Task<bool> TryFinishPendingNativeMarkAsync(SyncEntry baseline, AppSettings settings, CancellationToken ct)
    {
        var path = PathRules.FullPath(settings.RootPath, baseline.RelativePath);
        if (!File.Exists(path)) return false;
        if (_placeholders.IsPlaceholder(path) && !_placeholders.IsHydrated(path))
            await _placeholders.HydrateAsync(path, ct);
        if (baseline.Remote.Sha1 is not { Length: 40 } expected || !expected.All(Uri.IsHexDigit)) return false;
        await TransferResources.Hashing.WaitAsync(ct);
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Timestamp equality is not enough: editors and restore tools can retain it while
            // replacing same-size bytes. A pending metadata operation must recheck the source.
            if (source.Length != baseline.LocalSize || File.GetLastWriteTimeUtc(path) != baseline.LocalWriteUtc.UtcDateTime)
                return false;
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(source, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        finally { TransferResources.Hashing.Release(); }
        // The source handle and shared hash slot are released before native marking, which
        // obtains its own protected handle and rechecks bytes atomically before marking clean.
        ShowPendingNativeMark(baseline, ActivityKind.Upload);
        await FinishNativeMarkAsync(baseline, settings, ct);
        _transfers.Complete(baseline.RelativePath, ActivityKind.Upload);
        Record(ActivityKind.Information, baseline.RelativePath, "Windows file sync status updated; the verified B2 copy was retained.");
        return true;
    }

    private void ShowPendingNativeMark(SyncEntry baseline, ActivityKind kind)
    {
        _transfers.Queue([(baseline.RelativePath, kind, baseline.LocalSize)]);
        SetPhase(baseline.RelativePath, kind, TransferPhase.Verifying);
        UpdateProgress(baseline.RelativePath, kind,
            new(baseline.LocalSize, baseline.LocalSize) { IsBaseline = true });
        SetStatus(_snapshot with { Message = "Completing Windows file sync status" });
    }

    private async Task FinishNativeMarkAsync(SyncEntry baseline, AppSettings settings, CancellationToken ct)
    {
        var path = PathRules.FullPath(settings.RootPath, baseline.RelativePath);
        try
        {
            await _placeholders.MarkInSyncAsync(path, baseline.Remote with { ModifiedUtc = baseline.LocalWriteUtc }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.COMException)
        {
            throw new IOException("Your file is verified in Backblaze B2. Windows could not update its sync status; " +
                "CloudBay will retry without uploading the same bytes again. " + error.Message, error);
        }
        _manifest.Put(baseline with { NativeMarkPending = false });
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await TransferResources.Hashing.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                return Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
            }, ct);
        }
        finally { TransferResources.Hashing.Release(); }
    }
    private void SetPhase(string path, ActivityKind kind, TransferPhase phase)
    {
        _transfers.Phase(path, kind, phase);
        SetStatus(_snapshot with { State = ClientState.Syncing, Message = "Syncing your files" });
    }
    private void UpdateProgress(string path, ActivityKind kind, TransferProgress value)
    {
        if (_transfers.Progress(path, kind, value)) SetStatus(_snapshot with { State = ClientState.Syncing, Message = "Syncing your files" });
    }
    private void SetStatus(SyncSnapshot value)
    {
        lock (_progressGate)
        {
            if (_paused) value = value with { State = ClientState.Paused, Message = "Sync paused" };
            if (value.State == ClientState.Paused) _transfers.Pause();
            _snapshot = _transfers.Apply(value); _status(_snapshot);
        }
    }
    private void Record(ActivityKind kind, string path, string message, long bytes = 0) =>
        _activity(new(DateTimeOffset.UtcNow, kind, path, message, bytes));

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        lock (_stateGate)
        {
            try { _cycleCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }
        _watcher?.Dispose();
        if (_background is not null) await _background;
        await _cycleGate.WaitAsync(); _cycleGate.Release();
        _lifetime.Dispose(); _wake.Dispose(); _cycleGate.Dispose();
    }
    private sealed record LocalFile(long Size, DateTimeOffset WriteUtc, bool Hydrated, bool HasLocalChanges);
    private sealed record UploadedLocal(string Relative, CloudObject File, long Size, DateTimeOffset Modified);
    private sealed record PreparedLocal(string Relative, FileStream Source, string Sha1, DateTimeOffset Modified);
    private sealed class LocalSnapshot
    {
        public Dictionary<string, LocalFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unavailable = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _protectedParents = new(StringComparer.OrdinalIgnoreCase);

        public void Block(string relative, Exception? error = null)
        {
            _unavailable.Add(relative);
            // An ancestor folder marker can hide the unavailable subtree through a
            // cloud/local type change. Protect the marker without blocking healthy siblings.
            for (var index = relative.LastIndexOf('/'); index > 0; index = relative.LastIndexOf('/', index - 1))
                _protectedParents.Add(relative[..index]);
            if (error is not null)
                Issues.TryAdd(relative, $"Could not inspect '{relative}'. This path and its cloud history were left unchanged; " +
                    "readable folders can continue syncing. " + FileSystemError.Describe(error));
        }

        public bool IsUnavailable(string relative)
        {
            if (_protectedParents.Contains(relative)) return true;
            for (var path = relative; ;)
            {
                if (_unavailable.Contains(path)) return true;
                var separator = path.LastIndexOf('/');
                if (separator < 0) return false;
                path = path[..separator];
            }
        }

        public void RemoveUnavailableEntries()
        {
            foreach (var path in Files.Keys.Where(IsUnavailable).ToArray()) Files.Remove(path);
            Directories.RemoveWhere(IsUnavailable);
        }
    }
    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }
}

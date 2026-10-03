using System.Security.Cryptography;

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
    private DateTimeOffset? _resumeAt;
    private readonly object _stateGate = new();
    private HashSet<string>? _pendingDeletionReview;
    private HashSet<string>? _approvedDeletionReview;
    private readonly object _progressGate = new();
    private readonly Dictionary<string, TransferProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private SyncSnapshot _snapshot = new(ClientState.Connecting, "Connecting to Backblaze B2");

    public SyncEngine(ICloudStore cloud, IPlaceholderService placeholders, SyncManifest manifest,
        AppSettings settings, string recoveryPath, Action<ActivityEvent> activity, Action<SyncSnapshot> status,
        Func<string?>? policy = null)
    {
        _cloud = cloud; _placeholders = placeholders; _manifest = manifest; _settings = settings;
        _recoveryPath = recoveryPath; _activity = activity; _status = status; _policy = policy ?? (() => null);
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
        SetStatus(_snapshot with { State = ClientState.Syncing, Message = "Checking for changes" });
        var baseline = _manifest.ReadAll();
        var directoryBaseline = _manifest.ReadDirectories();
        var localSnapshot = await Task.Run(() => ScanLocal(settings, ct), ct);
        var local = localSnapshot.Files;
        var remote = new Dictionary<string, CloudObject>(StringComparer.OrdinalIgnoreCase);
        var remoteDirectories = new Dictionary<string, CloudObject>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        await foreach (var file in _cloud.ListCurrentAsync(settings.BucketId, settings.Prefix, ct))
        {
            if (file.Action != "upload" || file.Key == settings.Prefix) continue;
            var directory = file.Key.EndsWith('/');
            if (directory && file.Size != 0) throw new InvalidDataException("B2 contains a nonempty object whose name ends in '/'. It cannot be represented as a Windows folder.");
            string relative;
            try { relative = PathRules.FromKey(directory ? file.Key[..^1] : file.Key, settings.Prefix); }
            catch (InvalidDataException error) { invalid++; Record(ActivityKind.Error, file.Key, error.Message); continue; }
            if (PathRules.IsExcluded(relative, settings, isDirectory: directory)) continue;
            if (!(directory ? remoteDirectories : remote).TryAdd(relative, file))
                throw new InvalidDataException("B2 contains names that differ only by letter case. Resolve the collision before syncing to Windows.");
        }
        ValidateRemoteHierarchy(remote.Keys, remoteDirectories.Keys);
        ValidateMixedHierarchy(localSnapshot, remote.Keys, remoteDirectories.Keys);
        // Do not infer deletions unless BOTH complete snapshots were read successfully.
        var deletions = baseline.Keys.Where(p => !PathRules.IsExcluded(p, settings) &&
            (!local.ContainsKey(p) || !remote.ContainsKey(p))).Select(p => "F:" + p)
            .Concat(directoryBaseline.Keys.Where(p => !PathRules.IsExcluded(p, settings, isDirectory: true) &&
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
        var errors = invalid;
        foreach (var relative in local.Keys.Concat(remote.Keys).Concat(baseline.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (PathRules.IsExcluded(relative, settings)) continue;
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
                    else await ApplyRemoteAsync(relative, cloud, settings, ct);
                    continue;
                }
                if (last is null)
                {
                    if (disk.Hydrated && await TryAdoptRemoteAsync(relative, path, cloud, ct))
                    {
                        // A completed B2 upload followed by a crash can leave no local baseline.
                        // Matching the locked bytes safely adopts the already uploaded cloud version.
                    }
                    else
                    {
                        if (disk.Hydrated) uploads.Add(PreserveConflict(path, settings.RootPath));
                        await ApplyRemoteAsync(relative, cloud, settings, ct);
                        Record(ActivityKind.Conflict, relative, "An existing local file differed from B2. Both copies were preserved.");
                    }
                    continue;
                }
                var changed = IsLocallyChanged(disk, last);
                if (cloud.FileId != last.Remote.FileId)
                {
                    if (changed)
                    {
                        if (disk.Hydrated && await TryAdoptRemoteAsync(relative, path, cloud, ct)) continue;
                        uploads.Add(PreserveConflict(path, settings.RootPath));
                        Record(ActivityKind.Conflict, relative, "Local and cloud edits overlapped. Both copies were preserved.");
                    }
                    await ApplyRemoteAsync(relative, cloud, settings, ct);
                }
                else if (changed) uploads.Add(relative);
                else if (!settings.FilesOnDemand && !disk.Hydrated)
                    await _placeholders.SetPinAsync(path, PinMode.AlwaysAvailable, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { errors++; Record(ActivityKind.Error, relative, error.Message); }
        }

        SetStatus(_snapshot with { Pending = uploads.Count, Message = uploads.Count == 0 ? "Finishing sync" : $"Uploading {uploads.Count} files" });
        var remaining = uploads.Count;
        await Parallel.ForEachAsync(uploads.Distinct(StringComparer.OrdinalIgnoreCase), new ParallelOptions
        { MaxDegreeOfParallelism = settings.UploadConcurrency, CancellationToken = ct }, async (relative, token) =>
        {
            try { await UploadLocalAsync(relative, settings, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Interlocked.Increment(ref errors); Record(ActivityKind.Error, relative, error.Message); }
            finally
            {
                lock (_progressGate) _progress.Remove(relative);
                var left = Interlocked.Decrement(ref remaining);
                SetStatus(_snapshot with { Pending = Math.Max(0, left) });
            }
        });
        errors += await ReconcileDirectoriesAsync(localSnapshot.Directories, remoteDirectories, directoryBaseline, settings, ct);
        var final = _manifest.ReadAll();
        var localBytes = (await Task.Run(() => ScanLocal(settings, ct), ct)).Files.Values.Where(f => f.Hydrated).Sum(f => f.Size);
        SetStatus(new(errors == 0 ? ClientState.UpToDate : ClientState.Attention,
            errors == 0 ? "Your files are up to date" : $"{errors} items need attention. Failed changes will retry.",
            0, final.Count, final.Values.Sum(f => f.Remote.Size), localBytes, LastSync: DateTimeOffset.UtcNow));
    }

    private LocalSnapshot ScanLocal(AppSettings settings, CancellationToken ct)
    {
        var result = new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(settings.RootPath);
        while (directories.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(settings.RootPath, child).Replace('\\', '/');
                var attributes = File.GetAttributes(child);
                if (PathRules.IsExcluded(relative, settings, isDirectory: (attributes & FileAttributes.Directory) != 0)) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && new DirectoryInfo(child).LinkTarget is not null)
                        throw new IOException($"Linked directory is not supported: {relative}");
                    PathRules.ValidateRelative(relative);
                    if (!folderNames.Add(relative)) throw new InvalidDataException("Local folder names differ only by case and cannot be synchronized safely.");
                    directories.Push(child); continue;
                }
                var placeholder = _placeholders.IsPlaceholder(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !placeholder)
                    throw new IOException($"Linked file is not supported: {relative}");
                PathRules.ValidateRelative(relative);
                result.Add(relative, ReadLocal(child));
            }
        }
        return new(result, folderNames);
    }

    private async Task<int> ReconcileDirectoriesAsync(HashSet<string> local, Dictionary<string, CloudObject> remote,
        IReadOnlyDictionary<string, SyncDirectoryEntry> baseline, AppSettings settings, CancellationToken ct)
    {
        var errors = 0;
        // Children first: remote folder removal only removes truly empty directories, never their data.
        var paths = local.Concat(remote.Keys).Concat(baseline.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (PathRules.IsExcluded(relative, settings, isDirectory: true)) continue;
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
                    await _cloud.HideAsync(settings.BucketId, cloud.Key, ct);
                    _manifest.RemoveDirectory(relative);
                    Record(ActivityKind.Delete, relative, "Moved the deleted folder marker to B2 version history.");
                    continue;
                }
                if (File.Exists(path)) throw new IOException($"The cloud folder '{relative}' conflicts with a local file. The local file was retained.");
                Directory.CreateDirectory(path);
                _manifest.PutDirectory(new(relative, cloud));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { errors++; Record(ActivityKind.Error, relative, error.Message); }
        }
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
        return new(info.Length, info.LastWriteTimeUtc, !_placeholders.IsPlaceholder(path) || _placeholders.IsHydrated(path),
            _placeholders.HasLocalChanges(path));
    }

    private async Task UploadLocalAsync(string relative, AppSettings settings, CancellationToken ct)
    {
        var path = PathRules.FullPath(settings.RootPath, relative);
        if (!File.Exists(path)) return;
        if (_placeholders.IsPlaceholder(path) && !_placeholders.IsHydrated(path))
            await _placeholders.HydrateAsync(path, ct);
        // Deny concurrent writes for the hash and transfer so B2 receives the exact verified snapshot.
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Read metadata after acquiring the writer-denying handle; an editor may have saved between
        // the initial directory scan and this open.
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(source, ct)).ToLowerInvariant();
        source.Position = 0;
        var file = await _cloud.UploadAsync(settings.BucketId, settings.Prefix + relative, source, source.Length,
            sha1, modified, new InlineProgress(p => UpdateProgress(relative, p)), ct);
        var size = source.Length;
        await source.DisposeAsync();
        var after = new FileInfo(path);
        // A save immediately after upload remains dirty and will be sent by the next scan.
        if (after.Exists && after.Length == size && after.LastWriteTimeUtc == modified.UtcDateTime)
            await _placeholders.MarkInSyncAsync(path, file with { ModifiedUtc = modified }, ct);
        _manifest.Put(new(relative, file, size, modified));
        Record(ActivityKind.Upload, relative, "Uploaded to Backblaze B2", file.Size);
    }

    private async Task ApplyRemoteAsync(string relative, CloudObject cloud, AppSettings settings, CancellationToken ct)
    {
        var path = PathRules.FullPath(settings.RootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (settings.FilesOnDemand && (!File.Exists(path) || _placeholders.IsPlaceholder(path)))
            await _placeholders.CreateOrUpdateAsync(path, cloud, true, ct);
        else
        {
            var original = File.Exists(path) ? ReadLocal(path) : null;
            var temporary = Path.Combine(settings.RootPath, ".cloudbay", "transfers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    await _cloud.DownloadAsync(cloud, output, progress: new InlineProgress(p => UpdateProgress(relative, p)), cancellationToken: ct);
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                }
                if (new FileInfo(temporary).Length != cloud.Size)
                    throw new InvalidDataException("The downloaded file length does not match B2 metadata.");
                if (cloud.Sha1 is { Length: > 0 } sha1 && !sha1.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(await HashAsync(temporary, ct), sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded file failed its B2 checksum verification.");
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
                await _placeholders.MarkInSyncAsync(path, cloud, ct);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!settings.FilesOnDemand) await _placeholders.SetPinAsync(path, PinMode.AlwaysAvailable, ct);
        SaveBaseline(relative, cloud);
        Record(ActivityKind.Download, relative, settings.FilesOnDemand ? "Cloud file is available in Explorer" : "Downloaded and available offline", cloud.Size);
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
        var name = Path.GetFileNameWithoutExtension(path);
        var conflict = Path.Combine(Path.GetDirectoryName(path)!, $"{name} (conflict {DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}){Path.GetExtension(path)}");
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

    private async Task<bool> TryAdoptRemoteAsync(string relative, string path, CloudObject cloud, CancellationToken ct)
    {
        if (cloud.Sha1 is not { Length: > 0 } || cloud.Sha1.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
        {
            if (source.Length != cloud.Size) return false;
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(source, ct)).ToLowerInvariant();
            if (!sha1.Equals(cloud.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
            // The read handle denies writers while the matching bytes get the cloud timestamp.
            File.SetLastWriteTimeUtc(path, cloud.ModifiedUtc.UtcDateTime);
        }
        await _placeholders.MarkInSyncAsync(path, cloud, ct);
        SaveBaseline(relative, cloud);
        return true;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
    private void UpdateProgress(string path, TransferProgress value)
    {
        lock (_progressGate)
        {
            _progress[path] = value;
            SetStatus(_snapshot with { State = ClientState.Syncing, Message = $"Transferring {Path.GetFileName(path)}",
                TransferredBytes = _progress.Values.Sum(p => p.Bytes), TransferTotalBytes = _progress.Values.Sum(p => p.TotalBytes) });
        }
    }
    private void SetStatus(SyncSnapshot value) { _snapshot = value; _status(value); }
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
    private sealed record LocalSnapshot(Dictionary<string, LocalFile> Files, HashSet<string> Directories);
    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }
}

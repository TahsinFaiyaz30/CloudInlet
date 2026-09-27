using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CloudBay.Core.Safety;

namespace CloudBay.Core.Links;

public sealed record FolderLink(
    Guid Id,
    string LocalPath,
    string CloudPath,
    string LocalBackupPath,
    DateTimeOffset CreatedAtUtc,
    Guid JournalId,
    string? JournalWarning = null);

public sealed record LinkPreflight(
    string LocalPath,
    string CloudRoot,
    string CloudPath,
    long FileCount,
    long TotalBytes,
    string Mechanism,
    string SafetyNote);

public sealed record LinkProgress(long FilesCopied, long TotalFiles, long BytesCopied, long TotalBytes);

public sealed record LinkRollbackResult(bool RestoredLocalPath, string Message, Guid? JournalId = null);

/// <summary>
/// Copies and verifies a local folder in the cloud, then replaces only its local path
/// with a directory symbolic link. The original folder is retained as a local backup.
/// No reparse point is ever created inside the provider's mount.
/// </summary>
public sealed class FolderLinkService
{
    private const int SymbolicLinkDirectory = 1;
    private const int AllowUnprivilegedCreate = 2;

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLinkNative(
        string linkPath, string targetPath, int flags);

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryNative(string path);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle OpenDirectoryHandle(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
        StringBuilder filePath, uint filePathLength, uint flags);

    private readonly IOperationJournal _journal;
    private readonly string _recordsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FolderLinkService(IOperationJournal journal, string? recordsPath = null)
    {
        _journal = journal;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) && recordsPath is null)
            throw new IOException("Windows local application data directory is unavailable.");
        _recordsPath = recordsPath ?? Path.Combine(local, "CloudBay", "links.json");
    }

    public async Task<IReadOnlyList<FolderLink>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await LoadRecordsAsync(cancellationToken).ConfigureAwait(false)).ToArray(); }
        finally { _gate.Release(); }
    }

    public Task<LinkPreflight> PreflightAsync(
        string localPath, string cloudRoot, string? targetName = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => PreflightCore(localPath, cloudRoot, targetName, cancellationToken), cancellationToken);

    private static LinkPreflight PreflightCore(
        string localPath, string cloudRoot, string? targetName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows directory links are required.");
        var source = NormalizeDirectory(localPath);
        var root = NormalizeDirectory(cloudRoot);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Local folder does not exist: {source}");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Cloud root does not exist: {root}");
        RejectReparseAncestors(source);
        if (IsSameOrChild(source, root) || IsSameOrChild(root, source))
            throw new IOException("Local and cloud folders must be separate, non-nested paths.");
        // A selected cloud mount may itself be a reparse point. Compare the
        // resolved directories as well, so a mount back into the source cannot
        // make the copy recursive or replace the source through an alias.
        RejectPhysicalOverlap(source, root);
        if (Path.GetPathRoot(source)!.TrimEnd('\\') == source.TrimEnd('\\'))
            throw new IOException("A drive root cannot be linked.");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The local source is already a link or mount point.");
        var localDrive = new DriveInfo(Path.GetPathRoot(source)!);
        if (localDrive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("The local source must be on a local filesystem.");
        if (localDrive.DriveFormat is not ("NTFS" or "ReFS"))
            throw new IOException("The local filesystem must support Windows directory symbolic links (NTFS or ReFS).");

        var name = ValidateTargetName(targetName ?? Path.GetFileName(source.TrimEnd('\\')));
        var destination = Path.Combine(root, name);
        if (PathExists(destination))
            throw new IOException($"Cloud destination already exists: {destination}");
        if ((File.GetAttributes(root) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"Cloud root is read-only: {root}");

        long files = 0;
        long bytes = 0;
        foreach (var path in EnumerateTree(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The local folder contains a link or mount point: {path}");
            if ((attrs & FileAttributes.Directory) == 0)
            {
                using var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                files++;
                checked { bytes += new FileInfo(path).Length; }
            }
        }
        VerifyWritableDirectory(root, cancellationToken);
        VerifyWritableDirectory(Path.GetDirectoryName(source)!, cancellationToken);
        var safetyNote = "The original folder is retained under a local CloudBay backup name. The cloud copy remains after unlinking.";
        if (DirectoryCapacityProbe.TryGet(root, out ulong free, out ulong total, out string? capacityReason) &&
            total > 0)
        {
            if ((ulong)bytes > free)
                throw new IOException($"Cloud target has insufficient reported free space ({free:N0} bytes available; {bytes:N0} bytes required).");
        }
        else
        {
            safetyNote += " The cloud provider did not report usable capacity" +
                (capacityReason is null ? "." : $": {capacityReason}.") +
                " CloudBay verifies every copied file and leaves the source intact if the provider runs out of space.";
        }
        return new LinkPreflight(source, root, destination, files, bytes,
            "Local directory symbolic link to a verified cloud copy",
            safetyNote);
    }

    public Task<FolderLink> LinkAsync(
        string localPath, string cloudRoot, string? targetName = null,
        IProgress<LinkProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => LinkCoreAsync(localPath, cloudRoot, targetName, progress, cancellationToken),
            cancellationToken);

    private async Task<FolderLink> LinkCoreAsync(
        string localPath, string cloudRoot, string? targetName,
        IProgress<LinkProgress>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await PreflightAsync(localPath, cloudRoot, targetName, cancellationToken).ConfigureAwait(false);
            var records = await LoadRecordsAsync(cancellationToken).ConfigureAwait(false);
            if (records.Any(record => SamePath(record.LocalPath, plan.LocalPath) ||
                                      SamePath(record.CloudPath, plan.CloudPath)))
                throw new IOException("This source or destination is already managed by CloudBay.");

            var id = Guid.NewGuid();
            var backup = Path.Combine(Path.GetDirectoryName(plan.LocalPath)!,
                $".CloudBay-backup-{Path.GetFileName(plan.LocalPath)}-{id:N}");
            var stage = plan.CloudPath + $".cloudbay-stage-{id:N}";
            if (PathExists(backup) || PathExists(stage))
                throw new IOException("A CloudBay staging or backup path already exists.");

            var metadata = new Dictionary<string, string>
            {
                ["LinkId"] = id.ToString("D"),
                ["BackupPath"] = backup,
                ["StagePath"] = stage,
                ["Mechanism"] = "LocalDirectorySymbolicLink"
            };
            var journal = await _journal.BeginAsync("link.create", plan.LocalPath, plan.CloudPath,
                metadata, cancellationToken).ConfigureAwait(false);
            var sourceRenamed = false;
            var linked = false;
            var recordSaved = false;
            FolderLink? committedRecord = null;
            try
            {
                // Recheck immediately before mutation. A collision is never overwritten.
                if (PathExists(plan.CloudPath) ||
                    !Directory.Exists(plan.LocalPath) ||
                    (File.GetAttributes(plan.LocalPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The paths changed after preflight; retry after inspecting them.");

                Directory.CreateDirectory(stage);
                var expected = await CopyAndVerifyAsync(plan.LocalPath, stage, plan,
                    progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stage, plan.CloudPath);
                Directory.Move(plan.LocalPath, backup);
                sourceRenamed = true;
                // Verify that the source did not change while it was being copied.
                // Once the original path has moved, finish the commit or restore
                // it even if the caller cancels. Leaving a missing source path is
                // less safe than completing this short finalization.
                await VerifySourceSnapshotAsync(backup, expected, CancellationToken.None).ConfigureAwait(false);
                CreateDirectoryLink(plan.LocalPath, plan.CloudPath);
                linked = true;
                var record = new FolderLink(id, plan.LocalPath, plan.CloudPath, backup,
                    DateTimeOffset.UtcNow, journal.Id);
                committedRecord = record;
                records.Add(record);
                await SaveRecordsAsync(records, CancellationToken.None).ConfigureAwait(false);
                recordSaved = true;
                await _journal.CompleteAsync(journal.Id, new Dictionary<string, string>
                {
                    ["BackupPath"] = backup,
                    ["CloudCopyPath"] = plan.CloudPath,
                    ["RecordId"] = id.ToString("D")
                }, CancellationToken.None).ConfigureAwait(false);
                return record;
            }
            catch (Exception ex)
            {
                if (recordSaved && committedRecord is not null)
                {
                    // The active link and its record are already durable. A
                    // journal write failure cannot truthfully be reported as a
                    // failed link creation or undone without reconciling data.
                    return committedRecord with
                    {
                        JournalWarning = "The link is active, but its completion journal could not be updated: " + ex.Message
                    };
                }
                string? restoreError = null;
                if (!recordSaved && sourceRenamed)
                {
                    try
                    {
                        // Restore the local path. Never remove cloud contents or the local backup.
                        if (linked && IsOwnedLink(plan.LocalPath, plan.CloudPath))
                            RemoveOwnedDirectoryLink(plan.LocalPath, plan.CloudPath);
                        if (!PathExists(plan.LocalPath) && Directory.Exists(backup))
                            Directory.Move(backup, plan.LocalPath);
                    }
                    catch (Exception restoreException)
                    {
                        restoreError = restoreException.Message;
                    }
                }
                try
                {
                    await _journal.FailAsync(journal.Id,
                        ex.Message + (restoreError is null ? string.Empty : " Local restore needs attention: " + restoreError),
                        new Dictionary<string, string>
                    {
                        ["BackupPath"] = backup,
                        ["StagePath"] = stage,
                        ["CloudCopyPath"] = plan.CloudPath,
                        ["LocalRestored"] = Directory.Exists(plan.LocalPath).ToString()
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* Preserve the original failure; the begin entry remains on disk. */ }
                if (restoreError is not null)
                    throw new IOException($"{ex.Message} The original folder is at {backup}; restoring it to {plan.LocalPath} needs attention: {restoreError}", ex);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public Task<LinkRollbackResult> UnlinkAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.Run(() => UnlinkCoreAsync(id, cancellationToken), cancellationToken);

    private async Task<LinkRollbackResult> UnlinkCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsAsync(cancellationToken).ConfigureAwait(false);
            var record = records.SingleOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException($"CloudBay link {id} was not found.");
            if (!IsOwnedLink(record.LocalPath, record.CloudPath))
                throw new IOException("The local link has changed; refusing to remove an unrecognized path.");
            if (!Directory.Exists(record.LocalBackupPath) ||
                (File.GetAttributes(record.LocalBackupPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The original local backup is missing or has changed.");
            foreach (var backupEntry in EnumerateTree(record.LocalBackupPath, cancellationToken))
                if ((File.GetAttributes(backupEntry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The original local backup contains a link: {backupEntry}");
            if (!Directory.Exists(record.CloudPath))
                throw new IOException("The cloud copy is unavailable. Reconnect the cloud mount before unlinking so changes can be brought back safely.");
            var requiredLocalBytes = await EstimateReconcileBytesAsync(record, cancellationToken)
                .ConfigureAwait(false);
            ulong localFree = 0;
            if (requiredLocalBytes > 0 &&
                !DirectoryCapacityProbe.TryGet(record.LocalBackupPath, out localFree, out _,
                    out string? capacityReason))
                throw new IOException("Local restore capacity could not be verified: " + capacityReason);
            if (requiredLocalBytes > 0 && (ulong)requiredLocalBytes > localFree)
                throw new IOException($"Insufficient local capacity to restore cloud changes ({requiredLocalBytes:N0} bytes required).");

            var journal = await _journal.BeginAsync("link.unlink", record.LocalPath, record.CloudPath,
                new Dictionary<string, string> { ["BackupPath"] = record.LocalBackupPath, ["LinkId"] = id.ToString("D") },
                cancellationToken).ConfigureAwait(false);
            bool localRestored = false;
            bool recordsSaved = false;
            var reconciled = new ReconcileResult(0, 0);
            try
            {
                reconciled = await ReconcileCloudToBackupAsync(record, journal.Id, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOwnedLink(record.LocalPath, record.CloudPath))
                    throw new IOException("The local link changed during reconciliation; it was not removed.");
                RemoveOwnedDirectoryLink(record.LocalPath, record.CloudPath);
                try { Directory.Move(record.LocalBackupPath, record.LocalPath); }
                catch
                {
                    // The original data remains in the backup. Restore access through the
                    // original link path when possible, leaving both data sets untouched.
                    if (!PathExists(record.LocalPath) && Directory.Exists(record.CloudPath))
                        CreateDirectoryLink(record.LocalPath, record.CloudPath);
                    throw;
                }
                localRestored = true;
                records.Remove(record);
                // The original path is now restored. Finish the record and
                // journal updates regardless of a late caller cancellation.
                await SaveRecordsAsync(records, CancellationToken.None).ConfigureAwait(false);
                recordsSaved = true;
                await _journal.CompleteAsync(journal.Id, new Dictionary<string, string>
                {
                    ["CloudCopyPath"] = record.CloudPath,
                    ["LocalRestored"] = "True",
                    ["CloudFilesCopied"] = reconciled.Copied.ToString(),
                    ["LocalConflictsPreserved"] = reconciled.Conflicts.ToString()
                }, CancellationToken.None).ConfigureAwait(false);
                return new LinkRollbackResult(true,
                    $"Local folder restored with {reconciled.Copied} cloud files brought back. " +
                    $"{reconciled.Conflicts} different local versions were preserved with CloudBay conflict names. " +
                    $"The cloud copy remains at {record.CloudPath}.",
                    journal.Id);
            }
            catch (Exception ex)
            {
                if (recordsSaved)
                    return new LinkRollbackResult(true,
                        $"Local folder restored at {record.LocalPath}; {reconciled.Copied} cloud files were brought back. " +
                        "The link record was removed, but its completion journal could not be updated: " + ex.Message,
                        journal.Id);

                string? restoreError = null;
                if (localRestored)
                {
                    try
                    {
                        // A failed record write leaves the old active record on
                        // disk. Return to that state so retrying unlink remains
                        // possible, while keeping reconciled files in the backup.
                        Directory.Move(record.LocalPath, record.LocalBackupPath);
                        CreateDirectoryLink(record.LocalPath, record.CloudPath);
                    }
                    catch (Exception restoreException)
                    {
                        restoreError = restoreException.Message;
                    }
                }
                try { await _journal.FailAsync(journal.Id,
                    ex.Message + (restoreError is null ? string.Empty : " Link restoration needs attention: " + restoreError),
                    cancellationToken: CancellationToken.None).ConfigureAwait(false); }
                catch { }
                if (restoreError is not null)
                    throw new IOException($"{ex.Message} The local folder at {record.LocalPath} and backup at {record.LocalBackupPath} need review: {restoreError}", ex);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<LinkRollbackResult> RollbackAsync(Guid journalId, CancellationToken cancellationToken = default)
    {
        var entry = await _journal.GetAsync(journalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Journal entry {journalId} was not found.");
        if (entry.Operation != "link.create")
            throw new InvalidOperationException("Only a folder-link creation can be rolled back by this service.");
        var records = await ListAsync(cancellationToken).ConfigureAwait(false);
        var active = records.FirstOrDefault(record => record.JournalId == journalId);
        if (active is not null)
            return await UnlinkAsync(active.Id, cancellationToken).ConfigureAwait(false);

        var source = entry.SourcePath ?? throw new IOException("Journal source path is missing.");
        if (!entry.Metadata.TryGetValue("BackupPath", out var backup))
            throw new IOException("Journal backup path is missing.");
        if (!PathExists(source) && Directory.Exists(backup))
        {
            var restore = await _journal.BeginAsync("link.rollback", backup, source,
                new Dictionary<string, string> { ["OriginalJournalId"] = journalId.ToString("D") },
                cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Move(backup, source);
                await _journal.CompleteAsync(restore.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return new LinkRollbackResult(true, "Original local folder restored. Any cloud copy remains untouched.", restore.Id);
            }
            catch (Exception ex)
            {
                try { await _journal.FailAsync(restore.Id, ex.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
                catch { }
                throw;
            }
        }
        return new LinkRollbackResult(Directory.Exists(source),
            "No local path change was required. Any staged or completed cloud copy remains untouched.");
    }

    private static async Task<Dictionary<string, byte[]>> CopyAndVerifyAsync(
        string source, string destination, LinkPreflight plan,
        IProgress<LinkProgress>? progress, CancellationToken ct)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long copiedFiles = 0, copiedBytes = 0;
        foreach (var path in EnumerateTree(source, ct))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, path);
            var target = Path.Combine(destination, relative);
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"A link appeared during copying: {path}");
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var sha = SHA256.Create();
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, count, null, 0);
                    copiedBytes += count;
                    progress?.Report(new LinkProgress(copiedFiles, plan.FileCount, copiedBytes, plan.TotalBytes));
                }
                sha.TransformFinalBlock([], 0, 0);
                hashes.Add(relative, sha.Hash!);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            var cloudHash = await HashFileAsync(target, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hashes[relative], cloudHash))
                throw new IOException($"Cloud copy did not verify: {target}");
            copiedFiles++;
            progress?.Report(new LinkProgress(copiedFiles, plan.FileCount, copiedBytes, plan.TotalBytes));
        }
        if (copiedFiles != plan.FileCount || copiedBytes != plan.TotalBytes)
            throw new IOException("The local folder changed during copying; its cloud copy needs inspection.");
        return hashes;
    }

    private static async Task VerifySourceSnapshotAsync(
        string backup, Dictionary<string, byte[]> expected, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateTree(backup, ct))
        {
            ct.ThrowIfCancellationRequested();
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"A link appeared in the original folder: {path}");
            if ((attrs & FileAttributes.Directory) != 0) continue;
            var relative = Path.GetRelativePath(backup, path);
            if (!expected.TryGetValue(relative, out var hash))
                throw new IOException("The local folder changed during copying.");
            var actual = await HashFileAsync(path, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, actual))
                throw new IOException($"The local file changed during copying: {path}");
            seen.Add(relative);
        }
        if (seen.Count != expected.Count)
            throw new IOException("The local folder changed during copying.");
    }

    private sealed record ReconcileResult(int Copied, int Conflicts);

    private static async Task<long> EstimateReconcileBytesAsync(FolderLink record, CancellationToken ct)
    {
        long required = 0;
        foreach (var cloudEntry in EnumerateTree(record.CloudPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(cloudEntry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cloud copy contains a link: {cloudEntry}");
            if ((attributes & FileAttributes.Directory) != 0) continue;
            var relative = Path.GetRelativePath(record.CloudPath, cloudEntry);
            var backupEntry = Path.Combine(record.LocalBackupPath, relative);
            if (File.Exists(backupEntry))
            {
                var cloudHash = await HashFileAsync(cloudEntry, ct).ConfigureAwait(false);
                var backupHash = await HashFileAsync(backupEntry, ct).ConfigureAwait(false);
                if (CryptographicOperations.FixedTimeEquals(cloudHash, backupHash)) continue;
            }
            checked { required += new FileInfo(cloudEntry).Length; }
        }
        return required;
    }

    private static async Task<ReconcileResult> ReconcileCloudToBackupAsync(
        FolderLink record, Guid operationId, CancellationToken ct)
    {
        var stage = Path.Combine(Path.GetDirectoryName(record.LocalPath)!,
            $".CloudBay-reconcile-{operationId:N}");
        if (PathExists(stage))
            throw new IOException($"Reconciliation staging path already exists: {stage}");
        Directory.CreateDirectory(stage);
        var cloudDirectories = new List<string>();
        var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var cloudHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var cloudEntry in EnumerateTree(record.CloudPath, ct))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(cloudEntry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Cloud copy contains a link; reconciliation stopped: {cloudEntry}");
                var relative = Path.GetRelativePath(record.CloudPath, cloudEntry);
                var backupEntry = Path.Combine(record.LocalBackupPath, relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    cloudDirectories.Add(relative);
                    continue;
                }
                var cloudHash = await HashFileAsync(cloudEntry, ct).ConfigureAwait(false);
                cloudHashes.Add(relative, cloudHash);
                if (File.Exists(backupEntry))
                {
                    var localHash = await HashFileAsync(backupEntry, ct).ConfigureAwait(false);
                    if (CryptographicOperations.FixedTimeEquals(cloudHash, localHash)) continue;
                }
                var stagedFile = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
                await using (var input = new FileStream(cloudEntry, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(stagedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                var stagedHash = await HashFileAsync(stagedFile, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(cloudHash, stagedHash))
                    throw new IOException($"A cloud file changed during reconciliation: {cloudEntry}");
                staged.Add(relative, cloudHash);
            }

            int conflicts = 0;
            foreach (var relative in cloudDirectories.OrderBy(x => x.Length))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(record.LocalBackupPath, relative);
                if (File.Exists(target))
                {
                    File.Move(target, UniqueConflictName(target, operationId));
                    conflicts++;
                }
                Directory.CreateDirectory(target);
            }
            foreach (var pair in staged)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(record.LocalBackupPath, pair.Key);
                var stagedFile = Path.Combine(stage, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    File.Move(target, UniqueConflictName(target, operationId));
                    conflicts++;
                }
                else if (Directory.Exists(target))
                {
                    Directory.Move(target, UniqueConflictName(target, operationId));
                    conflicts++;
                }
                File.Move(stagedFile, target);
                var localHash = await HashFileAsync(target, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(pair.Value, localHash))
                    throw new IOException($"Local reconciliation failed verification: {target}");
            }
            // The cloud remains authoritative while linked; detect a late change before unlinking.
            foreach (var pair in cloudHashes)
            {
                var cloudEntry = Path.Combine(record.CloudPath, pair.Key);
                if (!File.Exists(cloudEntry))
                    throw new IOException($"Cloud copy changed during reconciliation: {cloudEntry}");
                var currentCloudHash = await HashFileAsync(cloudEntry, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(pair.Value, currentCloudHash))
                    throw new IOException($"Cloud copy changed during reconciliation: {cloudEntry}");
            }
            var currentCloudFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cloudEntry in EnumerateTree(record.CloudPath, ct))
                if ((File.GetAttributes(cloudEntry) & FileAttributes.Directory) == 0)
                    currentCloudFiles.Add(Path.GetRelativePath(record.CloudPath, cloudEntry));
            if (!currentCloudFiles.SetEquals(cloudHashes.Keys))
                throw new IOException("The cloud folder changed during reconciliation; retry unlinking.");
            return new ReconcileResult(staged.Count, conflicts);
        }
        finally
        {
            // Keep any staged data after a failure for manual recovery. On success the
            // moved files leave only empty directories, which are safe to remove.
            if (Directory.Exists(stage) && !Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Any())
            {
                foreach (var dir in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)
                    .OrderByDescending(x => x.Length))
                    Directory.Delete(dir);
                Directory.Delete(stage);
            }
        }
    }

    private static string UniqueConflictName(string existingPath, Guid operationId)
    {
        for (var i = 0; i < 1000; i++)
        {
            var suffix = i == 0 ? string.Empty : $"-{i}";
            var candidate = existingPath + $".cloudbay-local-{operationId:N}{suffix}";
            if (!PathExists(candidate)) return candidate;
        }
        throw new IOException($"Cannot reserve a conflict name for {existingPath}");
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateTree(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                yield return path;
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    stack.Push(path);
            }
        }
    }

    private async Task<List<FolderLink>> LoadRecordsAsync(CancellationToken ct)
    {
        if (!File.Exists(_recordsPath)) return [];
        var data = await File.ReadAllBytesAsync(_recordsPath, ct).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<List<FolderLink>>(data, JsonOptions)
            ?? throw new IOException("CloudBay link records are empty or invalid."); }
        catch (JsonException ex) { throw new IOException("CloudBay link records are corrupt; no link was changed.", ex); }
    }

    private async Task SaveRecordsAsync(List<FolderLink> records, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_recordsPath)!;
        Directory.CreateDirectory(directory);
        var temp = _recordsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions), ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, _recordsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsOwnedLink(string path, string cloudPath)
    {
        var info = new DirectoryInfo(path);
        string? target;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return false;
            target = info.LinkTarget;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (!Path.IsPathFullyQualified(target))
            target = Path.GetFullPath(target, Path.GetDirectoryName(path)!);
        return SamePath(target, cloudPath);
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (PathExists(path)) throw new IOException($"The local link path already exists: {path}");
        if (CreateSymbolicLinkNative(path, target, SymbolicLinkDirectory | AllowUnprivilegedCreate)) return;
        var error = Marshal.GetLastWin32Error();
        // Older Windows builds do not understand the unprivileged flag.
        if (error == 87 && CreateSymbolicLinkNative(path, target, SymbolicLinkDirectory)) return;
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create directory link at {path}");
    }

    private static void RemoveOwnedDirectoryLink(string path, string target)
    {
        if (!IsOwnedLink(path, target))
            throw new IOException("The local link changed; refusing to remove an unrecognized path.");
        // RemoveDirectoryW unlinks a directory reparse point; it does not walk its target.
        if (!RemoveDirectoryNative(path))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not remove directory link at {path}");
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectReparseAncestors(string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory.Parent is not null)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Path contains a link or mount point that could alias another folder: {directory.FullName}");
            directory = directory.Parent;
        }
    }

    private static void RejectPhysicalOverlap(string source, string cloudRoot)
    {
        // VOLUME_NAME_GUID gives both paths the same volume identity even when
        // one is reached through a directory mount point. Some virtual volumes
        // only support DOS names, so retry the pair with that mode.
        if (!TryGetFinalDirectoryPath(source, 1, out string sourceFinal) ||
            !TryGetFinalDirectoryPath(cloudRoot, 1, out string rootFinal))
        {
            if (!TryGetFinalDirectoryPath(source, 0, out sourceFinal) ||
                !TryGetFinalDirectoryPath(cloudRoot, 0, out rootFinal))
                throw new IOException("The selected paths could not be resolved safely. Check that the cloud mount is connected.");
        }
        if (IsCanonicalChild(sourceFinal, rootFinal) || IsCanonicalChild(rootFinal, sourceFinal))
            throw new IOException("The local folder and cloud mount resolve to overlapping locations.");
    }

    private static bool TryGetFinalDirectoryPath(string directory, uint mode, out string finalPath)
    {
        finalPath = string.Empty;
        const uint shareReadWriteDelete = 0x00000007;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using SafeFileHandle handle = OpenDirectoryHandle(directory, 0, shareReadWriteDelete,
            IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, mode);
        if (length == 0 || length >= buffer.Capacity) return false;
        finalPath = buffer.ToString().TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return finalPath.Length > 0;
    }

    private static bool IsCanonicalChild(string candidate, string parent) =>
        candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + '\\', StringComparison.OrdinalIgnoreCase);

    private static void VerifyWritableDirectory(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var probe = Path.Combine(directory, ".cloudbay-write-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
            stream.Flush(true);
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A folder path is required.", nameof(path));
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(NormalizeDirectory(a), NormalizeDirectory(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var value = NormalizeDirectory(candidate);
        var root = NormalizeDirectory(parent);
        return value.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(root.EndsWith('\\') ? root : root + '\\', StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateTargetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new ArgumentException("Choose a single valid Windows folder name.", nameof(name));
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or
            "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or
            "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            throw new ArgumentException("Windows reserves this folder name.", nameof(name));
        return name;
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CloudBay.Core.Folders;

internal static class FolderTransfer
{
    private const int MaximumReportedIssues = 40;

    internal static FolderPreflightResult Scan(
        string source, string destination, string? requiredRoot = null,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null)
    {
        var issues = new List<string>();
        long files = 0;
        long bytes = 0;

        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            return new FolderPreflightResult(false, false, source, destination, 0, 0,
                ["Both source and destination must be absolute paths."]);
        try
        {
            source = Normalize(source);
            destination = Normalize(destination);
            if (destination.Length >= 260)
                issues.Add("The destination exceeds the Windows shell path length limit.");
            if (PathEquals(source, destination))
                return new FolderPreflightResult(Directory.Exists(source), true, source, destination, 0, 0,
                    Directory.Exists(source) ? Array.Empty<string>() : [$"The folder is unavailable: {source}"]);
            if (IsWithin(source, destination) || IsWithin(destination, source))
                issues.Add("The source and destination cannot contain one another.");

            if (requiredRoot is not null && !Directory.Exists(requiredRoot))
                issues.Add($"The cloud root is unavailable: {requiredRoot}");
            if (!Directory.Exists(source))
                issues.Add($"The source folder is unavailable: {source}. Reconnect it before migrating data.");
            else if (IsReparseDirectory(source))
                issues.Add($"The source folder is a link or reparse point: {source}.");

            if (File.Exists(destination)) issues.Add($"A file occupies the destination: {destination}");
            if (Directory.Exists(destination) && IsReparseDirectory(destination))
                issues.Add($"The destination is a link or reparse point: {destination}.");
            ValidateDestinationAncestors(destination, requiredRoot, issues);
            if (requiredRoot is not null && Directory.Exists(requiredRoot) && IsReparseDirectory(requiredRoot))
            {
                // A user-selected mount can be a reparse point. Resolve ordinary
                // junctions/symlinks so a link back into the source cannot recurse.
                FileSystemInfo? resolved = new DirectoryInfo(requiredRoot).ResolveLinkTarget(true);
                if (resolved is not null)
                {
                    string physicalDestination = Path.Combine(resolved.FullName,
                        Path.GetRelativePath(requiredRoot, destination));
                    if (IsWithin(source, physicalDestination) || IsWithin(physicalDestination, source))
                        issues.Add("The destination mount resolves inside the source folder or its parent.");
                }
            }

            if (issues.Count == 0)
            {
                // Creating and removing only CloudBay's own probe confirms write access
                // without creating the destination before its journal entry exists.
                ProbeWritable(ClosestExistingDirectory(destination));
                progress?.Report(new FolderOperationProgress("Scanning files", 0, 0, 0, 0, source));
                var pending = new Stack<string>();
                pending.Push(source);
                while (pending.Count != 0 && issues.Count < MaximumReportedIssues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    foreach (string item in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FileAttributes attributes = File.GetAttributes(item);
                        string relative = Path.GetRelativePath(source, item);
                        string target = Path.Combine(destination, relative);
                        if (target.Length >= 260)
                            issues.Add($"The destination item exceeds the Windows shell path length limit: {target}");
                        else if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                                issues.Add($"Linked directory cannot be migrated safely: {item}");
                            else if (File.Exists(target))
                                issues.Add($"A file conflicts with the source directory: {target}");
                            else if (Directory.Exists(target) && IsReparseDirectory(target))
                                issues.Add($"Linked destination directory cannot be merged: {target}");
                            else
                                pending.Push(item);
                        }
                        else
                        {
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                                issues.Add($"Linked or cloud placeholder file cannot be migrated safely: {item}");
                            else if (Directory.Exists(target))
                                issues.Add($"A directory conflicts with the source file: {target}");
                            else
                            {
                                using var input = OpenReadable(item);
                                long length = input.Length;
                                if (File.Exists(target))
                                {
                                    using var existing = OpenReadable(target);
                                    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 ||
                                        length != existing.Length ||
                                        !SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(existing)))
                                        issues.Add($"A different file already exists at the destination: {target}");
                                }
                                else
                                {
                                    _ = input.ReadByte();
                                    bytes = checked(bytes + length);
                                }
                                files++;
                                progress?.Report(new FolderOperationProgress("Scanning files", files, 0, 0, bytes, relative));
                            }
                        }
                        if (issues.Count >= MaximumReportedIssues) break;
                    }
                }
                if (issues.Count == 0 && bytes > 0)
                {
                    string capacityPath = ClosestExistingDirectory(destination);
                    if (!TryGetFreeBytes(capacityPath, out ulong free, out string? reason))
                        issues.Add($"Available destination capacity could not be verified: {reason}");
                    else if ((ulong)bytes > free)
                        issues.Add($"The destination has {free:N0} free bytes but needs at least {bytes:N0} bytes.");
                }
                progress?.Report(new FolderOperationProgress("Scanning files", files, files, 0, bytes, null));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException or
                                   NotSupportedException or OverflowException or Win32Exception)
        {
            issues.Add($"The folder tree could not be checked: {ex.Message}");
        }

        if (issues.Count >= MaximumReportedIssues)
            issues.Add("Additional conflicts may exist; resolve the listed issues and check again.");
        return new FolderPreflightResult(issues.Count == 0, false, source, destination, files, bytes, issues);
    }

    internal static async Task<(long Files, long Bytes)> CopyAsync(
        FolderPreflightResult plan,
        IProgress<FolderOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!plan.CanProceed || plan.IsNoOp)
            throw new InvalidOperationException("A successful transfer preflight is required.");
        long copiedFiles = 0;
        long copiedBytes = 0;
        long completedFiles = 0;
        var pending = new Stack<string>();
        pending.Push(plan.SourcePath);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(item);
                string relative = Path.GetRelativePath(plan.SourcePath, item);
                string target = Path.Combine(plan.DestinationPath, relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"A linked directory appeared during migration: {item}");
                    if (File.Exists(target) || (Directory.Exists(target) && IsReparseDirectory(target)))
                        throw new IOException($"The destination directory became unsafe: {target}");
                    Directory.CreateDirectory(target);
                    pending.Push(item);
                }
                else
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"A linked file appeared during migration: {item}");
                    if (File.Exists(target))
                    {
                        if (!await FilesIdenticalAsync(item, target, cancellationToken).ConfigureAwait(false))
                            throw new IOException($"A destination file changed or conflicts: {target}");
                    }
                    else
                    {
                        long copied = await CopyFileAsync(item, target, cancellationToken).ConfigureAwait(false);
                        copiedFiles++;
                        copiedBytes = checked(copiedBytes + copied);
                    }
                    completedFiles++;
                    progress?.Report(new FolderOperationProgress("Copying files", completedFiles, plan.TotalFiles,
                        copiedBytes, plan.TotalBytes, relative));
                }
            }
        }

        await VerifyCoverageAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        return (copiedFiles, copiedBytes);
    }

    internal static bool PathEquals(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    internal static bool IsWithin(string parent, string candidate)
    {
        string normalizedParent = Normalize(parent);
        string normalizedCandidate = Normalize(candidate);
        if (string.Equals(normalizedParent, normalizedCandidate, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = normalizedParent.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedParent : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsReparseDirectory(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static FileStream OpenReadable(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.SequentialScan);

    private static FileStream OpenReadableAsync(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<bool> FilesIdenticalAsync(string left, string right, CancellationToken token)
    {
        if ((File.GetAttributes(left) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(right) & FileAttributes.ReparsePoint) != 0) return false;
        await using var a = OpenReadableAsync(left);
        await using var b = OpenReadableAsync(right);
        if (a.Length != b.Length) return false;
        byte[] leftHash = await SHA256.HashDataAsync(a, token).ConfigureAwait(false);
        byte[] rightHash = await SHA256.HashDataAsync(b, token).ConfigureAwait(false);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static async Task<long> CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"The destination item already exists: {destination}");

        var sourceInfo = new FileInfo(source);
        long originalLength = sourceInfo.Length;
        DateTime originalWriteTime = sourceInfo.LastWriteTimeUtc;
        string temporaryPath = Path.Combine(Path.GetDirectoryName(destination)!, $".cloudbay-{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;
        try
        {
            await using (var input = OpenReadableAsync(source))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            sourceInfo.Refresh();
            if (sourceInfo.Length != originalLength || sourceInfo.LastWriteTimeUtc != originalWriteTime ||
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The source file changed while it was being copied: {source}");
            if (!await FilesIdenticalAsync(source, temporaryPath, cancellationToken).ConfigureAwait(false))
                throw new IOException($"The copied file failed SHA-256 verification: {source}");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"The destination item appeared while copying: {destination}");

            File.Move(temporaryPath, destination);
            temporaryCreated = false;
            try { File.SetLastWriteTimeUtc(destination, originalWriteTime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
            try
            {
                FileAttributes preserved = File.GetAttributes(source) &
                    (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
                File.SetAttributes(destination, preserved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
            return originalLength;
        }
        finally
        {
            if (temporaryCreated && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task VerifyCoverageAsync(
        FolderPreflightResult plan, IProgress<FolderOperationProgress>? progress, CancellationToken token)
    {
        long verified = 0;
        var pending = new Stack<string>();
        pending.Push(plan.SourcePath);
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            foreach (string item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(item);
                string relative = Path.GetRelativePath(plan.SourcePath, item);
                string target = Path.Combine(plan.DestinationPath, relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(target) ||
                        IsReparseDirectory(target))
                        throw new IOException($"The copied directory could not be verified: {item}");
                    pending.Push(item);
                }
                else
                {
                    if (!File.Exists(target) || !await FilesIdenticalAsync(item, target, token).ConfigureAwait(false))
                        throw new IOException($"The copied file failed SHA-256 verification: {item}");
                    verified++;
                    progress?.Report(new FolderOperationProgress("Verifying SHA-256", verified, plan.TotalFiles,
                        plan.TotalBytes, plan.TotalBytes, relative));
                }
            }
        }
        if (verified != plan.TotalFiles)
            throw new IOException("The source changed after preflight; file counts no longer match.");
    }

    private static string ClosestExistingDirectory(string path)
    {
        string? current = path;
        while (current is not null && !Directory.Exists(current)) current = Path.GetDirectoryName(current);
        return current ?? throw new DirectoryNotFoundException($"No accessible parent directory exists for {path}.");
    }

    private static void ValidateDestinationAncestors(string destination, string? requiredRoot, List<string> issues)
    {
        string? current = destination;
        while (current is not null && (requiredRoot is null || !PathEquals(current, requiredRoot)))
        {
            if (File.Exists(current))
                issues.Add($"A file blocks the destination path: {current}");
            if (Directory.Exists(current) && IsReparseDirectory(current))
                issues.Add($"A linked directory is in the destination path: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void ProbeWritable(string directory)
    {
        string probe = Path.Combine(directory, $".cloudbay-probe-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.WriteThrough))
            {
                created = true;
                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            if (created && File.Exists(probe)) File.Delete(probe);
        }
    }

    private static bool TryGetFreeBytes(string directory, out ulong free, out string? reason)
    {
        free = 0;
        if (!OperatingSystem.IsWindows())
        {
            reason = "Windows capacity APIs are unavailable.";
            return false;
        }
        string queryPath = Path.EndsInDirectorySeparator(directory)
            ? directory : directory + Path.DirectorySeparatorChar;
        if (GetDiskFreeSpaceEx(queryPath, out free, out _, out _))
        {
            reason = null;
            return true;
        }
        reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directory, out ulong freeBytesAvailableToCaller,
        out ulong totalBytes, out ulong totalFreeBytes);
}

using CloudBay.Core.Safety;

namespace CloudBay.Application;

/// <summary>Reads mount health and periodically tests that the selected root still accepts writes.</summary>
public sealed class CloudRootProbe
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastPath;
    private DateTimeOffset _lastWriteCheck;
    private bool _lastCanWrite;
    private string _lastWriteMessage = string.Empty;

    public async Task<CloudRootStatus> InspectAsync(string? configuredPath, bool forceWriteCheck = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return new CloudRootStatus(string.Empty, false, false, false, "Not configured", null, null,
                string.Empty, "Choose an existing cloud mount or folder to get started.");

        string path;
        try
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new ArgumentException("The storage root must be an absolute path.");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CloudRootStatus(configuredPath, true, false, false, "Invalid path", null, null,
                string.Empty, exception.Message);
        }

        try
        {
            // Running sync Windows filesystem APIs on the thread pool keeps WinUI responsive.
            var drive = await Task.Run(() => ReadDrive(path), cancellationToken);
            if (!drive.Exists)
                return new CloudRootStatus(path, true, false, false, "Disconnected", drive.Available,
                    drive.Total, drive.FileSystem, "The configured folder is unavailable. Reconnect its mount.");

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (forceWriteCheck || !StringComparer.OrdinalIgnoreCase.Equals(_lastPath, path) ||
                    DateTimeOffset.UtcNow - _lastWriteCheck > TimeSpan.FromMinutes(5))
                {
                    var writeResult = await Task.Run(() => TestWrite(path), cancellationToken);
                    _lastPath = path;
                    _lastWriteCheck = DateTimeOffset.UtcNow;
                    _lastCanWrite = writeResult.Success;
                    _lastWriteMessage = writeResult.Message;
                }

                return new CloudRootStatus(path, true, true, _lastCanWrite,
                    _lastCanWrite ? "Ready" : "Read only", drive.Available, drive.Total,
                    drive.FileSystem, _lastCanWrite
                        ? drive.Available.HasValue
                            ? "The storage root is connected and writable. Space is reported by Windows; cloud provider quota may differ."
                            : "The storage root is connected and writable. Cloud provider capacity is unavailable."
                        : _lastWriteMessage);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new CloudRootStatus(path, true, false, false, "Unavailable", null, null,
                string.Empty, exception.Message);
        }
    }

    private static (bool Exists, long? Available, long? Total, string FileSystem) ReadDrive(string path)
    {
        if (!Directory.Exists(path)) return (false, null, null, string.Empty);
        // Enumerating one entry tests directory read access without scanning an entire cloud tree.
        using (var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
            entries.MoveNext();

        long? available = null;
        long? total = null;
        if (DirectoryCapacityProbe.TryGet(path, out ulong freeBytes, out ulong totalBytes, out _))
        {
            available = freeBytes > long.MaxValue ? null : (long)freeBytes;
            total = totalBytes > long.MaxValue ? null : (long)totalBytes;
        }
        // Virtual providers may not expose volume details. Empty values are preferable
        // to showing the containing local drive's capacity or filesystem by mistake.
        var fileSystem = DirectoryCapacityProbe.GetFileSystemOrEmpty(path);
        return (true, available, total, fileSystem);
    }

    private static (bool Success, string Message) TestWrite(string path)
    {
        var probePath = Path.Combine(path, ".cloudbay-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.WriteThrough))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probePath);
            return (true, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return (false, "The storage root is reachable, but a write test failed: " + exception.Message);
        }
    }
}

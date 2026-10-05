using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CloudBay.Core.Sync;

namespace CloudBay.Core.Transfers;

/// <summary>Explicit local endpoint for the same transfer jobs; never chosen for a cloud location.</summary>
public sealed class LocalTransferEndpoint : ITransferEndpoint
{
    public TransferLocation Location { get; }
    private readonly string _root;
    public LocalTransferEndpoint(TransferLocation location)
    {
        TransferValidation.ValidateLocation(location);
        if (location.Provider != "local" || !Path.IsPathFullyQualified(location.Path)) throw new ArgumentException("An absolute local folder is required.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path));
        if (_root == Path.GetPathRoot(_root)) throw new ArgumentException("Choose a dedicated local folder.");
        Location = location;
        CheckRoot();
    }
    public static TransferLocation ForFolder(string path, string? name = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return new("local", Environment.UserName, Path.GetPathRoot(full)!, full, full, name ?? Path.GetFileName(full));
    }
    private void CheckRoot()
    {
        for (var path = _root; path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException("Linked local transfer folders are not supported.");
    }
    private string FullPath(string relative) { CheckRoot(); return PathRules.FullPath(_root, relative.TrimEnd('/')); }

    public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); CheckRoot();
        var folders = Directory.EnumerateDirectories(_root).Select(path => new DirectoryInfo(path))
            .Where(info => info.LinkTarget is null).OrderBy(info => info.Name, StringComparer.Ordinal)
            .Where(info => cursor is null || string.CompareOrdinal(info.Name, cursor) > 0).Take(201).ToArray();
        return Task.FromResult(new TransferFolderPage(folders.Take(200).Select(info => new TransferFolder(info.FullName, info.Name, info.FullName)).ToArray(),
            folders.Length > 200 ? folders[199].Name : null));
    }

    public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); CheckRoot();
        var saved = cursor is null ? new LocalCursor([""], null) : JsonSerializer.Deserialize<LocalCursor>(cursor)
            ?? throw new InvalidDataException("Invalid local discovery cursor.");
        if (saved.Folders is null || saved.Folders.Count == 0 || saved.Folders.Count > 100_000) throw new InvalidDataException("Invalid local discovery queue.");
        foreach (var folder in saved.Folders.Where(folder => folder.Length > 0)) PathRules.ValidateRelative(folder);
        var current = saved.Folders[0];
        var directory = current.Length == 0 ? _root : FullPath(current);
        // Resume only the current directory; completed subtrees are recorded in the durable cursor.
        // File-system enumeration has no stable provider cursor, so ordering this one directory is necessary.
        var entries = Directory.EnumerateFileSystemEntries(directory).Select(path => new FileInfo(path))
            .Where(info => saved.After is null || string.CompareOrdinal(info.Name, saved.After) > 0)
            .OrderBy(info => info.Name, StringComparer.Ordinal).Take(201).ToArray();
        var items = new List<TransferEntry>();
        var pending = saved.Folders.ToList();
        foreach (var info in entries.Take(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files cannot be transferred as local source files.");
            var relative = current.Length == 0 ? info.Name : current + "/" + info.Name;
            if (PathRules.IsExcluded(relative, Array.Empty<string>())) continue;
            var isFolder = (info.Attributes & FileAttributes.Directory) != 0;
            var size = isFolder ? 0 : info.Length;
            var version = Version(info.FullName, size, isFolder);
            items.Add(new(Identity(info.FullName), relative, version, size, new DateTimeOffset(info.LastWriteTimeUtc), IsFolder: isFolder));
            if (isFolder) pending.Add(relative);
        }
        string? next;
        if (entries.Length > 200) next = JsonSerializer.Serialize(new LocalCursor(pending, entries[199].Name));
        else
        {
            pending.RemoveAt(0);
            next = pending.Count > 0 ? JsonSerializer.Serialize(new LocalCursor(pending, null)) : null;
        }
        return Task.FromResult(new TransferDiscoveryPage(items, next));
    }

    private static string Identity(string path)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).ToLowerInvariant();
    }
    private static string Version(string path, long size, bool directory = false) => size + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
        File.GetCreationTimeUtc(path).Ticks + (directory ? ":folder" : "");

    public ITransferSourceFile OpenSource(TransferEntry entry) => new LocalSource(this, entry);

    public async Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default)
    {
        var final = FullPath(request.RelativePath);
        if (source.Entry.IsFolder)
            return Directory.Exists(final) ? Receipt(request, source.Entry, final, null) : null;
        if (checkpoint?.Data is null || !checkpoint.Data.TryGetValue("committing", out var committing) || committing != "true" || !File.Exists(final)) return null;
        ValidateCheckpoint(request, checkpoint);
        var expected = checkpoint.Data.TryGetValue("sha1", out var hash) ? hash : null;
        if (expected is null) throw new InvalidDataException("The saved local commit has no verified checksum.");
        await using var stream = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (stream.Length != source.Entry.Size || !expected.Equals(Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken)), StringComparison.OrdinalIgnoreCase))
            throw new TransferConflictException("The interrupted local destination commit has changed. Its contents were retained.");
        return Receipt(request, source.Entry, final, expected);
    }

    public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var final = FullPath(request.RelativePath);
        if (source.Entry.IsFolder)
        {
            if (File.Exists(final)) throw new TransferConflictException("A file occupies the selected destination folder.");
            Directory.CreateDirectory(final);
            return Receipt(request, source.Entry, final, null);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        // This destination is explicitly local. Its private partial lives beside the final file
        // so atomic rename is possible; cloud-to-cloud jobs never instantiate this adapter.
        var partial = Path.Combine(Path.GetDirectoryName(final)!, ".CloudBay-transfer-" + request.OperationId + ".part");
        var expectedVersion = File.Exists(final) ? Version(final, new FileInfo(final).Length) : "absent";
        if (checkpoint is not null)
        {
            ValidateCheckpoint(request, checkpoint);
            expectedVersion = checkpoint.Data?.GetValueOrDefault("destinationVersion") ?? throw new InvalidDataException("Missing local destination intent.");
        }
        else if (expectedVersion != "absent")
        {
            if (request.ConflictPolicy == TransferConflictPolicy.Skip) throw new TransferSkippedException("The local destination already exists.");
            if (request.ConflictPolicy != TransferConflictPolicy.Replace) throw new TransferConflictException("The local destination already exists.");
        }
        long offset = 0;
        var hashes = new List<DownloadChunk>();
        if (checkpoint?.Data?.TryGetValue("chunks", out var savedChunks) == true)
            hashes = JsonSerializer.Deserialize<List<DownloadChunk>>(savedChunks) ?? throw new InvalidDataException("Invalid local range receipts.");
        await using var destination = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true);
        foreach (var chunk in hashes)
        {
            if (chunk.Offset != offset || chunk.Length <= 0 || chunk.Length > 4 * 1024 * 1024 || offset + chunk.Length > source.Entry.Size || destination.Length < offset + chunk.Length)
                throw new InvalidDataException("Invalid local destination range checkpoint.");
            destination.Position = offset;
            var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.Length);
            try
            {
                await destination.ReadExactlyAsync(buffer.AsMemory(0, (int)chunk.Length), cancellationToken);
                if (!chunk.Sha1.Equals(Convert.ToHexString(SHA1.HashData(buffer.AsSpan(0, (int)chunk.Length))), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A saved local destination range failed verification. The partial was retained.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            offset += chunk.Length;
        }
        if (offset != (checkpoint?.AcknowledgedBytes ?? 0)) throw new InvalidDataException("The saved local byte counter does not match its range receipts.");
        destination.SetLength(offset);
        var data = new Dictionary<string, string> { ["destinationVersion"] = expectedVersion, ["relativePath"] = request.RelativePath,
            ["operation"] = request.OperationId, ["chunks"] = JsonSerializer.Serialize(hashes) };
        await saveCheckpoint(new("local", request.OperationId, offset, data), cancellationToken);
        progress?.Report(new(offset, source.Entry.Size) { IsBaseline = true });
        var bytes = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            while (offset < source.Entry.Size)
            {
                var length = Math.Min(4 * 1024 * 1024, source.Entry.Size - offset);
                await using var input = await source.OpenReadAsync(offset, length, cancellationToken);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                long written = 0;
                while (written < length)
                {
                    var read = await input.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, length - written)), cancellationToken);
                    if (read == 0) throw new EndOfStreamException("The source range ended early.");
                    hash.AppendData(bytes, 0, read);
                    await destination.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
                    written += read;
                    progress?.Report(new(offset + written, source.Entry.Size));
                }
                await destination.FlushAsync(cancellationToken); destination.Flush(true);
                hashes.Add(new(offset, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                offset += length;
                data["chunks"] = JsonSerializer.Serialize(hashes);
                await saveCheckpoint(new("local", request.OperationId, offset, new Dictionary<string,string>(data)), cancellationToken);
            }
            destination.Position = 0;
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(destination, cancellationToken)).ToLowerInvariant();
            if (source.Entry.Sha1 is not null && !source.Entry.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The local copy failed the source checksum.");
            await source.ValidateAsync(cancellationToken);
            data["committing"] = "true"; data["sha1"] = sha1;
            await saveCheckpoint(new("local", request.OperationId, offset, new Dictionary<string,string>(data)), cancellationToken);
            await destination.DisposeAsync();
            var current = File.Exists(final) ? Version(final, new FileInfo(final).Length) : "absent";
            if (current != expectedVersion) throw new TransferConflictException("The local destination changed during the transfer. Its contents were retained.");
            if (expectedVersion == "absent") File.Move(partial, final, overwrite: false);
            else File.Replace(partial, final, null);
            File.SetLastWriteTimeUtc(final, source.Entry.ModifiedUtc.UtcDateTime);
            return Receipt(request, source.Entry, final, sha1);
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    private void ValidateCheckpoint(TransferUploadRequest request, TransferCheckpoint checkpoint)
    {
        if (checkpoint.Provider != "local" || checkpoint.SessionId != request.OperationId || checkpoint.Data is null ||
            checkpoint.Data.GetValueOrDefault("relativePath") != request.RelativePath || checkpoint.Data.GetValueOrDefault("operation") != request.OperationId)
            throw new InvalidDataException("The local checkpoint belongs to a different destination.");
    }
    private static TransferReceipt Receipt(TransferUploadRequest request, TransferEntry entry, string path, string? sha1) =>
        new(Identity(path), request.RelativePath, Version(path, entry.Size, entry.IsFolder), entry.Size, sha1, request.OperationId);

    public async Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
    {
        var path = FullPath(receipt.RelativePath);
        if (source.Entry.IsFolder)
        {
            if (!Directory.Exists(path)) throw new InvalidDataException("The copied local folder is missing.");
            return;
        }
        await using var output = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (output.Length != receipt.Size || Identity(path) != receipt.Id || Version(path, output.Length) != receipt.Version)
            throw new InvalidDataException("The local destination changed after upload.");
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(output, cancellationToken));
        if (receipt.Sha1 is null || !actual.Equals(receipt.Sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The local destination checksum failed.");
        if (source.Entry.Sha1 is not null) return;
        await using var input = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
        var expected = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The local destination content differs from the source.");
    }

    public async Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default)
    {
        var source = OpenSource(entry);
        await source.ValidateAsync(cancellationToken);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Exact local Move deletion requires the Windows file-handle API.");
        // Deny writers and renames, verify the contents, then mark this exact handle for
        // deletion. A failed validation never arms DeleteOnClose on a replacement file.
        var path = FullPath(entry.RelativePath);
        using var handle = CreateFile(path, 0x80000000u | 0x00010000u, 0, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        await using var held = new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: true);
        if (held.Length != entry.Size || Version(path, held.Length) != entry.Version)
            throw new TransferSourceChangedException("The local source changed before Move deletion.");
        if (!GetFileInformationByHandle(handle, out var identity) || entry.Id != $"{identity.VolumeSerialNumber:x8}:{identity.FileIndexHigh:x8}{identity.FileIndexLow:x8}")
            throw new TransferSourceChangedException("The local source was replaced before Move deletion.");
        if (entry.Sha1 is null || !entry.Sha1.Equals(Convert.ToHexString(await SHA1.HashDataAsync(held, cancellationToken)), StringComparison.OrdinalIgnoreCase))
            throw new TransferSourceChangedException("The local source content changed before Move deletion.");
        cancellationToken.ThrowIfCancellationRequested();
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public Task<bool> IsSourceDeletedAsync(TransferEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Local path identity is not an immutable file ID. A replacement at the same path
        // must be handled as changed, even if an earlier exact file was already deleted.
        return Task.FromResult(!File.Exists(FullPath(entry.RelativePath)));
    }

    private sealed class LocalSource(LocalTransferEndpoint owner, TransferEntry entry) : ITransferSourceFile
    {
        public TransferEntry Entry { get; } = entry;
        public Task ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = owner.FullPath(Entry.RelativePath);
            if (Entry.IsFolder ? !Directory.Exists(path) : !File.Exists(path)) throw new TransferSourceChangedException("The local source no longer exists.");
            var size = Entry.IsFolder ? 0 : new FileInfo(path).Length;
            if (Identity(path) != Entry.Id || Version(path, size, Entry.IsFolder) != Entry.Version)
                throw new TransferSourceChangedException("The local source changed since it was discovered.");
            return Task.CompletedTask;
        }
        public async Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            if (offset < 0 || length < 0 || offset > Entry.Size || length > Entry.Size - offset) throw new ArgumentOutOfRangeException(nameof(offset));
            await ValidateAsync(cancellationToken);
            if (Entry.IsFolder) return Stream.Null;
            var file = new FileStream(owner.FullPath(Entry.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            try { await ValidateAsync(cancellationToken); file.Position = offset; return new LimitedReadStream(file, length); }
            catch { await file.DisposeAsync(); throw; }
        }
    }
    private sealed class LimitedReadStream(Stream input, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return 0;
            var read = await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            if (read == 0) throw new EndOfStreamException(); _remaining -= read; return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset,count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => input.DisposeAsync();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _length; public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    }
    private sealed record LocalCursor(IReadOnlyList<string> Folders, string? After);
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
}

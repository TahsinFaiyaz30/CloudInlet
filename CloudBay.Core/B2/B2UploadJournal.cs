using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudBay.Core.B2;

// The journal stores identities and checksums, never keys, authorization tokens, or file contents.
// Confirmed B2 parts are the authoritative checkpoint, so no disk flush is needed after every part.
internal sealed class B2UploadJournal
{
    internal sealed record Entry(int Version, string AccountId, string BucketId, string Key, string SourcePath,
        long SourceOffset, long Length, long ModifiedTicks, long PartSize, string Sha1, string[] PartHashes,
        string UploadId, string? FileId, DateTimeOffset CreatedUtc);

    private readonly string _directory;
    public B2UploadJournal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The upload checkpoint directory must be absolute.");
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        CheckDirectory();
        Directory.CreateDirectory(_directory);
        CheckDirectory();
    }

    public string PathFor(string accountId, string bucketId, string key, string sourcePath, long sourceOffset)
    {
        var identity = JsonSerializer.Serialize(new[] { accountId, bucketId, key, sourcePath.ToUpperInvariant(), sourceOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(_directory, name + ".json");
    }

    public IEnumerable<string> Paths()
    {
        CheckDirectory();
        return Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileNameWithoutExtension(p) is { Length: 64 } name && name.All(Uri.IsHexDigit));
    }

    // A share-denying lock protects the same checkpoint even if two client processes try to resume it.
    public async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryLock(path) is { } held) return held;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    // Background maintenance must not wait behind an active multipart transfer.
    public FileStream? TryLock(string path)
    {
        CheckPath(path); CheckPath(path + ".lock");
        try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }

    public DateTimeOffset LastActivityUtc(string path)
    {
        CheckPath(path);
        return File.GetLastWriteTimeUtc(path);
    }

    public Entry? Read(string path)
    {
        CheckPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1_000_000) throw InvalidJournal();
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path)); }
        catch (JsonException) { throw InvalidJournal(); }
        if (entry is null || entry.Version != 1 || string.IsNullOrWhiteSpace(entry.AccountId) ||
            string.IsNullOrWhiteSpace(entry.BucketId) || string.IsNullOrWhiteSpace(entry.Key) ||
            string.IsNullOrWhiteSpace(entry.SourcePath) || !Path.IsPathFullyQualified(entry.SourcePath) ||
            entry.SourceOffset < 0 || entry.Length <= 0 || entry.PartSize < 5_000_000 || entry.PartSize > 5_000_000_000 ||
            entry.ModifiedTicks < 0 || entry.ModifiedTicks > DateTimeOffset.MaxValue.Ticks ||
            entry.Length > 10_000_000_000_000 || !Sha1(entry.Sha1) || entry.PartHashes is null ||
            entry.PartHashes.Length is < 2 or > 10_000 || entry.PartHashes.Length != (entry.Length + entry.PartSize - 1) / entry.PartSize ||
            entry.PartHashes.Any(h => !Sha1(h)) || !Guid.TryParseExact(entry.UploadId, "N", out _) ||
            entry.FileId is { Length: 0 } ||
            !string.Equals(path, PathFor(entry.AccountId, entry.BucketId, entry.Key, entry.SourcePath, entry.SourceOffset), StringComparison.OrdinalIgnoreCase))
            throw InvalidJournal();
        return entry;
    }

    public void Write(string path, Entry entry)
    {
        CheckPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024))
            {
                JsonSerializer.Serialize(file, entry);
                file.Flush(flushToDisk: true);
            }
            CheckPath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Remove(string path) { CheckPath(path); File.Delete(path); }

    private void CheckPath(string path)
    {
        CheckDirectory();
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Upload checkpoints cannot follow linked paths.");
    }

    private void CheckDirectory()
    {
        for (DirectoryInfo? directory = new(_directory); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Upload checkpoints cannot follow linked directories.");
    }

    private static bool Sha1(string? hash) => hash is { Length: 40 } && hash.All(Uri.IsHexDigit);
    private static InvalidDataException InvalidJournal() => new("An upload checkpoint is invalid. It was preserved so its unfinished B2 upload can be recovered safely.");
}

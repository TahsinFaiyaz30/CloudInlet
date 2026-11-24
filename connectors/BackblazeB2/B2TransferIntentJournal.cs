using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudInlet.Core.B2;

// Creating a B2 version has no idempotency key. Persist the identity before the
// request; an uncertain outcome is resolved by its metadata receipt, never replayed.
internal sealed class B2TransferIntentJournal
{
    internal sealed record Entry(int Version, string Kind, string AccountId, string BucketId, string Key,
        string OperationId, string SourceId, long Length, string Sha1, long ModifiedMillis,
        long PartSize = 0, string? FileId = null, bool RequestPending = false);

    private readonly string _directory;
    public B2TransferIntentJournal(string directory)
    {
        _directory = Path.GetFullPath(directory);
        CheckDirectory(); Directory.CreateDirectory(_directory); CheckDirectory();
    }
    public string PathFor(string accountId, string operationId) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "|" + operationId))).ToLowerInvariant() + ".json");
    public IEnumerable<string> Paths() { CheckDirectory(); return Directory.EnumerateFiles(_directory, "*.json"); }
    public async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); CheckPath(path); CheckPath(path + ".lock");
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }
    public Entry? Read(string path)
    {
        CheckPath(path); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 32_768) throw Invalid();
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path)); }
        catch (JsonException) { throw Invalid(); }
        if (entry is null || entry.Version != 1 || entry.Kind is not ("copy" or "upload") ||
            string.IsNullOrWhiteSpace(entry.AccountId) || string.IsNullOrWhiteSpace(entry.BucketId) ||
            string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.SourceId) ||
            entry.OperationId is not { Length: 64 } || !entry.OperationId.All(Uri.IsHexDigit) ||
            entry.Length is < 0 or > 10_000_000_000_000 || entry.Sha1 is not { Length: 40 } || !entry.Sha1.All(Uri.IsHexDigit) ||
            entry.PartSize != 0 && entry.PartSize is < 5_000_000 or > 5_000_000_000 ||
            entry.ModifiedMillis < -62_135_596_800_000 || entry.ModifiedMillis > 253_402_300_799_999 ||
            entry.FileId is { Length: 0 } ||
            !path.Equals(PathFor(entry.AccountId, entry.OperationId), StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return entry;
    }
    public void Write(string path, Entry entry)
    {
        CheckPath(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, entry); output.Flush(true); }
            CheckPath(path); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Remove(string path) { CheckPath(path); File.Delete(path); }
    private void CheckPath(string path)
    {
        CheckDirectory();
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Transfer intents cannot follow linked paths.");
    }
    private void CheckDirectory()
    {
        for (DirectoryInfo? directory = new(_directory); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Transfer intents cannot follow linked directories.");
    }
    private static InvalidDataException Invalid() => new("A transfer intent is invalid. It was retained to prevent creating a duplicate cloud version.");
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudBay.Core.Sync;

/// <summary>Private, version-bound staging. Incomplete bytes never replace a user's file.</summary>
internal sealed class DownloadStaging : IAsyncDisposable
{
    private sealed record Entry(int Version, string FileId, string Key, long Size, string? Sha1, List<DownloadChunk> Chunks);
    private readonly string _journalPath;
    private readonly FileStream _lock;
    private readonly SemaphoreSlim _checkpointGate = new(1);
    private readonly Entry _entry;
    public string Path { get; }
    public FileStream Stream { get; }
    public IReadOnlyList<DownloadChunk> Chunks => _entry.Chunks.ToArray();
    private DownloadStaging(string path, string journal, FileStream held, FileStream stream, Entry entry)
    { Path = path; _journalPath = journal; _lock = held; Stream = stream; _entry = entry; }

    public static void CleanupAbandoned(string root, CancellationToken ct)
    {
        var folder = PathRules.FullPath(root, ".cloudbay/transfers");
        if (!Directory.Exists(folder)) return;
        CheckPath(System.IO.Path.Combine(folder, "checkpoint-check"));
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var path in Directory.EnumerateFiles(folder, "*.part", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length != 64 || !name.All(Uri.IsHexDigit)) continue;
            var journal = path + ".json";
            var lockPath = path + ".lock";
            CheckPath(path); CheckPath(journal); CheckPath(lockPath);
            if (File.GetLastWriteTimeUtc(path) >= cutoff || File.Exists(journal) && File.GetLastWriteTimeUtc(journal) >= cutoff) continue;
            try
            {
                using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                CheckPath(path); CheckPath(journal);
                File.Delete(path); File.Delete(journal);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { continue; }
            try { File.Delete(lockPath); } catch (IOException) { }
        }
    }

    public static async Task<DownloadStaging> OpenAsync(string root, string relative, CloudObject file, CancellationToken ct)
    {
        var folder = PathRules.FullPath(root, ".cloudbay/transfers");
        CheckPath(System.IO.Path.Combine(folder, "checkpoint-check"));
        Directory.CreateDirectory(folder);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { relative.ToUpperInvariant(), file.FileId, file.Key, file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Sha1 ?? "" }))));
        var path = System.IO.Path.Combine(folder, identity + ".part");
        var journal = path + ".json";
        var lockPath = path + ".lock";
        FileStream held;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            CheckPath(lockPath);
            try { held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); break; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, ct); }
        }
        FileStream? opened = null;
        try
        {
            CheckPath(path); CheckPath(journal);
            var entry = new Entry(1, file.FileId, file.Key, file.Size, file.Sha1, []);
            var reset = false;
            if (File.Exists(journal))
            {
                Entry? saved = null;
                if (new FileInfo(journal).Length <= 2_000_000)
                {
                    try { saved = JsonSerializer.Deserialize<Entry>(await File.ReadAllBytesAsync(journal, ct)); }
                    catch (JsonException) { /* A damaged private checkpoint must not block a fresh download. */ }
                }
                reset = saved is null || saved.Version != 1 || saved.FileId != file.FileId || saved.Key != file.Key || saved.Size != file.Size ||
                    saved.Sha1 != file.Sha1 || saved.Chunks is null || saved.Chunks.Count > 10_000 ||
                    saved.Chunks.Any(chunk => chunk is null || chunk.Offset < 0 || chunk.Length <= 0 || chunk.Offset > file.Size ||
                        chunk.Length > file.Size - chunk.Offset || chunk.Offset % B2.B2CloudStore.GetDownloadChunkSize(file.Size) != 0 ||
                        chunk.Length != Math.Min(B2.B2CloudStore.GetDownloadChunkSize(file.Size), file.Size - chunk.Offset) ||
                        chunk.Sha1 is not { Length: 40 } || !chunk.Sha1.All(Uri.IsHexDigit)) ||
                    saved.Chunks.Select(chunk => chunk.Offset).Distinct().Count() != saved.Chunks.Count;
                if (!reset) entry = saved!;
            }
            var stream = opened = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (reset || stream.Length > file.Size)
            {
                // Both names are derived from this immutable version, under the private staging
                // directory and its exclusive lock. Never touch the user's destination here.
                CheckPath(path); CheckPath(journal);
                stream.SetLength(0);
                File.Delete(journal);
                entry = new Entry(1, file.FileId, file.Key, file.Size, file.Sha1, []);
            }
            var result = new DownloadStaging(path, journal, held, stream, entry);
            return result;
        }
        catch { if (opened is not null) await opened.DisposeAsync(); await held.DisposeAsync(); throw; }
    }

    public async Task CheckpointAsync(DownloadChunk chunk, CancellationToken ct)
    {
        await _checkpointGate.WaitAsync(ct);
        try
        {
            _entry.Chunks.RemoveAll(previous => previous.Offset == chunk.Offset);
            _entry.Chunks.Add(chunk);
            await WriteCheckpointAsync(ct);
        }
        finally { _checkpointGate.Release(); }
    }

    private async Task WriteCheckpointAsync(CancellationToken ct)
    {
        CheckPath(_journalPath);
        var temporary = _journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true))
            { await JsonSerializer.SerializeAsync(stream, _entry, cancellationToken: ct); await stream.FlushAsync(ct); stream.Flush(true); }
            CheckPath(_journalPath);
            File.Move(temporary, _journalPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void ForgetCheckpoint() { CheckPath(_journalPath); File.Delete(_journalPath); }
    public void DiscardCorruptBytes()
    {
        CheckPath(Path); CheckPath(_journalPath);
        Stream.SetLength(0);
        _entry.Chunks.Clear();
        File.Delete(_journalPath);
    }

    private static void CheckPath(string path)
    {
        for (var parent = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.LinkTarget is not null) throw new IOException("Download checkpoints cannot follow linked directories.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Download checkpoints cannot follow linked files.");
    }
    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        await _lock.DisposeAsync();
        var lockPath = Path + ".lock";
        try { CheckPath(lockPath); File.Delete(lockPath); }
        catch (IOException) { /* Another process may have acquired the lock since its handle closed. */ }
        _checkpointGate.Dispose();
    }
}

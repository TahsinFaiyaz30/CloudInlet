using System.Security.Cryptography;

namespace CloudBay.Core.Sync;

/// <summary>Verified, restartable folder copy which preserves colliding destination content.</summary>
public static class VerifiedTreeCopy
{
    public static async Task CopyAsync(string source, string destination, CancellationToken ct = default)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Backup folders must be separate, with neither containing the other.");
        ValidateDirectoryPath(source);
        ValidateDirectoryPath(destination);
        var snapshot = Snapshot(source, ct);
        // Inspect all affected destination paths before the first write. In particular, an existing
        // junction below the destination must not redirect a verified copy into unrelated data.
        foreach (var directory in snapshot.Directories)
        {
            ct.ThrowIfCancellationRequested();
            ValidateDirectoryPath(Path.Combine(destination, directory));
        }
        foreach (var relative in snapshot.Files.Keys)
        {
            ct.ThrowIfCancellationRequested();
            ValidateFilePath(Path.Combine(destination, relative));
        }
        CreateDirectory(destination);
        foreach (var directory in snapshot.Directories)
            CreateDirectory(Path.Combine(destination, directory));
        foreach (var (relative, fingerprint) in snapshot.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(source, relative);
            var target = Path.Combine(destination, relative);
            CreateDirectory(Path.GetDirectoryName(target)!);
            ValidateFilePath(path);
            ValidateFilePath(target);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (Fingerprint.Read(path) != fingerprint) throw new IOException("A source file changed during backup. Close apps using this folder and try again.");
            var sourceHash = await SHA256.HashDataAsync(input, ct);
            input.Position = 0;
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".cloudbay-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                ValidateFilePath(temporary);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true))
                {
                    await input.CopyToAsync(output, ct);
                    await output.FlushAsync(ct); output.Flush(true); output.Position = 0;
                    var copiedHash = await SHA256.HashDataAsync(output, ct);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash)) throw new IOException("Backup checksum verification failed.");
                }
                ValidateFilePath(temporary);
                File.SetLastWriteTimeUtc(temporary, fingerprint.ModifiedUtc);
                ValidateFilePath(target);
                if (File.Exists(target))
                {
                    byte[] destinationHash;
                    await using (var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                        destinationHash = await SHA256.HashDataAsync(existing, ct);
                    if (CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash)) continue;
                    var preserved = Path.Combine(Path.GetDirectoryName(target)!,
                        $"{Path.GetFileNameWithoutExtension(target)} (backup conflict {Guid.NewGuid().ToString("N")[..8]}){Path.GetExtension(target)}");
                    // Atomic move preserves even a late edit to the destination, then installs the verified source.
                    ValidateFilePath(target);
                    ValidateFilePath(preserved);
                    File.Move(target, preserved);
                }
                ValidateFilePath(temporary);
                ValidateFilePath(target);
                File.Move(temporary, target, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) { ValidateFilePath(temporary); File.Delete(temporary); }
            }
        }
        // Refuse Windows redirection if any file appeared, disappeared, or changed during the copy.
        var final = Snapshot(source, ct);
        if (!snapshot.Directories.SetEquals(final.Directories) || snapshot.Files.Count != final.Files.Count ||
            snapshot.Files.Any(pair => !final.Files.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new IOException("The source folder changed during backup. Original files were retained; close apps using this folder and try again.");
    }

    public static string GetFingerprint(string path, CancellationToken ct = default)
    {
        var snapshot = Snapshot(path, ct);
        var text = string.Join('\n', snapshot.Directories.Order(StringComparer.OrdinalIgnoreCase).Select(d => "D:" + d)
            .Concat(snapshot.Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"F:{p.Key}:{p.Value.Size}:{p.Value.ModifiedUtc.Ticks}")));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static TreeSnapshot Snapshot(string source, CancellationToken ct)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            ValidateDirectoryPath(current);
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                directories.Add(Path.GetRelativePath(source, directory)); pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                ValidateFilePath(file);
                files.Add(Path.GetRelativePath(source, file), Fingerprint.Read(file));
            }
        }
        return new(files, directories);
    }

    private static void ValidateDirectoryPath(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            // Cloud Files directories can be reparse points without being links. Retain them;
            // reject actual junctions/symbolic links, including ancestors of a not-yet-created path.
            if (directory.LinkTarget is not null) throw new IOException("Backup cannot follow linked directories.");
    }

    private static void ValidateFilePath(string path)
    {
        ValidateDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Backup cannot follow file links.");
    }

    private static void CreateDirectory(string path)
    {
        ValidateDirectoryPath(path);
        Directory.CreateDirectory(path);
        ValidateDirectoryPath(path);
    }
    private sealed record TreeSnapshot(Dictionary<string, Fingerprint> Files, HashSet<string> Directories);
    private sealed record Fingerprint(long Size, DateTime ModifiedUtc)
    { public static Fingerprint Read(string path) { var file = new FileInfo(path); return new(file.Length, file.LastWriteTimeUtc); } }
}

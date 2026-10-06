using System.Security.Cryptography;
using System.Text.Json;

namespace CloudInlet.Core.Updates;

internal sealed class UpdateCache
{
    // This durable ownership marker also protects packages downloaded before
    // the product rename. Changing it would strand recoverable update state.
    private const string OwnerText = "CloudBay update cache v1\n";
    private readonly string _root;
    public UpdateCache(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(_root) == _root) throw new IOException("An update cache cannot be a drive root.");
        CheckDirectory();
        Directory.CreateDirectory(_root);
        CheckDirectory();
        var owner = PathFor(".cloudbay-update-cache-v1");
        if (!File.Exists(owner))
        {
            if (Directory.EnumerateFileSystemEntries(_root).Any())
                throw new IOException("The update cache must be a dedicated, empty directory.");
            using var output = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(output);
            writer.Write(OwnerText);
        }
        if (new FileInfo(owner).Length != OwnerText.Length || File.ReadAllText(owner) != OwnerText)
            throw new IOException("The update cache ownership marker is invalid.");
    }

    public FileStream AcquireLock() => new(PathFor("updater.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    public FileStream AcquireInstallationLock()
    {
        try
        {
            return new FileStream(PathFor("update-install.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new InvalidOperationException("An update installation is in progress. Wait for it to finish before changing downloaded files.", error);
        }
    }
    public string PackagePath(UpdateCandidate candidate) => PathFor("pending-" + candidate.Asset.FileName);
    public string PartialPath(UpdateCandidate candidate) => PathFor("partial-" + candidate.Asset.FileName);

    public T? Read<T>(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("The update cache metadata is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), UpdateManifestRules.JsonOptions);
    }

    public void Write<T>(string name, T value)
    {
        var path = PathFor(name);
        var temporary = PathFor(name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, value, UpdateManifestRules.JsonOptions); output.Flush(true); }
            CheckPath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { DeletePath(temporary); }
    }

    public void RemovePackagesExcept(string? retained)
    {
        CheckDirectory();
        // Only the reserved installer names are ours; never recursively delete cache directories
        // or arbitrary paths named in a tampered metadata file.
        foreach (var path in Directory.EnumerateFiles(_root))
        {
            var name = Path.GetFileName(path);
            if ((name.StartsWith("pending-CloudBay-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("partial-CloudBay-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("pending-CloudInlet-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("partial-CloudInlet-", StringComparison.OrdinalIgnoreCase)) &&
                !path.Equals(retained, StringComparison.OrdinalIgnoreCase)) DeletePath(path);
        }
    }

    public FileStream CreatePartial(UpdateCandidate candidate) => new(PartialPath(candidate), FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    public void Promote(UpdateCandidate candidate)
    {
        var source = PartialPath(candidate); var target = PackagePath(candidate);
        CheckPath(source); CheckPath(target); File.Move(source, target, overwrite: false);
    }
    public void DeletePartial(UpdateCandidate candidate) => DeletePath(PartialPath(candidate));

    public async Task<bool> VerifyAsync(UpdateCandidate candidate, CancellationToken token)
    {
        var path = PackagePath(candidate);
        if (!File.Exists(path)) return false;
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != candidate.Asset.Size) return false;
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(candidate.Asset.Sha256));
    }

    private string PathFor(string name)
    {
        if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new IOException("The update cache path is invalid.");
        var path = Path.Combine(_root, name); CheckPath(path); return path;
    }
    private void DeletePath(string path) { CheckPath(path); if (File.Exists(path)) File.Delete(path); }
    private void CheckPath(string path)
    {
        CheckDirectory();
        var attributes = ReadAttributes(path);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _root, StringComparison.OrdinalIgnoreCase) ||
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Update files cannot follow linked or unexpected paths.");
    }
    private void CheckDirectory()
    {
        for (DirectoryInfo? directory = new(_root); directory is not null; directory = directory.Parent)
            if ((ReadAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Updates cannot follow linked cache directories.");
    }
    private static FileAttributes ReadAttributes(string path)
    {
        // File.Exists follows a link and returns false for a missing target. GetAttributes
        // inspects the directory entry itself, so dangling links cannot bypass validation.
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }
}

using System.IO.Enumeration;

namespace CloudBay.Core.Sync;

public static class PathRules
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static string NormalizePrefix(string prefix)
    {
        prefix = prefix.Replace('\\', '/').Trim('/');
        if (prefix.Length > 0) ValidateRelative(prefix);
        return prefix.Length == 0 ? "" : prefix + "/";
    }

    public static string ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new InvalidDataException("The cloud file name is not a safe Windows relative path.");
        foreach (var part in relative.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl) ||
                Reserved.Contains(part.Split('.')[0]))
                throw new InvalidDataException($"The cloud file name contains an unsupported Windows path component: {part}");
        }
        return relative;
    }

    public static string FromKey(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("The file is outside this client's cloud prefix.");
        return ValidateRelative(key[prefix.Length..]);
    }

    public static string FullPath(string root, string relative)
    {
        ValidateRelative(relative);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The path escapes the sync folder.");
        var current = Path.GetDirectoryName(full);
        while (current is not null && !current.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("Linked directories inside the sync folder cannot be synchronized.");
            current = Path.GetDirectoryName(current);
        }
        return full;
    }

    public static bool IsExcluded(string relative, IEnumerable<string> patterns)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Equals(".cloudbay", StringComparison.OrdinalIgnoreCase) || p.Equals(".cloudbay-backups", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(".cloudbay-copy-", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))) return true;
        return patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Any(pattern =>
            pattern.Contains('/')
                ? FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true)
                : parts.Any(p => FileSystemName.MatchesSimpleExpression(pattern, p, ignoreCase: true)));
    }

    public static void ValidateSettings(AppSettings settings)
    {
        if (settings.KeyId is null || settings.BucketId is null || settings.BucketName is null || settings.AccountId is null ||
            settings.RootPath is null || settings.Prefix is null || settings.Theme is null ||
            settings.Backups is null || settings.CustomBackups is null || settings.Exclusions is null ||
            settings.Backups.Any(folder => folder is null || string.IsNullOrWhiteSpace(folder.Name) || string.IsNullOrWhiteSpace(folder.OriginalPath) || string.IsNullOrWhiteSpace(folder.DestinationPath)) ||
            settings.CustomBackups.Any(folder => folder is null || string.IsNullOrWhiteSpace(folder.Name) || string.IsNullOrWhiteSpace(folder.SourcePath) || folder.Prefix is null) ||
            settings.Exclusions.Any(pattern => pattern is null))
            throw new InvalidDataException("CloudBay settings contain missing account, folder, or exclusion fields. The original settings were preserved.");
        if (settings.Theme is not ("System" or "Light" or "Dark")) throw new InvalidDataException("The saved Windows theme is invalid.");
        if (settings.UploadConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(settings.UploadConcurrency));
        if (settings.UploadBytesPerSecond < 0 || settings.DownloadBytesPerSecond < 0) throw new ArgumentOutOfRangeException("Transfer limits must be nonnegative.");
        if (settings.PollSeconds is < 15 or > 3600) throw new ArgumentOutOfRangeException(nameof(settings.PollSeconds));
        if (NormalizePrefix(settings.Prefix) != settings.Prefix) throw new InvalidDataException("The saved cloud prefix is not normalized.");
        var root = Path.GetFullPath(settings.RootPath);
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a dedicated sync folder, not a volume root.");
        if (settings.Exclusions.Any(p => p.Contains("..") || p.Contains(':'))) throw new InvalidDataException("Exclusions must be relative names or wildcard patterns.");
        foreach (var folder in settings.Backups)
        {
            if (!FullPath(root, folder.Name).Equals(Path.GetFullPath(folder.DestinationPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A saved system folder destination does not match this sync root.");
            Path.GetFullPath(folder.OriginalPath);
        }
        foreach (var folder in settings.CustomBackups)
        {
            if (ValidateRelative(folder.Name).Contains('/') || folder.Name.Length > 64 || folder.Name.StartsWith(".cloudbay", StringComparison.OrdinalIgnoreCase) ||
                folder.Prefix != settings.Prefix + ".cloudbay-backups/" + folder.Name + "/")
                throw new InvalidDataException("A saved custom backup name or cloud prefix is invalid.");
            Path.GetFullPath(folder.SourcePath);
        }
        if (settings.Backups.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Backups.Count ||
            settings.CustomBackups.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.CustomBackups.Count)
            throw new InvalidDataException("Saved backup names must be unique.");
    }
}

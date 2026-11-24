using System.IO.Enumeration;

namespace CloudInlet.Core.Sync;

public static class ConnectorPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
      "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };

    /// <summary>Retains a unique conflict suffix without exceeding NTFS's component limit.</summary>
    public static string ConflictFileName(string originalFileName, string suffix)
    {
        ValidateRelative(originalFileName);
        if (originalFileName.Contains('/') || string.IsNullOrWhiteSpace(suffix) || suffix.Length > 253)
            throw new ArgumentException("A conflict name needs one file name and a bounded unique suffix.");
        ValidateRelative("x" + suffix);
        if (suffix.Contains('/')) throw new ArgumentException("A conflict suffix cannot contain directory separators.");
        var extension = Path.GetExtension(originalFileName);
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var available = 255 - suffix.Length;
        // An unusually long extension must leave at least one character for the stem.
        extension = TruncateComponent(extension, Math.Min(extension.Length, available - 1));
        stem = TruncateComponent(stem, available - extension.Length);
        if (stem.Length == 0) stem = "x";
        var result = stem + suffix + extension;
        ValidateRelative(result);
        return result;
    }

    private static string TruncateComponent(string value, int maximum)
    {
        var length = Math.Min(value.Length, maximum);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }

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
            p.Equals(".cloudinlet", StringComparison.OrdinalIgnoreCase) || p.Equals(".cloudinlet-backups", StringComparison.OrdinalIgnoreCase) ||
            (p.StartsWith(".cloudbay-copy-", StringComparison.OrdinalIgnoreCase) || p.StartsWith(".cloudinlet-copy-", StringComparison.OrdinalIgnoreCase)) && p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            IsOwnedTransferArtifact(p))) return true;
        return patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Any(pattern =>
            pattern.Contains('/')
                ? FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true)
                : parts.Any(p => FileSystemName.MatchesSimpleExpression(pattern, p, ignoreCase: true)));
    }

    private static bool IsOwnedTransferArtifact(string name)
    {
        var prefix = name.StartsWith(".cloudbay-transfer-", StringComparison.OrdinalIgnoreCase) ? ".cloudbay-transfer-" : ".cloudinlet-transfer-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? ".part" :
            name.EndsWith(".original", StringComparison.OrdinalIgnoreCase) ? ".original" : null;
        if (suffix is null || name.Length != prefix.Length + 64 + suffix.Length) return false;
        foreach (var character in name.AsSpan(prefix.Length, 64)) if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

}

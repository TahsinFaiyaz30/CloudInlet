namespace CloudInlet.Core.Sync;

/// <summary>Uses the extended Win32 namespace for already validated, absolute file paths.</summary>
public static class WindowsFilePaths
{
    public static string ToExtendedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A native file path must be absolute.", nameof(path));
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("A device path is not a file-system path.", nameof(path));
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var suffix = full[4..];
            if (suffix.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ||
                suffix.Length >= 3 && char.IsAsciiLetter(suffix[0]) && suffix[1] == ':' && suffix[2] == '\\') return full;
            throw new ArgumentException("Only drive and network file-system paths are supported.", nameof(path));
        }
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}

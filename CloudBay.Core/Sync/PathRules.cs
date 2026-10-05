using System.IO.Enumeration;

namespace CloudBay.Core.Sync;

public static class PathRules
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
            p.StartsWith(".cloudbay-copy-", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            IsOwnedTransferArtifact(p))) return true;
        return patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Any(pattern =>
            pattern.Contains('/')
                ? FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true)
                : parts.Any(p => FileSystemName.MatchesSimpleExpression(pattern, p, ignoreCase: true)));
    }

    private static bool IsOwnedTransferArtifact(string name)
    {
        const string prefix = ".cloudbay-transfer-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? ".part" :
            name.EndsWith(".original", StringComparison.OrdinalIgnoreCase) ? ".original" : null;
        if (suffix is null || name.Length != prefix.Length + 64 + suffix.Length) return false;
        foreach (var character in name.AsSpan(prefix.Length, 64)) if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    /// <summary>Matches legacy, literal, and guided rules against this engine's root. Folder rules include descendants.</summary>
    public static bool IsExcluded(string relative, AppSettings settings, bool isDirectory = false)
    {
        if (IsExcluded(relative, settings.Exclusions.Except(settings.DisabledLegacyExclusions, StringComparer.OrdinalIgnoreCase))) return true;
        if (settings.SelectedExclusions.Count == 0 && settings.GuidedExclusions.Count == 0) return false;
        relative = relative.Replace('\\', '/');
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.RootPath));
        if (settings.SelectedExclusions.Any(selection =>
            selection.Enabled && selection.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            (relative.Equals(selection.RelativePath, StringComparison.OrdinalIgnoreCase) && selection.IsFolder == isDirectory ||
                selection.IsFolder && relative.StartsWith(selection.RelativePath + "/", StringComparison.OrdinalIgnoreCase)))) return true;
        foreach (var rule in settings.GuidedExclusions)
        {
            if (!rule.Enabled || rule.RootPath is not null && !rule.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            var scopedRelative = relative;
            if (rule.RelativeDirectory is not null)
            {
                if (!relative.StartsWith(rule.RelativeDirectory + "/", StringComparison.OrdinalIgnoreCase)) continue;
                scopedRelative = relative[(rule.RelativeDirectory.Length + 1)..];
            }
            var parts = scopedRelative.Split('/');
            if (!isDirectory && (rule.Target is ExclusionTarget.Files or ExclusionTarget.All) &&
                MatchesGuidedPattern(rule.Pattern, rule.Pattern.Contains('/') ? scopedRelative : parts[^1])) return true;
            if (rule.Target is not (ExclusionTarget.Folders or ExclusionTarget.All)) continue;
            var folderCount = isDirectory ? parts.Length : parts.Length - 1;
            if (folderCount > 0 && (rule.Pattern.Contains('/')
                ? MatchesGuidedPattern(rule.Pattern, scopedRelative, folderCount)
                : parts.Take(folderCount).Any(part => FileSystemName.MatchesSimpleExpression(rule.Pattern, part, ignoreCase: true)))) return true;
        }
        return false;
    }

    private static bool MatchesGuidedPattern(string pattern, string path, int? matchingFolderCount = null)
    {
        var expression = pattern.Split('/');
        var components = path.Split('/');
        var componentCount = matchingFolderCount ?? components.Length;
        // Component-based dynamic matching makes '**' zero or more directories without
        // exponential wildcard backtracking or recursion through deeply nested user data.
        var matches = new bool[componentCount + 1];
        matches[0] = true;
        foreach (var segment in expression)
        {
            var next = new bool[componentCount + 1];
            if (segment == "**")
            {
                next[0] = matches[0];
                for (var i = 1; i < next.Length; i++) next[i] = matches[i] || next[i - 1];
            }
            else
                for (var i = 1; i < next.Length; i++)
                    next[i] = matches[i - 1] && FileSystemName.MatchesSimpleExpression(segment, components[i - 1], ignoreCase: true);
            matches = next;
        }
        // A matched folder prefix excludes its descendants; the file's own name is never
        // mistaken for a folder. The root (zero components) is not an excludable folder.
        return matchingFolderCount.HasValue ? matches.Skip(1).Any(value => value) : matches[^1];
    }

    /// <summary>Converts a file picker selection into a safe, literal rule for its configured root.</summary>
    public static SelectedExclusion CreateSelectedExclusion(AppSettings settings, string absolutePath, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath) || HasTraversal(absolutePath))
            throw new InvalidDataException("Select a file or folder inside a configured CloudBay backup.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
        var roots = settings.CustomBackups.Select(folder => folder.SourcePath).Prepend(settings.RootPath)
            .Select(NormalizeSelectionRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(root => root.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("To exclude an entire backup, stop that folder's backup instead.");
        var matchingRoots = roots.Where(root => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingRoots.Length != 1)
            throw new InvalidDataException("Select a file or folder inside one configured CloudBay backup.");
        var selectedRoot = matchingRoots[0];
        var relative = ValidateRelative(Path.GetRelativePath(selectedRoot, path).Replace('\\', '/'));
        FullPath(selectedRoot, relative); // Reject linked parent directories without reading or hydrating data.
        for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (Directory.Exists(ancestor) && new DirectoryInfo(ancestor).LinkTarget is not null ||
                File.Exists(ancestor) && new FileInfo(ancestor).LinkTarget is not null)
                throw new IOException("Linked files and folders cannot be selected for backup exclusions.");
        if (Directory.Exists(path) && !isFolder || File.Exists(path) && isFolder)
            throw new InvalidDataException("The selected item does not match the file or folder exclusion type.");
        return new(selectedRoot, relative, isFolder);
    }

    private static bool HasTraversal(string path) => path.Split(['/', '\\']).Any(part => part is "." or "..");

    private static string NormalizeSelectionRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || HasTraversal(root))
            throw new InvalidDataException("A selected exclusion must have an absolute backup root.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (normalized.Equals(Path.GetPathRoot(normalized), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A selected exclusion must belong to a dedicated backup folder.");
        return normalized;
    }

    public static void ValidateSettings(AppSettings settings)
    {
        if (settings.Notifications is null) throw new InvalidDataException("The saved notification preferences are invalid.");
        if (settings.KeyId is null || settings.BucketId is null || settings.BucketName is null || settings.AccountId is null ||
            settings.RootPath is null || settings.Prefix is null || settings.Theme is null ||
            settings.Backups is null || settings.CustomBackups is null || settings.Exclusions is null ||
            settings.SelectedExclusions is null || settings.GuidedExclusions is null || settings.DisabledLegacyExclusions is null ||
            settings.Backups.Any(folder => folder is null || string.IsNullOrWhiteSpace(folder.Name) || string.IsNullOrWhiteSpace(folder.OriginalPath) || string.IsNullOrWhiteSpace(folder.DestinationPath)) ||
            settings.CustomBackups.Any(folder => folder is null || string.IsNullOrWhiteSpace(folder.Name) || string.IsNullOrWhiteSpace(folder.SourcePath) || folder.Prefix is null) ||
            settings.Exclusions.Any(pattern => pattern is null) || settings.DisabledLegacyExclusions.Any(pattern => pattern is null) ||
            settings.SelectedExclusions.Any(selection => selection is null || selection.RootPath is null || selection.RelativePath is null) ||
            settings.GuidedExclusions.Any(rule => rule is null || rule.Pattern is null))
            throw new InvalidDataException("CloudBay settings contain missing account, folder, or exclusion fields. The original settings were preserved.");
        if (settings.Theme is not ("System" or "Light" or "Dark")) throw new InvalidDataException("The saved Windows theme is invalid.");
        if (settings.UploadConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(settings.UploadConcurrency));
        if (settings.DownloadConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(settings.DownloadConcurrency));
        if (!Enum.IsDefined(settings.UploadMode)) throw new ArgumentOutOfRangeException(nameof(settings.UploadMode));
        if (settings.UploadBytesPerSecond < 0 || settings.DownloadBytesPerSecond < 0) throw new ArgumentOutOfRangeException("Transfer limits must be nonnegative.");
        if (settings.PollSeconds is < 15 or > 3600) throw new ArgumentOutOfRangeException(nameof(settings.PollSeconds));
        if (NormalizePrefix(settings.Prefix) != settings.Prefix) throw new InvalidDataException("The saved cloud prefix is not normalized.");
        var root = Path.GetFullPath(settings.RootPath);
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a dedicated sync folder, not a volume root.");
        if (settings.Exclusions.Any(p => p.Contains("..") || p.Contains(':'))) throw new InvalidDataException("Exclusions must be relative names or wildcard patterns.");
        if (settings.DisabledLegacyExclusions.Except(settings.Exclusions, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("A disabled legacy exclusion must identify an existing legacy rule.");
        foreach (var selection in settings.SelectedExclusions)
        {
            if (!NormalizeSelectionRoot(selection.RootPath).Equals(selection.RootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A selected exclusion backup root is not normalized.");
            ValidateRelative(selection.RelativePath);
            // A removed backup may leave an inactive rule, which must never broaden to another root.
        }
        foreach (var rule in settings.GuidedExclusions)
        {
            if (!Enum.IsDefined(rule.Target)) throw new InvalidDataException("A guided exclusion has an unsupported target.");
            var components = rule.Pattern.Split('/');
            if (rule.Pattern.Length > 4096 || components.Length > 128 || components.Any(part => part.Length > 255))
                throw new InvalidDataException("An exclusion pattern supports up to 128 path components, 255 characters per component, and 4096 characters in total.");
            if (components.Any(part => part.Contains("**", StringComparison.Ordinal) && part != "**"))
                throw new InvalidDataException("Use the any-number-of-folders token as a complete path component.");
            // '*' and '?' are the UI's wildcard tokens; all remaining characters are literal Windows path components.
            ValidateRelative(rule.Pattern.Replace('*', 'x').Replace('?', 'x'));
            if (rule.RootPath is not null && !NormalizeSelectionRoot(rule.RootPath).Equals(rule.RootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A guided exclusion backup root is not normalized.");
            if (rule.RelativeDirectory is not null)
            {
                if (rule.RootPath is null) throw new InvalidDataException("A folder-scoped exclusion must identify its backup root.");
                ValidateRelative(rule.RelativeDirectory);
            }
        }
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

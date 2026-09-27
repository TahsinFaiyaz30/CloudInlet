using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using CloudBay.Core.Safety;

namespace CloudBay.Core.Filters;

[Flags]
public enum FilterTargets
{
    None = 0,
    Git = 1,
    Rclone = 2,
    MountainDuck = 4,
    Cyberduck = 8,
    All = Git | Rclone | MountainDuck | Cyberduck
}

public sealed record FilterApplyResult(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<Guid> JournalIds,
    IReadOnlyList<string> ActivationNotes,
    string GitRules,
    string RcloneRules,
    string MountainDuckNameRegex,
    string CyberduckTransferRegex);

/// <summary>Generates provider-specific rules without treating one provider's format as another's.</summary>
public sealed class DeveloperFilterService
{
    private const string StartMarker = "# >>> CloudBay developer exclusions >>>";
    private const string EndMarker = "# <<< CloudBay developer exclusions <<<";
    private const string MountainDuckKey = "fs.filenames.filter.name.regexp";
    private const string CyberduckKey = "queue.upload.skip.regex";
    private static readonly string[] PresetNames =
    [
        "node_modules", ".pnpm", ".next", ".turbo", ".cache", "__pycache__",
        ".venv", "venv", "target", "bin", "obj"
    ];

    // These are the documented Windows defaults. An existing user value in default.properties
    // takes precedence and is retained when CloudBay builds its combined value.
    private const string MountainDuckWindowsDefaults = @"[.]{1,2} .*[\x3c\x3e"":?*|/\x5c].* [.]file-segments";
    private const string CyberduckUploadDefaults = @".*~[.].*|[.]DS_Store|[.]svn|CVS";
    private readonly IOperationJournal _journal;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<Guid> _currentJournalIds = [];

    public DeveloperFilterService(IOperationJournal journal) => _journal = journal;

    public static IReadOnlyList<string> Presets => Array.AsReadOnly(PresetNames);

    public static IReadOnlyList<string> ValidateSelection(IEnumerable<string> selectedNames)
    {
        ArgumentNullException.ThrowIfNull(selectedNames);
        var allowed = new HashSet<string>(PresetNames, StringComparer.Ordinal);
        var selected = new HashSet<string>(selectedNames, StringComparer.Ordinal);
        if (selected.Any(name => !allowed.Contains(name)))
            throw new ArgumentException("Selection contains an unknown developer exclusion preset.", nameof(selectedNames));
        return PresetNames.Where(selected.Contains).ToArray();
    }

    /// <summary>
    /// Accepts filename globs only. Duck's filter is applied to individual names, so a
    /// path-scoped Git/Rclone rule could not be represented consistently across providers.
    /// A trailing slash denotes a directory-only rule for Git and Rclone.
    /// </summary>
    public static IReadOnlyList<string> ValidateCustomPatterns(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 128 ||
                pattern != pattern.Trim() || pattern.Any(char.IsControl) ||
                pattern.Contains('/') && !pattern.EndsWith('/') ||
                pattern.Count(c => c == '/') > 1 || pattern.Contains('\\') ||
                pattern[0] is '!' or '#' or '-' or '+' ||
                pattern.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '*' or '?' or '/')))
                throw new ArgumentException($"Invalid exclusion pattern: {pattern}", nameof(patterns));
            var name = pattern.TrimEnd('/');
            if (name.Length == 0 || name is "." or ".." or "*" or "**" or "?" or "*.*" ||
                name.Contains("**", StringComparison.Ordinal))
                throw new ArgumentException($"Unsafe exclusion pattern: {pattern}", nameof(patterns));
            if (seen.Add(pattern)) result.Add(pattern);
            if (result.Count > 128)
                throw new ArgumentException("At most 128 custom exclusion patterns are supported.", nameof(patterns));
        }
        return result;
    }

    public static string GenerateGitRules(IEnumerable<string> selectedNames) =>
        string.Join("\n", ValidateSelection(selectedNames).Select(name => $"{name}/"));

    public static string GenerateRcloneRules(IEnumerable<string> selectedNames) =>
        string.Join("\n", ValidateSelection(selectedNames).SelectMany(name =>
            new[] { $"- {name}/", $"- {name}/**" }));

    public static string GenerateFilenameRegex(IEnumerable<string> selectedNames)
    {
        var names = ValidateSelection(selectedNames);
        if (names.Count == 0) return string.Empty;
        return "^(?:" + string.Join("|", names.Select(Regex.Escape)) + ")$";
    }

    public static string GenerateGitRules(IEnumerable<string> selectedNames, IEnumerable<string> customPatterns) =>
        JoinLines(GenerateGitRules(selectedNames), ValidateCustomPatterns(customPatterns));

    public static string GenerateRcloneRules(IEnumerable<string> selectedNames, IEnumerable<string> customPatterns)
    {
        var custom = ValidateCustomPatterns(customPatterns).SelectMany(pattern => pattern.EndsWith('/')
            ? new[] { $"- {pattern}", $"- {pattern}**" }
            : new[] { $"- {pattern}" });
        return JoinLines(GenerateRcloneRules(selectedNames), custom);
    }

    public static string GenerateFilenameRegex(IEnumerable<string> selectedNames, IEnumerable<string> customPatterns)
    {
        var names = ValidateSelection(selectedNames);
        var custom = ValidateCustomPatterns(customPatterns);
        var globs = names.Select(Regex.Escape).Concat(custom.Select(pattern =>
            GlobToRegex(pattern.TrimEnd('/')))).ToArray();
        return globs.Length == 0 ? string.Empty : "^(?:" + string.Join("|", globs) + ")$";
    }

    private static string GlobToRegex(string glob) => string.Concat(glob.Select(c => c switch
    {
        '*' => ".*",
        '?' => ".",
        _ => Regex.Escape(c.ToString())
    }));

    private static string JoinLines(string first, IEnumerable<string> second)
    {
        var extra = string.Join("\n", second);
        return first.Length == 0 ? extra : extra.Length == 0 ? first : first + "\n" + extra;
    }

    /// <summary>
    /// Writes Git/Rclone rule files in cloudRoot and, when selected, the real Duck application
    /// preference file. Duck preferences are global to the Windows user and require a restart.
    /// Rclone only uses the generated rule file when --filter-from points to it.
    /// </summary>
    public async Task<FilterApplyResult> ApplyAsync(
        string cloudRoot,
        IEnumerable<string> selectedNames,
        FilterTargets targets = FilterTargets.All,
        CancellationToken cancellationToken = default) =>
        await ApplyAsync(cloudRoot, selectedNames, Array.Empty<string>(), targets, cancellationToken)
            .ConfigureAwait(false);

    public async Task<FilterApplyResult> ApplyAsync(
        string cloudRoot,
        IEnumerable<string> selectedNames,
        IEnumerable<string> customPatterns,
        FilterTargets targets = FilterTargets.All,
        CancellationToken cancellationToken = default)
    {
        if ((targets & ~FilterTargets.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(targets));
        var names = ValidateSelection(selectedNames);
        var custom = ValidateCustomPatterns(customPatterns);
        var root = Path.GetFullPath(cloudRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Cloud root does not exist: {root}");

        var git = GenerateGitRules(names, custom);
        var rclone = GenerateRcloneRules(names, custom);
        var nameRegex = GenerateFilenameRegex(names, custom);
        var written = new List<string>();
        var notes = new List<string>();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentJournalIds.Clear();
            try
            {
                var gitPath = Path.Combine(root, ".gitignore");
                if (await WriteManagedAsync(gitPath, git, targets.HasFlag(FilterTargets.Git),
                    "filter.git", root, cancellationToken).ConfigureAwait(false))
                    written.Add(gitPath);
                if (targets.HasFlag(FilterTargets.Git))
                    notes.Add("Git reads .gitignore only within its containing repository; nested independent repositories need their own rules.");

                var rclonePath = Path.Combine(root, ".cloudbay-rclone-filter");
                if (await WriteManagedAsync(rclonePath, rclone, targets.HasFlag(FilterTargets.Rclone),
                    "filter.rclone", root, cancellationToken).ConfigureAwait(false))
                    written.Add(rclonePath);
                if (targets.HasFlag(FilterTargets.Rclone))
                    notes.Add($"Pass --filter-from \"{rclonePath}\" to Rclone commands. Rclone does not automatically load a rule file from the cloud root; avoid --delete-excluded.");

                var cyberduckExportPath = Path.Combine(root, ".cyberduckignore");
                var cyberduckExport = "# CloudBay exclusion export. Cyberduck and Mountain Duck do not read this file automatically.\n" +
                    "# Active Duck filtering is configured through the regex in default.properties.\n" + git;
                if (await WriteManagedAsync(cyberduckExportPath, cyberduckExport,
                    targets.HasFlag(FilterTargets.Cyberduck) || targets.HasFlag(FilterTargets.MountainDuck),
                    "filter.cyberduckexport", root, cancellationToken).ConfigureAwait(false))
                    written.Add(cyberduckExportPath);
                if (targets.HasFlag(FilterTargets.Cyberduck) || targets.HasFlag(FilterTargets.MountainDuck))
                    notes.Add(".cyberduckignore is an export for review only. Cyberduck and Mountain Duck do not automatically load it; default.properties holds the active filename regex.");

                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(roaming))
                    throw new IOException("Windows roaming application data directory is unavailable.");
                var duckPath = Path.Combine(roaming, "Cyberduck", "default.properties");
                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                if (targets.HasFlag(FilterTargets.MountainDuck))
                {
                    keys.Add(MountainDuckKey, nameRegex);
                    notes.Add("Mountain Duck exclusions are filename based and apply to every mount for this Windows account. Restart Mountain Duck for changes to take effect.");
                }
                if (targets.HasFlag(FilterTargets.Cyberduck))
                {
                    keys.Add(CyberduckKey, nameRegex);
                    notes.Add("Cyberduck upload transfer exclusions apply to every connection for this Windows account. Restart Cyberduck for changes to take effect.");
                }
                if (await WriteDuckPreferencesAsync(duckPath, keys, root, cancellationToken).ConfigureAwait(false))
                    written.Add(duckPath);
            }
            catch (Exception applyError)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var id in _currentJournalIds.AsEnumerable().Reverse())
                {
                    try { await RollbackCoreAsync(id, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { rollbackErrors.Add(error); }
                }
                if (rollbackErrors.Count > 0)
                    throw new AggregateException("Filter apply failed and one or more completed writes could not be rolled back.",
                        new[] { applyError }.Concat(rollbackErrors));
                throw;
            }
        }
        finally { _writeLock.Release(); }

        return new FilterApplyResult(written, _currentJournalIds.ToArray(), notes, git, rclone, nameRegex, nameRegex);
    }

    private async Task<bool> WriteDuckPreferencesAsync(
        string path, IReadOnlyDictionary<string, string> selectedKeys, string root, CancellationToken ct)
    {
        var existing = await ReadTextSafelyAsync(path, ct).ConfigureAwait(false);
        var withoutBlock = RemoveManagedBlock(existing);
        var lines = new List<string>();
        foreach (var pair in selectedKeys)
        {
            var original = LastSimplePropertyValue(withoutBlock, pair.Key);
            var baseline = original ?? (pair.Key == MountainDuckKey
                ? MountainDuckWindowsDefaults : CyberduckUploadDefaults);
            string combined;
            if (pair.Value.Length == 0) combined = baseline;
            else if (pair.Key == MountainDuckKey) combined = baseline + " " + pair.Value;
            else combined = "(?:" + baseline + ")|(?:" + pair.Value + ")";
            // Java .properties treats backslash as an escape character.
            lines.Add(pair.Key + "=" + combined.Replace("\\", "\\\\", StringComparison.Ordinal));
        }
        var content = lines.Count == 0 ? withoutBlock : AddManagedBlock(withoutBlock, string.Join("\n", lines));
        return await WriteFullFileAsync(path, content, "filter.duck", root, ct).ConfigureAwait(false);
    }

    private async Task<bool> WriteManagedAsync(
        string path, string body, bool enabled, string operation, string root, CancellationToken ct)
    {
        var existing = await ReadTextSafelyAsync(path, ct).ConfigureAwait(false);
        var withoutBlock = RemoveManagedBlock(existing);
        var updated = enabled ? AddManagedBlock(withoutBlock, body) : withoutBlock;
        return await WriteFullFileAsync(path, updated, operation, root, ct).ConfigureAwait(false);
    }

    private async Task<bool> WriteFullFileAsync(string path, string content, string operation, string root, CancellationToken ct)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to overwrite a linked rule file: {path}");
        var existingBytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
        var newBytes = new UTF8Encoding(false, true).GetBytes(content);
        if (existingBytes is null && content.Length == 0) return false;
        if (existingBytes is not null && existingBytes.AsSpan().SequenceEqual(newBytes)) return false;
        var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Backups");
        var backup = existingBytes is null ? string.Empty : Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".bak");
        var newHash = Convert.ToHexString(SHA256.HashData(newBytes));
        var entry = await _journal.BeginAsync(operation, root, path, new Dictionary<string, string>
        {
            ["BackupPath"] = backup,
            ["CreatedNew"] = (existingBytes is null).ToString(),
            ["TargetPath"] = path,
            ["OriginalSha256"] = existingBytes is null ? string.Empty : Convert.ToHexString(SHA256.HashData(existingBytes)),
            ["NewSha256"] = newHash
        }, ct).ConfigureAwait(false);
        _currentJournalIds.Add(entry.Id);
        string? temp = null;
        try
        {
            if (existingBytes is not null)
            {
                Directory.CreateDirectory(backupDir);
                await File.WriteAllBytesAsync(backup, existingBytes, ct).ConfigureAwait(false);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + ".cloudbay-" + entry.Id.ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, newBytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (existingBytes is null)
            {
                File.Move(temp, path, false);
            }
            else
            {
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(existingBytes))
                    throw new IOException($"Filter file changed during update; refusing to overwrite it: {path}");
                File.Move(temp, path, true);
            }
            temp = null;
            await _journal.CompleteAsync(entry.Id, new Dictionary<string, string>
            {
                ["BackupPath"] = backup,
                ["CreatedNew"] = (existingBytes is null).ToString(),
                ["TargetPath"] = path
            }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await _journal.FailAsync(entry.Id, ex.Message, new Dictionary<string, string>
            {
                ["BackupPath"] = backup,
                ["TargetPath"] = path
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (temp is not null && File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<bool> RollbackAsync(Guid journalId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RollbackCoreAsync(journalId, cancellationToken).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task<bool> RollbackCoreAsync(Guid journalId, CancellationToken ct)
    {
        var original = await _journal.GetAsync(journalId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Journal entry {journalId} was not found.");
        if (original.Operation is not ("filter.git" or "filter.rclone" or "filter.duck" or "filter.cyberduckexport"))
            throw new InvalidOperationException("This journal entry is not a filter update.");
        if (original.State == JournalState.RolledBack) return false;
        if (!original.Metadata.TryGetValue("TargetPath", out var target) ||
            !original.Metadata.TryGetValue("NewSha256", out var newHash) ||
            !original.Metadata.TryGetValue("BackupPath", out var backup) ||
            !original.Metadata.TryGetValue("CreatedNew", out var createdText))
            throw new IOException("Filter journal is missing rollback data.");
        var created = bool.Parse(createdText);
        if (!File.Exists(target))
        {
            if (created)
            {
                await _journal.MarkRolledBackAsync(journalId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            throw new IOException("Filter target changed or disappeared; automatic rollback is unsafe.");
        }
        var current = await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false);
        if (original.Metadata.TryGetValue("OriginalSha256", out var unchangedHash) &&
            unchangedHash.Length > 0 &&
            string.Equals(Convert.ToHexString(SHA256.HashData(current)), unchangedHash, StringComparison.Ordinal))
        {
            await _journal.MarkRolledBackAsync(journalId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(current)), newHash, StringComparison.Ordinal))
            throw new IOException("Filter target was edited after CloudBay wrote it; automatic rollback is unsafe.");
        byte[]? restore = null;
        if (!created)
        {
            if (!File.Exists(backup))
                throw new IOException("Original filter backup is missing.");
            restore = await File.ReadAllBytesAsync(backup, ct).ConfigureAwait(false);
            if (!original.Metadata.TryGetValue("OriginalSha256", out var oldHash) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(restore)), oldHash, StringComparison.Ordinal))
                throw new IOException("Original filter backup failed verification.");
        }
        var rollback = await _journal.BeginAsync("filter.rollback", backup, target,
            new Dictionary<string, string> { ["OriginalJournalId"] = journalId.ToString("D") }, ct).ConfigureAwait(false);
        try
        {
            if (created) File.Delete(target);
            else
            {
                var temp = target + ".cloudbay-rollback-" + rollback.Id.ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temp, restore!, ct).ConfigureAwait(false);
                    File.Move(temp, target, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            await _journal.CompleteAsync(rollback.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await _journal.MarkRolledBackAsync(journalId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            try { await _journal.FailAsync(rollback.Id, ex.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
            catch { }
            throw;
        }
    }

    private static string? LastSimplePropertyValue(string content, string key)
    {
        string? found = null;
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith('!')) continue;
            if (!trimmed.StartsWith(key, StringComparison.Ordinal)) continue;
            var tail = trimmed[key.Length..];
            if (!tail.StartsWith('=') && !tail.StartsWith(':'))
            {
                if (tail.Length == 0 || char.IsWhiteSpace(tail[0]))
                    throw new IOException($"Existing {key} preference uses an unsupported form; edit it manually before applying CloudBay filters.");
                continue;
            }
            if (tail.EndsWith('\\'))
                throw new IOException($"Existing {key} preference spans multiple lines; edit it manually before applying CloudBay filters.");
            found = tail[1..].Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        return found;
    }

    private static string RemoveManagedBlock(string content)
    {
        var start = content.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);
        if ((start < 0) != (end < 0) || (start >= 0 && end < start))
            throw new IOException("CloudBay managed exclusion block is incomplete; refusing to alter the file.");
        if (start < 0) return content;
        if ((start > 0 && content[start - 1] != '\n') ||
            (end + EndMarker.Length < content.Length &&
             content[end + EndMarker.Length] is not ('\r' or '\n')))
            throw new IOException("CloudBay exclusion markers are not on their own lines; refusing to alter the file.");
        if (content.IndexOf(StartMarker, start + StartMarker.Length, StringComparison.Ordinal) >= 0 ||
            content.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)
            throw new IOException("Multiple CloudBay managed exclusion blocks found; refusing to alter the file.");
        var endOfLine = content.IndexOf('\n', end + EndMarker.Length);
        var after = endOfLine < 0 ? content.Length : endOfLine + 1;
        return content[..start] + content[after..];
    }

    private static string AddManagedBlock(string existing, string body)
    {
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = existing.Length == 0 ? string.Empty :
            existing + (existing.EndsWith('\n') ? string.Empty : newline);
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        return prefix + StartMarker + newline + normalized + (normalized.Length > 0 ? newline : string.Empty) + EndMarker + newline;
    }

    private static async Task<string> ReadTextSafelyAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return string.Empty;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                                  (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            throw new IOException($"Cannot safely edit UTF-16 rule file: {path}");
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw new IOException($"Cannot safely edit non-UTF-8 rule file: {path}", ex); }
    }
}

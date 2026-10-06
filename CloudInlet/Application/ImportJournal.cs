using System.Text.Json;
using CloudInlet.Core.Sync;

namespace CloudInlet.Application;

public sealed record FolderImportRecord(string Id, DateTimeOffset StartedUtc, FolderImportPlan Plan,
    string DestinationIdentity, string State, string? Error = null);
public sealed record ImportHistoryCursor(bool Completed, DateTimeOffset StartedUtc, string Id);

/// <summary>Interrupted imports stay visible for explicit review; they never restart unattended.</summary>
public sealed class ImportJournal(string stateDirectory)
{
    private readonly string _directory = Path.Combine(stateDirectory, "Imports");
    private readonly object _gate = new();
    public int SkippedRecords { get; private set; }
    public bool HasMoreRecords { get; private set; }
    public IReadOnlyList<FolderImportRecord> Read(string? destinationIdentity = null, ImportHistoryCursor? after = null, string? recordId = null)
    {
        if (recordId is not null && !Guid.TryParseExact(recordId, "N", out _)) throw new ArgumentException("Invalid import identity.", nameof(recordId));
        lock (_gate)
        {
            SkippedRecords = 0;
            HasMoreRecords = false;
            if (!Directory.Exists(_directory)) return [];
            var items = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id, recordId is null ? after : null);
            var paths = recordId is null ? Directory.EnumerateFiles(_directory, "*.json") : new[] { Path.Combine(_directory, recordId + ".json") };
            foreach (var path in paths)
            {
                // Damaged records remain on disk for diagnosis, without inventing an import.
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("An import record cannot be a linked file.");
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (stream.Length > 1024 * 1024) throw new InvalidDataException("The folder import record exceeds the supported size.");
                    var item = JsonSerializer.Deserialize<FolderImportRecord>(stream) ?? throw new InvalidDataException("The import record is empty.");
                    if (!Guid.TryParseExact(item.Id, "N", out _) || !Path.GetFileNameWithoutExtension(path).Equals(item.Id, StringComparison.OrdinalIgnoreCase) ||
                        item.Plan is null || item.State is not ("Copying" or "Needs review" or "Completed") || item.StartedUtc == default || item.Error?.Length > 64 * 1024 ||
                        item.Plan.FileCount < 0 || item.Plan.TotalBytes < 0 || item.Plan.AvailableBytes is < 0 || item.Plan.Fingerprint is not { Length: 64 } ||
                        !item.Plan.Fingerprint.All(Uri.IsHexDigit) || !CanonicalPath(item.Plan.SourcePath) || !CanonicalPath(item.Plan.DestinationPath) ||
                        Within(item.Plan.SourcePath, item.Plan.DestinationPath) || Within(item.Plan.DestinationPath, item.Plan.SourcePath))
                        throw new InvalidDataException("The folder import record has invalid source or destination information.");
                    DestinationPrefix(item.DestinationIdentity, item.Plan.DestinationPath);
                    if (destinationIdentity is not null && item.DestinationIdentity != destinationIdentity) continue;
                    items.Add(item.State == "Copying" ? item with { State = "Needs review", Error = "The import was interrupted. Review the source to continue; completed copies were retained." } : item, stream.Length);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
                { SkippedRecords++; }
            }
            HasMoreRecords = items.HasMore;
            return items.Records;
        }
    }
    public void Save(FolderImportRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _)) throw new ArgumentException("Invalid import identity.");
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, record.Id + ".json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, record);
                    if (output.Length > 1024 * 1024) throw new InvalidDataException("The folder import checkpoint exceeds its supported size.");
                    output.Flush(true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static string DestinationPrefix(string identity, string destination)
    {
        var parts = identity?.Split('|');
        if (parts is not { Length: 4 } || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) ||
            parts[2] != PathRules.NormalizePrefix(parts[2]) || !CanonicalPath(parts[3]) || !CanonicalPath(destination) ||
            !Within(parts[3], destination) || destination.Equals(parts[3], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The import record belongs to an invalid destination account or folder.");
        var relative = PathRules.ValidateRelative(Path.GetRelativePath(parts[3], destination).Replace('\\', '/'));
        if (PathRules.IsExcluded(relative, Array.Empty<string>())) throw new InvalidDataException("An import destination cannot be an internal working folder.");
        return parts[2] + relative + "/";
    }

    private static bool CanonicalPath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        !path.Split(['/', '\\']).Any(component => component is "." or "..") &&
        path.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bounds retained history while giving interrupted work priority over completed imports.</summary>
internal sealed class ImportHistorySelection<T>(Func<T, bool> interrupted, Func<T, DateTimeOffset> started,
    Func<T, string> id, ImportHistoryCursor? after = null)
{
    internal const long MaximumBytes = 128L * 1024 * 1024;
    private readonly List<(T Record, long Bytes)> _items = [];
    private long _bytes;
    private ImportHistoryCursor? _firstOmitted;
    public bool HasMore { get; private set; }
    public IReadOnlyList<T> Records => _items.Select(item => item.Record).ToArray();
    public bool MayInclude(T record)
    {
        if (!IsAfter(record)) return false;
        if (_firstOmitted is not null && Compare(Cursor(record), _firstOmitted) >= 0)
        { HasMore = true; return false; }
        if (_items.Count < 50 || Compare(record, _items[^1].Record) < 0) return true;
        RememberOmitted(Cursor(record));
        return false;
    }

    public void Add(T record, long bytes)
    {
        if (!MayInclude(record)) return;
        if (bytes < 0 || bytes > MaximumBytes)
            throw new InvalidDataException("The import history entry exceeds the supported memory budget. Its checkpoint was retained for review.");
        var index = _items.FindIndex(item => Compare(record, item.Record) < 0);
        _items.Insert(index < 0 ? _items.Count : index, (record, bytes));
        _bytes += bytes;
        while (_items.Count > 50 || _bytes > MaximumBytes)
        {
            RememberOmitted(Cursor(_items[^1].Record));
            _bytes -= _items[^1].Bytes;
            _items.RemoveAt(_items.Count - 1);
        }
    }

    private int Compare(T first, T second)
        => Compare(Cursor(first), Cursor(second));
    private ImportHistoryCursor Cursor(T record) => new(!interrupted(record), started(record), id(record));
    private bool IsAfter(T record) => after is null || Compare(Cursor(record), after) > 0;
    private void RememberOmitted(ImportHistoryCursor omitted)
    {
        HasMore = true;
        if (_firstOmitted is null || Compare(omitted, _firstOmitted) < 0) _firstOmitted = omitted;
    }
    private static int Compare(ImportHistoryCursor first, ImportHistoryCursor second)
    {
        var priority = first.Completed.CompareTo(second.Completed);
        if (priority != 0) return priority;
        var time = second.StartedUtc.CompareTo(first.StartedUtc);
        return time != 0 ? time : StringComparer.Ordinal.Compare(first.Id, second.Id);
    }
}

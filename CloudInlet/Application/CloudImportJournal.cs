using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Core.Sync;

namespace CloudInlet.Application;

public sealed class CloudImportJournal(string stateDirectory)
{
    private sealed record CompletionReceipt(string SourceVersionId, CloudObject Copied);
    private readonly string _directory = Path.Combine(stateDirectory, "CloudImports");
    private readonly object _gate = new();
    public int SkippedRecords { get; private set; }
    public bool HasMoreRecords { get; private set; }
    public IReadOnlyList<CloudImportRecord> Read(string? destinationIdentity = null, ImportHistoryCursor? after = null, string? recordId = null)
    {
        if (recordId is not null && !Guid.TryParseExact(recordId, "N", out _)) throw new ArgumentException("Invalid cloud import identity.", nameof(recordId));
        lock (_gate)
        {
            SkippedRecords = 0;
            HasMoreRecords = false;
            if (!Directory.Exists(_directory)) return [];
            var result = new ImportHistorySelection<CloudImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id, recordId is null ? after : null);
            var paths = recordId is null ? Directory.EnumerateFiles(_directory, "*.json") : new[] { Path.Combine(_directory, recordId + ".json") };
            foreach (var path in paths)
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("An import record cannot be a linked file.");
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (stream.Length > 64L * 1024 * 1024) throw new InvalidDataException("The cloud import record exceeds the supported size.");
                    var item = JsonSerializer.Deserialize<CloudImportRecord>(stream) ?? throw new InvalidDataException("The cloud import record is empty.");
                    if (!Guid.TryParseExact(item.Id, "N", out _) || !Path.GetFileNameWithoutExtension(path).Equals(item.Id, StringComparison.OrdinalIgnoreCase) ||
                        item.Plan is null || item.Plan.JobId != item.Id || item.Completed is null || item.State is not ("Copying" or "Needs review" or "Completed") ||
                        item.StartedUtc == default || item.Error?.Length > 64 * 1024)
                        throw new InvalidDataException("The cloud import record does not match its saved identity.");
                    if (destinationIdentity is not null && item.DestinationIdentity != destinationIdentity) continue;
                    CloudImport.ValidatePlan(item.Plan);
                    if (!result.MayInclude(item)) continue;
                    item = item with { Completed = ReadProgress(item, stream.Length, out var receiptBytes) };
                    CloudImport.ValidateCompleted(item.Plan, item.Completed,
                        ImportJournal.DestinationPrefix(item.DestinationIdentity, item.Plan.DestinationPath));
                    if (item.State == "Completed" && item.Completed.Count != item.Plan.Items.Count)
                        throw new InvalidDataException("The completed import record is missing file receipts.");
                    result.Add(item.State == "Copying" ? item with { State = "Needs review", Error = "The cloud import was interrupted. Completed file versions were retained; continue from import history." } : item, checked(stream.Length + receiptBytes));
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
                { SkippedRecords++; } // The damaged file remains untouched for review.
            }
            HasMoreRecords = result.HasMore;
            return result.Records;
        }
    }
    public void Save(CloudImportRecord item)
    {
        if (!Guid.TryParseExact(item.Id, "N", out _) || item.Plan.JobId != item.Id) throw new ArgumentException("Invalid cloud import identity.");
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, item.Id + ".json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, item);
                    if (stream.Length > 64L * 1024 * 1024) throw new InvalidDataException("This cloud import plan exceeds the supported checkpoint size. Choose a smaller source folder.");
                    stream.Flush(true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    /// <summary>Persists one verified receipt without rewriting the immutable source plan.</summary>
    public void SaveProgress(string recordId, string sourceVersionId, CloudObject copied)
    {
        if (!Guid.TryParseExact(recordId, "N", out _) || string.IsNullOrWhiteSpace(sourceVersionId) || sourceVersionId.Length > 1024 || copied is null)
            throw new ArgumentException("Invalid cloud import receipt identity.");
        lock (_gate)
        {
            var directory = Path.Combine(_directory, recordId);
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The import receipt folder cannot be a linked directory.");
            var path = Path.Combine(directory, ReceiptName(sourceVersionId) + ".json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new CompletionReceipt(sourceVersionId, copied));
                    if (stream.Length > 64 * 1024) throw new InvalidDataException("The cloud import receipt exceeds its supported size.");
                    stream.Flush(true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private IReadOnlyDictionary<string, CloudObject> ReadProgress(CloudImportRecord item, long recordBytes, out long receiptBytes)
    {
        receiptBytes = 0;
        var completed = item.Completed.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var directory = Path.Combine(_directory, item.Id);
        if (!Directory.Exists(directory)) return completed;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An import receipt folder cannot be linked.");
        var destinationPrefix = ImportJournal.DestinationPrefix(item.DestinationIdentity, item.Plan.DestinationPath);
        var sourceByVersion = item.Plan.Items.ToDictionary(source => source.Source.FileId, StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("An import receipt cannot be a linked file.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > 64 * 1024) throw new InvalidDataException("The import receipt exceeds its supported size.");
                var receipt = JsonSerializer.Deserialize<CompletionReceipt>(stream) ?? throw new InvalidDataException("The import receipt is empty.");
                if (receipt.SourceVersionId is null || !sourceByVersion.TryGetValue(receipt.SourceVersionId, out var source) ||
                    !Path.GetFileNameWithoutExtension(path).Equals(ReceiptName(receipt.SourceVersionId), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An import receipt does not match its immutable source version.");
                // Validate one receipt against one source, with original per-file count/size.
                CloudImport.ValidateCompleted(item.Plan with { Items = [source], FileCount = source.RelativePath.EndsWith('/') ? 0 : 1, TotalBytes = source.Source.Size },
                    new Dictionary<string, CloudObject>(StringComparer.Ordinal) { [receipt.SourceVersionId] = receipt.Copied }, destinationPrefix);
                if (completed.TryGetValue(receipt.SourceVersionId, out var prior) && prior != receipt.Copied)
                    throw new InvalidDataException("Conflicting import receipts were retained for review.");
                if (prior is null) receiptBytes = checked(receiptBytes + stream.Length);
                if (recordBytes + receiptBytes > ImportHistorySelection<CloudImportRecord>.MaximumBytes)
                    break;
                completed[receipt.SourceVersionId] = receipt.Copied;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            { SkippedRecords++; }
        }
        if (recordBytes + receiptBytes > ImportHistorySelection<CloudImportRecord>.MaximumBytes)
            throw new InvalidDataException("The import receipts exceed the supported history memory budget. Original checkpoints were retained for review.");
        return completed;
    }

    private static string ReceiptName(string sourceVersionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceVersionId))).ToLowerInvariant();
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudBay.Core.Safety;

/// <summary>
/// Stores one timestamped JSON file per operation. A successful BeginAsync has
/// flushed the file to disk before the caller may mutate shell or file state.
/// </summary>
public sealed class OperationJournal : IOperationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OperationJournal(string? journalDirectory = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(journalDirectory) && string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        _directory = Path.GetFullPath(journalDirectory ?? Path.Combine(localAppData, "CloudBay", "Journals"));
    }

    public string JournalDirectory => _directory;

    public async Task<JournalEntry> BeginAsync(
        string operation,
        string? sourcePath,
        string? destinationPath,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            Operation = operation,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            State = JournalState.Started,
            Metadata = CopyMetadata(metadata)
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            string fileName = $"{entry.StartedAtUtc:yyyyMMddTHHmmssfffffffZ}-{entry.Id:N}.json";
            await WriteNewAsync(Path.Combine(_directory, fileName), entry, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<JournalEntry> CompleteAsync(
        Guid id,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id, JournalState.Completed, null, metadata, cancellationToken);

    public Task<JournalEntry> FailAsync(
        Guid id,
        string error,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return UpdateAsync(id, JournalState.Failed, error, metadata, cancellationToken);
    }

    public Task<JournalEntry> MarkRolledBackAsync(
        Guid id,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id, JournalState.RolledBack, null, metadata, cancellationToken);

    public async Task<JournalEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("A journal ID is required.", nameof(id));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? path = FindPath(id);
            return path is null ? null : await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<JournalEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return Array.Empty<JournalEntry>();
            var entries = new List<JournalEntry>();
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(await ReadAsync(path, cancellationToken).ConfigureAwait(false));
            }

            return entries.OrderByDescending(entry => entry.StartedAtUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JournalEntry> UpdateAsync(
        Guid id,
        JournalState state,
        string? error,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) throw new ArgumentException("A journal ID is required.", nameof(id));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = FindPath(id) ?? throw new FileNotFoundException($"Journal entry {id} was not found.");
            JournalEntry current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (current.State == JournalState.RolledBack)
                throw new InvalidOperationException("A rolled back operation cannot be changed.");
            if (current.State == JournalState.Completed && state is JournalState.Completed or JournalState.Failed)
                throw new InvalidOperationException("A completed operation cannot be completed or failed again.");
            if (current.State == JournalState.Failed && state is JournalState.Completed or JournalState.Failed)
                throw new InvalidOperationException("A failed operation cannot be completed or failed again.");

            var mergedMetadata = new Dictionary<string, string>(current.Metadata, StringComparer.Ordinal);
            if (metadata is not null)
            {
                foreach (var pair in metadata) mergedMetadata[pair.Key] = pair.Value;
            }

            JournalEntry updated = current with
            {
                State = state,
                Error = error,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Metadata = mergedMetadata
            };
            await ReplaceAsync(path, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? FindPath(Guid id)
    {
        if (!Directory.Exists(_directory)) return null;
        string[] matches = Directory.GetFiles(_directory, $"*-{id:N}.json");
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new IOException($"Multiple journal entries have ID {id}.")
        };
    }

    private static async Task<JournalEntry> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        JournalEntry? entry = await JsonSerializer.DeserializeAsync<JournalEntry>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return entry ?? throw new InvalidDataException($"Journal entry is empty: {path}");
    }

    private static async Task WriteNewAsync(string path, JournalEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task ReplaceAsync(string path, JournalEntry entry, CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteNewAsync(temporaryPath, entry, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyDictionary<string, string> CopyMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
}

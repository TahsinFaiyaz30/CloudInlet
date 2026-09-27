namespace CloudBay.Core.Safety;

/// <summary>A durable record of filesystem and shell operations.</summary>
public interface IOperationJournal
{
    Task<JournalEntry> BeginAsync(
        string operation,
        string? sourcePath,
        string? destinationPath,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<JournalEntry> CompleteAsync(
        Guid id,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<JournalEntry> FailAsync(
        Guid id,
        string error,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<JournalEntry> MarkRolledBackAsync(
        Guid id,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<JournalEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JournalEntry>> ListAsync(CancellationToken cancellationToken = default);
}

public enum JournalState
{
    Started,
    Completed,
    Failed,
    RolledBack
}

public sealed record JournalEntry
{
    public Guid Id { get; init; }
    public string Operation { get; init; } = string.Empty;
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public JournalState State { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

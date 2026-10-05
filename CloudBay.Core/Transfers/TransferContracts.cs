namespace CloudBay.Core.Transfers;

/// <summary>A reviewed endpoint; FolderId is a provider identity, Path is a B2 prefix/local root.</summary>
public sealed record TransferLocation(string Provider, string AccountId, string ContainerId,
    string FolderId, string Path, string DisplayName);

public enum TransferOperation { Copy, Move }
public enum TransferConflictPolicy { Fail, Skip, Replace, Rename }
public enum TransferJobState { Discovering, Running, Paused, Cancelled, Attention, Completed }
public enum TransferItemState { Queued, Transferring, Verifying, Verified, DeletingSource, Completed, Skipped, Attention }

/// <summary>Saved source identity and version. RelativePath is relative to the selected folder.</summary>
public sealed record TransferEntry(string Id, string RelativePath, string Version, long Size,
    DateTimeOffset ModifiedUtc, string? Sha1 = null, bool IsFolder = false);
public sealed record TransferDiscoveryPage(IReadOnlyList<TransferEntry> Entries, string? NextCursor);
public sealed record TransferFolder(string Id, string Name, string Path);
public sealed record TransferFolderPage(IReadOnlyList<TransferFolder> Folders, string? NextCursor);

/// <summary>May contain a preauthenticated upload URL. Protect this record at rest.</summary>
public sealed record TransferCheckpoint(string Provider, string SessionId, long AcknowledgedBytes,
    IReadOnlyDictionary<string, string>? Data = null);
public sealed record TransferReceipt(string Id, string RelativePath, string Version, long Size,
    string? Sha1, string OperationId, IReadOnlyDictionary<string, string>? Data = null);
public sealed record TransferUploadRequest(string OperationId, string RelativePath, TransferConflictPolicy ConflictPolicy);

/// <summary>Every open produces an independent, replayable, version-checked range. No cloud payload staging.</summary>
public interface ITransferSourceFile
{
    TransferEntry Entry { get; }
    Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default);
    Task ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Shared by local, Graph and B2. Provider adapters own protocol-specific resume and receipts.</summary>
public interface ITransferEndpoint
{
    TransferLocation Location { get; }
    Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default);
    Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default);
    Task<TransferDiscoveryPage> DiscoverAsync(string? cursor, IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default) =>
        DiscoverAsync(cursor, cancellationToken);
    ITransferSourceFile OpenSource(TransferEntry entry);
    Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default);
    Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default);
    /// <summary>May enrich the durable receipt with a digest computed from verified destination bytes.</summary>
    async Task<TransferReceipt> VerifyReceiptAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
    {
        await VerifyAsync(receipt, source, cancellationToken).ConfigureAwait(false);
        return receipt;
    }
    Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default);
    /// <summary>Reconciles an interrupted exact-version deletion; absence must never refer to a replacement at the same path.</summary>
    Task<bool> IsSourceDeletedAsync(TransferEntry entry, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed record TransferJobPlan(string Id, TransferLocation Source, TransferLocation Destination,
    TransferOperation Operation, TransferConflictPolicy ConflictPolicy, IReadOnlyList<string> Exclusions,
    DateTimeOffset CreatedUtc);
public sealed record TransferItemSnapshot(string Id, string RelativePath, TransferItemState State,
    long Bytes, long TotalBytes, double BytesPerSecond, string? Error);
public sealed record TransferJobSnapshot(TransferJobPlan Plan, TransferJobState State, bool DiscoveryComplete,
    long FileCount, long CompletedFiles, long SkippedFiles, long TotalBytes, long TransferredBytes,
    long RemainingBytes, long QueuedFiles, double BytesPerSecond,
    IReadOnlyList<TransferItemSnapshot> Items, string? Error = null);

/// <summary>Encrypt/decrypt checkpoint secrets using the user's OS credential protection.</summary>
public interface ITransferCheckpointProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class TransferConflictException(string message) : IOException(message);
public sealed class TransferSourceChangedException(string message) : IOException(message);
public sealed class TransferSkippedException(string message) : IOException(message);

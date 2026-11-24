namespace CloudInlet.Core.Transfers;

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

namespace CloudBay.Core.Sync;

/// <summary>The file action explicitly selected in a reviewed Windows folder change.</summary>
public enum BackupTransferMode { Copy, Move, None }

/// <summary>A mapping can succeed while Windows retains some originals after a verified move.</summary>
public sealed record BackupTransferOutcome(long RemovedFileCount, long RetainedFileCount, string? RetentionWarning)
{
    public static BackupTransferOutcome NoRemoval { get; } = new(0, 0, null);
}

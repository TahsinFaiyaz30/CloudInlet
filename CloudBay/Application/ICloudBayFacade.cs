namespace CloudBay.Application;

public interface ICloudBayFacade
{
    event EventHandler<CloudBayProgress>? ProgressChanged;
    Task<CloudBaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ConfigureRootAsync(string path, CancellationToken cancellationToken = default);
    Task<OperationResult> RedirectKnownFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<OperationResult> RestoreKnownFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<OperationResult> RestoreUnmanagedKnownFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<FolderRecoveryPreview> PreviewUnmanagedRestoreAsync(string folderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrphanedFolderStatus>> GetOrphanedOneDriveFoldersAsync(CancellationToken cancellationToken = default);
    Task<FolderRecoveryPreview> PreviewOrphanRecoveryAsync(string orphanId, string destinationChoice, CancellationToken cancellationToken = default);
    Task<OperationResult> RecoverOrphanAsync(string orphanId, string destinationChoice, CancellationToken cancellationToken = default);
    Task<OperationResult> LinkFolderAsync(string localPath, string targetName, CancellationToken cancellationToken = default);
    Task<OperationResult> UnlinkFolderAsync(string linkId, CancellationToken cancellationToken = default);
    Task<OperationResult> SaveFilterSettingsAsync(FilterSettings settings, CancellationToken cancellationToken = default);
    Task<OperationResult> SetIncludeDownloadsAsync(bool include, CancellationToken cancellationToken = default);
    Task<OperationResult> SetThemeAsync(string theme, CancellationToken cancellationToken = default);
    Task<OperationResult> RollbackAsync(string journalId, CancellationToken cancellationToken = default);
}

public sealed class CloudBayProgress : EventArgs
{
    public string Operation { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public long BytesProcessed { get; init; }
    public long? TotalBytes { get; init; }
    public int FilesProcessed { get; init; }
}

public sealed record FolderRecoveryPreview(
    string SourcePath,
    string DestinationPath,
    bool CanProceed,
    long TotalFiles,
    long TotalBytes,
    IReadOnlyList<string> Issues);

public sealed record OrphanedFolderStatus(
    string Id,
    string Kind,
    string Path,
    string ActivePath,
    long FileCount,
    long TotalBytes,
    string State,
    string Message);

public sealed record CloudBaySnapshot(
    CloudRootStatus Root,
    IReadOnlyList<KnownFolderStatus> KnownFolders,
    IReadOnlyList<CustomLinkStatus> Links,
    FilterSettings Filters,
    IReadOnlyList<JournalEntrySummary> Journals,
    bool IncludeDownloads,
    string Theme);

public sealed record CloudRootStatus(
    string Path,
    bool IsConfigured,
    bool IsReachable,
    bool CanWrite,
    string Health,
    long? AvailableBytes,
    long? TotalBytes,
    string FileSystem,
    string Message);

public sealed record KnownFolderStatus(
    string Id,
    string DisplayName,
    string CurrentPath,
    string DefaultPath,
    string State,
    string Message,
    bool IsCloudManaged,
    bool IsOptional);

public sealed record CustomLinkStatus(
    string Id,
    string LocalPath,
    string CloudPath,
    string Kind,
    string State);

public sealed class FilterSettings
{
    public bool Git { get; set; } = true;
    public bool MountainDuck { get; set; } = true;
    public bool Cyberduck { get; set; } = true;
    public bool Rclone { get; set; } = true;
    public List<string> EnabledNames { get; set; } =
    [
        "node_modules", ".pnpm", ".next", ".turbo", ".cache",
        "__pycache__", ".venv", "venv", "target", "bin", "obj"
    ];
    public List<string> CustomPatterns { get; set; } = [];
}

public sealed record JournalEntrySummary(
    string Id,
    DateTimeOffset Timestamp,
    string Operation,
    string State,
    string Source,
    string? Destination,
    string? Message,
    bool CanRollback);

public sealed record OperationResult(bool Success, string Message, string? JournalId = null)
{
    public static OperationResult Ok(string message, Guid? journalId = null) =>
        new(true, message, journalId?.ToString("D"));

    public static OperationResult Error(string message, Guid? journalId = null) =>
        new(false, message, journalId?.ToString("D"));
}

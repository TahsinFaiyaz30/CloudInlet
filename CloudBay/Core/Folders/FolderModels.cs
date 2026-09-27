namespace CloudBay.Core.Folders;

public enum KnownFolderKind
{
    Desktop,
    Documents,
    Pictures,
    Videos,
    Music,
    Downloads
}

public sealed record KnownFolderInfo(
    KnownFolderKind Kind,
    string DisplayName,
    string CurrentPath,
    string DefaultLocalPath,
    KnownFolderState State,
    bool Exists,
    string Message);

public sealed record FolderPreflightResult(
    bool CanProceed,
    bool IsNoOp,
    string SourcePath,
    string DestinationPath,
    long TotalFiles,
    long TotalBytes,
    IReadOnlyList<string> Issues);

public sealed record FolderOperationProgress(
    string Phase,
    long FilesCompleted,
    long TotalFiles,
    long BytesCopied,
    long TotalBytes,
    string? CurrentItem);

public sealed record FolderOperationResult(
    bool Success,
    string Message,
    Guid? JournalId,
    string CurrentPath,
    long CopiedFileCount,
    long CopiedBytes);

public interface IKnownFolderService
{
    Task<IReadOnlyList<KnownFolderInfo>> GetFoldersAsync(
        string? cloudRoot = null,
        CancellationToken cancellationToken = default);

    Task<KnownFolderInfo> GetFolderAsync(
        KnownFolderKind kind,
        string? cloudRoot = null,
        CancellationToken cancellationToken = default);

    Task<UserShellFolderDiagnostic> GetRegistryDiagnosticAsync(
        KnownFolderKind kind,
        CancellationToken cancellationToken = default);

    Task<FolderPreflightResult> PreflightRedirectAsync(
        KnownFolderKind kind,
        string cloudRoot,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null);

    Task<FolderOperationResult> RedirectAsync(
        KnownFolderKind kind,
        string cloudRoot,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FolderPreflightResult> PreflightRestoreAsync(
        KnownFolderKind kind,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null);

    Task<FolderPreflightResult> PreflightUnmanagedRestoreAsync(
        KnownFolderKind kind,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null);

    Task<FolderOperationResult> RestoreAsync(
        KnownFolderKind kind,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicit recovery path for an external redirect, including OneDrive.
    /// The caller must present the preflight findings to the user first.
    /// </summary>
    Task<FolderOperationResult> RestoreUnmanagedAsync(
        KnownFolderKind kind,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FolderOperationResult> RollbackAsync(Guid journalId, CancellationToken cancellationToken = default);
}

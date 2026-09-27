using System.Security.Cryptography;
using System.Text;
using CloudBay.Core.Safety;

namespace CloudBay.Core.Folders;

public sealed record OneDriveOrphanInfo(
    string Id,
    KnownFolderKind Kind,
    string SourcePath,
    string ActivePath,
    long TotalFiles,
    long TotalBytes,
    string Message);

public interface IOneDriveRecoveryService
{
    Task<IReadOnlyList<OneDriveOrphanInfo>> GetOrphansAsync(
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null);

    Task<FolderPreflightResult> PreviewRecoveryAsync(
        string orphanId,
        string destinationRoot,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FolderOperationResult> RecoverAsync(
        string orphanId,
        string destinationRoot,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds possible left-behind OneDrive known-folder trees. Copying is explicit and
/// never changes shell mappings or removes the source. OneDrive sync connection
/// state cannot be inferred reliably from a directory alone, so results are
/// presented as candidates for user review.
/// </summary>
public sealed class OneDriveRecoveryService : IOneDriveRecoveryService
{
    public const string RecoverOperation = "OneDrive.Recover";

    private static readonly KnownFolderKind[] Kinds =
    [
        KnownFolderKind.Desktop,
        KnownFolderKind.Documents,
        KnownFolderKind.Pictures,
        KnownFolderKind.Music,
        KnownFolderKind.Videos,
        KnownFolderKind.Downloads
    ];

    private readonly IOperationJournal _journal;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public OneDriveRecoveryService(IOperationJournal journal) =>
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    public Task<IReadOnlyList<OneDriveOrphanInfo>> GetOrphansAsync(
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null) =>
        Task.Run(() => FindCandidates(cancellationToken, progress), cancellationToken);

    public async Task<FolderPreflightResult> PreviewRecoveryAsync(
        string orphanId, string destinationRoot,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orphanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return await Task.Run(() => BuildPlan(orphanId, destinationRoot, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FolderOperationResult> RecoverAsync(
        string orphanId, string destinationRoot,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FolderPreflightResult plan = await PreviewRecoveryAsync(orphanId, destinationRoot, progress,
                cancellationToken).ConfigureAwait(false);
            if (plan.IsNoOp || !plan.CanProceed)
                return new FolderOperationResult(false, string.Join(Environment.NewLine, plan.Issues),
                    null, plan.SourcePath, 0, 0);
            OneDriveOrphanInfo candidate = FindCandidates(cancellationToken)
                .FirstOrDefault(item => string.Equals(item.Id, orphanId, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("The OneDrive recovery candidate is no longer available.");
            if (FolderTransfer.PathEquals(candidate.ActivePath, candidate.SourcePath))
                return new FolderOperationResult(false, "This folder is now active in Windows; review its path again.",
                    null, candidate.ActivePath, 0, 0);

            var metadata = new Dictionary<string, string>
            {
                ["FolderKind"] = candidate.Kind.ToString(),
                ["ActiveShellPathBefore"] = candidate.ActivePath,
                ["SourceRetained"] = "true",
                ["DestinationExisted"] = Directory.Exists(plan.DestinationPath).ToString(),
                ["RollbackPolicy"] = "No files are deleted; recovery can be resumed after conflict review."
            };
            JournalEntry entry = await _journal.BeginAsync(RecoverOperation, plan.SourcePath,
                plan.DestinationPath, metadata, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new FolderOperationProgress("Preparing recovery destination", 0,
                    plan.TotalFiles, 0, plan.TotalBytes, null));
                Directory.CreateDirectory(plan.DestinationPath);
                var copied = await FolderTransfer.CopyAsync(plan, progress, cancellationToken).ConfigureAwait(false);
                metadata["CopiedFiles"] = copied.Files.ToString();
                metadata["CopiedBytes"] = copied.Bytes.ToString();
                await _journal.CompleteAsync(entry.Id, metadata, CancellationToken.None).ConfigureAwait(false);
                return new FolderOperationResult(true,
                    $"Recovered {copied.Files} files into {plan.DestinationPath}. The OneDrive source was retained.",
                    entry.Id, candidate.ActivePath, copied.Files, copied.Bytes);
            }
            catch (Exception ex)
            {
                string error = ex is OperationCanceledException ? "Recovery was cancelled." : ex.Message;
                try { await _journal.FailAsync(entry.Id, error, metadata, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception journalError) { error += $" Journal update failed: {journalError.Message}"; }
                return new FolderOperationResult(false,
                    $"{error} Source files were retained; completed destination copies remain in place.",
                    entry.Id, candidate.ActivePath, 0, 0);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static FolderPreflightResult BuildPlan(
        string orphanId, string destinationRoot,
        IProgress<FolderOperationProgress>? progress, CancellationToken cancellationToken)
    {
        OneDriveOrphanInfo candidate = FindCandidates(cancellationToken)
            .FirstOrDefault(item => string.Equals(item.Id, orphanId, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("The OneDrive recovery candidate is no longer available.");
        if (!Path.IsPathFullyQualified(destinationRoot))
            return new FolderPreflightResult(false, false, candidate.SourcePath, destinationRoot,
                0, 0, ["The recovery destination root must be an absolute path."]);
        string root = FolderTransfer.Normalize(destinationRoot);
        string destination = Path.Combine(root, candidate.Kind.ToString());
        if (IsUnderProfileOneDrive(root))
            return new FolderPreflightResult(false, false, candidate.SourcePath, destination,
                0, 0, ["Choose a local profile or cloud root outside OneDrive for recovery."]);
        return FolderTransfer.Scan(candidate.SourcePath, destination, root, cancellationToken, progress);
    }

    private static IReadOnlyList<OneDriveOrphanInfo> FindCandidates(
        CancellationToken cancellationToken, IProgress<FolderOperationProgress>? progress = null)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) || !Directory.Exists(profile))
            return Array.Empty<OneDriveOrphanInfo>();

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateDirectories(profile, "OneDrive*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsProfileOneDriveRoot(path)) roots.Add(FolderTransfer.Normalize(path));
        }
        foreach (string name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? path = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                IsProfileOneDriveRoot(path) && Directory.Exists(path))
                roots.Add(FolderTransfer.Normalize(path));
        }

        var activePaths = Kinds.ToDictionary(kind => kind,
            kind => FolderTransfer.Normalize(NativeKnownFolders.GetCurrentPath(kind)));
        var candidates = new List<OneDriveOrphanInfo>();
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (KnownFolderKind kind in Kinds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = Path.Combine(root, kind.ToString());
                if (!Directory.Exists(source) ||
                    (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
                    FolderTransfer.PathEquals(source, activePaths[kind])) continue;
                (long files, long bytes, string? scanIssue) = CountUserFiles(source, cancellationToken, progress);
                if (files == 0 && scanIssue is null) continue;
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    kind + "|" + FolderTransfer.Normalize(source).ToUpperInvariant())));
                string message = scanIssue is null
                    ? "Possible left-behind OneDrive folder. Review its files and sync state before recovery."
                    : $"Possible left-behind OneDrive folder. The scan needs attention: {scanIssue}";
                candidates.Add(new OneDriveOrphanInfo(id, kind, source, activePaths[kind],
                    files, bytes, message));
            }
        }
        return candidates.OrderBy(item => item.Kind).ThenBy(item => item.SourcePath,
            StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (long Files, long Bytes, string? Issue) CountUserFiles(
        string source, CancellationToken cancellationToken,
        IProgress<FolderOperationProgress>? progress)
    {
        long files = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(source);
        try
        {
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            return (files, bytes, $"A linked directory needs review: {path}");
                        pending.Push(path);
                    }
                    else if (!Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        files++;
                        bytes = checked(bytes + new FileInfo(path).Length);
                        if (files % 32 == 0)
                            progress?.Report(new FolderOperationProgress("Scanning OneDrive folders",
                                files, 0, 0, bytes, path));
                    }
                }
            }
            progress?.Report(new FolderOperationProgress("Scanning OneDrive folders",
                files, files, 0, bytes, source));
            return (files, bytes, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or OverflowException)
        {
            return (files, bytes, ex.Message);
        }
    }

    private static bool IsProfileOneDriveRoot(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(path)) return false;
        string? parent = Path.GetDirectoryName(FolderTransfer.Normalize(path));
        if (parent is null || !FolderTransfer.PathEquals(parent, profile)) return false;
        string name = Path.GetFileName(FolderTransfer.Normalize(path));
        return name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderProfileOneDrive(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) || !FolderTransfer.IsWithin(profile, path)) return false;
        string relative = Path.GetRelativePath(profile, path);
        string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase);
    }
}

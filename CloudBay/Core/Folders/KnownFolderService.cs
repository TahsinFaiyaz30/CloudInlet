using CloudBay.Core.Safety;
using System.Runtime.InteropServices;

namespace CloudBay.Core.Folders;

/// <summary>
/// Changes per-user shell locations only after copying the source tree. The
/// previous tree and all destination copies are retained on restore/rollback.
/// </summary>
public sealed class KnownFolderService : IKnownFolderService
{
    public const string RedirectOperation = "KnownFolder.Redirect";
    public const string RestoreOperation = "KnownFolder.Restore";
    public const string RollbackOperation = "KnownFolder.Rollback";

    private static readonly KnownFolderKind[] SupportedKinds =
    [
        KnownFolderKind.Desktop,
        KnownFolderKind.Documents,
        KnownFolderKind.Pictures,
        KnownFolderKind.Videos,
        KnownFolderKind.Music,
        KnownFolderKind.Downloads
    ];

    private readonly IOperationJournal _journal;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public KnownFolderService(IOperationJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async Task<IReadOnlyList<KnownFolderInfo>> GetFoldersAsync(
        string? cloudRoot = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ListAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var folders = new List<KnownFolderInfo>(SupportedKinds.Length);
            foreach (KnownFolderKind kind in SupportedKinds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                folders.Add(ReadInfo(kind, cloudRoot, entries));
            }
            return (IReadOnlyList<KnownFolderInfo>)folders;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnownFolderInfo> GetFolderAsync(
        KnownFolderKind kind,
        string? cloudRoot = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ListAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ReadInfo(kind, cloudRoot, entries), cancellationToken).ConfigureAwait(false);
    }

    public Task<UserShellFolderDiagnostic> GetRegistryDiagnosticAsync(
        KnownFolderKind kind, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UserShellFolderRegistry.Read(kind, NativeKnownFolders.GetCurrentPath(kind));
        }, cancellationToken);

    public async Task<FolderPreflightResult> PreflightRedirectAsync(
        KnownFolderKind kind,
        string cloudRoot,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cloudRoot);
        return await Task.Run(() => BuildRedirectPlan(kind, cloudRoot, cancellationToken, progress), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<FolderOperationResult> RedirectAsync(
        KnownFolderKind kind,
        string cloudRoot,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(kind, RedirectOperation, cloudRoot, progress, allowUnmanagedRestore: false, cancellationToken);

    public Task<FolderPreflightResult> PreflightRestoreAsync(
        KnownFolderKind kind,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null) =>
        BuildRestorePlanAsync(kind, allowUnmanaged: false, cancellationToken, progress);

    public Task<FolderPreflightResult> PreflightUnmanagedRestoreAsync(
        KnownFolderKind kind,
        CancellationToken cancellationToken = default,
        IProgress<FolderOperationProgress>? progress = null) =>
        BuildRestorePlanAsync(kind, allowUnmanaged: true, cancellationToken, progress);

    public Task<FolderOperationResult> RestoreAsync(
        KnownFolderKind kind,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(kind, RestoreOperation, null, progress, allowUnmanagedRestore: false, cancellationToken);

    public Task<FolderOperationResult> RestoreUnmanagedAsync(
        KnownFolderKind kind,
        IProgress<FolderOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(kind, RestoreOperation, null, progress, allowUnmanagedRestore: true, cancellationToken);

    public async Task<FolderOperationResult> RollbackAsync(Guid journalId, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JournalEntry original = await _journal.GetAsync(journalId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Journal entry {journalId} was not found.");
            if (original.Operation is not (RedirectOperation or RestoreOperation))
                return new FolderOperationResult(false, "This operation does not change a known folder path.",
                    journalId, string.Empty, 0, 0);
            if (original.State == JournalState.RolledBack)
                return new FolderOperationResult(true, "The operation is already rolled back.",
                    journalId, original.SourcePath ?? string.Empty, 0, 0);
            if (!TryGetKind(original, out KnownFolderKind kind) ||
                string.IsNullOrWhiteSpace(original.SourcePath) ||
                string.IsNullOrWhiteSpace(original.DestinationPath))
                return new FolderOperationResult(false, "The journal does not contain a complete rollback path.",
                    journalId, string.Empty, 0, 0);

            string current = await Task.Run(() => NativeKnownFolders.GetCurrentPath(kind), cancellationToken)
                .ConfigureAwait(false);
            if (FolderTransfer.PathEquals(current, original.SourcePath))
            {
                await _journal.MarkRolledBackAsync(original.Id, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return new FolderOperationResult(true,
                    "The original shell location is already active. Copied files remain at the destination.",
                    original.Id, current, 0, 0);
            }
            if (!FolderTransfer.PathEquals(current, original.DestinationPath))
                return new FolderOperationResult(false,
                    "The known folder path has changed since this operation; rollback would overwrite a newer setting.",
                    original.Id, current, 0, 0);
            if (!Directory.Exists(original.SourcePath))
                return new FolderOperationResult(false, "The previous folder is unavailable; reconnect it before rollback.",
                    original.Id, current, 0, 0);

            JournalEntry rollback = await _journal.BeginAsync(RollbackOperation, current, original.SourcePath,
                new Dictionary<string, string>
                {
                    ["FolderKind"] = kind.ToString(),
                    ["OriginalJournalId"] = original.Id.ToString("D")
                }, cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    string before = NativeKnownFolders.GetCurrentPath(kind);
                    if (!FolderTransfer.PathEquals(before, original.DestinationPath))
                        throw new IOException("The shell path changed during rollback.");
                    NativeKnownFolders.SetPath(kind, original.SourcePath);
                    VerifyNativePath(kind, original.SourcePath);
                    NativeKnownFolders.NotifyChanged(before, original.SourcePath);
                }, CancellationToken.None).ConfigureAwait(false);
                await _journal.CompleteAsync(rollback.Id, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                await _journal.MarkRolledBackAsync(original.Id, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return new FolderOperationResult(true,
                    "The previous shell location was restored. Files at both locations were kept.",
                    rollback.Id, original.SourcePath, 0, 0);
            }
            catch (Exception ex)
            {
                string livePath = await Task.Run(() => NativeKnownFolders.GetCurrentPath(kind)).ConfigureAwait(false);
                string message = ex.Message;
                if (FolderTransfer.PathEquals(livePath, original.SourcePath))
                {
                    // The shell change succeeded even if a later journal write failed.
                    message += " The previous path is active; inspect the journal before retrying.";
                }
                try { await _journal.FailAsync(rollback.Id, message, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception journalError) { message += $" Journal update failed: {journalError.Message}"; }
                return new FolderOperationResult(false, message, rollback.Id, livePath, 0, 0);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<FolderPreflightResult> BuildRestorePlanAsync(
        KnownFolderKind kind, bool allowUnmanaged, CancellationToken cancellationToken,
        IProgress<FolderOperationProgress>? progress = null)
    {
        var entries = await _journal.ListAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => BuildRestorePlan(kind, entries, allowUnmanaged, cancellationToken, progress), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FolderOperationResult> ExecuteAsync(
        KnownFolderKind kind,
        string operation,
        string? cloudRoot,
        IProgress<FolderOperationProgress>? progress,
        bool allowUnmanagedRestore,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FolderPreflightResult plan = operation == RedirectOperation
                ? await PreflightRedirectAsync(kind, cloudRoot!, cancellationToken, progress).ConfigureAwait(false)
                : await BuildRestorePlanAsync(kind, allowUnmanagedRestore, cancellationToken, progress).ConfigureAwait(false);
            if (!plan.CanProceed)
                return new FolderOperationResult(false, string.Join(Environment.NewLine, plan.Issues),
                    null, plan.SourcePath, 0, 0);
            if (plan.IsNoOp)
                return new FolderOperationResult(true, "The known folder already uses this location.",
                    null, plan.SourcePath, 0, 0);

            var metadata = new Dictionary<string, string>
            {
                ["FolderKind"] = kind.ToString(),
                ["Action"] = operation == RedirectOperation ? "Redirect" : "Restore",
                ["SourceRetained"] = "true",
                ["DestinationExisted"] = Directory.Exists(plan.DestinationPath).ToString()
            };
            JournalEntry entry = await _journal.BeginAsync(operation, plan.SourcePath, plan.DestinationPath,
                metadata, cancellationToken).ConfigureAwait(false);
            long copiedFiles = 0;
            long copiedBytes = 0;
            bool shellChanged = false;
            try
            {
                progress?.Report(new FolderOperationProgress("Preparing destination", 0, plan.TotalFiles,
                    0, plan.TotalBytes, null));
                string livePath = await Task.Run(() => NativeKnownFolders.GetCurrentPath(kind), cancellationToken)
                    .ConfigureAwait(false);
                if (!FolderTransfer.PathEquals(livePath, plan.SourcePath))
                    throw new IOException("The known folder location changed after preflight. Run the check again.");

                Directory.CreateDirectory(plan.DestinationPath);
                ProbeDestination(plan.DestinationPath);
                (copiedFiles, copiedBytes) = await FolderTransfer.CopyAsync(plan, progress, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new FolderOperationProgress("Applying Windows folder appearance", copiedFiles,
                    plan.TotalFiles, copiedBytes, plan.TotalBytes, null));
                FolderAppearance.Result appearance = await Task.Run(
                    () => FolderAppearance.Apply(kind, plan.DestinationPath), cancellationToken).ConfigureAwait(false);
                metadata["CreatedDesktopIni"] = appearance.CreatedDesktopIni.ToString();
                metadata["OriginalDestinationAttributes"] = ((int)appearance.OriginalFolderAttributes).ToString();
                if (appearance.Warning is not null) metadata["AppearanceWarning"] = appearance.Warning;

                progress?.Report(new FolderOperationProgress("Updating Windows", copiedFiles,
                    plan.TotalFiles, copiedBytes, plan.TotalBytes, null));
                await Task.Run(() =>
                {
                    string before = NativeKnownFolders.GetCurrentPath(kind);
                    if (!FolderTransfer.PathEquals(before, plan.SourcePath))
                        throw new IOException("The known folder location changed during migration.");
                    NativeKnownFolders.SetPath(kind, plan.DestinationPath);
                    shellChanged = true;
                    VerifyNativePath(kind, plan.DestinationPath);
                    NativeKnownFolders.InitializeFolder(kind);
                    NativeKnownFolders.NotifyChanged(plan.SourcePath, plan.DestinationPath);
                }, CancellationToken.None).ConfigureAwait(false);

                metadata["CopiedFiles"] = copiedFiles.ToString();
                metadata["CopiedBytes"] = copiedBytes.ToString();
                await _journal.CompleteAsync(entry.Id, metadata, CancellationToken.None).ConfigureAwait(false);
                string message = appearance.Warning is null
                    ? $"{kind} now uses {plan.DestinationPath}. {copiedFiles} files were copied; originals remain at {plan.SourcePath}."
                    : $"{kind} now uses {plan.DestinationPath}. {copiedFiles} files were copied; originals remain at {plan.SourcePath}. {appearance.Warning}";
                return new FolderOperationResult(true, message, entry.Id, plan.DestinationPath,
                    copiedFiles, copiedBytes);
            }
            catch (Exception ex)
            {
                string error = ex is OperationCanceledException ? "The operation was cancelled." : ex.Message;
                if (shellChanged)
                {
                    try
                    {
                        await Task.Run(() =>
                        {
                            string current = NativeKnownFolders.GetCurrentPath(kind);
                            if (FolderTransfer.PathEquals(current, plan.DestinationPath))
                            {
                                NativeKnownFolders.SetPath(kind, plan.SourcePath);
                                VerifyNativePath(kind, plan.SourcePath);
                                NativeKnownFolders.NotifyChanged(plan.DestinationPath, plan.SourcePath);
                            }
                            else if (!FolderTransfer.PathEquals(current, plan.SourcePath))
                            {
                                throw new IOException("The shell path changed again; automatic rollback was stopped.");
                            }
                        }, CancellationToken.None).ConfigureAwait(false);
                        metadata["ShellRollback"] = "Succeeded";
                    }
                    catch (Exception rollbackError)
                    {
                        metadata["ShellRollback"] = "Failed";
                        error += $" Automatic shell rollback failed: {rollbackError.Message}";
                    }
                }
                metadata["CopiedFiles"] = copiedFiles.ToString();
                metadata["CopiedBytes"] = copiedBytes.ToString();
                try { await _journal.FailAsync(entry.Id, error, metadata, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception journalError) { error += $" Journal update failed: {journalError.Message}"; }
                string currentPath;
                try { currentPath = await Task.Run(() => NativeKnownFolders.GetCurrentPath(kind)).ConfigureAwait(false); }
                catch { currentPath = plan.SourcePath; }
                return new FolderOperationResult(false,
                    $"{error} Source files were retained; any completed copies remain at the destination.",
                    entry.Id, currentPath, copiedFiles, copiedBytes);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static FolderPreflightResult BuildRedirectPlan(
        KnownFolderKind kind, string cloudRoot, CancellationToken cancellationToken,
        IProgress<FolderOperationProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = FolderTransfer.Normalize(cloudRoot);
        string source = FolderTransfer.Normalize(NativeKnownFolders.GetCurrentPath(kind));
        string destination = FolderTransfer.Normalize(Path.Combine(root, kind.ToString()));
        FolderPreflightResult plan = FolderTransfer.Scan(source, destination, root, cancellationToken, progress);
        var issues = new List<string>(plan.Issues);
        foreach (KnownFolderKind otherKind in SupportedKinds)
        {
            if (otherKind == kind) continue;
            string other = FolderTransfer.Normalize(NativeKnownFolders.GetCurrentPath(otherKind));
            if (FolderTransfer.IsWithin(destination, other))
                issues.Add($"The destination contains the {otherKind} known folder: {other}");
        }

        return plan with { CanProceed = issues.Count == 0, Issues = issues };
    }

    private static FolderPreflightResult BuildRestorePlan(
        KnownFolderKind kind,
        IReadOnlyList<JournalEntry> entries,
        bool allowUnmanaged,
        CancellationToken cancellationToken,
        IProgress<FolderOperationProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = FolderTransfer.Normalize(NativeKnownFolders.GetCurrentPath(kind));
        string destination = FolderTransfer.Normalize(NativeKnownFolders.GetDefaultPath(kind));
        FolderPreflightResult plan = FolderTransfer.Scan(source, destination, cancellationToken: cancellationToken,
            progress: progress);
        if (!KnownFolderDecision.RequiresExplicitRecovery(plan.IsNoOp,
                IsManagedRedirect(kind, source, entries), allowUnmanaged)) return plan;
        var issues = new List<string>(plan.Issues)
        {
            "This location was redirected outside CloudBay. Review it and explicitly choose external redirect recovery before changing it."
        };
        return plan with { CanProceed = false, Issues = issues };
    }

    private static KnownFolderInfo ReadInfo(
        KnownFolderKind kind,
        string? cloudRoot,
        IReadOnlyList<JournalEntry> entries)
    {
        string localDefault;
        try { localDefault = FolderTransfer.Normalize(NativeKnownFolders.GetDefaultPath(kind)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Display-only fallback. A mutation still requires the native shell API.
            localDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                kind.ToString());
        }
        string current;
        try { current = FolderTransfer.Normalize(NativeKnownFolders.GetCurrentPath(kind)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            UserShellFolderDiagnostic fallback = UserShellFolderRegistry.Read(kind, string.Empty);
            return new KnownFolderInfo(kind, kind.ToString(), fallback.ExpandedPath ?? string.Empty,
                localDefault, KnownFolderState.BrokenRedirect, false,
                $"Windows could not read this known folder: {ex.Message} Registry diagnostic: {fallback.Message}");
        }
        bool exists = Directory.Exists(current);
        bool managed = IsManagedRedirect(kind, current, entries) &&
            (string.IsNullOrWhiteSpace(cloudRoot) || FolderTransfer.IsWithin(cloudRoot, current));
        KnownFolderState state = KnownFolderDecision.Classify(current, localDefault, exists, managed,
            IsOneDrivePath(current));
        string message = state switch
        {
            KnownFolderState.BrokenRedirect =>
                "The configured folder is unavailable. Reconnect it before migrating or restoring files.",
            KnownFolderState.LocalDefault => "Using the local Windows profile folder.",
            KnownFolderState.CloudManaged => "Redirected by CloudBay. The previous location is preserved.",
            KnownFolderState.LegacyOneDrive => "Redirected into OneDrive outside CloudBay.",
            _ => "Redirected outside the local default; its previous configuration is not managed by CloudBay."
        };
        UserShellFolderDiagnostic registry = UserShellFolderRegistry.Read(kind, current);
        if (registry.State is UserShellFolderDiagnosticState.Mismatch or UserShellFolderDiagnosticState.Invalid)
            message += " " + registry.Message;
        return new KnownFolderInfo(kind, kind.ToString(), current, localDefault, state, exists, message);
    }

    private static bool IsManagedRedirect(KnownFolderKind kind, string currentPath, IReadOnlyList<JournalEntry> entries)
    {
        JournalEntry? latest = entries
            .Where(entry => entry.State == JournalState.Completed &&
                            entry.Operation is RedirectOperation or RestoreOperation &&
                            TryGetKind(entry, out KnownFolderKind entryKind) && entryKind == kind)
            .OrderByDescending(entry => entry.StartedAtUtc)
            .FirstOrDefault();
        return latest?.Operation == RedirectOperation && latest.DestinationPath is not null &&
               FolderTransfer.PathEquals(latest.DestinationPath, currentPath);
    }

    private static bool TryGetKind(JournalEntry entry, out KnownFolderKind kind)
    {
        kind = default;
        return entry.Metadata.TryGetValue("FolderKind", out string? value) &&
               Enum.TryParse(value, ignoreCase: false, out kind) &&
               Enum.IsDefined(kind);
    }

    private static bool IsOneDrivePath(string path)
    {
        foreach (string key in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            string? configured = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) &&
                FolderTransfer.IsWithin(configured, path)) return true;
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) || !FolderTransfer.IsWithin(profile, path)) return false;
        string relative = Path.GetRelativePath(profile, path);
        string firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ||
               firstSegment.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase);
    }

    private static void ProbeDestination(string destination)
    {
        string probe = Path.Combine(destination, $".cloudbay-probe-{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.WriteThrough))
        {
            stream.Flush(flushToDisk: true);
        }
        File.Delete(probe);
    }

    private static void VerifyNativePath(KnownFolderKind kind, string expected)
    {
        string actual = NativeKnownFolders.GetCurrentPath(kind);
        if (!FolderTransfer.PathEquals(actual, expected))
            throw new IOException($"Windows reported {actual} after changing {kind}; expected {expected}.");
        UserShellFolderDiagnostic registry = UserShellFolderRegistry.Read(kind, actual);
        if (registry.State is UserShellFolderDiagnosticState.Mismatch or UserShellFolderDiagnosticState.Invalid)
            throw new IOException(registry.Message);
    }
}

using System.Text.Json;
using CloudBay.Core.Filters;
using CloudBay.Core.Folders;
using CloudBay.Core.Links;
using CloudBay.Core.Safety;

namespace CloudBay.Application;

public sealed class CloudBayFacade : ICloudBayFacade
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly PreferencesStore _preferences;
    private readonly CloudRootProbe _rootProbe;
    private readonly IKnownFolderService _folders;
    private readonly IOneDriveRecoveryService _orphanRecovery;
    private readonly FolderLinkService _links;
    private readonly DeveloperFilterService _filters;
    private readonly IOperationJournal _journal;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public event EventHandler<CloudBayProgress>? ProgressChanged;

    public CloudBayFacade(PreferencesStore preferences, CloudRootProbe rootProbe,
        IKnownFolderService folders, IOneDriveRecoveryService orphanRecovery,
        FolderLinkService links, DeveloperFilterService filters,
        IOperationJournal journal)
    {
        _preferences = preferences;
        _rootProbe = rootProbe;
        _folders = folders;
        _orphanRecovery = orphanRecovery;
        _links = links;
        _filters = filters;
        _journal = journal;
    }

    public async Task<CloudBaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _preferences.LoadAsync(cancellationToken);
        var rootTask = _rootProbe.InspectAsync(settings.RootPath, cancellationToken: cancellationToken);
        var folderTask = _folders.GetFoldersAsync(settings.RootPath, cancellationToken);
        var linkTask = _links.ListAsync(cancellationToken);
        var journalTask = _journal.ListAsync(cancellationToken);
        await Task.WhenAll(rootTask, folderTask, linkTask, journalTask);

        var folders = (await folderTask).Select(item => new KnownFolderStatus(
            item.Kind.ToString(), item.DisplayName, item.CurrentPath, item.DefaultLocalPath,
            item.State.ToString(), item.Message, item.State == KnownFolderState.CloudManaged,
            item.Kind == KnownFolderKind.Downloads)).ToArray();
        var links = (await linkTask).Select(item => new CustomLinkStatus(
            item.Id.ToString("D"), item.LocalPath, item.CloudPath, "Directory symbolic link",
            Directory.Exists(item.CloudPath) ? "Ready" : "Cloud path unavailable")).ToArray();
        var journals = (await journalTask).Select(item => new JournalEntrySummary(
            item.Id.ToString("D"), item.StartedAtUtc, item.Operation, item.State.ToString(),
            item.SourcePath ?? string.Empty, item.DestinationPath, item.Error,
            item.State == JournalState.Completed && SupportsRollback(item.Operation))).ToArray();
        return new CloudBaySnapshot(await rootTask, folders, links, settings.Filters,
            journals, settings.IncludeDownloads, settings.Theme);
    }

    public Task<OperationResult> ConfigureRootAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return OperationResult.Error("Choose an absolute path to an existing mounted folder.");
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var status = await _rootProbe.InspectAsync(fullPath, forceWriteCheck: true, ct);
            if (!status.IsReachable || !status.CanWrite)
                return OperationResult.Error(status.Message);
            return await SavePreferenceChangeAsync("settings.root", preferences => preferences.RootPath = fullPath, ct);
        }, cancellationToken);

    public Task<OperationResult> RedirectKnownFolderAsync(string folderId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            if (!TryParseFolder(folderId, out var kind)) return OperationResult.Error("Unknown Windows folder.");
            var settings = await _preferences.LoadAsync(ct);
            if (kind == KnownFolderKind.Downloads && !settings.IncludeDownloads)
                return OperationResult.Error("Enable Downloads redirection in Settings first. Downloads stays local by default.");
            var root = await _rootProbe.InspectAsync(settings.RootPath, cancellationToken: ct);
            if (!root.IsReachable || !root.CanWrite)
                return OperationResult.Error(root.Message);
            var progress = new Progress<FolderOperationProgress>(item => ProgressChanged?.Invoke(this,
                new CloudBayProgress
                {
                    Operation = "Redirect " + kind,
                    Message = item.Phase + (item.CurrentItem is null ? string.Empty : ": " + item.CurrentItem),
                    FilesProcessed = checked((int)Math.Min(item.FilesCompleted, int.MaxValue)),
                    BytesProcessed = item.BytesCopied,
                    TotalBytes = item.TotalBytes
                }));
            var result = await _folders.RedirectAsync(kind, root.Path, progress, ct);
            return new OperationResult(result.Success, result.Message, result.JournalId?.ToString("D"));
        }, cancellationToken);

    public Task<OperationResult> RestoreKnownFolderAsync(string folderId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(ct => RestoreFolderCoreAsync(folderId, unmanaged: false, ct), cancellationToken);

    public Task<OperationResult> RestoreUnmanagedKnownFolderAsync(string folderId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(ct => RestoreFolderCoreAsync(folderId, unmanaged: true, ct), cancellationToken);

    public async Task<FolderRecoveryPreview> PreviewUnmanagedRestoreAsync(
        string folderId, CancellationToken cancellationToken = default)
    {
        if (!TryParseFolder(folderId, out var kind))
            throw new ArgumentException("Unknown Windows folder.", nameof(folderId));
        var plan = await _folders.PreflightUnmanagedRestoreAsync(kind, cancellationToken);
        return new FolderRecoveryPreview(plan.SourcePath, plan.DestinationPath,
            plan.CanProceed, plan.TotalFiles, plan.TotalBytes, plan.Issues);
    }

    public async Task<IReadOnlyList<OrphanedFolderStatus>> GetOrphanedOneDriveFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var progress = new Progress<FolderOperationProgress>(item => ProgressChanged?.Invoke(this,
            new CloudBayProgress
            {
                Operation = "Scan OneDrive folders",
                Message = item.Phase + (item.CurrentItem is null ? string.Empty : ": " + item.CurrentItem),
                FilesProcessed = checked((int)Math.Min(item.FilesCompleted, int.MaxValue)),
                BytesProcessed = item.BytesCopied,
                TotalBytes = item.TotalBytes
            }));
        var orphans = await _orphanRecovery.GetOrphansAsync(cancellationToken, progress);
        return orphans.Select(item => new OrphanedFolderStatus(
            item.Id, item.Kind.ToString(), item.SourcePath, item.ActivePath,
            item.TotalFiles, item.TotalBytes, "Recoverable", item.Message)).ToArray();
    }

    public async Task<FolderRecoveryPreview> PreviewOrphanRecoveryAsync(
        string orphanId, string destinationChoice, CancellationToken cancellationToken = default)
    {
        var root = await ResolveRecoveryRootAsync(destinationChoice, cancellationToken);
        var plan = await _orphanRecovery.PreviewRecoveryAsync(orphanId, root,
            progress: null, cancellationToken);
        return new FolderRecoveryPreview(plan.SourcePath, plan.DestinationPath,
            plan.CanProceed, plan.TotalFiles, plan.TotalBytes, plan.Issues);
    }

    public Task<OperationResult> RecoverOrphanAsync(
        string orphanId, string destinationChoice, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            var root = await ResolveRecoveryRootAsync(destinationChoice, ct);
            var progress = new Progress<FolderOperationProgress>(item => ProgressChanged?.Invoke(this,
                new CloudBayProgress
                {
                    Operation = "Recover OneDrive files",
                    Message = item.Phase + (item.CurrentItem is null ? string.Empty : ": " + item.CurrentItem),
                    FilesProcessed = checked((int)Math.Min(item.FilesCompleted, int.MaxValue)),
                    BytesProcessed = item.BytesCopied,
                    TotalBytes = item.TotalBytes
                }));
            var result = await _orphanRecovery.RecoverAsync(orphanId, root, progress, ct);
            return new OperationResult(result.Success, result.Message, result.JournalId?.ToString("D"));
        }, cancellationToken);

    private async Task<string> ResolveRecoveryRootAsync(string destinationChoice, CancellationToken ct)
    {
        if (destinationChoice.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile) || !Directory.Exists(profile))
                throw new DirectoryNotFoundException("The local Windows profile is unavailable.");
            return profile;
        }
        if (destinationChoice.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            var settings = await _preferences.LoadAsync(ct);
            var root = await _rootProbe.InspectAsync(settings.RootPath, cancellationToken: ct);
            if (!root.IsReachable || !root.CanWrite)
                throw new IOException(root.Message);
            return root.Path;
        }
        throw new ArgumentException("Choose Local or Cloud as the recovery destination.", nameof(destinationChoice));
    }

    private async Task<OperationResult> RestoreFolderCoreAsync(string folderId, bool unmanaged, CancellationToken ct)
    {
        if (!TryParseFolder(folderId, out var kind)) return OperationResult.Error("Unknown Windows folder.");
        var progress = new Progress<FolderOperationProgress>(item => ProgressChanged?.Invoke(this,
            new CloudBayProgress
            {
                Operation = "Restore " + kind,
                Message = item.Phase + (item.CurrentItem is null ? string.Empty : ": " + item.CurrentItem),
                FilesProcessed = checked((int)Math.Min(item.FilesCompleted, int.MaxValue)),
                BytesProcessed = item.BytesCopied,
                TotalBytes = item.TotalBytes
            }));
        var result = unmanaged
            ? await _folders.RestoreUnmanagedAsync(kind, progress, ct)
            : await _folders.RestoreAsync(kind, progress, ct);
        return new OperationResult(result.Success, result.Message, result.JournalId?.ToString("D"));
    }

    public Task<OperationResult> LinkFolderAsync(string localPath, string targetName, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            var settings = await _preferences.LoadAsync(ct);
            var root = await _rootProbe.InspectAsync(settings.RootPath, cancellationToken: ct);
            if (!root.IsReachable || !root.CanWrite) return OperationResult.Error(root.Message);
            var progress = new Progress<LinkProgress>(item => ProgressChanged?.Invoke(this,
                new CloudBayProgress
                {
                    Operation = "Link project folder",
                    Message = $"Copying and verifying {item.FilesCopied} of {item.TotalFiles} files",
                    FilesProcessed = checked((int)Math.Min(item.FilesCopied, int.MaxValue)),
                    BytesProcessed = item.BytesCopied,
                    TotalBytes = item.TotalBytes
                }));
            var link = await _links.LinkAsync(localPath, root.Path, targetName, progress, ct);
            var message = $"Linked {link.LocalPath} to {link.CloudPath}. A local backup is retained at {link.LocalBackupPath}.";
            if (link.JournalWarning is not null) message += " " + link.JournalWarning;
            return OperationResult.Ok(message, link.JournalId);
        }, cancellationToken);

    public Task<OperationResult> UnlinkFolderAsync(string linkId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            if (!Guid.TryParse(linkId, out var id)) return OperationResult.Error("Invalid link ID.");
            var result = await _links.UnlinkAsync(id, ct);
            return new OperationResult(result.RestoredLocalPath, result.Message, result.JournalId?.ToString("D"));
        }, cancellationToken);

    public Task<OperationResult> SaveFilterSettingsAsync(FilterSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(settings);
            var selection = DeveloperFilterService.ValidateSelection(settings.EnabledNames);
            var customPatterns = DeveloperFilterService.ValidateCustomPatterns(settings.CustomPatterns);
            var preferences = await _preferences.LoadAsync(ct);
            var root = await _rootProbe.InspectAsync(preferences.RootPath, cancellationToken: ct);
            if (!root.IsReachable || !root.CanWrite) return OperationResult.Error(root.Message);
            var updated = Clone(preferences);
            updated.Filters = new FilterSettings
            {
                Git = settings.Git,
                MountainDuck = settings.MountainDuck,
                Cyberduck = settings.Cyberduck,
                Rclone = settings.Rclone,
                EnabledNames = selection.ToList()
            };
            updated.Filters.CustomPatterns = customPatterns.ToList();
            var beforeJson = JsonSerializer.Serialize(preferences, JsonOptions);
            var afterJson = JsonSerializer.Serialize(updated, JsonOptions);
            var entry = await _journal.BeginAsync("settings.filters", root.Path, root.Path,
                new Dictionary<string, string> { ["Before"] = beforeJson, ["After"] = afterJson }, ct);
            IReadOnlyList<Guid> childIds = [];
            try
            {
                FilterTargets targets = FilterTargets.None;
                if (settings.Git) targets |= FilterTargets.Git;
                if (settings.MountainDuck) targets |= FilterTargets.MountainDuck;
                if (settings.Cyberduck) targets |= FilterTargets.Cyberduck;
                if (settings.Rclone) targets |= FilterTargets.Rclone;
                var applied = await _filters.ApplyAsync(root.Path, selection, customPatterns, targets, ct);
                childIds = applied.JournalIds;
                await _preferences.SaveAsync(updated, ct);
                await _journal.CompleteAsync(entry.Id,
                    new Dictionary<string, string> { ["ChildJournalIds"] = string.Join(",", childIds) }, CancellationToken.None);
                var notes = string.Join(" ", applied.ActivationNotes);
                return OperationResult.Ok("Exclusion settings saved. " + notes, entry.Id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var rollbackErrors = new List<string>();
                foreach (var child in childIds.Reverse())
                {
                    try { await _filters.RollbackAsync(child, CancellationToken.None); }
                    catch (Exception rollbackException) { rollbackErrors.Add(rollbackException.Message); }
                }
                try
                {
                    var current = await _preferences.LoadAsync(CancellationToken.None);
                    var currentJson = JsonSerializer.Serialize(current, JsonOptions);
                    if (currentJson == afterJson)
                        await _preferences.SaveAsync(preferences, CancellationToken.None);
                    else if (currentJson != beforeJson)
                        rollbackErrors.Add("Settings changed during the operation; review them before retrying.");
                }
                catch (Exception rollbackException) { rollbackErrors.Add("Settings rollback failed: " + rollbackException.Message); }
                try
                {
                    await _journal.FailAsync(entry.Id, exception.Message,
                        new Dictionary<string, string> { ["ChildJournalIds"] = string.Join(",", childIds) }, CancellationToken.None);
                }
                catch (Exception journalException) { rollbackErrors.Add("Journal update failed: " + journalException.Message); }
                var reason = exception is OperationCanceledException ? "Exclusion update cancelled." : exception.Message;
                return OperationResult.Error(reason + (rollbackErrors.Count == 0 ? string.Empty :
                    " Automatic rule rollback also needs attention: " + string.Join("; ", rollbackErrors)), entry.Id);
            }
        }, cancellationToken);

    public Task<OperationResult> SetIncludeDownloadsAsync(bool include, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            if (!include)
            {
                var settings = await _preferences.LoadAsync(ct);
                var downloads = await _folders.GetFolderAsync(KnownFolderKind.Downloads, settings.RootPath, ct);
                if (downloads.State == KnownFolderState.CloudManaged)
                    return OperationResult.Error("Restore Downloads to the local profile before turning off its optional redirection.");
            }
            return await SavePreferenceChangeAsync("settings.downloads", preferences => preferences.IncludeDownloads = include, ct);
        }, cancellationToken);

    public Task<OperationResult> SetThemeAsync(string theme, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(ct =>
        {
            if (theme is not ("System" or "Light" or "Dark"))
                return Task.FromResult(OperationResult.Error("Choose System, Light, or Dark appearance."));
            return SavePreferenceChangeAsync("settings.theme", preferences => preferences.Theme = theme, ct);
        }, cancellationToken);

    public Task<OperationResult> RollbackAsync(string journalId, CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async ct =>
        {
            if (!Guid.TryParse(journalId, out var id)) return OperationResult.Error("Invalid journal ID.");
            var entry = await _journal.GetAsync(id, ct);
            if (entry is null) return OperationResult.Error("Journal entry was not found.");
            if (entry.State != JournalState.Completed)
                return OperationResult.Error("Only completed operations can be rolled back.");
            if (entry.Operation.StartsWith("KnownFolder.", StringComparison.Ordinal))
            {
                var result = await _folders.RollbackAsync(id, ct);
                return new OperationResult(result.Success, result.Message, result.JournalId?.ToString("D"));
            }
            if (entry.Operation == "link.create")
            {
                var result = await _links.RollbackAsync(id, ct);
                return new OperationResult(result.RestoredLocalPath, result.Message, result.JournalId?.ToString("D"));
            }
            if (entry.Operation.StartsWith("filter.", StringComparison.Ordinal))
            {
                await _filters.RollbackAsync(id, ct);
                return OperationResult.Ok("The selected exclusion rule change was rolled back.", id);
            }
            if (entry.Operation.StartsWith("settings.", StringComparison.Ordinal))
                return await RollbackPreferenceAsync(entry, ct);
            return OperationResult.Error("This operation has no automatic rollback. Its data remains available at the recorded paths.");
        }, cancellationToken);

    private async Task<OperationResult> RollbackPreferenceAsync(JournalEntry entry, CancellationToken ct)
    {
        if (!entry.Metadata.TryGetValue("Before", out var beforeJson) ||
            !entry.Metadata.TryGetValue("After", out var afterJson))
            return OperationResult.Error("This settings journal does not contain a reversible snapshot.");
        var current = await _preferences.LoadAsync(ct);
        if (JsonSerializer.Serialize(current, JsonOptions) != afterJson)
            return OperationResult.Error("Settings changed after this operation; review the newer changes before rolling back.");
        if (entry.Operation == "settings.filters" &&
            entry.Metadata.TryGetValue("ChildJournalIds", out var childIds))
        {
            foreach (var value in childIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (!Guid.TryParse(value, out var childId))
                    return OperationResult.Error("A child rule journal ID is invalid.");
                await _filters.RollbackAsync(childId, ct);
            }
        }
        var restored = JsonSerializer.Deserialize<CloudBayPreferences>(beforeJson, JsonOptions)
            ?? throw new InvalidDataException("The saved prior settings are invalid.");
        await _preferences.SaveAsync(restored, ct);
        await _journal.MarkRolledBackAsync(entry.Id, cancellationToken: CancellationToken.None);
        return OperationResult.Ok("Previous settings restored.", entry.Id);
    }

    private async Task<OperationResult> SavePreferenceChangeAsync(string operation,
        Action<CloudBayPreferences> change, CancellationToken ct)
    {
        var before = await _preferences.LoadAsync(ct);
        var after = Clone(before);
        change(after);
        var beforeJson = JsonSerializer.Serialize(before, JsonOptions);
        var afterJson = JsonSerializer.Serialize(after, JsonOptions);
        if (beforeJson == afterJson) return OperationResult.Ok("Already configured.");
        var entry = await _journal.BeginAsync(operation, before.RootPath, after.RootPath,
            new Dictionary<string, string> { ["Before"] = beforeJson, ["After"] = afterJson }, ct);
        try
        {
            await _preferences.SaveAsync(after, ct);
            await _journal.CompleteAsync(entry.Id, cancellationToken: CancellationToken.None);
            return OperationResult.Ok("Settings saved.", entry.Id);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { await _journal.FailAsync(entry.Id, exception.Message, cancellationToken: CancellationToken.None); }
            catch (Exception journalException)
            {
                return OperationResult.Error(exception.Message + " Journal update failed: " + journalException.Message,
                    entry.Id);
            }
            return OperationResult.Error(exception is OperationCanceledException ? "Settings update cancelled." : exception.Message,
                entry.Id);
        }
    }

    private async Task<OperationResult> ExecuteMutationAsync(
        Func<CancellationToken, Task<OperationResult>> action, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return OperationResult.Error(exception.Message);
        }
        finally { _mutationGate.Release(); }
    }

    private static bool TryParseFolder(string id, out KnownFolderKind kind) =>
        Enum.TryParse(id, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static bool SupportsRollback(string operation) =>
        operation.StartsWith("KnownFolder.", StringComparison.Ordinal) ||
        operation.StartsWith("filter.", StringComparison.Ordinal) ||
        operation.StartsWith("settings.", StringComparison.Ordinal) ||
        operation == "link.create";

    private static CloudBayPreferences Clone(CloudBayPreferences source) =>
        JsonSerializer.Deserialize<CloudBayPreferences>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("Could not copy CloudBay settings.");
}

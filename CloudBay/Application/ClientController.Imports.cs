using CloudBay.Core;
using CloudBay.Core.Sync;
using CloudBay.Windows;

namespace CloudBay.Application;

public sealed partial class ClientController
{
    public int ImportHistoryIssueCount { get; private set; }
    public bool HasOlderFolderImports { get; private set; }
    public IReadOnlyList<FolderImportRecord> ImportHistory
    {
        get
        {
            var journal = new ImportJournal(_storage.DirectoryPath);
            var records = journal.Read(ImportDestinationIdentity);
            ImportHistoryIssueCount = journal.SkippedRecords;
            HasOlderFolderImports = journal.HasMoreRecords;
            return records;
        }
    }
    public IReadOnlyList<FolderImportRecord> GetImportHistory(ImportHistoryCursor? after)
    {
        var journal = new ImportJournal(_storage.DirectoryPath);
        var records = journal.Read(ImportDestinationIdentity, after);
        ImportHistoryIssueCount = journal.SkippedRecords;
        HasOlderFolderImports = journal.HasMoreRecords;
        return records;
    }
    private string ImportDestinationIdentity => Settings.AccountId + "|" + Settings.BucketId + "|" + Settings.Prefix + "|" + Path.GetFullPath(Settings.RootPath).ToUpperInvariant();
    private readonly Dictionary<FolderImportPlan, string> _reviewedImports = [];

    public async Task<FolderImportPlan> PreviewFolderImportAsync(string sourcePath, string relativeDestination,
        CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            var relative = PathRules.ValidateRelative(relativeDestination.Replace('\\', '/').Trim());
            if (PathRules.IsExcluded(relative, Array.Empty<string>())) throw new IOException("Choose a personal folder outside CloudBay's internal working folders.");
            var destination = PathRules.FullPath(Settings.RootPath, relative);
            if (Settings.CustomBackups.Any(folder => IsNested(sourcePath, folder.SourcePath) || IsNested(folder.SourcePath, sourcePath)))
                throw new IOException("This source is already backed up by CloudBay. Choose a different source folder.");
            var plan = await Task.Run(() => FolderImport.Preview(sourcePath, destination, operation.Token), operation.Token);
            // Review applies only to this account and root generation, not an edited account.
            if (_reviewedImports.Count >= 32) _reviewedImports.Clear();
            _reviewedImports[plan] = ImportDestinationIdentity;
            return plan;
        }
        finally { _operations.Release(); }
    }

    public async Task ImportFolderAsync(FolderImportPlan reviewed, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        FolderImportRecord? record = null;
        var journal = new ImportJournal(_storage.DirectoryPath);
        try
        {
            EnsureConnected();
            if (!_reviewedImports.Remove(reviewed, out var identity) || identity != ImportDestinationIdentity ||
                !IsNested(reviewed.DestinationPath, Settings.RootPath))
                throw new IOException("This import review is no longer current. Review the source and destination again.");
            _maintenance = true;
            if (_engine is not null) await _engine.QuiesceAsync(operation.Token);
            record = new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, reviewed, identity, "Copying");
            journal.Save(record);
            NotifyChanged();
            await Task.Run(() => FolderImport.ExecuteAsync(reviewed, operation.Token, new ImportProgress(relative =>
                AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information,
                    Path.GetRelativePath(Settings.RootPath, reviewed.DestinationPath).Replace('\\', '/') + "/" + relative.Replace('\\', '/'), "Imported and verified local copy")
                { Location = MainActivityLocation(Path.GetRelativePath(Settings.RootPath, reviewed.DestinationPath).Replace('\\', '/') + "/" + relative.Replace('\\', '/')) }))), operation.Token);
            // VerifiedTreeCopy already preserved desktop.ini, named streams and their
            // activation attributes. A second cosmetic pass must not reject a valid
            // UNC or mounted-provider source after its verified copy has completed.
            journal.Save(record with { State = "Completed" });
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup,
                Path.GetRelativePath(Settings.RootPath, reviewed.DestinationPath).Replace('\\', '/'),
                $"Imported {reviewed.FileCount:N0} files. The source folder was retained; CloudBay will back up the verified copies.", reviewed.TotalBytes)
            { Location = MainActivityLocation(Path.GetRelativePath(Settings.RootPath, reviewed.DestinationPath).Replace('\\', '/')) });
        }
        catch (Exception error)
        {
            if (record is not null) journal.Save(record with { State = "Needs review", Error = error is OperationCanceledException
                ? "The import was cancelled. Completed copies and the source were retained." : FileSystemError.Describe(error) });
            throw;
        }
        finally { EndMaintenance(resume: true); _operations.Release(); NotifyChanged(); }
    }

    public async Task<BackupSourceReview> PreviewBackupAsync(string name, string? additionalSource = null,
        CancellationToken cancellationToken = default)
        => await PreviewBackupAsync(name, additionalSource, BackupTransferMode.Copy, true, cancellationToken);

    public async Task<BackupSourceReview> PreviewBackupAsync(string name, string? source,
        BackupTransferMode mode, bool includeCurrentFiles, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            var review = await Task.Run(() => KnownFolderBackup.Preview(name, Settings.RootPath, source, mode, includeCurrentFiles, operation.Token), operation.Token);
            lock (_backupReviews)
            {
                if (_backupReviews.Count >= 32) _backupReviews.Clear();
                _backupReviews[review] = ImportDestinationIdentity;
            }
            return review;
        }
        finally { _operations.Release(); }
    }
    private readonly Dictionary<BackupSourceReview, string> _backupReviews = [];
    private sealed class ImportProgress(Action<string> action) : IProgress<string>
    { public void Report(string value) => action(value); }

    public async Task<string?> EnableReviewedBackupAsync(BackupSourceReview reviewed, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            progress?.Report(reviewed.TransferMode == BackupTransferMode.None ? "Changing the Windows location…" : "Copying and verifying files…");
            lock (_backupReviews)
                if (!_backupReviews.Remove(reviewed, out var identity) || identity != ImportDestinationIdentity)
                    throw new IOException("Review this folder backup again. The account or folder selection changed.");
            if (Settings.Backups.Any(folder => folder.Name.Equals(reviewed.Name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("This Windows folder is already backed up.");
            if (Settings.CustomBackups.Any(folder => IsNested(folder.SourcePath, reviewed.OriginalWindowsPath) || IsNested(reviewed.OriginalWindowsPath, folder.SourcePath)))
                throw new IOException("Stop the custom backup inside this Windows folder before changing its location.");
            if (reviewed.AdditionalFiles is { } extra &&
                (IsNested(extra.SourcePath, Settings.RootPath) || IsNested(Settings.RootPath, extra.SourcePath) ||
                 Settings.CustomBackups.Any(folder => IsNested(extra.SourcePath, folder.SourcePath) || IsNested(folder.SourcePath, extra.SourcePath))))
                throw new IOException("Choose a source outside CloudBay and its custom backups.");
            _maintenance = true;
            await _engine!.QuiesceAsync(operation.Token);
            _storage.SaveBackupIntent(new(reviewed.Name, reviewed.OriginalWindowsPath, reviewed.CurrentFiles.DestinationPath), true);
            var applied = await Task.Run(() => KnownFolderBackup.ApplyEnableReviewedAsync(reviewed, operation.Token), operation.Token);
            Settings = Settings with { Backups = [.. Settings.Backups, applied.Folder] };
            _storage.SaveSettings(Settings);
            _storage.ClearBackupIntent();
            _engine.Configure(Settings);
            var detail = reviewed.TransferMode switch
            {
                BackupTransferMode.None => "Windows folder backup enabled without importing files. Existing files remain in their previous locations.",
                BackupTransferMode.Move => "Windows folder backup enabled. Verified source files were moved into CloudBay.",
                _ => "Windows folder backup enabled. Verified source files were copied; originals retained."
            };
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, reviewed.Name, applied.RetentionWarning ?? detail)
            { Location = MainActivityLocation(reviewed.Name) });
            return applied.RetentionWarning;
        }
        finally { EndMaintenance(resume: true); _operations.Release(); }
    }

    private readonly Dictionary<BackupRestoreReview, string> _restoreReviews = [];

    public async Task<BackupRestoreReview> PreviewStopBackupAsync(string name, string? destination,
        BackupTransferMode mode, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            var folder = Settings.Backups.SingleOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("This Windows folder is no longer backed up. Refresh the selection.");
            var review = await Task.Run(() => KnownFolderBackup.PreviewDisable(folder, destination, mode, operation.Token), operation.Token);
            ValidateRestoreDestination(review);
            lock (_restoreReviews)
            {
                if (_restoreReviews.Count >= 32) _restoreReviews.Clear();
                _restoreReviews[review] = ImportDestinationIdentity;
            }
            return review;
        }
        finally { _operations.Release(); }
    }

    private void ValidateRestoreDestination(BackupRestoreReview review)
    {
        if (IsNested(review.DestinationPath, Settings.RootPath) || IsNested(Settings.RootPath, review.DestinationPath) ||
            Settings.CustomBackups.Any(folder => IsNested(review.DestinationPath, folder.SourcePath) || IsNested(folder.SourcePath, review.DestinationPath)))
            throw new IOException("Choose a Windows location outside CloudBay and its custom backups.");
        if (Settings.Backups.Any(folder => folder.Name != review.Folder.Name &&
            (IsNested(review.DestinationPath, folder.OriginalPath) || IsNested(folder.OriginalPath, review.DestinationPath))))
            throw new IOException("Choose a separate restore location. This location belongs to another Windows folder backup.");
    }

    public async Task<string?> DisableReviewedBackupAsync(BackupRestoreReview reviewed, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null, bool freeLocalSpace = false)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            lock (_restoreReviews)
                if (!_restoreReviews.Remove(reviewed, out var identity) || identity != ImportDestinationIdentity)
                    throw new IOException("Review stopping this backup again. The account or folder selection changed.");
            if (freeLocalSpace && reviewed.TransferMode == BackupTransferMode.Move)
                throw new IOException("Choose either moving files or keeping B2 files online only. Review the choice again.");
            if (!Settings.Backups.Contains(reviewed.Folder))
                throw new IOException("This folder backup changed since the review. Review it again.");
            ValidateRestoreDestination(reviewed);
            if (Settings.CustomBackups.Any(folder => IsNested(folder.SourcePath, reviewed.Folder.DestinationPath) || IsNested(reviewed.Folder.DestinationPath, folder.SourcePath)))
                throw new IOException("Stop the custom backup inside this Windows folder before changing its location.");
            progress?.Report(reviewed.TransferMode == BackupTransferMode.None ? "Changing the Windows location…" : "Downloading, copying and verifying files…");
            _maintenance = true;
            await _engine!.QuiesceAsync(operation.Token);
            _storage.SaveBackupIntent(reviewed.Folder, false, reviewed.DestinationPath);
            var outcome = await Task.Run(() => KnownFolderBackup.ApplyDisableReviewedAsync(reviewed, operation.Token), operation.Token);
            Settings = Settings with { Backups = Settings.Backups.Where(folder => folder != reviewed.Folder).ToList() };
            _storage.SaveSettings(Settings);
            _storage.ClearBackupIntent();
            _engine.Configure(Settings);
            var detail = reviewed.TransferMode switch
            {
                BackupTransferMode.None => "Windows folder backup stopped without restoring files. Existing files remain in CloudBay.",
                BackupTransferMode.Move => "Windows folder backup stopped. Verified files moved to " + reviewed.DestinationPath + ". Removing CloudBay originals also removes their current B2 copies through sync.",
                _ => "Windows folder backup stopped. Verified files copied to " + reviewed.DestinationPath + "; CloudBay copies retained."
            };
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, reviewed.Folder.Name, outcome.RetentionWarning ?? detail)
            { Location = MainActivityLocation(reviewed.Folder.Name) });
            var warning = outcome.RetentionWarning;
            if (freeLocalSpace)
            {
                progress?.Report("Freeing verified downloaded copies…");
                try
                {
                    await _placeholders!.FreeSpaceAsync(reviewed.Folder.DestinationPath, operation.Token);
                    AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, reviewed.Folder.Name, "Eligible downloaded CloudBay copies released. Unsynced local files and B2 files retained."));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or
                    System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                {
                    warning = "Backup is off. Some downloaded copies were retained: " + error.Message;
                    AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, reviewed.Folder.Name,
                        warning));
                }
            }
            return warning;
        }
        finally { EndMaintenance(resume: true); _operations.Release(); NotifyChanged(); }
    }
}

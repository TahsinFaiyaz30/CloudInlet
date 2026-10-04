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
                    Path.GetRelativePath(Settings.RootPath, reviewed.DestinationPath).Replace('\\', '/') + "/" + relative.Replace('\\', '/'), "Imported and verified local copy")))), operation.Token);
            // VerifiedTreeCopy already preserved desktop.ini, named streams and their
            // activation attributes. A second cosmetic pass must not reject a valid
            // UNC or mounted-provider source after its verified copy has completed.
            journal.Save(record with { State = "Completed" });
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup,
                Path.GetRelativePath(Settings.RootPath, reviewed.DestinationPath).Replace('\\', '/'),
                $"Imported {reviewed.FileCount:N0} files. The source folder was retained; CloudBay will back up the verified copies.", reviewed.TotalBytes));
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
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            var review = await Task.Run(() => KnownFolderBackup.Preview(name, Settings.RootPath, additionalSource, operation.Token), operation.Token);
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

    public async Task EnableReviewedBackupAsync(BackupSourceReview reviewed, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            lock (_backupReviews)
                if (!_backupReviews.Remove(reviewed, out var identity) || identity != ImportDestinationIdentity)
                    throw new IOException("Review this folder backup again. The account or folder selection changed.");
            if (Settings.Backups.Any(folder => folder.Name.Equals(reviewed.Name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("This Windows folder is already backed up.");
            if (Settings.CustomBackups.Any(folder => IsNested(folder.SourcePath, reviewed.OriginalWindowsPath)))
                throw new IOException("Stop the custom backup inside this Windows folder before changing its location.");
            _maintenance = true;
            await _engine!.QuiesceAsync(operation.Token);
            _storage.SaveBackupIntent(new(reviewed.Name, reviewed.OriginalWindowsPath, reviewed.CurrentFiles.DestinationPath), true);
            var folder = await Task.Run(() => KnownFolderBackup.EnableReviewedAsync(reviewed, operation.Token), operation.Token);
            Settings = Settings with { Backups = [.. Settings.Backups, folder] };
            _storage.SaveSettings(Settings);
            _storage.ClearBackupIntent();
            _engine.Configure(Settings);
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, reviewed.Name, "Windows folder backup enabled using the reviewed source. Original files were retained."));
        }
        finally { EndMaintenance(resume: true); _operations.Release(); }
    }
}

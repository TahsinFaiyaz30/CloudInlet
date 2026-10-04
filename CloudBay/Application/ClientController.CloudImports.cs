using CloudBay.Core;
using CloudBay.Core.Sync;

namespace CloudBay.Application;

public sealed partial class ClientController
{
    private readonly Dictionary<CloudImportPlan, string> _reviewedCloudImports = [];
    public int CloudImportHistoryIssueCount { get; private set; }
    public bool HasOlderCloudImports { get; private set; }
    public IReadOnlyList<CloudImportRecord> CloudImportHistory
    {
        get
        {
            var journal = new CloudImportJournal(_storage.DirectoryPath);
            var records = journal.Read(ImportDestinationIdentity);
            CloudImportHistoryIssueCount = journal.SkippedRecords;
            HasOlderCloudImports = journal.HasMoreRecords;
            return records;
        }
    }
    public IReadOnlyList<CloudImportRecord> GetCloudImportHistory(ImportHistoryCursor? after)
    {
        var journal = new CloudImportJournal(_storage.DirectoryPath);
        var records = journal.Read(ImportDestinationIdentity, after);
        CloudImportHistoryIssueCount = journal.SkippedRecords;
        HasOlderCloudImports = journal.HasMoreRecords;
        return records;
    }

    public async Task<IReadOnlyList<CloudBucket>> GetImportBucketsAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try { EnsureConnected(); return await _cloud!.ListBucketsAsync(operation.Token); }
        finally { _operations.Release(); }
    }
    public async Task<CloudImportPlan> PreviewCloudImportAsync(string sourceBucketId, string sourcePrefix,
        string relativeDestination, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            EnsureConnected();
            var relative = PathRules.ValidateRelative(relativeDestination.Replace('\\', '/').Trim());
            if (PathRules.IsExcluded(relative, Array.Empty<string>())) throw new IOException("Choose a personal folder outside CloudBay's internal working folders.");
            var destination = PathRules.FullPath(Settings.RootPath, relative);
            await EnsureCloudImportDestinationEmptyAsync(destination, operation.Token);
            var plan = await CloudImport.PreviewAsync(_cloud!, sourceBucketId, sourcePrefix, destination, operation.Token);
            if (plan.Items.Count == 0) throw new IOException("This source folder has no files available to import. Choose another source folder.");
            if (_reviewedCloudImports.Count >= 32) _reviewedCloudImports.Clear();
            _reviewedCloudImports[plan] = ImportDestinationIdentity;
            return plan;
        }
        finally { _operations.Release(); }
    }
    private async Task EnsureCloudImportDestinationEmptyAsync(string destination, CancellationToken ct)
    {
        if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose a new, empty destination folder for this cloud import. Existing files were retained.");
        var relative = Path.GetRelativePath(Settings.RootPath, destination).Replace('\\', '/');
        var key = Settings.Prefix + relative;
        await foreach (var existing in _cloud!.ListCurrentAsync(Settings.BucketId, key, ct))
            if (existing.Key == key || existing.Key.StartsWith(key + "/", StringComparison.Ordinal))
                throw new IOException("The destination already contains B2 files. Choose a new folder so those versions remain unchanged.");
    }
    public Task ImportCloudAsync(CloudImportPlan reviewed, CancellationToken cancellationToken = default) =>
        RunCloudImportAsync(reviewed, null, cancellationToken);
    public async Task ResumeCloudImportAsync(string id, CancellationToken cancellationToken = default)
    {
        var record = new CloudImportJournal(_storage.DirectoryPath).Read(ImportDestinationIdentity, recordId: id)
            .SingleOrDefault(item => item.State != "Completed")
            ?? throw new IOException("This interrupted cloud import is no longer available for the connected account.");
        await RunCloudImportAsync(record.Plan, record, cancellationToken);
    }
    private async Task RunCloudImportAsync(CloudImportPlan plan, CloudImportRecord? previous, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        var journal = new CloudImportJournal(_storage.DirectoryPath);
        CloudImportRecord? record = null;
        try
        {
            EnsureConnected();
            CloudImport.ValidatePlan(plan);
            if (!Guid.TryParseExact(plan.JobId, "N", out _) || !IsNested(plan.DestinationPath, Settings.RootPath) ||
                plan.DestinationPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("This cloud import destination is invalid. Review the import again.");
            var destinationRelative = PathRules.ValidateRelative(Path.GetRelativePath(Settings.RootPath, plan.DestinationPath).Replace('\\', '/'));
            if (PathRules.IsExcluded(destinationRelative, Array.Empty<string>()) ||
                !PathRules.FullPath(Settings.RootPath, destinationRelative).Equals(plan.DestinationPath, StringComparison.OrdinalIgnoreCase) ||
                plan.Items.Any(item => item.RelativePath != PathRules.FromKey(item.Source.Key.EndsWith('/') ? item.Source.Key[..^1] : item.Source.Key, plan.SourcePrefix) +
                        (item.Source.Key.EndsWith('/') ? "/" : "")))
                throw new InvalidDataException("The import checkpoint contains an unexpected source or destination path. Review it again.");
            if (previous is null)
            {
                if (!_reviewedCloudImports.Remove(plan, out var identity) || identity != ImportDestinationIdentity)
                    throw new IOException("This cloud import review is no longer current. Review its destination again.");
                await EnsureCloudImportDestinationEmptyAsync(plan.DestinationPath, operation.Token);
            }
            else if (previous.DestinationIdentity != ImportDestinationIdentity)
                throw new IOException("Reconnect the original destination account before continuing this import.");
            var completed = previous?.Completed.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, CloudObject>(StringComparer.Ordinal);
            record = new(plan.JobId, previous?.StartedUtc ?? DateTimeOffset.UtcNow, plan, ImportDestinationIdentity, "Copying", completed);
            journal.Save(record);
            _maintenance = true;
            await _engine!.QuiesceAsync(operation.Token);
            var destinationPrefix = Settings.Prefix + Path.GetRelativePath(Settings.RootPath, plan.DestinationPath).Replace('\\', '/') + "/";
            if (previous is null) await EnsureCloudImportDestinationEmptyAsync(plan.DestinationPath, operation.Token);
            CloudImport.ValidateCompleted(plan, completed, destinationPrefix);
            // Stable immutable source versions and operation receipts survive response loss
            // and process exit. Completed keys are not blindly posted for a second time.
            foreach (var item in plan.Items)
            {
                operation.Token.ThrowIfCancellationRequested();
                var targetKey = destinationPrefix + item.RelativePath;
                if (completed.TryGetValue(item.Source.FileId, out var existing))
                {
                    if (existing.Key != targetKey || existing.Size != item.Source.Size)
                        throw new InvalidDataException("The import checkpoint does not match its reviewed destination. It was retained for recovery.");
                    await ((ICloudStore)_cloud!).VerifyCopyAsync(Settings.BucketId, targetKey, item.Source,
                        CloudImport.OperationId(plan.JobId, item, targetKey), existing, operation.Token);
                    continue;
                }
                var copied = await ((ICloudStore)_cloud!).CopyToAsync(Settings.BucketId, targetKey, item.Source,
                    CloudImport.OperationId(plan.JobId, item, targetKey), operation.Token);
                await _cloud.VerifyUploadAsync(copied, Settings.BucketId, operation.Token);
                completed.Add(item.Source.FileId, copied);
                // Persist only the new receipt. Rewriting the complete reviewed
                // source list for every tiny object would make import quadratic.
                journal.SaveProgress(plan.JobId, item.Source.FileId, copied);
                AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, targetKey[Settings.Prefix.Length..],
                    "Imported and verified in Backblaze B2. The original cloud version was retained.", copied.Size));
            }
            journal.Save(record with { State = "Completed" });
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup,
                Path.GetRelativePath(Settings.RootPath, plan.DestinationPath).Replace('\\', '/'),
                $"Cloud import completed: {plan.FileCount:N0} files. They will appear in your native CloudBay folder.", plan.TotalBytes));
        }
        catch (Exception error)
        {
            if (record is not null) journal.Save(record with { State = "Needs review", Error = error is OperationCanceledException
                ? "Cloud import interrupted. Completed versions were retained; continue from import history." : FileSystemError.Describe(error) });
            throw;
        }
        finally { EndMaintenance(resume: true); _operations.Release(); NotifyChanged(); }
    }
}

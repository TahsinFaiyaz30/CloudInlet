using System.Runtime.CompilerServices;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class CloudImportTests
{
    [TestMethod]
    public async Task ReviewCapturesImmutableFilesAndEmptyFoldersWithoutStartingCopies()
    {
        using var source = new SourceStore([
            new("one", "Source/report.txt", 100, new string('a', 40), DateTimeOffset.UtcNow),
            new("empty", "Source/empty/", 0, new string('b', 40), DateTimeOffset.UtcNow),
            new("two", "Source/photos/picture.png", 200, new string('c', 40), DateTimeOffset.UtcNow)]);
        var destination = Path.Combine(Path.GetTempPath(), "CloudBay-cloud-review-" + Guid.NewGuid().ToString("N"));
        var plan = await CloudImport.PreviewAsync(source, "bucket", "Source", destination);
        Assert.AreEqual(2L, plan.FileCount);
        Assert.AreEqual(300L, plan.TotalBytes);
        Assert.AreEqual("Source/", plan.SourcePrefix);
        CollectionAssert.AreEquivalent(new[] { "report.txt", "empty/", "photos/picture.png" }, plan.Items.Select(item => item.RelativePath).ToArray());
        Assert.IsFalse(Directory.Exists(destination));
        var item = plan.Items.First();
        Assert.AreEqual(CloudImport.OperationId(plan.JobId, item, "CloudBay/report.txt"), CloudImport.OperationId(plan.JobId, item, "CloudBay/report.txt"));
        Assert.AreNotEqual(CloudImport.OperationId(plan.JobId, item, "CloudBay/report.txt"), CloudImport.OperationId(plan.JobId, item with { Source = item.Source with { FileId = "new-version" } }, "CloudBay/report.txt"));
    }
    [DataTestMethod]
    [DataRow("Source/COM¹.txt")]
    [DataRow("Source/../escape.txt")]
    [DataRow("Outside/file.txt")]
    [DataRow("Source/.cloudbay/temporary.bin")]
    public async Task InvalidCloudPathsNeverProduceAnImportPlan(string key)
    {
        using var source = new SourceStore([new("id", key, 1, null, DateTimeOffset.UtcNow)]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => CloudImport.PreviewAsync(source, "bucket", "Source", "C:\\review"));
    }
    [TestMethod]
    public async Task CaseCollisionsAndFileParentConflictsMustBeResolvedBeforeImport()
    {
        using var caseSource = new SourceStore([
            new("one", "Source/a.txt", 1, null, DateTimeOffset.UtcNow), new("two", "Source/A.txt", 1, null, DateTimeOffset.UtcNow)]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => CloudImport.PreviewAsync(caseSource, "bucket", "Source", "C:\\review"));
        using var parentSource = new SourceStore([
            new("one", "Source/folder", 1, null, DateTimeOffset.UtcNow), new("two", "Source/folder/file.txt", 1, null, DateTimeOffset.UtcNow)]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => CloudImport.PreviewAsync(parentSource, "bucket", "Source", "C:\\review"));
    }
    [TestMethod]
    public async Task FailedSourceListingCannotTurnAPartialReviewIntoAnImport()
    {
        using var source = new SourceStore([new("one", "Source/a.txt", 1, null, DateTimeOffset.UtcNow)], failAfterListing: true);
        await Assert.ThrowsExceptionAsync<IOException>(() => CloudImport.PreviewAsync(source, "bucket", "Source", "C:\\review"));
    }
    [TestMethod]
    public void CloudImportJournalRetainsCompletedImmutableVersionsAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-cloud-import-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new CloudObject("source-version", "Source/a.bin", 10, new string('a', 40), DateTimeOffset.UtcNow);
            var copied = source with { FileId = "copied-version", Key = "CloudBay/Destination/a.bin" };
            var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), "source-bucket", "Source/", Path.Combine(root, "Destination"), [new("a.bin", source)], 1, 10);
            var record = new CloudImportRecord(plan.JobId, DateTimeOffset.UtcNow, plan, "account|bucket|CloudBay/|" + root, "Copying", new Dictionary<string, CloudObject> { [source.FileId] = copied });
            new CloudImportJournal(root).Save(record);
            var restored = new CloudImportJournal(root).Read().Single();
            Assert.AreEqual("Needs review", restored.State);
            Assert.AreEqual(source.FileId, restored.Plan.Items.Single().Source.FileId);
            Assert.AreEqual(copied, restored.Completed[source.FileId]);
            Assert.IsFalse(Directory.Exists(plan.DestinationPath), "Reading history never resumes cloud copies or downloads.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow("prefix")]
    [DataRow("bucket")]
    [DataRow("count")]
    [DataRow("bytes")]
    [DataRow("versions")]
    [DataRow("null-items")]
    [DataRow("parent-file")]
    public void DamagedPlanCannotAuthorizeCloudCopies(string damage)
    {
        var source = new CloudObject("source", "Source/a.bin", 10, new string('a', 40), DateTimeOffset.UtcNow);
        var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), "bucket", "Source/", Path.Combine(Path.GetTempPath(), "reviewed-import"), [new("a.bin", source)], 1, 10);
        var broken = damage switch
        {
            "prefix" => plan with { SourcePrefix = "Source" },
            "bucket" => plan with { SourceBucketId = " " },
            "count" => plan with { FileCount = 2 },
            "bytes" => plan with { TotalBytes = 11 },
            "versions" => plan with { Items = [.. plan.Items, new("b.bin", source with { Key = "Source/b.bin" })], FileCount = 2, TotalBytes = 20 },
            "null-items" => plan with { Items = null! },
            "parent-file" => plan with { Items = [.. plan.Items, new("a.bin/child.txt", source with { FileId = "child", Key = "Source/a.bin/child.txt" })], FileCount = 2, TotalBytes = 20 },
            _ => throw new AssertFailedException("Unknown damage fixture")
        };
        Assert.ThrowsException<InvalidDataException>(() => CloudImport.ValidatePlan(broken));
        CloudImport.ValidatePlan(plan);
    }

    [TestMethod]
    public async Task CloudVersionsWithoutCompleteChecksumsRequireVerifiedWindowsImportBeforeAnyCopy()
    {
        using var source = new SourceStore([new("source", "Source/a.bin", 10, null, DateTimeOffset.UtcNow)]);
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => CloudImport.PreviewAsync(source, "bucket", "Source/", Path.Combine(Path.GetTempPath(), "reviewed-import")));
        StringAssert.Contains(error.Message, "through Windows");
    }

    [DataTestMethod]
    [DataRow("checksum")]
    [DataRow("target")]
    [DataRow("size")]
    [DataRow("source-id")]
    [DataRow("timestamp")]
    [DataRow("copied-id")]
    public void SameSizeWrongReceiptCannotBeTreatedAsACompletedSourceVersion(string damage)
    {
        var source = new CloudObject("source", "Source/a.bin", 10, new string('a', 40), DateTimeOffset.UtcNow);
        var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), "bucket", "Source/", Path.Combine(Path.GetTempPath(), "reviewed-import"), [new("a.bin", source)], 1, 10);
        var copied = source with { FileId = "copy", Key = "CloudBay/import/a.bin" };
        var receipt = damage switch
        {
            "checksum" => copied with { Sha1 = new string('b', 40) },
            "target" => copied with { Key = "CloudBay/unreviewed/a.bin" },
            "size" => copied with { Size = 11 },
            "timestamp" => copied with { ModifiedUtc = copied.ModifiedUtc.AddMinutes(1) },
            "copied-id" => copied with { FileId = new string('x', 1025) },
            "source-id" => copied,
            _ => throw new AssertFailedException("Unknown damage fixture")
        };
        var key = damage == "source-id" ? "unexpected-source" : source.FileId;
        Assert.ThrowsException<InvalidDataException>(() => CloudImport.ValidateCompleted(plan,
            new Dictionary<string, CloudObject> { [key] = receipt }, "CloudBay/import/"));
        CloudImport.ValidateCompleted(plan, new Dictionary<string, CloudObject> { [source.FileId] = copied }, "CloudBay/import/");
    }

    [TestMethod]
    public void DurableProgressReceiptsResumeWithoutRewritingSourcePlanAndRejectDamagedHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-cloud-import-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new CloudObject("source-version", "Source/a.bin", 10, new string('a', 40), DateTimeOffset.UtcNow);
            var copied = source with { FileId = "copied-version", Key = "CloudBay/Destination/a.bin" };
            var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), "bucket", "Source/", Path.Combine(root, "Destination"), [new("a.bin", source)], 1, 10);
            var journal = new CloudImportJournal(root);
            var record = new CloudImportRecord(plan.JobId, DateTimeOffset.UtcNow, plan, "account|bucket|CloudBay/|" + root, "Copying", new Dictionary<string, CloudObject>());
            journal.Save(record);
            var planFile = Path.Combine(root, "CloudImports", plan.JobId + ".json");
            var originalPlan = File.ReadAllBytes(planFile);
            journal.SaveProgress(plan.JobId, source.FileId, copied);
            CollectionAssert.AreEqual(originalPlan, File.ReadAllBytes(planFile), "Saving each receipt must not rewrite the complete source plan.");
            Assert.AreEqual(copied, new CloudImportJournal(root).Read().Single().Completed[source.FileId]);
            var receiptFile = Directory.GetFiles(Path.Combine(root, "CloudImports", plan.JobId), "*.json").Single();
            var validReceipt = File.ReadAllBytes(receiptFile);
            File.WriteAllText(receiptFile, "{broken");
            var reopened = new CloudImportJournal(root);
            Assert.AreEqual(0, reopened.Read().Single().Completed.Count);
            Assert.AreEqual(1, reopened.SkippedRecords);
            Assert.AreEqual("{broken", File.ReadAllText(receiptFile), "Invalid checkpoint bytes remain untouched for review.");
            File.WriteAllBytes(receiptFile, validReceipt);
            var wrongName = Path.Combine(root, "CloudImports", Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(wrongName, originalPlan);
            var oversized = Path.Combine(root, "CloudImports", Guid.NewGuid().ToString("N") + ".json");
            using (var stream = new FileStream(oversized, FileMode.CreateNew)) stream.SetLength(64L * 1024 * 1024 + 1);
            Assert.AreEqual(1, reopened.Read().Count);
            Assert.AreEqual(2, reopened.SkippedRecords);
            Assert.IsTrue(File.Exists(wrongName)); Assert.AreEqual(64L * 1024 * 1024 + 1, new FileInfo(oversized).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FilteredCloudHistoryKeepsOldInterruptedReceiptsAheadOfCompletedAndOtherAccounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-cloud-import-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new CloudObject("source-version", "Source/a.bin", 10, new string('a', 40), DateTimeOffset.UtcNow);
            var copied = source with { FileId = "copied-version", Key = "CloudBay/Destination/a.bin" };
            var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), "bucket", "Source/", Path.Combine(root, "Destination"), [new("a.bin", source)], 1, 10);
            var identity = "account|bucket|CloudBay/|" + root;
            var interrupted = new CloudImportRecord(plan.JobId, DateTimeOffset.UtcNow.AddYears(-1), plan, identity, "Copying", new Dictionary<string, CloudObject>());
            var journal = new CloudImportJournal(root);
            journal.Save(interrupted);
            journal.SaveProgress(plan.JobId, source.FileId, copied);
            for (var i = 0; i < 60; i++)
            {
                var completePlan = plan with { JobId = Guid.NewGuid().ToString("N") };
                journal.Save(interrupted with { Id = completePlan.JobId, Plan = completePlan, StartedUtc = DateTimeOffset.UtcNow.AddMinutes(i),
                    State = "Completed", Completed = new Dictionary<string, CloudObject> { [source.FileId] = copied } });
                var foreignPlan = plan with { JobId = Guid.NewGuid().ToString("N") };
                journal.Save(interrupted with { Id = foreignPlan.JobId, Plan = foreignPlan, StartedUtc = DateTimeOffset.UtcNow.AddYears(1), DestinationIdentity = "other|bucket|CloudBay/|" + root });
            }
            var history = journal.Read(identity);
            Assert.AreEqual(50, history.Count);
            Assert.AreEqual(interrupted.Id, history[0].Id);
            Assert.AreEqual("Needs review", history[0].State);
            Assert.AreEqual(copied, history[0].Completed[source.FileId]);
            Assert.IsTrue(history.All(item => item.DestinationIdentity == identity));
            Assert.AreEqual(0, journal.SkippedRecords);
            Assert.AreEqual(121, Directory.GetFiles(Path.Combine(root, "CloudImports"), "*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CloudHistoryCursorAndExactRecoveryFindInterruptedCopiesBeyondTheFirstPage()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-cloud-import-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new CloudObject("source-version", "Source/a.bin", 10, new string('a', 40), DateTimeOffset.UtcNow);
            var copied = source with { FileId = "copied-version", Key = "CloudBay/Destination/a.bin" };
            var plan = new CloudImportPlan(Guid.NewGuid().ToString("N"), "bucket", "Source/", Path.Combine(root, "Destination"), [new("a.bin", source)], 1, 10);
            var journal = new CloudImportJournal(root);
            var identity = "account|bucket|CloudBay/|" + root;
            var time = DateTimeOffset.UtcNow.AddYears(-1);
            var ids = Enumerable.Range(1, 61).Select(number => number.ToString("x32")).ToArray();
            foreach (var id in ids) journal.Save(new(id, time, plan with { JobId = id }, identity, "Copying", new Dictionary<string, CloudObject>()));
            journal.SaveProgress(ids[^1], source.FileId, copied);
            var first = journal.Read(identity);
            Assert.IsTrue(journal.HasMoreRecords);
            CollectionAssert.AreEqual(ids.Take(50).ToArray(), first.Select(item => item.Id).ToArray());
            var last = first[^1];
            var second = journal.Read(identity, new(last.State == "Completed", last.StartedUtc, last.Id));
            Assert.IsFalse(journal.HasMoreRecords);
            CollectionAssert.AreEqual(ids.Skip(50).ToArray(), second.Select(item => item.Id).ToArray());
            var older = journal.Read(identity, recordId: ids[^1]).Single();
            Assert.AreEqual(copied, older.Completed[source.FileId], "Exact recovery must merge the older operation's durable receipt independently of the latest page.");
            Assert.AreEqual(0, journal.Read("other|bucket|CloudBay/|" + root, recordId: ids[^1]).Count);
            Assert.ThrowsException<ArgumentException>(() => journal.Read(recordId: "../escape"));
            Assert.AreEqual(61, Directory.GetFiles(Path.Combine(root, "CloudImports"), "*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SourceStore(IReadOnlyList<CloudObject> objects, bool failAfterListing = false) : ICloudStore
    {
        public async IAsyncEnumerable<CloudObject> ListCurrentAsync(string bucketId, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in objects) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); yield return item; }
            if (failAfterListing) throw new IOException("Interrupted source listing.");
        }
        public IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix, CancellationToken cancellationToken = default) => ListCurrentAsync(bucketId, prefix, cancellationToken);
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1, DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

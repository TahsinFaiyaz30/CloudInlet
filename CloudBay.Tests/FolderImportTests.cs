using CloudBay.Application;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class FolderImportTests
{
    [TestMethod]
    public async Task ReviewedImportRetainsSourceAndDestinationCollisionsAndResumesWithoutDuplicateCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "source content");
            await File.WriteAllTextAsync(Path.Combine(destination, "report.txt"), "existing destination");
            var plan = FolderImport.Preview(source, destination);
            await FolderImport.ExecuteAsync(plan);
            await FolderImport.ExecuteAsync(FolderImport.Preview(source, destination));
            Assert.AreEqual("source content", await File.ReadAllTextAsync(Path.Combine(source, "report.txt")));
            Assert.AreEqual("source content", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            var conflicts = Directory.GetFiles(destination, "*conflict*");
            Assert.AreEqual(1, conflicts.Length, "Explicit retry must retain the single existing conflict without recopying it.");
            Assert.AreEqual("existing destination", await File.ReadAllTextAsync(conflicts[0]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task ChangedSourceCannotExecuteStaleImportReview()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "report.txt");
            await File.WriteAllTextAsync(file, "original");
            var plan = FolderImport.Preview(source, destination);
            var modified = File.GetLastWriteTimeUtc(file);
            await File.WriteAllTextAsync(file, "new edit");
            File.SetLastWriteTimeUtc(file, modified);
            await Assert.ThrowsExceptionAsync<IOException>(() => FolderImport.ExecuteAsync(plan));
            Assert.IsFalse(Directory.Exists(destination), "A stale review must fail before destination writes.");
            Assert.AreEqual("new edit", await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SaveAfterVerifiedCopyCannotBecomeTheBaselineForWindowsFolderRedirection()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "report.txt");
            await File.WriteAllTextAsync(file, "alpha");
            var modified = File.GetLastWriteTimeUtc(file);
            var verified = await FolderImport.ExecuteAsync(FolderImport.Preview(source, destination));
            VerifiedTreeCopy.EnsureUnchanged(source, verified);
            // This is the gap after copy returns, before the mapping guard runs.
            await File.WriteAllTextAsync(file, "bravo");
            File.SetLastWriteTimeUtc(file, modified);
            Assert.ThrowsException<IOException>(() => VerifiedTreeCopy.EnsureUnchanged(source, verified));
            Assert.AreEqual("bravo", await File.ReadAllTextAsync(file));
            Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InterruptedImportJournalNeedsExplicitReviewAndPreservesOriginalPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 3, 300, 3000, true);
            var record = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, plan, "account|bucket|CloudBay/|" + root, "Copying");
            journal.Save(record);
            var reopened = new ImportJournal(root).Read().Single();
            Assert.AreEqual(plan, reopened.Plan);
            Assert.AreEqual("Needs review", reopened.State);
            StringAssert.Contains(reopened.Error!, "interrupted");
            Assert.IsFalse(Directory.Exists(plan.SourcePath));
            Assert.IsFalse(Directory.Exists(plan.DestinationPath), "Reading recovery history never starts an import.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DamagedOrMisnamedFolderImportRecordsAreRetainedWithoutCreatingImportJobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 1, 10, 1000, false);
            var record = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, plan, "account|bucket|CloudBay/|" + root, "Copying");
            journal.Save(record);
            var directory = Path.Combine(root, "Imports");
            var validPath = Path.Combine(directory, record.Id + ".json");
            var bytes = File.ReadAllBytes(validPath);
            File.WriteAllBytes(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), bytes);
            File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "{broken");
            var oversized = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            using (var stream = new FileStream(oversized, FileMode.CreateNew)) stream.SetLength(1024 * 1024 + 1);
            Assert.AreEqual(1, journal.Read().Count);
            Assert.AreEqual(3, journal.SkippedRecords);
            Assert.AreEqual(4, Directory.GetFiles(directory, "*.json").Length);
            Assert.IsFalse(Directory.Exists(plan.DestinationPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FilteredFolderHistoryKeepsOldInterruptedWorkAheadOfNewerCompletedAndOtherAccountImports()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var identity = "account|bucket|CloudBay/|" + root;
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 1, 10, 1000, false);
            var interrupted = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddYears(-1), plan, identity, "Copying");
            journal.Save(interrupted);
            for (var i = 0; i < 60; i++)
            {
                journal.Save(interrupted with { Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow.AddMinutes(i), State = "Completed" });
                journal.Save(interrupted with { Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow.AddYears(1), DestinationIdentity = "other|bucket|CloudBay/|" + root });
            }
            var history = journal.Read(identity);
            Assert.AreEqual(50, history.Count);
            Assert.AreEqual(interrupted.Id, history[0].Id);
            Assert.AreEqual("Needs review", history[0].State);
            Assert.IsTrue(history.All(item => item.DestinationIdentity == identity));
            Assert.AreEqual(121, Directory.GetFiles(Path.Combine(root, "Imports"), "*.json").Length, "Bounding history must never remove recovery records.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AggregateHistoryBudgetEvictsCompletedEntriesBeforeInterruptedWork()
    {
        var plan = new FolderImportPlan("C:\\source", "C:\\destination", new string('a', 64), 0, 0, null, false);
        var pending = new FolderImportRecord("pending", DateTimeOffset.UtcNow.AddYears(-1), plan, "identity", "Needs review");
        var olderCompleted = pending with { Id = "older-completed", StartedUtc = DateTimeOffset.UtcNow, State = "Completed" };
        var newerCompleted = olderCompleted with { Id = "newer-completed", StartedUtc = DateTimeOffset.UtcNow.AddDays(1) };
        var selection = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id);
        selection.Add(pending, 32L * 1024 * 1024);
        selection.Add(olderCompleted, 80L * 1024 * 1024);
        selection.Add(newerCompleted, 80L * 1024 * 1024);
        Assert.IsTrue(selection.HasMore, "A byte-limited page can contain fewer than 50 records while older entries remain.");
        CollectionAssert.AreEqual(new[] { pending.Id, newerCompleted.Id }, selection.Records.Select(item => item.Id).ToArray());
        selection.Add(pending with { Id = "second-pending", StartedUtc = pending.StartedUtc.AddDays(-1) }, 80L * 1024 * 1024);
        Assert.AreEqual(2, selection.Records.Count);
        Assert.IsTrue(selection.Records.All(item => item.State == "Needs review"));
    }

    [TestMethod]
    public void ByteLimitedHistoryPageCannotSkipALargeMiddleRecordWhenAdvancingItsCursor()
    {
        var plan = new FolderImportPlan("C:\\source", "C:\\destination", new string('a', 64), 0, 0, null, false);
        var first = new FolderImportRecord("first", DateTimeOffset.UtcNow, plan, "identity", "Needs review");
        var middle = first with { Id = "middle", StartedUtc = first.StartedUtc.AddMinutes(-1) };
        var last = first with { Id = "last", StartedUtc = first.StartedUtc.AddMinutes(-2) };
        var selection = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id);
        selection.Add(first, 80L * 1024 * 1024);
        selection.Add(middle, 80L * 1024 * 1024);
        selection.Add(last, 1);
        CollectionAssert.AreEqual(new[] { first.Id }, selection.Records.Select(item => item.Id).ToArray());
        Assert.IsTrue(selection.HasMore);
        var next = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id,
            new(false, first.StartedUtc, first.Id));
        next.Add(first, 80L * 1024 * 1024);
        next.Add(middle, 80L * 1024 * 1024);
        next.Add(last, 1);
        CollectionAssert.AreEqual(new[] { middle.Id, last.Id }, next.Records.Select(item => item.Id).ToArray());
        Assert.IsFalse(next.HasMore);
    }

    [TestMethod]
    public void FolderHistoryCursorIncludesEveryInterruptedRecordWithStableTimestampTies()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var identity = "account|bucket|CloudBay/|" + root;
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 0, 0, null, false);
            var time = DateTimeOffset.UtcNow.AddYears(-1);
            var ids = Enumerable.Range(1, 61).Select(number => number.ToString("x32")).ToArray();
            foreach (var id in ids) journal.Save(new(id, time, plan, identity, "Copying"));
            var completedId = Guid.NewGuid().ToString("N");
            journal.Save(new(completedId, DateTimeOffset.UtcNow, plan, identity, "Completed"));
            var first = journal.Read(identity);
            Assert.IsTrue(journal.HasMoreRecords);
            CollectionAssert.AreEqual(ids.Take(50).ToArray(), first.Select(item => item.Id).ToArray());
            var last = first[^1];
            var second = journal.Read(identity, new(last.State == "Completed", last.StartedUtc, last.Id));
            Assert.IsFalse(journal.HasMoreRecords);
            CollectionAssert.AreEqual(ids.Skip(50).Append(completedId).ToArray(), second.Select(item => item.Id).ToArray());
            CollectionAssert.AreEquivalent(ids, first.Concat(second).Where(item => item.State != "Completed").Select(item => item.Id).ToArray());
            Assert.AreEqual(ids[^1], journal.Read(identity, recordId: ids[^1]).Single().Id);
            Assert.AreEqual(0, journal.Read("other|bucket|CloudBay/|" + root, recordId: ids[^1]).Count);
            Assert.ThrowsException<ArgumentException>(() => journal.Read(recordId: "../escape"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    public void ImportRejectsNestedSourceAndDestinationBeforeCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBay-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.ThrowsException<IOException>(() => FolderImport.Preview(root, Path.Combine(root, "Child")));
            Assert.ThrowsException<IOException>(() => FolderImport.Preview(Path.Combine(root, "Child"), root));
        }
        finally { Directory.Delete(root, true); }
    }
}

using CloudInlet.Core;
using CloudInlet.Core.Transfers;
using CloudInlet.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ProgressPresentationTests
{
    [TestMethod]
    public void KnownByteProgressShowsMeasuredPercentAndBothSizes()
    {
        var progress = ProgressPresentation.ForBytes(1048576, 2097152);
        Assert.AreEqual(50d, progress.Value);
        Assert.IsFalse(progress.IsIndeterminate);
        Assert.AreEqual("50% · 1 MiB of 2 MiB", progress.Label);
        Assert.AreEqual("0% · 0 B of 2 MiB", ProgressPresentation.ForBytes(-1, 2097152).Label);
        Assert.AreEqual("100% · 2 MiB of 2 MiB", ProgressPresentation.ForBytes(long.MaxValue, 2097152).Label);
    }

    [TestMethod]
    public void UnknownTotalShowsOnlyMeasuredBytesAndCannotImplyCompletion()
    {
        var progress = ProgressPresentation.ForBytes(1048576, 0);
        Assert.IsTrue(progress.IsIndeterminate);
        Assert.AreEqual("1 MiB transferred", progress.Label);
        Assert.IsFalse(progress.Label.Contains('%'));
        Assert.AreEqual("Waiting for transfer size", ProgressPresentation.ForBytes(0, 0).Label);
    }

    [TestMethod]
    public void IncompleteTransferNeverRoundsToOneHundredPercent()
    {
        foreach (var pair in new[] { (Completed: 999999L, Total: 1000000L), (Completed: long.MaxValue - 1, Total: long.MaxValue) })
        {
            var progress = ProgressPresentation.ForBytes(pair.Completed, pair.Total);
            Assert.IsTrue(progress.Value < 100);
            Assert.IsFalse(progress.Label.StartsWith("100%", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void PartialAggregateInventoryCannotClaimACompletionPercentage()
    {
        var progress = ProgressPresentation.ForSnapshot(new(ClientState.Syncing, "Discovering files", TransferredBytes: 1048576, TransferTotalBytes: 2097152)
            { TransferTotalKnown = false });
        Assert.IsTrue(progress.IsIndeterminate);
        Assert.AreEqual("1 MiB transferred · 2 MiB discovered so far", progress.Label);
        Assert.IsFalse(progress.Label.Contains('%'));
    }

    [TestMethod]
    public void KnownAggregatePreservesItsFullBatchRatio()
    {
        var snapshot = new SyncSnapshot(ClientState.Syncing, "Syncing files", TransferredBytes: 1048576, TransferTotalBytes: 4194304);
        Assert.IsTrue(snapshot.TransferTotalKnown);
        var progress = ProgressPresentation.ForSnapshot(snapshot);
        Assert.IsFalse(progress.IsIndeterminate);
        Assert.AreEqual(25d, progress.Value);
        Assert.AreEqual("25% · 1 MiB of 4 MiB", progress.Label);
    }

    [TestMethod]
    public void PreparationVerificationAndRetryDoNotPretendToMeasureTheirCompletion()
    {
        foreach (var phase in new[] { TransferPhase.Queued, TransferPhase.Hashing, TransferPhase.Verifying, TransferPhase.Retrying })
        {
            var progress = ProgressPresentation.ForTransfer(new("id", "root", "file", ActivityKind.Upload, phase, 1024, 1024));
            Assert.IsTrue(progress.IsIndeterminate, phase.ToString());
            Assert.AreEqual("", progress.Label, phase.ToString());
        }
        var paused = ProgressPresentation.ForTransfer(new("id", "root", "file", ActivityKind.Upload, TransferPhase.Paused, 1024, 2048));
        Assert.AreEqual("50% · 1 KiB of 2 KiB", paused.Label);
    }

    [TestMethod]
    public void CloudDiscoveryDoesNotUsePartialInventoryAsTheTotal()
    {
        var progress = ProgressPresentation.ForCloudJob(Job() with { DiscoveryComplete = false, RemainingBytes = 0 });
        Assert.IsTrue(progress.IsIndeterminate);
        Assert.AreEqual("1 KiB transferred · 2 KiB discovered so far", progress.Label);
        Assert.IsFalse(progress.Label.Contains('%'));
    }

    [TestMethod]
    public void CloudJobCountsSkippedWorkWithoutCallingItTransferred()
    {
        var progress = ProgressPresentation.ForCloudJob(Job() with { SkippedFiles = 1, RemainingBytes = 0 });
        Assert.AreEqual(100d, progress.Value);
        Assert.AreEqual("100% · 2 KiB of 2 KiB processed", progress.Label);
    }

    [TestMethod]
    public void ZeroByteFilesUseProcessedFileCountAndEmptyJobsWaitForConfirmedCompletion()
    {
        var job = Job() with { TotalBytes = 0, TransferredBytes = 0, RemainingBytes = 0, FileCount = 4, CompletedFiles = 1, SkippedFiles = 1 };
        Assert.AreEqual("50% · 2 of 4 files processed", ProgressPresentation.ForCloudJob(job).Label);
        var empty = job with { FileCount = 0, CompletedFiles = 0, SkippedFiles = 0 };
        Assert.IsTrue(ProgressPresentation.ForCloudJob(empty).IsIndeterminate);
        Assert.AreEqual("100% · No files to transfer", ProgressPresentation.ForCloudJob(empty with { State = TransferJobState.Completed }).Label);
    }

    private static TransferJobSnapshot Job() => new(
        new("job", new("b2", "account", "bucket", "", "", "Source"), new("onedrive", "account", "drive", "", "", "Destination"),
            TransferOperation.Copy, TransferConflictPolicy.Skip, [], DateTimeOffset.UtcNow),
        TransferJobState.Running, true, 2, 1, 0, 2048, 1024, 1024, 1, 0, []);
}

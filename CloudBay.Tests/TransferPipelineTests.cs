using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class TransferPipelineTests
{
    [TestMethod]
    public async Task BlockedLargeHashesDoNotDelayTinyUploadPayloads()
    {
        await VerifyMixedPreparationAsync(UploadMode.Manual, 2);
    }

    [DataTestMethod]
    [DataRow(UploadMode.Manual, 1)]
    [DataRow(UploadMode.Manual, 3)]
    [DataRow(UploadMode.Intelligent, 3)]
    [DataRow(UploadMode.MaximumThroughput, 3)]
    public async Task MixedPreparationPreservesExistingUploadWorkerPreferences(UploadMode mode, int manualWorkers)
    {
        await VerifyMixedPreparationAsync(mode, manualWorkers);
    }

    private static async Task VerifyMixedPreparationAsync(UploadMode mode, int manualWorkers)
    {
        await using var h = new Harness(manualWorkers, mode);
        for (var index = 0; index < 2; index++)
        {
            await using var large = new FileStream(h.Path($"a-large-{index}.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            large.SetLength(8 * 1024 * 1024 + 1);
        }
        var expectedWorkers = TransferLimits.For(h.Settings).Uploads;
        for (var index = 0; index < expectedWorkers + 2; index++) await File.WriteAllTextAsync(h.Path($"z-tiny-{index}.txt"), "tiny snapshot");
        h.Cloud.HoldLargePreparation = true;
        h.Cloud.HoldUpload = true;
        var work = h.Engine.SyncNowAsync();
        await h.Cloud.LargePreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Cloud.TinyPayloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.WaitSnapshotAsync(value => value.Transfers.Count(item => item.Phase == TransferPhase.Uploading) == expectedWorkers);
        Assert.AreEqual(expectedWorkers, h.Cloud.ActiveUploads, "Prepared tiny files must supply the existing configured network slots while the large hash remains blocked.");
        Assert.AreEqual(0, h.Cloud.PreparedLargeFiles, "The small payload must start before any blocked large checksum completes.");
        h.Cloud.ReleaseUpload.TrySetResult();
        h.Cloud.ReleaseLargePreparation.TrySetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(h.Cloud.MaximumActiveUploads <= expectedWorkers, "Hash preparation lanes must never override upload concurrency preferences.");
        Assert.AreEqual(expectedWorkers + 4, h.Cloud.UploadedCount);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task NextLocalSourceIsPreparedWhileTheActiveUploadStillOwnsItsNetworkSlot()
    {
        await using var h = new Harness(1);
        for (var index = 0; index < 3; index++) await File.WriteAllTextAsync(h.Path($"file-{index}.txt"), "prepared locked snapshot");
        h.Cloud.HoldUpload = true;
        var work = h.Engine.SyncNowAsync();
        await h.Cloud.FirstUpload.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Cloud.SecondPreparation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, h.Cloud.UploadedCount, "Preparation of the next source must overlap the current network request.");
        var ready = h.Cloud.PreparedPaths.Skip(1).First();
        Assert.ThrowsException<IOException>(() => { using var changed = new FileStream(ready, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        h.Cloud.ReleaseUpload.TrySetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(3, h.Cloud.UploadedCount);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task NextDownloadStartsWhileThePriorProviderChecksumIsBlockedWithBoundedPendingStaging()
    {
        await using var h = new Harness(1);
        for (var index = 0; index < 8; index++) h.Cloud.Seed($"remote-{index}.txt", "verified cloud snapshot");
        h.Cloud.HoldDownloadCompletion = true;
        var work = h.Engine.SyncNowAsync();
        var snapshot = await h.WaitSnapshotAsync(value => value.ActiveTransfers == 4 &&
            value.Transfers.Count(item => item.Phase == TransferPhase.Verifying) == 4);
        Assert.AreEqual(4, h.Cloud.DownloadsStarted.Count, "The configured network worker must feed its bounded verification queue instead of waiting for the first checksum.");
        Assert.AreEqual(4, snapshot.QueuedTransfers);
        Assert.AreEqual(0, h.Manifest.ReadAll().Count, "Payload completion cannot establish a verified native baseline.");
        Assert.AreEqual(0, h.Placeholders.Marked);
        Assert.AreEqual(4, Directory.GetFiles(h.Path(".cloudbay/transfers"), "*.part").Length);
        h.Cloud.ReleaseDownload.TrySetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(8, h.Cloud.DownloadsStarted.Count);
        Assert.AreEqual(8, h.Manifest.ReadAll().Count);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task WindowsFinalizationDoesNotKeepTheNextDownloadNetworkSlotOccupied()
    {
        await using var h = new Harness(1);
        for (var index = 0; index < 3; index++) h.Cloud.Seed($"remote-{index}.txt", "verified bytes awaiting native marking");
        h.Placeholders.HoldMarks = true;
        var work = h.Engine.SyncNowAsync();
        await h.Placeholders.MarkStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.WaitSnapshotAsync(value => value.Transfers.Count(item => item.Phase == TransferPhase.Verifying) == 3);
        Assert.AreEqual(3, h.Cloud.DownloadsStarted.Count, "Native marking must not serialize the next file's HTTPS request.");
        Assert.AreEqual(1, h.Manifest.ReadAll().Count);
        Assert.IsTrue(h.Manifest.ReadAll().Single().Value.NativeMarkPending);
        h.Placeholders.ReleaseMarks.TrySetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(3, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task PauseDrainsDeferredDownloadTasksAndHandlesWhileRetainingVersionBoundCheckpoints()
    {
        await using var h = new Harness(1);
        for (var index = 0; index < 8; index++) h.Cloud.Seed($"remote-{index}.txt", "recoverable completed source ranges");
        h.Cloud.HoldDownloadCompletion = true;
        h.Cloud.CheckpointFullDownload = true;
        var work = h.Engine.SyncNowAsync();
        await h.WaitSnapshotAsync(value => value.ActiveTransfers == 4 &&
            value.Transfers.Count(item => item.Phase == TransferPhase.Verifying) == 4);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Engine.QuiesceAsync(timeout.Token);
        await work.WaitAsync(timeout.Token);
        Assert.AreEqual(0, h.Manifest.ReadAll().Count);
        Assert.AreEqual(0, h.Cloud.ActiveDownloads, "Quiescence must await every provider operation before disposing staging.");
        var folder = h.Path(".cloudbay/transfers");
        Assert.AreEqual(4, Directory.GetFiles(folder, "*.part.json").Length);
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.lock").Length);
        foreach (var file in Directory.GetFiles(folder, "*.part"))
        { using var exclusivelyOwned = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        Assert.AreEqual(ClientState.Paused, h.Latest.State);
        h.Cloud.HoldDownloadCompletion = false;
        h.Cloud.ReleaseDownload.TrySetResult();
        h.Engine.Resume();
        await h.Engine.SyncNowAsync(timeout.Token);
        Assert.AreEqual(4, h.Cloud.ReusedDownloadRanges);
        Assert.AreEqual(8L * System.Text.Encoding.UTF8.GetByteCount("recoverable completed source ranges"), h.Cloud.DownloadedPayloadBytes,
            "Resuming the drained pipeline must not download its already acknowledged ranges again.");
        Assert.AreEqual(8, h.Manifest.ReadAll().Count);
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.part.json").Length);
    }

    [TestMethod]
    public async Task ActiveUploadsShowPathsAndQueueWhileIndependentVerificationKeepsFilesDirty()
    {
        await using var h = new Harness(2);
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(h.Path($"file-{i}.txt"), "test content");
        h.Cloud.HoldVerification = true;
        var work = h.Engine.SyncNowAsync();
        await h.Cloud.TwoVerifications.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = await h.WaitSnapshotAsync(value => value.Transfers.Count(item => item.Phase == TransferPhase.Verifying) == 5);
        Assert.AreEqual(5, h.Cloud.UploadedCount, "Waiting for verification must not occupy the network upload workers.");
        Assert.AreEqual(5, snapshot.ActiveTransfers);
        Assert.AreEqual(0, snapshot.QueuedTransfers);
        Assert.AreEqual(5, snapshot.Transfers.Count(item => item.Phase == TransferPhase.Verifying));
        Assert.IsTrue(snapshot.Transfers.All(item => item.RootName == "Personal backup" && item.RelativePath.StartsWith("file-")));
        Assert.AreEqual(0, h.Manifest.ReadAll().Count, "An upload is not acknowledged locally before independent verification.");
        Assert.AreEqual(0, h.Placeholders.Marked);
        h.Cloud.Release.TrySetResult();
        await work;
        Assert.AreEqual(5, h.Manifest.ReadAll().Count);
        Assert.AreEqual(5, h.Placeholders.Marked);
        Assert.AreEqual(0, h.Latest.Transfers.Count);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task DeferredVerificationBackpressureBoundsAcknowledgmentsWithoutLosingTheQueue()
    {
        await using var h = new Harness(2);
        for (var i = 0; i < 40; i++) await File.WriteAllTextAsync(h.Path($"file-{i}.txt"), "tiny file");
        h.Cloud.HoldVerification = true;
        h.Cloud.VerificationDelay = TimeSpan.FromMilliseconds(20);
        var work = h.Engine.SyncNowAsync();
        // Active includes hashing and uploading. Wait until every backlog slot has
        // reached verification so the two final writers have acknowledged their uploads.
        var snapshot = await h.WaitSnapshotAsync(value => value.ActiveTransfers == 8 &&
            value.Transfers.Count(item => item.Phase == TransferPhase.Verifying) == 8);
        Assert.AreEqual(8, h.Cloud.UploadedCount);
        Assert.AreEqual(32, snapshot.QueuedTransfers);
        Assert.AreEqual(40, snapshot.Pending);
        Assert.AreEqual(0, h.Manifest.ReadAll().Count);
        h.Cloud.Release.TrySetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        // Two verifying files, four buffered acknowledgments and at most two writers awaiting
        // channel admission bound the live backlog even when verification is slower than upload.
        Assert.IsTrue(h.Cloud.MaximumPendingVerification <= 8, $"Unverified upload backlog grew to {h.Cloud.MaximumPendingVerification}.");
        Assert.AreEqual(40, h.Cloud.UploadedCount);
        Assert.AreEqual(40, h.Manifest.ReadAll().Count);
        Assert.AreEqual(40, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task PauseQuiescesUploadAndVerificationWorkersThenResumeReconcilesAcknowledgedVersions()
    {
        await using var h = new Harness(2);
        for (var i = 0; i < 20; i++) await File.WriteAllTextAsync(h.Path($"file-{i}.txt"), "retained local file");
        h.Cloud.HoldVerification = true;
        var work = h.Engine.SyncNowAsync();
        await h.WaitSnapshotAsync(value => value.ActiveTransfers == 8 &&
            value.Transfers.Count(item => item.Phase == TransferPhase.Verifying) == 8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Engine.QuiesceAsync(timeout.Token);
        await work.WaitAsync(timeout.Token);
        var acknowledged = h.Cloud.UploadedCount;
        Assert.AreEqual(8, acknowledged);
        Assert.AreEqual(0, h.Manifest.ReadAll().Count);
        Assert.AreEqual(0, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.Paused, h.Latest.State);
        Assert.AreEqual(0, h.Latest.ActiveTransfers);
        Assert.AreEqual(20, h.Latest.QueuedTransfers);
        Assert.IsTrue(h.Latest.Transfers.All(item => item.Phase == TransferPhase.Paused));
        for (var index = 0; index < 20; index++)
        {
            using var released = new FileStream(h.Path($"file-{index}.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        h.Cloud.HoldVerification = false;
        h.Cloud.Release.TrySetResult();
        h.Engine.Resume();
        await h.Engine.SyncNowAsync(timeout.Token);
        Assert.AreEqual(20, h.Manifest.ReadAll().Count);
        Assert.AreEqual(20, h.Placeholders.Marked);
        Assert.AreEqual(20, h.Cloud.UploadedCount, "Previously acknowledged matching versions should be adopted instead of uploaded again.");
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task EditDuringDeferredVerificationReleasesSourceHandleAndRetainsTheNewEdit()
    {
        await using var h = new Harness(1);
        const string original = "original snapshot";
        const string edited = "the user saved a longer edited document";
        var path = h.Path("document.txt");
        await File.WriteAllTextAsync(path, original);
        h.Cloud.HoldVerification = true;
        var work = h.Engine.SyncNowAsync();
        await h.Cloud.OneVerification.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(path, edited);
        h.Cloud.Release.TrySetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(edited, await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, h.Placeholders.Marked, "A later local save must not be marked in sync with the older cloud snapshot.");
        var baseline = h.Manifest.ReadAll()["document.txt"];
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(original), baseline.LocalSize);
        Assert.AreEqual(Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(original))).ToLowerInvariant(), baseline.Remote.Sha1);
        h.Cloud.HoldVerification = false;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(2, h.Cloud.UploadedCount, "The deferred local edit must be found and transferred on the next reconciliation.");
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(edited), h.Manifest.ReadAll()["document.txt"].LocalSize);
        Assert.AreEqual(1, h.Placeholders.Marked);
    }

    [TestMethod]
    public async Task EditDuringRemoteAdoptionKeepsTheVerifiedBaselineAndUploadsTheEditNextTime()
    {
        await using var h = new Harness(1);
        const string original = "already uploaded";
        const string edited = "a newer user save arrived while native marking completed";
        var remote = h.Cloud.Seed("document.txt", original);
        var path = h.Path("document.txt");
        await File.WriteAllTextAsync(path, original);
        h.Placeholders.OnMark = marked =>
        {
            h.Placeholders.OnMark = null;
            File.WriteAllText(marked, edited);
        };
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(edited, await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, h.Cloud.UploadedCount, "The matching original cloud version can be adopted without a duplicate upload.");
        var baseline = h.Manifest.ReadAll()["document.txt"];
        Assert.AreEqual(remote.Size, baseline.LocalSize);
        Assert.AreEqual(remote.ModifiedUtc, baseline.LocalWriteUtc);
        Assert.AreEqual(remote.FileId, baseline.Remote.FileId);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(edited, await File.ReadAllTextAsync(path));
        Assert.AreEqual(1, h.Cloud.UploadedCount, "The save arriving after the verified snapshot must remain a local change.");
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(edited), h.Manifest.ReadAll()["document.txt"].LocalSize);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task IncorrectUploadAcknowledgmentNeverMarksLocalFileClean()
    {
        await using var h = new Harness(1);
        await File.WriteAllTextAsync(h.Path("important.txt"), "keep this");
        h.Cloud.CorruptAcknowledgment = true;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("keep this", await File.ReadAllTextAsync(h.Path("important.txt")));
        Assert.AreEqual(0, h.Manifest.ReadAll().Count);
        Assert.AreEqual(0, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.Attention, h.Latest.State);
        Assert.AreEqual(TransferPhase.Retrying, h.Latest.Transfers.Single().Phase);
    }

    [TestMethod]
    public async Task VerificationFailureRetainsDirtyFileAndOriginalBaseline()
    {
        await using var h = new Harness(2);
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(h.Path($"important-{i}.txt"), "keep this");
        h.Cloud.FailVerification = true;
        await h.Engine.SyncNowAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, h.Manifest.ReadAll().Count);
        Assert.AreEqual(0, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.Attention, h.Latest.State);
        Assert.AreEqual(5, h.Cloud.UploadedCount);
        Assert.AreEqual(5, h.Latest.Transfers.Count(item => item.Phase == TransferPhase.Retrying));
        for (var i = 0; i < 5; i++) Assert.AreEqual("keep this", await File.ReadAllTextAsync(h.Path($"important-{i}.txt")));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VerifiedUploadSurvivesMarkFailureAndRestartsWithoutCreatingAnotherCloudVersion(bool cloudFilesHresult)
    {
        await using var h = new Harness(1);
        await File.WriteAllTextAsync(h.Path("document.txt"), "a verified cloud snapshot");
        h.Placeholders.FailMarks = 1;
        if (cloudFilesHresult)
            h.Placeholders.MarkFailure = new System.Runtime.InteropServices.COMException("The requested Cloud Files operation is unsupported.", unchecked((int)0x8007017C));
        h.Placeholders.OnMark = _ =>
        {
            h.Placeholders.OnMark = null;
            Assert.IsTrue(h.Manifest.ReadAll()["document.txt"].NativeMarkPending,
                "The verified immutable version and pending local status must be durable before marking begins.");
        };
        await h.Engine.SyncNowAsync();
        var verified = h.Manifest.ReadAll()["document.txt"];
        Assert.IsTrue(verified.NativeMarkPending);
        Assert.AreEqual(1, h.Cloud.UploadedCount);
        Assert.AreEqual(0, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.Attention, h.Latest.State);
        Assert.AreEqual(TransferPhase.Retrying, h.Latest.Transfers.Single().Phase);
        var firstHistory = h.Activity.ToArray();
        Assert.AreEqual(1, firstHistory.Count(item => item.Kind == ActivityKind.Upload));
        Assert.IsTrue(firstHistory.Single(item => item.Kind == ActivityKind.Upload).Message.Contains("verified", StringComparison.Ordinal));
        Assert.IsTrue(firstHistory.Single(item => item.Kind == ActivityKind.Error).Message.Contains("Windows could not update", StringComparison.Ordinal));

        await h.RestartAsync();
        Assert.IsTrue(h.Manifest.ReadAll()["document.txt"].NativeMarkPending);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(1, h.Cloud.UploadedCount, "A Windows marking retry must reuse the same verified B2 version.");
        Assert.AreEqual(verified.Remote.FileId, h.Manifest.ReadAll()["document.txt"].Remote.FileId);
        Assert.IsFalse(h.Manifest.ReadAll()["document.txt"].NativeMarkPending);
        Assert.AreEqual(2, h.Placeholders.MarkAttempts);
        Assert.AreEqual(1, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
        Assert.AreEqual(0, h.Latest.Transfers.Count);
        Assert.IsTrue(h.Snapshots.Any(value => value.Message == "Completing Windows file sync status" &&
            value.Transfers.Any(item => item.Phase == TransferPhase.Verifying)));
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(1, h.Cloud.UploadedCount);
        Assert.AreEqual(2, h.Placeholders.MarkAttempts, "Completed native metadata must not stay permanently pending.");
        Assert.AreEqual(1, h.Activity.Count(item => item.Kind == ActivityKind.Upload), "Metadata retries must not fabricate uploaded-file history.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingMarkHashCheckKeepsLaterLocalEditsDirtyEvenWhenMetadataMatches(bool preserveTimestamp)
    {
        await using var h = new Harness(1);
        var path = h.Path("document.txt");
        await File.WriteAllTextAsync(path, "original");
        h.Placeholders.FailMarks = 1;
        await h.Engine.SyncNowAsync();
        var previous = h.Manifest.ReadAll()["document.txt"];
        await File.WriteAllTextAsync(path, "modified"); // Same length; a timestamp-preserving save requires the SHA1 check.
        if (preserveTimestamp) File.SetLastWriteTimeUtc(path, previous.LocalWriteUtc.UtcDateTime);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("modified", await File.ReadAllTextAsync(path));
        Assert.AreEqual(2, h.Cloud.UploadedCount, "Changed bytes must be uploaded before they can be marked clean.");
        var current = h.Manifest.ReadAll()["document.txt"];
        Assert.AreNotEqual(previous.Remote.FileId, current.Remote.FileId);
        Assert.AreNotEqual(previous.Remote.Sha1, current.Remote.Sha1);
        Assert.IsFalse(current.NativeMarkPending);
        Assert.AreEqual(1, h.Placeholders.Marked);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task EditDuringPendingMarkFailureKeepsTheVerifiedVersionAndUploadsOnlyTheNewSave()
    {
        await using var h = new Harness(1);
        var path = h.Path("document.txt");
        await File.WriteAllTextAsync(path, "old snapshot");
        h.Placeholders.FailMarks = 1;
        await h.Engine.SyncNowAsync();
        var previous = h.Manifest.ReadAll()["document.txt"];
        h.Placeholders.OnMark = marked =>
        {
            h.Placeholders.OnMark = null;
            File.WriteAllText(marked, "a later user save remains dirty");
            throw new IOException("The protected source changed before native marking finished.");
        };
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(1, h.Cloud.UploadedCount, "Retrying unchanged uploaded bytes must perform metadata work only.");
        Assert.AreEqual("a later user save remains dirty", await File.ReadAllTextAsync(path));
        Assert.AreEqual(previous.Remote.FileId, h.Manifest.ReadAll()["document.txt"].Remote.FileId);
        Assert.IsTrue(h.Manifest.ReadAll()["document.txt"].NativeMarkPending);
        Assert.AreEqual(ClientState.Attention, h.Latest.State);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(2, h.Cloud.UploadedCount);
        Assert.AreEqual("a later user save remains dirty", await File.ReadAllTextAsync(path));
        Assert.IsFalse(h.Manifest.ReadAll()["document.txt"].NativeMarkPending);
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task ReadOnlySourceRetainsItsAttributeAcrossVerifiedUploadAndMetadataRetry()
    {
        await using var h = new Harness(1);
        var path = h.Path("read-only.txt");
        await File.WriteAllTextAsync(path, "important read-only source");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            h.Placeholders.FailMarks = 1;
            await h.Engine.SyncNowAsync();
            await h.Engine.SyncNowAsync();
            Assert.AreEqual(1, h.Cloud.UploadedCount);
            Assert.IsTrue((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
            Assert.AreEqual("important read-only source", await File.ReadAllTextAsync(path));
            Assert.IsFalse(h.Manifest.ReadAll()["read-only.txt"].NativeMarkPending);
        }
        finally { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
    }

    [TestMethod]
    public async Task LockedLocalEditCannotBecomeACloudDeletionOrACommittedOlderSnapshot()
    {
        await using var h = new Harness(1);
        var path = h.Path("locked.txt");
        await File.WriteAllTextAsync(path, "original content");
        await h.Engine.SyncNowAsync();
        var previous = h.Manifest.ReadAll()["locked.txt"];
        await File.WriteAllTextAsync(path, "a later locked local edit");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await h.Engine.SyncNowAsync();
            Assert.AreEqual(ClientState.Attention, h.Latest.State);
            Assert.AreEqual(1, h.Cloud.UploadedCount);
            Assert.AreEqual(0, h.Cloud.Hidden.Count);
            Assert.AreEqual(previous.Remote.FileId, h.Manifest.ReadAll()["locked.txt"].Remote.FileId);
        }
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(2, h.Cloud.UploadedCount);
        Assert.AreEqual("a later locked local edit", await File.ReadAllTextAsync(path));
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public async Task NearMaximumLengthFileNamePreservesBothCopiesWhenCloudAndLocalChangesConflict()
    {
        await using var h = new Harness(1);
        var name = new string('a', 246) + ".txt";
        var path = h.Path(name);
        await File.WriteAllTextAsync(path, "original local snapshot");
        await h.Engine.SyncNowAsync();
        await File.WriteAllTextAsync(path, "newer local content");
        h.Cloud.Seed(name, "newer cloud content");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("newer cloud content", await File.ReadAllTextAsync(path));
        var conflict = Directory.GetFiles(h.Root, "* (conflict *).txt").Single();
        Assert.IsTrue(System.IO.Path.GetFileName(conflict).Length <= 255);
        Assert.AreEqual("newer local content", await File.ReadAllTextAsync(conflict));
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
    }

    [TestMethod]
    public void LegacyManifestAddsPendingMetadataStateWithoutChangingExistingBaselines()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBay.LegacyManifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var database = System.IO.Path.Combine(folder, "legacy.sqlite");
        var remote = new CloudObject("verified-version", "CloudBay/legacy.txt", 17,
            new string('a', 40), DateTimeOffset.UnixEpoch);
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE files(path TEXT PRIMARY KEY COLLATE NOCASE,remote TEXT NOT NULL,local_size INTEGER NOT NULL,local_write TEXT NOT NULL);" +
                    " CREATE TABLE directories(path TEXT PRIMARY KEY COLLATE NOCASE,remote TEXT NOT NULL);" +
                    " INSERT INTO files(path,remote,local_size,local_write) VALUES('legacy.txt',$remote,17,$write);";
                command.Parameters.AddWithValue("$remote", JsonSerializer.Serialize(remote));
                command.Parameters.AddWithValue("$write", DateTimeOffset.UnixEpoch.ToString("O"));
                command.ExecuteNonQuery();
            }
            var migrated = new SyncManifest(database);
            var original = migrated.ReadAll()["legacy.txt"];
            Assert.AreEqual(remote, original.Remote);
            Assert.IsFalse(original.NativeMarkPending, "Older baselines have no unfinished native status operation.");
            migrated.Put(original with { NativeMarkPending = true });
            var reopened = new SyncManifest(database);
            Assert.IsTrue(reopened.ReadAll()["legacy.txt"].NativeMarkPending);
            reopened.Put(original);
            Assert.IsFalse(new SyncManifest(database).ReadAll()["legacy.txt"].NativeMarkPending);
            reopened.Put(original with { NativeMarkPending = true });
            reopened.Remove("LEGACY.TXT");
            reopened.Put(original);
            Assert.IsFalse(new SyncManifest(database).ReadAll()["legacy.txt"].NativeMarkPending,
                "Deleting a file baseline must also remove its old case-insensitive pending mark.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public async Task InterruptedDownloadKeepsExistingFileAndReusesVersionBoundStaging()
    {
        await using var h = new Harness(2);
        var remote = h.Cloud.Seed("remote.txt", "ten bytes!");
        h.Cloud.InterruptDownload = true;
        await h.Engine.SyncNowAsync();
        Assert.IsFalse(File.Exists(h.Path("remote.txt")));
        Assert.AreEqual(0, h.Manifest.ReadAll().Count);
        Assert.AreEqual(1, Directory.GetFiles(h.Path(".cloudbay/transfers"), "*.part.json").Length);
        h.Cloud.InterruptDownload = false;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(1, h.Cloud.ResumedChunks);
        Assert.AreEqual("ten bytes!", await File.ReadAllTextAsync(h.Path("remote.txt")));
        Assert.AreEqual(remote.FileId, h.Manifest.ReadAll()["remote.txt"].Remote.FileId);
        Assert.AreEqual(0, Directory.GetFiles(h.Path(".cloudbay/transfers"), "*.part.json").Length);
    }

    [DataTestMethod]
    [DataRow("malformed-json")]
    [DataRow("different-version")]
    [DataRow("oversized-journal")]
    [DataRow("oversized-part")]
    [DataRow("null-chunk")]
    [DataRow("null-chunks")]
    [DataRow("invalid-chunk")]
    public async Task InvalidPrivateDownloadCheckpointRestartsWithoutTouchingTheExistingUserFile(string corruption)
    {
        await using var h = new Harness(1);
        const string original = "the original user document remains safe";
        const string downloaded = "ten bytes!";
        var destination = h.Path("document.txt");
        await File.WriteAllTextAsync(destination, original);
        await h.Engine.SyncNowAsync();
        var previous = h.Manifest.ReadAll()["document.txt"].Remote;
        var remote = h.Cloud.Seed("document.txt", downloaded);
        h.Cloud.InterruptDownload = true;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(original, await File.ReadAllTextAsync(destination));
        Assert.AreEqual(previous.FileId, h.Manifest.ReadAll()["document.txt"].Remote.FileId);
        var journal = Directory.GetFiles(h.Path(".cloudbay/transfers"), "*.part.json").Single();
        var part = journal[..^5];
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(journal))!;
        switch (corruption)
        {
            case "malformed-json": await File.WriteAllTextAsync(journal, "{invalid-json"); break;
            case "different-version":
                saved["FileId"] = "a different immutable version";
                await File.WriteAllTextAsync(journal, saved.ToJsonString());
                break;
            case "oversized-journal": await File.WriteAllTextAsync(journal, new string(' ', 2_000_001)); break;
            case "oversized-part":
                using (var extra = new FileStream(part, FileMode.Append, FileAccess.Write)) extra.WriteByte(42);
                break;
            case "null-chunk":
                saved["Chunks"] = JsonNode.Parse("[null]");
                await File.WriteAllTextAsync(journal, saved.ToJsonString());
                break;
            case "null-chunks":
                saved["Chunks"] = null;
                await File.WriteAllTextAsync(journal, saved.ToJsonString());
                break;
            case "invalid-chunk":
                saved["Chunks"]![0]!["Offset"] = 1;
                await File.WriteAllTextAsync(journal, saved.ToJsonString());
                break;
            default: Assert.Fail("Unknown checkpoint corruption fixture."); break;
        }
        h.Cloud.InterruptDownload = false;
        h.Cloud.OnDownloadStarting = (stream, chunks) =>
        {
            Assert.AreEqual(original, File.ReadAllText(destination), "Checkpoint recovery must not replace or truncate the user's destination.");
            Assert.AreEqual(0, chunks.Count, "A malformed checkpoint must not authorize reuse of any old bytes.");
            Assert.AreEqual(0L, stream.Length, "Only guarded private staging bytes are restarted.");
        };
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
        Assert.AreEqual(downloaded, await File.ReadAllTextAsync(destination));
        Assert.AreEqual(remote.FileId, h.Manifest.ReadAll()["document.txt"].Remote.FileId);
        Assert.AreEqual(0, h.Cloud.ResumedChunks);
        Assert.AreEqual(0, Directory.GetFiles(h.Path(".cloudbay/transfers"), "*.part.json").Length);
    }

    [TestMethod]
    public void LargeQueueKeepsExactCountsAndActiveRowsWithinBoundedPresentation()
    {
        var tracker = new TransferTracker("C:/Backup", "Personal");
        tracker.Queue(Enumerable.Range(0, 10_000).Select(index => ($"file-{index}.txt", ActivityKind.Upload, 10L)));
        tracker.Phase("file-9999.txt", ActivityKind.Upload, TransferPhase.Uploading);
        tracker.Progress("file-9999.txt", ActivityKind.Upload, new(5, 10));
        var snapshot = tracker.Apply(new(ClientState.Syncing, ""));
        Assert.AreEqual(256, snapshot.Transfers.Count);
        Assert.AreEqual("file-9999.txt", snapshot.Transfers[0].RelativePath);
        Assert.AreEqual(1, snapshot.ActiveTransfers);
        Assert.AreEqual(9999, snapshot.QueuedTransfers);
        Assert.AreEqual(100_000, snapshot.TransferTotalBytes);
        tracker.Complete("file-9999.txt", ActivityKind.Upload);
        Assert.AreEqual(10, tracker.Apply(snapshot).TransferredBytes, "Aggregate progress must retain completed small-file bytes.");
        tracker.Phase("file-5.txt", ActivityKind.Upload, TransferPhase.Uploading);
        tracker.Progress("file-5.txt", ActivityKind.Upload, new(4, 10));
        tracker.Pause();
        var paused = tracker.Apply(snapshot);
        Assert.AreEqual(14, paused.TransferredBytes);
        Assert.AreEqual(0, paused.ActiveTransfers);
        Assert.AreEqual(9999, paused.QueuedTransfers);
    }

    [DataTestMethod]
    [DataRow(UploadMode.Intelligent, 1, 512L * 1024 * 1024, 2, 2)]
    [DataRow(UploadMode.Intelligent, 1, 8L * 1024 * 1024 * 1024, 4, 4)]
    [DataRow(UploadMode.Intelligent, 32, 8L * 1024 * 1024 * 1024, 8, 8)]
    [DataRow(UploadMode.MaximumThroughput, 32, 8L * 1024 * 1024 * 1024, 16, 16)]
    [DataRow(UploadMode.Manual, 32, 8L * 1024 * 1024 * 1024, 3, 5)]
    public void PerformanceModeHonorsManualChoicesAndBoundsResourceAwareDefaults(UploadMode mode, int cpu, long memory, int upload, int download)
    {
        var limits = TransferLimits.For(new AppSettings { UploadMode = mode, UploadConcurrency = 3, DownloadConcurrency = 5 }, cpu, memory);
        Assert.AreEqual(upload, limits.Uploads);
        Assert.AreEqual(download, limits.Downloads);
    }

    [TestMethod]
    public void LateTransferNotificationsRemainPausedUntilTheNextReconciliation()
    {
        var tracker = new TransferTracker("C:/Backup", "Personal");
        tracker.Queue(new[] { ("large.bin", ActivityKind.Upload, 100L) });
        tracker.Phase("large.bin", ActivityKind.Upload, TransferPhase.Hashing);
        tracker.Pause();
        tracker.Phase("large.bin", ActivityKind.Upload, TransferPhase.Uploading);
        tracker.Progress("large.bin", ActivityKind.Upload, new(42, 100));
        var paused = tracker.Apply(new(ClientState.Paused, "Sync paused"));
        Assert.AreEqual(0, paused.ActiveTransfers);
        Assert.AreEqual(1, paused.QueuedTransfers);
        Assert.AreEqual(TransferPhase.Paused, paused.Transfers.Single().Phase);
        Assert.AreEqual(42, paused.TransferredBytes);
        tracker.Reset();
        tracker.Queue(new[] { ("large.bin", ActivityKind.Upload, 100L) });
        tracker.Phase("large.bin", ActivityKind.Upload, TransferPhase.Uploading);
        Assert.AreEqual(1, tracker.Apply(paused).ActiveTransfers);
    }

    [TestMethod]
    public async Task PolicyPauseUpdatesExistingWaitingRowsWithoutClaimingActiveTransfers()
    {
        await using var h = new Harness(1);
        await File.WriteAllTextAsync(h.Path("important.txt"), "retain local edits");
        h.Cloud.FailVerification = true;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(TransferPhase.Retrying, h.Latest.Transfers.Single().Phase);
        h.PolicyReason = "Metered connection";
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Paused, h.Latest.State);
        Assert.AreEqual("Metered connection", h.Latest.Message);
        Assert.AreEqual(0, h.Latest.ActiveTransfers);
        Assert.AreEqual(TransferPhase.Paused, h.Latest.Transfers.Single().Phase);
        Assert.AreEqual("retain local edits", await File.ReadAllTextAsync(h.Path("important.txt")));
    }

    [TestMethod]
    public async Task StagingCleanupRemovesOnlyExpiredUnlockedPrivateParts()
    {
        await using var h = new Harness(1);
        var folder = h.Path(".cloudbay/transfers");
        Directory.CreateDirectory(folder);
        var expired = System.IO.Path.Combine(folder, new string('a', 64) + ".part");
        var locked = System.IO.Path.Combine(folder, new string('b', 64) + ".part");
        var recentlyCheckpointed = System.IO.Path.Combine(folder, new string('c', 64) + ".part");
        var recent = System.IO.Path.Combine(folder, new string('d', 64) + ".part");
        var unrelated = System.IO.Path.Combine(folder, "user-file.part");
        foreach (var path in new[] { expired, locked, recentlyCheckpointed, recent, unrelated })
        {
            await File.WriteAllTextAsync(path, "private or retained data");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        }
        await File.WriteAllTextAsync(expired + ".json", "expired checkpoint");
        File.SetLastWriteTimeUtc(expired + ".json", DateTime.UtcNow.AddDays(-8));
        await File.WriteAllTextAsync(recentlyCheckpointed + ".json", "recent checkpoint");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
        using var held = new FileStream(locked + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Latest.State);
        Assert.IsFalse(File.Exists(expired));
        Assert.IsFalse(File.Exists(expired + ".json"));
        foreach (var path in new[] { locked, recentlyCheckpointed, recent, unrelated })
            Assert.AreEqual("private or retained data", await File.ReadAllTextAsync(path));
        Assert.AreEqual("recent checkpoint", await File.ReadAllTextAsync(recentlyCheckpointed + ".json"));
        Assert.IsTrue(File.Exists(locked + ".lock"));
        Assert.IsFalse(File.Exists(expired + ".lock"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBay.Pipeline", Guid.NewGuid().ToString("N"));
        private readonly int _concurrency;
        private readonly UploadMode _mode;
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public FakeCloud Cloud { get; } = new();
        public FakePlaceholders Placeholders { get; } = new();
        public string? PolicyReason;
        public SyncManifest Manifest { get; private set; }
        public SyncEngine Engine { get; private set; }
        public ConcurrentQueue<ActivityEvent> Activity { get; } = new();
        private readonly ConcurrentQueue<SyncSnapshot> _snapshots = new();
        public SyncSnapshot Latest => _snapshots.Last();
        public IEnumerable<SyncSnapshot> Snapshots => _snapshots.ToArray();
        public Harness(int concurrency, UploadMode mode = UploadMode.Manual)
        {
            _concurrency = concurrency;
            _mode = mode;
            Directory.CreateDirectory(Root);
            Manifest = new(System.IO.Path.Combine(_directory, "state.sqlite"));
            Engine = CreateEngine();
        }
        public AppSettings Settings => new() { RootPath = Root, KeyId = "key", BucketId = "bucket", FilesOnDemand = false,
            UploadMode = _mode, UploadConcurrency = _concurrency, DownloadConcurrency = _concurrency };
        private SyncEngine CreateEngine() => new(Cloud, Placeholders, Manifest, Settings,
                System.IO.Path.Combine(_directory, "Recovery"), Activity.Enqueue, _snapshots.Enqueue,
                policy: () => PolicyReason, rootDisplayName: "Personal backup");
        public async Task RestartAsync()
        {
            await Engine.DisposeAsync();
            Manifest = new(System.IO.Path.Combine(_directory, "state.sqlite"));
            Engine = CreateEngine();
        }
        public string Path(string name) => System.IO.Path.Combine(Root, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public async Task<SyncSnapshot> WaitSnapshotAsync(Func<SyncSnapshot, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (_snapshots.TryPeek(out _) && predicate(Latest)) return Latest;
                await Task.Delay(10, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        { await Engine.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    }

    private sealed class FakePlaceholders : IPlaceholderService
    {
        public int Marked, MarkAttempts, FailMarks;
        public Exception? MarkFailure;
        public Action<string>? OnMark;
        public bool HoldMarks;
        public TaskCompletionSource MarkStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMarks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPlaceholder(string path) => false;
        public bool IsHydrated(string path) => true;
        public bool HasLocalChanges(string path) => false;
        public Task ConnectAsync(string root, string identity, HydrationHandler hydrate, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateOrUpdateAsync(string path, CloudObject file, bool inSync, CancellationToken ct = default) => Task.CompletedTask;
        public async Task MarkInSyncAsync(string path, CloudObject file, CancellationToken ct = default)
        {
            Interlocked.Increment(ref MarkAttempts);
            OnMark?.Invoke(path);
            MarkStarted.TrySetResult();
            if (HoldMarks) await ReleaseMarks.Task.WaitAsync(ct);
            if (FailMarks > 0)
            {
                Interlocked.Decrement(ref FailMarks);
                throw MarkFailure ?? new IOException("Windows file metadata is temporarily locked.");
            }
            Interlocked.Increment(ref Marked);
        }
        public Task SetPinAsync(string path, PinMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task FreeSpaceAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCloud : ICloudStore
    {
        private readonly ConcurrentDictionary<string, (CloudObject Object, byte[] Bytes)> _files = new();
        private int _verifying, _uploaded, _pendingVerification, _maximumPendingVerification;
        public bool HoldVerification, FailVerification, CorruptAcknowledgment, InterruptDownload;
        public bool HoldUpload, HoldDownloadCompletion, CheckpointFullDownload;
        public bool HoldLargePreparation;
        private int _activeUploads, _maximumActiveUploads, _preparedLargeFiles;
        public int ActiveUploads => Volatile.Read(ref _activeUploads);
        public int MaximumActiveUploads => Volatile.Read(ref _maximumActiveUploads);
        public int PreparedLargeFiles => Volatile.Read(ref _preparedLargeFiles);
        public TaskCompletionSource LargePreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLargePreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TinyPayloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prepared, _activeDownloads, _reusedDownloadRanges;
        private long _downloadedPayloadBytes;
        public int ActiveDownloads => Volatile.Read(ref _activeDownloads);
        public int ReusedDownloadRanges => Volatile.Read(ref _reusedDownloadRanges);
        public long DownloadedPayloadBytes => Interlocked.Read(ref _downloadedPayloadBytes);
        public ConcurrentQueue<string> PreparedPaths { get; } = new();
        public ConcurrentQueue<string> DownloadsStarted { get; } = new();
        public TaskCompletionSource FirstUpload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondPreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUpload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResumedChunks;
        public ConcurrentBag<string> Hidden { get; } = [];
        public int UploadedCount => Volatile.Read(ref _uploaded);
        public int MaximumPendingVerification => Volatile.Read(ref _maximumPendingVerification);
        public TimeSpan VerificationDelay;
        public Action<FileStream, IReadOnlyList<DownloadChunk>>? OnDownloadStarting;
        public TaskCompletionSource OneVerification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoVerifications { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CloudObject Seed(string name, string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), "CloudBay/" + name, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow);
            _files[file.Key] = (file, bytes);
            return file;
        }
        public async IAsyncEnumerable<CloudObject> ListAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.Yield(); foreach (var item in _files.Values) yield return item.Object; }
        public async Task<CloudObject> UploadAsync(string bucket, string key, Stream source, long length, string sha1, DateTimeOffset modified,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeUploads);
            for (var maximum = Volatile.Read(ref _maximumActiveUploads); active > maximum;
                maximum = Volatile.Read(ref _maximumActiveUploads))
                if (Interlocked.CompareExchange(ref _maximumActiveUploads, active, maximum) == maximum) break;
            try
            {
                FirstUpload.TrySetResult();
                using var output = new MemoryStream(); await source.CopyToAsync(output, cancellationToken);
                if (length < 8 * 1024 * 1024) TinyPayloadStarted.TrySetResult();
                if (HoldUpload) await ReleaseUpload.Task.WaitAsync(cancellationToken);
                progress?.Report(new(length, length));
                var file = new CloudObject(Guid.NewGuid().ToString("N"), key, length, CorruptAcknowledgment ? new string('0', 40) : sha1, modified);
                _files[key] = (file, output.ToArray());
                Interlocked.Increment(ref _uploaded);
                var pending = Interlocked.Increment(ref _pendingVerification);
                for (var maximum = Volatile.Read(ref _maximumPendingVerification); pending > maximum;
                    maximum = Volatile.Read(ref _maximumPendingVerification))
                    if (Interlocked.CompareExchange(ref _maximumPendingVerification, pending, maximum) == maximum) break;
                return file;
            }
            finally { Interlocked.Decrement(ref _activeUploads); }
        }
        public async Task<string> PrepareUploadChecksumAsync(string bucketId, string key, Stream source, long length,
            DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default)
        {
            await TransferResources.Hashing.WaitAsync(cancellationToken);
            try
            {
                if (HoldLargePreparation && length >= 8 * 1024 * 1024)
                {
                    LargePreparationStarted.TrySetResult();
                    await ReleaseLargePreparation.Task.WaitAsync(cancellationToken);
                }
                var position = source.Position;
                var hash = Convert.ToHexString(await SHA1.HashDataAsync(source, cancellationToken)).ToLowerInvariant();
                source.Position = position;
                PreparedPaths.Enqueue(((FileStream)source).Name);
                if (Interlocked.Increment(ref _prepared) == 2) SecondPreparation.TrySetResult();
                if (length >= 8 * 1024 * 1024) Interlocked.Increment(ref _preparedLargeFiles);
                return hash;
            }
            finally { TransferResources.Hashing.Release(); }
        }
        public async Task VerifyUploadAsync(CloudObject file, string bucket, CancellationToken ct = default)
        {
            OneVerification.TrySetResult();
            if (Interlocked.Increment(ref _verifying) >= 2) TwoVerifications.TrySetResult();
            if (HoldVerification) await Release.Task.WaitAsync(ct);
            if (VerificationDelay > TimeSpan.Zero) await Task.Delay(VerificationDelay, ct);
            Interlocked.Decrement(ref _pendingVerification);
            if (FailVerification) throw new InvalidDataException("Server version checksum mismatch.");
        }
        public Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            destination.WriteAsync(_files[file.Key].Bytes.AsMemory((int)offset, (int)(length ?? file.Size - offset)), cancellationToken).AsTask();
        public async Task DownloadFileAsync(CloudObject file, FileStream destination, IReadOnlyList<DownloadChunk> chunks,
            Func<DownloadChunk, CancellationToken, Task> checkpoint, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            DownloadsStarted.Enqueue(file.Key);
            Interlocked.Increment(ref _activeDownloads);
            try
            {
                var bytes = _files[file.Key].Bytes;
                OnDownloadStarting?.Invoke(destination, chunks);
                if (InterruptDownload)
                {
                    await destination.WriteAsync(bytes, cancellationToken); destination.Flush(true);
                    Interlocked.Add(ref _downloadedPayloadBytes, bytes.Length);
                    await checkpoint(new(0, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes))), cancellationToken);
                    throw new IOException("Connection interrupted.");
                }
                ResumedChunks = chunks.Count;
                Interlocked.Add(ref _reusedDownloadRanges, chunks.Count);
                if (chunks.Count > 0)
                { destination.Position = 0; var prefix = new byte[5]; await destination.ReadExactlyAsync(prefix, cancellationToken); CollectionAssert.AreEqual(bytes[..5], prefix); }
                if (chunks.Count == 0)
                {
                    destination.Position = 0;
                    await destination.WriteAsync(bytes, cancellationToken);
                    Interlocked.Add(ref _downloadedPayloadBytes, bytes.Length);
                }
                destination.SetLength(bytes.Length); destination.Flush(true);
                if (CheckpointFullDownload) await checkpoint(new(0, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes))), cancellationToken);
                progress?.Report(new(bytes.Length, bytes.Length));
                if (HoldDownloadCompletion) await ReleaseDownload.Task.WaitAsync(cancellationToken);
            }
            finally { Interlocked.Decrement(ref _activeDownloads); }
        }
        public Task HideAsync(string bucket, string key, CancellationToken ct = default)
        { Hidden.Add(key); _files.TryRemove(key, out _); return Task.CompletedTask; }
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucket, CloudObject file, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

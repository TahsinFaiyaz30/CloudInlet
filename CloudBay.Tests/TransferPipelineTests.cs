using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class TransferPipelineTests
{
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
        var snapshot = await h.WaitSnapshotAsync(value => value.ActiveTransfers == 8);
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
        await h.WaitSnapshotAsync(value => value.ActiveTransfers == 8);
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
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public FakeCloud Cloud { get; } = new();
        public FakePlaceholders Placeholders { get; } = new();
        public string? PolicyReason;
        public SyncManifest Manifest { get; }
        public SyncEngine Engine { get; }
        private readonly ConcurrentQueue<SyncSnapshot> _snapshots = new();
        public SyncSnapshot Latest => _snapshots.Last();
        public Harness(int concurrency)
        {
            Directory.CreateDirectory(Root);
            Manifest = new(System.IO.Path.Combine(_directory, "state.sqlite"));
            Engine = new(Cloud, Placeholders, Manifest, new AppSettings { RootPath = Root, KeyId = "key", BucketId = "bucket",
                FilesOnDemand = false, UploadMode = UploadMode.Manual, UploadConcurrency = concurrency, DownloadConcurrency = concurrency },
                System.IO.Path.Combine(_directory, "Recovery"), _ => { }, _snapshots.Enqueue,
                policy: () => PolicyReason, rootDisplayName: "Personal backup");
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
        public int Marked;
        public Action<string>? OnMark;
        public bool IsPlaceholder(string path) => false;
        public bool IsHydrated(string path) => true;
        public bool HasLocalChanges(string path) => false;
        public Task ConnectAsync(string root, string identity, HydrationHandler hydrate, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateOrUpdateAsync(string path, CloudObject file, bool inSync, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkInSyncAsync(string path, CloudObject file, CancellationToken ct = default)
        { Interlocked.Increment(ref Marked); OnMark?.Invoke(path); return Task.CompletedTask; }
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
        public int ResumedChunks;
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
            using var output = new MemoryStream(); await source.CopyToAsync(output, cancellationToken);
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
            var bytes = _files[file.Key].Bytes;
            OnDownloadStarting?.Invoke(destination, chunks);
            if (InterruptDownload)
            {
                await destination.WriteAsync(bytes, cancellationToken); destination.Flush(true);
                await checkpoint(new(0, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes))), cancellationToken);
                throw new IOException("Connection interrupted.");
            }
            ResumedChunks = chunks.Count;
            if (chunks.Count > 0)
            { destination.Position = 0; var prefix = new byte[5]; await destination.ReadExactlyAsync(prefix, cancellationToken); CollectionAssert.AreEqual(bytes[..5], prefix); }
            destination.Position = 0;
            await destination.WriteAsync(bytes, cancellationToken);
            destination.SetLength(bytes.Length); destination.Flush(true);
            progress?.Report(new(bytes.Length, bytes.Length));
        }
        public Task HideAsync(string bucket, string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucket, CloudObject file, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

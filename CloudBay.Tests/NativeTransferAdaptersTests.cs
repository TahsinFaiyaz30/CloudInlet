using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class NativeTransferAdaptersTests
{
    [TestMethod]
    public async Task PreparationAndUploadPreserveOriginalFileHandleAndCachedChecksums()
    {
        using var local = new TestFile(Encoding.UTF8.GetBytes("locked local snapshot"));
        await using var source = local.OpenRead();
        using var store = new NativeStore();
        var modified = DateTimeOffset.UtcNow;
        var sha1 = await NativeTransferAdapters.PrepareUploadChecksumAsync(store, "bucket", "prefix/file", source,
            source.Length, modified);
        Assert.AreEqual(0L, source.Position, "Preparation must rewind even when a provider leaves its reader at EOF.");

        var uploaded = await NativeTransferAdapters.UploadPreparedAsync(store, "bucket", "prefix/file", source,
            source.Length, sha1, modified);

        Assert.AreSame(source, store.PreparedSource);
        Assert.AreSame(source, store.UploadedSource, "A wrapper would lose B2's ConditionalWeakTable multipart preparation.");
        Assert.AreEqual(1, store.ChecksumPasses);
        Assert.IsTrue(store.ReusedPreparedChecksum);
        Assert.AreEqual(sha1, uploaded.Sha1);
    }

    [DataTestMethod]
    [DataRow("key")]
    [DataRow("size")]
    [DataRow("hash")]
    [DataRow("action")]
    [DataRow("identity")]
    public async Task UploadDoesNotAcceptAnUnrelatedAcknowledgment(string mismatch)
    {
        using var local = new TestFile(Encoding.UTF8.GetBytes("local file"));
        await using var source = local.OpenRead();
        using var store = new NativeStore { AcknowledgmentMismatch = mismatch };
        var modified = DateTimeOffset.UtcNow;
        var sha1 = await NativeTransferAdapters.PrepareUploadChecksumAsync(store, "bucket", "file", source,
            source.Length, modified);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => NativeTransferAdapters.UploadPreparedAsync(store,
            "bucket", "file", source, source.Length, sha1, modified));
    }

    [TestMethod]
    public async Task DownloadPreservesProviderResumeRangesAndDurableCheckpointCallback()
    {
        var payload = Encoding.UTF8.GetBytes("completed and unfinished ranges");
        using var local = new TestFile(payload[..9]);
        await using var destination = local.OpenStaging();
        using var store = new NativeStore { Payload = payload };
        var saved = new[] { new DownloadChunk(0, 9, Sha1(payload[..9])) };
        DownloadChunk? acknowledged = null;
        var progress = new CollectProgress();

        await NativeTransferAdapters.DownloadFileAsync(store, new("version", "file", payload.Length, Sha1(payload), DateTimeOffset.UtcNow),
            destination, saved, async (chunk, ct) =>
            {
                acknowledged = chunk;
                var position = destination.Position;
                destination.Position = 0;
                var durableBytes = new byte[payload.Length];
                await destination.ReadExactlyAsync(durableBytes, ct);
                destination.Position = position;
                CollectionAssert.AreEqual(payload, durableBytes,
                    "The destination must contain the completed range before its checkpoint is acknowledged.");
            }, progress);

        Assert.AreSame(destination, store.DownloadDestination);
        Assert.AreSame(saved, store.CompletedChunks);
        Assert.AreEqual(9L, store.DownloadStart);
        Assert.AreEqual(9L, acknowledged!.Offset);
        Assert.IsTrue(progress.Values[0].IsBaseline);
        Assert.AreEqual(9L, progress.Values[0].Bytes);
        await destination.DisposeAsync();
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(local.Path));
    }

    [TestMethod]
    public async Task CancelledDownloadRetainsTheAcknowledgedPartialForRestart()
    {
        using var local = new TestFile([]);
        await using var destination = local.OpenStaging();
        using var store = new NativeStore { Payload = Encoding.UTF8.GetBytes("durable partial"), StopAfterBytes = 7 };
        using var cancellation = new CancellationTokenSource();
        DownloadChunk? saved = null;

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => NativeTransferAdapters.DownloadFileAsync(store,
            new("version", "file", store.Payload.Length, Sha1(store.Payload), DateTimeOffset.UtcNow), destination, [],
            (chunk, ct) =>
            {
                saved = chunk;
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellationToken: cancellation.Token));

        Assert.AreEqual(7L, saved!.Length);
        Assert.AreEqual(7L, destination.Length);
        await destination.DisposeAsync();
        CollectionAssert.AreEqual(store.Payload[..7], await File.ReadAllBytesAsync(local.Path));
    }

    [TestMethod]
    public async Task ShortCompletedDownloadCannotBeInstalledAsTheCloudVersion()
    {
        using var local = new TestFile([]);
        await using var destination = local.OpenStaging();
        using var store = new NativeStore { Payload = [1, 2, 3] };

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => NativeTransferAdapters.DownloadFileAsync(store,
            new("version", "file", 4, null, DateTimeOffset.UtcNow), destination, [], (_, _) => Task.CompletedTask));
        Assert.AreEqual(3L, destination.Length, "An invalid provider result remains available for safe recovery.");
    }

    [TestMethod]
    public async Task VerificationFailureReleasesTheSharedBudgetAndPropagatesToNativeOrchestration()
    {
        using var store = new NativeStore { VerificationFailure = true };
        var available = TransferResources.Verification.CurrentCount;
        var file = new CloudObject("version", "file", 0, Sha1([]), DateTimeOffset.UtcNow);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => NativeTransferAdapters.VerifyUploadAsync(store, file, "bucket"));
        Assert.AreEqual(available, TransferResources.Verification.CurrentCount);
        store.VerificationFailure = false;
        await NativeTransferAdapters.VerifyUploadAsync(store, file, "bucket");
        Assert.AreEqual(2, store.Verifications);
    }

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private sealed class CollectProgress : IProgress<TransferProgress>
    {
        public List<TransferProgress> Values { get; } = [];
        public void Report(TransferProgress value) => Values.Add(value);
    }

    private sealed class TestFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBay-native-adapter-" + Guid.NewGuid().ToString("N"));
        public TestFile(byte[] payload) => File.WriteAllBytes(Path, payload);
        public FileStream OpenRead() => new(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        public FileStream OpenStaging() => new(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, true);
        public void Dispose() => File.Delete(Path);
    }

    private sealed class NativeStore : ICloudStore
    {
        private sealed record Prepared(string Sha1);
        private readonly ConditionalWeakTable<Stream, Prepared> _checksums = new();
        public Stream? PreparedSource { get; private set; }
        public Stream? UploadedSource { get; private set; }
        public FileStream? DownloadDestination { get; private set; }
        public IReadOnlyList<DownloadChunk>? CompletedChunks { get; private set; }
        public int ChecksumPasses { get; private set; }
        public bool ReusedPreparedChecksum { get; private set; }
        public string? AcknowledgmentMismatch { get; init; }
        public byte[] Payload { get; init; } = [];
        public int? StopAfterBytes { get; init; }
        public long DownloadStart { get; private set; }
        public bool VerificationFailure { get; set; }
        public int Verifications { get; private set; }

        public async Task<string> PrepareUploadChecksumAsync(string bucketId, string key, Stream source, long length,
            DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default)
        {
            PreparedSource = source;
            ChecksumPasses++;
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(source, cancellationToken)).ToLowerInvariant();
            _checksums.Add(source, new(sha1));
            return sha1; // Deliberately leave Position at EOF; the adapter restores it.
        }

        public async Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1,
            DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            UploadedSource = source;
            ReusedPreparedChecksum = _checksums.TryGetValue(source, out var prepared) && prepared.Sha1 == sha1;
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(source, cancellationToken)).ToLowerInvariant();
            Assert.AreEqual(sha1, actual);
            return new(AcknowledgmentMismatch == "identity" ? "" : "uploaded-version",
                AcknowledgmentMismatch == "key" ? "another-file" : key,
                AcknowledgmentMismatch == "size" ? length + 1 : length,
                AcknowledgmentMismatch == "hash" ? new string('0', 40) : sha1, modifiedUtc,
                AcknowledgmentMismatch == "action" ? "hide" : "upload");
        }

        public async Task DownloadFileAsync(CloudObject file, FileStream destination, IReadOnlyList<DownloadChunk> completedChunks,
            Func<DownloadChunk, CancellationToken, Task> checkpoint, IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadDestination = destination;
            CompletedChunks = completedChunks;
            DownloadStart = completedChunks.Sum(chunk => chunk.Length);
            Assert.AreEqual(DownloadStart, destination.Length, "The adapter must not truncate already completed bytes.");
            progress?.Report(new(DownloadStart, file.Size) { IsBaseline = true });
            destination.Position = DownloadStart;
            var end = StopAfterBytes ?? Payload.Length;
            await destination.WriteAsync(Payload.AsMemory((int)DownloadStart, end - (int)DownloadStart), cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Flush(true);
            var chunk = new DownloadChunk(DownloadStart, end - DownloadStart, Sha1(Payload[(int)DownloadStart..end]));
            await checkpoint(chunk, cancellationToken);
            progress?.Report(new(end, file.Size));
        }

        public Task VerifyUploadAsync(CloudObject file, string bucketId, CancellationToken cancellationToken = default)
        {
            Verifications++;
            if (VerificationFailure) throw new InvalidDataException("Injected independent destination checksum failure.");
            return Task.CompletedTask;
        }

        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

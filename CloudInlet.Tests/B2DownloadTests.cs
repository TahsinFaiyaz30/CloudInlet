using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class B2DownloadTests
{
    [TestMethod]
    public async Task NativeNonSeekableDownloadContinuesFromDeliveredBytesWithoutReplay()
    {
        var data = Encoding.UTF8.GetBytes("native hydration resumes correctly");
        var file = Object(data);
        var attempts = 0;
        var progress = new List<long>();
        using var store = Store(request =>
        {
            if (++attempts == 1)
                return Download(file, new BrokenReadStream(data[..5]), data.Length);
            Assert.AreEqual($"bytes=5-{data.Length - 1}", request.Headers.Range!.ToString());
            return Download(file, new MemoryStream(data[5..]), data.Length - 5, 5);
        });
        await Connect(store);
        using var destination = new NonSeekableOutput();
        await store.DownloadAsync(file, destination, progress: new InlineProgress(value => progress.Add(value.Bytes)));
        CollectionAssert.AreEqual(data, destination.ToArray());
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(progress.Zip(progress.Skip(1), (before, after) => after >= before).All(value => value));
    }

    [TestMethod]
    public async Task NativeCancellationDoesNotReplayOrRetryDeliveredBytes()
    {
        var data = Encoding.UTF8.GetBytes("cancelled native bytes");
        var file = Object(data);
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        using var store = Store(_ => { attempts++; return Download(file, new MemoryStream(data), data.Length); });
        await Connect(store);
        using var destination = new CancelAfterWrite(cancellation);
        await ExpectCancellation(() => store.DownloadAsync(file, destination, cancellationToken: cancellation.Token), cancellation.Token);
        Assert.AreEqual(1, attempts);
        CollectionAssert.AreEqual(data, destination.ToArray());
    }

    [TestMethod]
    public async Task ChangedVersionOrIgnoredRangeNeverWritesAnyBytes()
    {
        var data = Encoding.UTF8.GetBytes("range identity");
        var file = Object(data);
        foreach (var wrongIdentity in new[] { false, true })
        {
            var attempts = 0;
            using var store = Store(_ =>
            {
                attempts++;
                var response = Download(file, new MemoryStream(wrongIdentity ? data[2..] : data), wrongIdentity ? data.Length - 2 : data.Length,
                    wrongIdentity ? 2 : null);
                if (wrongIdentity)
                {
                    response.Headers.Remove("X-Bz-File-Id");
                    response.Headers.TryAddWithoutValidation("X-Bz-File-Id", "different-version");
                }
                return response;
            });
            await Connect(store);
            using var output = new NonSeekableOutput();
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadAsync(file, output, 2));
            Assert.AreEqual(0L, output.Length);
            Assert.AreEqual(1, attempts);
        }
    }

    [TestMethod]
    public async Task CorruptDownloadDoesNotRetryAsNetworkFailure()
    {
        var data = Encoding.UTF8.GetBytes("checksum identity");
        var file = Object(data);
        var corrupt = (byte[])data.Clone(); corrupt[^1] ^= 0xff;
        var attempts = 0;
        using var store = Store(_ => { attempts++; return Download(file, new MemoryStream(corrupt), corrupt.Length); });
        await Connect(store);
        using var output = new MemoryStream();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadAsync(file, output));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task ResumeCannotCombineRangesWithChangedChecksumMetadata()
    {
        var data = Encoding.UTF8.GetBytes("immutable range checksum");
        var metadata = Object(data);
        var file = metadata with { Sha1 = null };
        var attempts = 0;
        using var store = Store(request =>
        {
            if (++attempts == 1) return Download(metadata, new BrokenReadStream(data[..5]), data.Length);
            Assert.AreEqual($"bytes=5-{data.Length - 1}", request.Headers.Range!.ToString());
            var response = Download(metadata, new MemoryStream(data[5..]), data.Length - 5, 5);
            response.Headers.Remove("X-Bz-Content-Sha1");
            response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", new string('0', 40));
            return response;
        });
        await Connect(store);
        using var output = new NonSeekableOutput();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadAsync(file, output));
        CollectionAssert.AreEqual(data[..5], output.ToArray());
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task LocalWriteFailureDoesNotRestartNetworkDownload()
    {
        var data = Encoding.UTF8.GetBytes("disk full");
        var file = Object(data);
        var attempts = 0;
        using var store = Store(_ => { attempts++; return Download(file, new MemoryStream(data), data.Length); });
        await Connect(store);
        using var output = new FailingOutput();
        var error = await Assert.ThrowsExceptionAsync<IOException>(() => store.DownloadAsync(file, output));
        Assert.AreEqual("Disk full in test", error.Message);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task NativeDownloadsAcrossFilesShareOneConfiguredRequestBudget()
    {
        var data = Encoding.UTF8.GetBytes("shared downloads");
        var file = Object(data);
        var active = 0;
        var maximum = 0;
        var concurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = new B2CloudStore(new Handler(async (request, ct) =>
        {
            if (Authorize(request)) return Authorization();
            var current = Interlocked.Increment(ref active);
            RaiseMaximum(ref maximum, current);
            if (current == 2) concurrent.TrySetResult();
            await concurrent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return Download(file, new CountedReadStream(data, () => Interlocked.Decrement(ref active)), data.Length);
        }));
        store.ConfigureDownloads(2);
        await Connect(store);
        await Task.WhenAll(Enumerable.Range(0, 7).Select(async _ =>
        {
            using var output = new NonSeekableOutput();
            await store.DownloadAsync(file, output);
            CollectionAssert.AreEqual(data, output.ToArray());
        }));
        Assert.AreEqual(2, maximum);
        Assert.AreEqual(0, active);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.ConfigureDownloads(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.ConfigureDownloads(33));
    }

    [TestMethod]
    public async Task ParallelChunksAndNativeHydrationShareSameBudgetAndAssembleExactBytes()
    {
        var data = Payload(checked((int)(B2CloudStore.DownloadChunkSize * 3 + 117)));
        var file = Object(data);
        var active = 0;
        var maximum = 0;
        var concurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentBag<long>();
        using var store = new B2CloudStore(new Handler(async (request, ct) =>
        {
            if (Authorize(request)) return Authorization();
            var range = request.Headers.Range?.Ranges.Single();
            var offset = range?.From ?? 0;
            var count = checked((int)((range?.To ?? data.Length - 1) - offset + 1));
            requests.Add(offset);
            var current = Interlocked.Increment(ref active);
            RaiseMaximum(ref maximum, current);
            if (current == 2) concurrent.TrySetResult();
            await concurrent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return Download(file, new CountedReadStream(data, checked((int)offset), count,
                () => Interlocked.Decrement(ref active)), count, range is null ? null : offset);
        }));
        store.ConfigureDownloads(2);
        await Connect(store);
        var path = TempFile();
        try
        {
            await using var staged = Staged(path);
            var completed = new ConcurrentBag<DownloadChunk>();
            var values = new ConcurrentQueue<long>();
            using var native = new NonSeekableOutput();
            await Task.WhenAll(
                store.DownloadFileAsync(file, staged, [], (chunk, _) => { completed.Add(chunk); return Task.CompletedTask; },
                    new InlineProgress(value => values.Enqueue(value.Bytes))),
                store.DownloadAsync(file, native, 11, 129));
            Assert.AreEqual(2, maximum);
            Assert.AreEqual(0, active);
            Assert.AreEqual(4, completed.Count);
            Assert.AreEqual((long)data.Length, staged.Position);
            CollectionAssert.AreEqual(data, await ReadStaged(staged, data.Length));
            CollectionAssert.AreEqual(data[11..140], native.ToArray());
            var ordered = values.ToArray();
            Assert.IsTrue(ordered.Zip(ordered.Skip(1), (before, after) => after >= before).All(value => value));
            Assert.AreEqual((long)data.Length, ordered[^1]);
            Assert.AreEqual((long)data.Length, staged.Position);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task DurableResumeAcrossStoreRestartSkipsAlreadyFlushedChunks()
    {
        await CheckResume(tamperSavedChunk: false);
    }

    [TestMethod]
    public async Task DurableResumeRechecksSavedBytesAndRedownloadsModifiedChunk()
    {
        await CheckResume(tamperSavedChunk: true);
    }

    private static async Task CheckResume(bool tamperSavedChunk)
    {
        var data = Payload(checked((int)(B2CloudStore.DownloadChunkSize * 2 + 193)));
        var file = Object(data);
        var path = TempFile();
        var saved = new List<DownloadChunk>();
        try
        {
            using (var cancellation = new CancellationTokenSource())
            using (var store = Store(request => RangeDownload(request, file, data)))
            {
                store.ConfigureDownloads(1);
                await Connect(store);
                await using var staged = Staged(path);
                await ExpectCancellation(() => store.DownloadFileAsync(file, staged, [],
                    (chunk, _) => { saved.Add(chunk); cancellation.Cancel(); return Task.CompletedTask; }, cancellationToken: cancellation.Token), cancellation.Token);
                Assert.AreEqual(1, saved.Count);
            }
            if (tamperSavedChunk)
            {
                await using var changed = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                changed.WriteByte((byte)(data[0] ^ 0xff));
            }
            var resumedOffsets = new List<long>();
            using (var restarted = Store(request =>
            {
                resumedOffsets.Add(request.Headers.Range?.Ranges.Single().From ?? 0);
                return RangeDownload(request, file, data);
            }))
            {
                restarted.ConfigureDownloads(1);
                await Connect(restarted);
                await using var staged = Staged(path);
                await restarted.DownloadFileAsync(file, staged, saved, (_, _) => Task.CompletedTask);
            }
            Assert.AreEqual(tamperSavedChunk ? 3 : 2, resumedOffsets.Count);
            Assert.AreEqual(tamperSavedChunk, resumedOffsets.Contains(0));
            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task CorruptParallelDownloadIsRejectedAfterAssemblyWithoutNetworkRetries()
    {
        var data = Payload(checked((int)(B2CloudStore.DownloadChunkSize + 101)));
        var file = Object(data);
        data[^1] ^= 0xff;
        var requests = 0;
        using var store = Store(request => { Interlocked.Increment(ref requests); return RangeDownload(request, file, data); });
        await Connect(store);
        var path = TempFile();
        try
        {
            await using var staged = Staged(path);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadFileAsync(file, staged, [], (_, _) => Task.CompletedTask));
            Assert.AreEqual(2, requests);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task TinyStagedDownloadVerifiesDiskBytesWithoutRedundantJournalFlushes()
    {
        var data = Encoding.UTF8.GetBytes("tiny file independently verified");
        var file = Object(data);
        foreach (var corrupt in new[] { false, true })
        {
            var received = (byte[])data.Clone();
            if (corrupt) received[0] ^= 0xff;
            var requests = 0;
            var checkpoints = 0;
            using var store = Store(request => { requests++; return RangeDownload(request, file, received); });
            await Connect(store);
            var path = TempFile();
            try
            {
                await using var staged = Staged(path);
                Task Transfer() => store.DownloadFileAsync(file, staged, [], (_, _) => { checkpoints++; return Task.CompletedTask; });
                if (corrupt) await Assert.ThrowsExceptionAsync<InvalidDataException>(Transfer);
                else
                {
                    await Transfer();
                    Assert.AreEqual(file.Size, staged.Position);
                    CollectionAssert.AreEqual(data, await ReadStaged(staged, data.Length));
                    Assert.AreEqual(file.Size, staged.Position);
                }
                Assert.AreEqual(1, requests);
                Assert.AreEqual(0, checkpoints);
                Assert.IsTrue(staged.CanWrite);
            }
            finally { File.Delete(path); }
        }
    }

    [TestMethod]
    public async Task InvalidChunkLayoutIsRejectedBeforeDownloading()
    {
        var data = Encoding.UTF8.GetBytes("checkpoint validation");
        var file = Object(data);
        var requests = 0;
        using var store = Store(_ => { requests++; return Download(file, new MemoryStream(data), data.Length); });
        await Connect(store);
        var path = TempFile();
        try
        {
            await using var staged = Staged(path);
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.DownloadFileAsync(file, staged,
                [new(1, 5, file.Sha1!)], (_, _) => Task.CompletedTask));
            Assert.AreEqual(0, requests);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void HugeDownloadHasBoundedCheckpointCount()
    {
        foreach (var size in new[] { 0L, 100L, 8_388_608L, 10_000_000_000_000L, long.MaxValue })
        {
            var chunk = B2CloudStore.GetDownloadChunkSize(size);
            Assert.IsTrue(chunk >= B2CloudStore.DownloadChunkSize);
            Assert.AreEqual(0L, chunk % (64 * 1024));
            Assert.IsTrue(size / chunk + (size % chunk == 0 ? 0 : 1) <= 10_000);
        }
    }

    [TestMethod]
    public async Task EmptyDownloadStillValidatesCloudChecksumAndVersion()
    {
        var file = Object([]);
        using var store = Store(_ => Download(file, new MemoryStream(), 0));
        await Connect(store);
        var path = TempFile();
        try
        {
            await using var staged = Staged(path);
            await store.DownloadFileAsync(file, staged, [], (_, _) => Task.CompletedTask);
            Assert.AreEqual(0L, staged.Length);
        }
        finally { File.Delete(path); }
    }

    private static CloudObject Object(byte[] data) => new("immutable", "CloudInlet/file.bin", data.Length,
        Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(), DateTimeOffset.UnixEpoch);
    private static B2CloudStore Store(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler((request, _) =>
        Task.FromResult(Authorize(request) ? Authorization() : send(request))));
    private static bool Authorize(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("b2_authorize_account");
    private static Task<CloudAccount> Connect(B2CloudStore store) => store.ConnectAsync(new("test-key-id", "test-key"));
    private static HttpResponseMessage Authorization() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            accountId = "account", authorizationToken = "account-token",
            apiInfo = new { storageApi = new
            {
                apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid",
                absoluteMinimumPartSize = 5_000_000, recommendedPartSize = 100_000_000,
                allowed = new { capabilities = new[] { "readFiles" }, buckets = (object?)null, namePrefix = "CloudInlet/" }
            } }
        }), Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Download(CloudObject file, Stream body, long length, long? offset = null)
    {
        var response = new HttpResponseMessage(offset is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = length;
        if (offset is { } start) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, file.Size);
        response.Headers.TryAddWithoutValidation("X-Bz-File-Id", file.FileId);
        response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", file.Sha1);
        return response;
    }
    private static HttpResponseMessage RangeDownload(HttpRequestMessage request, CloudObject file, byte[] bytes)
    {
        var range = request.Headers.Range?.Ranges.Single();
        var offset = range?.From ?? 0;
        var length = checked((int)((range?.To ?? bytes.Length - 1) - offset + 1));
        return Download(file, new MemoryStream(bytes, checked((int)offset), length, writable: false), length, range is null ? null : offset);
    }
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i * 29 + i / 317);
        return bytes;
    }
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "CloudInlet-download-" + Guid.NewGuid().ToString("N") + ".part");
    private static async Task<byte[]> ReadStaged(FileStream stream, int length)
    {
        var bytes = new byte[length];
        stream.Position = 0;
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }
    private static async Task ExpectCancellation(Func<Task> operation, CancellationToken token)
    {
        try { await operation(); }
        catch (OperationCanceledException) { Assert.IsTrue(token.IsCancellationRequested); return; }
        Assert.Fail("The caller-cancelled download unexpectedly completed.");
    }
    private static FileStream Staged(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
    private static void RaiseMaximum(ref int maximum, int value)
    {
        int old;
        do { old = Volatile.Read(ref maximum); if (old >= value) return; }
        while (Interlocked.CompareExchange(ref maximum, value, old) != old);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => report(value); }
    private class NonSeekableOutput : MemoryStream
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
    private sealed class CancelAfterWrite(CancellationTokenSource cancellation) : NonSeekableOutput
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await base.WriteAsync(buffer, cancellationToken); cancellation.Cancel(); }
    }
    private sealed class FailingOutput : NonSeekableOutput
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Disk full in test"));
    }
    private sealed class BrokenReadStream(byte[] delivered) : MemoryStream(delivered)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length ? ValueTask.FromException<int>(new IOException("Network break in test")) : base.ReadAsync(buffer, cancellationToken);
    }
    private sealed class CountedReadStream : MemoryStream
    {
        private readonly Action _dispose;
        private int _disposed;
        public CountedReadStream(byte[] bytes, Action dispose) : base(bytes) => _dispose = dispose;
        public CountedReadStream(byte[] bytes, int offset, int count, Action dispose) : base(bytes, offset, count, false) => _dispose = dispose;
        protected override void Dispose(bool disposing)
        { if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) _dispose(); base.Dispose(disposing); }
    }
}

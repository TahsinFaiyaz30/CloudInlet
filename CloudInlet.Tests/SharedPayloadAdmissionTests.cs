using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class SharedPayloadAdmissionTests
{
    private const int SourceLength = 512 * 1024;
    private static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    private static readonly string SourceHash = Hash(new byte[SourceLength]);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Settles(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(8));

    [TestMethod]
    public async Task NativeRelayAndSeparateOneDriveAccountsShareUploadCapWithoutBlockingMetadata()
    {
        using var nativeServer = new ProviderServer();
        using var relayServer = new ProviderServer();
        using var graphServer = new ProviderServer();
        var budget = new TransferBandwidthBudget();
        using var native = await B2Async(nativeServer, budget);
        using var relay = await B2Async(relayServer, budget);
        using var graphHttp = new HttpClient(graphServer);
        var graph = Graph(graphHttp, budget);
        var otherAccount = Graph(graphHttp, budget);
        budget.ConfigureRequestLimits(1, 2);
        var release = Signal();
        nativeServer.UploadWait = release.Task;
        var first = NativeUpload(native);
        await Settles(nativeServer.UploadStarted.Task);
        var cloud = new B2TransferEndpoint(relay, Destination);
        var second = cloud.UploadAsync(Request, new MemorySource(), null, (_, _) => Task.CompletedTask);
        var third = graph.PutSmallAsync("drive", "root", "small.bin", new byte[16], "fail", null);
        var fourth = otherAccount.UploadFragmentAsync("https://graph-upload.invalid/session", new byte[16], 0, 16);
        try
        {
            await Settles(graph.GetItemAsync("drive", "source"));
            await Settles(graph.CreateUploadSessionAsync("drive", "root", "metadata.bin", "fail", null));
            await Settles(relay.VerifyUploadAsync(SourceObject, "bucket"));
            await Task.Delay(100);
            Assert.AreEqual(0, relayServer.UploadSends);
            Assert.AreEqual(0, graphServer.UploadSends);
        }
        finally { release.TrySetResult(); }
        await Settles(Task.WhenAll(first, second, third, fourth));
        Assert.AreEqual(1, nativeServer.UploadSends);
        Assert.AreEqual(1, relayServer.UploadSends);
        Assert.AreEqual(2, graphServer.UploadSends);
    }

    [TestMethod]
    public async Task QueuedUploadCancellationAndLiveLimitChangesPreserveAdmission()
    {
        var budget = new TransferBandwidthBudget();
        using var firstServer = new ProviderServer();
        using var secondServer = new ProviderServer();
        using var thirdServer = new ProviderServer();
        using var firstHttp = new HttpClient(firstServer);
        using var secondHttp = new HttpClient(secondServer);
        using var thirdHttp = new HttpClient(thirdServer);
        var first = Graph(firstHttp, budget);
        var second = Graph(secondHttp, budget);
        var third = Graph(thirdHttp, budget);
        budget.ConfigureRequestLimits(1, 1);
        var releaseFirst = Signal(); var releaseSecond = Signal();
        firstServer.UploadWait = releaseFirst.Task; secondServer.UploadWait = releaseSecond.Task;
        var activeFirst = first.PutSmallAsync("drive", "root", "first.bin", new byte[16], "fail", null);
        await Settles(firstServer.UploadStarted.Task);
        using (var canceled = new CancellationTokenSource())
        {
            var waiting = second.PutSmallAsync("drive", "root", "canceled.bin", new byte[16], "fail", null, canceled.Token);
            canceled.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waiting);
        }
        Assert.AreEqual(0, secondServer.UploadSends);
        var activeSecond = second.PutSmallAsync("drive", "root", "second.bin", new byte[16], "fail", null);
        first.Configure(0, 0, 2, 1);
        await Settles(secondServer.UploadStarted.Task);
        first.Configure(0, 0, 1, 1);
        var waitingThird = third.PutSmallAsync("drive", "root", "third.bin", new byte[16], "fail", null);
        releaseFirst.TrySetResult();
        await Settles(activeFirst);
        await Task.Delay(100);
        Assert.AreEqual(0, thirdServer.UploadSends, "Decreasing the live limit waits for existing leases to finish.");
        releaseSecond.TrySetResult();
        await Settles(Task.WhenAll(activeSecond, waitingThird));
        Assert.AreEqual(1, thirdServer.UploadSends);
    }

    [TestMethod]
    public async Task UploadAdmissionLastsThroughAcknowledgmentBodyParsing()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer();
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        var read = Signal(); var release = Signal();
        b2Server.ResponseBodyRead = read; b2Server.ResponseBodyWait = release.Task;
        var upload = NativeUpload(b2);
        await Settles(read.Task);
        var waiting = graph.PutSmallAsync("drive", "root", "next.bin", new byte[16], "fail", null);
        try
        {
            await Settles(graph.GetItemAsync("drive", "source"));
            await Task.Delay(100);
            Assert.AreEqual(0, graphServer.UploadSends, "ResponseHeadersRead alone must not release an unfinished acknowledgment.");
        }
        finally { release.TrySetResult(); }
        await Settles(Task.WhenAll(upload, waiting));
    }

    [TestMethod]
    public async Task DownloadAdmissionFollowsReturnedRangeDisposalAndNeverBuffersItsPayload()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer();
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        var read = Signal(); var releaseRead = Signal();
        b2Server.DownloadBodyRead = read; b2Server.DownloadBodyWait = releaseRead.Task;
        var source = new B2TransferEndpoint(b2, SourceLocation).OpenSource(SourceEntry);
        var stream = await source.OpenReadAsync(0, SourceLength).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsFalse(read.Task.IsCompleted, "Opening a range must return the existing network stream without buffering it.");
        var waiting = graph.OpenReadAsync("drive", GraphSource, 0, SourceLength);
        try
        {
            await Settles(graphServer.MetadataStarted.Task);
            await Task.Delay(100);
            Assert.AreEqual(0, graphServer.DownloadSends);
        }
        finally { await stream.DisposeAsync(); stream.Dispose(); }
        await using var graphStream = await waiting.WaitAsync(TimeSpan.FromSeconds(8));
        var nativeDownload = b2.DownloadAsync(SourceObject, Stream.Null);
        await Task.Delay(100);
        Assert.AreEqual(1, b2Server.DownloadSends);
        await graphStream.DisposeAsync();
        releaseRead.TrySetResult();
        await Settles(nativeDownload);
        Assert.AreEqual(2, b2Server.DownloadSends);
    }

    [TestMethod]
    public async Task QueuedDownloadCancellationAndLiveChangesDoNotLeakOrInterruptStreams()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer();
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        await using var first = await new B2TransferEndpoint(b2, SourceLocation).OpenSource(SourceEntry).OpenReadAsync(0, SourceLength);
        using (var canceled = new CancellationTokenSource())
        {
            var waiting = graph.OpenReadAsync("drive", GraphSource, 0, SourceLength, canceled.Token);
            canceled.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waiting);
        }
        Assert.AreEqual(0, graphServer.DownloadSends);
        var pending = graph.OpenReadAsync("drive", GraphSource, 0, SourceLength);
        b2.ConfigureDownloads(2);
        await using var second = await pending.WaitAsync(TimeSpan.FromSeconds(8));
        b2.Configure(0, 0, 16, 1);
        var third = b2.DownloadAsync(SourceObject, Stream.Null);
        await first.DisposeAsync();
        await Task.Delay(100);
        Assert.AreEqual(1, b2Server.DownloadSends);
        await second.DisposeAsync();
        await Settles(third);
        Assert.AreEqual(2, b2Server.DownloadSends);
    }

    [TestMethod]
    public async Task AsymmetricB2ConfigurationDoesNotTemporarilyAdmitQueuedDownloads()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer();
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        b2.Configure(0, 0, 16, 1);
        await using var held = await new B2TransferEndpoint(b2, SourceLocation).OpenSource(SourceEntry).OpenReadAsync(0, SourceLength);
        using var canceled = new CancellationTokenSource();
        var queued = Enumerable.Range(0, 4).Select(_ => graph.OpenReadAsync("drive", GraphSource, 0, SourceLength, canceled.Token)).ToArray();
        for (var iteration = 0; iteration < 100; iteration++) b2.Configure(0, 0, 16, 1);
        await Task.Delay(100);
        Assert.AreEqual(0, graphServer.DownloadSends, "A 16-upload/1-download setting must never pulse the shared download bound to 16.");
        canceled.Cancel();
        foreach (var waiting in queued) await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waiting);
    }

    [TestMethod]
    public async Task WaitingForCloudSourceDownloadNeverOccupiesTheSharedUploadSlot()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer();
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        await using var held = await new B2TransferEndpoint(b2, SourceLocation).OpenSource(SourceEntry).OpenReadAsync(0, SourceLength);
        var pending = new B2TransferEndpoint(b2, Destination).UploadAsync(Request, new GraphSourceFile(graph), null, (_, _) => Task.CompletedTask);
        await Settles(graphServer.SecondMetadataStarted.Task);
        Assert.AreEqual(0, graphServer.DownloadSends);
        await Settles(graph.PutSmallAsync("drive", "root", "independent.bin", new byte[16], "fail", null));
        Assert.AreEqual(0, b2Server.UploadSends, "No B2 headers are sent before its cloud source is primed.");
        await held.DisposeAsync();
        await Settles(pending);
    }

    [TestMethod]
    public async Task OppositeCloudRelaysCompleteWithOneUploadAndOneDownloadSlot()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer();
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        var destination = new B2TransferEndpoint(b2, Destination);
        var graphSource = new GraphSourceFile(graph);
        var outward = destination.UploadAsync(Request, graphSource, null, (_, _) => Task.CompletedTask);
        async Task Reverse()
        {
            await using var source = await new B2TransferEndpoint(b2, SourceLocation).OpenSource(SourceEntry).OpenReadAsync(0, SourceLength);
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes);
            await source.DisposeAsync();
            await graph.PutSmallAsync("drive", "root", "reverse.bin", bytes.ToArray(), "fail", null);
        }
        await Settles(Task.WhenAll(outward, Reverse()));
        await Settles(destination.VerifyAsync(await outward, graphSource));
        Assert.AreEqual(1, b2Server.UploadSends);
        Assert.AreEqual(1, graphServer.UploadSends);
        Assert.AreEqual(1, b2Server.DownloadSends);
        Assert.AreEqual(1, graphServer.DownloadSends);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnhashedCrossProviderReceiptVerificationWorksWithOneDownloadSlot(bool corruptDestination)
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer { OmitB2SourceHash = true };
        using var graphServer = new ProviderServer { OmitGraphHashes = true, CorruptDownloadBody = corruptDestination };
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        var source = new B2TransferEndpoint(b2, SourceLocation).OpenSource(SourceEntry with { Sha1 = null });
        var destination = new OneDriveTransferEndpoint(graph, new("onedrive", "graph-account", "drive", "root", "", "OneDrive"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var verification = destination.VerifyAsync(new("graph-source", "source.bin", "source-v1", SourceLength, null, Request.OperationId), source, timeout.Token);
        if (corruptDestination) await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await verification);
        else await verification;
        Assert.AreEqual(1, b2Server.DownloadSends);
        Assert.AreEqual(1, graphServer.DownloadSends);
    }

    [TestMethod]
    public async Task FailedPayloadSendReleasesSharedAdmissionForAnotherProvider()
    {
        var budget = new TransferBandwidthBudget();
        using var graphServer = new ProviderServer { FailUpload = true };
        using var b2Server = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        await Assert.ThrowsExceptionAsync<IOException>(() => graph.PutSmallAsync("drive", "root", "failed.bin", new byte[16], "fail", null));
        await Settles(NativeUpload(b2));
        Assert.AreEqual(1, b2Server.UploadSends);
    }

    [TestMethod]
    public async Task RejectedB2UploadReleasesSharedAdmissionDuringRetryBackoff()
    {
        var budget = new TransferBandwidthBudget();
        using var b2Server = new ProviderServer { RejectFirstUpload = true };
        using var graphServer = new ProviderServer();
        using var b2 = await B2Async(b2Server, budget);
        using var http = new HttpClient(graphServer);
        var graph = Graph(http, budget);
        budget.ConfigureRequestLimits(1, 1);
        var retrying = NativeUpload(b2);
        await Settles(b2Server.RejectionRead.Task);
        await graph.PutSmallAsync("drive", "root", "while-retrying.bin", new byte[16], "fail", null).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, b2Server.UploadSends, "The other provider must progress before the rejected upload's two-second backoff ends.");
        await Settles(retrying);
        Assert.AreEqual(2, b2Server.UploadSends);
    }

    private static TransferLocation SourceLocation => new("b2", "account", "bucket", "", "", "B2 source");
    private static TransferLocation Destination => SourceLocation with { Path = "destination" };
    private static TransferEntry SourceEntry => new("b2-source", "source.bin", "b2-source", SourceLength, Modified, SourceHash);
    private static CloudObject SourceObject => new("b2-source", "source.bin", SourceLength, SourceHash, Modified);
    private static OneDriveItem GraphSource => new("graph-source", "source.bin", SourceLength, "source-v1", null, false, Modified, SourceHash, null, "https://cdn.invalid/source");
    private static TransferUploadRequest Request => new(new string('a', 64), "copied.bin", TransferConflictPolicy.Fail);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    private static async Task<CloudObject> NativeUpload(B2CloudStore store)
    {
        using var source = new MemoryStream(new byte[16], false);
        return await store.UploadAsync("bucket", "native.bin", source, 16, Hash(new byte[16]), Modified);
    }
    private static async Task<B2CloudStore> B2Async(ProviderServer server, TransferBandwidthBudget budget)
    {
        var store = new B2CloudStore(server, bandwidthBudget: budget);
        store.Configure(0, 0, 4);
        await store.ConnectAsync(new("test-key", "test-value"));
        return store;
    }
    private static OneDriveClient Graph(HttpClient http, TransferBandwidthBudget budget)
    {
        var client = new OneDriveClient(new("3f7bdd18-1b8e-44a4-8600-77c20f466005", tokens:
            new("test-token", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"), http: http), http, budget);
        client.Configure(0, 0, 4, 4);
        return client;
    }
    private sealed class MemorySource : ITransferSourceFile
    {
        public TransferEntry Entry => new("memory", "memory.bin", "v1", 16, Modified, Hash(new byte[16]));
        public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(new byte[checked((int)length)], false));
    }
    private sealed class GraphSourceFile(OneDriveClient client) : ITransferSourceFile
    {
        public TransferEntry Entry => new("graph-source", "source.bin", "source-v1", SourceLength, Modified, SourceHash);
        public Task ValidateAsync(CancellationToken cancellationToken = default) => client.ValidateAsync("drive", GraphSource, cancellationToken);
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default) => client.OpenReadAsync("drive", GraphSource, offset, length, cancellationToken);
    }

    private sealed class ProviderServer : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, object> _uploaded = new();
        public int UploadSends, DownloadSends, MetadataSends;
        public readonly TaskCompletionSource UploadStarted = Signal(), MetadataStarted = Signal(), SecondMetadataStarted = Signal(), RejectionRead = Signal();
        public Task? UploadWait, ResponseBodyWait, DownloadBodyWait;
        public TaskCompletionSource? ResponseBodyRead, DownloadBodyRead;
        public bool FailUpload, RejectFirstUpload, OmitB2SourceHash, OmitGraphHashes, CorruptDownloadBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!;
            if (url.Host is "b2-upload.invalid" or "graph-upload.invalid" || url.Host == "graph.microsoft.com" && request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref UploadSends); UploadStarted.TrySetResult();
                if (FailUpload) throw new IOException("Injected payload send failure.");
                if (UploadWait is not null) await UploadWait.WaitAsync(token);
                using var body = new MemoryStream();
                await request.Content!.CopyToAsync(body, token);
                var bytes = body.ToArray();
                if (url.Host != "b2-upload.invalid") return Json(GraphItem("uploaded", bytes.Length, OmitGraphHashes));
                var length = bytes.Length;
                var advertised = request.Headers.GetValues("X-Bz-Content-Sha1").Single();
                if (advertised == "hex_digits_at_end") length -= 40;
                var hash = Hash(bytes[..length]);
                if (advertised == "hex_digits_at_end") Assert.AreEqual(hash, Encoding.ASCII.GetString(bytes, length, 40));
                else Assert.AreEqual(hash, advertised);
                if (RejectFirstUpload && UploadSends == 1)
                {
                    var rejected = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(new GateReadStream(
                        JsonSerializer.SerializeToUtf8Bytes(new { code = "too_many_requests", message = "Retry later" }), RejectionRead, Task.CompletedTask)) };
                    rejected.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                    return rejected;
                }
                var key = Uri.UnescapeDataString(request.Headers.GetValues("X-Bz-File-Name").Single());
                var info = request.Headers.Where(header => header.Key.StartsWith("X-Bz-Info-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(header => header.Key[10..], header => header.Value.Single(), StringComparer.OrdinalIgnoreCase);
                var id = "uploaded-" + UploadSends;
                var metadata = B2Metadata(id, key, length, hash, info);
                _uploaded[id] = metadata;
                if (ResponseBodyWait is null) return Json(metadata);
                return new(HttpStatusCode.OK) { Content = new StreamContent(new GateReadStream(JsonSerializer.SerializeToUtf8Bytes(metadata), ResponseBodyRead!, ResponseBodyWait)) };
            }
            if (url.Host is "b2-download.invalid" or "cdn.invalid")
            {
                Interlocked.Increment(ref DownloadSends);
                var payload = new byte[SourceLength];
                if (CorruptDownloadBody) payload[0] = 1;
                var content = DownloadBodyWait is null ? (HttpContent)new ByteArrayContent(payload)
                    : new StreamContent(new GateReadStream(payload, DownloadBodyRead!, DownloadBodyWait));
                content.Headers.ContentLength = SourceLength;
                content.Headers.ContentRange = new ContentRangeHeaderValue(0, SourceLength - 1, SourceLength);
                var response = new HttpResponseMessage(request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
                response.Headers.TryAddWithoutValidation("X-Bz-File-Id", "b2-source");
                response.Headers.TryAddWithoutValidation("X-Bz-File-Name", "source.bin");
                response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", SourceHash);
                return response;
            }
            if (url.Host == "graph.microsoft.com")
            {
                var count = Interlocked.Increment(ref MetadataSends); MetadataStarted.TrySetResult();
                if (count >= 2) SecondMetadataStarted.TrySetResult();
                if (url.AbsolutePath.EndsWith("/createUploadSession", StringComparison.Ordinal))
                    return Json(new { uploadUrl = "https://graph-upload.invalid/session", expirationDateTime = DateTimeOffset.UtcNow.AddHours(1), nextExpectedRanges = new[] { "0-" } });
                return Json(GraphItem("graph-source", SourceLength, OmitGraphHashes));
            }
            var operation = url.Segments.Last().Trim('/');
            if (operation == "b2_authorize_account") return Json(new { accountId = "account", authorizationToken = "test-token", apiInfo = new { storageApi = new
            {
                apiUrl = "https://b2-api.invalid", downloadUrl = "https://b2-download.invalid", absoluteMinimumPartSize = 5_000_000,
                recommendedPartSize = 5_000_000, allowed = new { buckets = (object?)null, namePrefix = (string?)null,
                    capabilities = new[] { "listBuckets", "listFiles", "writeFiles", "readFiles", "deleteFiles" } }
            } } });
            if (operation == "b2_get_upload_url") return Json(new { uploadUrl = "https://b2-upload.invalid/small", authorizationToken = "test-upload-token" });
            if (operation == "b2_list_file_names") return Json(new { files = Array.Empty<object>(), nextFileName = (string?)null });
            if (operation == "b2_get_file_info")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var id = body.RootElement.GetProperty("fileId").GetString()!;
                return Json(_uploaded.TryGetValue(id, out var file) ? file : B2Metadata("b2-source", "source.bin", SourceLength, OmitB2SourceHash ? null : SourceHash, new()));
            }
            throw new AssertFailedException("Unexpected test provider operation: " + operation);
        }
        private static object GraphItem(string id, int size, bool omitHashes) => new Dictionary<string, object>
        {
            ["id"] = id, ["name"] = "source.bin", ["size"] = size, ["eTag"] = id == "graph-source" ? "source-v1" : "uploaded-v1",
            ["lastModifiedDateTime"] = Modified, ["file"] = new { hashes = omitHashes ? null : new { sha1Hash = Hash(new byte[size]) } },
            ["@microsoft.graph.downloadUrl"] = "https://cdn.invalid/source"
        };
        private static object B2Metadata(string id, string key, int size, string? hash, Dictionary<string, string> info) => new
        { accountId = "account", bucketId = "bucket", fileId = id, fileName = key, contentLength = size, contentSha1 = hash ?? "none", action = "upload", uploadTimestamp = Modified.ToUnixTimeMilliseconds(), fileInfo = info };
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class GateReadStream(byte[] bytes, TaskCompletionSource read, Task release) : MemoryStream(bytes, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            read.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

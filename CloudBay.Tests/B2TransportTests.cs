using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.B2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class B2TransportTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("small file payload");
    private static readonly string DataSha = Convert.ToHexString(SHA1.HashData(Data)).ToLowerInvariant();
    private static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    private static readonly CloudObject File = new("version", "CloudBay/data.txt", Data.Length, DataSha, Modified);

    [TestMethod]
    public async Task StalledMetadataHeadersRetryWithoutExposingRequestSecrets()
    {
        var attempts = 0;
        using var store = new B2CloudStore(new FakeHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Authorization();
        }), metadataTimeout: TimeSpan.FromMilliseconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var account = await store.ConnectAsync(new("id", "private"), guard.Token);
        Assert.AreEqual("account", account.AccountId);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task StalledMetadataBodyIsDisposedAndRetried()
    {
        var attempts = 0;
        var stalled = new StalledReadStream([]);
        using var store = new B2CloudStore(new FakeHandler((request, _) =>
        {
            if (Operation(request) == "b2_authorize_account") return Task.FromResult(Authorization());
            Assert.AreEqual("b2_list_buckets", Operation(request));
            return Task.FromResult(++attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }
                : Json(new { buckets = new[] { new { bucketId = "bucket", bucketName = "backup" } } }));
        }), metadataTimeout: TimeSpan.FromMilliseconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        Assert.AreEqual("backup", (await store.ListBucketsAsync(guard.Token)).Single().Name);
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(stalled.Disposed, "A timed-out response must release its stream and connection.");
    }

    [TestMethod]
    public async Task DownloadInactivityRetriesFromOriginalDestinationPosition()
    {
        var attempts = 0;
        var stalled = new StalledReadStream(Data[..5]);
        using var store = new B2CloudStore(new FakeHandler((request, _) =>
        {
            if (Operation(request) == "b2_authorize_account") return Task.FromResult(Authorization());
            if (++attempts > 1) return Task.FromResult(Download(Data));
            var response = Download(Data);
            response.Content = new StreamContent(stalled);
            response.Content.Headers.ContentLength = Data.Length;
            return Task.FromResult(response);
        }), transferInactivityTimeout: TimeSpan.FromMilliseconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        using var output = new MemoryStream();
        output.WriteByte(77);
        await store.DownloadAsync(File, output, cancellationToken: guard.Token);
        CollectionAssert.AreEqual(new byte[] { 77 }.Concat(Data).ToArray(), output.ToArray());
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(stalled.Disposed);
    }

    [TestMethod]
    public async Task UploadInactivityRetiresEndpointAndReplaysCompleteSource()
    {
        var attempts = 0;
        var targets = 0;
        using var store = new B2CloudStore(new FakeHandler(async (request, ct) =>
        {
            if (Operation(request) == "b2_authorize_account") return Authorization();
            if (Operation(request) == "b2_get_upload_url") return UploadTarget("timeout-" + ++targets);
            CollectionAssert.AreEqual(Data, await request.Content!.ReadAsByteArrayAsync(ct));
            if (++attempts == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ObjectResponse(File.Key);
        }), transferInactivityTimeout: TimeSpan.FromMilliseconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        using var source = new MemoryStream(Data);
        await store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified, cancellationToken: guard.Token);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(2, targets, "A stalled endpoint must not be returned to the reusable pool.");
        Assert.IsTrue(source.CanRead);
    }

    [TestMethod]
    public async Task StalledUploadSocketWriteIsCancelledBeforeRetry()
    {
        var attempts = 0;
        using var store = new B2CloudStore(new FakeHandler(async (request, ct) =>
        {
            if (Operation(request) == "b2_authorize_account") return Authorization();
            if (Operation(request) == "b2_get_upload_url") return UploadTarget("write-" + attempts);
            if (++attempts == 1)
            {
                using var blockedSocket = new StalledWriteStream();
                await request.Content!.CopyToAsync(blockedSocket, ct);
            }
            else CollectionAssert.AreEqual(Data, await request.Content!.ReadAsByteArrayAsync(ct));
            return ObjectResponse(File.Key);
        }), transferInactivityTimeout: TimeSpan.FromMilliseconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        using var source = new MemoryStream(Data);
        await store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified, cancellationToken: guard.Token);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task StalledUploadErrorBodyCannotHoldWorkerForever()
    {
        var attempts = 0;
        var stalled = new StalledReadStream([]);
        using var store = new B2CloudStore(new FakeHandler(async (request, ct) =>
        {
            if (Operation(request) == "b2_authorize_account") return Authorization();
            if (Operation(request) == "b2_get_upload_url") return UploadTarget("error-" + attempts);
            await request.Content!.CopyToAsync(Stream.Null, ct);
            return ++attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StreamContent(stalled) }
                : ObjectResponse(File.Key);
        }), metadataTimeout: TimeSpan.FromMilliseconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        using var source = new MemoryStream(Data);
        await store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified, cancellationToken: guard.Token);
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(stalled.Disposed);
    }

    [TestMethod]
    public async Task DeliberateBandwidthCapCanExceedTransferInactivityTimeout()
    {
        using var store = new B2CloudStore(new FakeHandler(async (request, ct) =>
        {
            if (Operation(request) == "b2_authorize_account") return Authorization();
            if (Operation(request) == "b2_get_upload_url") return UploadTarget("capped");
            using var received = new MemoryStream();
            await request.Content!.CopyToAsync(received, ct);
            CollectionAssert.AreEqual(Data, received.ToArray());
            return ObjectResponse(File.Key);
        }), metadataTimeout: TimeSpan.FromMilliseconds(100), transferInactivityTimeout: TimeSpan.FromMilliseconds(60));
        store.Configure(20, 0, 1);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        using var source = new MemoryStream(Data);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified, cancellationToken: guard.Token);
        Assert.IsTrue(elapsed.Elapsed > TimeSpan.FromMilliseconds(300), "The test must exercise an intentional delay longer than the timeout.");
    }

    [TestMethod]
    public async Task DeliberateDownloadCapCanExceedReadInactivityTimeout()
    {
        using var store = new B2CloudStore(new FakeHandler((request, _) =>
            Task.FromResult(Operation(request) == "b2_authorize_account" ? Authorization() : Download(Data))),
            transferInactivityTimeout: TimeSpan.FromMilliseconds(60));
        store.Configure(0, 20, 1);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.ConnectAsync(new("id", "private"), guard.Token);
        using var output = new MemoryStream();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await store.DownloadAsync(File, output, cancellationToken: guard.Token);
        CollectionAssert.AreEqual(Data, output.ToArray());
        Assert.IsTrue(elapsed.Elapsed > TimeSpan.FromMilliseconds(300));
    }

    [TestMethod]
    public async Task CallerCancellationIsNotRetriedAsNetworkTimeout()
    {
        var attempts = 0;
        using var store = new B2CloudStore(new FakeHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Authorization();
        }), metadataTimeout: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        try
        {
            await store.ConnectAsync(new("id", "private"), cancellation.Token);
            Assert.Fail("A cancelled operation must not report success.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task SequentialSmallFilesReuseUploadEndpointAndKeepSourceOpen()
    {
        var targets = 0;
        var files = 0;
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            if (Operation(r) == "b2_get_upload_url") { targets++; return UploadTarget("shared"); }
            Assert.AreEqual("https://upload.invalid/shared", r.RequestUri!.AbsoluteUri);
            Assert.IsFalse(r.Headers.ExpectContinue ?? false);
            Assert.IsFalse(r.Headers.ConnectionClose ?? false);
            Assert.AreEqual(Data.Length, r.Content!.Headers.ContentLength);
            Assert.AreEqual("CloudBay/hello%20%CE%B1%23.txt", r.Headers.GetValues("X-Bz-File-Name").Single());
            Assert.AreEqual(DataSha, r.Headers.GetValues("X-Bz-Content-Sha1").Single());
            Assert.AreEqual(Modified.ToUnixTimeMilliseconds().ToString(), r.Headers.GetValues("X-Bz-Info-src_last_modified_millis").Single());
            CollectionAssert.AreEqual(Data, await r.Content.ReadAsByteArrayAsync(ct));
            files++;
            return ObjectResponse("CloudBay/hello α#.txt");
        }));
        await store.ConnectAsync(new("id", "private"));
        using var source = new MemoryStream(Data);
        for (var i = 0; i < 3; i++)
        {
            source.Position = 0;
            await store.UploadAsync("bucket", "CloudBay/hello α#.txt", source, Data.Length, DataSha, Modified);
            Assert.IsTrue(source.CanRead);
        }
        Assert.AreEqual(1, targets);
        Assert.AreEqual(3, files);
    }

    [TestMethod]
    public async Task ParallelFilesNeverShareAnActiveUploadToken()
    {
        var targets = 0;
        var activeTokens = new ConcurrentDictionary<string, bool>();
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            if (Operation(r) == "b2_get_upload_url") return UploadTarget("target" + Interlocked.Increment(ref targets));
            var target = r.RequestUri!.AbsoluteUri;
            Assert.IsTrue(activeTokens.TryAdd(target, true), "An upload URL is concurrently in use.");
            try
            {
                await r.Content!.CopyToAsync(Stream.Null, ct);
                await Task.Delay(30, ct);
                return ObjectResponse(Uri.UnescapeDataString(r.Headers.GetValues("X-Bz-File-Name").Single()));
            }
            finally { activeTokens.TryRemove(target, out _); }
        }));
        store.Configure(0, 0, 3);
        await store.ConnectAsync(new("id", "private"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var source = new MemoryStream(Data);
            await store.UploadAsync("bucket", $"CloudBay/{i}.txt", source, Data.Length, DataSha, Modified);
        }));
        Assert.AreEqual(3, targets, "Workers should keep reusing their exclusive endpoints.");
    }

    [TestMethod]
    public async Task EmptyDirectoryMarkerPreservesTrailingSlashAndCanBeHidden()
    {
        const string key = "CloudBay/empty-folder/";
        const string sha = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        var hidden = false;
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            switch (Operation(r))
            {
                case "b2_authorize_account": return Authorization();
                case "b2_get_upload_url": return UploadTarget("empty");
                case "b2_hide_file":
                    using (var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)))
                        Assert.AreEqual(key, body.RootElement.GetProperty("fileName").GetString());
                    hidden = true;
                    return Json(new { fileId = "hide", fileName = key, contentLength = 0, contentSha1 = (string?)null, action = "hide", uploadTimestamp = 0 });
                case "b2_list_file_versions":
                    return Json(new { files = new[] { new { fileId = hidden ? "hide" : "empty", fileName = key, contentLength = 0, contentSha1 = hidden ? null : sha, action = hidden ? "hide" : "upload", uploadTimestamp = 0 } }, nextFileName = (string?)null, nextFileId = (string?)null });
            }
            Assert.AreEqual(key, r.Headers.GetValues("X-Bz-File-Name").Single());
            Assert.AreEqual(0L, r.Content!.Headers.ContentLength);
            Assert.AreEqual(sha, r.Headers.GetValues("X-Bz-Content-Sha1").Single());
            await r.Content.CopyToAsync(Stream.Null, ct);
            return Json(new { fileId = "empty", fileName = key, contentLength = 0, contentSha1 = sha, action = "upload", uploadTimestamp = 0 });
        }));
        await store.ConnectAsync(new("id", "private"));
        using var source = new MemoryStream();
        var uploaded = await store.UploadAsync("bucket", key, source, 0, sha, Modified);
        Assert.AreEqual(key, uploaded.Key);
        await foreach (var file in store.ListAsync("bucket", "CloudBay/")) Assert.AreEqual(key, file.Key);
        await store.HideAsync("bucket", key);
        await foreach (var file in store.ListAsync("bucket", "CloudBay/")) Assert.AreEqual("hide", file.Action);
    }

    [TestMethod]
    public async Task RejectedUploadIsDrainedBeforeNewTargetAndRetryReplaysWholeSource()
    {
        var targets = 0;
        var uploads = 0;
        var errorBody = new TrackingStream(Encoding.UTF8.GetBytes("{\"code\":\"service_unavailable\",\"message\":\"private token\"}"));
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            if (Operation(r) == "b2_get_upload_url")
            {
                if (targets > 0) Assert.IsTrue(errorBody.AtEnd, "The rejected response was not drained.");
                return UploadTarget("target" + ++targets);
            }
            CollectionAssert.AreEqual(Data, await r.Content!.ReadAsByteArrayAsync(ct));
            if (++uploads == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StreamContent(errorBody) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            }
            return ObjectResponse(File.Key);
        }));
        await store.ConnectAsync(new("id", "private"));
        using var source = new MemoryStream(Data);
        await store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified);
        Assert.AreEqual(2, targets);
        Assert.AreEqual(2, uploads);
    }

    [TestMethod]
    public async Task RestrictedKeysExposeKnownBucketsWithoutListBucketsPermission()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            calls++;
            return Task.FromResult(Authorization(["listFiles", "readFiles", "writeFiles"],
                [new { id = "bucket", name = "Backup" }, new { id = "second", name = "Shared" }], "CloudBay/"));
        }));
        var account = await store.ConnectAsync(new("id", "private"));
        Assert.IsNull(account.AllowedBucketId, "A multi-bucket key must not be treated as a single-bucket key.");
        Assert.AreEqual(2, (await store.ListBucketsAsync()).Count);
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(async () =>
        {
            await foreach (var _ in store.ListAsync("other", "CloudBay/")) { }
        });
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(async () =>
        {
            await foreach (var _ in store.ListAsync("bucket", "Forbidden/")) { }
        });
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task PaginatedCurrentListingKeepsDeletionMarkerAndSkipsOlderVersions()
    {
        var page = 0;
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            Assert.AreEqual("b2_list_file_versions", Operation(r));
            using var payload = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (page++ == 0)
                return Json(new
                {
                    files = new[] { Object("CloudBay/a.txt", "hide", "deleted"), Object("CloudBay/a.txt", "upload", "old") },
                    nextFileName = "CloudBay/a.txt", nextFileId = "older"
                });
            Assert.AreEqual("CloudBay/a.txt", payload.RootElement.GetProperty("startFileName").GetString());
            Assert.AreEqual("older", payload.RootElement.GetProperty("startFileId").GetString());
            return Json(new
            {
                files = new[] { Object("CloudBay/a.txt", "upload", "older"), Object("CloudBay/b.txt", "start", "unfinished"), Object("CloudBay/b.txt", "upload", "complete") },
                nextFileName = (string?)null, nextFileId = (string?)null
            });
        }));
        await store.ConnectAsync(new("id", "private"));
        var files = new List<CloudObject>();
        await foreach (var file in store.ListAsync("bucket", "CloudBay/")) files.Add(file);
        Assert.AreEqual(2, files.Count);
        Assert.AreEqual("hide", files[0].Action);
        Assert.AreEqual("complete", files[1].FileId);
        Assert.AreEqual(Modified, files[1].ModifiedUtc);
    }

    [TestMethod]
    public async Task ConcurrentExpiredAccountRequestsShareOneReauthorization()
    {
        var authorizations = 0;
        var rejected = 0;
        var allRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account")
                return Authorization(token: "account" + Interlocked.Increment(ref authorizations));
            if (r.Headers.GetValues("Authorization").Single() == "account1")
            {
                if (Interlocked.Increment(ref rejected) == 4) allRejected.TrySetResult();
                await allRejected.Task.WaitAsync(ct);
                return Json(new { code = "expired_auth_token", message = "private" }, HttpStatusCode.Unauthorized);
            }
            return Json(new { buckets = new[] { new { bucketId = "bucket", bucketName = "Backup" } } });
        }));
        await store.ConnectAsync(new("id", "private"));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ListBucketsAsync()));
        Assert.AreEqual(2, authorizations);
    }

    [TestMethod]
    public async Task FullDownloadVerifiesSha1AndLeavesDestinationOpen()
    {
        using var store = new B2CloudStore(new FakeHandler((r, _) => Task.FromResult(
            Operation(r) == "b2_authorize_account" ? Authorization() : Download(Data))));
        await store.ConnectAsync(new("id", "private"));
        using var output = new MemoryStream();
        await store.DownloadAsync(File, output);
        CollectionAssert.AreEqual(Data, output.ToArray());
        Assert.IsTrue(output.CanWrite);
    }

    [TestMethod]
    public async Task CorruptFullDownloadCannotReportSuccess()
    {
        var corrupt = (byte[])Data.Clone(); corrupt[0] ^= 0xff;
        using var store = new B2CloudStore(new FakeHandler((r, _) => Task.FromResult(
            Operation(r) == "b2_authorize_account" ? Authorization() : Download(corrupt))));
        await store.ConnectAsync(new("id", "private"));
        using var output = new MemoryStream();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadAsync(File, output));
    }

    [TestMethod]
    public async Task HydrationReadsOnlyTheExactRequestedRange()
    {
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            Assert.AreEqual("bytes=3-7", r.Headers.Range!.ToString());
            return Task.FromResult(Download(Data[3..8], new ContentRangeHeaderValue(3, 7, Data.Length)));
        }));
        await store.ConnectAsync(new("id", "private"));
        using var output = new MemoryStream();
        await store.DownloadAsync(File, output, 3, 5);
        CollectionAssert.AreEqual(Data[3..8], output.ToArray());
    }

    [TestMethod]
    public async Task IgnoredOrIncorrectRangeIsRejectedBeforeWriting()
    {
        foreach (var range in new ContentRangeHeaderValue?[] { null, new(2, 6, Data.Length) })
        {
            using var store = new B2CloudStore(new FakeHandler((r, _) => Task.FromResult(
                Operation(r) == "b2_authorize_account" ? Authorization() : Download(range is null ? Data : Data[2..7], range))));
            await store.ConnectAsync(new("id", "private"));
            using var output = new MemoryStream();
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadAsync(File, output, 3, 5));
            Assert.AreEqual(0L, output.Length);
        }
    }

    [TestMethod]
    public async Task ForbiddenErrorDoesNotRetryOrRevealServerEchoedSecrets()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            calls++;
            return Task.FromResult(Json(new { code = "storage_cap_exceeded", message = "Authorization: private-token-key" }, HttpStatusCode.Forbidden));
        }));
        await store.ConnectAsync(new("id", "private-token-key"));
        var error = await Assert.ThrowsExceptionAsync<B2RequestException>(() => store.HideAsync("bucket", File.Key));
        Assert.AreEqual(1, calls);
        Assert.AreEqual("storage_cap_exceeded", error.Code);
        Assert.IsFalse(error.ToString().Contains("private-token-key"));
    }

    [TestMethod]
    public async Task RateLimitRetryWaitIsCancellableAndDoesNotSpin()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            calls++;
            var response = Json(new { code = "too_many_requests" }, HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        }));
        await store.ConnectAsync(new("id", "private"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => store.ListBucketsAsync(cancel.Token));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task RetryExhaustionIsBoundedAndRetainsSafeFailureCode()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            calls++;
            var response = Json(new { code = "too_many_requests", message = "private-token" }, HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return Task.FromResult(response);
        }));
        await store.ConnectAsync(new("id", "private-token"));
        var failure = await Assert.ThrowsExceptionAsync<B2RequestException>(() => store.ListBucketsAsync());
        Assert.AreEqual(5, calls);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.IsFalse(failure.ToString().Contains("private-token"));
    }

    [TestMethod]
    public async Task NetworkRetryKeepsExceptionTextAndInnerExceptionsSecretFree()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            if (calls++ == 0) throw new HttpRequestException("Failed https://upload.invalid/private-token", new Exception("private-token"));
            return Task.FromResult(Json(new { buckets = new[] { new { bucketId = "bucket", bucketName = "Backup" } } }));
        }));
        await store.ConnectAsync(new("id", "private-token"));
        Assert.AreEqual(1, (await store.ListBucketsAsync()).Count);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ChangingSpeedCapToUnlimitedUnblocksActiveTransferPromptly()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            if (Operation(r) == "b2_get_upload_url") return UploadTarget("limited");
            started.TrySetResult();
            await r.Content!.CopyToAsync(Stream.Null, ct);
            return ObjectResponse(File.Key);
        }));
        store.Configure(1, 0, 1);
        await store.ConnectAsync(new("id", "private"));
        using var source = new MemoryStream(Data);
        var transfer = store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified);
        await started.Task;
        await Task.Delay(120);
        Assert.IsFalse(transfer.IsCompleted, "The active speed cap was ignored.");
        store.Configure(0, 0, 1);
        await transfer.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task LargeVersionRestoreCopiesOrderedByteRangesWithoutDownloading()
    {
        const long size = 5_000_000_000;
        var ranges = new ConcurrentDictionary<int, string>();
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            using var payload = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            switch (Operation(r))
            {
                case "b2_start_large_file": return Json(new { fileId = "restore-large" });
                case "b2_copy_part":
                    var number = payload.RootElement.GetProperty("partNumber").GetInt32();
                    ranges[number] = payload.RootElement.GetProperty("range").GetString()!;
                    return Json(new { contentSha1 = number.ToString("x40") });
                case "b2_finish_large_file":
                    var hashes = payload.RootElement.GetProperty("partSha1Array").EnumerateArray().Select(v => v.GetString()).ToArray();
                    Assert.AreEqual(50, hashes.Length);
                    for (var i = 0; i < 50; i++)
                    {
                        Assert.AreEqual((i + 1).ToString("x40"), hashes[i]);
                        Assert.AreEqual($"bytes={i * 100_000_000L}-{(i + 1) * 100_000_000L - 1}", ranges[i + 1]);
                    }
                    return Json(new { fileId = "restore-large", fileName = File.Key, contentLength = size, contentSha1 = "none", action = "upload", uploadTimestamp = 0,
                        fileInfo = new { large_file_sha1 = DataSha } });
            }
            Assert.Fail("Large restore attempted an unexpected operation: " + Operation(r));
            return null!;
        }));
        await store.ConnectAsync(new("id", "private"));
        var restored = await store.RestoreAsync("bucket", File with { Size = size });
        Assert.AreEqual(size, restored.Size);
        Assert.AreEqual(50, ranges.Count);
    }

    [TestMethod]
    public async Task LostMultipartFinishAcknowledgmentRecoversByKnownFileId()
    {
        var lookedUp = false;
        const long size = 5_000_000_000;
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            switch (Operation(r))
            {
                case "b2_start_large_file": return Json(new { fileId = "finished-remotely" });
                case "b2_copy_part": return Json(new { contentSha1 = DataSha });
                case "b2_finish_large_file": return Json(new { code = "bad_request", message = "already finished" }, HttpStatusCode.BadRequest);
                case "b2_get_file_info":
                    using (var payload = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)))
                        Assert.AreEqual("finished-remotely", payload.RootElement.GetProperty("fileId").GetString());
                    lookedUp = true;
                    return Json(new { fileId = "finished-remotely", fileName = File.Key, action = "upload", contentLength = size,
                        contentSha1 = "none", uploadTimestamp = 0, fileInfo = new { large_file_sha1 = DataSha } });
                case "b2_cancel_large_file": Assert.Fail("Completed content must not be canceled."); break;
            }
            Assert.Fail("Unexpected operation."); return null!;
        }));
        await store.ConnectAsync(new("id", "private"));
        var restored = await store.RestoreAsync("bucket", File with { Size = size });
        Assert.IsTrue(lookedUp);
        Assert.AreEqual("finished-remotely", restored.FileId);
    }

    [TestMethod]
    public async Task OversizedMultipartFileIsRejectedBeforeAnyRemoteUploadStarts()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            calls++;
            Assert.AreEqual("b2_authorize_account", Operation(r));
            return Task.FromResult(Authorization());
        }));
        await store.ConnectAsync(new("id", "private"));
        using var source = new ZeroStream(10_000_000_000_001);
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            store.UploadAsync("bucket", File.Key, source, source.Length, DataSha, Modified));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ExpiredDownloadHeaderTokenIsRefreshedBeforeWriting()
    {
        var authorizations = 0;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization(token: "auth" + ++authorizations));
            if (r.Headers.GetValues("Authorization").Single() == "auth1")
                return Task.FromResult(Json(new { code = "bad_auth_token" }, HttpStatusCode.Unauthorized));
            return Task.FromResult(Download(Data));
        }));
        await store.ConnectAsync(new("id", "private"));
        using var output = new MemoryStream();
        await store.DownloadAsync(File, output);
        Assert.AreEqual(2, authorizations);
        CollectionAssert.AreEqual(Data, output.ToArray());
    }

    [TestMethod]
    public async Task ExternalMultipartFileWithoutChecksumReportsLimitedVerificationAndDownloads()
    {
        var reported = false;
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            var response = Download(Data);
            response.Headers.Remove("X-Bz-Content-Sha1");
            response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", "none");
            return Task.FromResult(response);
        }));
        store.Diagnostic += (_, note) => reported = note.Contains("no whole-file SHA1");
        await store.ConnectAsync(new("id", "private"));
        using var output = new MemoryStream();
        await store.DownloadAsync(File with { Sha1 = null }, output);
        Assert.IsTrue(reported);
        CollectionAssert.AreEqual(Data, output.ToArray());
    }

    [TestMethod]
    public async Task LargeFileChecksumHeaderStillDetectsCorruptPayload()
    {
        using var store = new B2CloudStore(new FakeHandler((r, _) =>
        {
            if (Operation(r) == "b2_authorize_account") return Task.FromResult(Authorization());
            var response = Download(new byte[Data.Length]);
            response.Headers.Remove("X-Bz-Content-Sha1");
            response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", "none");
            response.Headers.TryAddWithoutValidation("X-Bz-Info-large_file_sha1", DataSha);
            return Task.FromResult(response);
        }));
        await store.ConnectAsync(new("id", "private"));
        using var output = new MemoryStream();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DownloadAsync(File with { Sha1 = null }, output));
    }

    [TestMethod]
    public async Task CancellingMultipartStillRemovesUnfinishedChargedParts()
    {
        using var cancel = new CancellationTokenSource();
        var cleanupCalled = false;
        using var store = new B2CloudStore(new FakeHandler((r, ct) =>
        {
            switch (Operation(r))
            {
                case "b2_authorize_account": return Task.FromResult(Authorization());
                case "b2_start_large_file": return Task.FromResult(Json(new { fileId = "unfinished" }));
                case "b2_get_upload_part_url": return Task.FromResult(UploadTarget("cancel"));
                case "b2_cancel_large_file":
                    Assert.IsFalse(ct.IsCancellationRequested, "Cleanup must receive a separate cancellation token.");
                    cleanupCalled = true;
                    return Task.FromResult(Json(new { fileId = "unfinished" }));
            }
            cancel.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        }));
        store.Configure(0, 0, 1);
        await store.ConnectAsync(new("id", "private"));
        using var source = new ZeroStream(200_000_001);
        try
        {
            await store.UploadAsync("bucket", "CloudBay/large.bin", source, source.Length, ZeroHash(source.Length), Modified, cancellationToken: cancel.Token);
            Assert.Fail("Cancellation must interrupt the multipart upload.");
        }
        catch (OperationCanceledException) { }
        Assert.IsTrue(cleanupCalled);
    }

    [TestMethod]
    public async Task RestoreCreatesNewVersionThroughServerCopy()
    {
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            Assert.AreEqual("b2_copy_file", Operation(r));
            using var payload = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            Assert.AreEqual(File.FileId, payload.RootElement.GetProperty("sourceFileId").GetString());
            Assert.AreEqual("COPY", payload.RootElement.GetProperty("metadataDirective").GetString());
            return Json(Object(File.Key, "copy", "restored"));
        }));
        await store.ConnectAsync(new("id", "private"));
        var restored = await store.RestoreAsync("bucket", File);
        Assert.AreEqual("restored", restored.FileId);
        Assert.AreEqual(File.Sha1, restored.Sha1);
    }

    [TestMethod]
    public async Task NonSeekableUploadCanReplayWithoutBufferingWholeFileInMemory()
    {
        var calls = 0;
        using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
        {
            if (Operation(r) == "b2_authorize_account") return Authorization();
            if (Operation(r) == "b2_get_upload_url") return UploadTarget("spool" + calls);
            CollectionAssert.AreEqual(Data, await r.Content!.ReadAsByteArrayAsync(ct));
            if (calls++ == 0)
            {
                var failure = Json(new { code = "service_unavailable" }, HttpStatusCode.ServiceUnavailable);
                failure.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return failure;
            }
            return ObjectResponse(File.Key);
        }));
        await store.ConnectAsync(new("id", "private"));
        using var source = new NonSeekableStream(Data);
        await store.UploadAsync("bucket", File.Key, source, Data.Length, DataSha, Modified);
        Assert.AreEqual(2, calls);
        Assert.IsTrue(source.CanRead);
    }

    [TestMethod]
    public async Task MultipartStreamsOrderedPartsAndCancelsUnfinishedFileOnFailure()
    {
        const long size = 200_000_001;
        var partHashes = new ConcurrentDictionary<int, string>();
        var targetCount = 0;
        var cancelCount = 0;
        var key = "CloudBay/large.bin";
        var wholeHash = ZeroHash(size);
        foreach (var fail in new[] { false, true })
        {
            partHashes.Clear(); targetCount = 0;
            using var store = new B2CloudStore(new FakeHandler(async (r, ct) =>
            {
                switch (Operation(r))
                {
                    case "b2_authorize_account": return Authorization();
                    case "b2_start_large_file":
                        using (var payload = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)))
                            Assert.AreEqual(wholeHash, payload.RootElement.GetProperty("fileInfo").GetProperty("large_file_sha1").GetString());
                        return Json(new { fileId = "large" });
                    case "b2_get_upload_part_url": return UploadTarget("part" + Interlocked.Increment(ref targetCount));
                    case "b2_finish_large_file":
                        using (var payload = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)))
                        {
                            var ordered = payload.RootElement.GetProperty("partSha1Array").EnumerateArray().Select(x => x.GetString()).ToArray();
                            Assert.AreEqual(3, ordered.Length);
                            for (var i = 0; i < ordered.Length; i++) Assert.AreEqual(partHashes[i + 1], ordered[i]);
                        }
                        return Json(new { fileId = "large", fileName = key, contentLength = size, contentSha1 = "none", action = "upload", uploadTimestamp = 0,
                            fileInfo = new { large_file_sha1 = wholeHash, src_last_modified_millis = Modified.ToUnixTimeMilliseconds().ToString() } });
                    case "b2_cancel_large_file": Interlocked.Increment(ref cancelCount); return Json(new { fileId = "large" });
                }
                var number = int.Parse(r.Headers.GetValues("X-Bz-Part-Number").Single());
                if (fail) return Json(new { code = "storage_cap_exceeded" }, HttpStatusCode.Forbidden);
                await Task.Delay(10, ct);
                await r.Content!.CopyToAsync(Stream.Null, ct);
                partHashes[number] = r.Headers.GetValues("X-Bz-Content-Sha1").Single();
                Assert.AreEqual(ZeroHash(r.Content.Headers.ContentLength!.Value), partHashes[number]);
                return Json(new { partNumber = number, contentLength = r.Content.Headers.ContentLength, contentSha1 = partHashes[number] });
            }));
            store.Configure(0, 0, 2);
            await store.ConnectAsync(new("id", "private"));
            using var source = new ZeroStream(size);
            if (fail)
                await Assert.ThrowsExceptionAsync<B2RequestException>(() => store.UploadAsync("bucket", key, source, size, wholeHash, Modified));
            else
            {
                var uploaded = await store.UploadAsync("bucket", key, source, size, wholeHash, Modified);
                Assert.AreEqual(size, uploaded.Size);
                Assert.AreEqual(2, targetCount, "Each multipart worker should reuse its upload URL.");
            }
        }
        Assert.AreEqual(1, cancelCount);
    }

    private static string Operation(HttpRequestMessage request) => request.RequestUri!.Segments.Last().Trim('/');
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Authorization(string[]? capabilities = null, object[]? buckets = null,
        string? prefix = null, string token = "account") => Json(new
    {
        accountId = "account", authorizationToken = token,
        apiInfo = new { storageApi = new
        {
            apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid", absoluteMinimumPartSize = 5_000_000, recommendedPartSize = 100_000_000,
            allowed = new { buckets, capabilities = capabilities ?? ["listBuckets", "listFiles", "readFiles", "writeFiles"], namePrefix = prefix }
        } }
    });
    private static HttpResponseMessage UploadTarget(string target) => Json(new { uploadUrl = "https://upload.invalid/" + target, authorizationToken = target });
    private static object Object(string key, string action = "upload", string id = "version") => new
    {
        fileId = id, fileName = key, contentLength = action is "upload" or "copy" ? Data.Length : 0, contentSha1 = action is "upload" or "copy" ? DataSha : null,
        action, uploadTimestamp = 0, fileInfo = new { src_last_modified_millis = Modified.ToUnixTimeMilliseconds().ToString() }
    };
    private static HttpResponseMessage ObjectResponse(string key, string id = "version") => Json(Object(key, id: id));
    private static HttpResponseMessage Download(byte[] body, ContentRangeHeaderValue? range = null)
    {
        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = body.Length;
        response.Content.Headers.ContentRange = range;
        response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", DataSha);
        response.Headers.TryAddWithoutValidation("X-Bz-File-Id", File.FileId);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool AtEnd { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (Position == Length) AtEnd = true;
            return read;
        }
    }
    private sealed class StalledReadStream(byte[] initialBytes) : MemoryStream(initialBytes)
    {
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class StalledWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private static string ZeroHash(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var chunk = new byte[64 * 1024];
        while (length > 0) { var count = (int)Math.Min(chunk.Length, length); hash.AppendData(chunk, 0, count); length -= count; }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private sealed class ZeroStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, length - Position);
            Array.Clear(buffer, offset, count); Position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - Position);
            buffer.Span[..count].Clear(); Position += count; return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

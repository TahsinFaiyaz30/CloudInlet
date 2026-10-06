using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class B2TransferEndpointTests
{
    private static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    private static TransferLocation Location => new("b2", "account", "bucket", "", "dest", "B2 destination");
    private static TransferUploadRequest Request(int number = 0, TransferConflictPolicy policy = TransferConflictPolicy.Fail) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("operation-" + number))).ToLowerInvariant(), "file-" + number + ".bin", policy);

    [TestMethod]
    public async Task TinyFilesStreamOnceAndReuseAuthorizationAndUploadEndpoint()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        for (var i = 0; i < 20; i++)
        {
            var source = new Source(127, sha1: PatternHash(127));
            TransferCheckpoint? saved = null;
            Assert.IsNull(await endpoint.ReconcileAsync(Request(i), source, null),
                "A fresh durable plan has no prior network creation to reconcile.");
            var receipt = await endpoint.UploadAsync(Request(i), source, null,
                (value, _) => { saved = value; return Task.CompletedTask; });
            var verified = await endpoint.VerifyReceiptAsync(receipt, source);
            Assert.AreEqual(PatternHash(source.Entry.Size), verified.Sha1, "Streaming verification must return the durable digest used by exact local Move deletion.");
            Assert.AreEqual(1, source.Opens.Count, "Tiny files must not have a preparatory checksum/download pass.");
            Assert.AreEqual("true", saved!.Data!["pending"], "The durable creation intent precedes sending payload.");
            Assert.AreEqual(127L, receipt.Size);
        }
        Assert.AreEqual(1, server.AuthorizationCalls);
        Assert.AreEqual(1, server.UploadUrlCalls, "Successful tiny files share the borrowed upload endpoint.");
        Assert.AreEqual(20, server.UploadCalls);
        Assert.AreEqual(0, server.VersionListCalls, "Fresh tiny files must not spend an extra round trip searching operation receipts.");
        Assert.AreEqual(20, server.Files.Count);
    }

    [TestMethod]
    public async Task LostSmallUploadAcknowledgmentFindsOperationReceiptWithoutReplay()
    {
        using var server = new Server { LoseSmallAcknowledgment = true };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(251);
        TransferCheckpoint? saved = null;
        var receipt = await endpoint.UploadAsync(Request(), source, null, (value, _) => { saved = value; return Task.CompletedTask; });
        Assert.AreEqual(1, source.Opens.Count);
        Assert.AreEqual(1, server.UploadCalls);
        Assert.AreEqual(1, server.Files.Count);
        Assert.IsNotNull(await endpoint.ReconcileAsync(Request(), source, saved));
        Assert.AreEqual(receipt.Id, (await endpoint.ReconcileAsync(Request(), source, saved))!.Id);
        Assert.IsNull(await endpoint.ReconcileAsync(Request(), source, null), "Recovery requires the durable creation intent.");
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    public async Task DefiniteRetryableRejectionWithTruncatedBodyReleasesIntentAndUsesFreshEndpoint(HttpStatusCode status)
    {
        using var server = new Server { RejectSmallUpload = status, BreakErrorBody = true, RejectOnlyOnce = true };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(4096);
        var checkpoints = new List<TransferCheckpoint>();

        var receipt = await endpoint.UploadAsync(Request(), source, null,
            (value, _) => { checkpoints.Add(value); return Task.CompletedTask; });

        Assert.AreEqual(4096L, receipt.Size);
        Assert.IsTrue(checkpoints.Any(value => value.Data?.GetValueOrDefault("pending") == "false"),
            "A definite rejected request cannot leave a blocking unknown-commit checkpoint.");
        Assert.AreEqual(2, server.UploadCalls);
        Assert.AreEqual(2, server.UploadUrlCalls, "A rejected upload endpoint is not reused.");
        Assert.AreEqual(0, server.VersionListCalls, "A definite rejection does not require uncertain receipt reconciliation.");
        Assert.AreEqual(1, server.Files.Count);
    }

    [TestMethod]
    public async Task DefiniteForbiddenRejectionWithTruncatedBodyRemainsSafelyRestartable()
    {
        using var server = new Server { RejectSmallUpload = HttpStatusCode.Forbidden, BreakErrorBody = true };
        var source = new Source(4096);
        TransferCheckpoint? saved = null;
        using (var store = await ConnectAsync(server))
        {
            var endpoint = new B2TransferEndpoint(store, Location);
            var error = await Assert.ThrowsExceptionAsync<B2RequestException>(() => endpoint.UploadAsync(Request(), source, null,
                (value, _) => { saved = value; return Task.CompletedTask; }));
            Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.AreEqual("false", saved!.Data!["pending"]);
            Assert.AreEqual(0, server.Files.Count);
        }
        server.RejectSmallUpload = null;
        using (var restarted = await ConnectAsync(server))
        {
            var endpoint = new B2TransferEndpoint(restarted, Location);
            Assert.IsNull(await endpoint.ReconcileAsync(Request(), source, saved));
            var receipt = await endpoint.UploadAsync(Request(), source, saved, (_, _) => Task.CompletedTask);
            Assert.AreEqual(4096L, receipt.Size);
        }
        Assert.AreEqual(2, server.UploadCalls, "Only the definitely rejected request and one successful restart are sent.");
        Assert.AreEqual(1, server.Files.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UncertainServerFailureAfterBodyRetainsSafeStatusWithoutReplaying(bool breakBody)
    {
        using var server = new Server { RejectSmallUpload = HttpStatusCode.ServiceUnavailable, BreakErrorBody = breakBody };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        TransferCheckpoint? saved = null;
        await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), new Source(4096), null,
            (value, _) => { saved = value; return Task.CompletedTask; }));

        Assert.AreEqual("true", saved!.Data!["pending"]);
        Assert.AreEqual("503", saved.Data["response_status"]);
        Assert.AreEqual(breakBody ? "provider_error_body_unreadable" : "provider_response", saved.Data["failure_category"]);
        Assert.IsFalse(JsonSerializer.Serialize(saved).Contains("private-token", StringComparison.Ordinal));
        Assert.AreEqual(1, server.UploadCalls);
        Assert.AreEqual(0, server.Files.Count);
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.Found, false)]
    [DataRow(HttpStatusCode.TemporaryRedirect, true)]
    [DataRow(HttpStatusCode.Conflict, false)]
    [DataRow(HttpStatusCode.RequestTimeout, true)]
    public async Task UnexpectedOrUncertainStatusAfterBodyNeverReleasesCreationIntent(HttpStatusCode status, bool breakBody)
    {
        using var server = new Server { RejectSmallUpload = status, BreakErrorBody = breakBody, MalformedErrorBody = true };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(4096);
        TransferCheckpoint? saved = null;
        await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), source, null,
            (value, _) => { saved = value; return Task.CompletedTask; }));

        Assert.AreEqual("true", saved!.Data!["pending"]);
        Assert.AreEqual(((int)status).ToString(), saved.Data["response_status"]);
        Assert.AreEqual(breakBody ? "provider_error_body_unreadable" : "provider_response", saved.Data["failure_category"]);
        Assert.IsFalse(JsonSerializer.Serialize(saved).Contains("private-token", StringComparison.Ordinal));
        await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), source, saved, (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, server.UploadCalls, "An unrecognized response cannot authorize duplicate creation after restart.");
        Assert.AreEqual(1, source.Opens.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MalformedDefiniteRejectionBodyRetainsOnlySafeProviderCode(bool nonObjectBody)
    {
        using var server = new Server { RejectSmallUpload = HttpStatusCode.Forbidden, MalformedErrorBody = true, NonObjectErrorBody = nonObjectBody };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        TransferCheckpoint? saved = null;
        var error = await Assert.ThrowsExceptionAsync<B2RequestException>(() => endpoint.UploadAsync(Request(), new Source(4096), null,
            (value, _) => { saved = value; return Task.CompletedTask; }));

        Assert.AreEqual("false", saved!.Data!["pending"]);
        Assert.AreEqual("request_failed", error.Code);
        Assert.IsFalse(error.ToString().Contains("private-token", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(saved).Contains("private-token", StringComparison.Ordinal));
        Assert.AreEqual(1, server.UploadCalls);
        Assert.AreEqual(0, server.Files.Count);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task MalformedOrTruncatedSuccessAcknowledgmentPreservesUnknownCommitUntilReceiptAppears(bool breakBody, bool nonObjectBody)
    {
        using var server = new Server { MalformedSmallAcknowledgment = true, BreakSmallAcknowledgmentBody = breakBody,
            NonObjectSmallAcknowledgment = nonObjectBody, HideReceipts = true };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(4096);
        TransferCheckpoint? saved = null;
        await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), source, null,
            (value, _) => { saved = value; return Task.CompletedTask; }));

        Assert.AreEqual("true", saved!.Data!["pending"]);
        Assert.AreEqual("200", saved.Data["response_status"]);
        Assert.AreEqual("acknowledgment_unreadable", saved.Data["failure_category"]);
        Assert.IsFalse(JsonSerializer.Serialize(saved).Contains("private-token", StringComparison.Ordinal));
        await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), source, saved, (_, _) => Task.CompletedTask));
        server.HideReceipts = false;
        var recovered = await endpoint.ReconcileAsync(Request(), source, saved);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(1, server.Files.Count);
        Assert.AreEqual(1, server.UploadCalls);
        Assert.AreEqual(1, source.Opens.Count);
    }

    [TestMethod]
    public async Task ReceiptReconciliationIgnoresUnfinishedStartVersions()
    {
        using var server = new Server();
        var source = new Source(70 * 1024 * 1024);
        TransferCheckpoint? saved = null;
        using var store = await ConnectAsync(server);
        store.Configure(0, 0, 1);
        var endpoint = new B2TransferEndpoint(store, Location);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => endpoint.UploadAsync(Request(), source, null,
            (value, _) => { saved = value; if (value.AcknowledgedBytes > 0) throw new OperationCanceledException(); return Task.CompletedTask; }));
        server.IncludeStartsInVersionListing = true;
        Assert.IsNull(await endpoint.ReconcileAsync(Request(), source, saved), "B2 returns unfinished starts in version listings; these are not completed receipts.");
    }

    [TestMethod]
    public async Task UnknownSmallCompletionRefusesDuplicateReplayAcrossRestart()
    {
        using var server = new Server { LoseSmallAcknowledgment = true, HideReceipts = true };
        var source = new Source(251);
        TransferCheckpoint? saved = null;
        using (var store = await ConnectAsync(server))
        {
            var endpoint = new B2TransferEndpoint(store, Location);
            await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), source, null,
                (value, _) => { saved = value; return Task.CompletedTask; }));
        }
        using (var store = await ConnectAsync(server))
        {
            var endpoint = new B2TransferEndpoint(store, Location);
            await AssertIOExceptionAsync(() => endpoint.UploadAsync(Request(), source, saved, (_, _) => Task.CompletedTask));
        }
        Assert.AreEqual(1, server.UploadCalls);
        Assert.AreEqual(1, source.Opens.Count);
    }

    [TestMethod]
    public async Task MultipartRestartReusesAcknowledgedRangesAndNeverPrescansWholeSource()
    {
        using var server = new Server();
        var source = new Source(70 * 1024 * 1024);
        TransferCheckpoint? saved = null;
        using (var store = await ConnectAsync(server))
        {
            store.Configure(0, 0, 1);
            var endpoint = new B2TransferEndpoint(store, Location);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => endpoint.UploadAsync(Request(), source, null,
                (value, _) =>
                {
                    saved = value;
                    if (value.AcknowledgedBytes > 0) throw new OperationCanceledException();
                    return Task.CompletedTask;
                }));
        }
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.AcknowledgedBytes > 0);
        var firstRange = source.Opens.Single();
        Assert.IsTrue(firstRange.Length < source.Entry.Size);
        using (var store = await ConnectAsync(server))
        {
            store.Configure(0, 0, 2);
            var endpoint = new B2TransferEndpoint(store, Location);
            var receipt = await endpoint.UploadAsync(Request(), source, saved, (value, _) => { saved = value; return Task.CompletedTask; });
            Assert.AreEqual(1, source.Opens.Count(r => r.Offset == firstRange.Offset), "Acknowledged ranges must never be fetched again after restart.");
            Assert.AreEqual(1, server.StartCalls);
            Assert.AreEqual(1, server.Files.Count);
            var verified = await endpoint.VerifyReceiptAsync(receipt, source);
            Assert.AreEqual(PatternHash(source.Entry.Size), verified.Sha1, "An external multipart source without a hash needs a verified durable whole-file digest.");
        }
        Assert.AreEqual(source.Entry.Size, saved!.AcknowledgedBytes);
    }

    [TestMethod]
    public async Task MultipartRejectsWrongPartChecksumAndPreservesCheckpoint()
    {
        using var server = new Server { CorruptPartChecksum = true };
        using var store = await ConnectAsync(server);
        store.Configure(0, 0, 1);
        var endpoint = new B2TransferEndpoint(store, Location);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => endpoint.UploadAsync(Request(), new Source(70 * 1024 * 1024), null,
            (value, _) => { saved = value; return Task.CompletedTask; }));
        Assert.IsNotNull(saved);
        Assert.AreEqual(0L, saved.AcknowledgedBytes);
        Assert.AreEqual(0, server.FinishCalls);
        Assert.AreEqual(0, server.CancelCalls, "Integrity failure must retain an inspectable remote/checkpoint state.");
    }

    [TestMethod]
    public async Task MultipartReadbackDetectsCorruptDestination()
    {
        using var server = new Server { CorruptDownload = true };
        using var store = await ConnectAsync(server);
        store.Configure(0, 0, 2);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(70 * 1024 * 1024);
        var receipt = await endpoint.UploadAsync(Request(), source, null, (_, _) => Task.CompletedTask);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => endpoint.VerifyAsync(receipt, source));
        Assert.AreEqual(0, server.DeleteCalls);
    }

    [TestMethod]
    public async Task SameB2AccountVerificationReleasesDestinationBeforeOpeningHashlessSource()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        store.Configure(0, 0, 1);
        const long size = 70 * 1024 * 1024;
        var sourceEntry = new TransferEntry("external-source", "source.bin", "external-source", size, Modified);
        server.Files[sourceEntry.Id] = Metadata(sourceEntry.Id, "source/source.bin", size, null, new());
        var sourceEndpoint = new B2TransferEndpoint(store, Location with { Path = "source", DisplayName = "B2 source" });
        var source = sourceEndpoint.OpenSource(sourceEntry);
        var request = Request();
        var sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { sourceEntry.Id, sourceEntry.Version, sourceEntry.Size, sourceEntry.ModifiedUtc })))).ToLowerInvariant();
        const string destinationId = "verified-destination";
        var key = "dest/" + request.RelativePath;
        server.Files[destinationId] = Metadata(destinationId, key, size, null, new()
            { ["cloudbay_upload_id"] = request.OperationId, ["cloudbay_source_id"] = sourceId });
        var receipt = new TransferReceipt(destinationId, request.RelativePath, destinationId, size, null,
            request.OperationId, new Dictionary<string, string> { ["key"] = key });
        var destinationEndpoint = new B2TransferEndpoint(store, Location);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var verified = await destinationEndpoint.VerifyReceiptAsync(receipt, source, deadline.Token);

        Assert.AreEqual(PatternHash(size), verified.Sha1);
        Assert.AreEqual(2, server.DownloadCalls, "Verification needs one independent destination read and one source read.");
        Assert.AreEqual(0, server.DeleteCalls);
    }

    [TestMethod]
    public async Task ChangedSourceStopsBeforeCreationAndChecksumMismatchStopsBeforeCommit()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var changed = new Source(127) { Changed = true };
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => endpoint.UploadAsync(Request(), changed, null, (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, server.UploadCalls);
        var wrongHash = new Source(127, sha1: new string('a', 40));
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => endpoint.UploadAsync(Request(1), wrongHash, null, (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, server.Files.Count);
    }

    [TestMethod]
    public async Task DiscoveryResumesSavedCursorAndFolderBrowserUsesProviderDelimiter()
    {
        using var server = new Server();
        server.ListOverride = body =>
        {
            if (body.TryGetProperty("delimiter", out var delimiter))
            {
                Assert.AreEqual("/", delimiter.GetString());
                return Json(new { files = new[] { new { action = "folder", fileName = "dest/folder/" } }, nextFileName = (string?)null });
            }
            Assert.AreEqual("dest/b.bin", body.GetProperty("startFileName").GetString());
            return Json(new { files = new[] { Metadata("b", "dest/b.bin", 127, PatternHash(127), new()) }, nextFileName = (string?)null });
        };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var page = await endpoint.DiscoverAsync("dest/b.bin");
        Assert.AreEqual("b.bin", page.Entries.Single().RelativePath);
        var folders = await endpoint.BrowseFoldersAsync();
        Assert.AreEqual("dest/folder/", folders.Folders.Single().Id);
        CollectionAssert.AreEqual(new[] { "dest/folder/" }, (await store.ListFoldersAsync("bucket", "dest/")).ToArray());
    }

    [TestMethod]
    public async Task ConflictsAndRenameAreDeterministicWithoutDuplicateVersions()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(127);
        await endpoint.UploadAsync(Request(), source, null, (_, _) => Task.CompletedTask);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => endpoint.UploadAsync(Request(1) with { RelativePath = Request().RelativePath }, source, null, (_, _) => Task.CompletedTask));
        await Assert.ThrowsExceptionAsync<TransferSkippedException>(() => endpoint.UploadAsync(Request(2, TransferConflictPolicy.Skip) with { RelativePath = Request().RelativePath }, source, null, (_, _) => Task.CompletedTask));
        var rename = Request(3, TransferConflictPolicy.Rename) with { RelativePath = Request().RelativePath };
        TransferCheckpoint? saved = null;
        var renamed = await endpoint.UploadAsync(rename, source, null, (value, _) => { saved = value; return Task.CompletedTask; });
        Assert.IsTrue(renamed.RelativePath.Contains(rename.OperationId[..12], StringComparison.Ordinal));
        Assert.AreEqual(renamed.Id, (await endpoint.ReconcileAsync(rename, source, saved))!.Id);
        Assert.AreEqual(2, server.Files.Count);
    }

    [TestMethod]
    public async Task MoveRefusesNewerCurrentSourceAndDeletesOnlyExactImmutableVersion()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var receipt = await endpoint.UploadAsync(Request(), new Source(127), null, (_, _) => Task.CompletedTask);
        var entry = new TransferEntry(receipt.Id, receipt.RelativePath, receipt.Id, receipt.Size, Modified, receipt.Sha1);
        server.NewerCurrentVersion = true;
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => endpoint.DeleteSourceAsync(entry));
        Assert.AreEqual(0, server.DeleteCalls);
        server.NewerCurrentVersion = false;
        await endpoint.DeleteSourceAsync(entry);
        Assert.AreEqual(1, server.DeleteCalls);
        Assert.IsTrue(await endpoint.IsSourceDeletedAsync(entry));
    }

    [TestMethod]
    public async Task ExpiredRangeAuthorizationRefreshesOnceAndUploadTokenIsRetired()
    {
        using var server = new Server { ExpireFirstUploadToken = true };
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var receipt = await endpoint.UploadAsync(Request(), new Source(127), null, (_, _) => Task.CompletedTask);
        Assert.AreEqual(2, server.UploadUrlCalls, "A rejected upload URL/token must not return to the reusable pool.");
        server.ExpireFirstDownloadAuthorization = true;
        var entry = new TransferEntry(receipt.Id, receipt.RelativePath, receipt.Id, receipt.Size, Modified, receipt.Sha1);
        await using var input = await endpoint.OpenSource(entry).OpenReadAsync(5, 20);
        var bytes = new byte[20];
        await input.ReadExactlyAsync(bytes);
        CollectionAssert.AreEqual(Enumerable.Range(5, 20).Select(value => (byte)value).ToArray(), bytes);
        Assert.AreEqual(2, server.AuthorizationCalls);
    }

    [TestMethod]
    public async Task LegacyLargeNonSeekableUploadRejectsWithoutReadingOrSpoolingPayload()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        using var source = new PatternStream(0, 5 * 1024 * 1024);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.UploadAsync("bucket", "dest/legacy.bin", source,
            source.Length, new string('a', 40), Modified));
        Assert.AreEqual(0L, source.Position);
        Assert.AreEqual(0, server.UploadCalls);
    }

    [TestMethod]
    public async Task EmptyFolderMarkerPreservesReviewedReceiptPathAndReconciles()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        var endpoint = new B2TransferEndpoint(store, Location);
        var source = new Source(0, folder: true);
        var request = Request() with { RelativePath = "empty-folder/" };
        TransferCheckpoint? saved = null;
        var receipt = await endpoint.UploadAsync(request, source, null, (value, _) => { saved = value; return Task.CompletedTask; });
        Assert.AreEqual(request.RelativePath, receipt.RelativePath);
        Assert.AreEqual("dest/empty-folder/", receipt.Data!["key"]);
        Assert.AreEqual(receipt.Id, (await endpoint.ReconcileAsync(request, source, saved))!.Id);
        var verified = await endpoint.VerifyReceiptAsync(receipt, source);
        Assert.AreEqual(PatternHash(0), verified.Sha1);
    }

    [TestMethod]
    public async Task RemovedMultipartSessionRestartsOnlyItsUnfinishedFile()
    {
        using var server = new Server();
        using var store = await ConnectAsync(server);
        store.Configure(0, 0, 1);
        var endpoint = new B2TransferEndpoint(store, Location);
        var completed = await endpoint.UploadAsync(Request(99), new Source(127), null, (_, _) => Task.CompletedTask);
        var large = new Source(70 * 1024 * 1024);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => endpoint.UploadAsync(Request(), large, null,
            (value, _) => { saved = value; if (value.AcknowledgedBytes > 0) throw new OperationCanceledException(); return Task.CompletedTask; }));
        server.RemoveUnfinishedOnNextPartsList = true;
        var receipt = await endpoint.UploadAsync(Request(), large, saved, (value, _) => { saved = value; return Task.CompletedTask; });
        Assert.AreEqual(2, server.StartCalls);
        Assert.AreEqual(2, server.Files.Count);
        Assert.IsTrue(server.Files.ContainsKey(completed.Id), "Recovery must retain an unrelated completed destination.");
        Assert.AreEqual(large.Entry.Size, receipt.Size);
        Assert.AreEqual(large.Entry.Size, saved!.AcknowledgedBytes);
    }

    private static async Task<B2CloudStore> ConnectAsync(Server server)
    {
        var store = new B2CloudStore(new Handler(server));
        await store.ConnectAsync(new("test-key", "test-secret"));
        return store;
    }
    private static async Task AssertIOExceptionAsync(Func<Task> operation)
    {
        try { await operation(); Assert.Fail("Expected an unresolved transfer outcome."); }
        catch (IOException error)
        {
            StringAssert.Contains(error.Message, "outcome could not be confirmed");
            Assert.IsFalse(error.ToString().Contains("private-token", StringComparison.Ordinal),
                "Raw response/parser failures must not escape through exception diagnostics.");
        }
    }
    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static object Metadata(string id, string key, long size, string? sha1, Dictionary<string, string> info) => new
    { accountId = "account", bucketId = "bucket", fileId = id, fileName = key, contentLength = size, contentSha1 = sha1 ?? "none",
        action = "upload", uploadTimestamp = Modified.ToUnixTimeMilliseconds(), fileInfo = info };
    private static string PatternHash(long size)
    { using var input = new PatternStream(0, size); return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant(); }

    private sealed class Source(long size, string? sha1 = null, bool folder = false) : ITransferSourceFile
    {
        public TransferEntry Entry { get; } = new("source", "source.bin", "immutable-v1", size, Modified, sha1, folder);
        public ConcurrentBag<(long Offset, long Length)> Opens { get; } = [];
        public bool Changed { get; init; }
        public Task ValidateAsync(CancellationToken cancellationToken = default) => Changed
            ? Task.FromException(new TransferSourceChangedException("Source changed.")) : Task.CompletedTask;
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        { Opens.Add((offset, length)); return Task.FromResult<Stream>(new PatternStream(offset, length)); }
    }

    private sealed class PatternStream(long offset, long length, bool corrupt = false) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            for (var i = 0; i < read; i++) buffer[start + i] = (byte)((offset + _position + i) % 251);
            if (corrupt && _position == 0 && read > 0) buffer[start] ^= 0xff;
            _position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - _position);
            for (var i = 0; i < read; i++) buffer.Span[i] = (byte)((offset + _position + i) % 251);
            if (corrupt && _position == 0 && read > 0) buffer.Span[0] ^= 0xff;
            _position += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Handler(Server server) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => server.SendAsync(request, cancellationToken); }

    private sealed class Server : IDisposable
    {
        public ConcurrentDictionary<string, object> Files { get; } = new();
        private readonly ConcurrentDictionary<string, (string Key, Dictionary<string, string> Info)> _starts = new();
        private readonly ConcurrentDictionary<int, (long Length, string Hash)> _parts = new();
        public int AuthorizationCalls, UploadUrlCalls, UploadCalls, StartCalls, FinishCalls, CancelCalls, DeleteCalls, DownloadCalls, VersionListCalls;
        public bool LoseSmallAcknowledgment, HideReceipts, CorruptPartChecksum, CorruptDownload, NewerCurrentVersion;
        public bool ExpireFirstUploadToken, ExpireFirstDownloadAuthorization;
        public bool IncludeStartsInVersionListing;
        public bool RemoveUnfinishedOnNextPartsList;
        public HttpStatusCode? RejectSmallUpload;
        public bool BreakErrorBody, MalformedErrorBody, NonObjectErrorBody, RejectOnlyOnce, MalformedSmallAcknowledgment,
            BreakSmallAcknowledgmentBody, NonObjectSmallAcknowledgment;
        public Func<JsonElement, HttpResponseMessage>? ListOverride;

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var operation = request.RequestUri!.Segments.Last().Trim('/');
            if (operation == "b2_authorize_account")
            {
                Interlocked.Increment(ref AuthorizationCalls);
                return Json(new { accountId = "account", authorizationToken = "account-token", apiInfo = new { storageApi = new
                { apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid", absoluteMinimumPartSize = 5_000_000,
                    recommendedPartSize = 5_000_000, allowed = new { buckets = (object?)null,
                        capabilities = new[] { "listBuckets", "listFiles", "writeFiles", "readFiles", "deleteFiles" }, namePrefix = (string?)null } } } });
            }
            if (request.RequestUri.Host == "upload.invalid")
            {
                Interlocked.Increment(ref UploadCalls);
                if (ExpireFirstUploadToken)
                {
                    ExpireFirstUploadToken = false;
                    var rejected = Json(new { code = "expired_auth_token", message = "Expired" }, HttpStatusCode.Unauthorized);
                    rejected.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                    return rejected;
                }
                Assert.AreEqual("hex_digits_at_end", request.Headers.GetValues("X-Bz-Content-Sha1").Single());
                var length = request.Content!.Headers.ContentLength!.Value - 40;
                using var sink = new UploadSink(length);
                await request.Content.CopyToAsync(sink, token);
                Assert.AreEqual(length + 40, sink.Length);
                Assert.AreEqual(sink.Hash, sink.Trailer);
                if (request.Headers.TryGetValues("X-Bz-Part-Number", out var number))
                {
                    var part = int.Parse(number.Single());
                    _parts[part] = (length, sink.Hash);
                    return Json(new { fileId = "large", partNumber = part, contentLength = length,
                        contentSha1 = CorruptPartChecksum ? new string('a', 40) : sink.Hash });
                }
                if (RejectSmallUpload is { } rejectedStatus)
                {
                    if (RejectOnlyOnce) RejectSmallUpload = null;
                    return BreakErrorBody
                        ? new HttpResponseMessage(rejectedStatus) { Content = new StreamContent(new BrokenErrorStream()) }
                        : NonObjectErrorBody ? Json(new[] { "private-token" }, rejectedStatus)
                        : MalformedErrorBody ? MalformedResponse(rejectedStatus)
                        : Json(new { code = "service_unavailable", message = "Generated provider rejection." }, rejectedStatus);
                }
                var key = Uri.UnescapeDataString(request.Headers.GetValues("X-Bz-File-Name").Single());
                var info = request.Headers.Where(h => h.Key.StartsWith("X-Bz-Info-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(h => h.Key[10..].ToLowerInvariant(), h => Uri.UnescapeDataString(h.Value.Single()));
                var id = "small-" + UploadCalls;
                var file = Metadata(id, key, length, sink.Hash, info);
                Files[id] = file;
                if (LoseSmallAcknowledgment) throw new HttpRequestException("Lost acknowledgment.");
                if (BreakSmallAcknowledgmentBody) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenErrorStream()) };
                if (NonObjectSmallAcknowledgment) return Json(new[] { "private-token" });
                if (MalformedSmallAcknowledgment) return MalformedResponse(HttpStatusCode.OK);
                return Json(file);
            }
            if (operation == "b2_download_file_by_id")
            {
                Interlocked.Increment(ref DownloadCalls);
                if (ExpireFirstDownloadAuthorization)
                {
                    ExpireFirstDownloadAuthorization = false;
                    return Json(new { code = "expired_auth_token", message = "Expired" }, HttpStatusCode.Unauthorized);
                }
                var id = Uri.UnescapeDataString(request.RequestUri.Query.Split('=')[1]);
                var file = JsonSerializer.SerializeToElement(Files[id]);
                var size = file.GetProperty("contentLength").GetInt64();
                var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
                var length = (request.Headers.Range?.Ranges.Single().To ?? size - 1) - offset + 1;
                var response = new HttpResponseMessage(request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new StreamContent(new PatternStream(offset, length, CorruptDownload)) };
                response.Content.Headers.ContentLength = length;
                if (request.Headers.Range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, size);
                response.Headers.TryAddWithoutValidation("X-Bz-File-Id", id);
                response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", file.GetProperty("contentSha1").GetString());
                return response;
            }
            using var bodyDocument = request.Content is null ? JsonDocument.Parse("{}") : JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var body = bodyDocument.RootElement;
            switch (operation)
            {
                case "b2_get_upload_url": Interlocked.Increment(ref UploadUrlCalls); return Json(new { uploadUrl = "https://upload.invalid/small", authorizationToken = "upload-token" });
                case "b2_get_upload_part_url": return Json(new { uploadUrl = "https://upload.invalid/large", authorizationToken = "part-token" });
                case "b2_list_file_names":
                    if (ListOverride is not null) return ListOverride(body);
                    var prefix = body.GetProperty("prefix").GetString()!;
                    var listed = Files.Values.Select(f => JsonSerializer.SerializeToElement(f)).Where(f => f.GetProperty("fileName").GetString()!.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    if (NewerCurrentVersion && listed.Length > 0)
                        listed = [JsonSerializer.SerializeToElement(Metadata("newer", listed[0].GetProperty("fileName").GetString()!, 127, PatternHash(127), new()))];
                    return Json(new { files = listed.Take(body.GetProperty("maxFileCount").GetInt32()).ToArray(), nextFileName = (string?)null });
                case "b2_list_file_versions":
                    Interlocked.Increment(ref VersionListCalls);
                    var starts = IncludeStartsInVersionListing ? _starts.Select(s => (object)new { accountId = "account", bucketId = "bucket", fileId = s.Key,
                        fileName = s.Value.Key, action = "start", contentLength = 0, contentSha1 = "none", uploadTimestamp = 0, fileInfo = s.Value.Info }).ToArray() : [];
                    return Json(new { files = HideReceipts ? [] : Files.Values.Concat(starts).ToArray(), nextFileName = (string?)null, nextFileId = (string?)null });
                case "b2_get_file_info":
                    return Files.TryGetValue(body.GetProperty("fileId").GetString()!, out var infoFile) ? Json(infoFile) : Json(new { code = "file_not_present", message = "Missing" }, HttpStatusCode.NotFound);
                case "b2_start_large_file":
                    Interlocked.Increment(ref StartCalls);
                    _starts["large"] = (body.GetProperty("fileName").GetString()!, JsonSerializer.Deserialize<Dictionary<string, string>>(body.GetProperty("fileInfo"))!);
                    return Json(new { fileId = "large" });
                case "b2_list_unfinished_large_files":
                    return Json(new { files = _starts.Select(s => new { fileId = s.Key, bucketId = "bucket", fileName = s.Value.Key, action = "start", fileInfo = s.Value.Info }).ToArray(), nextFileId = (string?)null });
                case "b2_list_parts":
                    if (RemoveUnfinishedOnNextPartsList)
                    {
                        RemoveUnfinishedOnNextPartsList = false; _starts.Clear(); _parts.Clear();
                        return Json(new { code = "file_not_present", message = "The unfinished file was removed." }, HttpStatusCode.NotFound);
                    }
                    return Json(new { parts = _parts.OrderBy(p => p.Key).Select(p => new { fileId = "large", partNumber = p.Key, contentLength = p.Value.Length, contentSha1 = p.Value.Hash }).ToArray(), nextPartNumber = (int?)null });
                case "b2_finish_large_file":
                    Interlocked.Increment(ref FinishCalls);
                    var start = _starts["large"];
                    var completed = Metadata("large", start.Key, _parts.Sum(p => p.Value.Length), null, start.Info);
                    Files["large"] = completed; _starts.TryRemove("large", out _); return Json(completed);
                case "b2_cancel_large_file": Interlocked.Increment(ref CancelCalls); return Json(new { fileId = "large" });
                case "b2_delete_file_version":
                    Interlocked.Increment(ref DeleteCalls);
                    var deleteId = body.GetProperty("fileId").GetString()!;
                    Files.TryRemove(deleteId, out _); return Json(new { fileId = deleteId, fileName = body.GetProperty("fileName").GetString() });
                default: throw new AssertFailedException("Unexpected B2 operation: " + operation);
            }
        }
        public void Dispose() { }
        private static HttpResponseMessage MalformedResponse(HttpStatusCode status) => new(status)
        { Content = new StringContent("{\"credential\":\"private-token\",\"message\":", Encoding.UTF8, "application/json") };
    }

    private sealed class BrokenErrorStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Generated truncated error body: private-token must never be retained."));
    }

    private sealed class UploadSink(long payloadLength) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        private long _written;
        private readonly StringBuilder _trailer = new();
        public string Hash { get; private set; } = "";
        public string Trailer => _trailer.ToString();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var payload = (int)Math.Min(buffer.Length, Math.Max(0, payloadLength - _written));
            if (payload > 0) _hash.AppendData(buffer.Span[..payload]);
            if (_written + payload == payloadLength && Hash.Length == 0)
                Hash = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            if (payload < buffer.Length) _trailer.Append(Encoding.ASCII.GetString(buffer.Span[payload..]));
            _written += buffer.Length;
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

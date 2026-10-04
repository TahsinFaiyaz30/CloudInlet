using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.B2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class B2CopyTests
{
    private const string Destination = "CloudBay/Imports/example/file.bin";
    private static readonly string OperationId = new('f', 64);

    [DataTestMethod]
    [DataRow(4_999_999_999L, false)]
    [DataRow(5_000_000_000L, true)]
    public async Task CopyUsesDocumentedFiveGbBoundaryPreservesMetadataAndVerifiesImmutableResult(long size, bool multipart)
    {
        using var fixture = new Fixture(size);
        using var store = await fixture.ConnectAsync();
        var copied = await store.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual(Destination, copied.Key); Assert.AreEqual(size, copied.Size);
        Assert.AreEqual(fixture.Source.Sha1, copied.Sha1);
        Assert.AreEqual(fixture.Source.ModifiedUtc, copied.ModifiedUtc);
        Assert.AreEqual(multipart ? 1 : 0, fixture.Starts);
        Assert.AreEqual(multipart ? 0 : 1, fixture.Copies);
        Assert.AreEqual(multipart ? 5 : 0, fixture.CopiedParts.Count);
        Assert.AreEqual(1, fixture.Versions.Count);
        Assert.IsTrue(fixture.Verifications >= 1, "Copy acknowledgment must be independently checked by immutable ID.");
        var receipt = fixture.Versions.Single().Value;
        Assert.AreEqual("keep original metadata", receipt.Info["custom"]);
        Assert.AreEqual(OperationId, receipt.Info["cloudbay_import_id"]);
        Assert.AreEqual(fixture.Source.FileId, receipt.Info["cloudbay_source_id"]);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task CopyCancelledBeforeRequestAdmissionCanRestartWithoutUnknownOutcome()
    {
        using var fixture = new Fixture(31);
        using var store = await fixture.ConnectAsync();
        // Hold the request budget independently of the file budget, as multipart
        // workers do. This isolates cancellation before any creating HTTP call.
        var gate = typeof(B2CloudStore).GetField("_uploadRequests",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var enter = gate.GetType().GetMethod("EnterAsync")!;
        var exit = gate.GetType().GetMethod("Exit")!;
        for (var i = 0; i < 4; i++) await (Task)enter.Invoke(gate, [CancellationToken.None])!;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await store.CopyToAsync("destination", Destination, fixture.Source, OperationId, cancellation.Token);
                Assert.Fail("A queued copy must observe cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.AreEqual(1, fixture.SourceReads);
            Assert.AreEqual(1, fixture.ReceiptListings);
            Assert.AreEqual(0, fixture.Copies);
            Assert.AreEqual(0, fixture.Journals.Length, "No creating call was admitted, so no uncertain receipt may block resume.");
        }
        finally { for (var i = 0; i < 4; i++) exit.Invoke(gate, null); }
        using var restarted = await fixture.ConnectAsync();
        var copied = await restarted.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual(fixture.Source.Size, copied.Size);
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Versions.Count);
    }

    [TestMethod]
    public async Task LostCopySuccessResponseReconcilesReceiptWithoutCreatingAnotherVersion()
    {
        using var fixture = new Fixture(1_023) { LoseCopyResponse = true };
        using var store = await fixture.ConnectAsync();
        var copied = await store.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Versions.Count);
        fixture.LoseCopyResponse = false;
        var original = await store.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual(copied.FileId, original.FileId);
        Assert.AreEqual(1, fixture.Copies, "Replaying a completed operation must return its original receipt.");
    }

    [TestMethod]
    public async Task LostSmallRestoreSuccessDoesNotBlindlyCreateAnotherVersion()
    {
        using var fixture = new Fixture(23) { LoseCopyResponse = true, IsRestore = true };
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => store.RestoreAsync("destination", fixture.Source));
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Versions.Count);
    }

    [TestMethod]
    public async Task TemporarilyAbsentAmbiguousCopyReceiptIsRetainedAcrossRestartAndNeverBlindlyReposted()
    {
        using var fixture = new Fixture(129) { LoseCopyResponse = true, HideReceipts = true };
        using (var store = await fixture.ConnectAsync())
            await ExpectUnknownOutcomeAsync(() => store.CopyToAsync("destination", Destination, fixture.Source, OperationId));
        Assert.AreEqual(1, fixture.Journals.Length);
        using (var restart = await fixture.ConnectAsync())
            await ExpectUnknownOutcomeAsync(() => restart.CopyToAsync("destination", Destination, fixture.Source, OperationId));
        Assert.AreEqual(1, fixture.Copies, "An absent listing receipt is not proof that the creating request failed.");
        fixture.HideReceipts = false;
        using var recovered = await fixture.ConnectAsync();
        var result = await recovered.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual(fixture.Versions.Single().Key, result.FileId);
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task LostMultipartStartResponseRecoversItsOwnedSourceIntentWithoutStartingTwice()
    {
        using var fixture = new Fixture(5_000_000_000) { LoseStartResponse = true };
        using var store = await fixture.ConnectAsync();
        await store.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual(1, fixture.Starts);
        Assert.AreEqual(1, fixture.Finishes);
        Assert.AreEqual(1, fixture.Versions.Count);
    }

    [TestMethod]
    public async Task InterruptedMultipartCopyResumesOnlyMissingPartsAcrossRestart()
    {
        using var fixture = new Fixture(5_000_000_017);
        using var cancellation = new CancellationTokenSource(); fixture.Interrupt = cancellation;
        using (var store = await fixture.ConnectAsync())
        {
            store.Configure(0, 0, 1);
            try { await store.CopyToAsync("destination", Destination, fixture.Source, OperationId, cancellation.Token);
                Assert.Fail("An interrupted multipart copy must not report completion."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Assert.AreEqual(1, fixture.Starts); Assert.AreEqual(0, fixture.Finishes);
        Assert.AreEqual(1, fixture.Journals.Length);
        Assert.AreEqual(1, fixture.CopiedParts.Count);
        fixture.Interrupt = null; fixture.CopiedParts.Clear();
        using var resumed = await fixture.ConnectAsync(); resumed.Configure(0, 0, 1);
        await resumed.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, fixture.CopiedParts.ToArray());
        Assert.AreEqual(1, fixture.Starts); Assert.AreEqual(1, fixture.Finishes);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [DataTestMethod]
    [DataRow("key")]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("bucket")]
    public async Task WrongCopyAcknowledgmentNeverPassesAsVerified(string mismatch)
    {
        using var fixture = new Fixture(137) { BadAcknowledgment = mismatch };
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CopyToAsync("destination", Destination, fixture.Source, OperationId));
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Journals.Length, "An invalid acknowledgment must not authorize a second creating request.");
    }

    [TestMethod]
    public async Task ReadOnlyCompletedReceiptCheckRejectsDifferentOperationWithoutCreatingAnything()
    {
        using var fixture = new Fixture(37);
        using var store = await fixture.ConnectAsync();
        var copied = await store.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        await store.VerifyCopyAsync("destination", Destination, fixture.Source, OperationId, copied);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            store.VerifyCopyAsync("destination", Destination, fixture.Source, new string('d', 64), copied));
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Versions.Count);
    }

    [DataTestMethod]
    [DataRow("operation")]
    [DataRow("source")]
    [DataRow("modified")]
    public async Task IndependentCopiedVersionReadMustMatchOperationSourceAndOriginalTimestamp(string mismatch)
    {
        using var fixture = new Fixture(91) { BadReceipt = mismatch };
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CopyToAsync("destination", Destination, fixture.Source, OperationId));
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Versions.Count);
        Assert.AreEqual(1, fixture.Journals.Length, "A failed independent receipt check must retain the original creating intent.");
    }

    [TestMethod]
    public async Task ConcurrentAttemptsForOneOperationReturnOneImmutableVersion()
    {
        using var fixture = new Fixture(37);
        using var store = await fixture.ConnectAsync();
        var copies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            store.CopyToAsync("destination", Destination, fixture.Source, OperationId)));
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, copies.Select(copy => copy.FileId).Distinct().Count());
        Assert.AreEqual(1, fixture.Versions.Count);
    }

    [DataTestMethod]
    [DataRow(503)]
    [DataRow(403)]
    public async Task CopySuccessFollowedByFailedIndependentReadRetainsCreatingIntentAcrossRestart(int status)
    {
        using var fixture = new Fixture(79) { VerificationFailureStatus = status };
        using (var store = await fixture.ConnectAsync())
        {
            try { await store.CopyToAsync("destination", Destination, fixture.Source, OperationId);
                Assert.Fail("An unverified copy must retain its intent and report the metadata read failure."); }
            catch (IOException) { }
        }
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Journals.Length);
        var journal = File.ReadAllText(fixture.Journals.Single());
        StringAssert.Contains(journal, "\"RequestPending\":true");
        fixture.VerificationFailureStatus = 0;
        using var restarted = await fixture.ConnectAsync();
        var original = await restarted.CopyToAsync("destination", Destination, fixture.Source, OperationId);
        Assert.AreEqual("copied", original.FileId);
        Assert.AreEqual(1, fixture.Copies);
        Assert.AreEqual(1, fixture.Versions.Count);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task RestrictedPrefixAndActualSourceBucketAreCheckedBeforeAnyCreatingRequest()
    {
        using var fixture = new Fixture(31) { AllowedPrefix = "CloudBay/", SourceBucket = "forbidden", AllowedBuckets = ["destination", "source"] };
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() =>
            store.CopyToAsync("destination", "Outside/file.bin", fixture.Source, OperationId));
        Assert.AreEqual(0, fixture.SourceReads, "Invalid destination prefix must fail before requesting cloud metadata.");
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() =>
            store.CopyToAsync("destination", Destination, fixture.Source, OperationId));
        Assert.AreEqual(1, fixture.SourceReads);
        Assert.AreEqual(0, fixture.Copies); Assert.AreEqual(0, fixture.Starts);
    }

    [TestMethod]
    public async Task HealthySmallUploadsKeepPooledEndpointWithoutReceiptListingOrDurableIntentWrites()
    {
        using var fixture = new Fixture(11);
        using var store = await fixture.ConnectAsync();
        var bytes = Encoding.UTF8.GetBytes("small bytes");
        var sha = Convert.ToHexString(SHA1.HashData(bytes));
        for (var i = 0; i < 8; i++)
        {
            using var source = new MemoryStream(bytes);
            await store.UploadAsync("destination", "CloudBay/small-" + i, source, bytes.Length, sha, fixture.Source.ModifiedUtc);
        }
        Assert.AreEqual(8, fixture.Uploads); Assert.AreEqual(1, fixture.UploadTargets);
        Assert.AreEqual(0, fixture.ReceiptListings);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task SmallUploadLostSuccessResponseReturnsOnlyOriginalVerifiedVersion()
    {
        using var fixture = new Fixture(11) { LoseUploadResponse = true };
        using var store = await fixture.ConnectAsync();
        var bytes = Encoding.UTF8.GetBytes("small bytes"); using var source = new MemoryStream(bytes);
        var copied = await store.UploadAsync("destination", "CloudBay/small", source, bytes.Length,
            Convert.ToHexString(SHA1.HashData(bytes)), fixture.Source.ModifiedUtc);
        Assert.AreEqual(1, fixture.Uploads); Assert.AreEqual(1, fixture.Versions.Count);
        Assert.AreEqual(fixture.Versions.Single().Key, copied.FileId);
        Assert.AreEqual(bytes.Length, source.Position);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task FullySentUploadReturning503AfterCommitConfirmsReceiptWithoutAnotherPost()
    {
        using var fixture = new Fixture(11) { UploadResponse503 = true };
        using var store = await fixture.ConnectAsync();
        var bytes = Encoding.UTF8.GetBytes("small bytes"); using var source = new MemoryStream(bytes);
        var result = await store.UploadAsync("destination", "CloudBay/small", source, bytes.Length,
            Convert.ToHexString(SHA1.HashData(bytes)), fixture.Source.ModifiedUtc);
        Assert.AreEqual(1, fixture.Uploads);
        Assert.AreEqual(1, fixture.Versions.Count);
        Assert.AreEqual(fixture.Versions.Single().Key, result.FileId);
    }

    [TestMethod]
    public async Task SmallUploadAbsentAmbiguousReceiptSurvivesRestartAndNeverCreatesDuplicate()
    {
        using var fixture = new Fixture(11) { LoseUploadResponse = true, HideReceipts = true };
        var bytes = Encoding.UTF8.GetBytes("small bytes"); var sha = Convert.ToHexString(SHA1.HashData(bytes));
        using (var store = await fixture.ConnectAsync())
        {
            using var source = new MemoryStream(bytes);
            await ExpectUnknownOutcomeAsync(() => store.UploadAsync("destination", "CloudBay/small", source, bytes.Length, sha, fixture.Source.ModifiedUtc));
        }
        Assert.AreEqual(1, fixture.Uploads); Assert.AreEqual(1, fixture.Journals.Length);
        using (var restart = await fixture.ConnectAsync())
        {
            using var source = new MemoryStream(bytes);
            await ExpectUnknownOutcomeAsync(() => restart.UploadAsync("destination", "CloudBay/small", source, bytes.Length, sha, fixture.Source.ModifiedUtc));
        }
        Assert.AreEqual(1, fixture.Uploads);
        fixture.HideReceipts = false;
        using var confirmed = await fixture.ConnectAsync(); using var retry = new MemoryStream(bytes);
        await confirmed.UploadAsync("destination", "CloudBay/small", retry, bytes.Length, sha, fixture.Source.ModifiedUtc);
        Assert.AreEqual(1, fixture.Uploads); Assert.AreEqual(1, fixture.Versions.Count);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task PartialSmallUploadCancellationDoesNotLeaveAnUnconfirmableIntentAndCanResumeAfterRestart()
    {
        using var fixture = new Fixture(11);
        using var cancellation = new CancellationTokenSource(); fixture.PartialUploadCancellation = cancellation;
        var bytes = RandomNumberGenerator.GetBytes(128 * 1024 + 11);
        var sha = Convert.ToHexString(SHA1.HashData(bytes));
        using (var store = await fixture.ConnectAsync())
        {
            using var source = new MemoryStream(bytes);
            try
            {
                await store.UploadAsync("destination", "CloudBay/cancelled", source, bytes.Length, sha,
                    fixture.Source.ModifiedUtc, cancellationToken: cancellation.Token);
                Assert.Fail("A cancelled partial request cannot complete.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Assert.AreEqual(64 * 1024, fixture.PartialUploadBytes, "The first payload block must be sent before cancellation.");
        Assert.AreEqual(0, fixture.Journals.Length, "An incomplete body cannot create a B2 version and must not leave a permanent unknown receipt.");
        Assert.AreEqual(0, fixture.Versions.Count);
        fixture.PartialUploadCancellation = null;
        using var restarted = await fixture.ConnectAsync(); using var retry = new MemoryStream(bytes);
        var uploaded = await restarted.UploadAsync("destination", "CloudBay/cancelled", retry, bytes.Length, sha, fixture.Source.ModifiedUtc);
        Assert.AreEqual(bytes.Length, uploaded.Size);
        Assert.AreEqual(1, fixture.Uploads);
        Assert.AreEqual(1, fixture.Versions.Count);
        Assert.AreEqual(0, fixture.ReceiptListings, "An incomplete cancelled attempt must resume directly, without waiting for a nonexistent receipt.");
    }

    [TestMethod]
    public async Task FullySentSmallUploadCancellationRetainsReceiptAndResumesOriginalVersionAfterRestart()
    {
        using var fixture = new Fixture(11);
        using var cancellation = new CancellationTokenSource(); fixture.CompletedUploadCancellation = cancellation;
        var bytes = Encoding.UTF8.GetBytes("small bytes"); var sha = Convert.ToHexString(SHA1.HashData(bytes));
        using (var store = await fixture.ConnectAsync())
        {
            using var source = new MemoryStream(bytes);
            try
            {
                await store.UploadAsync("destination", "CloudBay/cancelled", source, bytes.Length, sha,
                    fixture.Source.ModifiedUtc, cancellationToken: cancellation.Token);
                Assert.Fail("A caller cancellation cannot acknowledge a fully sent request.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Assert.AreEqual(1, fixture.Journals.Length);
        Assert.AreEqual(1, fixture.Versions.Count);
        fixture.CompletedUploadCancellation = null;
        using var restarted = await fixture.ConnectAsync(); using var retry = new MemoryStream(bytes);
        var original = await restarted.UploadAsync("destination", "CloudBay/cancelled", retry, bytes.Length, sha, fixture.Source.ModifiedUtc);
        Assert.AreEqual(fixture.Versions.Single().Key, original.FileId);
        Assert.AreEqual(1, fixture.Uploads, "A cancelled acknowledgment after commit must never create another version.");
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    private sealed record Version(string Id, string Key, string Bucket, long Length, string Sha, Dictionary<string, string> Info);
    private sealed class Fixture(long length) : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudBay.CopyTransport.Tests", Guid.NewGuid().ToString("N"));
        public CloudObject Source { get; } = new("source-version", "CloudBay/source.bin", length, new string('a', 40), DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        public string SourceBucket = "source";
        public string? AllowedPrefix; public string[]? AllowedBuckets;
        public bool LoseCopyResponse, LoseStartResponse, LoseUploadResponse, HideReceipts, UploadResponse503, IsRestore;
        public string? BadAcknowledgment, BadReceipt;
        public int Copies, Starts, Finishes, Uploads, UploadTargets, SourceReads, Verifications, ReceiptListings;
        public int VerificationFailureStatus;
        public CancellationTokenSource? Interrupt;
        public CancellationTokenSource? PartialUploadCancellation, CompletedUploadCancellation;
        public long PartialUploadBytes;
        public ConcurrentDictionary<string, Version> Versions { get; } = new();
        public ConcurrentQueue<int> CopiedParts { get; } = new();
        private Version? _unfinished;
        private readonly ConcurrentDictionary<int, (long Length, string Sha)> _parts = new();
        public string[] Journals => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories) : [];
        public async Task<B2CloudStore> ConnectAsync()
        {
            var store = new B2CloudStore(new Handler(SendAsync));
            store.ConfigureResumableUploads(_directory);
            await store.ConnectAsync(new("fixture-key", "fixture-secret")); return store;
        }
        private object Object(Version version, string action = "upload") => new
        { accountId = "account", bucketId = version.Bucket, fileId = version.Id, fileName = version.Key,
            contentLength = version.Length, contentSha1 = version.Sha, action, uploadTimestamp = 0, fileInfo = version.Info,
            contentType = "application/example" };
        private Dictionary<string, string> Metadata(JsonElement body) => body.GetProperty("fileInfo").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var operation = request.RequestUri!.Segments[^1];
            if (operation == "b2_authorize_account") return Json(new
            {
                accountId = "account", authorizationToken = "fixture-token",
                apiInfo = new { storageApi = new { apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid",
                    recommendedPartSize = 1_000_000_000L, absoluteMinimumPartSize = 5_000_000L,
                    allowed = new { capabilities = new[] { "readFiles", "writeFiles", "listFiles", "listBuckets" }, namePrefix = AllowedPrefix,
                        buckets = AllowedBuckets?.Select(id => new { id, name = id }).ToArray() } } }
            });
            if (request.RequestUri.Host == "upload.invalid")
            {
                if (PartialUploadCancellation is { } partialCancellation)
                {
                    using var interruptedBody = new PartialUploadSink(partialCancellation, count => PartialUploadBytes = count);
                    await request.Content!.CopyToAsync(interruptedBody, token);
                    Assert.Fail("Partial fixture cancellation must interrupt body serialization.");
                }
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                var info = new Dictionary<string, string>
                {
                    ["cloudbay_upload_id"] = request.Headers.GetValues("X-Bz-Info-cloudbay_upload_id").Single(),
                    ["cloudbay_source_id"] = request.Headers.GetValues("X-Bz-Info-cloudbay_source_id").Single(),
                    ["src_last_modified_millis"] = request.Headers.GetValues("X-Bz-Info-src_last_modified_millis").Single()
                };
                var version = new Version("uploaded-" + Interlocked.Increment(ref Uploads),
                    Uri.UnescapeDataString(request.Headers.GetValues("X-Bz-File-Name").Single()), "destination", bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), info);
                Versions[version.Id] = version;
                if (CompletedUploadCancellation is { } fullCancellation)
                { fullCancellation.Cancel(); throw new OperationCanceledException(token); }
                if (LoseUploadResponse) throw new HttpRequestException("Generated response lost after storing upload.");
                if (UploadResponse503) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{\"code\":\"service_unavailable\"}") };
                return Json(Object(version));
            }
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var body = payload.RootElement;
            switch (operation)
            {
                case "b2_get_upload_url":
                    Interlocked.Increment(ref UploadTargets);
                    return Json(new { uploadUrl = "https://upload.invalid/pooled", authorizationToken = "upload-token" });
                case "b2_get_file_info":
                    var id = body.GetProperty("fileId").GetString()!;
                    if (id == Source.FileId)
                    {
                        Interlocked.Increment(ref SourceReads);
                        return Json(Object(new(Source.FileId, Source.Key, SourceBucket, Source.Size, Source.Sha1!,
                            new() { ["custom"] = "keep original metadata", ["src_last_modified_millis"] = Source.ModifiedUtc.ToUnixTimeMilliseconds().ToString() })));
                    }
                    Interlocked.Increment(ref Verifications);
                    if (VerificationFailureStatus != 0)
                    {
                        var failure = new HttpResponseMessage((HttpStatusCode)VerificationFailureStatus)
                            { Content = new StringContent("{\"code\":\"service_unavailable\"}") };
                        failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                        return failure;
                    }
                    return Json(Object(Versions[id]));
                case "b2_list_file_versions":
                    Interlocked.Increment(ref ReceiptListings);
                    var prefix = body.GetProperty("prefix").GetString()!;
                    return Json(new { files = HideReceipts ? [] : Versions.Values.Where(v => v.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(v => Object(v)).ToArray(),
                        nextFileName = (string?)null, nextFileId = (string?)null });
                case "b2_copy_file":
                    Interlocked.Increment(ref Copies);
                    Assert.AreEqual(Source.FileId, body.GetProperty("sourceFileId").GetString());
                    Assert.AreEqual(IsRestore ? "COPY" : "REPLACE", body.GetProperty("metadataDirective").GetString());
                    var copied = new Version("copied", body.GetProperty("fileName").GetString()!, body.GetProperty("destinationBucketId").GetString()!, Source.Size, Source.Sha1!,
                        IsRestore ? new() { ["src_last_modified_millis"] = Source.ModifiedUtc.ToUnixTimeMilliseconds().ToString() } : Metadata(body));
                    if (BadReceipt is { } badReceipt)
                        copied.Info[badReceipt == "operation" ? "cloudbay_import_id" : badReceipt == "source" ? "cloudbay_source_id" : "src_last_modified_millis"] =
                            badReceipt == "modified" ? "123" : "different";
                    Versions[copied.Id] = copied;
                    if (LoseCopyResponse) throw new HttpRequestException("Generated copy response lost after commit.");
                    if (BadAcknowledgment is { } mismatch)
                        copied = copied with { Key = mismatch == "key" ? "different" : copied.Key,
                            Bucket = mismatch == "bucket" ? "different" : copied.Bucket,
                            Length = mismatch == "length" ? copied.Length + 1 : copied.Length,
                            Sha = mismatch == "hash" ? new string('b', 40) : copied.Sha };
                    return Json(Object(copied, "copy"));
                case "b2_start_large_file":
                    Interlocked.Increment(ref Starts);
                    _unfinished = new("large-copy", body.GetProperty("fileName").GetString()!, body.GetProperty("bucketId").GetString()!, 0, "none", Metadata(body));
                    if (LoseStartResponse) throw new HttpRequestException("Generated multipart start response lost.");
                    return Json(Object(_unfinished, "start"));
                case "b2_list_unfinished_large_files":
                    return Json(new { files = _unfinished is null ? [] : new[] { Object(_unfinished, "start") }, nextFileId = (string?)null });
                case "b2_list_parts":
                    return Json(new { parts = _parts.OrderBy(p => p.Key).Select(p => new
                        { fileId = "large-copy", partNumber = p.Key, contentLength = p.Value.Length, contentSha1 = p.Value.Sha }).ToArray(), nextPartNumber = (int?)null });
                case "b2_copy_part":
                    var number = body.GetProperty("partNumber").GetInt32(); var range = body.GetProperty("range").GetString()![6..].Split('-');
                    var from = long.Parse(range[0]); var to = long.Parse(range[1]);
                    Assert.AreEqual(Source.FileId, body.GetProperty("sourceFileId").GetString());
                    Assert.AreEqual((number - 1) * 1_000_000_000L, from);
                    Assert.AreEqual(Math.Min(Source.Size, from + 1_000_000_000L) - 1, to);
                    var hash = number.ToString("x40"); _parts[number] = (to - from + 1, hash); CopiedParts.Enqueue(number);
                    if (Interrupt is { } interrupt) { interrupt.Cancel(); throw new OperationCanceledException(token); }
                    return Json(new { fileId = "large-copy", partNumber = number, contentLength = to - from + 1, contentSha1 = hash });
                case "b2_finish_large_file":
                    Interlocked.Increment(ref Finishes);
                    CollectionAssert.AreEqual(_parts.OrderBy(p => p.Key).Select(p => p.Value.Sha).ToArray(),
                        body.GetProperty("partSha1Array").EnumerateArray().Select(p => p.GetString()).ToArray());
                    var finished = _unfinished! with { Length = Source.Size, Sha = Source.Sha1! };
                    Versions[finished.Id] = finished; _unfinished = null; return Json(Object(finished));
                default: throw new AssertFailedException("Unexpected B2 operation: " + operation);
            }
        }
        public void Dispose()
        { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class PartialUploadSink(CancellationTokenSource cancellation, Action<long> received) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer, token);
            received(Length);
            cancellation.Cancel();
            // Return this successfully written block. SegmentContent publishes its sent
            // counter before the next read observes the caller's cancelled token.
        }
    }
    private static async Task ExpectUnknownOutcomeAsync(Func<Task> operation)
    {
        try { await operation(); Assert.Fail("An unconfirmed creating request must not be silently replayed."); }
        catch (IOException error) { StringAssert.Contains(error.Message, "outcome could not be confirmed"); }
    }
}

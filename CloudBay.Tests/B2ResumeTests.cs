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
public sealed class B2ResumeTests
{
    [TestMethod]
    public async Task ParallelMultipartRetryPublishesOrderedBaselinesWithoutLosingTransferredPayload()
    {
        using var fixture = new Fixture { RetryFirstPartOnce = true };
        using var store = await fixture.ConnectAsync();
        store.Configure(0, 0, 3);
        await using var source = fixture.Open();
        var progress = new SerializedProgress();
        await fixture.UploadAsync(store, source, progress);
        Assert.IsFalse(progress.ConcurrentCallback,
            "Parallel part workers must publish their shared progress counter in the same order that it changes.");
        long previous = 0, payload = 0;
        var baselines = 0;
        foreach (var value in progress.Values)
        {
            if (value.IsBaseline) baselines++;
            else
            {
                Assert.IsTrue(value.Bytes >= previous, "A decreasing counter must be explicitly marked as a retry baseline.");
                payload += value.Bytes - previous;
            }
            previous = value.Bytes;
        }
        Assert.IsTrue(baselines >= 2, "The interrupted part must publish its rollback baseline before its retry writes.");
        Assert.AreEqual(Fixture.Length + fixture.RecommendedPartSize, fixture.ReceivedPayloadBytes);
        Assert.AreEqual(fixture.ReceivedPayloadBytes, payload,
            "Measured progress must count all sent payload, including the retried part, without inventing traffic from baseline resets.");
        Assert.AreEqual(Fixture.Length, previous);
        Assert.AreEqual(1, fixture.Finishes);
    }

    [TestMethod]
    public async Task PreparedMultipartChecksumsUseOneDiskPassBeforeNetworkTransfer()
    {
        using var fixture = new Fixture { ValidateReceivedHashes = true };
        using var store = await fixture.ConnectAsync();
        await using var source = new CountingFileStream(fixture.SourcePath);
        var hash = await store.PrepareUploadChecksumAsync("bucket", Fixture.Key, source, Fixture.Length, Fixture.Modified);
        Assert.AreEqual(fixture.Sha1, hash);
        Assert.AreEqual(0L, source.Position, "Preparing must restore the upload stream position.");
        Assert.AreEqual(Fixture.Length, source.BytesRead);
        await fixture.UploadAsync(store, source);
        Assert.AreEqual(2 * Fixture.Length, source.BytesRead,
            "One checksum pass plus one network-read pass should cover an unchanged prepared file.");
    }

    [TestMethod]
    public async Task SourceMutationAfterPreparationIsRejectedBeforeMultipartAcknowledgment()
    {
        using var fixture = new Fixture { ValidateReceivedHashes = true };
        using var store = await fixture.ConnectAsync();
        // Deliberately simulate a caller that permits writers; the production engine denies them.
        await using var source = new CountingFileStream(fixture.SourcePath, FileShare.ReadWrite);
        await store.PrepareUploadChecksumAsync("bucket", Fixture.Key, source, Fixture.Length, Fixture.Modified);
        await using (var changed = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            changed.WriteByte(42);
        System.IO.File.SetLastWriteTimeUtc(fixture.SourcePath, Fixture.Modified.UtcDateTime);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.UploadAsync(store, source));
        Assert.AreEqual(0, fixture.UploadedParts.Count, "Changed source bytes must fail before any part is acknowledged.");
        Assert.AreEqual(0, fixture.Finishes);
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task ChangedAuthorizationPartLayoutInvalidatesPreparedHashes()
    {
        using var fixture = new Fixture();
        using var store = await fixture.ConnectAsync();
        await using var source = new CountingFileStream(fixture.SourcePath);
        await store.PrepareUploadChecksumAsync("bucket", Fixture.Key, source, Fixture.Length, Fixture.Modified);
        fixture.RecommendedPartSize = 50_000_000;
        await store.ConnectAsync(new("application-id", "secret-application-key"));
        await fixture.UploadAsync(store, source);
        Assert.AreEqual(5, fixture.UploadedParts.Count);
        Assert.AreEqual(3 * Fixture.Length, source.BytesRead,
            "A changed layout must recompute part hashes before uploading.");
    }

    [TestMethod]
    public async Task RestartReconcilesConfirmedAndLostAcknowledgmentPartsWithoutResendingThem()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 2;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        Assert.AreEqual(1, fixture.Journals.Length);
        Assert.AreEqual(0, fixture.Cancellations);
        fixture.Interrupt = null;
        fixture.UploadedParts.Clear();
        var progress = new CapturedProgress();
        await fixture.CompleteAsync(progress);
        CollectionAssert.AreEqual(new[] { 3 }, fixture.UploadedParts.ToArray());
        Assert.AreEqual(1, fixture.Starts, "Restart must reuse the existing B2 large file.");
        Assert.IsTrue(progress.Values.Any(p => p.Bytes == 200_000_000 && p.TotalBytes == Fixture.Length), "Already stored bytes must be visible before sending the final part.");
        Assert.AreEqual(0, fixture.Journals.Length);
        Assert.AreEqual(0, fixture.Cancellations);
    }

    [TestMethod]
    public async Task LostStartAcknowledgmentRecoversOnlyMatchingPersistedUploadIntent()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptStart = true;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        Assert.AreEqual(1, fixture.Journals.Length);
        var checkpoint = System.IO.File.ReadAllText(fixture.Journals[0]);
        Assert.IsFalse(checkpoint.Contains("secret-application-key", StringComparison.Ordinal));
        Assert.IsFalse(checkpoint.Contains("account-token", StringComparison.Ordinal));
        fixture.Interrupt = null;
        await fixture.CompleteAsync();
        Assert.AreEqual(1, fixture.Starts);
        Assert.AreEqual(3, fixture.UploadedParts.Count);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedSourceCancelsOldChargedPartsBeforeStartingReplacement(bool preserveTimestamp)
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        await using (var changed = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Write, FileShare.None))
            changed.WriteByte(42);
        System.IO.File.SetLastWriteTimeUtc(fixture.SourcePath, preserveTimestamp
            ? Fixture.Modified.UtcDateTime : Fixture.Modified.UtcDateTime.AddSeconds(1));
        await using (var source = fixture.Open()) fixture.Sha1 = Convert.ToHexString(await SHA1.HashDataAsync(source)).ToLowerInvariant();
        await fixture.CompleteAsync();
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(2, fixture.Starts);
        CollectionAssert.AreEqual(new[] { "start", "cancel", "start", "finish" }, fixture.Lifecycle.ToArray());
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task MismatchedRemotePartNeverFinishesAndRemovesUnsafeCheckpoint()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        fixture.CorruptListedPart = true;
        using var store = await fixture.ConnectAsync();
        await using var source = fixture.Open();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.UploadAsync(store, source));
        Assert.AreEqual(0, fixture.Finishes);
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task PermanentUploadFailureCancelsChargedPartsAndCheckpoint()
    {
        using var fixture = new Fixture { RejectUploads = true };
        using var store = await fixture.ConnectAsync();
        await using var source = fixture.Open();
        await Assert.ThrowsExceptionAsync<B2RequestException>(() => fixture.UploadAsync(store, source));
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task MultipartAcknowledgmentForAnotherVersionCannotFinishTheFile()
    {
        using var fixture = new Fixture { WrongAcknowledgmentId = true };
        using var store = await fixture.ConnectAsync();
        await using var source = fixture.Open();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.UploadAsync(store, source));
        Assert.AreEqual(0, fixture.Finishes);
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task LostFinishAcknowledgmentAfterRestartReturnsSameCompletedVersion()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptFinish = true;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        Assert.AreEqual(1, fixture.Journals.Length);
        fixture.Interrupt = null;
        fixture.UploadedParts.Clear();
        var file = await fixture.CompleteAsync();
        Assert.AreEqual("large-1", file.FileId);
        Assert.AreEqual(1, fixture.Finishes);
        Assert.AreEqual(1, fixture.Starts);
        Assert.AreEqual(0, fixture.UploadedParts.Count);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task IntentionalDisconnectCancelsManagedCheckpointWithSeparateToken()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        using var store = await fixture.ConnectAsync();
        await store.CancelPendingUploadsAsync();
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task FailedIntentionalCleanupRetainsCheckpointForItsNextSafeAttempt()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        fixture.RejectCancellation = true;
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<B2RequestException>(() => store.CancelPendingUploadsAsync());
        Assert.AreEqual(1, fixture.Journals.Length, "A failed remote cancellation must retain its recoverable identity.");
        Assert.AreEqual(0, fixture.Cancellations);
        fixture.RejectCancellation = false;
        await store.CancelPendingUploadsAsync();
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
        Assert.AreEqual(0, Directory.GetFiles(fixture.JournalDirectory, "*.lock").Length,
            "Completed cleanup must not accumulate lock-file artifacts.");
    }

    [TestMethod]
    public async Task MissingSourceCleanupRemovesUnfinishedChargedParts()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        System.IO.File.Delete(fixture.SourcePath);
        using var store = await fixture.ConnectAsync();
        await store.CleanupAbandonedUploadsAsync();
        Assert.AreEqual(1, fixture.Cancellations);
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task CleanupSkipsActiveCheckpointInsteadOfWaitingForItsTransferLock()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        System.IO.File.Delete(fixture.SourcePath);
        using var store = await fixture.ConnectAsync();
        using (var active = new FileStream(fixture.Journals.Single() + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            await store.CleanupAbandonedUploadsAsync(deadline.Token);
        Assert.AreEqual(0, fixture.Cancellations, "Maintenance must not interrupt an active managed upload.");
        Assert.AreEqual(1, fixture.Journals.Length);
        await store.CleanupAbandonedUploadsAsync();
        Assert.AreEqual(1, fixture.Cancellations, "Once its active lock is released, the removed source may be cleaned up.");
        Assert.AreEqual(0, fixture.Journals.Length);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CleanupUsesLastJournalActivityRatherThanOnlyOriginalAttemptAge(bool recentlyUsed)
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        var journal = fixture.Journals.Single();
        var entry = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(journal))!.AsObject();
        entry["CreatedUtc"] = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        System.IO.File.WriteAllText(journal, entry.ToJsonString());
        System.IO.File.SetLastWriteTimeUtc(journal, DateTime.UtcNow.AddDays(recentlyUsed ? -1 : -30));
        using var store = await fixture.ConnectAsync();
        await store.CleanupAbandonedUploadsAsync();
        Assert.AreEqual(recentlyUsed ? 0 : 1, fixture.Cancellations);
        Assert.AreEqual(recentlyUsed ? 1 : 0, fixture.Journals.Length);
    }

    [TestMethod]
    public async Task IndependentUploadVerificationChecksImmutableVersionAndChecksum()
    {
        using var fixture = new Fixture();
        fixture.IsFinished = true;
        fixture.FileId = "large-verified";
        using var store = await fixture.ConnectAsync();
        var file = new CloudObject(fixture.FileId, Fixture.Key, Fixture.Length, fixture.Sha1, Fixture.Modified);
        await store.VerifyUploadAsync(file, "bucket");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.VerifyUploadAsync(file with { Sha1 = new string('f', 40) }, "bucket"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.VerifyUploadAsync(file with { Size = file.Size - 1 }, "bucket"));
    }

    [TestMethod]
    public async Task MalformedJournalIsPreservedAndDoesNotCreateOrCancelRemoteFiles()
    {
        using var fixture = new Fixture();
        using var interrupted = new CancellationTokenSource();
        fixture.Interrupt = interrupted;
        fixture.InterruptPart = 1;
        await fixture.ExpectInterruptedAsync(interrupted.Token);
        fixture.Interrupt = null;
        System.IO.File.WriteAllText(fixture.Journals.Single(), "{interrupted checkpoint");
        using var store = await fixture.ConnectAsync();
        await using var source = fixture.Open();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.UploadAsync(store, source));
        Assert.AreEqual(1, fixture.Starts);
        Assert.AreEqual(0, fixture.Cancellations);
        Assert.AreEqual(1, fixture.Journals.Length);
    }

    private sealed class CapturedProgress : IProgress<TransferProgress>
    {
        public ConcurrentQueue<TransferProgress> Values { get; } = new();
        public void Report(TransferProgress value) => Values.Enqueue(value);
    }

    private sealed class SerializedProgress : IProgress<TransferProgress>
    {
        private int _callbacks;
        public bool ConcurrentCallback;
        public ConcurrentQueue<TransferProgress> Values { get; } = new();
        public void Report(TransferProgress value)
        {
            if (Interlocked.Increment(ref _callbacks) > 1) ConcurrentCallback = true;
            try
            {
                // Give another worker a scheduling opportunity. A plain atomic
                // byte increment without ordered publication overlaps here.
                if (!value.IsBaseline) Thread.Sleep(1);
                Values.Enqueue(value);
            }
            finally { Interlocked.Decrement(ref _callbacks); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const long Length = 200_000_001;
        public const string Key = "CloudBay/large.bin";
        public static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        private static readonly string ZeroSha = ZeroHash(Length);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudBay-B2Resume-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentDictionary<int, (long Length, string Sha1)> _parts = new();
        private string? _uploadId;
        public string SourcePath { get; }
        public string JournalDirectory { get; }
        public string[] Journals => Directory.GetFiles(JournalDirectory, "*.json");
        public string Sha1 { get; set; } = ZeroSha;
        public string? FileId { get; set; }
        public CancellationTokenSource? Interrupt { get; set; }
        public int InterruptPart { get; set; }
        public bool InterruptStart { get; set; }
        public bool InterruptFinish { get; set; }
        public bool IsFinished { get; set; }
        public bool RejectUploads { get; set; }
        public bool RejectCancellation { get; set; }
        public bool WrongAcknowledgmentId { get; set; }
        public bool ValidateReceivedHashes { get; set; }
        public bool RetryFirstPartOnce { get; set; }
        private int _firstPartAttempts;
        private long _receivedPayloadBytes;
        public long ReceivedPayloadBytes => Interlocked.Read(ref _receivedPayloadBytes);
        public long RecommendedPartSize { get; set; } = 100_000_000;
        public bool CorruptListedPart { get; set; }
        public int Starts { get; private set; }
        public int Finishes { get; private set; }
        public int Cancellations { get; private set; }
        public ConcurrentQueue<int> UploadedParts { get; } = new();
        public ConcurrentQueue<string> Lifecycle { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            JournalDirectory = Path.Combine(_directory, "uploads");
            Directory.CreateDirectory(JournalDirectory);
            SourcePath = Path.Combine(_directory, "source.bin");
            using (var source = new FileStream(SourcePath, FileMode.CreateNew, FileAccess.Write)) source.SetLength(Length);
            System.IO.File.SetLastWriteTimeUtc(SourcePath, Modified.UtcDateTime);
        }
        public FileStream Open() => new(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        public async Task<B2CloudStore> ConnectAsync()
        {
            var store = new B2CloudStore(new FakeHandler(HandleAsync));
            store.Configure(0, 0, 1);
            store.ConfigureResumableUploads(JournalDirectory);
            await store.ConnectAsync(new("application-id", "secret-application-key"));
            return store;
        }
        public Task<CloudObject> UploadAsync(B2CloudStore store, FileStream source, IProgress<TransferProgress>? progress = null, CancellationToken token = default) =>
            store.UploadAsync("bucket", Key, source, Length, Sha1, System.IO.File.GetLastWriteTimeUtc(SourcePath), progress, token);
        public async Task ExpectInterruptedAsync(CancellationToken token)
        {
            using var store = await ConnectAsync();
            await using var source = Open();
            try { await UploadAsync(store, source, token: token); Assert.Fail("The fixture must interrupt the operation."); }
            catch (OperationCanceledException) { }
        }
        public async Task<CloudObject> CompleteAsync(IProgress<TransferProgress>? progress = null)
        {
            using var store = await ConnectAsync();
            await using var source = Open();
            return await UploadAsync(store, source, progress);
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var operation = request.RequestUri!.Segments.Last().Trim('/');
            if (operation == "b2_authorize_account") return Json(new
            {
                accountId = "account", authorizationToken = "account-token",
                apiInfo = new { storageApi = new { apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid", absoluteMinimumPartSize = 5_000_000,
                    recommendedPartSize = RecommendedPartSize, allowed = new { buckets = (object?)null, capabilities = new[] { "writeFiles", "readFiles", "listFiles" }, namePrefix = (string?)null } } }
            });
            if (operation == "part")
            {
                if (RejectUploads) return Json(new { code = "storage_cap_exceeded" }, HttpStatusCode.Forbidden);
                var number = int.Parse(request.Headers.GetValues("X-Bz-Part-Number").Single());
                var hash = request.Headers.GetValues("X-Bz-Content-Sha1").Single();
                var length = request.Content!.Headers.ContentLength!.Value;
                if (ValidateReceivedHashes)
                {
                    using var received = new HashSinkStream();
                    await request.Content.CopyToAsync(received, ct);
                    if (!received.Finish().Equals(hash, StringComparison.OrdinalIgnoreCase))
                        return Json(new { code = "sha1_mismatch" }, HttpStatusCode.BadRequest);
                }
                else await request.Content.CopyToAsync(Stream.Null, ct);
                Interlocked.Add(ref _receivedPayloadBytes, length);
                if (RetryFirstPartOnce && number == 1 && Interlocked.Increment(ref _firstPartAttempts) == 1)
                {
                    var retry = Json(new { code = "service_unavailable" }, HttpStatusCode.ServiceUnavailable);
                    retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                    return retry;
                }
                _parts[number] = (length, hash); UploadedParts.Enqueue(number);
                if (Interrupt is not null && number == InterruptPart) { Interrupt.Cancel(); ct.ThrowIfCancellationRequested(); }
                return Json(new { fileId = WrongAcknowledgmentId ? "another-version" : FileId,
                    partNumber = number, contentLength = length, contentSha1 = hash });
            }
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            switch (operation)
            {
                case "b2_start_large_file":
                    Starts++; Lifecycle.Enqueue("start"); FileId = "large-" + Starts; IsFinished = false; _parts.Clear();
                    _uploadId = payload.RootElement.GetProperty("fileInfo").GetProperty("cloudbay_upload_id").GetString();
                    if (Interrupt is not null && InterruptStart) { Interrupt.Cancel(); ct.ThrowIfCancellationRequested(); }
                    return Json(new { fileId = FileId });
                case "b2_get_upload_part_url": return Json(new { uploadUrl = "https://upload.invalid/part", authorizationToken = "part-token" });
                case "b2_list_parts":
                    if (IsFinished) return Json(new { code = "bad_request" }, HttpStatusCode.BadRequest);
                    return Json(new { parts = _parts.OrderBy(p => p.Key).Select(p => new { fileId = FileId, partNumber = p.Key,
                        contentLength = p.Value.Length, contentSha1 = CorruptListedPart ? new string('f', 40) : p.Value.Sha1 }).ToArray(), nextPartNumber = (int?)null });
                case "b2_list_unfinished_large_files":
                    return Json(new { files = FileId is null || IsFinished ? Array.Empty<object>() : new object[] { new { fileId = FileId, fileName = Key, bucketId = "bucket",
                        action = "start", contentLength = 0, contentSha1 = "none", uploadTimestamp = 0, fileInfo = new { cloudbay_upload_id = _uploadId, large_file_sha1 = Sha1 } } }, nextFileId = (string?)null });
                case "b2_cancel_large_file":
                    Assert.IsFalse(ct.IsCancellationRequested);
                    if (RejectCancellation)
                    {
                        var rejected = Json(new { code = "service_unavailable" }, HttpStatusCode.ServiceUnavailable);
                        rejected.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                        return rejected;
                    }
                    Cancellations++; Lifecycle.Enqueue("cancel"); _parts.Clear(); FileId = null;
                    return Json(new { fileId = "cancelled" });
                case "b2_finish_large_file":
                    Finishes++; Lifecycle.Enqueue("finish");
                    var hashes = payload.RootElement.GetProperty("partSha1Array").EnumerateArray().Select(h => h.GetString()).ToArray();
                    Assert.AreEqual(_parts.Count, hashes.Length);
                    for (var i = 0; i < hashes.Length; i++) Assert.AreEqual(_parts[i + 1].Sha1, hashes[i]);
                    IsFinished = true;
                    if (Interrupt is not null && InterruptFinish) { Interrupt.Cancel(); ct.ThrowIfCancellationRequested(); }
                    return Completed();
                case "b2_get_file_info": return IsFinished ? Completed() : Json(new { code = "not_found" }, HttpStatusCode.NotFound);
                default: throw new InvalidOperationException("Unexpected B2 operation: " + operation);
            }
        }
        private HttpResponseMessage Completed() => Json(new { fileId = FileId, bucketId = "bucket", accountId = "account", fileName = Key,
            contentLength = Length, contentSha1 = "none", action = "upload", uploadTimestamp = 0, fileInfo = new { large_file_sha1 = Sha1 } });
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private static string ZeroHash(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var chunk = new byte[64 * 1024];
        while (length > 0) { var count = (int)Math.Min(chunk.Length, length); hash.AppendData(chunk, 0, count); length -= count; }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request, token);
    }

    private sealed class CountingFileStream(string path, FileShare share = FileShare.Read)
        : FileStream(path, FileMode.Open, FileAccess.Read, share, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }

    private sealed class HashSinkStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _hash.AppendData(buffer.Span); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}

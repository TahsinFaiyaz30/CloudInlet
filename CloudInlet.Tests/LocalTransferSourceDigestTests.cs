using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class LocalTransferSourceDigestTests
{
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(41)]
    [DataRow(10 * 1024 * 1024 + 19)]
    public async Task SourceWithoutProviderChecksumIsReadOnceAndDestinationIsIndependentlyVerified(int size)
    {
        using var fixture = new Fixture(size);
        var receipt = await fixture.UploadAsync();
        fixture.Source.ForbidReads = true;
        var verified = await ((ITransferEndpoint)fixture.Endpoint).VerifyReceiptAsync(receipt, fixture.Source);
        Assert.AreEqual(receipt, verified);
        Assert.AreEqual(size == 0 ? 0 : 1, fixture.Source.Ranges.Count);
        Assert.AreEqual(fixture.PayloadHash, receipt.Sha1);
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
        var proof = JsonNode.Parse(receipt.Data!["sourceProof"])!;
        Assert.AreEqual(fixture.Source.Entry.Id, proof["SourceId"]!.GetValue<string>());
        Assert.AreEqual(fixture.Source.Entry.Version, proof["SourceVersion"]!.GetValue<string>());
        Assert.AreEqual((long)size, proof["SourceSize"]!.GetValue<long>());
        Assert.AreEqual(fixture.PayloadHash, proof["Sha1"]!.GetValue<string>());
        Assert.AreEqual(receipt.Data["sourceProof"], fixture.Saved!.Data!["sourceProof"]);
    }

    [TestMethod]
    public async Task RestartHashesVerifiedPrefixAndReadsOnlyUnfinishedSourceSuffix()
    {
        using var fixture = new Fixture(9 * 1024 * 1024 + 23);
        await fixture.InterruptAfterFirstChunkAsync();
        var saved = RoundTrip(fixture.Saved!);
        var restartedSource = new MemorySource(fixture.Payload, fixture.Source.Entry);
        var restarted = new LocalTransferEndpoint(fixture.Endpoint.Location);
        var receipt = await restarted.UploadAsync(fixture.Request, restartedSource, saved, (_, _) => Task.CompletedTask);
        restartedSource.ForbidReads = true;
        var durableReceipt = JsonSerializer.Deserialize<TransferReceipt>(JsonSerializer.Serialize(receipt))!;
        await ((ITransferEndpoint)restarted).VerifyReceiptAsync(durableReceipt, restartedSource);
        CollectionAssert.AreEqual(new[] { (4 * 1024 * 1024L, fixture.Source.Entry.Size - 4 * 1024 * 1024L) }, restartedSource.Ranges.ToArray());
        Assert.AreEqual(fixture.PayloadHash, receipt.Sha1);
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
    }

    [TestMethod]
    public async Task RealLocalSourceMetadataPreservingEditCannotVerifyAHybridResumedCopy()
    {
        using var fixture = new Fixture(5 * 1024 * 1024 + 27);
        var sourceFolder = Path.Combine(fixture.DirectoryPath, "source");
        Directory.CreateDirectory(sourceFolder);
        var sourcePath = Path.Combine(sourceFolder, "item.bin");
        await File.WriteAllBytesAsync(sourcePath, fixture.Payload);
        var sourceEndpoint = new LocalTransferEndpoint(LocalTransferEndpoint.ForFolder(sourceFolder));
        var entry = (await sourceEndpoint.DiscoverAsync()).Entries.Single();
        var source = sourceEndpoint.OpenSource(entry);
        Assert.IsFalse(source.HasContentBoundVersion);
        Assert.IsNull(entry.Sha1);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source, null,
            (checkpoint, _) =>
            {
                saved = checkpoint;
                if (checkpoint.AcknowledgedBytes == 4 * 1024 * 1024L) throw new OperationCanceledException();
                return Task.CompletedTask;
            }));

        var modified = File.GetLastWriteTimeUtc(sourcePath);
        var changed = fixture.Payload.ToArray();
        changed[0] ^= 0xff;
        changed[4 * 1024 * 1024] ^= 0xff;
        using (var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.None))
        { await file.WriteAsync(changed); file.Flush(true); }
        File.SetLastWriteTimeUtc(sourcePath, modified);
        var rediscovered = (await sourceEndpoint.DiscoverAsync()).Entries.Single();
        Assert.AreEqual(entry.Id, rediscovered.Id);
        Assert.AreEqual(entry.Version, rediscovered.Version, "Local metadata can remain unchanged while the contents change.");
        var restartedSource = sourceEndpoint.OpenSource(entry);
        await restartedSource.ValidateAsync();
        TransferCheckpoint? latest = null;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.UploadAsync(fixture.Request, restartedSource,
            RoundTrip(saved!), (checkpoint, _) => { latest = checkpoint; return Task.CompletedTask; }));
        Assert.IsFalse(File.Exists(fixture.Final), "A mixed old prefix and new suffix must not become an installed verified copy.");
        Assert.IsTrue(File.Exists(fixture.Partial));
        Assert.IsFalse(latest!.Data!.ContainsKey("committing"));
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(sourcePath));
    }

    [TestMethod]
    public async Task UntrustedVersionProofRetainsFullSourceComparison()
    {
        using var fixture = new Fixture(499);
        var receipt = await fixture.UploadAsync();
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry) { HasContentBoundVersion = false };
        await fixture.Endpoint.VerifyAsync(receipt, source);
        Assert.AreEqual(1, source.Ranges.Count);
        var changed = fixture.Payload.ToArray(); changed[0] ^= 0xff;
        var changedSource = new MemorySource(changed, fixture.Source.Entry) { HasContentBoundVersion = false };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.VerifyAsync(receipt, changedSource));
        Assert.AreEqual(1, changedSource.Ranges.Count);
    }

    [TestMethod]
    public async Task SourceChecksumStillVerifiesWithoutAContentBoundVersionOrSecondRead()
    {
        using var fixture = new Fixture(499);
        var receipt = await fixture.UploadAsync();
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry with { Sha1 = fixture.PayloadHash })
        { HasContentBoundVersion = false, ForbidReads = true };
        await fixture.Endpoint.VerifyAsync(receipt, source);
        Assert.AreEqual(0, source.Ranges.Count);
    }

    [DataTestMethod]
    [DataRow("b2")]
    [DataRow("onedrive")]
    public async Task RealProviderSourcesKeepOnePayloadReadWithoutProviderChecksums(string provider)
    {
        using var fixture = new Fixture(631);
        using var handler = new ProviderSourceHandler(fixture.Payload);
        using var http = new HttpClient(handler);
        using var b2 = provider == "b2" ? new B2CloudStore(handler) : null;
        ITransferSourceFile source;
        if (b2 is not null)
        {
            await b2.ConnectAsync(new("test-key", "test-value"));
            source = new B2TransferEndpoint(b2, new("b2", "account", "bucket", "", "", "B2 source"))
                .OpenSource(new("file-v1", "item.bin", "file-v1", fixture.Payload.Length, DateTimeOffset.UnixEpoch));
        }
        else
        {
            var graph = new OneDriveClient(new("00000000-0000-0000-0000-000000000001", tokens:
                new("test-token", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"), http: http), http);
            source = new OneDriveTransferEndpoint(graph, new("onedrive", "account", "drive", "root", "", "Graph source"))
                .OpenSource(new("file-id", "item.bin", "etag-v1", fixture.Payload.Length, DateTimeOffset.UnixEpoch));
        }
        Assert.IsTrue(source.HasContentBoundVersion);
        Assert.IsNull(source.Entry.Sha1);
        var receipt = await fixture.Endpoint.UploadAsync(fixture.Request, source, null, (_, _) => Task.CompletedTask);
        await source.ValidateAsync();
        await fixture.Endpoint.VerifyAsync(receipt, source);
        await source.ValidateAsync();
        Assert.AreEqual(1, handler.PayloadReads, "Provider version proof must retain the single cloud payload pass.");
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
    }

    [TestMethod]
    public async Task CompletedSourceReadReleasesDownloadSlotBeforeBlockedLocalFinalization()
    {
        using var fixture = new Fixture(128 * 1024 + 29);
        using var admission = new SemaphoreSlim(1, 1);
        fixture.Source.ReadAdmission = admission;
        var finalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.OnValidation = async (count, token) =>
        {
            if (count != 2) return;
            Assert.AreEqual(0, fixture.Source.ActiveStreams, "Payload admission must be released before source revalidation and installation.");
            finalization.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var first = fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data!.GetValueOrDefault("committing") == "true") Assert.AreEqual(0, fixture.Source.ActiveStreams);
        });
        Task<TransferReceipt>? second = null;
        try
        {
            await finalization.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var next = new MemorySource("next-cloud-file"u8.ToArray()) { ReadAdmission = admission };
            var request = fixture.Request with { RelativePath = "next.bin", OperationId = new string('b', 64) };
            second = fixture.Endpoint.UploadAsync(request, next, null, (_, _) => Task.CompletedTask);
            await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(first.IsCompleted, "The next download must finish while the first copy is still finalizing.");
            Assert.AreEqual(1, next.Ranges.Count);
            Assert.AreEqual(0, next.ActiveStreams);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public async Task RestartWithFullyAcknowledgedPartialNeedsNoSourcePayloadAgain()
    {
        using var fixture = new Fixture(4 * 1024 * 1024 + 43);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.UploadAsync(onCheckpoint: checkpoint =>
        {
            if (checkpoint.Data!.GetValueOrDefault("committing") == "true") throw new OperationCanceledException();
        }));
        Assert.IsFalse(File.Exists(fixture.Final));
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry) { ForbidReads = true };
        var restarted = new LocalTransferEndpoint(fixture.Endpoint.Location);
        var receipt = await restarted.UploadAsync(fixture.Request, source, RoundTrip(fixture.Saved!), (_, _) => Task.CompletedTask);
        await ((ITransferEndpoint)restarted).VerifyReceiptAsync(receipt, source);
        Assert.AreEqual(0, source.Ranges.Count);
        Assert.AreEqual(fixture.PayloadHash, receipt.Sha1);
    }

    [TestMethod]
    public async Task InterruptedCommitReconcilesUsingDurableSourceProofWithoutAnotherDownload()
    {
        using var fixture = new Fixture(121);
        var initial = await fixture.UploadAsync();
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry) { ForbidReads = true };
        var restarted = new LocalTransferEndpoint(fixture.Endpoint.Location);
        var reconciled = await restarted.ReconcileAsync(fixture.Request, source, RoundTrip(fixture.Saved!));
        Assert.IsNotNull(reconciled);
        Assert.AreEqual(initial.Id, reconciled.Id);
        Assert.AreEqual(initial.Version, reconciled.Version);
        Assert.AreEqual(initial.Data!["sourceProof"], reconciled.Data!["sourceProof"]);
        await ((ITransferEndpoint)restarted).VerifyReceiptAsync(reconciled, source);
        Assert.AreEqual(0, source.Ranges.Count);
        Assert.AreEqual(1, Directory.GetFiles(fixture.DirectoryPath).Length, "Reconciliation must retain the single installed copy.");
    }

    [TestMethod]
    public async Task LegacyReceiptWithoutProofStillReadsAndComparesTheSource()
    {
        using var fixture = new Fixture(237);
        var receipt = await fixture.UploadAsync();
        var legacy = receipt with { Data = null };
        await ((ITransferEndpoint)fixture.Endpoint).VerifyReceiptAsync(legacy, fixture.Source);
        CollectionAssert.AreEqual(new[] { (0L, fixture.Source.Entry.Size), (0L, fixture.Source.Entry.Size) }, fixture.Source.Ranges.ToArray());
        var wrongSource = new MemorySource(fixture.Payload.Select(value => (byte)(value ^ 0xff)).ToArray(), fixture.Source.Entry);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.VerifyAsync(legacy, wrongSource));
        Assert.AreEqual(1, wrongSource.Ranges.Count);
    }

    [TestMethod]
    public async Task LegacyCommitReconciliationRetainsSourceHashFallback()
    {
        using var fixture = new Fixture(401);
        await fixture.UploadAsync();
        var data = new Dictionary<string, string>(fixture.Saved!.Data!);
        data.Remove("sourceProof");
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry);
        var reconciled = await fixture.Endpoint.ReconcileAsync(fixture.Request, source, fixture.Saved with { Data = data });
        Assert.IsNotNull(reconciled);
        Assert.IsFalse(reconciled.Data?.ContainsKey("sourceProof") == true);
        await ((ITransferEndpoint)fixture.Endpoint).VerifyReceiptAsync(reconciled, source);
        CollectionAssert.AreEqual(new[] { (0L, source.Entry.Size) }, source.Ranges.ToArray());
    }

    [TestMethod]
    public async Task LegacyUnboundPartialRequiresFullSourceVerificationBeforeIssuingNewProof()
    {
        using var fixture = new Fixture(5 * 1024 * 1024 + 27);
        await fixture.InterruptAfterFirstChunkAsync();
        var data = new Dictionary<string, string>(fixture.Saved!.Data!);
        data.Remove("sourceProof");
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry);
        var receipt = await fixture.Endpoint.UploadAsync(fixture.Request, source, fixture.Saved with { Data = data }, (_, _) => Task.CompletedTask);
        CollectionAssert.AreEqual(new[] { (4 * 1024 * 1024L, source.Entry.Size - 4 * 1024 * 1024L), (0L, source.Entry.Size) }, source.Ranges.ToArray());
        source.ForbidReads = true;
        await ((ITransferEndpoint)fixture.Endpoint).VerifyReceiptAsync(receipt, source);
        Assert.IsNotNull(receipt.Data!["sourceProof"]);
    }

    [TestMethod]
    public async Task LegacyUnboundPrefixCannotBecomeProofForChangedBytesAtTheSameAdvertisedVersion()
    {
        using var fixture = new Fixture(5 * 1024 * 1024 + 27);
        await fixture.InterruptAfterFirstChunkAsync();
        var data = new Dictionary<string, string>(fixture.Saved!.Data!);
        data.Remove("sourceProof");
        var changed = fixture.Payload.ToArray(); changed[0] ^= 1;
        var source = new MemorySource(changed, fixture.Source.Entry);
        TransferCheckpoint? latest = null;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source,
            fixture.Saved with { Data = data }, (value, _) => { latest = value; return Task.CompletedTask; }));
        Assert.IsFalse(File.Exists(fixture.Final));
        Assert.IsTrue(File.Exists(fixture.Partial));
        Assert.IsFalse(latest!.Data!.ContainsKey("sourceProof"), "An unbound legacy prefix must not become a recoverable proof before source comparison.");
        Assert.IsFalse(latest.Data.ContainsKey("committing"));
    }

    [DataTestMethod]
    [DataRow("json")]
    [DataRow("missing")]
    [DataRow("format")]
    [DataRow("identity")]
    [DataRow("version")]
    [DataRow("size")]
    [DataRow("checksum")]
    [DataRow("malformed-checksum")]
    [DataRow("incomplete")]
    public async Task MalformedOrMismatchedProofCannotFallBackToReDownloadingOrAdoptDestination(string alteration)
    {
        using var fixture = new Fixture(313);
        var receipt = await fixture.UploadAsync();
        var proof = AlterProof(receipt.Data!["sourceProof"], alteration);
        var data = new Dictionary<string, string>(receipt.Data) { ["sourceProof"] = proof };
        fixture.Source.ForbidReads = true;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.VerifyAsync(receipt with { Data = data }, fixture.Source));
        var checkpointData = new Dictionary<string, string>(fixture.Saved!.Data!) { ["sourceProof"] = proof };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.ReconcileAsync(fixture.Request, fixture.Source,
            fixture.Saved with { Data = checkpointData }));
        Assert.AreEqual(1, fixture.Source.Ranges.Count);
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
    }

    [TestMethod]
    public async Task SourceBindingMismatchInPartialStopsBeforeOpeningSourceOrChangingPartial()
    {
        using var fixture = new Fixture(5 * 1024 * 1024);
        await fixture.InterruptAfterFirstChunkAsync();
        var before = File.ReadAllBytes(fixture.Partial);
        var data = new Dictionary<string, string>(fixture.Saved!.Data!)
        { ["sourceProof"] = AlterProof(fixture.Saved.Data!["sourceProof"], "version") };
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry) { ForbidReads = true };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source,
            fixture.Saved with { Data = data }, (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, source.Ranges.Count);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Partial));
    }

    [TestMethod]
    public async Task CorruptedAcknowledgedPrefixIsNeverIncludedInTheResumedDigest()
    {
        using var fixture = new Fixture(5 * 1024 * 1024);
        await fixture.InterruptAfterFirstChunkAsync();
        using (var partial = new FileStream(fixture.Partial, FileMode.Open, FileAccess.Write, FileShare.None)) partial.WriteByte(99);
        var source = new MemorySource(fixture.Payload, fixture.Source.Entry) { ForbidReads = true };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source,
            fixture.Saved, (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, source.Ranges.Count);
        Assert.IsFalse(File.Exists(fixture.Final));
        Assert.IsTrue(File.Exists(fixture.Partial));
    }

    [TestMethod]
    public async Task DestinationCorruptionWithUnchangedIdentitySizeAndTimestampFailsIndependentHash()
    {
        using var fixture = new Fixture(819);
        var receipt = await fixture.UploadAsync();
        CorruptWithoutChangingMetadata(fixture.Final);
        fixture.Source.ForbidReads = true;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.VerifyAsync(receipt, fixture.Source));
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => fixture.Endpoint.ReconcileAsync(fixture.Request, fixture.Source, fixture.Saved));
        Assert.AreEqual(1, fixture.Source.Ranges.Count);
        Assert.IsTrue(File.Exists(fixture.Final));
    }

    [DataTestMethod]
    [DataRow("identity")]
    [DataRow("version")]
    [DataRow("size")]
    public async Task BoundReceiptCannotVerifyAgainstAnotherSourceEntry(string alteration)
    {
        using var fixture = new Fixture(571);
        var receipt = await fixture.UploadAsync();
        var entry = alteration switch
        {
            "identity" => fixture.Source.Entry with { Id = "another-source" },
            "version" => fixture.Source.Entry with { Version = "source-v2" },
            _ => fixture.Source.Entry with { Size = fixture.Source.Entry.Size + 1 }
        };
        var source = new MemorySource(fixture.Payload, entry) { ForbidReads = true };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.VerifyAsync(receipt, source));
        Assert.AreEqual(0, source.Ranges.Count);
    }

    [TestMethod]
    public async Task SourceChangeAtPostCopyValidationCannotIssueCommitProofOrInstallTheFile()
    {
        using var fixture = new Fixture(947);
        fixture.Source.ChangeOnValidation = 2;
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => fixture.UploadAsync());
        Assert.IsFalse(File.Exists(fixture.Final));
        Assert.IsTrue(File.Exists(fixture.Partial));
        Assert.IsFalse(fixture.Saved!.Data!.ContainsKey("committing"));
        Assert.IsNull(JsonNode.Parse(fixture.Saved.Data["sourceProof"])!["Sha1"]);
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => fixture.UploadAsync(fixture.Saved));
        Assert.AreEqual(1, fixture.Source.Ranges.Count);
    }

    [TestMethod]
    public async Task ChangedSourceAfterCopyBeforeEngineVerificationRetainsBothCopiesAndNeverMoves()
    {
        using var fixture = new Fixture(631);
        fixture.Source.ChangeOnValidation = 3;
        var sourceEndpoint = new SourceEndpoint(fixture.Source);
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), sourceEndpoint.Location, fixture.Endpoint.Location,
            TransferOperation.Move, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        await using var engine = new TransferJobEngine(new(Path.Combine(fixture.DirectoryPath, "jobs.sqlite"), new Protector()),
            location => location == plan.Source ? sourceEndpoint : fixture.Endpoint, 1);
        await engine.CreateAsync(plan);
        await engine.RunAsync(plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Attention, engine.Snapshots().Single().State);
        Assert.AreEqual(0, sourceEndpoint.Deletes.Count);
        Assert.AreEqual(1, fixture.Source.Ranges.Count);
        CollectionAssert.AreEqual(fixture.Payload, File.ReadAllBytes(fixture.Final));
        await engine.ResumeAsync(plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, sourceEndpoint.Deletes.Count);
        Assert.AreEqual(1, fixture.Source.Ranges.Count, "A saved verified-content receipt must not cause another upload after a source change.");
    }

    [TestMethod]
    public async Task UnchangedMoveUsesReceivedDigestAndExactSavedSourceIdentityWithoutAnotherDownload()
    {
        using var fixture = new Fixture(697);
        var sourceEndpoint = new SourceEndpoint(fixture.Source);
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), sourceEndpoint.Location, fixture.Endpoint.Location,
            TransferOperation.Move, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        await using var engine = new TransferJobEngine(new(Path.Combine(fixture.DirectoryPath, "jobs.sqlite"), new Protector()),
            location => location == plan.Source ? sourceEndpoint : fixture.Endpoint, 1);
        await engine.CreateAsync(plan);
        await engine.RunAsync(plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, engine.Snapshots().Single().State);
        Assert.AreEqual(1, fixture.Source.Ranges.Count);
        var deleted = sourceEndpoint.Deletes.Single();
        Assert.AreEqual(fixture.Source.Entry.Id, deleted.Id);
        Assert.AreEqual(fixture.Source.Entry.Version, deleted.Version);
        Assert.AreEqual(fixture.PayloadHash, deleted.Sha1);
    }

    private static TransferCheckpoint RoundTrip(TransferCheckpoint checkpoint) =>
        JsonSerializer.Deserialize<TransferCheckpoint>(JsonSerializer.Serialize(checkpoint))!;

    private static string AlterProof(string proof, string alteration)
    {
        if (alteration == "json") return "{broken";
        var data = JsonNode.Parse(proof)!.AsObject();
        switch (alteration)
        {
            case "missing": data.Remove("SourceSize"); break;
            case "format": data["Format"] = 999; break;
            case "identity": data["SourceId"] = "foreign-source"; break;
            case "version": data["SourceVersion"] = "source-v2"; break;
            case "size": data["SourceSize"] = data["SourceSize"]!.GetValue<long>() + 1; break;
            case "checksum": data["Sha1"] = new string('f', 40); break;
            case "malformed-checksum": data["Sha1"] = "not-a-sha1"; break;
            case "incomplete": data["Sha1"] = null; break;
        }
        return data.ToJsonString();
    }

    private static void CorruptWithoutChangingMetadata(string path)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { var original = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(original ^ 0xff)); file.Flush(true); }
        File.SetLastWriteTimeUtc(path, modified);
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CloudInletSourceDigest-" + Guid.NewGuid().ToString("N"));
        public byte[] Payload { get; }
        public string PayloadHash => Convert.ToHexString(SHA1.HashData(Payload)).ToLowerInvariant();
        public LocalTransferEndpoint Endpoint { get; }
        public MemorySource Source { get; }
        public TransferUploadRequest Request { get; } = new(Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(), "item.bin", TransferConflictPolicy.Fail);
        public string Final => Path.Combine(DirectoryPath, "item.bin");
        public string Partial => Path.Combine(DirectoryPath, ".CloudBay-transfer-" + Request.OperationId + ".part");
        public TransferCheckpoint? Saved { get; private set; }
        public Fixture(int size)
        {
            Directory.CreateDirectory(DirectoryPath);
            Payload = new byte[size];
            for (var index = 0; index < size; index++) Payload[index] = (byte)((index * 31L + index / 1024) % 251);
            Source = new(Payload);
            Endpoint = new(LocalTransferEndpoint.ForFolder(DirectoryPath));
        }
        public Task<TransferReceipt> UploadAsync(TransferCheckpoint? checkpoint = null, Action<TransferCheckpoint>? onCheckpoint = null) =>
            Endpoint.UploadAsync(Request, Source, checkpoint, (value, _) =>
            { Saved = value; onCheckpoint?.Invoke(value); return Task.CompletedTask; });
        public Task InterruptAfterFirstChunkAsync() => Assert.ThrowsExceptionAsync<OperationCanceledException>(() => UploadAsync(onCheckpoint: checkpoint =>
        { if (checkpoint.AcknowledgedBytes == 4 * 1024 * 1024L) throw new OperationCanceledException(); }));
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class MemorySource : ITransferSourceFile
    {
        private readonly byte[] _payload;
        public TransferEntry Entry { get; }
        public bool HasContentBoundVersion { get; init; } = true;
        public List<(long Offset, long Length)> Ranges { get; } = [];
        public bool ForbidReads { get; set; }
        public int ChangeOnValidation { get; set; } = int.MaxValue;
        public SemaphoreSlim? ReadAdmission { get; set; }
        public Func<int, CancellationToken, Task>? OnValidation { get; set; }
        private int _activeStreams;
        public int ActiveStreams => Volatile.Read(ref _activeStreams);
        private int _validations;
        public MemorySource(byte[] payload, TransferEntry? entry = null)
        { _payload = payload; Entry = entry ?? new("drive-item-id", "item.bin", "source-v1", payload.Length, DateTimeOffset.UnixEpoch); }
        public async Task ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref _validations);
            if (count >= ChangeOnValidation) throw new TransferSourceChangedException("The cloud source version changed.");
            if (OnValidation is not null) await OnValidation(count, cancellationToken);
        }
        public async Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ForbidReads) throw new AssertFailedException("Verification or reconciliation unexpectedly downloaded the source again.");
            if (ReadAdmission is not null) await ReadAdmission.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _activeStreams);
            Ranges.Add((offset, length));
            return new TrackedStream(_payload, checked((int)offset), checked((int)length), () =>
            { Interlocked.Decrement(ref _activeStreams); ReadAdmission?.Release(); });
        }

        private sealed class TrackedStream(byte[] payload, int offset, int length, Action onDispose) : MemoryStream(payload, offset, length, false)
        {
            private int _disposed;
            protected override void Dispose(bool disposing)
            { if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(); base.Dispose(disposing); }
            public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ProviderSourceHandler(byte[] payload) : HttpMessageHandler
    {
        public int PayloadReads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.Host is "b2-download.invalid" or "cdn.invalid")
            {
                PayloadReads++;
                var content = new ByteArrayContent(payload);
                var partial = request.Headers.Range is not null;
                if (partial) content.Headers.ContentRange = new ContentRangeHeaderValue(0, payload.Length - 1, payload.Length);
                var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
                response.Headers.TryAddWithoutValidation("X-Bz-File-Id", "file-v1");
                response.Headers.TryAddWithoutValidation("X-Bz-File-Name", "item.bin");
                response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", "none");
                return Task.FromResult(response);
            }
            if (uri.Host == "graph.microsoft.com") return Json(new Dictionary<string, object?>
            {
                ["id"] = "file-id", ["name"] = "item.bin", ["size"] = payload.Length, ["eTag"] = "etag-v1",
                ["lastModifiedDateTime"] = DateTimeOffset.UnixEpoch, ["file"] = new { hashes = new { } },
                ["@microsoft.graph.downloadUrl"] = "https://cdn.invalid/item"
            });
            if (uri.AbsolutePath.EndsWith("b2_authorize_account", StringComparison.Ordinal)) return Json(new
            {
                accountId = "account", authorizationToken = "test-token", apiInfo = new { storageApi = new
                {
                    apiUrl = "https://b2-api.invalid", downloadUrl = "https://b2-download.invalid", absoluteMinimumPartSize = 5_000_000,
                    recommendedPartSize = 5_000_000, allowed = new { buckets = (object?)null, namePrefix = (string?)null,
                        capabilities = new[] { "listFiles", "readFiles" } }
                } }
            });
            if (uri.AbsolutePath.EndsWith("b2_get_file_info", StringComparison.Ordinal)) return Json(new
            {
                accountId = "account", bucketId = "bucket", fileId = "file-v1", fileName = "item.bin", contentLength = payload.Length,
                contentSha1 = "none", action = "upload", uploadTimestamp = 0, fileInfo = new { }
            });
            throw new AssertFailedException("Unexpected isolated provider request: " + uri.AbsolutePath);
        }
        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }

    private sealed class Protector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
    }

    private sealed class SourceEndpoint(MemorySource source) : ITransferEndpoint
    {
        public TransferLocation Location { get; } = new("onedrive", "test-account", "test-drive", "test-folder", "", "isolated test source");
        public List<TransferEntry> Deletes { get; } = [];
        public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransferDiscoveryPage([source.Entry], null));
        public ITransferSourceFile OpenSource(TransferEntry entry) => source;
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile file, TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile file, TransferCheckpoint? checkpoint,
            Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default)
        { await source.ValidateAsync(cancellationToken); Deletes.Add(entry); }
    }
}

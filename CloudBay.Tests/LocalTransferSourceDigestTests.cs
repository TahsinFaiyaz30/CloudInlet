using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudBay.Core;
using CloudBay.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

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
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CloudBaySourceDigest-" + Guid.NewGuid().ToString("N"));
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

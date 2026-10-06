using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.OneDrive;
using CloudBay.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class TransferBoundarySchedulingTests
{
    [DataTestMethod]
    [DataRow("local", "b2")]
    [DataRow("b2", "local")]
    [DataRow("local", "onedrive")]
    [DataRow("onedrive", "local")]
    [DataRow("onedrive", "b2")]
    [DataRow("b2", "onedrive")]
    public async Task SlowReconciliationDoesNotOccupyTheOnlyPayloadWorker(string sourceProvider, string destinationProvider)
    {
        await using var fixture = new PipelineFixture(sourceProvider, destinationProvider);
        fixture.Destination.HoldReconciliation = true;
        var run = fixture.Engine.RunAsync(fixture.Plan.Id);
        try
        {
            await fixture.Destination.ReconciliationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Destination.SecondPayload.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(fixture.Destination.Uploaded.ContainsKey("first"));
            Assert.IsTrue(fixture.Destination.Uploaded.ContainsKey("second"));
        }
        finally { fixture.Destination.Release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(2L, fixture.Engine.Snapshots().Single().CompletedFiles);
    }

    [DataTestMethod]
    [DataRow("local", "b2")]
    [DataRow("b2", "local")]
    [DataRow("local", "onedrive")]
    [DataRow("onedrive", "local")]
    [DataRow("onedrive", "b2")]
    [DataRow("b2", "onedrive")]
    public async Task SlowVerificationDoesNotHoldTheNextFileInAnyDirection(string sourceProvider, string destinationProvider)
    {
        await using var fixture = new PipelineFixture(sourceProvider, destinationProvider);
        fixture.Destination.HoldVerification = true;
        var run = fixture.Engine.RunAsync(fixture.Plan.Id);
        try
        {
            await fixture.Destination.VerificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Destination.SecondPayload.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, fixture.Destination.Uploaded.Count);
            Assert.IsFalse(run.IsCompleted);
        }
        finally { fixture.Destination.Release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
    }

    [TestMethod]
    public async Task CancellingReconciliationSettlesEveryWorkerAndRetainsTheQueueForResume()
    {
        await using var fixture = new PipelineFixture("onedrive", "b2");
        fixture.Destination.HoldReconciliation = true;
        var run = fixture.Engine.RunAsync(fixture.Plan.Id);
        await fixture.Destination.ReconciliationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Destination.SecondPayload.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.PauseAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        await run;
        Assert.AreEqual(0, fixture.Destination.ActiveReconciliations);
        fixture.Destination.HoldReconciliation = false;
        await fixture.Engine.ResumeAsync(fixture.Plan.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TransferJobState.Completed, fixture.Engine.Snapshots().Single().State);
        Assert.AreEqual(1, fixture.Destination.Uploaded["second"], "A saved completed copy cannot be replayed.");
    }

    [TestMethod]
    public async Task LocalDestinationUsesOneContinuousSourceRangeAcrossDurableChunks()
    {
        using var fixture = new LocalFixture();
        var source = new ReplaySource(new byte[12 * 1024 * 1024 + 17]);
        var checkpoints = new List<long>();
        var receipt = await fixture.Endpoint.UploadAsync(fixture.Request, source, null, (value, _) =>
        { checkpoints.Add(value.AcknowledgedBytes); return Task.CompletedTask; });
        CollectionAssert.AreEqual(new[] { (0L, source.Entry.Size) }, source.Ranges.ToArray());
        Assert.IsTrue(checkpoints.Contains(4 * 1024 * 1024L));
        Assert.IsTrue(checkpoints.Contains(8 * 1024 * 1024L));
        Assert.IsTrue(checkpoints.Contains(12 * 1024 * 1024L));
        Assert.AreEqual(source.Entry.Sha1, receipt.Sha1);
        await fixture.Endpoint.VerifyAsync(receipt, source);
    }

    [TestMethod]
    public async Task InterruptedContinuousLocalCopyReopensOnlyTheUnfinishedSuffix()
    {
        using var fixture = new LocalFixture();
        var source = new ReplaySource(new byte[9 * 1024 * 1024 + 13]);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source, null, (value, _) =>
        {
            saved = value;
            if (value.AcknowledgedBytes == 4 * 1024 * 1024L) throw new OperationCanceledException();
            return Task.CompletedTask;
        }));
        Assert.IsNotNull(saved);
        var restarted = new LocalTransferEndpoint(fixture.Endpoint.Location);
        var receipt = await restarted.UploadAsync(fixture.Request, source, saved, (_, _) => Task.CompletedTask);
        CollectionAssert.AreEqual(new[] { (0L, source.Entry.Size), (4 * 1024 * 1024L, source.Entry.Size - 4 * 1024 * 1024L) }, source.Ranges.ToArray());
        await restarted.VerifyAsync(receipt, source);
    }

    [TestMethod]
    public async Task DamagedSavedLocalChunkStopsBeforeOpeningAnotherSourceConnection()
    {
        using var fixture = new LocalFixture();
        var source = new ReplaySource(new byte[9 * 1024 * 1024]);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source, null, (value, _) =>
        {
            saved = value;
            if (value.AcknowledgedBytes == 4 * 1024 * 1024L) throw new OperationCanceledException();
            return Task.CompletedTask;
        }));
        var partial = Directory.GetFiles(fixture.DirectoryPath, "*.part").Single();
        using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None)) file.WriteByte(123);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Endpoint.UploadAsync(fixture.Request, source, saved, (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, source.Ranges.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DirectoryPath, "item.bin")));
    }

    [TestMethod]
    public async Task KnownOneDriveParentOverlapsIndependentSourceAndDestinationPreflight()
    {
        var source = new GatedSource();
        var lookup = NewSignal();
        var mutations = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            { lookup.TrySetResult(); return Json(new { error = new { code = "itemNotFound" } }, HttpStatusCode.NotFound); }
            Interlocked.Increment(ref mutations);
            CollectionAssert.AreEqual("payload"u8.ToArray(), await request.Content!.ReadAsByteArrayAsync(token));
            return Json(new { id = "created", name = "item.bin", size = source.Entry.Size, eTag = "v1", file = new { hashes = new { sha1Hash = source.Entry.Sha1 } } });
        }));
        var endpoint = OneDrive(http);
        var run = endpoint.UploadAsync(new(new string('a', 64), "item.bin", TransferConflictPolicy.Fail), source, null, (_, _) => Task.CompletedTask);
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await lookup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, mutations, "Parallel preflight cannot mutate an unvalidated destination.");
        }
        finally { source.Release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, mutations);
    }

    [DataTestMethod]
    [DataRow("item.bin")]
    [DataRow("missing-parent/item.bin")]
    public async Task FailedOneDriveSourcePreflightNeverCreatesAParentOrUploads(string path)
    {
        var source = new GatedSource { Changed = true };
        source.Release.TrySetResult();
        var mutations = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref mutations);
            return Task.FromResult(Json(new { error = new { code = "itemNotFound" } }, HttpStatusCode.NotFound));
        }));
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => OneDrive(http).UploadAsync(
            new(new string('a', 64), path, TransferConflictPolicy.Fail), source, null, (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, mutations);
    }

    private static OneDriveTransferEndpoint OneDrive(HttpClient http) => new(new(new("00000000-0000-0000-0000-000000000001",
        tokens: new("token", "refresh", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"), http: http), http),
        new("onedrive", "account", "drive", "root", "", "OneDrive"));
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }

    private sealed class ReplaySource(byte[] bytes) : ITransferSourceFile
    {
        public TransferEntry Entry { get; } = new("source", "item.bin", "v1", bytes.Length, DateTimeOffset.UnixEpoch,
            Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
        public List<(long Offset, long Length)> Ranges { get; } = [];
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Ranges.Add((offset, length)); return Task.FromResult<Stream>(new MemoryStream(bytes, (int)offset, (int)length, false)); }
        public Task ValidateAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
    private sealed class GatedSource : ITransferSourceFile
    {
        private readonly ReplaySource _source = new("payload"u8.ToArray());
        public TransferEntry Entry => _source.Entry;
        public bool Changed;
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public async Task ValidateAsync(CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); if (Changed) throw new TransferSourceChangedException("Source changed."); }
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default) => _source.OpenReadAsync(offset, length, cancellationToken);
    }
    private sealed class LocalFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CloudBayContinuousDownload-" + Guid.NewGuid().ToString("N"));
        public LocalTransferEndpoint Endpoint { get; }
        public TransferUploadRequest Request { get; } = new(Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(), "item.bin", TransferConflictPolicy.Fail);
        public LocalFixture() { Directory.CreateDirectory(DirectoryPath); Endpoint = new(LocalTransferEndpoint.ForFolder(DirectoryPath)); }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    private sealed class PipelineFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudBayBoundaryPipeline-" + Guid.NewGuid().ToString("N"));
        public Endpoint Source { get; }
        public Endpoint Destination { get; }
        public TransferJobEngine Engine { get; }
        public TransferJobPlan Plan { get; }
        public PipelineFixture(string sourceProvider, string destinationProvider)
        {
            Directory.CreateDirectory(_directory);
            Source = new(Location(sourceProvider, "source")); Destination = new(Location(destinationProvider, "destination"));
            Engine = new(new(Path.Combine(_directory, "jobs.sqlite"), new Protector()), location => location == Source.Location ? Source : Destination, 1);
            Plan = new(Guid.NewGuid().ToString("N"), Source.Location, Destination.Location, TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            Engine.CreateAsync(Plan).GetAwaiter().GetResult();
        }
        private TransferLocation Location(string provider, string role) => provider == "local"
            ? LocalTransferEndpoint.ForFolder(Path.Combine(_directory, role))
            : new(provider, role, provider == "b2" ? "bucket" : "drive", provider == "b2" ? "" : "folder", provider == "b2" ? role + "/" : "", role);
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); Directory.Delete(_directory, true); }
    }
    private sealed class Protector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
    }
    private sealed class Endpoint(TransferLocation location) : ITransferEndpoint
    {
        public TransferLocation Location { get; } = location;
        public bool HoldReconciliation, HoldVerification;
        public int ActiveReconciliations;
        public ConcurrentDictionary<string, int> Uploaded { get; } = new();
        public TaskCompletionSource ReconciliationEntered { get; } = NewSignal();
        public TaskCompletionSource VerificationEntered { get; } = NewSignal();
        public TaskCompletionSource SecondPayload { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new TransferFolderPage([], null));
        public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransferDiscoveryPage([Entry("first"), Entry("second")], null));
        private static TransferEntry Entry(string id) => new(id, id + ".txt", "v1", 7, DateTimeOffset.UnixEpoch,
            Convert.ToHexString(SHA1.HashData("payload"u8.ToArray())).ToLowerInvariant());
        public ITransferSourceFile OpenSource(TransferEntry entry) => new Source(entry);
        public async Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ActiveReconciliations);
            try
            {
                if (source.Entry.Id == "first" && HoldReconciliation)
                { ReconciliationEntered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
                return null;
            }
            finally { Interlocked.Decrement(ref ActiveReconciliations); }
        }
        public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint,
            Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await source.ValidateAsync(cancellationToken);
            await using var input = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
            await input.CopyToAsync(Stream.Null, cancellationToken);
            Uploaded.AddOrUpdate(source.Entry.Id, 1, (_, count) => count + 1);
            if (source.Entry.Id == "second") SecondPayload.TrySetResult();
            progress?.Report(new(source.Entry.Size, source.Entry.Size));
            return new("receipt-" + source.Entry.Id, request.RelativePath, "v1", source.Entry.Size, source.Entry.Sha1, request.OperationId);
        }
        public async Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
        {
            if (source.Entry.Id == "first" && HoldVerification)
            { VerificationEntered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
        }
        public Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private sealed class Source(TransferEntry entry) : ITransferSourceFile
        {
            public TransferEntry Entry { get; } = entry;
            public Task ValidateAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream("payload"u8.ToArray(), (int)offset, (int)length, false));
        }
    }
}

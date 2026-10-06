using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

/// <summary>Opt-in, real HTTPS acceptance. Credentials come only from existing Windows DPAPI stores.</summary>
[TestClass]
public sealed class OneDriveProviderAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("LiveProvider")]
    public async Task RealOneDriveAndB2RoundTripRecoveryIntegrityAndTinyThroughput()
    {
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_ONEDRIVE_ACCEPTANCE") != "1")
            Assert.Inconclusive("Opt in with CLOUDINLET_RUN_ONEDRIVE_ACCEPTANCE=1 after connecting OneDrive and installing restricted B2 validation credentials. No provider requests were made.");
        var driveId = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_ONEDRIVE_DRIVE_ID");
        if (string.IsNullOrWhiteSpace(driveId)) Assert.Inconclusive("Set CLOUDINLET_ACCEPTANCE_ONEDRIVE_DRIVE_ID to the reviewed OneDrive drive identity.");
        var storage = new ClientStorage(Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_CLIENT_DATA"));
        var accountId = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_ONEDRIVE_ACCOUNT_ID");
        var accounts = storage.LoadOneDriveConnections();
        var connected = accountId is null && accounts.Count == 1 ? accounts.Single() : accounts.SingleOrDefault(account => account.Id == accountId);
        if (connected is null) Assert.Inconclusive("Connect OneDrive in CloudInlet first, and select its saved account identity when multiple accounts exist.");
        var b2CredentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        if (!File.Exists(b2CredentialPath)) Assert.Inconclusive("Install a B2 application key restricted to one test bucket in CloudInlet's existing Validation credential store first.");
        var credentialsBytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(b2CredentialPath), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        B2Credentials credentials;
        try { credentials = JsonSerializer.Deserialize<B2Credentials>(credentialsBytes) ?? throw new InvalidDataException("The B2 validation credential store is empty."); }
        finally { CryptographicOperations.ZeroMemory(credentialsBytes); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = deadline.Token;
        using var b2 = new B2CloudStore();
        var b2Account = await b2.ConnectAsync(credentials, ct);
        var buckets = await b2.ListBucketsAsync(ct);
        if (buckets.Count != 1 || b2Account.AllowedBucketId != buckets[0].Id)
            Assert.Inconclusive("Live acceptance requires an application key restricted to exactly one test bucket; no test payload was written.");
        var bucket = buckets[0];
        using var transport = new AcceptanceHandler();
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromMinutes(10) };
        var gate = new SemaphoreSlim(1, 1);
        OneDriveClient CreateClient()
        {
            var saved = storage.LoadOneDriveConnections().Single(account => account.Id == connected!.Id);
            return new(new(saved.ClientId, saved.Tenant, saved.Tokens, async (tokens, cancellationToken) =>
            {
                await gate.WaitAsync(cancellationToken);
                try { storage.SaveOneDriveConnections(storage.LoadOneDriveConnections().Select(account => account.Id == saved.Id ? account with { Tokens = tokens } : account).ToArray()); }
                finally { gate.Release(); }
            }, http), http);
        }
        var client = CreateClient();
        var rootId = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_ONEDRIVE_FOLDER_ID") ?? (await client.GetRootAsync(driveId!, ct)).Id;
        var run = Guid.NewGuid().ToString("N");
        var sourceFolder = await client.EnsureFolderAsync(driveId!, rootId, "CloudInlet acceptance source " + run, ct);
        var destinationFolder = await client.EnsureFolderAsync(driveId!, rootId, "CloudInlet acceptance destination " + run, ct);
        var recoveryFolder = await client.EnsureFolderAsync(driveId!, rootId, "CloudInlet acceptance recovery " + run, ct);
        var sourceLocation = new TransferLocation("onedrive", connected!.Id, driveId!, sourceFolder.Id, "", "OneDrive acceptance source");
        var destinationLocation = sourceLocation with { FolderId = destinationFolder.Id, DisplayName = "OneDrive acceptance destination" };
        var recoveryLocation = sourceLocation with { FolderId = recoveryFolder.Id, DisplayName = "OneDrive acceptance recovery" };
        var b2Location = new TransferLocation("b2", b2Account.AccountId, bucket.Id, "", (b2Account.AllowedNamePrefix ?? "") + "CloudInletGraphAcceptance/" + run + "/", "B2 acceptance namespace");
        var artifacts = Path.Combine(TestContext.TestRunDirectory!, "CloudInlet-provider-acceptance-" + run);
        Directory.CreateDirectory(artifacts);
        var marker = Encoding.ASCII.GetBytes("CLOUDINLET-CLOUD-PAYLOAD-" + run + "-");
        var files = Enumerable.Range(0, 64).Select(index => (Name: $"tiny-{index:D3}.bin", Size: 4096))
            .Concat(Enumerable.Range(0, 4).Select(index => (Name: $"mixed-{index:D3}.bin", Size: 256 * 1024)))
            .Append((Name: "large.bin", Size: 12 * 1024 * 1024 + 123)).ToArray();
        var expected = new ConcurrentDictionary<string, string>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (file, token) =>
        {
            var bytes = new byte[file.Size];
            for (var offset = 0; offset < bytes.Length; offset += marker.Length)
                marker.AsSpan(0, Math.Min(marker.Length, bytes.Length - offset)).CopyTo(bytes.AsSpan(offset));
            expected[file.Name] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            await client.PutSmallAsync(driveId!, sourceFolder.Id, file.Name, bytes, "fail", null, token);
        });
        var journalPath = Path.Combine(artifacts, "transfer-jobs.sqlite");
        ITransferEndpoint Resolve(TransferLocation location) => location.Provider == "b2" ? new B2TransferEndpoint(b2, location) : new OneDriveTransferEndpoint(client, location);
        var outwardPlan = Plan(sourceLocation, b2Location);
        var outward = Stopwatch.StartNew();
        using (var journal = new TransferJobJournal(journalPath, new DpapiProtector()))
        await using (var engine = new TransferJobEngine(journal, Resolve, 4))
        {
            await engine.CreateAsync(outwardPlan, ct);
            await engine.RunAsync(outwardPlan.Id, ct);
            var result = engine.Snapshots().Single(snapshot => snapshot.Plan.Id == outwardPlan.Id);
            Assert.AreEqual(TransferJobState.Completed, result.State, result.Error);
            Assert.AreEqual((long)files.Length, result.CompletedFiles);
        }
        outward.Stop();
        // Reopening the durable journal must restore complete progress without any provider access or discovery.
        using (var journal = new TransferJobJournal(journalPath, new DpapiProtector()))
        await using (var restored = new TransferJobEngine(journal, Resolve, 4))
        {
            var before = transport.RequestCount;
            await restored.ResumeAsync(outwardPlan.Id, ct);
            Assert.AreEqual(before, transport.RequestCount);
            Assert.AreEqual((long)files.Length, restored.Snapshots().Single().CompletedFiles);
        }
        transport.ResetMetrics();
        var reversePlan = Plan(b2Location, destinationLocation);
        var reverse = Stopwatch.StartNew();
        using (var journal = new TransferJobJournal(journalPath, new DpapiProtector()))
        await using (var engine = new TransferJobEngine(journal, Resolve, 4))
        {
            await engine.CreateAsync(reversePlan, ct);
            await engine.RunAsync(reversePlan.Id, ct);
            var result = engine.Snapshots().Single(snapshot => snapshot.Plan.Id == reversePlan.Id);
            Assert.AreEqual(TransferJobState.Completed, result.State, result.Error);
            Assert.AreEqual((long)files.Length, result.CompletedFiles);
        }
        reverse.Stop();
        var metrics = transport.Metrics();
        var b2Endpoint = new B2TransferEndpoint(b2, b2Location);
        var b2Files = await DiscoverAllAsync(b2Endpoint, ct);
        var large = b2Files.Single(entry => entry.RelativePath == "large.bin");
        var recoveryEndpoint = new OneDriveTransferEndpoint(client, recoveryLocation);
        var largeRequest = new TransferUploadRequest(Guid.NewGuid().ToString("N"), "resumable.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? checkpoint = null;
        var sessionUrls = new HashSet<string>(StringComparer.Ordinal);
        var checkpointPath = Path.Combine(artifacts, "resume.dpapi");
        async Task Save(TransferCheckpoint value, CancellationToken token)
        {
            checkpoint = value;
            if (!string.IsNullOrEmpty(value.SessionId)) sessionUrls.Add(value.SessionId);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(value);
            try { await File.WriteAllBytesAsync(checkpointPath, new DpapiProtector().Protect(serialized), token); }
            finally { CryptographicOperations.ZeroMemory(serialized); }
        }
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => recoveryEndpoint.UploadAsync(largeRequest, b2Endpoint.OpenSource(large), null,
            async (value, token) => { await Save(value, token); if (value.AcknowledgedBytes >= OneDriveTransferEndpoint.FragmentBytes) throw new OperationCanceledException(); }, cancellationToken: ct));
        Assert.AreEqual((long)OneDriveTransferEndpoint.FragmentBytes, checkpoint!.AcknowledgedBytes);
        var decrypted = new DpapiProtector().Unprotect(await File.ReadAllBytesAsync(checkpointPath, ct));
        try { checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(decrypted)!; }
        finally { CryptographicOperations.ZeroMemory(decrypted); }
        client = CreateClient();
        recoveryEndpoint = new OneDriveTransferEndpoint(client, recoveryLocation);
        transport.DropFinalResponse = true;
        var recovered = await recoveryEndpoint.UploadAsync(largeRequest, b2Endpoint.OpenSource(large), checkpoint, Save, cancellationToken: ct);
        await recoveryEndpoint.VerifyAsync(recovered, b2Endpoint.OpenSource(large), ct);
        Assert.IsTrue(transport.FinalResponseDropped, "The live final-response loss injection was not exercised.");
        var recoveryFiles = await DiscoverAllAsync(recoveryEndpoint, ct);
        Assert.AreEqual(1, recoveryFiles.Count, "A lost final acknowledgment must not create another destination file.");
        Assert.AreEqual(expected["large.bin"], recovered.Sha1);
        var expiredRequest = new TransferUploadRequest(Guid.NewGuid().ToString("N"), "expired-session.bin", TransferConflictPolicy.Fail);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => recoveryEndpoint.UploadAsync(expiredRequest, b2Endpoint.OpenSource(large), null,
            async (value, token) => { await Save(value, token); if (value.AcknowledgedBytes >= OneDriveTransferEndpoint.FragmentBytes) throw new OperationCanceledException(); }, cancellationToken: ct));
        await client.CancelUploadSessionAsync(checkpoint!.SessionId, ct);
        Assert.IsNull(await client.GetUploadSessionAsync(checkpoint.SessionId, ct), "A revoked provider session must be detected before resume.");
        var expiredRecovery = await new OneDriveTransferEndpoint(CreateClient(), recoveryLocation).UploadAsync(expiredRequest,
            b2Endpoint.OpenSource(large), checkpoint, Save, cancellationToken: ct);
        await recoveryEndpoint.VerifyAsync(expiredRecovery, b2Endpoint.OpenSource(large), ct);
        Assert.AreEqual(expected["large.bin"], expiredRecovery.Sha1);
        var corrupt = recovered with { Sha1 = new string('0', 40) };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => recoveryEndpoint.VerifyAsync(corrupt, b2Endpoint.OpenSource(large), ct));
        var tiny = b2Files.Single(entry => entry.RelativePath == "tiny-000.bin");
        var destination = new OneDriveTransferEndpoint(client, destinationLocation);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => destination.UploadAsync(new("conflict", tiny.RelativePath, TransferConflictPolicy.Fail), b2Endpoint.OpenSource(tiny), null, Save, cancellationToken: ct));
        await Assert.ThrowsExceptionAsync<TransferSkippedException>(() => destination.UploadAsync(new("skip", tiny.RelativePath, TransferConflictPolicy.Skip), b2Endpoint.OpenSource(tiny), null, Save, cancellationToken: ct));
        var originalDestination = (await DiscoverAllAsync(destination, ct)).Single(entry => entry.RelativePath == tiny.RelativePath);
        var tinySource = b2Endpoint.OpenSource(tiny);
        var replaced = await destination.UploadAsync(new(Guid.NewGuid().ToString("N"), tiny.RelativePath, TransferConflictPolicy.Replace), tinySource, null, Save, cancellationToken: ct);
        await destination.VerifyAsync(replaced, tinySource, ct);
        Assert.AreEqual(originalDestination.Id, replaced.Id, "Replace must retain the existing OneDrive item identity.");
        Assert.AreNotEqual(originalDestination.Version, replaced.Version, "The provider must acknowledge a new destination version after Replace.");
        var renameRequest = new TransferUploadRequest(Guid.NewGuid().ToString("N"), tiny.RelativePath, TransferConflictPolicy.Rename);
        TransferCheckpoint? renamedCheckpoint = null;
        var renamed = await destination.UploadAsync(renameRequest, tinySource, null,
            async (value, token) => { renamedCheckpoint = value; await Save(value, token); }, cancellationToken: ct);
        await destination.VerifyAsync(renamed, tinySource, ct);
        Assert.AreNotEqual(tiny.RelativePath, renamed.RelativePath, "Rename must choose a distinct target when the original path exists.");
        var retainedOriginal = await client.GetItemAsync(driveId!, replaced.Id, ct);
        Assert.AreEqual(replaced.Version, retainedOriginal.ETag, "Rename must retain the original item's exact unchanged version.");
        var reconciledRename = await new OneDriveTransferEndpoint(client, destinationLocation).ReconcileAsync(renameRequest, tinySource, renamedCheckpoint, ct);
        Assert.AreEqual(renamed.Id, reconciledRename?.Id, "A restored Rename receipt must resolve to the same destination identity.");
        var absentRenameName = "rename-without-conflict.bin";
        var absentRename = await destination.UploadAsync(new(Guid.NewGuid().ToString("N"), absentRenameName, TransferConflictPolicy.Rename), tinySource, null, Save, cancellationToken: ct);
        await destination.VerifyAsync(absentRename, tinySource, ct);
        Assert.AreEqual(absentRenameName, absentRename.RelativePath, "Rename must preserve the requested path when no conflict exists.");
        var conflictFiles = await DiscoverAllAsync(destination, ct);
        Assert.AreEqual(files.Length + 2, conflictFiles.Count, "Replace and restored Rename must not create duplicate destination objects.");
        Assert.AreEqual(1, conflictFiles.Count(entry => entry.RelativePath == tiny.RelativePath));
        Assert.AreEqual(1, conflictFiles.Count(entry => entry.Id == renamed.Id));
        Assert.AreEqual(1, conflictFiles.Count(entry => entry.Id == absentRename.Id));
        var moveFolder = await client.EnsureFolderAsync(driveId!, rootId, "CloudInlet acceptance move " + run, ct);
        var moveGraphLocation = sourceLocation with { FolderId = moveFolder.Id, DisplayName = "OneDrive Move proof" };
        var moveB2Location = b2Location with { Path = b2Location.Path + "move-proof/", DisplayName = "B2 Move proof" };
        var moveBytes = new byte[4096];
        for (var offset = 0; offset < moveBytes.Length; offset += marker.Length)
            marker.AsSpan(0, Math.Min(marker.Length, moveBytes.Length - offset)).CopyTo(moveBytes.AsSpan(offset));
        var moveHash = Convert.ToHexString(SHA1.HashData(moveBytes)).ToLowerInvariant();
        var moveSeed = await client.PutSmallAsync(driveId!, moveFolder.Id, "move-proof.bin", moveBytes, "fail", null, ct);
        var outwardMove = Plan(moveGraphLocation, moveB2Location, TransferOperation.Move);
        await RunMoveAsync(outwardMove);
        var deletedGraph = await Assert.ThrowsExceptionAsync<OneDriveApiException>(() => client.GetItemAsync(driveId!, moveSeed.Id, ct));
        Assert.AreEqual(404, deletedGraph.StatusCode, "Move must remove only the exact saved OneDrive source identity.");
        Assert.AreEqual(0, (await DiscoverAllAsync(Resolve(moveGraphLocation), ct)).Count);
        var b2Moved = await VerifyMovedDestinationAsync(outwardMove);
        var reverseMove = Plan(moveB2Location, moveGraphLocation, TransferOperation.Move);
        await RunMoveAsync(reverseMove);
        Assert.IsTrue(await Resolve(moveB2Location).IsSourceDeletedAsync(b2Moved, ct), "Move must remove the exact verified B2 source version.");
        Assert.AreEqual(0, (await DiscoverAllAsync(Resolve(moveB2Location), ct)).Count);
        await VerifyMovedDestinationAsync(reverseMove);
        async Task RunMoveAsync(TransferJobPlan movePlan)
        {
            using var journal = new TransferJobJournal(journalPath, new DpapiProtector());
            await using var engine = new TransferJobEngine(journal, Resolve, 4);
            await engine.CreateAsync(movePlan, ct);
            await engine.RunAsync(movePlan.Id, ct);
            var moved = engine.Snapshots().Single(snapshot => snapshot.Plan.Id == movePlan.Id);
            Assert.AreEqual(TransferJobState.Completed, moved.State, moved.Error);
            Assert.AreEqual(1L, moved.CompletedFiles);
            Assert.AreEqual(0L, moved.RemainingBytes);
        }
        async Task<TransferEntry> VerifyMovedDestinationAsync(TransferJobPlan movePlan)
        {
            TransferReceipt receipt;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journalPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT receipt,verified,delete_started,state FROM transfer_items WHERE job_id=$job";
                command.Parameters.AddWithValue("$job", movePlan.Id);
                using var reader = command.ExecuteReader(); Assert.IsTrue(reader.Read());
                receipt = JsonSerializer.Deserialize<TransferReceipt>(reader.GetString(0))!;
                Assert.IsTrue(reader.GetBoolean(1), "The durable Move receipt must be verified before source deletion.");
                Assert.IsTrue(reader.GetBoolean(2), "The source-deletion intent must be durable.");
                Assert.AreEqual((int)TransferItemState.Completed, reader.GetInt32(3));
                Assert.IsFalse(reader.Read(), "The one-file Move must not duplicate queue work.");
            }
            var endpoint = Resolve(movePlan.Destination);
            var copied = (await DiscoverAllAsync(endpoint, ct)).Single();
            Assert.AreEqual(receipt.Id, copied.Id); Assert.AreEqual(receipt.Version, copied.Version);
            await using var copiedStream = await endpoint.OpenSource(copied).OpenReadAsync(0, copied.Size, ct);
            Assert.AreEqual(moveHash, Convert.ToHexString(await SHA1.HashDataAsync(copiedStream, ct)).ToLowerInvariant(), "The moved destination must contain independently verified bytes.");
            return copied;
        }
        var originals = await DiscoverAllAsync(new OneDriveTransferEndpoint(client, sourceLocation), ct);
        var changed = originals.Single(entry => entry.RelativePath == "tiny-000.bin");
        await client.PutSmallAsync(driveId!, sourceFolder.Id, changed.RelativePath, "source changed during verification"u8.ToArray(), "replace", changed.Version, ct);
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => new OneDriveTransferEndpoint(client, sourceLocation).DeleteSourceAsync(changed, ct));
        Assert.IsNotNull(await client.GetItemAsync(driveId!, changed.Id, ct), "Changed source content must survive an attempted Move deletion.");
        foreach (var file in Directory.EnumerateFiles(artifacts))
        {
            var saved = await File.ReadAllBytesAsync(file, ct);
            Assert.IsFalse(saved.AsSpan().IndexOf(marker) >= 0, "A cloud payload marker reached local progress storage: " + Path.GetFileName(file));
            Assert.IsFalse(sessionUrls.Any(url => Encoding.UTF8.GetString(saved).Contains(url, StringComparison.Ordinal)), "A signed session URL reached plaintext storage.");
        }
        var total = files.Sum(file => (long)file.Size);
        var report = new { files = files.Length, payloadBytes = total, oneDriveToB2Seconds = outward.Elapsed.TotalSeconds,
            b2ToOneDriveSeconds = reverse.Elapsed.TotalSeconds, oneDriveToB2VerifiedBytesPerSecond = total / outward.Elapsed.TotalSeconds,
            b2ToOneDriveVerifiedBytesPerSecond = total / reverse.Elapsed.TotalSeconds, graph = metrics,
            recovery = "5 MiB acknowledged checkpoint reloaded; remaining bytes resumed; final response dropped and reconciled; revoked session restarted only unfinished file",
            payloadDiskCheck = "Unique payload marker absent from all job/checkpoint files; adapter transport uses RAM streams only",
            sourceChange = "Changed exact source identity retained",
            conflicts = "Fail and Skip rejected; Replace verified with a new version; Rename retained the original and reconciled the exact new identity; absent Rename kept its requested name",
            conflictDestinationFiles = conflictFiles.Count,
            moves = "OneDrive to B2 and B2 to OneDrive durable Move jobs completed; verified receipts persisted before exact source deletion; independent destination SHA-1 and no duplicate object proved",
            providerLimits = "Graph fragments sequential; provider RTT, throttling and independent checksum readback remain overhead" };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        TestContext.WriteLine(JsonSerializer.Serialize(report));
        TestContext.WriteLine("Metadata report: " + Path.Combine(artifacts, "report.json"));
        // Delete only exact identities in this run's unique namespaces after every verification succeeds.
        foreach (var location in new[] { sourceLocation, destinationLocation, recoveryLocation, moveGraphLocation })
        {
            var endpoint = new OneDriveTransferEndpoint(client, location);
            foreach (var entry in await DiscoverAllAsync(endpoint, ct)) await endpoint.DeleteSourceAsync(entry, ct);
        }
        foreach (var entry in b2Files) await b2Endpoint.DeleteSourceAsync(entry, ct);
        // Empty OneDrive acceptance folders are retained as reviewable run markers; no recursive delete is performed.
    }

    private static TransferJobPlan Plan(TransferLocation source, TransferLocation destination, TransferOperation operation = TransferOperation.Copy) =>
        new(Guid.NewGuid().ToString("N"), source, destination, operation, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
    private static async Task<List<TransferEntry>> DiscoverAllAsync(ITransferEndpoint endpoint, CancellationToken ct)
    {
        var entries = new List<TransferEntry>(); string? cursor = null;
        do { var page = await endpoint.DiscoverAsync(cursor, ct); entries.AddRange(page.Entries); cursor = page.NextCursor; } while (cursor is not null);
        return entries;
    }
    private sealed class DpapiProtector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, "CloudBay.ProviderAcceptance.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, "CloudBay.ProviderAcceptance.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    }
    private sealed class AcceptanceHandler : DelegatingHandler
    {
        private readonly ConcurrentBag<(long Start, long End, long Size)> _smallPayloads = [];
        private int _drop;
        public long RequestCount;
        public bool FinalResponseDropped { get; private set; }
        public bool DropFinalResponse { set => Interlocked.Exchange(ref _drop, value ? 1 : 0); }
        public AcceptanceHandler() : base(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 32 }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref RequestCount);
            var start = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Put && request.RequestUri!.Host == "graph.microsoft.com" && request.RequestUri.AbsolutePath.EndsWith("/content") &&
                request.Content?.Headers.ContentLength is > 0 and <= OneDriveTransferEndpoint.SmallFileBytes)
                _smallPayloads.Add((start, Stopwatch.GetTimestamp(), request.Content.Headers.ContentLength.Value));
            var range = request.Content?.Headers.ContentRange;
            if (response.IsSuccessStatusCode && request.Method == HttpMethod.Put && range is not null && range.To + 1 == range.Length && Interlocked.CompareExchange(ref _drop, 0, 1) == 1)
            { FinalResponseDropped = true; response.Dispose(); throw new HttpRequestException("Acceptance injection: final response lost after provider acknowledgment."); }
            return response;
        }
        public void ResetMetrics() { while (_smallPayloads.TryTake(out _)) { } RequestCount = 0; }
        public object Metrics()
        {
            var intervals = _smallPayloads.OrderBy(value => value.Start).ToArray();
            var gaps = new List<double>(); long end = 0;
            foreach (var interval in intervals)
            { if (end > 0) gaps.Add(Math.Max(0, interval.Start - end) * 1000d / Stopwatch.Frequency); end = Math.Max(end, interval.End); }
            var elapsed = intervals.Length == 0 ? 0 : (intervals.Max(value => value.End) - intervals[0].Start) / (double)Stopwatch.Frequency;
            var tiny = intervals.Where(value => value.Size == 4096).ToArray();
            var tinyElapsed = tiny.Length == 0 ? 0 : (tiny.Max(value => value.End) - tiny[0].Start) / (double)Stopwatch.Frequency;
            var orderedGaps = gaps.Order().ToArray();
            return new { httpRequests = RequestCount, smallPayloadRequests = intervals.Length,
                smallFilesPerSecond = elapsed == 0 ? 0 : intervals.Length / elapsed,
                tinyPayloadRequests = tiny.Length, tinyPayloadBytes = tiny.Sum(value => value.Size),
                tinyPayloadWindowSeconds = tinyElapsed, tinyFilesPerSecondWithinMixedWorkload = tinyElapsed == 0 ? 0 : tiny.Length / tinyElapsed,
                aggregatePayloadIdleGapMillisecondsMax = gaps.Count == 0 ? 0 : gaps.Max(),
                aggregatePayloadIdleGapMillisecondsMean = gaps.Count == 0 ? 0 : gaps.Average(),
                aggregatePayloadIdleGapMillisecondsP95 = orderedGaps.Length == 0 ? 0 : orderedGaps[(int)Math.Ceiling(orderedGaps.Length * .95) - 1],
                measurement = "Graph small PUT intervals within the mixed workload; zero-byte reservations excluded; gaps may overlap large-file or verification work" };
        }
    }
}

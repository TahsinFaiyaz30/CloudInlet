using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

/// <summary>Real production 5GB multipart boundary, using only one 50MB RAM seed and server-side copies.</summary>
[TestClass]
public sealed partial class CloudPrefixMultipartProviderAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static byte[] Protect(byte[] bytes) => CloudPrefixProviderAcceptanceTests.Protect(bytes);
    private static byte[] Unprotect(byte[] bytes) => CloudPrefixProviderAcceptanceTests.Unprotect(bytes);

    [TestMethod]
    [TestCategory("LiveProvider")]
    public async Task RealB2MultipartMigrationResumesOwnedPartsAfterJournalReopen()
    {
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_MULTIPART_ACCEPTANCE") != "1")
            Assert.Inconclusive("Set CLOUDINLET_RUN_B2_MULTIPART_ACCEPTANCE=1 only after reviewing the isolated 5GB server-copy fixture. No requests were sent.");
        var bucketName = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        var evidenceRoot = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(evidenceRoot) ||
            !Path.IsPathFullyQualified(evidenceRoot) || !Directory.Exists(evidenceRoot))
            Assert.Inconclusive("An explicit test bucket and existing absolute artifact directory are required.");
        var credentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        if (!File.Exists(credentialPath)) Assert.Inconclusive("The protected validation credential store is not provisioned.");
        var credentialBytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(credentialPath), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        B2Credentials credential;
        try { credential = JsonSerializer.Deserialize<B2Credentials>(credentialBytes) ?? throw new InvalidDataException("Invalid validation credentials."); }
        finally { CryptographicOperations.ZeroMemory(credentialBytes); }

        var run = Guid.NewGuid();
        var directory = Path.Combine(evidenceRoot!, "cloud-prefix-multipart-acceptance-" + run.ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("Fresh multipart evidence directory already exists.");
        Directory.CreateDirectory(directory);
        var guards = new List<SafeFileHandle>();
        ClientRegistrationMigration.PinAncestors(directory, guards);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        var token = deadline.Token;
        var seed = new byte[CloudPrefixMultipartAcceptanceTransport.SeedLength];
        // Distinct, non-secret, reproducible bytes include the run identity. No payload file is created.
        new Random(run.GetHashCode()).NextBytes(seed);
        var wholeHash = RepeatedHash(seed, 0, CloudPrefixMultipartAcceptanceTransport.LargeLength);
        var hashes = Enumerable.Range(0, checked((int)((CloudPrefixMultipartAcceptanceTransport.LargeLength + CloudPrefixMultipartAcceptanceTransport.MigrationPartLength - 1) /
            CloudPrefixMultipartAcceptanceTransport.MigrationPartLength))).Select(index => RepeatedHash(seed,
                index * CloudPrefixMultipartAcceptanceTransport.MigrationPartLength,
                Math.Min(CloudPrefixMultipartAcceptanceTransport.MigrationPartLength,
                    CloudPrefixMultipartAcceptanceTransport.LargeLength - index * CloudPrefixMultipartAcceptanceTransport.MigrationPartLength))).ToArray();
        using var transport = new CloudPrefixMultipartAcceptanceTransport(directory, seed, wholeHash, hashes);
        using var store = new B2CloudStore(transport);
        var phase = "preflight";
        var succeeded = false;
        var interrupted = false;
        var reopened = false;
        string? failureType = null, failureStack = null, recoveredLargeId = null, inconclusiveReason = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        var completeReports = 0;
        void Progress(string operation, int? current = null, int? total = null)
        {
            using var file = new FileStream(Path.Combine(directory, "progress.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
            file.Write(JsonSerializer.SerializeToUtf8Bytes(new { utc = DateTimeOffset.UtcNow, phase, operation, current, total }));
            file.WriteByte((byte)'\n'); file.Flush(flushToDisk: true);
            TestContext.WriteLine(operation + (current is not null ? $" ({current}/{total})" : ""));
        }
        try
        {
            Progress("Inspecting isolated multipart test destination");
            var account = await store.ConnectAsync(credential, token); credential = null!;
            var buckets = await store.ListBucketsAsync(token);
            if (buckets.Count != 1 || account.AllowedBucketId != buckets[0].Id || buckets[0].Name != bucketName)
                Assert.Inconclusive("Multipart acceptance requires a key restricted to exactly the named test bucket.");
            var missing = CloudPrefixAcceptanceScope.MissingCapabilities(account);
            if (missing.Length != 0) Assert.Inconclusive("Protection capability checks are incomplete: " + string.Join(", ", missing));
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run);
            transport.Scope = scope;
            var empty = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            scope.RequirePolicy(empty);
            if (Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
                Assert.Inconclusive("Read-only multipart preflight finished. Writes remain disabled.");
            scope.EnableWrites(account, empty);
            phase = "upload-50mb-ram-seed"; Progress("Uploading bounded RAM seed");
            using (var input = new MemoryStream(seed, writable: false))
                await store.UploadAsync(scope.Bucket, scope.Source + "seed.bin", input, seed.Length, transport.SeedHash,
                    new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), cancellationToken: token);
            var seedId = scope.Owned.Single().Id;
            transport.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token), allowOwnedUnfinished: false);
            phase = "construct-real-5gb-source"; Progress("Starting exact 5GB source from server-side seed copies", 0, 100);
            using (await transport.FixturePostAsync("b2_start_large_file", new
            {
                bucketId = scope.Bucket, fileName = scope.Source + "multipart.bin", contentType = "application/octet-stream",
                fileInfo = new Dictionary<string, string> { ["large_file_sha1"] = wholeHash, ["src_last_modified_millis"] = "1704164645000" },
                serverSideEncryption = new { mode = empty.DefaultEncryptionMode, algorithm = empty.DefaultEncryptionAlgorithm }
            }, token)) { }
            for (var part = 1; part <= 100; part++)
            {
                transport.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token), allowOwnedUnfinished: true);
                using (await transport.FixturePostAsync("b2_copy_part", new
                {
                    sourceFileId = seedId, largeFileId = transport.SourceLargeId, partNumber = part,
                    range = "bytes=0-49999999"
                }, token)) { }
                Progress("Verified server-side source part", part, 100);
            }
            transport.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token), allowOwnedUnfinished: true);
            using (await transport.FixturePostAsync("b2_finish_large_file", new
            { fileId = transport.SourceLargeId, partSha1Array = Enumerable.Repeat(transport.SeedHash, 100).ToArray() }, token)) { }
            var before = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            transport.RequirePolicy(before, allowOwnedUnfinished: false);
            Assert.AreEqual(2, before.SourceVersions.Count);
            Assert.AreEqual(CloudPrefixMultipartAcceptanceTransport.LargeLength, before.SourceVersions.Single(value => value.Key.EndsWith("/multipart.bin", StringComparison.Ordinal)).Size);
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Isolated real multipart acceptance", scope.Source, scope.Destination, "Retain original fixture history")]);
            var copyPath = Path.Combine(directory, "copies.dpapi");
            var journalPath = Path.Combine(directory, "migration.dpapi");
            phase = "interrupt-after-two-durable-multipart-parts";
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var copies = new ProtectedB2CloudPrefixCopyJournal(copyPath, Protect, Unprotect))
            using (var journal = new ProtectedCloudPrefixMigrationJournal(journalPath, Protect, Unprotect))
            {
                var observed = new StopAfterPartsJournal(copies, stop, 2, Progress);
                var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, observed);
                var binding = new MultipartFixtureBinding(directory, plan.Fingerprint);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
                try { await operation.ApplyAsync(new CallbackProgress(value => Progress(value.Operation)), stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested) { interrupted = true; }
                Assert.IsTrue(interrupted, "The controlled cancellation must follow two durable part acknowledgments.");
                var intent = copies.Read()!.Intents.Single(value => value.Source.Size == CloudPrefixMultipartAcceptanceTransport.LargeLength);
                Assert.AreEqual(B2CloudPrefixCopyPhase.CopyingParts, intent.Phase);
                Assert.AreEqual(2, intent.PartHashes!.Count(value => value is not null));
                recoveredLargeId = intent.LargeFileId;
                Assert.AreEqual(transport.DestinationLargeId, recoveredLargeId);
                Assert.AreEqual(2, transport.DestinationPartRequests);
                Assert.IsTrue(journal.Read()!.Entries.Any(entry => entry.RequestPending));
                await binding.VerifyOriginalAsync(token);
            }
            var paused = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            transport.RequirePolicy(paused, allowOwnedUnfinished: true);
            Assert.AreEqual(JsonSerializer.Serialize(before.SourceVersions), JsonSerializer.Serialize(paused.SourceVersions));
            Assert.AreEqual(1, paused.UnfinishedUploads.Count);
            phase = "reopen-and-complete-multipart";
            using (var copies = new ProtectedB2CloudPrefixCopyJournal(copyPath, Protect, Unprotect))
            using (var journal = new ProtectedCloudPrefixMigrationJournal(journalPath, Protect, Unprotect))
            {
                var saved = copies.Read()!.Intents.Single(value => value.Source.Size == CloudPrefixMultipartAcceptanceTransport.LargeLength);
                Assert.AreEqual(recoveredLargeId, saved.LargeFileId);
                Assert.AreEqual(2, saved.PartHashes!.Count(value => value is not null));
                reopened = true; Progress("Reopened authenticated journals and retained the same unfinished file ID");
                var observed = new ProgressPartsJournal(copies, Progress);
                var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, observed);
                var binding = new MultipartFixtureBinding(directory, plan.Fingerprint);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
                await operation.ApplyAsync(new CallbackProgress(value =>
                { Progress(value.Operation); if (value.Operation == "Cloud folder migrated") completeReports++; }), token);
                await operation.VerifyAsync(token);
                Assert.AreEqual(1, completeReports);
                Assert.AreEqual(CloudPrefixMigrationPhase.Switched, journal.Read()!.Phase);
                var completed = copies.Read()!.Intents.Single(value => value.Source.Size == CloudPrefixMultipartAcceptanceTransport.LargeLength);
                Assert.AreEqual(B2CloudPrefixCopyPhase.Completed, completed.Phase);
                Assert.AreEqual(recoveredLargeId, completed.Completed!.FileId);
                CollectionAssert.AreEqual(hashes, completed.PartHashes!.ToArray());
                Assert.AreEqual(hashes.Length + transport.ReplayedUnconfirmedParts, transport.DestinationPartRequests,
                    "Retry may replay only an identical unconfirmed part, never an acknowledged part.");
                var after = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
                transport.RequirePolicy(after, allowOwnedUnfinished: false);
                Assert.AreEqual(JsonSerializer.Serialize(before.SourceVersions), JsonSerializer.Serialize(after.SourceVersions));
                Assert.AreEqual(2, after.DestinationVersions.Count);
                Assert.AreEqual(4, scope.Owned.Count);
                Assert.AreEqual(wholeHash, completed.Completed.Sha1);
                Assert.AreEqual(CloudPrefixMultipartAcceptanceTransport.LargeLength, completed.Completed.Size);
                foreach (var mapping in operation.VersionMappings)
                {
                    Assert.AreEqual(mapping.Original.EncryptionMode, mapping.Migrated.EncryptionMode);
                    Assert.AreEqual(mapping.Original.EncryptionAlgorithm, mapping.Migrated.EncryptionAlgorithm);
                    foreach (var pair in mapping.Original.Metadata) Assert.AreEqual(pair.Value, mapping.Migrated.Metadata[pair.Key]);
                }
                scope.EnableCleanup(after);
            }
            phase = "exact-owned-cleanup";
            foreach (var owned in scope.Owned.ToArray())
            {
                transport.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token), allowOwnedUnfinished: false);
                using (await transport.FixturePostAsync("b2_delete_file_version", new { fileId = owned.Id, fileName = owned.Key }, token)) { }
                Progress("Removed one exact acknowledged test version", transport.Deleted.Count, scope.Owned.Count);
            }
            var clean = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            transport.RequirePolicy(clean, allowOwnedUnfinished: false);
            Assert.AreEqual(0, clean.SourceVersions.Count + clean.DestinationVersions.Count + clean.UnfinishedUploads.Count);
            phase = "complete"; succeeded = true; Progress("Real multipart migration, durable resume, and exact cleanup completed");
        }
        catch (AssertInconclusiveException error) { inconclusiveReason = error.Message; throw; }
        catch (Exception error)
        {
            failureType = error.GetType().FullName; failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("Multipart acceptance stopped at " + phase + " (" + error.GetType().Name + "). Exact receipts were retained; unknown outcomes were not retried or broadly cleaned.");
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
                {
                    run, phase, succeeded, failureType, failureStack, providerFailure, inconclusiveReason, interrupted, reopened, recoveredLargeId, completeReports,
                    fixtureNamespace = transport.Scope?.Root, transport.SourceLargeId, transport.DestinationLargeId,
                    transport.Mutations, transport.SourcePartRequests, transport.DestinationPartRequests, transport.ReplayedUnconfirmedParts, transport.UploadedPayloadBytes,
                    owned = transport.Scope?.Owned, unfinished = transport.Unfinished, deleted = transport.Deleted, transport.HasUnconfirmedMutation,
                    wholeHash, migrationPartHashes = hashes, payloadDiskWrites = 0, payloadDownloads = 0,
                    scope = "real 5GB server-side construction and production multipart threshold; controlled stop after two durable parts; authenticated journal reopen in same process; isolated binding"
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddResultFile(Path.Combine(directory, "report.json"));
            }
            finally { CryptographicOperations.ZeroMemory(seed); foreach (var guard in guards) guard.Dispose(); }
        }
    }

    internal static string RepeatedHash(byte[] seed, long offset, long length)
    {
        if (seed.Length == 0 || offset < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        while (length != 0)
        {
            var start = checked((int)(offset % seed.Length));
            var count = checked((int)Math.Min(length, seed.Length - start));
            hash.AppendData(seed, start, count); offset += count; length -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class CallbackProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => report(value); }
    private sealed class StopAfterPartsJournal(IB2CloudPrefixCopyJournal inner, CancellationTokenSource stop, int count,
        Action<string, int?, int?> progress) : IB2CloudPrefixCopyJournal
    {
        public B2CloudPrefixCopyCheckpoint? Read() => inner.Read();
        public void Write(B2CloudPrefixCopyCheckpoint checkpoint)
        {
            inner.Write(checkpoint);
            var part = checkpoint.Intents.SingleOrDefault(value => value.Source.Size == CloudPrefixMultipartAcceptanceTransport.LargeLength);
            if (part?.Phase != B2CloudPrefixCopyPhase.CopyingParts) return;
            var hashes = part.PartHashes ?? throw new InvalidDataException("A multipart copy checkpoint is missing part hashes.");
            var done = hashes.Count(value => value is not null);
            progress("Durably recorded multipart copy progress", done, hashes.Count);
            if (done == count) stop.Cancel();
        }
    }
    private sealed class ProgressPartsJournal(IB2CloudPrefixCopyJournal inner, Action<string, int?, int?> progress) : IB2CloudPrefixCopyJournal
    {
        public B2CloudPrefixCopyCheckpoint? Read() => inner.Read();
        public void Write(B2CloudPrefixCopyCheckpoint checkpoint)
        {
            inner.Write(checkpoint);
            var part = checkpoint.Intents.SingleOrDefault(value => value.Source.Size == CloudPrefixMultipartAcceptanceTransport.LargeLength);
            if (part?.PartHashes is { } hashes) progress("Durably recorded multipart copy progress", hashes.Count(value => value is not null), hashes.Count);
        }
    }
    private sealed class MultipartFixtureBinding(string directory, string fingerprint) : ICloudPrefixMigrationBinding
    {
        private readonly string _path = Path.Combine(directory, "binding.dpapi");
        public string MaintenanceReceipt => fingerprint;
        public Task VerifyMaintenanceAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task VerifyOriginalAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Assert.IsFalse(File.Exists(_path)); return Task.CompletedTask; }
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bytes = JsonSerializer.SerializeToUtf8Bytes(mappings);
            try { using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(Protect(bytes)); file.Flush(true); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return Task.CompletedTask;
        }
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bytes = Unprotect(File.ReadAllBytes(_path));
            try { CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(mappings), bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return Task.CompletedTask;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

/// <summary>A second independently authenticated writer must invalidate the reviewed source history.</summary>
[TestClass]
public sealed class CloudPrefixExternalWriterAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("LiveProvider")]
    public async Task ExternalSourceVersionStopsRetryWithoutMergingDeletingOrSwitching()
    {
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_MIGRATION_ACCEPTANCE") != "1")
            Assert.Inconclusive("Explicit isolated B2 migration acceptance is required. No requests were sent.");
        var bucketName = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        var evidenceRoot = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(evidenceRoot) ||
            !Path.IsPathFullyQualified(evidenceRoot) || !Directory.Exists(evidenceRoot))
            Assert.Inconclusive("An expected isolated bucket and absolute durable evidence directory are required.");
        if (Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
            Assert.Inconclusive("External-writer acceptance requires explicit mutation mode after read-only preflight.");

        var run = Guid.NewGuid();
        var directory = Path.Combine(evidenceRoot!, "cloud-prefix-external-writer-acceptance-" + run.ToString("N"));
        Assert.IsFalse(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var transport = new CloudPrefixAcceptanceTransport(directory);
        using var store = new B2CloudStore(transport);
        var guards = new List<SafeFileHandle>();
        var stage = "preflight";
        var succeeded = false;
        var visibleConflict = false;
        var independentWriterConnected = false;
        var bindingSwitched = false;
        string? failureType = null, failureStack = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        try
        {
            ClientRegistrationMigration.PinAncestors(directory, guards);
            var account = await store.ConnectAsync(ReadCredentials(), token);
            var buckets = await store.ListBucketsAsync(token);
            Assert.AreEqual(1, buckets.Count);
            Assert.AreEqual(bucketName, buckets[0].Name);
            Assert.AreEqual(buckets[0].Id, account.AllowedBucketId);
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run);
            transport.Scope = scope;
            scope.EnableWrites(account, await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            async Task<CloudObject> Upload(B2CloudStore writer, string name, string content)
            {
                scope.RequireRemainingInventory(await writer.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                var bytes = Encoding.UTF8.GetBytes(content + " " + run.ToString("N"));
                using var input = new MemoryStream(bytes, false);
                var uploaded = await writer.UploadAsync(scope.Bucket, scope.Source + name, input, bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), cancellationToken: token);
                payloads.Add(uploaded.FileId, bytes);
                return uploaded;
            }

            stage = "seed-isolated-history";
            await Upload(store, "a-first.txt", "Reviewed first object");
            await Upload(store, "z-next.txt", "Reviewed next object");
            var before = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(2, before.SourceVersions.Count);
            Assert.AreEqual(0, before.DestinationVersions.Count);
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Isolated writer conflict acceptance",
                scope.Source, scope.Destination, "Keep every original and externally written version")]);
            var copyPath = Path.Combine(directory, "copies.dpapi");
            var migrationPath = Path.Combine(directory, "migration.dpapi");
            var sessionPath = Path.Combine(directory, "session.json");
            CloudPrefixVersion acknowledged;
            using (var pause = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var copies = Copies(copyPath))
            using (var journal = Journal(migrationPath))
            {
                var binding = new OriginalBinding(plan.Fingerprint, () => bindingSwitched = true);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                    store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
                using var session = MigrationSession.Create(plan, ["cloud"], sessionPath, [operation]);
                session.ProgressChanged += value =>
                {
                    TestContext.WriteLine("Migration before external write: " + value.Operation);
                    if (value.Operation == "Verified cloud version" && value.Completed == 1) pause.Cancel();
                };
                stage = "pause-after-first-acknowledged-copy";
                await session.RunAsync(pause.Token);
                Assert.IsTrue(pause.IsCancellationRequested && !token.IsCancellationRequested);
                Assert.AreEqual(MigrationItemState.Paused, session.Items.Single().State);
                Assert.AreEqual(CloudPrefixMigrationPhase.Copying, journal.Read()!.Phase);
                Assert.IsTrue(journal.Read()!.Entries.All(value => !value.RequestPending));
                acknowledged = journal.Read()!.Entries.Single(value => value.Destination is not null).Destination!;
                Assert.AreEqual(scope.Destination + "a-first.txt", acknowledged.Key);
                Assert.AreEqual(3, transport.Mutations);
                await binding.VerifyOriginalAsync(token);
            }

            stage = "independent-writer-adds-source-version";
            CloudObject external;
            // Borrow only the scoped network guard and durable ownership ledger. Authentication,
            // HTTP client, provider session and upload endpoint belong to this independent store.
            using (var writer = new B2CloudStore(new BorrowedTransport(transport)))
            {
                var writerAccount = await writer.ConnectAsync(ReadCredentials(), token);
                Assert.AreEqual(account.AccountId, writerAccount.AccountId);
                Assert.AreEqual(scope.Bucket, writerAccount.AllowedBucketId);
                independentWriterConnected = true;
                external = await Upload(writer, "a-first.txt", "New value from independent writer");
            }
            Assert.AreEqual(4, transport.Mutations);
            var changed = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(3, changed.SourceVersions.Count);
            Assert.AreEqual(1, changed.DestinationVersions.Count);
            Assert.AreEqual(acknowledged.FileId, changed.DestinationVersions.Single().FileId);
            Assert.AreEqual(external.FileId, changed.SourceVersions.First(value => value.Key == external.Key).FileId);
            foreach (var original in before.SourceVersions)
                Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(changed.SourceVersions.Single(value => value.FileId == original.FileId)));

            stage = "reopened-retry-refuses-changed-source";
            using (var copies = Copies(copyPath))
            using (var journal = Journal(migrationPath))
            {
                var binding = new OriginalBinding(plan.Fingerprint, () => bindingSwitched = true);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                    store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
                using var session = MigrationSession.Open(sessionPath, [operation]);
                await session.RetryAsync("cloud", token);
                var status = session.Items.Single();
                Assert.AreEqual(MigrationItemState.Failed, status.State);
                StringAssert.Contains(status.Error!, "Cloud source versions or their protection changed");
                visibleConflict = !string.IsNullOrWhiteSpace(status.Error);
                Assert.IsFalse(session.IsComplete);
                Assert.AreEqual(CloudPrefixMigrationPhase.Copying, journal.Read()!.Phase);
                Assert.AreEqual(2, journal.Read()!.Entries.Count, "The external version must not be silently added to the reviewed checkpoint.");
                Assert.IsFalse(journal.Read()!.Entries.Any(value => value.Source.FileId == external.FileId));
                Assert.AreEqual(acknowledged.FileId, operation.VersionMappings.Single().Migrated.FileId);
                Assert.IsTrue(journal.Read()!.Entries.All(value => !value.RequestPending));
                await binding.VerifyOriginalAsync(token);
                Assert.IsTrue(session.CanKeepOriginal("cloud"));
                var entries = JsonSerializer.Serialize(journal.Read()!.Entries);
                await session.KeepOriginalAsync("cloud", token);
                await session.VerifyKeptOriginalsAsync(token);
                Assert.AreEqual(MigrationItemState.OriginalKept, session.Items.Single().State);
                Assert.AreEqual(CloudPrefixMigrationPhase.OriginalKept, journal.Read()!.Phase);
                Assert.AreEqual(entries, JsonSerializer.Serialize(journal.Read()!.Entries));
                Assert.IsFalse(session.IsComplete);
            }
            Assert.IsFalse(bindingSwitched);
            Assert.AreEqual(4, transport.Mutations, "A changed source must stop retry before any further copy, hide or delete.");
            var afterRefusal = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(JsonSerializer.Serialize(changed.SourceVersions), JsonSerializer.Serialize(afterRefusal.SourceVersions));
            Assert.AreEqual(JsonSerializer.Serialize(changed.DestinationVersions), JsonSerializer.Serialize(afterRefusal.DestinationVersions));
            foreach (var version in afterRefusal.SourceVersions)
            {
                using var output = new MemoryStream();
                await store.DownloadAsync(new(version.FileId, version.Key, version.Size, version.Sha1,
                    DateTimeOffset.FromUnixTimeMilliseconds(version.UploadedAtMillis)), output, cancellationToken: token);
                CollectionAssert.AreEqual(payloads[version.FileId], output.ToArray());
            }

            // This is fixture teardown after the expected safe refusal, not migration recovery.
            // The second writer's exact acknowledgment belongs to this test ledger only.
            stage = "exact-owned-fixture-cleanup";
            scope.EnableCleanup(afterRefusal);
            foreach (var owned in scope.RemainingOwned.ToArray())
            {
                scope.RequireRemainingInventory(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                using var deleted = await transport.PostAsync("b2_delete_file_version", new { fileId = owned.Id, fileName = owned.Key }, token);
                Assert.AreEqual(owned.Id, deleted.RootElement.GetProperty("fileId").GetString());
                Assert.AreEqual(owned.Key, deleted.RootElement.GetProperty("fileName").GetString());
            }
            var empty = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(0, empty.SourceVersions.Count + empty.DestinationVersions.Count + empty.UnfinishedUploads.Count);
            Assert.AreEqual(8, transport.Mutations);
            Assert.AreEqual(0, scope.RemainingOwned.Count);
            stage = "complete";
            succeeded = true;
        }
        catch (Exception error)
        {
            failureType = error.GetType().FullName; failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("Isolated external-writer acceptance stopped at " + stage + ". Exact fixture and recovery metadata were retained; no broad cleanup ran.");
        }
        finally
        {
            try
            {
                var path = Path.Combine(directory, "report.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    run, stage, succeeded, independentWriterConnected, visibleConflict, bindingSwitched,
                    transport.Mutations, transport.PayloadDownloads, payloadDiskWrites = 0,
                    fixtureNamespace = transport.Scope?.Root, ownedVersions = transport.Scope?.Owned.Count ?? 0,
                    remainingOwnedVersions = transport.Scope?.RemainingOwned.Count ?? 0, failureType, failureStack, providerFailure,
                    scope = "independent authenticated writer adds one source version during a durable pause; isolated binding, no desktop state"
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddResultFile(path);
            }
            finally { foreach (var guard in guards) guard.Dispose(); }
        }
    }

    private static B2Credentials ReadCredentials()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<B2Credentials>(plain) ?? throw new InvalidDataException("Validation credentials are invalid."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static ProtectedB2CloudPrefixCopyJournal Copies(string path) =>
        new(path, CloudPrefixProviderAcceptanceTests.Protect, CloudPrefixProviderAcceptanceTests.Unprotect);
    private static ProtectedCloudPrefixMigrationJournal Journal(string path) =>
        new(path, CloudPrefixProviderAcceptanceTests.Protect, CloudPrefixProviderAcceptanceTests.Unprotect);

    private sealed class BorrowedTransport(HttpMessageHandler owner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(owner, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _invoker.SendAsync(request, token);
        protected override void Dispose(bool disposing) { if (disposing) _invoker.Dispose(); base.Dispose(disposing); }
    }
    private sealed class OriginalBinding(string fingerprint, Action switched) : ICloudPrefixMigrationBinding
    {
        public string MaintenanceReceipt => fingerprint;
        public Task VerifyMaintenanceAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task VerifyOriginalAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        { switched(); throw new AssertFailedException("A changed source must never switch application references."); }
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token) =>
            throw new AssertFailedException("A changed source must never report switched application references.");
    }
}

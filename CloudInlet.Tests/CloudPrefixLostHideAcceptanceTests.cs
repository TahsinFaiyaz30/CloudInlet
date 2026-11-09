using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

[TestClass]
public sealed class CloudPrefixLostHideAcceptanceTests
{
    [TestMethod, TestCategory("LiveProvider")]
    public async Task LostHideResponseRetainsUnknownMarkerAndKeepsVerifiedOriginalLocation()
    {
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_MIGRATION_ACCEPTANCE") != "1" ||
            Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
            Assert.Inconclusive("Explicit isolated B2 mutation acceptance is required.");
        var root = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        var bucket = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(bucket))
            Assert.Inconclusive("Expected test bucket and existing absolute evidence root are required.");
        var run = Guid.NewGuid();
        var directory = Path.Combine(root!, "cloud-prefix-lost-hide-acceptance-" + run.ToString("N"));
        Assert.IsFalse(Directory.Exists(directory)); Directory.CreateDirectory(directory);
        var guards = new List<SafeFileHandle>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var transport = new CloudPrefixAcceptanceTransport(directory);
        using var cloud = new B2CloudStore(transport);
        var stage = "preflight"; var succeeded = false; var keptOriginal = false; var visibleFailure = false;
        string? failureType = null, failureStack = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        try
        {
            ClientRegistrationMigration.PinAncestors(directory, guards);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            B2Credentials credential;
            try { credential = JsonSerializer.Deserialize<B2Credentials>(plain)!; }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var account = await cloud.ConnectAsync(credential, token); credential = null!;
            var buckets = await cloud.ListBucketsAsync(token);
            Assert.AreEqual(1, buckets.Count); Assert.AreEqual(bucket, buckets[0].Name); Assert.AreEqual(buckets[0].Id, account.AllowedBucketId);
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run); transport.Scope = scope;
            scope.EnableWrites(account, await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
            stage = "seed-hidden-history";
            var bytes = System.Text.Encoding.UTF8.GetBytes("Preserve hidden history " + run.ToString("N"));
            using (var input = new MemoryStream(bytes, false))
                await cloud.UploadAsync(scope.Bucket, scope.Source + "hidden.txt", input, bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), cancellationToken: token);
            scope.RequireRemainingInventory(await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
            using (await transport.PostAsync("b2_hide_file", new { bucketId = scope.Bucket, fileName = scope.Source + "hidden.txt" }, token)) { }
            var original = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(2, original.SourceVersions.Count);
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Lost hide response acceptance", scope.Source, scope.Destination, "Retain originals and partial copies")]);
            var sessionPath = Path.Combine(directory, "session.json");
            var historyPath = Path.Combine(directory, "history.dpapi");
            var copyPath = Path.Combine(directory, "copies.dpapi");
            ProtectedCloudPrefixMigrationJournal History() => new(historyPath, CloudPrefixProviderAcceptanceTests.Protect, CloudPrefixProviderAcceptanceTests.Unprotect);
            ProtectedB2CloudPrefixCopyJournal Copies() => new(copyPath, CloudPrefixProviderAcceptanceTests.Protect, CloudPrefixProviderAcceptanceTests.Unprotect);
            var binding = new OriginalBinding(plan.Fingerprint);
            stage = "lose-committed-hide-response";
            using (var history = History())
            using (var copies = Copies())
            {
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                    cloud.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, history);
                using var session = MigrationSession.Create(plan, ["cloud"], sessionPath, [operation]);
                transport.ArmOneHideAcknowledgmentLoss();
                await session.RunAsync(token);
                Assert.IsNotNull(transport.DroppedHideAcknowledgment);
                Assert.AreEqual(MigrationItemState.Failed, session.Items.Single().State);
                visibleFailure = !string.IsNullOrWhiteSpace(session.Items.Single().Error);
                Assert.IsTrue(visibleFailure);
                Assert.AreEqual("hide", history.Read()!.Entries.Single(value => value.RequestPending).Source.Action);
                Assert.AreEqual(0, copies.Read()!.HideReceipts?.Count ?? 0, "Test ledger must not become a protected production receipt.");
            }
            Assert.AreEqual(4, transport.Mutations);
            var afterFault = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(JsonSerializer.Serialize(original.SourceVersions), JsonSerializer.Serialize(afterFault.SourceVersions));
            Assert.AreEqual(2, afterFault.DestinationVersions.Count);
            stage = "reopen-refuse-guess-and-keep-original";
            using (var history = History())
            using (var copies = Copies())
            {
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                    cloud.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, history);
                using var session = MigrationSession.Open(sessionPath, [operation]);
                await session.RetryAsync("cloud", token);
                Assert.AreEqual(MigrationItemState.Failed, session.Items.Single().State);
                Assert.AreEqual(4, transport.Mutations, "Retry must not replay or adopt the unacknowledged hide.");
                var entries = JsonSerializer.Serialize(history.Read()!.Entries);
                Assert.IsTrue(session.CanKeepOriginal("cloud"));
                await session.KeepOriginalAsync("cloud", token); await session.VerifyKeptOriginalsAsync(token);
                Assert.AreEqual(MigrationItemState.OriginalKept, session.Items.Single().State);
                Assert.AreEqual(entries, JsonSerializer.Serialize(history.Read()!.Entries));
                Assert.AreEqual(CloudPrefixMigrationPhase.OriginalKept, history.Read()!.Phase);
                Assert.AreEqual(0, copies.Read()!.HideReceipts?.Count ?? 0);
                Assert.IsFalse(session.IsComplete); keptOriginal = true;
            }
            var retained = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(JsonSerializer.Serialize(afterFault.SourceVersions), JsonSerializer.Serialize(retained.SourceVersions));
            Assert.AreEqual(JsonSerializer.Serialize(afterFault.DestinationVersions), JsonSerializer.Serialize(retained.DestinationVersions));
            Assert.AreEqual(4, transport.Mutations);
            // Independent test teardown only: the guard observed and durably recorded the provider's
            // real success response before hiding it from production. Production never receives this ledger.
            stage = "exact-test-owned-cleanup"; scope.EnableCleanup(retained);
            foreach (var owned in scope.RemainingOwned.ToArray())
            {
                scope.RequireRemainingInventory(await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                using (await transport.PostAsync("b2_delete_file_version", new { fileId = owned.Id, fileName = owned.Key }, token)) { }
            }
            var empty = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(0, empty.SourceVersions.Count + empty.DestinationVersions.Count + empty.UnfinishedUploads.Count);
            Assert.AreEqual(8, transport.Mutations); stage = "complete"; succeeded = true;
        }
        catch (Exception error)
        {
            failureType = error.GetType().FullName; failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("Lost hide acceptance stopped at " + stage + ". Exact isolated fixture and journals retained.");
        }
        finally
        {
            try { await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
            { run, stage, succeeded, keptOriginal, visibleFailure, failureType, failureStack, providerFailure, transport.Mutations,
                transport.DroppedHideAcknowledgment, remainingOwnedVersions = transport.Scope?.RemainingOwned.Count,
                payloadDiskWrites = 0, scope = "Injected lost real B2 hide response; protected recovery declines ownership and explicitly retains original binding. Independent exact fixture teardown after verification." },
                new JsonSerializerOptions { WriteIndented = true })); }
            finally { foreach (var guard in guards) guard.Dispose(); }
        }
    }
    private sealed class OriginalBinding(string fingerprint) : ICloudPrefixMigrationBinding
    {
        public string MaintenanceReceipt => fingerprint;
        public Task VerifyMaintenanceAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task VerifyOriginalAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token) => throw new AssertFailedException("Ambiguous hide must never switch.");
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token) => throw new AssertFailedException("Ambiguous hide must never complete.");
    }
}

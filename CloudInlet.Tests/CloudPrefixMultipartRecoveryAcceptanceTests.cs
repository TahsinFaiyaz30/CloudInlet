using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

public sealed partial class CloudPrefixMultipartProviderAcceptanceTests
{
    [TestMethod, TestCategory("LiveProvider")]
    public async Task RealB2MultipartResumesPreviouslyAuthenticatedFixture()
    {
        var hasRun = Guid.TryParseExact(Environment.GetEnvironmentVariable("CLOUDINLET_B2_MULTIPART_RESUME_RUN"), "N", out var run);
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_MULTIPART_ACCEPTANCE") != "1" || !hasRun || run == Guid.Empty)
            Assert.Inconclusive("An explicit existing multipart fixture ID and opt-in are required.");
        if (Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
            Assert.Inconclusive("Multipart recovery remains disabled in read-only mode.");
        var root = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        var expectedBucket = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(expectedBucket))
            Assert.Inconclusive("Existing absolute evidence root and explicitly named test bucket are required.");
        var directory = Path.Combine(root!, "cloud-prefix-multipart-acceptance-" + run.ToString("N"));
        if (!Directory.Exists(directory)) throw new IOException("Existing protected multipart evidence is required.");
        var recovery = Path.Combine(directory, "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recovery);
        var guards = new List<SafeFileHandle>();
        var stage = "authenticate-saved-fixture";
        var succeeded = false;
        string? failureType = null, failureStack = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        var acknowledgmentsBefore = 0;
        var seed = new byte[CloudPrefixMultipartAcceptanceTransport.SeedLength];
        new Random(run.GetHashCode()).NextBytes(seed);
        var wholeHash = RepeatedHash(seed, 0, CloudPrefixMultipartAcceptanceTransport.LargeLength);
        var hashes = Enumerable.Range(0, 48).Select(index => RepeatedHash(seed,
            index * CloudPrefixMultipartAcceptanceTransport.MigrationPartLength,
            Math.Min(CloudPrefixMultipartAcceptanceTransport.MigrationPartLength,
                CloudPrefixMultipartAcceptanceTransport.LargeLength - index * CloudPrefixMultipartAcceptanceTransport.MigrationPartLength))).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        var token = deadline.Token;
        using var transport = new CloudPrefixMultipartAcceptanceTransport(recovery, seed, wholeHash, hashes);
        using var cloud = new B2CloudStore(transport);
        try
        {
            ClientRegistrationMigration.PinAncestors(directory, guards);
            using var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect);
            using var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect);
            var before = journal.Read() ?? throw new InvalidDataException("Missing authenticated history.");
            var providerBefore = copies.Read() ?? throw new InvalidDataException("Missing authenticated provider receipts.");
            var credentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
            var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(credentialPath, token), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            B2Credentials credentials;
            try { credentials = JsonSerializer.Deserialize<B2Credentials>(plain) ?? throw new InvalidDataException("Invalid protected test credential."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var account = await cloud.ConnectAsync(credentials, token); credentials = null!;
            var buckets = await cloud.ListBucketsAsync(token);
            Assert.AreEqual(1, buckets.Count); Assert.AreEqual(expectedBucket, buckets[0].Name);
            Assert.AreEqual(buckets[0].Id, account.AllowedBucketId);
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run);
            transport.Scope = scope;
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Isolated real multipart acceptance", scope.Source, scope.Destination, "Retain original fixture history")]);
            Assert.AreEqual(plan.Fingerprint, before.PlanFingerprint);
            Assert.AreEqual(plan.Fingerprint, before.MaintenanceReceipt);
            Assert.AreEqual("cloud", before.ItemId);
            var binding = new MultipartFixtureBinding(directory, plan.Fingerprint);
            await binding.VerifyOriginalAsync(token);
            var current = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            transport.RestoreAuthenticatedMultipart(account, current, before, providerBefore);
            var originalIntent = providerBefore.Intents.Single();
            acknowledgmentsBefore = originalIntent.PartHashes!.Count(value => value is not null);
            Assert.IsTrue(acknowledgmentsBefore > 0 && acknowledgmentsBefore < hashes.Length);
            void Progress(string operation, int? done = null, int? total = null)
            {
                using var file = new FileStream(Path.Combine(recovery, "progress.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(JsonSerializer.SerializeToUtf8Bytes(new { utc = DateTimeOffset.UtcNow, stage, operation, done, total }));
                file.WriteByte((byte)'\n'); file.Flush(true);
            }
            stage = "resume-existing-immutable-large-file";
            var provider = cloud.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, new ProgressPartsJournal(copies, Progress));
            var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
            await operation.ApplyAsync(new CallbackProgress(value => Progress(value.Operation)), token);
            await operation.VerifyAsync(token);
            var finished = copies.Read()!.Intents.Single(value => value.Source.Size == CloudPrefixMultipartAcceptanceTransport.LargeLength);
            Assert.AreEqual(B2CloudPrefixCopyPhase.Completed, finished.Phase);
            Assert.AreEqual(originalIntent.LargeFileId, finished.Completed!.FileId);
            Assert.AreEqual(wholeHash, finished.Completed.Sha1);
            CollectionAssert.AreEqual(hashes, finished.PartHashes!.ToArray());
            Assert.AreEqual(hashes.Length - acknowledgmentsBefore + transport.ReplayedUnconfirmedParts, transport.DestinationPartRequests);
            Assert.AreEqual(0, transport.SourcePartRequests, "Existing source history must not be reconstructed.");
            Assert.AreEqual(CloudPrefixMigrationPhase.Switched, journal.Read()!.Phase);
            var after = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            transport.RequirePolicy(after, allowOwnedUnfinished: false);
            Assert.AreEqual(JsonSerializer.Serialize(current.SourceVersions), JsonSerializer.Serialize(after.SourceVersions));
            Assert.AreEqual(2, after.DestinationVersions.Count); Assert.AreEqual(4, scope.Owned.Count);
            foreach (var mapping in operation.VersionMappings)
            {
                Assert.AreEqual(mapping.Original.EncryptionMode, mapping.Migrated.EncryptionMode);
                Assert.AreEqual(mapping.Original.EncryptionAlgorithm, mapping.Migrated.EncryptionAlgorithm);
                foreach (var pair in mapping.Original.Metadata) Assert.AreEqual(pair.Value, mapping.Migrated.Metadata[pair.Key]);
            }
            stage = "cleanup-exact-authenticated-versions";
            scope.EnableCleanup(after);
            foreach (var owned in scope.RemainingOwned.ToArray())
            {
                transport.RequirePolicy(await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token), allowOwnedUnfinished: false);
                using (await transport.FixturePostAsync("b2_delete_file_version", new { fileId = owned.Id, fileName = owned.Key }, token)) { }
            }
            transport.RequirePolicy(await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token), allowOwnedUnfinished: false);
            Assert.AreEqual(0, scope.RemainingOwned.Count);
            stage = "complete"; succeeded = true; Progress("Existing multipart recovery and exact cleanup completed");
        }
        catch (Exception error)
        {
            failureType = error.GetType().FullName; failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("Existing multipart recovery stopped at " + stage + " (" + error.GetType().Name + "). Protected journals and exact cloud fixture were retained.");
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(recovery, "report.json"), JsonSerializer.Serialize(new
                {
                    run, stage, succeeded, failureType, failureStack, providerFailure, acknowledgmentsBefore,
                    transport.Mutations, transport.DestinationPartRequests, transport.SourcePartRequests, transport.ReplayedUnconfirmedParts,
                    transport.DestinationLargeId, remainingOwned = transport.Scope?.RemainingOwned.Count,
                    payloadDiskWrites = 0, payloadDownloads = 0,
                    scope = "Separate process recovering a real previously interrupted 5GB multipart fixture from protected journals; original immutable destination ID retained"
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { CryptographicOperations.ZeroMemory(seed); foreach (var guard in guards) guard.Dispose(); }
        }
    }
}

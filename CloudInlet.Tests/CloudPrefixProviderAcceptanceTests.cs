using System.Net.Http.Json;
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

/// <summary>Opt-in, bounded history migration and durable retry. Uses an isolated binding, never the user's profile.</summary>
[TestClass]
public sealed class CloudPrefixProviderAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly byte[] Entropy = "CloudInlet.CloudPrefixAcceptance.v1"u8.ToArray();
    internal static byte[] Protect(byte[] value) => ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
    internal static byte[] Unprotect(byte[] value) => ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser);

    [DataTestMethod]
    [DataRow("initial-completion")]
    [DataRow("acknowledged-version-interruption-and-journal-reopen")]
    [DataRow("lost-copy-response-and-journal-reopen")]
    [DataRow("revert-acknowledged-versions-after-journal-reopen")]
    [DataRow("lost-delete-response-and-revert-after-journal-reopen")]
    [TestCategory("LiveProvider")]
    public async Task RealB2PrefixMigrationPreservesHistoryMetadataAndContent(string scenario)
    {
        var interruptAndReopen = scenario == "acknowledged-version-interruption-and-journal-reopen";
        var loseCopyResponse = scenario == "lost-copy-response-and-journal-reopen";
        var loseDeleteResponse = scenario == "lost-delete-response-and-revert-after-journal-reopen";
        var revert = loseDeleteResponse || scenario == "revert-acknowledged-versions-after-journal-reopen";
        Assert.IsTrue(interruptAndReopen || loseCopyResponse || revert || scenario == "initial-completion", "Unknown acceptance scenario.");
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_MIGRATION_ACCEPTANCE") != "1")
            Assert.Inconclusive("Set CLOUDINLET_RUN_B2_MIGRATION_ACCEPTANCE=1 only for a reviewed isolated B2 test bucket. No requests were made.");
        var expectedBucket = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        if (string.IsNullOrWhiteSpace(expectedBucket)) Assert.Inconclusive("Set the non-secret CLOUDINLET_ACCEPTANCE_B2_BUCKET to the reviewed test bucket name.");
        var evidenceRoot = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(evidenceRoot) || !Path.IsPathFullyQualified(evidenceRoot) || !Directory.Exists(evidenceRoot))
            Assert.Inconclusive("Set CLOUDINLET_B2_MIGRATION_ARTIFACTS to an existing absolute evidence directory outside the test runner's temporary directories.");
        var credentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        if (!File.Exists(credentialPath)) Assert.Inconclusive("The existing protected validation credential store is not provisioned.");
        var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(credentialPath), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        B2Credentials credentials;
        try { credentials = JsonSerializer.Deserialize<B2Credentials>(plain) ?? throw new InvalidDataException("Invalid validation credentials."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var run = Guid.NewGuid();
        // MSTest can remove its deployment directory for skipped/inconclusive runs. Recovery journals
        // and the zero-mutation preflight report need an explicit durable location instead.
        var directory = Path.Combine(Path.GetFullPath(evidenceRoot!), "cloud-prefix-acceptance-" + run.ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("The fresh acceptance evidence directory already exists.");
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var transport = new CloudPrefixAcceptanceTransport(directory);
        using var store = new B2CloudStore(transport);
        var guards = new List<SafeFileHandle>();
        var phase = "preflight";
        var succeeded = false;
        var interruptionObserved = false;
        var lostCopyResponseObserved = false;
        var journalsReopened = false;
        var completionReports = 0;
        RevertEvidence? revertEvidence = null;
        string[] acknowledgedBeforeRetry = [];
        string? failureType = null, failureStack = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        try
        {
            ClientRegistrationMigration.PinAncestors(directory, guards);
            var account = await store.ConnectAsync(credentials, token);
            credentials = null!;
            var buckets = await store.ListBucketsAsync(token);
            if (buckets.Count != 1 || account.AllowedBucketId != buckets[0].Id || buckets[0].Name != expectedBucket)
                Assert.Inconclusive("The credential must be restricted to exactly the named test bucket. No mutations were sent.");
            var missing = CloudPrefixAcceptanceScope.MissingCapabilities(account);
            if (missing.Length != 0) Assert.Inconclusive("Protection preflight is incomplete: " + string.Join(", ", missing) + ". No mutations were sent.");
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run);
            transport.Scope = scope;
            var empty = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            if (Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
            {
                scope.RequirePolicy(empty);
                Assert.Inconclusive("Read-only preflight finished. Fixture writes were not enabled; live migration was not executed.");
            }
            scope.EnableWrites(account, empty);
            phase = "create-isolated-history";
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            async Task Seed(string name, string content)
            {
                scope.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                var bytes = Encoding.UTF8.GetBytes(content + " " + run.ToString("N"));
                using var input = new MemoryStream(bytes, writable: false);
                var uploaded = await store.UploadAsync(scope.Bucket, scope.Source + name, input, bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), cancellationToken: token);
                payloads.Add(uploaded.FileId, bytes);
            }
            await Seed("history.txt", "first historical value");
            await Seed("history.txt", "second historical value");
            await Seed("hidden.txt", "hidden retained value");
            scope.RequirePolicy(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
            using (await transport.PostAsync("b2_hide_file", new { bucketId = scope.Bucket, fileName = scope.Source + "hidden.txt" }, token)) { }
            await Seed("nested/visible.txt", "nested visible value");
            var before = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(5, before.SourceVersions.Count);
            Assert.AreEqual(0, before.DestinationVersions.Count);
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Isolated B2 acceptance", scope.Source, scope.Destination, "Retain original fixture history")]);
            var mutationsAfterSeeding = transport.Mutations;
            if (revert)
            {
                phase = "revert-acknowledged-versions";
                revertEvidence = await RevertIsolatedFixtureAsync(directory, store, transport, before, loseDeleteResponse, token);
                var reverted = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
                AssertVersionsEqual(before.SourceVersions, reverted.SourceVersions);
                Assert.AreEqual(0, reverted.DestinationVersions.Count);
                scope.EnableCleanup(reverted);
            }
            if (interruptAndReopen)
            {
                phase = "interrupt-after-acknowledged-versions";
                using var interruption = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect);
                using var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect);
                var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies);
                var binding = new FixtureBinding(directory, plan.Fingerprint);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
                var completionReported = false;
                try
                {
                    await operation.ApplyAsync(new ImmediateProgress(value =>
                    {
                        TestContext.WriteLine("Migration before retry: " + value.Operation);
                        completionReported |= value.Operation == "Cloud folder migrated";
                        // This callback follows both durable acknowledgments. Cancellation is checked
                        // before the next intent, so no request outcome is deliberately made ambiguous.
                        if (value.Operation == "Verified cloud version" && value.Completed == 2)
                            interruption.Cancel();
                    }), interruption.Token);
                }
                catch (OperationCanceledException) when (interruption.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    interruptionObserved = true;
                }
                Assert.IsTrue(interruptionObserved, "The controlled interruption must stop the first attempt.");
                Assert.IsFalse(completionReported, "An interrupted migration must not report completion.");
                var checkpoint = journal.Read()!;
                Assert.AreEqual(CloudPrefixMigrationPhase.Copying, checkpoint.Phase);
                Assert.IsTrue(checkpoint.Entries.All(entry => !entry.RequestPending), "The pause must leave no ambiguous request.");
                acknowledgedBeforeRetry = checkpoint.Entries.Where(entry => entry.Destination is not null)
                    .Select(entry => entry.Destination!.FileId).ToArray();
                Assert.AreEqual(2, acknowledgedBeforeRetry.Length);
                Assert.IsTrue(operation.CanRevert, "The isolated binding must still use the original location.");
                await binding.VerifyOriginalAsync(token);
                var interruptedInventory = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
                AssertVersionsEqual(before.SourceVersions, interruptedInventory.SourceVersions);
                CollectionAssert.AreEquivalent(acknowledgedBeforeRetry, interruptedInventory.DestinationVersions.Select(value => value.FileId).ToArray());
                Assert.AreEqual(mutationsAfterSeeding + 2, transport.Mutations, "Only the two acknowledged destination versions may have been created.");
                var providerReceipts = copies.Read()!;
                Assert.AreEqual(1, providerReceipts.Intents.Count, "The interruption must preserve one completed upload-copy receipt.");
                Assert.AreEqual(B2CloudPrefixCopyPhase.Completed, providerReceipts.Intents[0].Phase);
                Assert.AreEqual(1, providerReceipts.HideReceipts?.Count ?? 0, "The interruption must also preserve the acknowledged hide-marker receipt.");
                CollectionAssert.AreEquivalent(acknowledgedBeforeRetry,
                    providerReceipts.Intents.Where(intent => intent.Completed is not null).Select(intent => intent.Completed!.FileId)
                        .Concat((providerReceipts.HideReceipts ?? []).Select(value => value.FileId)).ToArray());
            }
            if (loseCopyResponse)
            {
                phase = "drop-acknowledged-copy-response";
                using var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect);
                using var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect);
                var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies);
                var binding = new FixtureBinding(directory, plan.Fingerprint);
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
                var completionReported = false;
                transport.ArmOneCopyAcknowledgmentLoss();
                try
                {
                    await operation.ApplyAsync(new ImmediateProgress(value =>
                    {
                        TestContext.WriteLine("Migration before response recovery: " + value.Operation);
                        completionReported |= value.Operation == "Cloud folder migrated";
                    }), token);
                }
                catch (HttpRequestException) when (transport.DroppedCopyAcknowledgment is not null && !token.IsCancellationRequested)
                {
                    lostCopyResponseObserved = true;
                }
                Assert.IsTrue(lostCopyResponseObserved, "The first attempt must expose the injected lost copy response.");
                Assert.IsFalse(completionReported, "An unconfirmed request must not report completion.");
                var checkpoint = journal.Read()!;
                Assert.AreEqual(CloudPrefixMigrationPhase.Copying, checkpoint.Phase);
                Assert.AreEqual(1, checkpoint.Entries.Count(entry => entry.RequestPending));
                Assert.IsTrue(checkpoint.Entries.All(entry => entry.Destination is null), "No production receipt may be fabricated from the test's ownership ledger.");
                var pending = checkpoint.Entries.Single(entry => entry.RequestPending);
                Assert.AreEqual("upload", pending.Source.Action);
                var intent = copies.Read()!.Intents.Single();
                Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, intent.Phase);
                Assert.IsNull(intent.Completed);
                Assert.AreEqual(pending.OperationId, intent.OperationId);
                Assert.AreEqual(pending.Source.FileId, intent.Source.FileId);
                Assert.AreEqual(pending.DestinationKey, intent.DestinationKey);
                Assert.IsTrue(operation.CanRevert, "The original binding must remain active after a lost response.");
                await binding.VerifyOriginalAsync(token);
                var interruptedInventory = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
                AssertVersionsEqual(before.SourceVersions, interruptedInventory.SourceVersions);
                var created = interruptedInventory.DestinationVersions.Single();
                Assert.AreEqual(transport.DroppedCopyAcknowledgment!.Id, created.FileId);
                Assert.AreEqual(transport.DroppedCopyAcknowledgment.Key, created.Key);
                Assert.AreEqual(pending.OperationId, created.Metadata[CloudPrefixMigration.ReceiptMetadataKey]);
                Assert.AreEqual(pending.Source.FileId, created.Metadata[CloudPrefixMigration.SourceMetadataKey]);
                Assert.AreEqual(mutationsAfterSeeding + 1, transport.Mutations, "An ambiguous creating request must not be replayed automatically.");
                Assert.AreEqual(before.SourceVersions.Count + 1, scope.Owned.Count);
                acknowledgedBeforeRetry = [created.FileId];
            }
            // The first attempt and both journal leases are gone. Reconstruct the provider and
            // binding as well as the operation; only the scoped HTTP owner and fixture ledger remain.
            if (!revert)
            {
                phase = interruptAndReopen || loseCopyResponse ? "reopen-protected-journals-and-retry" : "production-migration";
                using (var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect))
                using (var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect))
                {
                    var provider = store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies);
                    var binding = new FixtureBinding(directory, plan.Fingerprint);
                    var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination, provider, binding, journal);
                    if (interruptAndReopen)
                    {
                        Assert.AreEqual(CloudPrefixMigrationPhase.Copying, journal.Read()!.Phase);
                        CollectionAssert.AreEquivalent(acknowledgedBeforeRetry, operation.VersionMappings.Select(value => value.Migrated.FileId).ToArray());
                        var reopenedReceipts = copies.Read()!;
                        CollectionAssert.AreEquivalent(acknowledgedBeforeRetry,
                            reopenedReceipts.Intents.Where(intent => intent.Completed is not null).Select(intent => intent.Completed!.FileId)
                                .Concat((reopenedReceipts.HideReceipts ?? []).Select(value => value.FileId)).ToArray());
                        await binding.VerifyOriginalAsync(token);
                        Assert.AreEqual(mutationsAfterSeeding + 2, transport.Mutations, "Reopening the journals must not create any versions.");
                        journalsReopened = true;
                    }
                    if (loseCopyResponse)
                    {
                        Assert.AreEqual(CloudPrefixMigrationPhase.Copying, journal.Read()!.Phase);
                        Assert.AreEqual(1, journal.Read()!.Entries.Count(entry => entry.RequestPending));
                        Assert.AreEqual(0, operation.VersionMappings.Count, "Reopening must not promote the test ownership ledger into production receipts.");
                        var pending = copies.Read()!.Intents.Single();
                        Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, pending.Phase);
                        Assert.IsNull(pending.Completed);
                        await binding.VerifyOriginalAsync(token);
                        Assert.AreEqual(mutationsAfterSeeding + 1, transport.Mutations, "Reopening pending journals must not replay a copy.");
                        journalsReopened = true;
                    }
                    await operation.ApplyAsync(new ImmediateProgress(value =>
                    {
                        TestContext.WriteLine("Migration: " + value.Operation);
                        if (value.Operation != "Cloud folder migrated") return;
                        completionReports++;
                        Assert.AreEqual((long)before.SourceVersions.Count, value.Completed);
                        Assert.AreEqual((long?)before.SourceVersions.Count, value.Total);
                    }), token);
                    await operation.VerifyAsync(token);
                    Assert.AreEqual(1, completionReports, "Completion must be reported exactly once, after the binding is switched and verified.");
                    Assert.AreEqual(CloudPrefixMigrationPhase.Switched, journal.Read()!.Phase);
                    Assert.IsFalse(operation.CanRevert, "Completed reference switches must remain completed.");
                    var after = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
                    AssertVersionsEqual(before.SourceVersions, after.SourceVersions);
                    Assert.AreEqual(before.SourceVersions.Count, after.DestinationVersions.Count);
                    Assert.AreEqual(mutationsAfterSeeding + before.SourceVersions.Count, transport.Mutations,
                        "Every source version must produce exactly one destination request, including after retry.");
                    Assert.AreEqual(before.SourceVersions.Count * 2, scope.Owned.Count, "Retry must not create duplicate acknowledged versions.");
                    foreach (var acknowledged in acknowledgedBeforeRetry)
                        Assert.AreEqual(1, after.DestinationVersions.Count(value => value.FileId == acknowledged), "Retry must preserve each acknowledged destination ID.");
                    if (loseCopyResponse)
                    {
                        var dropped = transport.DroppedCopyAcknowledgment!;
                        Assert.AreEqual(1, operation.VersionMappings.Count(value => value.Migrated.FileId == dropped.Id), "Recovery must adopt the exact committed version whose response was lost.");
                        Assert.AreEqual(1, copies.Read()!.Intents.Count(value => value.Phase == B2CloudPrefixCopyPhase.Completed && value.Completed?.FileId == dropped.Id));
                        Assert.IsTrue(journal.Read()!.Entries.All(entry => !entry.RequestPending));
                    }
                    var mappings = operation.VersionMappings;
                    foreach (var group in before.SourceVersions.GroupBy(v => v.Key))
                    {
                        var expectedIds = group.Select(v => mappings.Single(m => m.Original.FileId == v.FileId).Migrated.FileId).ToArray();
                        var targetKey = scope.Destination + group.Key[scope.Source.Length..];
                        CollectionAssert.AreEqual(expectedIds, after.DestinationVersions.Where(v => v.Key == targetKey).Select(v => v.FileId).ToArray());
                    }
                    foreach (var mapping in mappings.Where(m => m.Original.Action == "upload"))
                    {
                        var original = mapping.Original; var migrated = mapping.Migrated;
                        Assert.AreEqual(original.ContentType, migrated.ContentType);
                        Assert.AreEqual(original.EncryptionMode, migrated.EncryptionMode);
                        Assert.AreEqual(original.EncryptionAlgorithm, migrated.EncryptionAlgorithm);
                        foreach (var pair in original.Metadata) Assert.AreEqual(pair.Value, migrated.Metadata[pair.Key]);
                        Assert.AreEqual(original.UploadedAtMillis.ToString(System.Globalization.CultureInfo.InvariantCulture), migrated.Metadata["cloudinlet_migration_source_upload_millis"]);
                        Assert.AreEqual(original.FileId, migrated.Metadata[CloudPrefixMigration.SourceMetadataKey]);
                        using var downloaded = new MemoryStream();
                        await store.DownloadAsync(new(migrated.FileId, migrated.Key, migrated.Size, migrated.Sha1,
                            DateTimeOffset.FromUnixTimeMilliseconds(migrated.UploadedAtMillis)), downloaded, cancellationToken: token);
                        CollectionAssert.AreEqual(payloads[original.FileId], downloaded.ToArray());
                    }
                    var current = new List<CloudObject>();
                    await foreach (var value in store.ListCurrentAsync(scope.Bucket, scope.Destination, token)) current.Add(value);
                    CollectionAssert.AreEquivalent(new[] { scope.Destination + "history.txt", scope.Destination + "nested/visible.txt" }, current.Select(v => v.Key).ToArray());
                    phase = "exact-owned-cleanup";
                    scope.EnableCleanup(after);
                }
            }
            // Never clean on failure. Lost acknowledgments and partial migrations retain journals for review.
            phase = "exact-owned-cleanup";
            foreach (var owned in scope.RemainingOwned.ToArray())
            {
                scope.RequireRemainingInventory(await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                using var deleted = await transport.PostAsync("b2_delete_file_version", new { fileName = owned.Key, fileId = owned.Id }, token);
                Assert.AreEqual(owned.Id, deleted.RootElement.GetProperty("fileId").GetString());
                Assert.AreEqual(owned.Key, deleted.RootElement.GetProperty("fileName").GetString());
            }
            var clean = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(0, clean.SourceVersions.Count + clean.DestinationVersions.Count + clean.UnfinishedUploads.Count);
            phase = "complete"; succeeded = true;
        }
        catch (AssertInconclusiveException) { throw; }
        catch (Exception error)
        {
            // Stack frames locate failures without recording provider bodies, headers or exception
            // messages, which can contain credentials or signed request URLs.
            failureType = error.GetType().FullName;
            failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("B2 migration acceptance stopped at " + phase + " (" + error.GetType().Name + "). Only metadata evidence was retained; incomplete cloud fixtures were not broadly cleaned.");
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
                {
                    run, phase, succeeded, failureType, failureStack, providerFailure, transport.Mutations, transport.PayloadDownloads, fixtureNamespace = transport.Scope?.Root,
                    scenario, interruptionObserved, lostCopyResponseObserved, journalsReopened, completionReports,
                    revertEvidence,
                    droppedCopyAcknowledgment = transport.DroppedCopyAcknowledgment,
                    droppedDeleteAcknowledgment = transport.DroppedDeleteAcknowledgment,
                    acknowledgedVersionsBeforeRetry = acknowledgedBeforeRetry.Length,
                    ownedVersions = transport.Scope?.Owned.Count ?? 0, remainingOwnedVersions = transport.Scope?.RemainingOwned.Count ?? 0,
                    acknowledgedDeletions = transport.Scope?.DeletedIds.Count ?? 0, payloadDiskWrites = 0,
                    binding = "isolated fixture only; desktop binding and restart acceptance are separate gates",
                    scope = "small objects and history; optional acknowledged cancellation, one test-injected lost copy/delete response, journal reopen and destination-only revert; no process restart, actual network outage or multipart acceptance"
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddResultFile(Path.Combine(directory, "report.json"));
            }
            finally { foreach (var guard in guards) guard.Dispose(); }
        }
    }

    private sealed record RevertEvidence(bool InterruptionObserved, bool JournalsReopened, bool LostDeleteResponseObserved,
        int DestinationVersionsRemoved, bool CompletedItemPreserved, bool OriginalBindingPreserved);

    private async Task<RevertEvidence> RevertIsolatedFixtureAsync(string directory, B2CloudStore store,
        CloudPrefixAcceptanceTransport transport, B2CloudPrefixMigrationInventory before, bool loseDeleteResponse, CancellationToken token)
    {
        var scope = transport.Scope!;
        var plan = MigrationPlan.Create([
            new("completed", MigrationArea.ApplicationState, "Completed isolated metadata item", "fixture/original", "fixture/completed", "Keep completed work intact"),
            new("cloud", MigrationArea.CloudPrefix, "Isolated B2 revert acceptance", scope.Source, scope.Destination, "Retain original fixture history")]);
        var completed = new CompletedFixtureOperation(directory, plan.Fingerprint);
        var sessionPath = Path.Combine(directory, "session.json");
        var mutationsAfterSeeding = transport.Mutations;
        string[] destinationIds;
        byte[] completedEvidence;
        using (var interruption = CancellationTokenSource.CreateLinkedTokenSource(token))
        using (var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect))
        using (var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect))
        {
            var binding = new FixtureBinding(directory, plan.Fingerprint);
            var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
            using var session = MigrationSession.Create(plan, ["completed", "cloud"], sessionPath, [completed, operation]);
            session.ProgressChanged += value =>
            {
                TestContext.WriteLine("Before revert: " + value.Operation);
                if (value.ItemId == "cloud" && value.Operation == "Verified cloud version" && value.Completed == 2) interruption.Cancel();
            };
            await session.RunAsync(interruption.Token);
            Assert.IsFalse(token.IsCancellationRequested, "The controlled pause must precede the acceptance deadline.");
            Assert.IsTrue(interruption.IsCancellationRequested);
            Assert.AreEqual(MigrationItemState.Paused, session.Items.Single(value => value.Id == "cloud").State);
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(value => value.Id == "completed").State);
            Assert.AreEqual(CloudPrefixMigrationPhase.Copying, journal.Read()!.Phase);
            Assert.IsTrue(journal.Read()!.Entries.All(value => !value.RequestPending));
            var receipts = journal.Read()!.Entries.Where(value => value.Destination is not null).Select(value => value.Destination!).ToArray();
            CollectionAssert.AreEquivalent(new[] { "upload", "hide" }, receipts.Select(value => value.Action).ToArray());
            destinationIds = receipts.Select(value => value.FileId).ToArray();
            Assert.AreEqual(2, destinationIds.Length);
            Assert.AreEqual(mutationsAfterSeeding + 2, transport.Mutations);
            await binding.VerifyOriginalAsync(token);
            completedEvidence = File.ReadAllBytes(completed.Path);
        }
        var preRevert = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
        AssertVersionsEqual(before.SourceVersions, preRevert.SourceVersions);
        CollectionAssert.AreEquivalent(destinationIds, preRevert.DestinationVersions.Select(value => value.FileId).ToArray());
        scope.EnableRevert(preRevert);
        if (loseDeleteResponse) transport.ArmOneDeleteAcknowledgmentLoss();
        using (var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect))
        using (var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect))
        {
            var binding = new FixtureBinding(directory, plan.Fingerprint);
            var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
            using var session = MigrationSession.Open(sessionPath, [completed, operation]);
            Assert.IsTrue(session.CanRevert("cloud")); Assert.IsFalse(session.CanRevert("completed"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RevertAsync("completed", token));
            Assert.AreEqual(mutationsAfterSeeding + 2, transport.Mutations);
            await session.RevertAsync("cloud", token);
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(value => value.Id == "completed").State);
            CollectionAssert.AreEqual(completedEvidence, File.ReadAllBytes(completed.Path));
            await binding.VerifyOriginalAsync(token);
            if (loseDeleteResponse)
            {
                Assert.IsNotNull(transport.DroppedDeleteAcknowledgment, "The test must lose one committed delete response.");
                Assert.AreEqual(MigrationItemState.RevertFailed, session.Items.Single(value => value.Id == "cloud").State);
                Assert.IsFalse(string.IsNullOrWhiteSpace(session.Items.Single(value => value.Id == "cloud").Error),
                    "A failed revert must retain a visible, sanitized failure explanation.");
                Assert.AreEqual(CloudPrefixMigrationPhase.Reverting, journal.Read()!.Phase);
                var deletedId = transport.DroppedDeleteAcknowledgment.Id;
                Assert.IsFalse(journal.Read()!.Entries.Single(value => value.Destination?.FileId == deletedId).Removed,
                    "Production must not receive the private test deletion receipt.");
                var partial = await store.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
                AssertVersionsEqual(before.SourceVersions, partial.SourceVersions);
                CollectionAssert.AreEquivalent(destinationIds.Where(value => value != deletedId).ToArray(), partial.DestinationVersions.Select(value => value.FileId).ToArray());
                scope.RequireRemainingInventory(partial);
                Assert.AreEqual(mutationsAfterSeeding + 3, transport.Mutations, "The ambiguous delete must not be replayed automatically.");
                Assert.AreEqual(1, scope.DeletedIds.Count);
            }
            else
            {
                Assert.AreEqual(MigrationItemState.Reverted, session.Items.Single(value => value.Id == "cloud").State);
                await operation.VerifyRevertedAsync(token);
            }
        }
        // Reopen all durable application receipts again. In the lost-response case, the provider
        // adapter must verify the exact acknowledged version is absent without sending its delete again.
        using (var copies = new ProtectedB2CloudPrefixCopyJournal(Path.Combine(directory, "copies.dpapi"), Protect, Unprotect))
        using (var journal = new ProtectedCloudPrefixMigrationJournal(Path.Combine(directory, "migration.dpapi"), Protect, Unprotect))
        {
            var binding = new FixtureBinding(directory, plan.Fingerprint);
            var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                store.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
            using var session = MigrationSession.Open(sessionPath, [completed, operation]);
            if (loseDeleteResponse) await session.RevertAsync("cloud", token);
            Assert.AreEqual(MigrationItemState.Reverted, session.Items.Single(value => value.Id == "cloud").State);
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(value => value.Id == "completed").State);
            Assert.IsFalse(session.IsComplete, "Reverted must never be reported as migrated.");
            await operation.VerifyRevertedAsync(token);
            Assert.AreEqual(CloudPrefixMigrationPhase.Reverted, journal.Read()!.Phase);
            Assert.IsTrue(journal.Read()!.Entries.Where(value => value.Destination is not null).All(value => value.Removed));
            await binding.VerifyOriginalAsync(token);
            CollectionAssert.AreEqual(completedEvidence, File.ReadAllBytes(completed.Path));
            var mutations = transport.Mutations;
            await operation.RevertAsync(new ImmediateProgress(_ => { }), token);
            await session.RunAsync(token);
            Assert.AreEqual(mutations, transport.Mutations, "Reverted or completed items must not mutate again.");
            Assert.AreEqual(1, completed.ApplyCalls);
            Assert.AreEqual(0, completed.RevertCalls);
        }
        Assert.AreEqual(mutationsAfterSeeding + 4, transport.Mutations, "Two creates and two exact deletes are the entire recovery mutation budget.");
        CollectionAssert.AreEquivalent(destinationIds, scope.DeletedIds.ToArray());
        Assert.AreEqual(before.SourceVersions.Count, scope.RemainingOwned.Count);
        var deleteAttempts = File.ReadAllLines(Path.Combine(directory, "mutation-attempts.jsonl"))
            .Select(value => JsonSerializer.Deserialize<CloudPrefixAcceptanceTransport.MutationAttempt>(value)!)
            .Where(value => value.Endpoint == "b2_delete_file_version").ToArray();
        CollectionAssert.AreEquivalent(destinationIds, deleteAttempts.Select(value => value.FileId!).ToArray());
        Assert.IsTrue(deleteAttempts.All(value => value.FileName.StartsWith(scope.Destination, StringComparison.Ordinal)), "Recovery must never delete source history.");
        return new(true, true, transport.DroppedDeleteAcknowledgment is not null, destinationIds.Length, true, true);
    }

    private sealed class CompletedFixtureOperation(string directory, string fingerprint) : IMigrationOperation
    {
        internal string Path { get; } = System.IO.Path.Combine(directory, "completed-item-receipt.json");
        internal int ApplyCalls { get; private set; }
        internal int RevertCalls { get; private set; }
        public string ItemId => "completed";
        public string ReviewedPlanFingerprint => fingerprint;
        public bool CanRevert => true; // The session must still forbid reverting a completed item.
        public Task CheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ApplyCalls++;
            using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { item = ItemId, fingerprint })); stream.Flush(flushToDisk: true);
            return Task.CompletedTask;
        }
        public Task VerifyAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(new { item = ItemId, fingerprint }), File.ReadAllBytes(Path));
            return Task.CompletedTask;
        }
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken token)
        { RevertCalls++; throw new AssertFailedException("Completed unrelated work must never be reverted."); }
        public Task VerifyRevertedAsync(CancellationToken token) => throw new AssertFailedException("Completed unrelated work must never be reverted.");
    }

    private static void AssertVersionsEqual(IReadOnlyList<CloudPrefixVersion> expected, IReadOnlyList<CloudPrefixVersion> actual)
    {
        // Preserve provider ordering, exact immutable IDs, timestamps, protections and every metadata value.
        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual), "Original history changed.");
    }
    private sealed class ImmediateProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => report(value); }
    private sealed class FixtureBinding(string directory, string fingerprint) : ICloudPrefixMigrationBinding
    {
        private readonly string _path = Path.Combine(directory, "binding.dpapi");
        public string MaintenanceReceipt => fingerprint;
        public Task VerifyMaintenanceAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task VerifyOriginalAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Assert.IsFalse(File.Exists(_path)); return Task.CompletedTask; }
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var plain = JsonSerializer.SerializeToUtf8Bytes(mappings);
            try
            {
                using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(Protect(plain)); file.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return Task.CompletedTask;
        }
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var plain = Unprotect(File.ReadAllBytes(_path));
            try { CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(mappings), plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return Task.CompletedTask;
        }
    }
}

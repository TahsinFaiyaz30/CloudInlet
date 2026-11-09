using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class CloudPrefixMigrationTests
{
    private const string Old = "CloudBay/", New = "CloudInlet/";
    private static readonly IProgress<MigrationProgress> Progress = new ImmediateProgress();
    private static CloudPrefixVersion Upload(string id, string key = Old + "file.txt") =>
        new(id, key, "upload", 7, new string('a', 40), 1234, "text/plain", new Dictionary<string, string>());
    private static CloudPrefixVersion Hide(string id, string key = Old + "file.txt") =>
        new(id, key, "hide", 0, null, 1234, "application/x-bz-hide-marker", new Dictionary<string, string>());

    [TestMethod]
    public async Task MigratesEveryVersionInProviderOrderAndPreservesImportMetadataAndOriginals()
    {
        var newest = Upload("newest") with { Metadata = new Dictionary<string, string>
            { ["cloudbay_import_id"] = "import-receipt", ["cloudbay_source_id"] = "import-source", ["custom"] = "value" } };
        var fixture = new Fixture(newest, Hide("hidden"), Upload("oldest"), Upload("other", Old + "other.txt"));
        var operation = fixture.Open();
        await operation.ApplyAsync(Progress, default);
        await operation.VerifyAsync(default);
        CollectionAssert.AreEqual(new[] { "oldest", "hidden", "newest", "other" }, fixture.Store.CreatedSources.ToArray());
        CollectionAssert.AreEqual(new[] { "newest", "hidden", "oldest" },
            fixture.Store.Versions.Where(v => v.Key == Old + "file.txt").Select(v => v.FileId).ToArray());
        Assert.AreEqual(4, operation.VersionMappings.Count);
        Assert.AreEqual("import-receipt", operation.VersionMappings.Single(v => v.Original.FileId == "newest").Migrated.Metadata["cloudbay_import_id"]);
        Assert.AreEqual("import-source", operation.VersionMappings.Single(v => v.Original.FileId == "newest").Migrated.Metadata["cloudbay_source_id"]);
        Assert.IsTrue(fixture.Binding.Switched);
        Assert.IsFalse(operation.CanRevert);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => operation.RevertAsync(Progress, default));
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.AreEqual(4, fixture.Store.CreatedSources.Count, "Reopening completion must not recopy history.");
    }

    [TestMethod]
    public async Task EmptySourceCanSwitchOnlyAfterConfirmingEmptyDestination()
    {
        var fixture = new Fixture();
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.IsTrue(fixture.Binding.Switched);
        Assert.AreEqual(0, fixture.Store.CopyCalls);
        Assert.AreEqual(CloudPrefixMigrationPhase.Switched, fixture.Journal.Value!.Phase);
    }

    [TestMethod]
    public void RejectsUnreviewedPrefixesAndChangedPlanBeforeAnyProviderRequest()
    {
        var fixture = new Fixture(Upload("a"));
        Assert.ThrowsException<ArgumentException>(() => new CloudPrefixMigration(fixture.Plan, "cloud", Old, "Unreviewed/", fixture.Store, fixture.Binding, fixture.Journal));
        Assert.ThrowsException<ArgumentException>(() => new CloudPrefixMigration(fixture.Plan with { Fingerprint = new string('0', 64) }, "cloud", Old, New, fixture.Store, fixture.Binding, fixture.Journal));
        Assert.AreEqual(0, fixture.Store.ListCalls);
    }

    [TestMethod]
    public async Task ExistingDestinationVersionIsNeverOverwrittenOrDeleted()
    {
        var fixture = new Fixture(Upload("source"), Upload("unrelated", New + "existing.txt"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.IsNull(fixture.Journal.Value);
        Assert.AreEqual(0, fixture.Store.CopyCalls);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
        Assert.AreEqual(2, fixture.Store.Versions.Count);
    }

    [TestMethod]
    public async Task UnsupportedOrUnreadableHistoryFailsBeforeFirstMutation()
    {
        var basic = Upload("source");
        var variants = new[] { basic with { Sha1 = null }, basic with { ProtectionReadable = false },
            basic with { RetentionMode = "governance" }, basic with { RetainUntilMillis = 99999 },
            basic with { LegalHold = "on" }, basic with { EncryptionMode = "SSE-C" },
            basic with { EncryptionAlgorithm = "AES256" }, basic with { ReplicationStatus = "PENDING" },
            basic with { Action = "start" }, basic with { Size = -1 }, basic with { ContentType = "" },
            basic with { Metadata = new Dictionary<string, string> { [CloudPrefixMigration.ReceiptMetadataKey] = "prior" } },
            basic with { Metadata = new Dictionary<string, string> { [CloudPrefixMigration.SourceMetadataKey] = "prior" } } };
        foreach (var version in variants)
        {
            var fixture = new Fixture(version);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
            Assert.AreEqual(0, fixture.Store.CopyCalls, version.ToString());
            Assert.IsNull(fixture.Journal.Value, version.ToString());
        }
    }

    [TestMethod]
    public async Task LostCopyAcknowledgmentRecoversReceiptWithoutCreatingDuplicate()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Store.AfterCopy = _ => throw new IOException("Injected lost acknowledgment");
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.IsTrue(fixture.Journal.Value!.Entries.Single().RequestPending);
        Assert.AreEqual(1, fixture.Store.CreatedSources.Count);
        fixture.Store.AfterCopy = null;
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.AreEqual(2, fixture.Store.CopyCalls);
        Assert.AreEqual(1, fixture.Store.CreatedSources.Count);
        Assert.IsTrue(fixture.Binding.Switched);
    }

    [TestMethod]
    public async Task DuplicatePendingReceiptBlocksRecoveryWithoutAdoptingOrDeletingEitherVersion()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Store.AfterCopy = _ => throw new IOException("Injected lost acknowledgment");
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        var copied = fixture.Store.Versions.Single(v => v.Key.StartsWith(New, StringComparison.Ordinal));
        fixture.Store.Versions.Insert(0, copied with { FileId = "duplicate" });
        fixture.Store.AfterCopy = null;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.AreEqual(1, fixture.Store.CopyCalls);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
        Assert.IsFalse(fixture.Binding.Switched);
    }

    [TestMethod]
    public async Task LostHideAcknowledgmentIsNeverRepeatedOrGuessedFromListing()
    {
        var fixture = new Fixture(Hide("source"));
        fixture.Store.AfterHide = () => throw new IOException("Injected lost acknowledgment");
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        var reopened = fixture.Open();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => reopened.ApplyAsync(Progress, default));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => reopened.RevertAsync(Progress, default));
        Assert.AreEqual(1, fixture.Store.HideCalls);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
        Assert.IsTrue(fixture.Store.Versions.Any(v => v.FileId == "source"));
    }

    [TestMethod]
    public async Task DefinitivelyRejectedHideCanBeRetriedAfterPersistedIntentIsCleared()
    {
        var fixture = new Fixture(Hide("source"));
        fixture.Store.BeforeHide = () => throw new CloudMigrationRequestRejectedException("Provider proved request rejected");
        await Assert.ThrowsExceptionAsync<CloudMigrationRequestRejectedException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.IsFalse(fixture.Journal.Value!.Entries.Single().RequestPending);
        fixture.Store.BeforeHide = null;
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.AreEqual(2, fixture.Store.HideCalls);
        Assert.AreEqual(1, fixture.Store.CreatedSources.Count);
    }

    [TestMethod]
    public async Task ChangedSourceStopsAtBatchVerificationBeforeReferenceSwitch()
    {
        var fixture = new Fixture(Upload("newest"), Upload("oldest"));
        fixture.Store.AfterCopy = _ => fixture.Store.Versions.Add(Upload("external"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.AreEqual(2, fixture.Store.CreatedSources.Count);
        Assert.IsFalse(fixture.Binding.Switched);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
        Assert.IsTrue(fixture.Store.Versions.Any(v => v.FileId == "external"));
    }

    [TestMethod]
    public async Task UnexpectedDestinationVersionStopsWithoutDeletingOtherClientsData()
    {
        var fixture = new Fixture(Upload("newest"), Upload("oldest"));
        fixture.Store.AfterCopy = _ => fixture.Store.Versions.Insert(0, Upload("external", New + "file.txt"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().RevertAsync(Progress, default));
        Assert.AreEqual(2, fixture.Store.CreatedSources.Count);
        Assert.IsFalse(fixture.Binding.Switched);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
    }

    [TestMethod]
    public async Task MetadataAndContentMismatchCannotBecomeCompletedReceipt()
    {
        foreach (var corrupt in new Func<CloudPrefixVersion, CloudPrefixVersion>[] {
            version => version with { Sha1 = new string('b', 40) },
            version => version with { ContentType = "application/octet-stream" },
            version => version with { Metadata = new Dictionary<string, string>() },
            version => version with { Size = version.Size + 1 } })
        {
            var fixture = new Fixture(Upload("source"));
            fixture.Store.TransformCopy = corrupt;
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
            Assert.IsNull(fixture.Journal.Value!.Entries.Single().Destination);
            Assert.IsTrue(fixture.Journal.Value.Entries.Single().RequestPending);
            Assert.IsFalse(fixture.Binding.Switched);
        }
    }

    [TestMethod]
    public async Task ChangedProviderHistoryOrderIsRejectedEvenWhenVersionsAndTimestampsMatch()
    {
        var fixture = new Fixture(Upload("newest"), Upload("oldest"));
        var operation = fixture.Open();
        await operation.CheckAsync(default);
        fixture.Store.Versions.Reverse();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => operation.ApplyAsync(Progress, default));
        Assert.AreEqual(0, fixture.Store.CopyCalls);
    }

    [TestMethod]
    public async Task JournalFailureBeforeIntentPreventsRequestAndPoisonsInstanceUntilReopened()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Journal.FailWrite = 2;
        var operation = fixture.Open();
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => operation.ApplyAsync(Progress, default));
        Assert.AreEqual(0, fixture.Store.CopyCalls);
        Assert.IsFalse(operation.CanRevert);
        fixture.Journal.FailWrite = 0;
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => operation.ApplyAsync(Progress, default));
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.AreEqual(1, fixture.Store.CreatedSources.Count);
    }

    [TestMethod]
    public async Task JournalFailureAfterCopyLeavesDurablePendingReceiptForRestart()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Journal.FailWrite = 3;
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.IsTrue(fixture.Journal.Value!.Entries.Single().RequestPending);
        fixture.Journal.FailWrite = 0;
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.AreEqual(1, fixture.Store.CreatedSources.Count);
        Assert.IsTrue(fixture.Binding.Switched);
    }

    [TestMethod]
    public async Task CrashDuringReferenceSwitchResumesIdempotentlyAndDisallowsRevert()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Binding.ThrowAfterSwitch = true;
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.AreEqual(CloudPrefixMigrationPhase.Switching, fixture.Journal.Value!.Phase);
        var reopened = fixture.Open();
        Assert.IsFalse(reopened.CanRevert);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => reopened.RevertAsync(Progress, default));
        fixture.Binding.ThrowAfterSwitch = false;
        await reopened.ApplyAsync(Progress, default);
        Assert.AreEqual(1, fixture.Store.CreatedSources.Count);
        Assert.AreEqual(2, fixture.Binding.SwitchCalls);
        await reopened.VerifyAsync(default);
    }

    [TestMethod]
    public async Task RevertDeletesOnlyAcknowledgedCopiesAndRetainsEveryOriginal()
    {
        var fixture = new Fixture(Upload("newest"), Upload("oldest"));
        fixture.Store.BeforeCopy = source => { if (source.FileId == "newest") throw new CloudMigrationRequestRejectedException("Injected rejection"); };
        await Assert.ThrowsExceptionAsync<CloudMigrationRequestRejectedException>(() => fixture.Open().ApplyAsync(Progress, default));
        var reopened = fixture.Open();
        await reopened.RevertAsync(Progress, default);
        await reopened.VerifyRevertedAsync(default);
        CollectionAssert.AreEqual(new[] { "copy-1" }, fixture.Store.Deletes.ToArray());
        CollectionAssert.AreEqual(new[] { "newest", "oldest" }, fixture.Store.Versions.Select(v => v.FileId).ToArray());
        Assert.IsFalse(fixture.Binding.Switched);
    }

    [TestMethod]
    public async Task LostDeleteAcknowledgmentResumesExactIdRecoveryWithoutTouchingOriginal()
    {
        var fixture = new Fixture(Upload("newest"), Upload("oldest"));
        fixture.Store.BeforeCopy = source => { if (source.FileId == "newest") throw new CloudMigrationRequestRejectedException("Injected rejection"); };
        await Assert.ThrowsExceptionAsync<CloudMigrationRequestRejectedException>(() => fixture.Open().ApplyAsync(Progress, default));
        fixture.Store.AfterDelete = () => throw new IOException("Injected lost delete acknowledgment");
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().RevertAsync(Progress, default));
        fixture.Store.AfterDelete = null;
        var reopened = fixture.Open();
        await reopened.RevertAsync(Progress, default);
        await reopened.VerifyRevertedAsync(default);
        Assert.IsTrue(fixture.Store.Deletes.All(id => id == "copy-1"));
        Assert.AreEqual(2, fixture.Store.Versions.Count);
    }

    [TestMethod]
    public async Task MaintenanceLeaseLossPreventsAnyCloudMutation()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Binding.MaintenanceValid = false;
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.AreEqual(0, fixture.Store.CopyCalls);
        Assert.IsNull(fixture.Journal.Value);
    }

    [TestMethod]
    public async Task ChangedMaintenanceReviewOrAccountCannotReusePriorJournal()
    {
        var fixture = new Fixture(Upload("source"));
        await fixture.Open().CheckAsync(default);
        fixture.Binding.MaintenanceReceipt = new string('b', 64);
        Assert.ThrowsException<InvalidDataException>(() => fixture.Open());
        fixture.Binding.MaintenanceReceipt = new string('a', 64);
        fixture.Store.AccountId = "another-account";
        Assert.ThrowsException<InvalidDataException>(() => fixture.Open());
    }

    [TestMethod]
    public async Task PendingCopyCannotBeRevertedUntilProviderReconcilesOwnership()
    {
        var fixture = new Fixture(Upload("source"));
        fixture.Store.AfterCopy = _ => throw new IOException("Injected lost acknowledgment");
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().RevertAsync(Progress, default));
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
    }

    [TestMethod]
    public async Task LargeHistoryUsesBoundedFullInventoryScansAndStillVerifiesBeforeSwitching()
    {
        var fixture = new Fixture(Enumerable.Range(0, 16_000).Select(index => Upload("source-" + index, Old + "file-" + index.ToString("D5") + ".txt")).ToArray());
        await fixture.Open().ApplyAsync(Progress, default);
        Assert.IsTrue(fixture.Binding.Switched); Assert.AreEqual(16_000, fixture.Store.CreatedSources.Count);
        Assert.IsTrue(fixture.Store.ListCalls < 270, $"Full inventory scans grew per version: {fixture.Store.ListCalls}.");
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
    }

    [TestMethod]
    public async Task ExternalChangeStopsAtNextBoundedBatchAndNeverActivatesPartialHistory()
    {
        var fixture = new Fixture(Enumerable.Range(0, 300).Select(index => Upload("source-" + index, Old + "file-" + index.ToString("D5") + ".txt")).ToArray());
        fixture.Store.AfterCopy = _ =>
        {
            fixture.Store.AfterCopy = null;
            fixture.Store.Versions.Add(Upload("external", Old + "external.txt"));
        };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.AreEqual(128, fixture.Store.CreatedSources.Count); Assert.IsFalse(fixture.Binding.Switched);
        Assert.AreEqual(0, fixture.Store.Deletes.Count);
    }

    [DataTestMethod]
    [DataRow("lost-hide")]
    [DataRow("external-source")]
    [DataRow("duplicate-receipt")]
    public async Task KeepOriginalPreservesAmbiguousCloudHistoryWithoutAnyProviderRequest(string problem)
    {
        var fixture = new Fixture(problem == "lost-hide" ? Hide("source") : Upload("source"));
        if (problem == "lost-hide") fixture.Store.AfterHide = () => throw new IOException("Lost response");
        else fixture.Store.AfterCopy = _ => throw new IOException("Lost response");
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        if (problem == "external-source") fixture.Store.Versions.Add(Upload("external", Old + "external.txt"));
        if (problem == "duplicate-receipt")
            fixture.Store.Versions.Add(fixture.Store.Versions.Single(value => value.Key.StartsWith(New)) with { FileId = "unacknowledged-duplicate" });
        var versions = System.Text.Json.JsonSerializer.Serialize(fixture.Store.Versions);
        var entries = System.Text.Json.JsonSerializer.Serialize(fixture.Journal.Value!.Entries);
        var requests = (fixture.Store.CopyCalls, fixture.Store.HideCalls, fixture.Store.ListCalls, fixture.Store.Deletes.Count);
        var operation = fixture.Open(); Assert.IsTrue(operation.CanKeepOriginal);
        await operation.KeepOriginalAsync(Progress, default);
        await fixture.Open().VerifyOriginalKeptAsync(default);
        Assert.AreEqual(CloudPrefixMigrationPhase.OriginalKept, fixture.Journal.Value!.Phase);
        Assert.AreEqual(entries, System.Text.Json.JsonSerializer.Serialize(fixture.Journal.Value.Entries));
        Assert.AreEqual(versions, System.Text.Json.JsonSerializer.Serialize(fixture.Store.Versions));
        Assert.AreEqual(requests, (fixture.Store.CopyCalls, fixture.Store.HideCalls, fixture.Store.ListCalls, fixture.Store.Deletes.Count));
        Assert.IsFalse(fixture.Binding.Switched); Assert.IsFalse(operation.CanRevert);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().ApplyAsync(Progress, default));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().RevertAsync(Progress, default));
        Assert.AreEqual(requests, (fixture.Store.CopyCalls, fixture.Store.HideCalls, fixture.Store.ListCalls, fixture.Store.Deletes.Count));
    }

    [TestMethod]
    public async Task KeepOriginalRequiresHealthyDurableReceiptAndVerifiedOriginalBinding()
    {
        var fixture = new Fixture(Upload("source"));
        await fixture.Open().CheckAsync(default);
        fixture.Binding.MaintenanceValid = false;
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().KeepOriginalAsync(Progress, default));
        fixture.Binding.MaintenanceValid = true; fixture.Binding.Switched = true;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().KeepOriginalAsync(Progress, default));
        Assert.AreEqual(CloudPrefixMigrationPhase.Copying, fixture.Journal.Value!.Phase);
        fixture.Binding.Switched = false; fixture.Journal.FailWrite = fixture.Journal.Writes + 1;
        var failed = fixture.Open();
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => failed.KeepOriginalAsync(Progress, default));
        Assert.IsFalse(failed.CanKeepOriginal);
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => failed.VerifyOriginalKeptAsync(default));
        Assert.AreEqual(CloudPrefixMigrationPhase.Copying, fixture.Journal.Value.Phase);
        fixture.Journal.FailWrite = 0;
        await fixture.Open().KeepOriginalAsync(Progress, default);
        await fixture.Open().VerifyOriginalKeptAsync(default);
        Assert.AreEqual(0, fixture.Store.CopyCalls + fixture.Store.HideCalls + fixture.Store.Deletes.Count);
    }

    [TestMethod]
    public async Task KeepOriginalCannotAbandonSwitchingOrInventMissingTerminalReceipt()
    {
        var fixture = new Fixture(Upload("source"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Open().VerifyOriginalKeptAsync(default));
        fixture.Binding.ThrowAfterSwitch = true;
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Open().ApplyAsync(Progress, default));
        Assert.AreEqual(CloudPrefixMigrationPhase.Switching, fixture.Journal.Value!.Phase);
        var operation = fixture.Open(); Assert.IsFalse(operation.CanKeepOriginal);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => operation.KeepOriginalAsync(Progress, default));
        Assert.IsTrue(fixture.Binding.Switched); Assert.AreEqual(0, fixture.Store.Deletes.Count);
    }

    private sealed class Fixture(params CloudPrefixVersion[] versions)
    {
        public MigrationPlan Plan { get; } = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix,
            "Cloud folder", Old, New, "Preserve every immutable version and original recovery history.")]);
        public Store Store { get; } = new(versions);
        public Binding Binding { get; } = new();
        public Journal Journal { get; } = new();
        public CloudPrefixMigration Open() => new(Plan, "cloud", Old, New, Store, Binding, Journal);
    }

    private sealed class Store(IEnumerable<CloudPrefixVersion> versions) : ICloudPrefixMigrationStore
    {
        public string AccountId { get; set; } = "fixture-account";
        public string BucketId => "fixture-bucket";
        public List<CloudPrefixVersion> Versions { get; } = versions.ToList();
        public List<string> CreatedSources { get; } = [];
        public List<string> Deletes { get; } = [];
        public int CopyCalls, HideCalls, ListCalls;
        public Action<CloudPrefixVersion>? BeforeCopy, AfterCopy;
        public Func<CloudPrefixVersion, CloudPrefixVersion>? TransformCopy;
        public Action? BeforeHide, AfterHide, AfterDelete;
        public Task ValidateAccessAsync(string sourcePrefix, string destinationPrefix, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudPrefixVersion>> ListAsync(string prefix, CancellationToken token)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<CloudPrefixVersion>>(Versions.Where(v => v.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(v => v.Key, StringComparer.Ordinal).ToArray());
        }
        public Task<CloudPrefixVersion> CopyAsync(CloudPrefixVersion source, string destinationKey, string operationId, CancellationToken token)
        {
            CopyCalls++;
            var recovered = Versions.SingleOrDefault(v => v.Key == destinationKey && v.Metadata.TryGetValue(CloudPrefixMigration.ReceiptMetadataKey, out var receipt) && receipt == operationId);
            if (recovered is not null) return Task.FromResult(recovered);
            BeforeCopy?.Invoke(source);
            var metadata = new Dictionary<string, string>(source.Metadata)
            { [CloudPrefixMigration.ReceiptMetadataKey] = operationId, [CloudPrefixMigration.SourceMetadataKey] = source.FileId };
            var copy = source with { FileId = "copy-" + (CreatedSources.Count + 1), Key = destinationKey, Metadata = metadata, UploadedAtMillis = 9999 };
            copy = TransformCopy?.Invoke(copy) ?? copy;
            CreatedSources.Add(source.FileId); Versions.Insert(0, copy);
            AfterCopy?.Invoke(source);
            return Task.FromResult(copy);
        }
        public Task<CloudPrefixVersion> HideOnceAsync(string destinationKey, CancellationToken token)
        {
            HideCalls++; BeforeHide?.Invoke();
            var source = Versions.First(v => v.Key == Old + destinationKey[New.Length..] && v.Action == "hide");
            var copy = source with { FileId = "copy-" + (CreatedSources.Count + 1), Key = destinationKey, UploadedAtMillis = 9999 };
            CreatedSources.Add(source.FileId); Versions.Insert(0, copy); AfterHide?.Invoke();
            return Task.FromResult(copy);
        }
        public Task DeleteExactAsync(CloudPrefixVersion version, CancellationToken token)
        {
            Deletes.Add(version.FileId);
            Versions.RemoveAll(v => v.FileId == version.FileId);
            AfterDelete?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Binding : ICloudPrefixMigrationBinding
    {
        public string MaintenanceReceipt { get; set; } = new('a', 64);
        public bool MaintenanceValid = true, Switched, ThrowAfterSwitch;
        public int SwitchCalls;
        public Task VerifyMaintenanceAsync(CancellationToken token) => MaintenanceValid ? Task.CompletedTask : throw new IOException("Maintenance lease lost");
        public Task VerifyOriginalAsync(CancellationToken token) => !Switched ? Task.CompletedTask : throw new InvalidDataException("Already switched");
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
        {
            SwitchCalls++; Switched = true;
            if (ThrowAfterSwitch) throw new IOException("Injected crash after durable reference switch");
            return Task.CompletedTask;
        }
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token) => Switched ? Task.CompletedTask : throw new InvalidDataException("Not switched");
    }

    private sealed class Journal : ICloudPrefixMigrationJournal
    {
        public CloudPrefixMigrationCheckpoint? Value;
        public int Writes, FailWrite;
        public CloudPrefixMigrationCheckpoint? Read() => Value;
        public void Write(CloudPrefixMigrationCheckpoint checkpoint)
        {
            if (++Writes == FailWrite) throw new IOException("Injected journal failure");
            Value = checkpoint;
        }
    }
    private sealed class ImmediateProgress : IProgress<MigrationProgress> { public void Report(MigrationProgress value) { } }
}

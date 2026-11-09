using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class B2CloudPrefixMigrationStoreTests
{
    private const string Old = "CloudBay/", New = "CloudInlet/";
    [TestMethod]
    public async Task ReviewedNestedBackupFolderMigratesItsVersionsAndMappingIsBoundToRecovery()
    {
        using var fixture = new Fixture(); var raw = fixture.AddSource("source"); raw["fileName"] = Old + ".cloudbay-backups/Documents/file.txt";
        var rules = new[] { new CloudPrefixRule(Old + ".cloudbay-backups/Documents/", New + ".cloudinlet-backups/Documents/") };
        using var owner = await fixture.Connect(); var journal = new Journal();
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history", CloudMappings: rules)]);
        var provider = owner.CreatePrefixMigrationStore("bucket", Old, New, journal, rules);
        var binding = new Binding(); var engine = new CloudPrefixMigration(plan, "cloud", Old, New, provider, binding, new EngineJournal());
        await engine.ApplyAsync(new ProgressSink(), default); await engine.VerifyAsync(default);
        Assert.AreEqual(New + ".cloudinlet-backups/Documents/file.txt", engine.VersionMappings.Single().Migrated.Key);
        Assert.ThrowsException<InvalidDataException>(() => owner.CreatePrefixMigrationStore("bucket", Old, New, journal));
    }
    [TestMethod]
    public async Task NestedBackupCollisionStopsBeforeAnyCreatingRequest()
    {
        using var fixture = new Fixture(); fixture.AddSource("old")["fileName"] = Old + ".cloudbay-backups/Documents/file.txt";
        fixture.AddSource("conflict")["fileName"] = Old + ".cloudinlet-backups/Documents/file.txt";
        var rules = new[] { new CloudPrefixRule(Old + ".cloudbay-backups/Documents/", New + ".cloudinlet-backups/Documents/") };
        using var owner = await fixture.Connect(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal(), rules);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.ValidateAccessAsync(Old, New, default)); Assert.AreEqual(0, fixture.Mutations);
    }
    private static readonly string Operation = new('a', 64);

    [DataTestMethod]
    [DataRow("copy")]
    [DataRow("upload")]
    public async Task CopyPreservesMetadataContentTypeExplicitEncryptionAndTimestampLineage(string acknowledgmentAction)
    {
        using var fixture = new Fixture { ChangeCopyResponse = response => response["action"] = acknowledgmentAction };
        fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default);
        var source = (await store.ListAsync(Old, default)).Single();
        var copy = await store.CopyAsync(source, New + "file.txt", Operation, default);
        Assert.AreEqual("upload", copy.Action); Assert.AreEqual("copy-1", copy.FileId);
        Assert.AreEqual(source.Size, copy.Size); Assert.AreEqual(source.Sha1, copy.Sha1);
        Assert.AreEqual("SSE-B2", copy.EncryptionMode); Assert.AreEqual("AES256", copy.EncryptionAlgorithm);
        Assert.IsTrue(copy.ProtectionReadable); Assert.AreEqual(source.LegalHold, copy.LegalHold);
        Assert.IsNull(copy.RetentionMode); Assert.IsNull(copy.RetainUntilMillis);
        Assert.AreEqual(source.ContentType, copy.ContentType); Assert.AreEqual("old-import", copy.Metadata["cloudbay_import_id"]);
        Assert.AreEqual("original-modified", copy.Metadata["src_last_modified_millis"]);
        Assert.AreEqual("1234", copy.Metadata["cloudinlet_migration_source_upload_millis"]);
        Assert.AreEqual(Operation, copy.Metadata[CloudPrefixMigration.ReceiptMetadataKey]);
        Assert.AreEqual(copy, journal.Value!.Intents.Single().Completed);
        var listed = (await store.ListAsync(New, default)).Single();
        Assert.AreEqual("upload", listed.Action); Assert.AreEqual(copy.FileId, listed.FileId);
        Assert.AreEqual(copy.UploadedAtMillis, listed.UploadedAtMillis);
        Assert.AreEqual(1, fixture.Count("b2_copy_file"));
        Assert.AreEqual(1, fixture.Versions.Count(v => v["fileName"]!.GetValue<string>().StartsWith(Old)));
        var reopened = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await reopened.ValidateAccessAsync(Old, New, default);
        Assert.AreEqual(copy.FileId, (await reopened.CopyAsync(source, New + "file.txt", Operation, default)).FileId);
        Assert.AreEqual(1, fixture.Count("b2_copy_file"), "Completion recovery must not create another version.");
    }

    [DataTestMethod]
    [DataRow("action-hide")]
    [DataRow("action-start")]
    [DataRow("action-other")]
    [DataRow("account")]
    [DataRow("bucket")]
    [DataRow("identity")]
    [DataRow("key")]
    [DataRow("checksum")]
    [DataRow("metadata")]
    [DataRow("retention")]
    [DataRow("legal-hold")]
    [DataRow("encryption")]
    [DataRow("unreadable-protection")]
    public async Task CopyAcknowledgmentNormalizationRetainsIdentityIntegrityAndProtectionGuards(string defect)
    {
        using var fixture = new Fixture { ChangeCopyResponse = response =>
        {
            switch (defect)
            {
                case "action-hide": response["action"] = "hide"; break;
                case "action-start": response["action"] = "start"; break;
                case "action-other": response["action"] = "unknown"; break;
                case "account": response["accountId"] = "another-account"; break;
                case "bucket": response["bucketId"] = "another-bucket"; break;
                case "identity": response["fileId"] = "source"; break;
                case "key": response["fileName"] = New + "unreviewed.txt"; break;
                case "checksum": response["contentSha1"] = new string('a', 40); break;
                case "metadata": response["fileInfo"]![CloudPrefixMigration.ReceiptMetadataKey] = "unowned"; break;
                case "retention": response["fileRetention"]!["value"]!["mode"] = "compliance"; break;
                case "legal-hold": response["legalHold"]!["value"] = "on"; break;
                case "encryption": response["serverSideEncryption"]!["mode"] = "SSE-C"; break;
                case "unreadable-protection": response["fileRetention"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null }; break;
            }
        } };
        fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal();
        var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.IsFalse(error.Message.Contains("No cloud changes were made", StringComparison.Ordinal), "A rejected acknowledgment must not claim its creating request made no changes.");
        Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, journal.Value!.Intents.Single().Phase);
        Assert.IsNull(journal.Value.Intents.Single().Completed);
        Assert.AreEqual(1, fixture.Count("b2_copy_file")); Assert.AreEqual(0, fixture.Count("b2_delete_file_version"));
        Assert.AreEqual(2, fixture.Versions.Count, "The source and unverified destination must both remain for recovery.");
    }

    [TestMethod]
    public async Task CopyActionIsNotAcceptedAsIndependentFileInfoVerification()
    {
        using var fixture = new Fixture { ChangeCreatedInfoResponse = response => response["action"] = "copy" };
        fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal();
        var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, journal.Value!.Intents.Single().Phase);
        Assert.IsNull(journal.Value.Intents.Single().Completed); Assert.AreEqual(1, fixture.Count("b2_copy_file"));
    }

    [TestMethod]
    public async Task LostCreatingAcknowledgmentRecoversUniqueReceiptWithoutReplayingRequest()
    {
        using var fixture = new Fixture { LoseAfter = "b2_copy_file" }; fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, journal.Value!.Intents.Single().Phase);
        var reopened = owner.CreatePrefixMigrationStore("bucket", Old, New, journal); await reopened.ValidateAccessAsync(Old, New, default);
        var recovered = await reopened.CopyAsync(source, New + "file.txt", Operation, default);
        Assert.AreEqual("copy-1", recovered.FileId); Assert.AreEqual(1, fixture.Count("b2_copy_file"));
    }

    [TestMethod]
    public async Task AbsentReceiptAfterAmbiguousCreateIsNotPermissionToReplay()
    {
        using var fixture = new Fixture { LoseBefore = "b2_copy_file" }; fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        await Assert.ThrowsExceptionAsync<IOException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(1, fixture.Count("b2_copy_file")); Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, journal.Value!.Intents.Single().Phase);
    }

    [TestMethod]
    public async Task DefinitivelyRejectedCreateCanRetryButVerificationRejectionCannotClearIntent()
    {
        using var fixture = new Fixture { Reject = "b2_copy_file" }; fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<CloudMigrationRequestRejectedException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(B2CloudPrefixCopyPhase.Ready, journal.Value!.Intents.Single().Phase);
        fixture.RejectCreatedInfo = true;
        await Assert.ThrowsExceptionAsync<B2RequestException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(B2CloudPrefixCopyPhase.Creating, journal.Value!.Intents.Single().Phase);
        fixture.RejectCreatedInfo = false;
        await store.CopyAsync(source, New + "file.txt", Operation, default);
        Assert.AreEqual(2, fixture.Count("b2_copy_file"), "Only the definitively rejected creating request may be replayed.");
    }

    [TestMethod]
    public async Task PendingIntentWriteFailurePreventsMutationAndPoisonsInstance()
    {
        using var fixture = new Fixture(); fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal { FailWhenCreating = true };
        var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(0, fixture.Count("b2_copy_file"));
    }

    [TestMethod]
    public async Task PostCreationJournalFailureRecoversFromDurableIntentAndProviderReceipt()
    {
        using var fixture = new Fixture(); fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal { FailWhenCompleted = true };
        var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        journal.FailWhenCompleted = false;
        var reopened = owner.CreatePrefixMigrationStore("bucket", Old, New, journal); await reopened.ValidateAccessAsync(Old, New, default);
        await reopened.CopyAsync(source, New + "file.txt", Operation, default); Assert.AreEqual(1, fixture.Count("b2_copy_file"));
    }

    [TestMethod]
    public async Task MissingProtectionCapabilitiesBlockBeforeAnyCloudMutation()
    {
        foreach (var missing in new[] { "readBucketRetentions", "readFileRetentions", "readFileLegalHolds" })
        {
            using var fixture = new Fixture { MissingCapability = missing }; fixture.AddSource("source");
            using var owner = await fixture.Connect(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal());
            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => store.ValidateAccessAsync(Old, New, default));
            Assert.AreEqual(0, fixture.Mutations);
        }
    }

    [TestMethod]
    public async Task EncryptionProtectionAndLifecycleRestrictionsRemainFailClosed()
    {
        foreach (var defect in new[] { "unreadable", "retained", "held", "sse-c", "unencrypted", "lifecycle", "replication" })
        {
            using var fixture = new Fixture(); var source = fixture.AddSource("source");
            switch (defect)
            {
                case "unreadable": source["fileRetention"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null }; break;
                case "retained": source["fileRetention"]!["value"]!["mode"] = "compliance"; break;
                case "held": source["legalHold"]!["value"] = "on"; break;
                case "sse-c": source["serverSideEncryption"]!["mode"] = "SSE-C"; break;
                case "unencrypted": source["serverSideEncryption"] = new JsonObject { ["mode"] = null, ["algorithm"] = null }; break;
                case "lifecycle": fixture.Bucket["lifecycleRules"] = new JsonArray(new JsonObject { ["fileNamePrefix"] = New }); break;
                case "replication": source["replicationStatus"] = "PENDING"; break;
            }
            using var owner = await fixture.Connect(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal());
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.ValidateAccessAsync(Old, New, default));
            Assert.AreEqual(0, fixture.Mutations, defect);
        }
    }

    [TestMethod]
    public async Task BucketPolicyChangeAndSourceMetadataChangePreventNextCopy()
    {
        foreach (var policyChange in new[] { true, false })
        {
            using var fixture = new Fixture(); var raw = fixture.AddSource("source");
            using var owner = await fixture.Connect(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal());
            await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
            if (policyChange) fixture.Bucket["revision"] = 2; else raw["fileInfo"]!["custom"] = "changed";
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
            Assert.AreEqual(0, fixture.Mutations);
        }
    }

    [TestMethod]
    public async Task DuplicateReceiptAndChangedCompletedReceiptCannotBeAdopted()
    {
        using var fixture = new Fixture { LoseAfter = "b2_copy_file" }; fixture.AddSource("source");
        using var owner = await fixture.Connect(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal());
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        var duplicate = (JsonObject)fixture.Versions.Last().DeepClone(); duplicate["fileId"] = "duplicate"; fixture.Versions.Add(duplicate);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
        Assert.AreEqual(1, fixture.Count("b2_copy_file"));
    }

    [TestMethod]
    public async Task LargeCopyResumesLostStartAndFinishWithoutAnotherVersionAndPreservesExactType()
    {
        foreach (var lost in new[] { "b2_start_large_file", "b2_finish_large_file" })
        {
            using var fixture = new Fixture { LoseAfter = lost }; fixture.AddSource("source", 5_000_000_001);
            using var owner = await fixture.Connect(); var journal = new Journal(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
            await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => store.CopyAsync(source, New + "file.txt", Operation, default));
            var reopened = owner.CreatePrefixMigrationStore("bucket", Old, New, journal); await reopened.ValidateAccessAsync(Old, New, default);
            var result = await reopened.CopyAsync(source, New + "file.txt", Operation, default);
            Assert.AreEqual(source.Size, result.Size); Assert.AreEqual(source.ContentType, result.ContentType);
            Assert.AreEqual(source.Sha1, result.Sha1); Assert.AreEqual("SSE-B2", result.EncryptionMode);
            Assert.AreEqual(1, fixture.Count("b2_start_large_file")); Assert.AreEqual(1, fixture.Count("b2_finish_large_file"));
            Assert.AreEqual(48, fixture.Count("b2_copy_part")); Assert.AreEqual(2, fixture.Versions.Count);
            Assert.AreEqual(0, fixture.Unfinished.Count);
        }
    }

    [TestMethod]
    public async Task ExactOwnedDeletionRecoversLostAcknowledgmentAndRefusesSourcesOrChangedMetadata()
    {
        using var fixture = new Fixture(); fixture.AddSource("source");
        using var owner = await fixture.Connect(); var store = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal());
        await store.ValidateAccessAsync(Old, New, default); var source = (await store.ListAsync(Old, default)).Single();
        var copy = await store.CopyAsync(source, New + "file.txt", Operation, default);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DeleteExactAsync(source, default));
        fixture.Versions.Last()["fileInfo"]!["custom"] = "different";
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.DeleteExactAsync(copy, default));
        fixture.Versions.Last()["fileInfo"]!["custom"] = "value";
        fixture.LoseAfter = "b2_delete_file_version";
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => store.DeleteExactAsync(copy, default));
        await store.DeleteExactAsync(copy, default);
        Assert.AreEqual(1, fixture.Count("b2_delete_file_version")); Assert.AreEqual("source", fixture.Versions.Single()["fileId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FullEnginePreservesUploadHideUploadOrderAndRevertsOnlyAcknowledgedDestination()
    {
        using var fixture = new Fixture(); fixture.AddSource("old", timestamp: 10); fixture.AddHide("hide", Old + "file.txt", 20); fixture.AddSource("new", timestamp: 30);
        using var owner = await fixture.Connect(); var provider = owner.CreatePrefixMigrationStore("bucket", Old, New, new Journal());
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
        var binding = new Binding(); var engine = new CloudPrefixMigration(plan, "cloud", Old, New, provider, binding, new EngineJournal());
        await engine.ApplyAsync(new ProgressSink(), default); await engine.VerifyAsync(default);
        var target = await provider.ListAsync(New, default);
        CollectionAssert.AreEqual(new[] { "upload", "hide", "upload" }, target.Select(v => v.Action).ToArray());
        Assert.AreEqual("new", target[0].Metadata[CloudPrefixMigration.SourceMetadataKey]);
        Assert.AreEqual("old", target[2].Metadata[CloudPrefixMigration.SourceMetadataKey]);
        Assert.IsTrue(binding.Switched); Assert.IsFalse(engine.CanRevert); Assert.AreEqual(3, (await provider.ListAsync(Old, default)).Count);
    }

    private sealed class ProgressSink(Action<MigrationProgress>? callback = null) : IProgress<MigrationProgress> { public void Report(MigrationProgress value) => callback?.Invoke(value); }
    [DataTestMethod]
    [DataRow("da39a3ee5e6b4b0d3255bfef95601890afd80709", null)]
    [DataRow(null, "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [DataRow("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709", "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    public async Task HideAcknowledgmentAndLaterListingUseTheSameNormalizedChecksum(string? responseChecksum, string? listedChecksum)
    {
        using var fixture = new Fixture { ChangeHideResponse = marker => marker["contentSha1"] = responseChecksum };
        fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal();
        var provider = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await provider.ValidateAccessAsync(Old, New, default);
        var acknowledged = await provider.HideOnceAsync(New + "file.txt", default);
        Assert.IsNull(acknowledged.Sha1); Assert.IsNull(journal.Value!.HideReceipts!.Single().Sha1);
        fixture.Versions.Last()["contentSha1"] = listedChecksum;
        var listed = (await provider.ListAsync(New, default)).Single();
        Assert.AreEqual(acknowledged.FileId, listed.FileId); Assert.IsNull(listed.Sha1);
        var recovered = await ((ICloudPrefixMigrationHideRecovery)provider).RecoverAcknowledgedHideAsync(New + "file.txt", [], default);
        Assert.AreEqual(acknowledged.FileId, recovered!.FileId);
        await provider.DeleteExactAsync(acknowledged, default);
        Assert.AreEqual(1, fixture.Count("b2_hide_file")); Assert.AreEqual(1, fixture.Count("b2_delete_file_version"));
        Assert.AreEqual("source", fixture.Versions.Single()["fileId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnexpectedHideAcknowledgmentChecksumCannotCreateAnOwnedReceipt()
    {
        using var fixture = new Fixture { ChangeHideResponse = marker => marker["contentSha1"] = new string('a', 40) };
        fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal();
        var provider = owner.CreatePrefixMigrationStore("bucket", Old, New, journal);
        await provider.ValidateAccessAsync(Old, New, default);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => provider.HideOnceAsync(New + "file.txt", default));
        Assert.AreEqual(0, journal.Value!.HideReceipts!.Count);
        Assert.AreEqual(1, fixture.Count("b2_hide_file")); Assert.AreEqual(0, fixture.Count("b2_delete_file_version"));
    }

    [TestMethod]
    public async Task EngineResumesOwnedStartReturnedInVersionHistoryWithoutRestartingCopy()
    {
        using var fixture = new Fixture { LoseAfter = "b2_start_large_file" }; fixture.AddSource("large", length: 5_000_000_000);
        using var owner = await fixture.Connect(); var journal = new Journal(); var outer = new EngineJournal();
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
        var binding = new Binding();
        CloudPrefixMigration Open() => new(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => Open().ApplyAsync(new ProgressSink(), default));
        Assert.AreEqual(1, fixture.Unfinished.Count);
        await Open().ApplyAsync(new ProgressSink(), default);
        Assert.IsTrue(binding.Switched); Assert.AreEqual(1, fixture.Count("b2_start_large_file"));
        Assert.AreEqual(0, fixture.Unfinished.Count); Assert.AreEqual(2, fixture.Versions.Count);
    }
    [TestMethod]
    public async Task InterruptedMultipartCopyCanRevertItsExactOwnedStartAndRecoverLostCancelAcknowledgment()
    {
        foreach (var lostCancel in new[] { false, true })
        {
            using var fixture = new Fixture { LoseAfter = "b2_start_large_file" }; fixture.AddSource("large", length: 5_000_000_000);
            using var owner = await fixture.Connect(); var journal = new Journal(); var outer = new EngineJournal();
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
            var binding = new Binding();
            CloudPrefixMigration Open() => new(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => Open().ApplyAsync(new ProgressSink(), default));
            if (lostCancel)
            {
                fixture.LoseAfter = "b2_cancel_large_file";
                await Assert.ThrowsExceptionAsync<HttpRequestException>(() => Open().RevertAsync(new ProgressSink(), default));
            }
            await Open().RevertAsync(new ProgressSink(), default); await Open().VerifyRevertedAsync(default);
            Assert.AreEqual(1, fixture.Count("b2_cancel_large_file")); Assert.AreEqual(0, fixture.Count("b2_finish_large_file"));
            Assert.AreEqual(0, fixture.Unfinished.Count); Assert.AreEqual("large", fixture.Versions.Single()["fileId"]!.GetValue<string>());
            Assert.AreEqual(B2CloudPrefixCopyPhase.Canceled, journal.Value!.Intents.Single().Phase); Assert.IsFalse(binding.Switched);
        }
    }
    [TestMethod]
    public async Task LostSmallCopyResponseCanRevertWithoutCreatingAnotherCopy()
    {
        using var fixture = new Fixture { LoseAfter = "b2_copy_file" }; fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal(); var outer = new EngineJournal();
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
        var binding = new Binding();
        CloudPrefixMigration Open() => new(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => Open().ApplyAsync(new ProgressSink(), default));
        await Open().RevertAsync(new ProgressSink(), default); await Open().VerifyRevertedAsync(default);
        Assert.AreEqual(1, fixture.Count("b2_copy_file")); Assert.AreEqual(1, fixture.Count("b2_delete_file_version"));
        Assert.AreEqual("source", fixture.Versions.Single()["fileId"]!.GetValue<string>());
    }
    [TestMethod]
    public async Task ReopenedTransportCanRevertAcknowledgedVersionsWithoutCallingApplyAgain()
    {
        using var fixture = new Fixture(); fixture.AddSource("source");
        using var owner = await fixture.Connect(); var journal = new Journal(); var outer = new EngineJournal();
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
        var binding = new Binding();
        var first = new CloudPrefixMigration(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await Assert.ThrowsExceptionAsync<IOException>(() => first.ApplyAsync(new ProgressSink(value =>
            { if (value.Operation == "Verified cloud version") throw new IOException("Stop before binding."); }), default));
        var resumed = new CloudPrefixMigration(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await resumed.RevertAsync(new ProgressSink(), default); await resumed.VerifyRevertedAsync(default);
        Assert.AreEqual(1, fixture.Count("b2_copy_file")); Assert.AreEqual(1, fixture.Count("b2_delete_file_version"));
        Assert.IsFalse(binding.Switched); Assert.AreEqual("source", fixture.Versions.Single()["fileId"]!.GetValue<string>());
    }
    [TestMethod]
    public async Task TransportAcknowledgedHideSurvivesOuterJournalInterruptionWithoutDuplicateHide()
    {
        using var fixture = new Fixture(); fixture.AddSource("old", timestamp: 10); fixture.AddHide("hide", Old + "file.txt", 20);
        using var owner = await fixture.Connect(); var journal = new Journal(); var outer = new EngineJournal { FailHideCompletion = true };
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
        var binding = new Binding();
        var first = new CloudPrefixMigration(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => first.ApplyAsync(new ProgressSink(), default));
        Assert.IsTrue(outer.Value!.Entries.Single(value => value.Source.Action == "hide").RequestPending);
        Assert.AreEqual(1, journal.Value!.HideReceipts!.Count); outer.FailHideCompletion = false;
        var resumed = new CloudPrefixMigration(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await resumed.ApplyAsync(new ProgressSink(), default); await resumed.VerifyAsync(default);
        Assert.AreEqual(1, fixture.Count("b2_hide_file")); Assert.IsTrue(binding.Switched);
    }
    [TestMethod]
    public async Task LostHideResponseStillCannotBeAdoptedFromListing()
    {
        using var fixture = new Fixture { LoseAfter = "b2_hide_file" }; fixture.AddHide("hide", Old + "file.txt", 20);
        using var owner = await fixture.Connect(); var journal = new Journal(); var outer = new EngineJournal();
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", Old, New, "Move cloud history")]);
        var binding = new Binding();
        var first = new CloudPrefixMigration(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => first.ApplyAsync(new ProgressSink(), default));
        var resumed = new CloudPrefixMigration(plan, "cloud", Old, New, owner.CreatePrefixMigrationStore("bucket", Old, New, journal), binding, outer);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => resumed.ApplyAsync(new ProgressSink(), default));
        Assert.AreEqual(1, fixture.Count("b2_hide_file")); Assert.IsFalse(binding.Switched);
    }
    private sealed class EngineJournal : ICloudPrefixMigrationJournal
    {
        public CloudPrefixMigrationCheckpoint? Value; public bool FailHideCompletion;
        public CloudPrefixMigrationCheckpoint? Read() => Value;
        public void Write(CloudPrefixMigrationCheckpoint checkpoint)
        {
            if (FailHideCompletion && checkpoint.Entries.Any(value => value.Source.Action == "hide" && value.Destination is not null)) throw new IOException("Interrupted outer journal.");
            Value = checkpoint;
        }
    }
    private sealed class Binding : ICloudPrefixMigrationBinding
    {
        public string MaintenanceReceipt => new('e', 64); public bool Switched;
        public Task VerifyMaintenanceAsync(CancellationToken token) => Task.CompletedTask;
        public Task VerifyOriginalAsync(CancellationToken token) { Assert.IsFalse(Switched); return Task.CompletedTask; }
        public Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token) { Switched = true; return Task.CompletedTask; }
        public Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token) { Assert.IsTrue(Switched); return Task.CompletedTask; }
    }
    private sealed class Journal : IB2CloudPrefixCopyJournal
    {
        public B2CloudPrefixCopyCheckpoint? Value; public bool FailWhenCreating, FailWhenCompleted;
        public B2CloudPrefixCopyCheckpoint? Read() => Value;
        public void Write(B2CloudPrefixCopyCheckpoint checkpoint)
        {
            if (FailWhenCreating && checkpoint.Intents.Any(i => i.Phase == B2CloudPrefixCopyPhase.Creating) ||
                FailWhenCompleted && checkpoint.Intents.Any(i => i.Phase == B2CloudPrefixCopyPhase.Completed)) throw new IOException("Injected durable-write failure.");
            Value = checkpoint;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string? MissingCapability, LoseAfter, LoseBefore, Reject;
        public Action<JsonObject>? ChangeHideResponse, ChangeCopyResponse, ChangeCreatedInfoResponse;
        public bool RejectCreatedInfo;
        public List<string> Calls { get; } = [];
        public List<JsonObject> Versions { get; } = [];
        public List<JsonObject> Unfinished { get; } = [];
        public Dictionary<int, JsonObject> Parts { get; } = [];
        public int Mutations => Calls.Count(c => c is "b2_copy_file" or "b2_start_large_file" or "b2_copy_part" or "b2_finish_large_file" or "b2_hide_file" or "b2_delete_file_version");
        public int Count(string operation) => Calls.Count(c => c == operation);
        public JsonObject Bucket { get; } = new()
        {
            ["accountId"] = "account", ["bucketId"] = "bucket", ["revision"] = 1,
            ["defaultServerSideEncryption"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = new JsonObject { ["mode"] = "SSE-B2", ["algorithm"] = "AES256" } },
            ["fileLockConfiguration"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = new JsonObject
                { ["isFileLockEnabled"] = false, ["defaultRetention"] = new JsonObject { ["mode"] = null, ["period"] = null } } },
            ["lifecycleRules"] = new JsonArray(), ["replicationConfiguration"] = new JsonObject { ["asReplicationSource"] = null, ["asReplicationDestination"] = null }
        };
        public JsonObject AddSource(string id, long length = 7, long timestamp = 1234)
        {
            var result = new JsonObject
            {
                ["accountId"] = "account", ["bucketId"] = "bucket", ["fileId"] = id, ["fileName"] = Old + "file.txt", ["action"] = "upload",
                ["contentLength"] = length, ["contentSha1"] = new string('b', 40), ["contentType"] = "application/custom-test", ["uploadTimestamp"] = timestamp,
                ["fileInfo"] = new JsonObject { ["custom"] = "value", ["cloudbay_import_id"] = "old-import", ["src_last_modified_millis"] = "original-modified" },
                ["fileRetention"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = new JsonObject { ["mode"] = null, ["retainUntilTimestamp"] = null } },
                ["legalHold"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = "off" },
                ["serverSideEncryption"] = new JsonObject { ["mode"] = "SSE-B2", ["algorithm"] = "AES256" }
            };
            if (length >= 5_000_000_000) { result["contentSha1"] = "none"; result["fileInfo"]!["large_file_sha1"] = new string('b', 40); }
            Versions.Add(result); return result;
        }
        public JsonObject AddHide(string id, string key, long timestamp)
        {
            var marker = new JsonObject { ["accountId"] = "account", ["bucketId"] = "bucket", ["fileId"] = id, ["fileName"] = key,
                ["action"] = "hide", ["contentLength"] = 0, ["contentSha1"] = null, ["contentType"] = "application/x-bz-hide-marker", ["fileInfo"] = new JsonObject(), ["uploadTimestamp"] = timestamp };
            Versions.Add(marker); return marker;
        }
        public async Task<B2CloudStore> Connect()
        {
            var owner = new B2CloudStore(new Handler(Send)); await owner.ConnectAsync(new B2Credentials("test-id", "test-secret")); return owner;
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            var operation = request.RequestUri!.Segments[^1]; Calls.Add(operation);
            if (operation == "b2_authorize_account") return Json(JsonSerializer.SerializeToNode(new
            {
                accountId = "account", authorizationToken = "fixture", apiInfo = new { storageApi = new
                {
                    apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid", recommendedPartSize = 100_000_000L, absoluteMinimumPartSize = 5_000_000L,
                    allowed = new { capabilities = new[] { "listBuckets", "listFiles", "readFiles", "writeFiles", "deleteFiles", "readBucketEncryption", "readBucketRetentions",
                        "readFileRetentions", "readFileLegalHolds", "readBucketReplications" }.Where(c => c != MissingCapability).ToArray(), namePrefix = (string?)null,
                        buckets = new[] { new { id = "bucket", name = "test" } } }
                } }
            })!);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            string Text(string key) => body[key]!.GetValue<string>();
            if (LoseBefore == operation) { LoseBefore = null; throw new HttpRequestException("Injected network loss before visible receipt."); }
            if (Reject == operation) { Reject = null; return Error(); }
            JsonNode result;
            switch (operation)
            {
                case "b2_list_buckets": result = new JsonObject { ["buckets"] = new JsonArray(Bucket.DeepClone()) }; break;
                case "b2_list_file_versions":
                    result = new JsonObject { ["files"] = new JsonArray(Versions.Concat(Unfinished).Where(v => v["fileName"]!.GetValue<string>().StartsWith(Text("prefix"), StringComparison.Ordinal))
                        .OrderBy(v => v["fileName"]!.GetValue<string>(), StringComparer.Ordinal).ThenByDescending(v => v["uploadTimestamp"]!.GetValue<long>()).Select(v => v.DeepClone()).ToArray()),
                        ["nextFileName"] = null, ["nextFileId"] = null }; break;
                case "b2_list_unfinished_large_files":
                    result = new JsonObject { ["files"] = new JsonArray(Unfinished.Where(v => v["fileName"]!.GetValue<string>().StartsWith(Text("namePrefix"), StringComparison.Ordinal)).Select(v => v.DeepClone()).ToArray()), ["nextFileId"] = null }; break;
                case "b2_get_file_info":
                    if (RejectCreatedInfo && Text("fileId").StartsWith("copy-", StringComparison.Ordinal)) return Error();
                    result = Versions.Concat(Unfinished).Single(v => v["fileId"]!.GetValue<string>() == Text("fileId")).DeepClone();
                    if (Text("fileId").StartsWith("copy-", StringComparison.Ordinal)) ChangeCreatedInfoResponse?.Invoke((JsonObject)result);
                    break;
                case "b2_copy_file":
                    var source = Versions.Single(v => v["fileId"]!.GetValue<string>() == Text("sourceFileId"));
                    var copy = (JsonObject)source.DeepClone(); copy["fileId"] = "copy-" + Count(operation); copy["fileName"] = Text("fileName");
                    copy["contentType"] = Text("contentType"); copy["fileInfo"] = body["fileInfo"]!.DeepClone();
                    copy["serverSideEncryption"] = body["destinationServerSideEncryption"]!.DeepClone(); copy["uploadTimestamp"] = 10_000L + Mutations;
                    Versions.Add(copy); result = copy.DeepClone(); result["action"] = "copy";
                    ChangeCopyResponse?.Invoke((JsonObject)result); break;
                case "b2_start_large_file":
                    var started = (JsonObject)Versions[0].DeepClone(); started["fileId"] = "large-" + Count(operation); started["fileName"] = Text("fileName");
                    started["action"] = "start"; started["contentLength"] = 0L; started["contentSha1"] = "none";
                    started["contentType"] = Text("contentType"); started["fileInfo"] = body["fileInfo"]!.DeepClone(); started["serverSideEncryption"] = body["serverSideEncryption"]!.DeepClone();
                    started["uploadTimestamp"] = 10_000L + Mutations; Unfinished.Add(started); result = started.DeepClone(); break;
                case "b2_list_parts": result = new JsonObject { ["parts"] = new JsonArray(Parts.OrderBy(p => p.Key).Select(p => p.Value.DeepClone()).ToArray()), ["nextPartNumber"] = null }; break;
                case "b2_copy_part":
                    Assert.IsFalse(body.ContainsKey("sourceServerSideEncryption"), "SSE-B2 source needs no customer-key parameters.");
                    Assert.IsFalse(body.ContainsKey("destinationServerSideEncryption"), "Parts inherit SSE-B2 from start; only SSE-C supplies part encryption parameters.");
                    var range = Text("range")[6..].Split('-').Select(long.Parse).ToArray(); var partNumber = body["partNumber"]!.GetValue<int>();
                    var part = new JsonObject { ["fileId"] = Text("largeFileId"), ["partNumber"] = partNumber, ["contentLength"] = range[1] - range[0] + 1,
                        ["contentSha1"] = new string('c', 40), ["serverSideEncryption"] = Unfinished.Single()["serverSideEncryption"]!.DeepClone() };
                    Parts[partNumber] = part; result = part.DeepClone(); break;
                case "b2_finish_large_file":
                    var finish = Unfinished.Single(v => v["fileId"]!.GetValue<string>() == Text("fileId")); Unfinished.Remove(finish);
                    finish["action"] = "upload"; finish["contentLength"] = Parts.Values.Sum(v => v["contentLength"]!.GetValue<long>()); finish["uploadTimestamp"] = 10_000L + Mutations;
                    Versions.Add(finish); result = finish.DeepClone(); break;
                case "b2_cancel_large_file":
                    var canceled = Unfinished.Single(v => v["fileId"]!.GetValue<string>() == Text("fileId")); Unfinished.Remove(canceled);
                    result = new JsonObject { ["fileId"] = Text("fileId"), ["fileName"] = canceled["fileName"]!.GetValue<string>(),
                        ["accountId"] = "account", ["bucketId"] = "bucket" }; Parts.Clear(); break;
                case "b2_hide_file":
                    result = AddHide("hide-" + Count(operation), Text("fileName"), 10_000L + Mutations).DeepClone();
                    ChangeHideResponse?.Invoke((JsonObject)result); break;
                case "b2_delete_file_version":
                    var remove = Versions.Single(v => v["fileId"]!.GetValue<string>() == Text("fileId")); Versions.Remove(remove);
                    result = new JsonObject { ["fileId"] = Text("fileId"), ["fileName"] = Text("fileName") }; break;
                default: throw new AssertFailedException("Unexpected payload or provider request: " + operation);
            }
            if (LoseAfter == operation) { LoseAfter = null; throw new HttpRequestException("Injected lost acknowledgment after provider committed."); }
            return Json(result);
        }
        public void Dispose() { }
    }
    private static HttpResponseMessage Error() => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"status\":400,\"code\":\"bad_request\",\"message\":\"fixture rejection\"}", Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Json(JsonNode value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
}

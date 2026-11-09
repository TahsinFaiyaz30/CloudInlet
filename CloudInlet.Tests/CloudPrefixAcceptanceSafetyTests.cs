using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class CloudPrefixAcceptanceSafetyTests
{
    private const string Api = "https://api001.backblazeb2.com/b2api/v4/";
    private const string Upload = "https://pod-000-1007-13.backblaze.com/b2api/v4/b2_upload_file/bucket/session";
    private static CloudAccount Account() => new("account", Api, "https://f001.backblazeb2.com",
        CloudPrefixAcceptanceScope.RequiredCapabilities, "bucket");
    private static CloudPrefixAcceptanceScope Scope() => new("bucket", "test/", Guid.NewGuid());
    private static B2CloudPrefixMigrationInventory Inventory(CloudPrefixAcceptanceScope scope,
        CloudPrefixVersion[]? sources = null, CloudPrefixVersion[]? destinations = null) =>
        new("account", "bucket", 1, scope.Source, scope.Destination, null, null, sources ?? [], destinations ?? [], [], []);
    private static CloudPrefixVersion Version(string id, string key) =>
        new(id, key, "upload", 3, new string('a', 40), 1, "text/plain", new Dictionary<string, string>());

    [TestMethod]
    public void EveryRequiredCapabilityMustBePresentBeforeWrites()
    {
        foreach (var capability in CloudPrefixAcceptanceScope.RequiredCapabilities)
        {
            var scope = Scope();
            var account = Account() with { Capabilities = Account().Capabilities.Where(v => v != capability).ToArray() };
            CollectionAssert.AreEqual(new[] { capability }, CloudPrefixAcceptanceScope.MissingCapabilities(account));
            Assert.ThrowsException<InvalidDataException>(() => scope.EnableWrites(account, Inventory(scope)));
            Assert.IsFalse(scope.WritesEnabled);
        }
    }

    [TestMethod]
    public void ExistingHistoryPolicyOrDifferentAccountCannotEnableWrites()
    {
        var scope = Scope();
        var empty = Inventory(scope);
        var invalid = new[]
        {
            empty with { AccountId = "other" }, empty with { BucketId = "other" },
            empty with { SourcePrefix = "CloudBay/" }, empty with { DestinationPrefix = "CloudInlet/" },
            empty with { SourceVersions = [Version("old", scope.Source + "file")] },
            empty with { DestinationVersions = [Version("old", scope.Destination + "file")] },
            empty with { UnfinishedUploads = [Version("unfinished", scope.Source + "file")] },
            empty with { Blockers = [new("bucket-retention-unreadable", "Unknown protection")] },
            empty with { DefaultEncryptionMode = "SSE-C", DefaultEncryptionAlgorithm = "AES256" }
        };
        foreach (var value in invalid)
        {
            Assert.ThrowsException<InvalidDataException>(() => scope.EnableWrites(Account(), value));
            Assert.IsFalse(scope.WritesEnabled);
        }
        Assert.ThrowsException<InvalidDataException>(() => scope.EnableWrites(Account() with { AllowedBucketId = null }, empty));
        scope.EnableWrites(Account(), empty);
        Assert.IsTrue(scope.WritesEnabled);
    }

    [DataTestMethod]
    [DataRow("CloudBay/")]
    [DataRow("CloudInlet/")]
    [DataRow("test/../")]
    [DataRow("test\\")]
    public void UnrelatedOrAmbiguousKeysCannotBecomeOwned(string key)
    {
        var scope = Scope(); scope.EnableWrites(Account(), Inventory(scope));
        Assert.ThrowsException<InvalidDataException>(() => scope.Acknowledge("foreign", key));
        Assert.AreEqual(0, scope.Owned.Count);
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireKey(scope.Source));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireKey(scope.Source + "../outside"));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireKey(scope.Root + "CloudBay-other/file"));
    }

    [TestMethod]
    public void CleanupNeedsExactAcknowledgmentsAndCompleteUnprotectedInventory()
    {
        var scope = Scope(); scope.EnableWrites(Account(), Inventory(scope));
        var source = Version("source-id", scope.Source + "file");
        var destination = Version("target-id", scope.Destination + "file");
        scope.Acknowledge(source.FileId, source.Key); scope.Acknowledge(destination.FileId, destination.Key);
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete(source.FileId, source.Key));
        foreach (var inventory in new[]
        {
            Inventory(scope, [source]),
            Inventory(scope, [source], [destination, Version("unacknowledged", scope.Destination + "other")]),
            Inventory(scope, [source], [destination with { Key = scope.Destination + "changed" }]),
            Inventory(scope, [source], [destination with { LegalHold = "on" }]),
            Inventory(scope, [source], [destination with { ProtectionReadable = false }]),
            Inventory(scope, [source], [destination with { RetentionMode = "governance", RetainUntilMillis = long.MaxValue }])
        })
        {
            Assert.ThrowsException<InvalidDataException>(() => scope.EnableCleanup(inventory));
            Assert.IsFalse(scope.CleanupEnabled);
        }
        scope.EnableCleanup(Inventory(scope, [source], [destination]));
        scope.RequireDelete(source.FileId, source.Key);
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete(source.FileId, destination.Key));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete("foreign-id", source.Key));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireWrite(source.Key));
    }

    [TestMethod]
    public async Task RealisticUploadUrlAndUnfinishedListingReachTransportWithoutParsingPayloadAsJson()
    {
        using var fixture = new TransportFixture();
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        using (await fixture.Post("b2_list_unfinished_large_files", new { bucketId = "bucket", namePrefix = fixture.Scope.Source })) { }
        using (await fixture.Post("b2_get_upload_url", new { bucketId = "bucket" })) { }
        using var upload = new HttpRequestMessage(HttpMethod.Post, Upload) { Content = new ByteArrayContent("non-json fixture bytes"u8.ToArray()) };
        upload.Headers.Add("X-Bz-File-Name", Uri.EscapeDataString(fixture.Scope.Source + "file.txt"));
        using (await fixture.Client.SendAsync(upload)) { }
        Assert.AreEqual(1, fixture.Transport.Mutations);
        Assert.AreEqual(fixture.Scope.Source + "file.txt", fixture.Scope.RequireOwned("source-id").Key);
        var ledger = File.ReadAllText(Path.Combine(fixture.Directory, "owned-versions.jsonl"));
        StringAssert.Contains(ledger, "source-id");
        Assert.IsFalse(ledger.Contains("non-json fixture bytes") || ledger.Contains("private-auth"));
        Assert.AreEqual(4, fixture.Inner.Requests);
    }

    [TestMethod]
    public void RecoveryOwnershipRequiresCompleteUniqueAcknowledgmentsAndUnprotectedMatchingInventory()
    {
        var scope = Scope();
        var source = Version("source", scope.Source + "file");
        var destination = Version("target", scope.Destination + "file");
        CloudPrefixAcceptanceScope.OwnedVersion[] receipts = [new(source.FileId, source.Key), new(destination.FileId, destination.Key)];
        foreach (var inventory in new[]
        {
            Inventory(scope, [source]),
            Inventory(scope, [source], [destination, Version("foreign", scope.Destination + "other")]),
            Inventory(scope, [source], [destination with { Key = scope.Destination + "changed" }]),
            Inventory(scope, [source], [destination with { LegalHold = "on" }]),
            Inventory(scope, [source], [destination]) with { AccountId = "other" },
            Inventory(scope, [source], [destination]) with { UnfinishedUploads = [Version("pending", scope.Destination + "pending")] }
        })
        {
            Assert.ThrowsException<InvalidDataException>(() => scope.RestoreForRecovery(Account(), inventory, receipts));
            Assert.IsFalse(scope.WritesEnabled);
            Assert.AreEqual(0, scope.Owned.Count);
        }
        Assert.ThrowsException<InvalidDataException>(() => scope.RestoreForRecovery(Account(), Inventory(scope, [source], [destination]), [receipts[0], receipts[0]]));
        Assert.ThrowsException<InvalidDataException>(() => scope.RestoreForRecovery(Account() with { AllowedBucketId = null }, Inventory(scope, [source], [destination]), receipts));
        Assert.ThrowsException<InvalidDataException>(() => scope.RestoreForRecovery(Account() with { Capabilities = ["listFiles"] }, Inventory(scope, [source], [destination]), receipts));
        scope.RestoreForRecovery(Account(), Inventory(scope, [source], [destination]), receipts);
        Assert.IsTrue(scope.WritesEnabled);
        Assert.AreEqual(2, scope.Owned.Count);
        Assert.ThrowsException<InvalidDataException>(() => scope.RestoreForRecovery(Account(), Inventory(scope, [source], [destination]), receipts));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequirePolicy(Inventory(scope, [source], [destination]) with { AccountId = "other" }));
    }

    [TestMethod]
    public void RevertCannotDeleteSourcesUnknownVersionsOrAlreadyAcknowledgedDeletions()
    {
        var scope = Scope(); scope.EnableWrites(Account(), Inventory(scope));
        var source = Version("source", scope.Source + "file");
        var target = Version("target", scope.Destination + "file");
        scope.Acknowledge(source.FileId, source.Key); scope.Acknowledge(target.FileId, target.Key);
        Assert.ThrowsException<InvalidDataException>(() => scope.EnableRevert(Inventory(scope, [source])));
        Assert.ThrowsException<InvalidDataException>(() => scope.EnableRevert(Inventory(scope, [source], [target with { ProtectionReadable = false }])));
        Assert.IsFalse(scope.RevertEnabled);
        scope.EnableRevert(Inventory(scope, [source], [target]));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireWrite(target.Key));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete(source.FileId, source.Key));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete("foreign", target.Key));
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete(target.FileId, target.Key + "changed"));
        scope.RequireDelete(target.FileId, target.Key);
        scope.AcknowledgeDeletion(target.FileId, target.Key);
        Assert.AreEqual(2, scope.Owned.Count, "Creation history remains durable after acknowledged deletion.");
        Assert.AreEqual(1, scope.RemainingOwned.Count);
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete(target.FileId, target.Key));
        Assert.ThrowsException<InvalidDataException>(() => scope.EnableCleanup(Inventory(scope, [source], [target])));
        scope.EnableCleanup(Inventory(scope, [source]));
        scope.RequireDelete(source.FileId, source.Key);
        Assert.ThrowsException<InvalidDataException>(() => scope.RequireDelete(target.FileId, target.Key));
    }

    [TestMethod]
    public async Task DeleteResponseLossRequiresReviewedRevertAndDurableExactAcknowledgment()
    {
        using var fixture = new TransportFixture();
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneDeleteAcknowledgmentLoss());
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        var source = Version("source-id", fixture.Scope.Source + "file.txt");
        var target = Version("target-id", fixture.Scope.Destination + "file.txt");
        fixture.Scope.Acknowledge(source.FileId, source.Key); fixture.Scope.Acknowledge(target.FileId, target.Key);
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneDeleteAcknowledgmentLoss());
        fixture.Scope.EnableRevert(Inventory(fixture.Scope, [source], [target]));
        fixture.Transport.ArmOneDeleteAcknowledgmentLoss();
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneDeleteAcknowledgmentLoss());
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_delete_file_version", new { fileId = source.FileId, fileName = source.Key }));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Post("b2_delete_file_version", new { fileId = target.FileId, fileName = target.Key }));
        Assert.AreEqual(target.FileId, fixture.Transport.DroppedDeleteAcknowledgment!.Id);
        var deleted = File.ReadAllLines(Path.Combine(fixture.Directory, "deleted-versions.jsonl"))
            .Select(value => JsonSerializer.Deserialize<CloudPrefixAcceptanceScope.OwnedVersion>(value)!).Single();
        Assert.AreEqual(target.FileId, deleted.Id); Assert.AreEqual(target.Key, deleted.Key);
        Assert.AreEqual(1, fixture.Scope.DeletedIds.Count);
        var requests = fixture.Inner.Requests;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_delete_file_version", new { fileId = target.FileId, fileName = target.Key }));
        Assert.AreEqual(requests, fixture.Inner.Requests, "An acknowledged delete must never be sent twice.");
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneDeleteAcknowledgmentLoss());
        Assert.AreEqual(1, ReadAttempts(fixture.Directory).Count(value => value.Endpoint == "b2_delete_file_version"));
    }

    [DataTestMethod]
    [DataRow("wrong-id")]
    [DataRow("wrong-key")]
    [DataRow("rejected")]
    public async Task InvalidDeleteResponseDoesNotAcknowledgeRemovalOrConsumeFault(string response)
    {
        using var fixture = new TransportFixture();
        await fixture.Authorize(); fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        var target = Version("target-id", fixture.Scope.Destination + "file.txt");
        fixture.Scope.Acknowledge(target.FileId, target.Key);
        fixture.Scope.EnableRevert(Inventory(fixture.Scope, destinations: [target]));
        fixture.Transport.ArmOneDeleteAcknowledgmentLoss();
        fixture.Inner.DeleteResponse = response;
        var body = new { fileId = target.FileId, fileName = target.Key };
        if (response == "rejected")
        { using var rejected = await fixture.Post("b2_delete_file_version", body); Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode); }
        else await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_delete_file_version", body));
        Assert.AreEqual(0, fixture.Scope.DeletedIds.Count);
        Assert.IsNull(fixture.Transport.DroppedDeleteAcknowledgment);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory, "deleted-versions.jsonl")));
        fixture.Inner.DeleteResponse = null;
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Post("b2_delete_file_version", body));
        Assert.AreEqual(1, fixture.Scope.DeletedIds.Count);
    }

    [TestMethod]
    public async Task CopyDownloadAndCleanupRequireAcknowledgedIdsAndExactMapping()
    {
        using var fixture = new TransportFixture();
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        var source = Version("source-id", fixture.Scope.Source + "file.txt");
        fixture.Scope.Acknowledge(source.FileId, source.Key);
        using (await fixture.Post("b2_copy_file", new { sourceFileId = source.FileId, destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt" })) { }
        var target = Version("target-id", fixture.Scope.Destination + "file.txt");
        using (var download = await fixture.Client.GetAsync("https://f001.backblazeb2.com/b2api/v4/b2_download_file_by_id?fileId=target-id"))
            CollectionAssert.AreEqual("abc"u8.ToArray(), await download.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(1, fixture.Transport.PayloadDownloads);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_delete_file_version", new { fileId = target.FileId, fileName = target.Key }));
        fixture.Scope.EnableCleanup(Inventory(fixture.Scope, [source], [target]));
        using var deleted = await fixture.Transport.PostAsync("b2_delete_file_version", new { fileId = target.FileId, fileName = target.Key }, default);
        Assert.AreEqual(target.FileId, deleted.RootElement.GetProperty("fileId").GetString());
        Assert.AreEqual(2, fixture.Transport.Mutations);
    }

    [TestMethod]
    public async Task CopyResponseLossIsOneShotAndOnlyAfterDurableOwnershipAcknowledgment()
    {
        using var fixture = new TransportFixture();
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneCopyAcknowledgmentLoss());
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        fixture.Transport.ArmOneCopyAcknowledgmentLoss();
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneCopyAcknowledgmentLoss());
        // An armed copy fault must not interrupt seed-upload acknowledgments.
        using (await fixture.Post("b2_get_upload_url", new { bucketId = "bucket" })) { }
        using (var upload = new HttpRequestMessage(HttpMethod.Post, Upload) { Content = new ByteArrayContent("abc"u8.ToArray()) })
        {
            upload.Headers.Add("X-Bz-File-Name", Uri.EscapeDataString(fixture.Scope.Source + "file.txt"));
            using var uploaded = await fixture.Client.SendAsync(upload);
        }
        Assert.IsNull(fixture.Transport.DroppedCopyAcknowledgment);
        var receipt = new string('a', 64);
        var copy = new { sourceFileId = "source-id", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt",
            fileInfo = new Dictionary<string, string> { [CloudPrefixMigration.ReceiptMetadataKey] = receipt } };
        fixture.Inner.BeforeRequest = request =>
        {
            if (request.RequestUri!.Segments[^1] != "b2_copy_file") return;
            var attempts = ReadAttempts(fixture.Directory);
            var attempt = attempts.Last();
            Assert.AreEqual("b2_copy_file", attempt.Endpoint);
            Assert.AreEqual("source-id", attempt.SourceFileId);
            Assert.AreEqual(copy.fileName, attempt.FileName);
            Assert.AreEqual(receipt, attempt.OperationReceipt);
            Assert.IsNull(attempt.FileId);
            Assert.AreEqual(TimeSpan.Zero, attempt.Utc.Offset);
            Assert.IsTrue(attempt.Attempt > 0);
        };
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Post("b2_copy_file", copy));
        Assert.AreEqual("target-id", fixture.Transport.DroppedCopyAcknowledgment!.Id);
        Assert.AreEqual(copy.fileName, fixture.Scope.RequireOwned("target-id").Key);
        var owned = File.ReadAllLines(Path.Combine(fixture.Directory, "owned-versions.jsonl"))
            .Select(value => JsonSerializer.Deserialize<CloudPrefixAcceptanceScope.OwnedVersion>(value)!).ToArray();
        Assert.AreEqual(2, owned.Length);
        Assert.AreEqual("target-id", owned[1].Id);
        Assert.AreEqual(copy.fileName, owned[1].Key);
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneCopyAcknowledgmentLoss());
        using (await fixture.Post("b2_copy_file", copy)) { }
        Assert.AreEqual("target-id", fixture.Transport.DroppedCopyAcknowledgment.Id, "The fault may drop only one response.");
        Assert.AreEqual(copy.fileName, fixture.Scope.RequireOwned("target-id-2").Key);
        Assert.AreEqual(2, fixture.Inner.CopyRequests);
        var log = File.ReadAllText(Path.Combine(fixture.Directory, "mutation-attempts.jsonl"));
        Assert.IsFalse(log.Contains("private-auth") || log.Contains("private-upload-auth") || log.Contains(Upload) || log.Contains("https://"));
        Assert.AreEqual(3, ReadAttempts(fixture.Directory).Length);
    }

    [TestMethod]
    public async Task FailedMutationAttemptLedgerWriteStopsBeforeNetworkDispatch()
    {
        using var fixture = new TransportFixture();
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        fixture.Scope.Acknowledge("source-id", fixture.Scope.Source + "file.txt");
        System.IO.Directory.CreateDirectory(Path.Combine(fixture.Directory, "mutation-attempts.jsonl"));
        var stopped = false;
        try
        {
            using var response = await fixture.Post("b2_copy_file", new { sourceFileId = "source-id", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt" });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { stopped = true; }
        Assert.IsTrue(stopped, "An unwritable mutation-attempt ledger must stop the request.");
        Assert.AreEqual(1, fixture.Inner.Requests, "Only the earlier authorization may reach the inner transport.");
        Assert.AreEqual(0, fixture.Inner.CopyRequests);
        Assert.AreEqual(1, fixture.Scope.Owned.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory, "owned-versions.jsonl")));
    }

    [TestMethod]
    public async Task RejectedCopyResponseDoesNotConsumeTheOneShotLossOrEstablishOwnership()
    {
        using var fixture = new TransportFixture();
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        fixture.Scope.Acknowledge("source-id", fixture.Scope.Source + "file.txt");
        fixture.Transport.ArmOneCopyAcknowledgmentLoss();
        fixture.Inner.RejectCopy = true;
        var copy = new { sourceFileId = "source-id", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt" };
        using (var rejected = await fixture.Post("b2_copy_file", copy)) Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.IsNull(fixture.Transport.DroppedCopyAcknowledgment);
        Assert.AreEqual(1, fixture.Scope.Owned.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory, "owned-versions.jsonl")));
        fixture.Inner.RejectCopy = false;
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Post("b2_copy_file", copy));
        Assert.AreEqual("target-id", fixture.Transport.DroppedCopyAcknowledgment!.Id);
        Assert.AreEqual(2, ReadAttempts(fixture.Directory).Length);
    }

    private static CloudPrefixAcceptanceTransport.MutationAttempt[] ReadAttempts(string directory) =>
        File.ReadAllLines(Path.Combine(directory, "mutation-attempts.jsonl"))
            .Select(value => JsonSerializer.Deserialize<CloudPrefixAcceptanceTransport.MutationAttempt>(value)!).ToArray();

    [TestMethod]
    public async Task HideResponseLossRequiresPreflightAndDurableExactReceiptAndCanOnlyOccurOnce()
    {
        using var fixture = new TransportFixture();
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneHideAcknowledgmentLoss());
        await fixture.Authorize(); fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        fixture.Transport.ArmOneHideAcknowledgmentLoss();
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneHideAcknowledgmentLoss());
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_hide_file", new { bucketId = "other", fileName = fixture.Scope.Destination + "file.txt" }));
        Assert.IsNull(fixture.Transport.DroppedHideAcknowledgment);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Post("b2_hide_file", new { bucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt" }));
        var receipt = fixture.Transport.DroppedHideAcknowledgment!;
        Assert.AreEqual("hide-id", receipt.Id); Assert.AreEqual(fixture.Scope.Destination + "file.txt", receipt.Key);
        Assert.AreEqual(receipt, JsonSerializer.Deserialize<CloudPrefixAcceptanceScope.OwnedVersion>(File.ReadAllLines(Path.Combine(fixture.Directory, "owned-versions.jsonl")).Single()));
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Transport.ArmOneHideAcknowledgmentLoss());
        using (await fixture.Post("b2_hide_file", new { bucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt" })) { }
        Assert.AreEqual(2, fixture.Transport.Mutations);
    }

    [DataTestMethod]
    [DataRow("PUT", "https://api001.backblazeb2.com/b2api/v4/b2_hide_file")]
    [DataRow("POST", "http://api001.backblazeb2.com/b2api/v4/b2_hide_file")]
    [DataRow("POST", "https://api001.backblazeb2.com.evil.invalid/b2api/v4/b2_hide_file")]
    [DataRow("POST", "https://api001.backblazeb2.com/unknown/b2_hide_file")]
    [DataRow("POST", "https://api001.backblazeb2.com/b2api/v4/b2_hide_file?bucketId=other")]
    [DataRow("POST", "https://pod-000-1007-13.backblaze.com/b2api/v4/b2_upload_file/bucket/unacknowledged")]
    [DataRow("POST", "https://api001.backblazeb2.com/b2api/v4/b2_delete_bucket")]
    public async Task UnexpectedMethodOriginPathOrOperationNeverReachesNetwork(string method, string url)
    {
        using var fixture = new TransportFixture();
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        { Content = JsonContent.Create(new { bucketId = "bucket", fileName = fixture.Scope.Source + "file.txt" }) };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Client.SendAsync(request));
        Assert.AreEqual(0, fixture.Inner.Requests); Assert.AreEqual(0, fixture.Transport.Mutations);
    }

    [TestMethod]
    public async Task MissingPreflightForeignCopyAndWrongBucketNeverReachNetwork()
    {
        using var fixture = new TransportFixture();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_hide_file", new { bucketId = "bucket", fileName = fixture.Scope.Source + "file.txt" }));
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_hide_file", new { bucketId = "other", fileName = fixture.Scope.Source + "file.txt" }));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_file", new { sourceFileId = "foreign", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "file.txt" }));
        fixture.Scope.Acknowledge("owned", fixture.Scope.Source + "file.txt");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_file", new { sourceFileId = "owned", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "different.txt" }));
        Assert.AreEqual(0, fixture.Inner.Requests); Assert.AreEqual(0, fixture.Transport.Mutations);
    }

    private sealed class TransportFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "CloudInlet-prefix-guard-" + Guid.NewGuid().ToString("N"));
        internal CloudPrefixAcceptanceScope Scope { get; } = CloudPrefixAcceptanceSafetyTests.Scope();
        internal FakeProvider Inner { get; }
        internal CloudPrefixAcceptanceTransport Transport { get; }
        internal HttpClient Client { get; }
        internal TransportFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Inner = new(Scope); Transport = new(Directory, Inner) { Scope = Scope }; Client = new(Transport);
        }
        internal Task<HttpResponseMessage> Post(string operation, object body) => Client.PostAsJsonAsync(Api + operation, body);
        internal async Task Authorize() { using var result = await Client.GetAsync("https://api.backblazeb2.com/b2api/v4/b2_authorize_account"); }
        public void Dispose()
        {
            Client.Dispose();
            var fullPath = Path.GetFullPath(Directory);
            var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "CloudInlet-prefix-guard-");
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup path.");
            System.IO.Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed class FakeProvider(CloudPrefixAcceptanceScope scope) : HttpMessageHandler
    {
        internal int Requests;
        internal int CopyRequests;
        internal bool RejectCopy;
        internal string? DeleteResponse;
        internal Action<HttpRequestMessage>? BeforeRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeforeRequest?.Invoke(request);
            Requests++;
            var uri = request.RequestUri!;
            if (uri.AbsoluteUri == Upload) return Json(new { fileId = "source-id", fileName = scope.Source + "file.txt" });
            return uri.Segments[^1] switch
            {
                "b2_authorize_account" => Json(new { authorizationToken = "private-auth", apiInfo = new { storageApi = new { apiUrl = "https://api001.backblazeb2.com" } } }),
                "b2_get_upload_url" => Json(new { uploadUrl = Upload, authorizationToken = "private-upload-auth" }),
                "b2_list_unfinished_large_files" => Json(new { files = Array.Empty<object>(), nextFileId = (string?)null }),
                "b2_copy_file" => RejectCopy ? new(HttpStatusCode.Conflict) : Json(new { fileId = ++CopyRequests == 1 ? "target-id" : "target-id-" + CopyRequests, fileName = scope.Destination + "file.txt" }),
                "b2_hide_file" => Json(new { fileId = "hide-id", fileName = scope.Destination + "file.txt" }),
                "b2_download_file_by_id" => new(HttpStatusCode.OK) { Content = new ByteArrayContent("abc"u8.ToArray()) },
                "b2_delete_file_version" => DeleteResponse switch
                {
                    "wrong-id" => Json(new { fileId = "foreign", fileName = scope.Destination + "file.txt" }),
                    "wrong-key" => Json(new { fileId = "target-id", fileName = scope.Destination + "changed.txt" }),
                    "rejected" => new(HttpStatusCode.Conflict),
                    _ => new(HttpStatusCode.OK) { Content = new StringContent(await request.Content!.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json") }
                },
                _ => throw new AssertFailedException("Unexpected fake provider request.")
            };
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}

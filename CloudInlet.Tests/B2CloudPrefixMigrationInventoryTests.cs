using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class B2CloudPrefixMigrationInventoryTests
{
    [TestMethod]
    public async Task ReadsAllVersionsAndHideMarkersWithoutCreatingOrDownloadingAnything()
    {
        using var fixture = new Fixture();
        fixture.SourcePages.Add(Page([Upload("new", 30), Hide("hidden", 20)], "CloudBay/file.txt", "old"));
        fixture.SourcePages.Add(Page([Upload("old", 10)]));
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        CollectionAssert.AreEqual(new[] { "new", "hidden", "old" }, result.SourceVersions.Select(v => v.FileId).ToArray());
        Assert.AreEqual("private-metadata", result.SourceVersions[0].Metadata["custom"]);
        Assert.AreEqual("text/custom", result.SourceVersions[0].ContentType);
        Assert.IsTrue(result.SourceVersions[1].ProtectionReadable, "Hide markers omit payload protection fields by API contract.");
        Assert.AreEqual(0, result.Blockers.Count);
        Assert.AreEqual(2, fixture.BucketReads);
        Assert.AreEqual(2, fixture.SourceReads);
        Assert.AreEqual(2, fixture.UnfinishedReads);
        Assert.IsTrue(fixture.Operations.All(v => v is "b2_authorize_account" or "b2_list_buckets" or "b2_list_file_versions" or "b2_list_unfinished_large_files"));
    }

    [TestMethod]
    public async Task EffectiveSseB2DefaultIsRecordedAndRequiresPreservationEvenWithoutExplicitConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Bucket["defaultServerSideEncryption"]!["value"]!["mode"] = "SSE-B2";
        fixture.Bucket["defaultServerSideEncryption"]!["value"]!["algorithm"] = "AES256";
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        Assert.AreEqual("SSE-B2", result.DefaultEncryptionMode);
        Assert.AreEqual("AES256", result.DefaultEncryptionAlgorithm);
        HasBlocker(result, "bucket-encryption-preservation-required");
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [DataRow("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709")]
    public async Task HideMarkerNullOrEmptyPayloadChecksumNormalizesWithoutChangingIdentity(string? checksum)
    {
        using var fixture = new Fixture();
        var marker = Hide("hidden", 20); marker["contentSha1"] = checksum;
        fixture.SourcePages.Add(Page([marker, Upload("older", 10)]));
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        var hidden = result.SourceVersions[0];
        Assert.AreEqual("hidden", hidden.FileId); Assert.AreEqual("hide", hidden.Action);
        Assert.AreEqual("CloudBay/file.txt", hidden.Key); Assert.AreEqual(20L, hidden.UploadedAtMillis);
        Assert.AreEqual(0L, hidden.Size); Assert.IsNull(hidden.Sha1); Assert.IsTrue(hidden.ProtectionReadable);
        Assert.AreEqual(0, result.Blockers.Count);
        Assert.AreEqual(new string('a', 40), result.SourceVersions[1].Sha1, "Upload checksums remain unchanged.");
    }

    [DataTestMethod]
    [DataRow("other-checksum")]
    [DataRow("none-checksum")]
    [DataRow("empty-checksum")]
    [DataRow("payload")]
    [DataRow("metadata")]
    [DataRow("mime")]
    [DataRow("fileRetention")]
    [DataRow("legalHold")]
    [DataRow("serverSideEncryption")]
    public async Task HideMarkerChecksumNormalizationDoesNotRelaxOtherMetadataGuards(string defect)
    {
        using var fixture = new Fixture();
        var marker = Hide("hidden", 20); marker["contentSha1"] = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        switch (defect)
        {
            case "other-checksum": marker["contentSha1"] = new string('a', 40); break;
            case "none-checksum": marker["contentSha1"] = "none"; break;
            case "empty-checksum": marker["contentSha1"] = ""; break;
            case "payload": marker["contentLength"] = 1; break;
            case "metadata": marker["fileInfo"]!["custom"] = "unexpected"; break;
            case "mime": marker["contentType"] = "application/octet-stream"; break;
            default: marker[defect] = null; break;
        }
        fixture.SourcePages.Add(Page([marker]));
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"));
    }

    [TestMethod]
    public async Task FilteredNullIsUnreadableProtectionAndNeverClaimedUnencrypted()
    {
        using var fixture = new Fixture();
        fixture.Bucket["defaultServerSideEncryption"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null };
        fixture.Bucket["fileLockConfiguration"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null };
        fixture.Bucket["replicationConfiguration"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null };
        var version = Upload("source", 1);
        version["fileRetention"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null };
        version["legalHold"] = new JsonObject { ["isClientAuthorizedToRead"] = false, ["value"] = null };
        version.Remove("serverSideEncryption");
        fixture.SourcePages.Add(Page([version]));
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        HasBlocker(result, "bucket-encryption-unreadable"); HasBlocker(result, "bucket-retention-unreadable");
        HasBlocker(result, "bucket-replication-unreadable"); HasBlocker(result, "version-protection-unreadable");
        Assert.IsFalse(result.SourceVersions.Single().ProtectionReadable);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopyAcknowledgmentActionIsRejectedInVersionListings(bool destination)
    {
        using var fixture = new Fixture();
        var version = Upload("version", 1, destination ? "CloudInlet/file.txt" : "CloudBay/file.txt");
        version["action"] = "copy";
        (destination ? fixture.TargetPages : fixture.SourcePages).Add(Page([version]));
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"));
        Assert.IsTrue(fixture.Operations.All(v => v is "b2_authorize_account" or "b2_list_buckets" or "b2_list_file_versions" or "b2_list_unfinished_large_files"));
    }

    [TestMethod]
    public async Task PreservesAndBlocksHistoricalRetentionEncryptionLegalHoldAndReplication()
    {
        using var fixture = new Fixture();
        var version = Upload("source", 1);
        version["fileRetention"]!["value"]!["mode"] = "compliance";
        version["fileRetention"]!["value"]!["retainUntilTimestamp"] = 100_000;
        version["legalHold"]!["value"] = "on";
        version["serverSideEncryption"]!["mode"] = "SSE-C";
        version["serverSideEncryption"]!["algorithm"] = "AES256";
        version["replicationStatus"] = "REPLICA";
        fixture.SourcePages.Add(Page([version]));
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        HasBlocker(result, "protected-versions"); HasBlocker(result, "version-encryption-preservation-required"); HasBlocker(result, "replicated-versions");
        var actual = result.SourceVersions.Single();
        Assert.AreEqual("compliance", actual.RetentionMode); Assert.AreEqual(100_000L, actual.RetainUntilMillis);
        Assert.AreEqual("on", actual.LegalHold); Assert.AreEqual("SSE-C", actual.EncryptionMode);
        Assert.AreEqual("AES256", actual.EncryptionAlgorithm); Assert.AreEqual("REPLICA", actual.ReplicationStatus);
    }

    [TestMethod]
    public async Task LifecycleOverlapsDefaultRetentionAndReplicationBecomeExplicitBlockers()
    {
        using var fixture = new Fixture();
        fixture.Bucket["lifecycleRules"] = new JsonArray(new JsonObject { ["fileNamePrefix"] = "CloudInlet/deep/", ["daysFromHidingToDeleting"] = 7 });
        fixture.Bucket["fileLockConfiguration"]!["value"]!["defaultRetention"]!["mode"] = "governance";
        fixture.Bucket["fileLockConfiguration"]!["value"]!["defaultRetention"]!["period"] = new JsonObject { ["duration"] = 1, ["unit"] = "days" };
        fixture.Bucket["replicationConfiguration"]!["asReplicationDestination"] = new JsonObject();
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        HasBlocker(result, "lifecycle-policy"); HasBlocker(result, "bucket-default-retention"); HasBlocker(result, "replication-policy");
    }

    [TestMethod]
    public async Task UnrelatedLifecyclePrefixDoesNotBlockReviewedFolders()
    {
        using var fixture = new Fixture();
        fixture.Bucket["lifecycleRules"] = new JsonArray(new JsonObject { ["fileNamePrefix"] = "unrelated/", ["daysFromHidingToDeleting"] = 7 });
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        Assert.AreEqual(0, result.Blockers.Count);
    }

    [TestMethod]
    public async Task DestinationHistoryAndUnfinishedUploadsArePreservedAndBlockMigration()
    {
        using var fixture = new Fixture();
        fixture.TargetPages.Add(Page([Upload("destination", 1, "CloudInlet/file.txt")]));
        fixture.UnfinishedPages.Add(new JsonObject { ["files"] = new JsonArray(Start("unfinished")), ["nextFileId"] = null });
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        HasBlocker(result, "destination-not-empty"); HasBlocker(result, "unfinished-uploads");
        Assert.AreEqual("unfinished", result.UnfinishedUploads.Single().FileId);
        Assert.AreEqual("destination", result.DestinationVersions.Single().FileId);
    }

    [TestMethod]
    public async Task LargeFileWholeChecksumIsTakenFromMetadataWithoutChangingStoredMetadata()
    {
        using var fixture = new Fixture();
        var version = Upload("large", 1); version["contentSha1"] = "none";
        version["fileInfo"]!["large_file_sha1"] = new string('b', 40);
        fixture.SourcePages.Add(Page([version]));
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        Assert.AreEqual(new string('b', 40), result.SourceVersions.Single().Sha1);
        Assert.AreEqual(new string('b', 40), result.SourceVersions.Single().Metadata["large_file_sha1"]);
        Assert.AreEqual(0, result.Blockers.Count);
    }

    [TestMethod]
    public async Task MissingChecksumIsExplicitAndDoesNotInventContentIntegrity()
    {
        using var fixture = new Fixture();
        var version = Upload("source", 1); version["contentSha1"] = "none";
        fixture.SourcePages.Add(Page([version]));
        using var store = await fixture.ConnectAsync();
        var result = await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/");
        HasBlocker(result, "missing-whole-file-checksum"); Assert.IsNull(result.SourceVersions.Single().Sha1);
    }

    [TestMethod]
    public async Task PolicyChangeBetweenInitialAndFinalReadRejectsStaleInventory()
    {
        using var fixture = new Fixture { ChangeRevision = true };
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"));
        Assert.AreEqual(2, fixture.BucketReads);
    }

    [TestMethod]
    public async Task RepeatedCursorAndDuplicateIdsCannotReturnPartialInventory()
    {
        using var fixture = new Fixture();
        fixture.SourcePages.Add(Page([Upload("same", 3)], "CloudBay/file.txt", "next"));
        fixture.SourcePages.Add(Page([Upload("same", 2)], "CloudBay/file.txt", "next"));
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"));
    }

    [TestMethod]
    public async Task InvalidPaginationAndUnsupportedVersionsFailClosed()
    {
        foreach (var defect in new[] { "missing-cursor", "wrong-bucket", "outside-prefix", "unknown-action", "nonstring-metadata", "newest-last", "reopened-key", "half-cursor" })
        {
            using var fixture = new Fixture();
            var version = Upload("source", 3); var page = Page([version]);
            switch (defect)
            {
                case "missing-cursor": page.Remove("nextFileId"); break;
                case "wrong-bucket": version["bucketId"] = "another-bucket"; break;
                case "outside-prefix": version["fileName"] = "Other/file.txt"; break;
                case "unknown-action": version["action"] = "folder"; break;
                case "nonstring-metadata": version["fileInfo"]!["custom"] = 42; break;
                case "newest-last": page = Page([Upload("older", 1), Upload("newer", 2)]); break;
                case "reopened-key": page = Page([Upload("one", 4), Upload("other", 2, "CloudBay/other.txt"), Upload("two", 1)]); break;
                case "half-cursor": page["nextFileName"] = "CloudBay/file.txt"; break;
            }
            fixture.SourcePages.Add(page);
            using var store = await fixture.ConnectAsync();
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"), defect);
        }
    }

    [TestMethod]
    public async Task RestrictedKeyCannotInspectOutsideItsAllowedBucketOrPrefix()
    {
        using var fixture = new Fixture { AllowedPrefix = "CloudBay/" };
        using var store = await fixture.ConnectAsync();
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"));
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => store.InspectPrefixMigrationAsync("different", "CloudBay/a/", "CloudBay/b/"));
        Assert.AreEqual(0, fixture.BucketReads);
    }

    [TestMethod]
    public async Task OverlappingOrUnnormalizedPrefixesAreRejectedBeforeApiReads()
    {
        using var fixture = new Fixture(); using var store = await fixture.ConnectAsync();
        foreach (var destination in new[] { "CloudBay/inside/", "CloudBay/", "CloudInlet", "CloudInlet/../", "/CloudInlet/", "CloudInlet\\folder/" })
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.InspectPrefixMigrationAsync("bucket", "CloudBay/", destination));
        Assert.AreEqual(0, fixture.BucketReads);
    }

    [TestMethod]
    public async Task MissingReplicationFieldNeverAssumesReplicationDisabled()
    {
        using var fixture = new Fixture(); fixture.Bucket.Remove("replicationConfiguration");
        using var store = await fixture.ConnectAsync();
        HasBlocker(await store.InspectPrefixMigrationAsync("bucket", "CloudBay/", "CloudInlet/"), "bucket-replication-unreadable");
    }

    private static void HasBlocker(B2CloudPrefixMigrationInventory value, string code) => Assert.IsTrue(value.Blockers.Any(v => v.Code == code), code);
    private static JsonObject Page(IEnumerable<JsonObject> files, string? nextName = null, string? nextId = null) => new()
    { ["files"] = new JsonArray(files.Select(v => (JsonNode)v).ToArray()), ["nextFileName"] = nextName, ["nextFileId"] = nextId };
    private static JsonObject Upload(string id, long timestamp, string name = "CloudBay/file.txt") => new()
    {
        ["accountId"] = "account", ["bucketId"] = "bucket", ["fileId"] = id, ["fileName"] = name, ["action"] = "upload",
        ["contentLength"] = 3, ["contentSha1"] = new string('a', 40), ["contentType"] = "text/custom", ["uploadTimestamp"] = timestamp,
        ["fileInfo"] = new JsonObject { ["custom"] = "private-metadata" },
        ["fileRetention"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = new JsonObject { ["mode"] = null, ["retainUntilTimestamp"] = null } },
        ["legalHold"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = "off" },
        ["serverSideEncryption"] = new JsonObject { ["mode"] = null, ["algorithm"] = null }
    };
    private static JsonObject Hide(string id, long timestamp) => new()
    {
        ["accountId"] = "account", ["bucketId"] = "bucket", ["fileId"] = id, ["fileName"] = "CloudBay/file.txt", ["action"] = "hide",
        ["contentLength"] = 0, ["contentSha1"] = null, ["contentType"] = "application/x-bz-hide-marker", ["uploadTimestamp"] = timestamp, ["fileInfo"] = new JsonObject()
    };
    private static JsonObject Start(string id)
    {
        var result = Upload(id, 1); result["action"] = "start"; result["contentLength"] = 0; result["contentSha1"] = "none"; return result;
    }
    private sealed class Fixture : IDisposable
    {
        public JsonObject Bucket { get; } = new()
        {
            ["accountId"] = "account", ["bucketId"] = "bucket", ["revision"] = 1,
            ["defaultServerSideEncryption"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = new JsonObject { ["mode"] = null, ["algorithm"] = null } },
            ["fileLockConfiguration"] = new JsonObject { ["isClientAuthorizedToRead"] = true, ["value"] = new JsonObject
                { ["isFileLockEnabled"] = false, ["defaultRetention"] = new JsonObject { ["mode"] = null, ["period"] = null } } },
            ["lifecycleRules"] = new JsonArray(), ["replicationConfiguration"] = new JsonObject { ["asReplicationSource"] = null, ["asReplicationDestination"] = null }
        };
        public List<JsonObject> SourcePages { get; } = [];
        public List<JsonObject> TargetPages { get; } = [];
        public List<JsonObject> UnfinishedPages { get; } = [];
        public List<string> Operations { get; } = [];
        public int BucketReads, SourceReads, TargetReads, UnfinishedReads;
        public bool ChangeRevision;
        public string? AllowedPrefix;
        public async Task<B2CloudStore> ConnectAsync()
        {
            var store = new B2CloudStore(new Handler(SendAsync));
            await store.ConnectAsync(new B2Credentials("fixture-key-id", "fixture-key")); return store;
        }
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var operation = request.RequestUri!.Segments[^1]; Operations.Add(operation);
            if (operation == "b2_authorize_account") return Json(JsonSerializer.SerializeToNode(new
            {
                accountId = "account", authorizationToken = "fixture-token", apiInfo = new { storageApi = new
                {
                    apiUrl = "https://api.invalid", downloadUrl = "https://download.invalid", recommendedPartSize = 5_000_000L, absoluteMinimumPartSize = 5_000_000L,
                    allowed = new { capabilities = new[] { "listBuckets", "listFiles", "readFiles", "readBucketReplications" }, namePrefix = AllowedPrefix,
                        buckets = new[] { new { id = "bucket", name = "fixture-bucket" } } }
                } }
            })!);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            switch (operation)
            {
                case "b2_list_buckets":
                    BucketReads++; var bucket = Bucket.DeepClone(); if (ChangeRevision && BucketReads > 1) bucket["revision"] = 2;
                    return Json(new JsonObject { ["buckets"] = new JsonArray(bucket) });
                case "b2_list_file_versions":
                    var source = body.RootElement.GetProperty("prefix").GetString() == "CloudBay/";
                    var pages = source ? SourcePages : TargetPages; var index = source ? SourceReads++ : TargetReads++;
                    return Json(pages.Count > index ? pages[index] : Page([]));
                case "b2_list_unfinished_large_files":
                    Assert.IsTrue(body.RootElement.GetProperty("maxFileCount").GetInt32() <= 100);
                    var pageIndex = UnfinishedReads++;
                    return Json(UnfinishedPages.Count > pageIndex ? UnfinishedPages[pageIndex] : new JsonObject { ["files"] = new JsonArray(), ["nextFileId"] = null });
                default: throw new AssertFailedException("Unexpected mutating or payload request: " + operation);
            }
        }
        public void Dispose() { }
    }
    private static HttpResponseMessage Json(JsonNode value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
}

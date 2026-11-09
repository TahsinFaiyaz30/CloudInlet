using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class CloudPrefixMultipartAcceptanceSafetyTests
{
    private const string Api = "https://api001.backblazeb2.com/b2api/v4/";
    private const string Upload = "https://pod-000-1007-13.backblaze.com/b2api/v4/b2_upload_file/bucket/session";
    private static readonly byte[] Seed = new byte[CloudPrefixMultipartAcceptanceTransport.SeedLength];
    private static CloudAccount Account() => new("account", Api, "https://f001.backblazeb2.com",
        CloudPrefixAcceptanceScope.RequiredCapabilities, "bucket");
    private static B2CloudPrefixMigrationInventory Inventory(CloudPrefixAcceptanceScope scope, params CloudPrefixVersion[] versions) =>
        new("account", "bucket", 1, scope.Source, scope.Destination, null, null, versions, [], [], []);

    [DataTestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 10)]
    [DataRow(3, 13)]
    [DataRow(27, 31)]
    public void RepeatedContentChecksumMatchesIndependentExpandedBytes(int offset, int length)
    {
        byte[] seed = [1, 37, 9, 255, 0, 11, 18];
        var expanded = Enumerable.Range(offset, length).Select(index => seed[index % seed.Length]).ToArray();
        Assert.AreEqual(Convert.ToHexString(SHA1.HashData(expanded)).ToLowerInvariant(),
            CloudPrefixMultipartProviderAcceptanceTests.RepeatedHash(seed, offset, length));
    }

    [TestMethod]
    public async Task RealUploadHostNeedsExactAcknowledgedUrlAndCleanupUpdatesSharedOwnership()
    {
        using var fixture = new Fixture();
        await fixture.Authorize();
        fixture.Scope.EnableWrites(Account(), Inventory(fixture.Scope));
        using (await fixture.Post("b2_get_upload_url", new { bucketId = "bucket" })) { }
        using (var wrong = fixture.UploadRequest(Upload + "/unacknowledged"))
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Client.SendAsync(wrong));
        Assert.AreEqual(2, fixture.Inner.Requests);
        using (var request = fixture.UploadRequest(Upload))
        using (await fixture.Client.SendAsync(request)) { }
        Assert.AreEqual(1, fixture.Scope.Owned.Count);
        Assert.AreEqual(CloudPrefixMultipartAcceptanceTransport.SeedLength, fixture.Transport.UploadedPayloadBytes);
        Assert.IsFalse(fixture.Transport.HasUnconfirmedMutation);
        var version = new CloudPrefixVersion("seed-id", fixture.Scope.Source + "seed.bin", "upload", Seed.Length,
            fixture.Transport.SeedHash, 1, "application/octet-stream", new Dictionary<string, string>());
        fixture.Scope.EnableCleanup(Inventory(fixture.Scope, version));
        using (await fixture.Post("b2_delete_file_version", new { fileId = "seed-id", fileName = version.Key })) { }
        Assert.AreEqual(0, fixture.Scope.RemainingOwned.Count);
        Assert.AreEqual(1, fixture.Transport.Deleted.Count);
        fixture.Transport.RequirePolicy(Inventory(fixture.Scope), allowOwnedUnfinished: false);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_delete_file_version", new { fileId = "seed-id", fileName = version.Key }));
        Assert.AreEqual(4, fixture.Inner.Requests);
        Assert.AreEqual(2, File.ReadAllLines(Path.Combine(fixture.Directory, "multipart-acknowledgments.jsonl")).Length);
    }

    [TestMethod]
    public async Task UnknownMutationPreventsAnyFollowingCreationAndRetainsDurableAttempt()
    {
        using var fixture = new Fixture();
        await fixture.Authorize(); fixture.EnableSeed();
        fixture.Inner.LoseStartResponse = true;
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Start());
        Assert.IsTrue(fixture.Transport.HasUnconfirmedMutation);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_file", new
        { sourceFileId = "seed-id", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "seed.bin" }));
        Assert.AreEqual(2, fixture.Inner.Requests);
        Assert.AreEqual(1, File.ReadAllLines(Path.Combine(fixture.Directory, "multipart-attempts.jsonl")).Length);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory, "multipart-acknowledgments.jsonl")));
    }

    [TestMethod]
    public async Task WrongAccountAcknowledgmentCannotEstablishOwnedUnfinishedIdentity()
    {
        using var fixture = new Fixture();
        await fixture.Authorize(); fixture.EnableSeed(); fixture.Inner.WrongAccount = true;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Start());
        Assert.AreEqual(1, fixture.Scope.Owned.Count);
        Assert.AreEqual(0, fixture.Transport.Unfinished.Count);
        Assert.IsTrue(fixture.Transport.HasUnconfirmedMutation);
    }

    [TestMethod]
    public async Task SourcePartRequiresExactRangeAndCannotReplayAcknowledgedPart()
    {
        using var fixture = new Fixture();
        await fixture.Authorize(); fixture.EnableSeed();
        using (await fixture.Start()) { }
        object Part(string range) => new { sourceFileId = "seed-id", largeFileId = "large-id", partNumber = 1, range };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", Part("bytes=1-50000000")));
        Assert.AreEqual(2, fixture.Inner.Requests);
        using (await fixture.Post("b2_copy_part", Part("bytes=0-49999999"))) { }
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", Part("bytes=0-49999999")));
        Assert.AreEqual(3, fixture.Inner.Requests);
        Assert.AreEqual(2, fixture.Transport.Mutations);
        Assert.IsFalse(fixture.Transport.HasUnconfirmedMutation);
    }

    [TestMethod]
    public async Task UnknownPartAllowsOnlyIdenticalImmutablePartRetryAndStillRejectsCompletedReplay()
    {
        using var fixture = new Fixture();
        await fixture.Authorize(); fixture.EnableSeed();
        using (await fixture.Start()) { }
        object Part(int number = 1, string source = "seed-id", string range = "bytes=0-49999999") =>
            new { sourceFileId = source, largeFileId = "large-id", partNumber = number, range };
        fixture.Inner.LosePartResponse = true;
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Post("b2_copy_part", Part()));
        Assert.IsTrue(fixture.Transport.HasUnconfirmedMutation);
        var sent = fixture.Inner.Requests;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", Part(2)));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", Part(source: "foreign")));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", Part(range: "bytes=1-50000000")));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_file", new
        { sourceFileId = "seed-id", destinationBucketId = "bucket", fileName = fixture.Scope.Destination + "seed.bin" }));
        Assert.AreEqual(sent, fixture.Inner.Requests);
        fixture.Inner.LosePartResponse = false;
        using (await fixture.Post("b2_copy_part", Part())) { }
        Assert.IsFalse(fixture.Transport.HasUnconfirmedMutation);
        Assert.AreEqual(1, fixture.Transport.ReplayedUnconfirmedParts);
        Assert.AreEqual(2, fixture.Transport.SourcePartRequests);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", Part()));
        Assert.AreEqual(sent + 1, fixture.Inner.Requests);
        Assert.AreEqual(3, File.ReadAllLines(Path.Combine(fixture.Directory, "multipart-attempts.jsonl")).Length);
    }

    [DataTestMethod]
    [DataRow("valid")]
    [DataRow("foreign-unfinished")]
    [DataRow("source-changed")]
    [DataRow("bad-saved-hash")]
    [DataRow("completed-switch")]
    [DataRow("foreign-account")]
    public async Task MultipartRecoveryRequiresExactProtectedProgressAndCompleteLiveInventory(string change)
    {
        using var fixture = new Fixture();
        await fixture.Authorize();
        var source = new CloudPrefixVersion("source-id", fixture.Scope.Source + "multipart.bin", "upload",
            CloudPrefixMultipartAcceptanceTransport.LargeLength, new string('a', 40), 1, "application/octet-stream", new Dictionary<string, string>());
        var seed = source with { FileId = "seed-id", Key = fixture.Scope.Source + "seed.bin", Size = Seed.Length, Sha1 = fixture.Transport.SeedHash };
        var unfinished = source with { FileId = "large-id", Key = fixture.Scope.Destination + "multipart.bin", Action = "start", Size = 0, Sha1 = null };
        var hashes = new string?[48]; hashes[0] = new string('b', 40);
        var intent = new B2CloudPrefixCopyIntent(new string('c', 64), source, unfinished.Key, B2CloudPrefixCopyPhase.CopyingParts,
            unfinished.FileId, CloudPrefixMultipartAcceptanceTransport.MigrationPartLength, hashes);
        var history = new CloudPrefixMigrationCheckpoint(1, "fingerprint", "cloud", "account", "bucket", fixture.Scope.Source, fixture.Scope.Destination,
            "fingerprint", CloudPrefixMigrationPhase.Copying,
            [new(source, unfinished.Key, intent.OperationId, true), new(seed, fixture.Scope.Destination + "seed.bin", new string('d', 64))]);
        var provider = new B2CloudPrefixCopyCheckpoint(1, "account", "bucket", fixture.Scope.Source, fixture.Scope.Destination, 1, [intent]);
        var inventory = Inventory(fixture.Scope, source, seed) with { DestinationVersions = [unfinished], UnfinishedUploads = [unfinished] };
        if (change == "foreign-unfinished") inventory = inventory with { UnfinishedUploads = [unfinished with { FileId = "foreign" }] };
        if (change == "source-changed") inventory = inventory with { SourceVersions = [source with { Sha1 = new string('e', 40) }, seed] };
        if (change == "bad-saved-hash") { hashes[0] = new string('e', 40); provider = provider with { Intents = [intent with { PartHashes = hashes }] }; }
        if (change == "completed-switch") history = history with { Phase = CloudPrefixMigrationPhase.Switched };
        if (change == "foreign-account") inventory = inventory with { AccountId = "foreign" };
        if (change == "valid")
        {
            fixture.Transport.RestoreAuthenticatedMultipart(Account(), inventory, history, provider);
            Assert.AreEqual(3, fixture.Scope.Owned.Count);
            Assert.AreEqual("large-id", fixture.Transport.DestinationLargeId);
            var sent = fixture.Inner.Requests;
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Post("b2_copy_part", new
            { sourceFileId = "source-id", largeFileId = "large-id", partNumber = 1, range = "bytes=0-104857599" }));
            Assert.AreEqual(sent, fixture.Inner.Requests, "An authenticated completed part must not be resent.");
        }
        else
        {
            Assert.ThrowsException<InvalidDataException>(() => fixture.Transport.RestoreAuthenticatedMultipart(Account(), inventory, history, provider));
            Assert.IsFalse(fixture.Scope.WritesEnabled);
            Assert.AreEqual(0, fixture.Scope.Owned.Count);
        }
        Assert.AreEqual(0, fixture.Transport.Mutations);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "CloudInlet-multipart-guard-" + Guid.NewGuid().ToString("N"));
        internal CloudPrefixAcceptanceScope Scope { get; } = new("bucket", "test/", Guid.NewGuid());
        internal FakeProvider Inner { get; }
        internal CloudPrefixMultipartAcceptanceTransport Transport { get; }
        internal HttpClient Client { get; }
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Inner = new(Scope); Transport = new(Directory, Seed, new string('a', 40), Enumerable.Repeat(new string('b', 40), 48).ToArray(), Inner) { Scope = Scope };
            Inner.SeedHash = Transport.SeedHash; Client = new(Transport);
        }
        internal Task<HttpResponseMessage> Post(string operation, object body) => Client.PostAsJsonAsync(Api + operation, body);
        internal async Task Authorize() { using var result = await Client.GetAsync("https://api.backblazeb2.com/b2api/v4/b2_authorize_account"); }
        internal void EnableSeed() { Scope.EnableWrites(Account(), Inventory(Scope)); Scope.Acknowledge("seed-id", Scope.Source + "seed.bin"); }
        internal Task<HttpResponseMessage> Start() => Post("b2_start_large_file", new
        { bucketId = "bucket", fileName = Scope.Source + "multipart.bin", fileInfo = new { large_file_sha1 = new string('a', 40) } });
        internal HttpRequestMessage UploadRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Seed) };
            request.Headers.Add("X-Bz-Content-Sha1", Transport.SeedHash);
            request.Headers.Add("X-Bz-File-Name", Uri.EscapeDataString(Scope.Source + "seed.bin"));
            return request;
        }
        public void Dispose()
        {
            Client.Dispose();
            var fullPath = Path.GetFullPath(Directory);
            var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "CloudInlet-multipart-guard-");
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup path.");
            System.IO.Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed class FakeProvider(CloudPrefixAcceptanceScope scope) : HttpMessageHandler
    {
        internal int Requests;
        internal bool LoseStartResponse, LosePartResponse, WrongAccount;
        internal string SeedHash = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.RequestUri!.AbsoluteUri == Upload) return Json(new
            { accountId = "account", bucketId = "bucket", fileId = "seed-id", fileName = scope.Source + "seed.bin", contentLength = Seed.Length, contentSha1 = SeedHash });
            switch (request.RequestUri.Segments[^1])
            {
                case "b2_authorize_account": return Json(new { accountId = "account", authorizationToken = "fake-private-auth", apiInfo = new { storageApi = new { apiUrl = "https://api001.backblazeb2.com" } } });
                case "b2_get_upload_url": return Json(new { uploadUrl = Upload, authorizationToken = "fake-upload-auth" });
                case "b2_start_large_file":
                    if (LoseStartResponse) throw new HttpRequestException("Injected lost response.");
                    return Json(new { accountId = WrongAccount ? "foreign" : "account", bucketId = "bucket", fileId = "large-id", fileName = scope.Source + "multipart.bin" });
                case "b2_copy_part":
                    if (LosePartResponse) throw new HttpRequestException("Injected lost part response.");
                    return Json(new { fileId = "large-id", partNumber = 1, contentLength = Seed.Length, contentSha1 = SeedHash });
                case "b2_delete_file_version":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token))) return Json(body.RootElement.Clone());
                default: throw new AssertFailedException("Unexpected fake provider endpoint.");
            }
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}

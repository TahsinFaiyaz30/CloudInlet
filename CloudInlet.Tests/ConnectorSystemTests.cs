using System.IO.Compression;
using System.Net;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Connectors;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Connectors;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using ExampleConnector;
using Fixture.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ConnectorSystemTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CloudInlet-packs-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly VersionRange V1 = new("1.0.0", "2.0.0");
    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    private string CatalogPath => Path.Combine(_root, "catalog.json");
    private ConnectorManager Manager() => new(Path.Combine(_root, "state", "Connectors"), new(new Uri(CatalogPath).AbsoluteUri, PublicKey));
    [TestInitialize] public void Initialize() => Directory.CreateDirectory(_root);
    [TestCleanup] public void Clean() { _key.Dispose(); Directory.Delete(_root, true); }

    private ConnectorOffer Pack<T>(string id, string version = "1.0.0", ConnectorDependency[]? deps = null,
        VersionRange? sdk = null, ECDsa? key = null) where T : new()
    {
        key ??= _key;
        var type = typeof(T);
        var file = Path.GetFileName(type.Assembly.Location);
        var bytes = File.ReadAllBytes(type.Assembly.Location);
        var m = new ConnectorManifest(1, id, id, "Fixture publisher", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            version, sdk ?? V1, ["windows"], ["x64"], file, type.FullName!,
            new T() is IProviderConnector p ? p.Capabilities : 0, deps ?? [], "MIT", "README.md", "Fixture release",
            new() { [file] = Convert.ToHexString(SHA256.HashData(bytes)) }, typeof(IConnectorComponent).IsAssignableFrom(type) ? "component" : "provider");
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".cipack");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
            var manifest = JsonSerializer.SerializeToUtf8Bytes(m, ConnectorFormat.Json);
            Add("manifest.json", manifest); Add("manifest.sig", key.SignData(manifest, HashAlgorithmName.SHA256)); Add(file, bytes);
        }
        return ConnectorManager.InspectLocal(path);
    }
    private void Catalog(params ConnectorOffer[] offers)
    {
        var entries = offers.Select(o => new CatalogEntry(o.Manifest, o.Hash, [Path.GetFileName(new Uri(o.Locations[0]).LocalPath)])).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ConnectorCatalog(1, entries), ConnectorFormat.Json);
        File.WriteAllBytes(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(new SignedConnectorCatalog(PublicKey,
            Convert.ToBase64String(bytes), Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256))), ConnectorFormat.Json));
    }
    private ConnectorDependency Dep(string id, bool optional = false) => new(id, ConnectorFormat.Fingerprint(PublicKey), V1, optional);
    [TestMethod]
    public async Task SharedComponentResolvesBeforeProviderAndPreventsRemoval()
    {
        var component = Pack<StorageComponent>("fixture.shared");
        var provider = Pack<DependentProvider>("fixture.dependent", deps: [Dep("fixture.shared"), Dep("fixture.optional", true)]);
        Catalog(provider, component);
        var manager = Manager(); await manager.RefreshAsync();
        var plan = manager.Plan(["fixture.dependent"]);
        CollectionAssert.AreEqual(new[] { "fixture.shared", "fixture.dependent" }, plan.Ordered.Select(o => o.Manifest.Id).ToArray());
        Assert.IsTrue(plan.Notices.Single().Contains("fixture.optional"));
        await manager.InstallAsync(plan);
        var loaded = manager.Provider("fixture.dependent");
        Assert.AreNotEqual(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.GetType().Assembly));
        Assert.AreEqual("Root from shared component", (await loaded.Open(new("account", new())).GetRootsAsync("account", default)).Single().DisplayName);
        Assert.ThrowsException<InvalidOperationException>(() => manager.Change("fixture.shared", "uninstall"));
        manager.Change("fixture.dependent", "disable"); manager.Change("fixture.shared", "disable");
        Assert.ThrowsException<ConnectorUnavailableException>(() => manager.Provider("fixture.dependent"));
        var enable = manager.Plan(["fixture.dependent"]);
        Assert.AreEqual(3, enable.Notices.Count);
        await manager.InstallAsync(enable);
        Assert.IsNull(manager.Issue("fixture.dependent"));
    }

    [TestMethod]
    public void MissingCyclicConflictingPublisherAndSdkRejectedBeforeLoading()
    {
        var manager = Manager();
        var dep = Pack<DependentProvider>("fixture.dependent", deps: [Dep("fixture.shared")]);
        Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["fixture.dependent"], imports: [dep]));
        var cycle = Pack<StorageComponent>("fixture.shared", deps: [Dep("fixture.dependent")]);
        StringAssert.Contains(Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["fixture.dependent"], imports: [dep, cycle])).Message, "Circular");
        var wrong = Pack<StorageComponent>("fixture.shared", "2.0.0");
        Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["fixture.dependent"], imports: [dep, wrong]));
        using var outsider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stolen = Pack<B2Connector>("b2", key: outsider);
        Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["b2"], imports: [stolen]));
        var future = Pack<ExampleProvider>("example.readonly", sdk: new("2.0.0", "3.0.0"));
        StringAssert.Contains(Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["example.readonly"], imports: [future])).Message, "SDK");
    }

    [TestMethod]
    public async Task UnknownCapabilitiesKeepOtherSignedCatalogEntriesUsable()
    {
        var current = Pack<ExampleProvider>("example.readonly");
        var future = Pack<DependentProvider>("fixture.future");
        Catalog(current, future);
        var envelope = ConnectorFormat.Parse<SignedConnectorCatalog>(File.ReadAllBytes(CatalogPath));
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(envelope.Payload))!;
        payload["packages"]![1]!["manifest"]!["capabilities"] = "Browse, FutureFeature";
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        File.WriteAllBytes(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(bytes),
            Signature = Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256)) }, ConnectorFormat.Json));
        var manager = Manager();
        await manager.RefreshAsync();
        StringAssert.Contains(Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["fixture.future"])).Message, "Update CloudInlet");
        await manager.InstallAsync(manager.Plan(["example.readonly"]));
        Assert.AreEqual("example.readonly", manager.Provider("example.readonly").Id);
        Assert.IsFalse(ConnectorFormat.ValidId("con.private"));
    }

    [TestMethod]
    public async Task UpdateInterruptionPreservesOldVersionAndRollbackAcrossRestart()
    {
        var manager = Manager(); var one = Pack<ExampleProvider>("example.readonly");
        await manager.InstallAsync(manager.Plan(["example.readonly"], imports: [one]));
        var loaded = manager.Provider("example.readonly");
        var two = Pack<ExampleProvider>("example.readonly", "1.1.0");
        var extra = Pack<StorageComponent>("fixture.shared");
        var plan = manager.Plan(["example.readonly", "fixture.shared"], true, [two, extra]);
        File.Delete(new Uri(extra.Locations[0]).LocalPath);
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => manager.InstallAsync(plan));
        Assert.AreEqual(one.Hash, Manager().Snapshot.Installed["example.readonly"].Current.Hash);
        Assert.IsFalse(Manager().Snapshot.Installed.ContainsKey("fixture.shared"));
        var retry = manager.Plan(["example.readonly"], true, [two]);
        await manager.InstallAsync(retry);
        Assert.IsTrue(manager.NeedsRestart("example.readonly")); Assert.AreSame(loaded, manager.Provider("example.readonly"));
        var reopened = Manager();
        Assert.IsFalse(reopened.NeedsRestart("example.readonly")); Assert.AreEqual(two.Hash, reopened.Snapshot.Installed["example.readonly"].Current.Hash);
        reopened.Change("example.readonly", "rollback");
        Assert.AreEqual(one.Hash, Manager().Snapshot.Installed["example.readonly"].Current.Hash);
        reopened.Change("example.readonly", "uninstall");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var changed = Pack<ExampleProvider>("example.readonly", "1.2.0", key: other);
        Assert.ThrowsException<InvalidDataException>(() => reopened.Plan(["example.readonly"], true, [changed]));
    }

    [DataTestMethod]
    [DataRow("signature")] [DataRow("payload")] [DataRow("outside")] [DataRow("missing")] [DataRow("duplicate")]
    public void InvalidArchivesRejected(string change)
    {
        var p = Pack<ExampleProvider>("example.readonly"); var path = new Uri(p.Locations.Single()).LocalPath;
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (change == "outside") zip.CreateEntry("../other.dll");
            if (change == "duplicate") zip.CreateEntry("MANIFEST.JSON");
            if (change == "missing") zip.GetEntry(p.Manifest.Assembly)!.Delete();
            if (change is "signature" or "payload")
            {
                var entry = zip.GetEntry(change == "signature" ? "manifest.sig" : p.Manifest.Assembly)!;
                using var stream = entry.Open(); var first = stream.ReadByte(); stream.Position = 0; stream.WriteByte((byte)(first ^ 255));
            }
        }
        Assert.ThrowsException<InvalidDataException>(() => ConnectorPackage.Inspect(path));
    }

    [TestMethod]
    public async Task FreshProfilesAcquireOnlyReviewedProvidersAndRemoveCodeIndependently()
    {
        var b2 = Pack<B2Connector>("b2");
        var drive = Pack<OneDriveConnector>("onedrive");
        Catalog(b2, drive);
        foreach (var profile in new[] { "Exe", "Msi", "Portable", "MsixLocalState" })
        {
            var root = Path.Combine(_root, profile, "ProviderPacks");
            var manager = new ConnectorManager(root, new(new Uri(CatalogPath).AbsoluteUri, PublicKey));
            Assert.AreEqual(0, manager.Snapshot.Installed.Count);
            await manager.RefreshAsync();
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "packages")));
            await manager.InstallAsync(manager.Plan(["b2"]));
            Assert.AreEqual("b2", manager.Provider("b2").Id);
            Assert.AreEqual(1, Directory.GetFiles(Path.Combine(root, "packages")).Length);
            Assert.IsNotNull(manager.Issue("onedrive"));
            manager.Change("b2", "disable"); manager.Change("b2", "enable");
            manager.Change("b2", "uninstall");
            Assert.AreEqual(0, manager.Snapshot.Installed.Count);
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(root, "packages")).Length);
        }
    }
    [TestMethod]
    public async Task PrivatePackageCopiesThroughControllerAndUninstallRetainsAccountsAndJournal()
    {
        var manager = Manager(); var p = Pack<ExampleProvider>("example.readonly");
        var storage = new ClientStorage(Path.Combine(_root, "state"));
        var account = new ClientStorage.OneDriveConnection("retained", "Existing account", Guid.NewGuid().ToString(), "common", new("fake", "fake", DateTimeOffset.UnixEpoch, "scope"));
        storage.SaveOneDriveConnections([account]);
        var vault = Path.Combine(storage.DirectoryPath, "onedrive.dpapi");
        var before = File.ReadAllBytes(vault);
        await manager.InstallAsync(manager.Plan(["example.readonly"], imports: [p]));
        var dest = Path.Combine(_root, "destination"); Directory.CreateDirectory(dest);
        string id;
        await using (var c = new ClientController(storage, _ => Assert.Fail("Unexpected root change"), false, _ => null, manager))
        {
            var source = (await c.CustomRootsAsync(p.Manifest.Id, "default", default)).Single();
            var target = LocalTransferEndpoint.ForFolder(dest);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => c.StartCloudTransferAsync(source, target, TransferOperation.Move, TransferConflictPolicy.Fail, []));
            id = await c.StartCloudTransferAsync(source, target, TransferOperation.Copy, TransferConflictPolicy.Fail, []);
            await c.WaitForCloudTransferAsync(id);
            Assert.AreEqual("Hello from a private CloudInlet connector!\n", File.ReadAllText(Path.Combine(dest, "welcome.txt")));
            c.ChangeConnector(p.Manifest.Id, "uninstall");
            CollectionAssert.AreEqual(new[] { "onedrive" }, c.RequiredConnectors());
        }
        await using var reboot = new ClientController(storage, _ => Assert.Fail(), false, _ => null, Manager());
        Assert.AreEqual(TransferJobState.Completed, reboot.CloudTransferJobs.Single(j => j.Plan.Id == id).State);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(vault));
    }

    [TestMethod]
    public async Task PrivateAccountVaultSurvivesUninstallAndReinstallation()
    {
        var manager = Manager();
        var shared = Pack<StorageComponent>("fixture.shared");
        var provider = Pack<DependentProvider>("fixture.dependent", deps: [Dep("fixture.shared")]);
        var plan = manager.Plan(["fixture.dependent"], imports: [provider, shared]);
        await manager.InstallAsync(plan);
        var storage = new ClientStorage(Path.Combine(_root, "state"));
        await using var controller = new ClientController(storage, _ => Assert.Fail(), false, _ => null, manager);
        const string secret = "private-fixture-secret";
        await controller.ConnectCustomAccountAsync("fixture.dependent", new Dictionary<string, string> { ["key"] = secret }, default);
        var account = controller.CustomAccounts("fixture.dependent").Single();
        Assert.AreEqual("test-account", account.Id);
        var vault = Path.Combine(storage.DirectoryPath, "ConnectorAccounts", "fixture.dependent.dpapi");
        var encrypted = File.ReadAllBytes(vault);
        Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains(secret));
        Assert.AreEqual("test-account", (await controller.CustomRootsAsync("fixture.dependent", account.Id, default)).Single().AccountId);
        controller.ChangeConnector("fixture.dependent", "uninstall");
        Assert.IsFalse(File.Exists(Path.Combine(storage.DirectoryPath, "Connectors", "packages", provider.Hash + ".cipack")));
        CollectionAssert.Contains(controller.RequiredConnectors(), "fixture.dependent");
        await manager.InstallAsync(plan);
        CollectionAssert.AreEqual(encrypted, File.ReadAllBytes(vault));
        Assert.AreEqual(account, controller.CustomAccounts("fixture.dependent").Single());
    }

    [TestMethod]
    public async Task SignedCatalogPinsSourceAndRejectsTamperedUpdateMetadata()
    {
        var manager = Manager(); var p = Pack<ExampleProvider>("example.readonly"); Catalog(p); await manager.RefreshAsync();
        await manager.InstallAsync(manager.Plan(["example.readonly"]));
        var update = Pack<ExampleProvider>("example.readonly", "1.1.0");
        Assert.ThrowsException<InvalidDataException>(() => manager.Plan(["example.readonly"], true, [update]));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => manager.InstallAsync(new([update], [])));
        var envelope = ConnectorFormat.Parse<SignedConnectorCatalog>(File.ReadAllBytes(CatalogPath));
        File.WriteAllBytes(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(envelope with { Signature = Convert.ToBase64String(new byte[64]) }, ConnectorFormat.Json));
        await Assert.ThrowsExceptionAsync<IOException>(() => manager.RefreshAsync());
        Assert.AreEqual(0, manager.Offers.Length);
        Assert.AreEqual(p.Hash, manager.Snapshot.Installed["example.readonly"].Current.Hash);
    }

    [TestMethod]
    public async Task InterruptedJobWaitsAcrossRestartsAndResumesAfterInstallation()
    {
        var manager = Manager();
        var storage = new ClientStorage(Path.Combine(_root, "state"));
        storage.SaveCredentials(new("retained-key", "retained-secret"));
        var vault = Path.Combine(storage.DirectoryPath, "credentials.dpapi");
        var original = File.ReadAllBytes(vault);
        var dest = Path.Combine(_root, "recovered"); Directory.CreateDirectory(dest);
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), new("example.readonly", "default", "samples", "root", "", "Samples"),
            LocalTransferEndpoint.ForFolder(dest), TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        using (var journal = new TransferJobJournal(Path.Combine(storage.DirectoryPath, "CloudTransfers", "jobs.sqlite"), new TestProtector())) journal.Create(plan);
        async Task Wait(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ready()) await Task.Delay(20, timeout.Token);
        }
        await using (var first = new ClientController(storage, _ => Assert.Fail(), false, _ => null, manager))
        {
            await first.StartAsync();
            await Wait(() => first.CloudTransferJobs.Single().Error?.Contains("Install required connectors") == true);
            Assert.AreEqual(0, Directory.GetFiles(dest).Length);
        }
        var pack = Pack<ExampleProvider>("example.readonly");
        await manager.InstallAsync(manager.Plan(["example.readonly"], imports: [pack]));
        await using var restart = new ClientController(storage, _ => Assert.Fail(), false, _ => null, Manager());
        await restart.StartAsync();
        await Wait(() => restart.CloudTransferJobs.Single().State == TransferJobState.Completed);
        Assert.IsTrue(File.Exists(Path.Combine(dest, "welcome.txt")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(vault));
    }

    [TestMethod]
    public async Task AutomaticUpdateHonorsGlobalAndIndividualPreferences()
    {
        var manager = Manager(); var old = Pack<ExampleProvider>("example.readonly"); Catalog(old);
        await manager.RefreshAsync(); await manager.InstallAsync(manager.Plan(["example.readonly"]));
        var update = Pack<ExampleProvider>("example.readonly", "1.1.0"); Catalog(update);
        var storage = new ClientStorage(Path.Combine(_root, "state"));
        await using var controller = new ClientController(storage, _ => Assert.Fail(), false, _ => null, manager);
        await controller.CheckAutomaticConnectorsAsync(default);
        Assert.AreEqual(old.Hash, manager.Snapshot.Installed["example.readonly"].Current.Hash);
        manager.SetAutomatic(true); manager.Change("example.readonly", "automatic");
        await controller.CheckAutomaticConnectorsAsync(default);
        Assert.AreEqual(old.Hash, manager.Snapshot.Installed["example.readonly"].Current.Hash);
        manager.Change("example.readonly", "automatic");
        await controller.CheckAutomaticConnectorsAsync(default);
        Assert.AreEqual(update.Hash, manager.Snapshot.Installed["example.readonly"].Current.Hash);
    }

    [TestMethod]
    public async Task LoadedOneDriveSourceAndB2DestinationCompleteVerifiedStreamingJob()
    {
        var manager = Manager();
        var sourcePack = Pack<OneDriveConnector>("onedrive"); var destPack = Pack<B2Connector>("b2");
        Catalog(sourcePack, destPack); await manager.RefreshAsync();
        await manager.InstallAsync(manager.Plan(["onedrive", "b2"]));
        using var server = new B2TransferEndpointTests.Server();
        var bandwidth = new TransferBandwidthBudget();
        using var b2 = (IBackupSession)manager.Provider("b2").Open(new ConnectorContext("account", bandwidth) { CreateHttpHandler = () => new B2TransferEndpointTests.Handler(server) });
        await b2.ConnectAsync(new("test-key", "test-secret"));
        var graph = new GraphFixture();
        var session = manager.Provider("onedrive").Open(new ConnectorContext("drive-account", bandwidth,
            JsonSerializer.Serialize(new OneDriveTokenSet("fixture-access", "fixture-refresh", DateTimeOffset.UtcNow.AddDays(1), "scope")),
            Options: new Dictionary<string, string> { ["clientId"] = Guid.NewGuid().ToString(), ["tenant"] = "common" })
            { CreateHttpHandler = () => graph });
        var source = new TransferLocation("onedrive", "drive-account", "drive", "root", "", "Source");
        var dest = new TransferLocation("b2", "account", "bucket", "", "dest", "Destination");
        var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source, dest, TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        await using var engine = new TransferJobEngine(new TransferJobJournal(Path.Combine(_root, "jobs.sqlite"), new TestProtector()),
            loc => (loc.Provider == "b2" ? (IConnectorSession)b2 : session).OpenEndpoint(loc));
        await engine.CreateAsync(plan); await engine.RunAsync(plan.Id);
        var job = engine.Snapshots().Single();
        Assert.AreEqual(TransferJobState.Completed, job.State, job.Error + string.Join(" ", job.Items.Select(i => i.Error)));
        Assert.AreEqual(1L, job.CompletedFiles); Assert.AreEqual(1, server.UploadCalls);
        Assert.IsTrue(graph.Ranges > 0); Assert.AreEqual(0, server.DeleteCalls);
        Assert.AreEqual(graph.Hash, JsonSerializer.SerializeToElement(server.Files.Values.Single()).GetProperty("contentSha1").GetString());
    }
    private sealed class GraphFixture : HttpMessageHandler
    {
        private readonly byte[] _data = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        public string Hash => Convert.ToHexString(SHA1.HashData(_data)).ToLowerInvariant();
        public int Ranges;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var uri = req.RequestUri!;
            if (uri.Host == "payload.invalid")
            {
                Assert.IsNull(req.Headers.Authorization);
                var range = req.Headers.Range!.Ranges.Single(); var offset = (int)range.From!.Value; var end = (int)range.To!.Value;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_data[offset..(end + 1)]) };
                response.Content.Headers.ContentRange = new(offset, end, _data.Length); Ranges++;
                return Task.FromResult(response);
            }
            Assert.AreEqual("graph.microsoft.com", uri.Host);
            var item = new Dictionary<string, object> { ["id"] = "item", ["name"] = "transfer.bin", ["size"] = _data.Length,
                ["eTag"] = "version-1", ["file"] = new { hashes = new { sha1Hash = Hash } }, ["lastModifiedDateTime"] = "2026-10-05T00:00:00Z",
                ["@microsoft.graph.downloadUrl"] = "https://payload.invalid/file" };
            object result = uri.AbsolutePath.EndsWith("/children") ? new { value = new[] { item } } : item;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TestProtector : ITransferCheckpointProtector
    { public byte[] Protect(byte[] bytes) => bytes; public byte[] Unprotect(byte[] bytes) => bytes; }
}

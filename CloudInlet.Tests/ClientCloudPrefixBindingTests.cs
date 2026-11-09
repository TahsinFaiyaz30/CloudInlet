using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Windows.CloudFiles;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Provider;

namespace CloudInlet.Tests;

[TestClass, DoNotParallelize]
public sealed class ClientCloudPrefixBindingTests
{
    private static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
    private static readonly string Hash = new('a', 40);
    private static readonly IProgress<MigrationProgress> Silent = new InlineProgress();
    [TestMethod]
    public async Task HostConnectionFailureCanRevertUnstartedItemWithoutNetwork()
    {
        using var f = new Fixture(); var connects = 0;
        using var operation = new ClientCloudPrefixMigration(f.Consent, "cloud-prefix", f.State, f.Receipts, f.Runtime,
            (_, _) => { connects++; return Task.FromException<ICloudPrefixMigrationStore>(new HttpRequestException("Offline fixture.")); });
        await Assert.ThrowsExceptionAsync<MigrationTransientException>(() => operation.CheckAsync(default));
        Assert.IsTrue(operation.CanRevert); await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        Assert.AreEqual(1, connects); Assert.AreEqual(0, f.Runtime.Calls.Count); Assert.AreEqual("CloudBay/", f.Storage.LoadSettings().Prefix);
    }
    [TestMethod]
    public async Task HostRejectsWrongAccountBeforeProviderAccessOrMutation()
    {
        using var f = new Fixture(); var store = new Store(f) { AccountId = "wrong" };
        using var operation = f.Host(store); await Assert.ThrowsExceptionAsync<IOException>(() => operation.CheckAsync(default));
        Assert.AreEqual(0, store.AccessCalls); Assert.AreEqual(0, store.Copies); Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public async Task HostCapabilityDenialLeavesOriginalAndAllowsLocalRevert()
    {
        using var f = new Fixture(); var store = new Store(f) { Denied = true };
        using var operation = f.Host(store); await Assert.ThrowsExceptionAsync<IOException>(() => operation.CheckAsync(default));
        await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        Assert.AreEqual(1, store.AccessCalls); Assert.AreEqual(0, store.Copies); Assert.AreEqual("CloudBay/", f.Storage.LoadSettings().Prefix);
    }
    [TestMethod]
    public async Task HostProtectedRecoveryReopensCompletedMappingWithoutCopyingAgain()
    {
        using var f = new Fixture(backups: true); f.AddHistory(); var store = new Store(f);
        using (var first = f.Host(store)) { await first.ApplyAsync(Silent, default); await first.VerifyAsync(default); Assert.IsFalse(first.CanRevert); }
        var copies = store.Copies; var nativeCalls = f.Runtime.Calls.Count;
        using var reopened = f.Host(store); await reopened.ApplyAsync(Silent, default); await reopened.VerifyAsync(default);
        Assert.AreEqual(copies, store.Copies); Assert.AreEqual(nativeCalls, f.Runtime.Calls.Count); Assert.IsFalse(reopened.CanRevert);
        Assert.AreEqual(f.Mappings.Count, store.Versions.Count(value => value.Key.StartsWith("CloudBay/", StringComparison.Ordinal)));
        Assert.AreEqual("CloudInlet/", f.Storage.LoadSettings().Prefix);
    }
    [TestMethod]
    public void HostRequiresExplicitProtectedCloudMaintenanceConsent()
    {
        using var f = new Fixture(); var store = new Store(f);
        Assert.ThrowsException<InvalidDataException>(() => new ClientCloudPrefixMigration(f.Consent with { CloudMaintenanceAcknowledged = false }, "cloud-prefix", f.State, f.Receipts, f.Runtime,
            (_, _) => Task.FromResult<ICloudPrefixMigrationStore>(store)));
        Assert.IsFalse(Directory.GetFiles(f.Receipts).Any());
    }
    [DataTestMethod, DataRow(false), DataRow(true)]
    public async Task HostMissingOuterJournalCannotBeAcknowledgedAsUnstarted(bool pendingHide)
    {
        using var f = new Fixture(); var store = new Store(f) { LoseHideResponse = pendingHide };
        if (pendingHide) store.Versions.Add(new("hidden", "CloudBay/deleted.txt", "hide", 0, null, 1800000000000, "application/x-bz-hide-marker", new Dictionary<string, string>()));
        using (var first = f.Host(store))
        {
            if (pendingHide) await Assert.ThrowsExceptionAsync<IOException>(() => first.ApplyAsync(Silent, default));
            else await first.CheckAsync(default);
        }
        var path = Directory.GetFiles(f.Receipts, "*.cloud-history.dpapi").Single(); var bytes = File.ReadAllBytes(path); File.Delete(path);
        Assert.ThrowsException<InvalidDataException>(() => f.Host(store));
        Assert.IsFalse(ClientCloudPrefixMigration.CanAcknowledgeUnstarted(f.Plan, "cloud-prefix", f.State, f.Receipts));
        Assert.AreEqual("CloudBay/", f.Storage.LoadSettings().Prefix);
        File.WriteAllBytes(path, bytes);
        // A failed constructor must release the history, transport and native binding leases.
        using var restored = f.Host(store);
        if (!pendingHide) { await restored.RevertAsync(Silent, default); await restored.VerifyRevertedAsync(default); }
        else Assert.AreEqual(1, store.HideCalls);
    }
    [TestMethod]
    public async Task CorruptCloudAnchorFailsClosedAndConstructorReleasesLeases()
    {
        using var f = new Fixture(); var store = new Store(f);
        using (var first = f.Host(store)) await first.CheckAsync(default);
        var path = Directory.GetFiles(f.Receipts, "*.cloud-history.dpapi.started").Single(); var bytes = File.ReadAllBytes(path);
        var corrupt = bytes.ToArray(); corrupt[^1] ^= 1; File.WriteAllBytes(path, corrupt);
        Assert.ThrowsException<CryptographicException>(() => f.Host(store)); Assert.AreEqual(0, store.Copies);
        File.WriteAllBytes(path, bytes); using var restored = f.Host(store); await restored.RevertAsync(Silent, default); await restored.VerifyRevertedAsync(default);
    }
    [TestMethod]
    public async Task CloudAnchorDirectoryCannotHideMissingOuterJournal()
    {
        using var f = new Fixture(); var store = new Store(f);
        using (var first = f.Host(store)) await first.CheckAsync(default);
        var marker = Directory.GetFiles(f.Receipts, "*.cloud-history.dpapi.started").Single(); var bytes = File.ReadAllBytes(marker);
        File.Delete(marker); Directory.CreateDirectory(marker); var journal = Directory.GetFiles(f.Receipts, "*.cloud-history.dpapi").Single(); File.Delete(journal);
        Assert.ThrowsException<InvalidDataException>(() => { using var unexpected = f.Host(store); }); Assert.AreEqual(0, store.Copies);
    }
    [TestMethod]
    public async Task SwitchPreservesLocalBytesDirtyBaselinesCredentialsAndCompletedHistory()
    {
        using var f = new Fixture(backups: true); var history = f.AddHistory();
        var mainBefore = f.Manifest.ReadAll()["file.txt"]; var backupBefore = f.BackupManifest.ReadAll()["file.txt"];
        using var binding = f.Open(); await binding.VerifyOriginalAsync(default);
        f.Storage.SaveSettings(f.Settings with { Theme = "Light" });
        await binding.SwitchAsync(f.Mappings, default); await binding.VerifySwitchedAsync(f.Mappings, default);
        var settings = f.Storage.LoadSettings(); Assert.AreEqual("Light", settings.Theme); Assert.AreEqual("CloudInlet/", settings.Prefix);
        Assert.AreEqual("CloudInlet/.cloudinlet-backups/Documents/", settings.CustomBackups.Single().Prefix);
        Assert.AreEqual(f.Folder, settings.RootPath); Assert.AreEqual(f.Backup, settings.CustomBackups.Single().SourcePath);
        Assert.AreEqual("fixture-secret", f.Storage.LoadCredentials()!.ApplicationKey);
        Assert.AreEqual("unuploaded personal changes", File.ReadAllText(Path.Combine(f.Folder, "file.txt")));
        var after = f.Manifest.ReadAll()["file.txt"];
        Assert.AreEqual(mainBefore with { Remote = mainBefore.Remote with { FileId = "new-main", Key = "CloudInlet/file.txt" }, NativeMarkPending = true }, after);
        Assert.AreEqual(backupBefore.LocalSize, f.BackupManifest.ReadAll()["file.txt"].LocalSize);
        Assert.AreEqual("new-backup", f.BackupManifest.ReadAll()["file.txt"].Remote.FileId);
        var cloud = new CloudImportJournal(f.State).Read(recordId: history.Cloud).Single();
        Assert.AreEqual("new-import", cloud.Completed["source-file"].FileId); Assert.AreEqual("archive/file.bin", cloud.Plan.Items.Single().Source.Key);
        StringAssert.Contains(cloud.DestinationIdentity, "|CloudInlet/|");
        StringAssert.Contains(new ImportJournal(f.State).Read(recordId: history.Folder).Single().DestinationIdentity, "|CloudInlet/|");
        var calls = f.Runtime.Calls.Count; await binding.SwitchAsync(f.Mappings, default); Assert.AreEqual(calls, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public async Task InterruptedRegistrationResumesWithoutRepeatingCompletedRoots()
    {
        using var f = new Fixture(backups: true); f.Runtime.FailRegistration = f.Folder;
        using (var first = f.Open()) await Assert.ThrowsExceptionAsync<IOException>(() => first.SwitchAsync(f.Mappings, default));
        Assert.AreEqual("CloudInlet/", f.Storage.LoadSettings().Prefix);
        using var resumed = f.Open(); await resumed.SwitchAsync(f.Mappings, default); await resumed.VerifySwitchedAsync(f.Mappings, default);
        Assert.AreEqual(2, f.Runtime.Calls.Count(value => value.StartsWith("unregister:")));
        Assert.AreEqual(1, f.Runtime.Calls.Count(value => value == "register:" + f.Backup));
    }
    [TestMethod]
    public async Task MissingMappingStopsBeforeUnregisteringAnyRoot()
    {
        using var f = new Fixture(); using var binding = f.Open();
        await Assert.ThrowsExceptionAsync<IOException>(() => binding.SwitchAsync([], default));
        Assert.AreEqual(0, f.Runtime.Calls.Count); await binding.VerifyOriginalAsync(default);
    }
    [TestMethod]
    public async Task DestinationOutsideReviewedPrefixIsRejectedBeforeLocalMutation()
    {
        using var f = new Fixture(); using var binding = f.Open(); var mapping = f.Mappings[0];
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => binding.SwitchAsync([mapping with { Migrated = mapping.Migrated with { Key = "other/file.txt" } }], default));
        Assert.AreEqual(0, f.Runtime.Calls.Count); Assert.AreEqual("CloudBay/", f.Storage.LoadSettings().Prefix);
    }
    [TestMethod]
    public async Task AddedOrRemovedImportHistoryBlocksMaintenance()
    {
        using var f = new Fixture(); using var binding = f.Open(); var history = f.AddHistory();
        await Assert.ThrowsExceptionAsync<IOException>(() => binding.VerifyMaintenanceAsync(default));
        Assert.IsTrue(File.Exists(Path.Combine(f.State, "Imports", history.Folder + ".json")));
        Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public async Task ChangedImportReceiptStopsBeforeNativeUnregister()
    {
        using var f = new Fixture(); f.AddHistory(); using var binding = f.Open();
        var path = Directory.GetFiles(Path.Combine(f.State, "CloudImports"), "*.json", SearchOption.AllDirectories).Single(value => Path.GetDirectoryName(value) != Path.Combine(f.State, "CloudImports"));
        File.WriteAllText(path, "{}"); await Assert.ThrowsExceptionAsync<IOException>(() => binding.SwitchAsync(f.Mappings, default));
        Assert.AreEqual("{}", File.ReadAllText(path)); Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public void OrphanImportReceiptAndWrongRecordFilenameAreRejected()
    {
        using var f = new Fixture(); var history = f.AddHistory();
        var path = Path.Combine(f.State, "Imports", history.Folder + ".json"); var wrong = Path.Combine(f.State, "Imports", Guid.NewGuid().ToString("N") + ".json");
        File.Copy(path, wrong); Assert.ThrowsException<IOException>(() => f.Open()); File.Delete(wrong);
        var orphan = Directory.CreateDirectory(Path.Combine(f.State, "CloudImports", Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(orphan, "orphan.json"), "{}"); Assert.ThrowsException<IOException>(() => f.Open());
    }
    [TestMethod]
    public void ReplacedManifestIsRejectedEvenWhenBytesAreIdentical()
    {
        using var f = new Fixture(); using (var binding = f.Open()) { }
        SqliteConnection.ClearAllPools(); File.Copy(f.ManifestPath, f.ManifestPath + ".copy"); File.Delete(f.ManifestPath); File.Move(f.ManifestPath + ".copy", f.ManifestPath);
        Assert.ThrowsException<IOException>(() => f.Open());
    }
    [TestMethod]
    public void RegisteredRootWithoutBaselineIsRejected()
    {
        using var f = new Fixture(); SqliteConnection.ClearAllPools(); File.Delete(f.ManifestPath);
        Assert.ThrowsException<IOException>(() => f.Open()); Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public async Task NewlyAppearingBaselineIsNotAdoptedForUnregisteredRoot()
    {
        using var f = new Fixture(); f.Runtime.Registered.Clear(); SqliteConnection.ClearAllPools(); File.Delete(f.ManifestPath);
        using var binding = f.Open(); _ = new SyncManifest(f.ManifestPath);
        await Assert.ThrowsExceptionAsync<IOException>(() => binding.VerifyMaintenanceAsync(default)); Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public async Task ForeignRegistrationAndChangedAccountRemainUntouched()
    {
        using var f = new Fixture(); using var binding = f.Open();
        f.Runtime.Registered[f.Folder] = ("foreign", false);
        await Assert.ThrowsExceptionAsync<IOException>(() => binding.SwitchAsync(f.Mappings, default)); Assert.AreEqual(0, f.Runtime.Calls.Count);
        f.Storage.SaveSettings(f.Settings with { AccountId = "other" });
        await Assert.ThrowsExceptionAsync<IOException>(() => binding.VerifyMaintenanceAsync(default)); Assert.AreEqual("other", f.Storage.LoadSettings().AccountId);
    }
    [TestMethod]
    public async Task UnregisteredRootCannotAcquireARegistrationDuringReview()
    {
        using var f = new Fixture(); f.Runtime.Registered.Clear(); using var binding = f.Open();
        f.Runtime.Registered[f.Folder] = (f.Account(f.Settings.Prefix), false);
        await Assert.ThrowsExceptionAsync<IOException>(() => binding.VerifyMaintenanceAsync(default)); Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public void NativeFolderAndManifestArePinnedAgainstReplacement()
    {
        using var f = new Fixture(); using var binding = f.Open();
        Assert.ThrowsException<IOException>(() => Directory.Move(f.Folder, f.Folder + ".replaced"));
        Assert.ThrowsException<IOException>(() => File.Move(f.ManifestPath, f.ManifestPath + ".replaced"));
        Assert.ThrowsException<IOException>(() => f.Open());
    }
    [TestMethod]
    public void ChangedRootAndTamperedReceiptAreRejected()
    {
        using var f = new Fixture(); using (var binding = f.Open()) { }
        var receipt = Directory.GetFiles(f.Receipts, "*.cloud-binding.dpapi").Single(); var original = File.ReadAllBytes(receipt);
        var tampered = original.ToArray(); tampered[^1] ^= 1; File.WriteAllBytes(receipt, tampered);
        Assert.ThrowsException<CryptographicException>(() => f.Open()); File.WriteAllBytes(receipt, original);
        Directory.Move(f.Folder, f.Folder + ".original"); Directory.CreateDirectory(f.Folder);
        Assert.ThrowsException<IOException>(() => f.Open());
    }
    [TestMethod, Timeout(120000)]
    public async Task NativeOnlineOnlyBytesSurvivePrefixAndRegistrationSwitch()
    {
        using var f = new Fixture(visible: true); var payload = System.Text.Encoding.UTF8.GetBytes(new string('z', 65536) + "preserved");
        var remote = new CloudObject("online", "CloudBay/online.bin", payload.Length, Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant(), Modified);
        f.Manifest.Put(new("online.bin", remote, remote.Size, remote.ModifiedUtc)); f.Mappings.Add(Map(remote));
        var cloud = new ClientFolderMigrationTests.FixtureCloud(f.Settings.AccountId, payload);
        var runtime = new ClientFolderMigration.WindowsRuntime(() => cloud);
        var oldId = WindowsPlaceholderService.GetRegistrationId(f.Account("CloudBay/")); var newId = WindowsPlaceholderService.GetRegistrationId(f.Account("CloudInlet/"), true);
        try
        {
            await using (var provider = new WindowsPlaceholderService())
            {
                await provider.ConnectAsync(f.Folder, f.Account("CloudBay/"), (_, _, _, _, _) => Task.FromException(new IOException("No external provider in fixture.")));
                await provider.CreateOrUpdateAsync(Path.Combine(f.Folder, "online.bin"), remote, true);
            }
            using var binding = f.Open(runtime); await binding.SwitchAsync(f.Mappings, default); await binding.VerifySwitchedAsync(f.Mappings, default);
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(Path.Combine(f.Folder, "online.bin")));
            Assert.IsTrue(cloud.ConnectCalls > 0); Assert.IsFalse(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == oldId));
            Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == newId && value.Path.Path == f.Folder));
            Assert.AreEqual("unuploaded personal changes", File.ReadAllText(Path.Combine(f.Folder, "file.txt")));
        }
        finally
        {
            foreach (var id in new[] { oldId, newId })
                if (StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == id && value.Path.Path == f.Folder)) StorageProviderSyncRootManager.Unregister(id);
            SqliteConnection.ClearAllPools();
        }
    }
    [TestMethod]
    public async Task HostKeepsOriginalAfterLostHideWithoutReconnectingAndAuthenticatesOnReopen()
    {
        using var f = new Fixture(); var store = new Store(f) { LoseHideResponse = true };
        store.Versions.Add(new("hidden", "CloudBay/hidden.txt", "hide", 0, null, 1800000000001,
            "application/x-bz-hide-marker", new Dictionary<string, string>()));
        using (var first = f.Host(store)) await Assert.ThrowsExceptionAsync<IOException>(() => first.ApplyAsync(Silent, default));
        var oldVersions = JsonSerializer.Serialize(store.Versions);
        var copyFiles = Directory.GetFiles(f.Receipts, "*.cloud-copies.dpapi");
        var copyBytes = copyFiles.ToDictionary(path => path, File.ReadAllBytes);
        var contacts = 0;
        ClientCloudPrefixMigration Offline() => new(f.Consent, "cloud-prefix", f.State, f.Receipts, f.Runtime,
            (_, _) => { contacts++; throw new IOException("Network must not be used"); });
        using (var recovery = Offline())
        {
            Assert.IsTrue(recovery.CanKeepOriginal); await recovery.KeepOriginalAsync(Silent, default);
            await recovery.VerifyOriginalKeptAsync(default); Assert.IsFalse(recovery.CanRevert);
        }
        using (var reopened = Offline()) await reopened.VerifyOriginalKeptAsync(default);
        Assert.AreEqual(0, contacts); Assert.AreEqual(oldVersions, JsonSerializer.Serialize(store.Versions));
        CollectionAssert.AreEquivalent(copyFiles, Directory.GetFiles(f.Receipts, "*.cloud-copies.dpapi"));
        foreach (var pair in copyBytes) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.AreEqual("CloudBay/", f.Storage.LoadSettings().Prefix); Assert.AreEqual(0, f.Runtime.Calls.Count);
        var historyPath = Directory.GetFiles(f.Receipts, "*.cloud-history.dpapi").Single();
        var protectedBytes = File.ReadAllBytes(historyPath); protectedBytes[^1] ^= 1; File.WriteAllBytes(historyPath, protectedBytes);
        Assert.ThrowsException<CryptographicException>(() => { using var changed = Offline(); });
        Assert.AreEqual(0, contacts);
    }

    [TestMethod]
    public async Task HostCannotKeepOriginalAfterLocalIdentityChanges()
    {
        using var f = new Fixture(); var store = new Store(f);
        using (var first = f.Host(store)) await first.CheckAsync(default);
        f.Storage.SaveSettings(f.Settings with { AccountId = "foreign-account" });
        var contacts = 0;
        using var recovery = new ClientCloudPrefixMigration(f.Consent, "cloud-prefix", f.State, f.Receipts, f.Runtime,
            (_, _) => { contacts++; throw new AssertFailedException("No connection is allowed"); });
        await Assert.ThrowsExceptionAsync<IOException>(() => recovery.KeepOriginalAsync(Silent, default));
        Assert.AreEqual(0, contacts); Assert.AreEqual("foreign-account", f.Storage.LoadSettings().AccountId);
    }

    private static CloudPrefixVersionMapping Map(CloudObject remote, string? destination = null) => new(
        new(remote.FileId, remote.Key, remote.Action, remote.Size, remote.Sha1, 1800000000000, "application/octet-stream", new Dictionary<string, string>()),
        new("new-" + remote.FileId, destination ?? "CloudInlet/" + remote.Key["CloudBay/".Length..], remote.Action, remote.Size, remote.Sha1, 1900000000000, "application/octet-stream", new Dictionary<string, string>()));
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Folder => Path.Combine(Root, "CloudBay");
        public string Backup => Path.Combine(Root, "Documents");
        public string State => Path.Combine(Root, "state");
        public string Receipts => Path.Combine(Root, "receipts");
        public ClientStorage Storage { get; }
        public AppSettings Settings { get; }
        public Runtime Runtime { get; } = new();
        public List<CloudPrefixVersionMapping> Mappings { get; } = [];
        public string ManifestPath => Path.Combine(State, "State", SyncRootMigrationIdentity.StateName(Settings, Folder, Settings.Prefix) + ".sqlite");
        public SyncManifest Manifest => new(ManifestPath);
        public SyncManifest BackupManifest => new(Path.Combine(State, "State", SyncRootMigrationIdentity.StateName(Settings, Backup, Settings.CustomBackups.Single().Prefix) + ".sqlite"));
        public MigrationPlan Plan => MigrationPlan.Create([new("cloud-prefix", MigrationArea.CloudPrefix, "Cloud prefix", "CloudBay/", "CloudInlet/", "Preserve originals", CloudMappings:
            Settings.CustomBackups.Count == 0 ? null : [new("CloudBay/.cloudbay-backups/Documents/", "CloudInlet/.cloudinlet-backups/Documents/")])]);
        private readonly string _consentId = Guid.NewGuid().ToString("N");
        public ClientMigrationReview.Consent Consent => new(1, _consentId, Plan, ["cloud-prefix"], true);
        public Fixture(bool backups = false, bool visible = false)
        {
            Root = Path.Combine(visible ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetTempPath(), "CloudInlet-CloudBindingAcceptance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder); Directory.CreateDirectory(Receipts); File.WriteAllText(Path.Combine(Folder, "file.txt"), "unuploaded personal changes");
            Settings = new() { RootPath = Folder, Prefix = "CloudBay/", AccountId = "fixture-" + Guid.NewGuid().ToString("N"), BucketId = "fixture", KeyId = "fixture",
                CustomBackups = backups ? [new("Documents", Backup, "CloudBay/.cloudbay-backups/Documents/")] : [] };
            Storage = new(State); Storage.SaveSettings(Settings); Storage.SaveCredentials(new("fixture", "fixture-secret"));
            var remote = new CloudObject("main", "CloudBay/file.txt", 3, Hash, Modified); Manifest.Put(new("file.txt", remote, 27, Modified.AddDays(1))); Mappings.Add(Map(remote));
            Runtime.Registered[Folder] = (Account(Settings.Prefix), false);
            if (backups)
            {
                Directory.CreateDirectory(Backup); File.WriteAllText(Path.Combine(Backup, "file.txt"), "different local bytes");
                var backupRemote = remote with { FileId = "backup", Key = Settings.CustomBackups.Single().Prefix + "file.txt" };
                BackupManifest.Put(new("file.txt", backupRemote, 21, Modified.AddDays(2))); Mappings.Add(Map(backupRemote, "CloudInlet/.cloudinlet-backups/Documents/file.txt"));
                Runtime.Registered[Backup] = (Account(Settings.CustomBackups.Single().Prefix), false);
            }
        }
        public string Account(string prefix) => Settings.AccountId + ":" + Settings.BucketId + ":" + prefix;
        public ClientCloudPrefixBinding Open(IClientFolderMigrationRuntime? runtime = null) => ClientCloudPrefixBinding.Open(Plan, "cloud-prefix", State, Receipts, new string('a', 64), runtime ?? Runtime);
        public ClientCloudPrefixMigration Host(Store store) => new(Consent, "cloud-prefix", State, Receipts, Runtime, (_, _) => Task.FromResult<ICloudPrefixMigrationStore>(store));
        public (string Folder, string Cloud) AddHistory()
        {
            var folderId = Guid.NewGuid().ToString("N"); var cloudId = Guid.NewGuid().ToString("N"); var destination = Path.Combine(Folder, "imported");
            var identity = Settings.AccountId + "|" + Settings.BucketId + "|CloudBay/|" + Folder;
            new ImportJournal(State).Save(new(folderId, Modified, new(Path.Combine(Root, "import-source"), destination, new string('a', 64), 1, 3, 100, false), identity, "Completed"));
            var source = new CloudObject("source-file", "archive/file.bin", 3, Hash, Modified); var copied = source with { FileId = "import", Key = "CloudBay/imported/file.bin" };
            var plan = new CloudImportPlan(cloudId, "source-bucket", "archive/", destination, [new("file.bin", source)], 1, 3);
            var journal = new CloudImportJournal(State); journal.Save(new(cloudId, Modified, plan, identity, "Completed", new Dictionary<string, CloudObject>())); journal.SaveProgress(cloudId, source.FileId, copied);
            Mappings.Add(Map(copied)); return (folderId, cloudId);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools(); var path = Path.GetFullPath(Root);
            if (!Path.GetFileName(path).StartsWith("CloudInlet-CloudBindingAcceptance-", StringComparison.Ordinal) ||
                !(Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || Path.GetDirectoryName(path) == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                throw new InvalidOperationException("Fixture cleanup escaped its isolated root.");
            Directory.Delete(path, true);
        }
    }
    private sealed class Runtime : IClientFolderMigrationRuntime
    {
        public Dictionary<string, (string Account, bool Canonical)> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public string? FailRegistration;
        public bool IsRegistered(string root, string account, bool canonical)
        { if (!Registered.TryGetValue(root, out var value)) return false; if (value != (account, canonical)) throw new IOException("A different native identity owns this root."); return true; }
        public Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials, IProgress<string> progress, CancellationToken token)
        { Assert.IsTrue(IsRegistered(root, account, canonical)); Calls.Add("unregister:" + root); Registered.Remove(root); return Task.CompletedTask; }
        public Task RegisterAsync(string root, string account, bool canonical, string manifestPath, CancellationToken token)
        { if (FailRegistration == root) { FailRegistration = null; throw new IOException("Native registration temporarily unavailable."); } Calls.Add("register:" + root); Registered.Add(root, (account, canonical)); return Task.CompletedTask; }
        public string GetKnownFolder(string name) => throw new AssertFailedException("Windows folder mappings must not change.");
        public void ExchangeKnownFolder(string name, string expected, string replacement) => throw new AssertFailedException("Windows folder mappings must not change.");
    }
    private sealed class Store : ICloudPrefixMigrationStore
    {
        public string AccountId { get; set; }
        public string BucketId => "fixture";
        public List<CloudPrefixVersion> Versions { get; }
        public int AccessCalls, Copies, HideCalls;
        public bool Denied, LoseHideResponse;
        public Store(Fixture fixture) { AccountId = fixture.Settings.AccountId; Versions = fixture.Mappings.Select(value => value.Original).ToList(); }
        public Task ValidateAccessAsync(string source, string destination, CancellationToken token)
        { AccessCalls++; if (Denied) throw new IOException("Missing isolated fixture protection-read capability."); return Task.CompletedTask; }
        public Task<IReadOnlyList<CloudPrefixVersion>> ListAsync(string prefix, CancellationToken token) => Task.FromResult<IReadOnlyList<CloudPrefixVersion>>(Versions.Where(value => value.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
        public Task<CloudPrefixVersion> CopyAsync(CloudPrefixVersion source, string destination, string operationId, CancellationToken token)
        {
            Copies++; var copy = source with { FileId = "new-" + source.FileId, Key = destination, UploadedAtMillis = 1900000000000,
                Metadata = new Dictionary<string, string>(source.Metadata) { [CloudPrefixMigration.ReceiptMetadataKey] = operationId, [CloudPrefixMigration.SourceMetadataKey] = source.FileId } };
            Versions.Insert(0, copy); return Task.FromResult(copy);
        }
        public Task<CloudPrefixVersion> HideOnceAsync(string destination, CancellationToken token)
        {
            HideCalls++; var original = Versions.Single(value => value.Action == "hide" && value.Key == "CloudBay/" + destination["CloudInlet/".Length..]);
            var hidden = original with { FileId = "new-hidden", Key = destination, UploadedAtMillis = 1900000000000 }; Versions.Insert(0, hidden);
            if (LoseHideResponse) throw new IOException("Isolated hide response was lost after commit."); return Task.FromResult(hidden);
        }
        public Task DeleteExactAsync(CloudPrefixVersion version, CancellationToken token) => throw new AssertFailedException("No deletion expected in host fixture.");
    }
    private sealed class InlineProgress : IProgress<MigrationProgress> { public void Report(MigrationProgress value) { } }
}

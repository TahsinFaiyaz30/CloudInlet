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
public sealed class ClientRegistrationMigrationTests
{
    private static readonly IProgress<MigrationProgress> Silent = new Progress();
    [TestMethod]
    public async Task RegistrationChangesWithoutMovingFilesOrManifest()
    {
        using var f = new Fixture(); var baseline = SyncRootMigrationIdentity.StateName(f.Settings, f.Folder, f.Settings.Prefix);
        using var operation = f.Open(); await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default);
        Assert.AreEqual(f.Folder, f.Storage.LoadSettings().RootPath);
        Assert.AreEqual(baseline, SyncRootMigrationIdentity.StateName(f.Storage.LoadSettings(), f.Folder, f.Settings.Prefix));
        Assert.IsTrue(SyncRootMigrationIdentity.IsCanonical(f.Storage.LoadSettings(), f.Folder));
        Assert.AreEqual("personal bytes", File.ReadAllText(Path.Combine(f.Folder, "local.txt")));
        CollectionAssert.AreEqual(new[] { "unregister:False", "register:True" }, f.Runtime.Calls);
        await operation.ApplyAsync(Silent, default);
        Assert.AreEqual(2, f.Runtime.Calls.Count);
    }
    [DataTestMethod, DataRow(false), DataRow(true)]
    public async Task InterruptedRegistrationCanResumeOrRevertOnlyItsBinding(bool revert)
    {
        using var f = new Fixture(); f.Runtime.FailRegister = true;
        using (var first = f.Open()) await Assert.ThrowsExceptionAsync<IOException>(() => first.ApplyAsync(Silent, default));
        Assert.IsNull(f.Runtime.Canonical);
        f.Storage.SaveSettings(f.Storage.LoadSettings() with { Theme = "Light" });
        using var resumed = f.Open();
        if (revert) { await resumed.RevertAsync(Silent, default); await resumed.VerifyRevertedAsync(default); }
        else { await resumed.ApplyAsync(Silent, default); await resumed.VerifyAsync(default); }
        Assert.AreEqual(!revert, f.Runtime.Canonical);
        Assert.AreEqual("Light", f.Storage.LoadSettings().Theme);
        Assert.AreEqual(!revert, SyncRootMigrationIdentity.IsCanonical(f.Storage.LoadSettings(), f.Folder));
        Assert.AreEqual(1, f.Runtime.Calls.Count(value => value == "unregister:False"));
    }
    [TestMethod]
    public async Task HydrationFailurePreservesOriginalRegistrationAndCanRevert()
    {
        using var f = new Fixture(); f.Runtime.FailHydrate = true;
        using var operation = f.Open(); await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        Assert.AreEqual(false, f.Runtime.Canonical);
        await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        Assert.AreEqual(0, f.Storage.LoadSettings().RootBindings.Count);
    }
    [TestMethod]
    public async Task PreflightRevertDoesNotTouchRegistration()
    {
        using var f = new Fixture(); using var operation = f.Open();
        await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public async Task ChangedAccountOrUnrelatedRegistrationIsNeverRewritten()
    {
        using var f = new Fixture(); using var operation = f.Open();
        f.Storage.SaveSettings(f.Settings with { AccountId = "changed-account" });
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        Assert.AreEqual(0, f.Runtime.Calls.Count);
        f.Storage.SaveSettings(f.Settings); f.Runtime.Foreign = true;
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public void ExactRootIsPinnedAgainstReplacementWhileOperationIsOpen()
    {
        using var f = new Fixture(); using var operation = f.Open();
        Assert.ThrowsException<IOException>(() => Directory.Move(f.Folder, f.Folder + "-replaced"));
    }
    [TestMethod]
    public void ChangedRootAfterInterruptionIsRejected()
    {
        using var f = new Fixture(); using (var operation = f.Open()) { }
        Directory.Move(f.Folder, f.Folder + "-original"); Directory.CreateDirectory(f.Folder);
        Assert.ThrowsException<IOException>(() => f.Open());
    }
    [TestMethod]
    public void TamperedProtectedReceiptDoesNotAuthorizeRegistration()
    {
        using var f = new Fixture(); using (var operation = f.Open()) { }
        var path = Directory.GetFiles(f.Receipts, "*.registration.dpapi").Single();
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => f.Open());
        Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [TestMethod]
    public void ActiveUploadCheckpointPreventsMigration()
    {
        using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.State, "Uploads"));
        File.WriteAllText(Path.Combine(f.State, "Uploads", "pending.json"), "{}");
        Assert.ThrowsException<IOException>(() => f.Open()); Assert.AreEqual(0, f.Runtime.Calls.Count);
    }
    [DataTestMethod, DataRow(false), DataRow(true), Timeout(120000)]
    public async Task NativeStandaloneRegistrationRetainsOriginalFolderAndLocalBytes(bool online)
    {
        // Native acceptance uses an isolated visible fixture, never a personal backup root.
        using var f = new Fixture(visible: true);
        var payload = System.Text.Encoding.UTF8.GetBytes(new string('p', 65536) + "online-only content");
        var cloud = new ClientFolderMigrationTests.FixtureCloud(f.Settings.AccountId, payload);
        var runtime = new ClientFolderMigration.WindowsRuntime(() => cloud);
        var account = f.Settings.AccountId + ":" + f.Settings.BucketId + ":" + f.Settings.Prefix;
        var oldId = WindowsPlaceholderService.GetRegistrationId(account); var newId = WindowsPlaceholderService.GetRegistrationId(account, true);
        try
        {
            await using (var provider = new WindowsPlaceholderService())
            {
                await provider.ConnectAsync(f.Folder, account, (_, _, _, _, _) => Task.FromException(new IOException("No cloud access in local fixture.")));
                if (online)
                {
                    f.Storage.SaveCredentials(new("fixture", "not-a-real-secret"));
                    var remote = new CloudObject("fixture-online", "CloudBay/online.bin", payload.Length,
                        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload)).ToLowerInvariant(), DateTimeOffset.UtcNow);
                    var manifest = new SyncManifest(Path.Combine(f.State, "State", SyncRootMigrationIdentity.StateName(f.Settings, f.Folder, f.Settings.Prefix) + ".sqlite"));
                    manifest.Put(new("online.bin", remote, remote.Size, remote.ModifiedUtc));
                    await provider.CreateOrUpdateAsync(Path.Combine(f.Folder, "online.bin"), remote, true);
                }
            }
            using var operation = ClientRegistrationMigration.Open(f.Plan, "sync-registration-test", f.State, f.Receipts, runtime);
            await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default);
            Assert.AreEqual("personal bytes", File.ReadAllText(Path.Combine(f.Folder, "local.txt")));
            if (online) CollectionAssert.AreEqual(payload, File.ReadAllBytes(Path.Combine(f.Folder, "online.bin")));
            Assert.AreEqual(online, cloud.ConnectCalls > 0);
            Assert.IsFalse(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == oldId));
            Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == newId && value.Path.Path == f.Folder));
            // Simulate a failed outer verification; restoring only this registration must retain the same folder.
            await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
            Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == oldId && value.Path.Path == f.Folder));
            Assert.AreEqual("personal bytes", File.ReadAllText(Path.Combine(f.Folder, "local.txt")));
        }
        finally
        {
            foreach (var id in new[] { oldId, newId })
                if (StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(value => value.Id == id && value.Path.Path == f.Folder)) StorageProviderSyncRootManager.Unregister(id);
            SqliteConnection.ClearAllPools();
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Folder => Path.Combine(Root, "SelectedFolder");
        public string State => Path.Combine(Root, "state");
        public string Receipts => Path.Combine(Root, "receipts");
        public ClientStorage Storage { get; }
        public AppSettings Settings { get; }
        public Runtime Runtime { get; } = new();
        public MigrationPlan Plan { get; }
        public Fixture(bool visible = false)
        {
            var parent = visible ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetTempPath();
            Root = Path.Combine(parent, "CloudInlet-RegistrationAcceptance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder); Directory.CreateDirectory(Receipts); File.WriteAllText(Path.Combine(Folder, "local.txt"), "personal bytes");
            Storage = new(State); Settings = new() { RootPath = Folder, AccountId = "registration-fixture-" + Guid.NewGuid().ToString("N"), BucketId = "fixture", Prefix = "CloudBay/", KeyId = "fixture" };
            Storage.SaveSettings(Settings);
            var account = Settings.AccountId + ":" + Settings.BucketId + ":" + Settings.Prefix;
            Plan = MigrationPlan.Create([new("sync-registration-test", MigrationArea.WindowsIntegration, "Files On-Demand registration",
                WindowsPlaceholderService.GetRegistrationId(account), WindowsPlaceholderService.GetRegistrationId(account, true), "Keep the same folder")]);
        }
        public ClientRegistrationMigration Open() => ClientRegistrationMigration.Open(Plan, "sync-registration-test", State, Receipts, Runtime);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!Path.GetFileName(resolved).StartsWith("CloudInlet-RegistrationAcceptance-", StringComparison.Ordinal) ||
                !(Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || Path.GetDirectoryName(resolved) == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
            Directory.Delete(resolved, true);
        }
    }
    private sealed class Runtime : IClientFolderMigrationRuntime
    {
        public bool? Canonical = false;
        public bool FailRegister, FailHydrate, Foreign;
        public List<string> Calls { get; } = [];
        public bool IsRegistered(string root, string account, bool canonical)
        { if (Foreign || Canonical is not null && Canonical != canonical) throw new IOException("Different registration owns this folder."); return Canonical == canonical; }
        public Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials, IProgress<string> progress, CancellationToken token)
        { if (FailHydrate) { FailHydrate = false; throw new IOException("Network outage."); } Calls.Add("unregister:" + canonical); Canonical = null; return Task.CompletedTask; }
        public Task RegisterAsync(string root, string account, bool canonical, string manifestPath, CancellationToken token)
        { if (FailRegister) { FailRegister = false; throw new IOException("Windows temporarily refused registration."); } if (Canonical == canonical) return Task.CompletedTask; Calls.Add("register:" + canonical); Canonical = canonical; return Task.CompletedTask; }
        public string GetKnownFolder(string name) => throw new AssertFailedException("No Windows folder mapping may change.");
        public void ExchangeKnownFolder(string name, string expected, string replacement) => throw new AssertFailedException("No Windows folder mapping may change.");
    }
    private sealed class Progress : IProgress<MigrationProgress> { public void Report(MigrationProgress value) { } }
}

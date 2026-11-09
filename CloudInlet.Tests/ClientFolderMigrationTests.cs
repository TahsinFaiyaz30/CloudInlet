using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Windows.CloudFiles;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Windows.Storage.Provider;

namespace CloudInlet.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ClientFolderMigrationTests
{
    private static readonly IProgress<MigrationProgress> Silent = new InlineProgress(_ => { });

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000)]
    public async Task NativeRegisteredFolderMigrationPreservesLocalAndOnlineFiles(bool online)
    {
        // A fresh visible, local fixture is required by the native Shell registration API.
        // No existing registration, personal known folder, or production cloud is touched.
        var fixtureRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudInlet-FolderAcceptance-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(fixtureRoot, BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay");
        var target = Path.Combine(fixtureRoot, BuildInfo.Flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet");
        var state = Path.Combine(fixtureRoot, "state"); var receipts = Path.Combine(fixtureRoot, "receipts");
        var accountId = "folder-acceptance-" + Guid.NewGuid().ToString("N");
        var account = accountId + ":fixture:CloudBay/";
        var legacyId = WindowsPlaceholderService.GetRegistrationId(account);
        var canonicalId = WindowsPlaceholderService.GetRegistrationId(account, true);
        var payload = System.Text.Encoding.UTF8.GetBytes(new string('x', 65536) + "verified fixture content");
        var remote = new CloudObject("fixture-immutable", "CloudBay/preserved.bin", payload.Length,
            Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant(), DateTimeOffset.UtcNow);
        var cloud = new FixtureCloud(accountId, payload) { FailDownloadOnce = online };
        var runtime = new ClientFolderMigration.WindowsRuntime(() => cloud);
        try
        {
            Directory.CreateDirectory(receipts);
            var storage = new ClientStorage(state);
            var settings = new AppSettings { RootPath = source, AccountId = accountId, BucketId = "fixture", KeyId = "fixture", Prefix = "CloudBay/" };
            storage.SaveSettings(settings);
            if (online) storage.SaveCredentials(new("fixture", "not-a-real-credential"));
            var stateName = SyncRootMigrationIdentity.StateName(settings, source, settings.Prefix);
            var manifest = new SyncManifest(Path.Combine(state, "State", stateName + ".sqlite"));
            manifest.Put(new("preserved.bin", remote, remote.Size, remote.ModifiedUtc));
            await using (var service = new WindowsPlaceholderService())
            {
                await service.ConnectAsync(source, account, (_, _, _, _, _) => Task.FromException(new IOException("Fixture is offline until migration starts.")));
                if (online) await service.CreateOrUpdateAsync(Path.Combine(source, "preserved.bin"), remote, true);
                else await File.WriteAllBytesAsync(Path.Combine(source, "preserved.bin"), payload);
                await File.WriteAllTextAsync(Path.Combine(source, "unuploaded.txt"), "original local content");
            }
            var registeredFixture = StorageProviderSyncRootManager.GetCurrentSyncRoots().Single(entry => entry.Id == legacyId);
            Assert.IsTrue(WindowsPlaceholderService.HasRegistrationIdentity(registeredFixture, account, false),
                "The current user's registration ID and persisted account context must match.");
            Assert.IsFalse(WindowsPlaceholderService.HasRegistrationIdentity(registeredFixture, account + "other", false));
            Assert.IsFalse(WindowsPlaceholderService.HasRegistrationIdentity(registeredFixture, account, true));
            var discovered = CloudInlet.Windows.LegacyMigrationDiscovery.Discover(settings,
                new(new("1.2.0", BuildInfo.Flavor == "Debug" ? CloudInlet.Core.Updates.UpdateBuildFlavor.Debug : CloudInlet.Core.Updates.UpdateBuildFlavor.Release,
                    CloudInlet.Core.Updates.UpdateInstallerKind.Portable), fixtureRoot), state);
            Assert.AreEqual(CloudInlet.Windows.LegacyMigrationDiscovery.ReviewOnlyReason,
                discovered.Items.Single(item => item.Id == "sync-folder").BlockedReason);
            StringAssert.Contains(discovered.Items.Single(item => item.Id == "sync-folder").Description, "Files On-Demand registration");
            var plan = MigrationPlan.Create([new("sync-folder", MigrationArea.SyncFolder, "Sync folder", source, target, "Migrate the isolated fixture")]);
            if (online)
            {
                using var first = ClientFolderMigration.Create(plan, "sync-folder", state, receipts, runtime);
                Exception? failure = null;
                try { await first.ApplyAsync(Silent, default); } catch (Exception error) { failure = error; }
                Assert.IsNotNull(failure, "The injected first network interruption must be visible.");
                Assert.IsTrue(Directory.Exists(source)); Assert.IsFalse(Directory.Exists(target));
                Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(entry => entry.Id == legacyId));
            }
            using (var operation = online ? ClientFolderMigration.Resume(plan, "sync-folder", state, receipts, runtime)
                : ClientFolderMigration.Create(plan, "sync-folder", state, receipts, runtime))
            {
                await operation.ApplyAsync(Silent, default);
                await operation.VerifyAsync(default);
            }
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(target, "preserved.bin")));
            Assert.AreEqual("original local content", await File.ReadAllTextAsync(Path.Combine(target, "unuploaded.txt")));
            Assert.IsFalse(Directory.Exists(source));
            Assert.IsFalse(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(entry => entry.Id == legacyId));
            Assert.IsTrue(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(entry => entry.Id == canonicalId && entry.Path.Path == target));
            Assert.AreEqual(stateName, SyncRootMigrationIdentity.StateName(storage.LoadSettings(), target, settings.Prefix));
            Assert.AreEqual(remote, manifest.ReadAll()["preserved.bin"].Remote);
            Assert.AreEqual(online, cloud.ConnectCalls > 0, "Already-local files must not require credentials or network access.");
        }
        finally
        {
            foreach (var id in new[] { legacyId, canonicalId })
                if (StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(entry => entry.Id == id &&
                    Path.GetFullPath(entry.Path.Path).StartsWith(Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    StorageProviderSyncRootManager.Unregister(id);
            SqliteConnection.ClearAllPools();
            var prefix = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) + Path.DirectorySeparatorChar + "CloudInlet-FolderAcceptance-";
            if (!Path.GetFullPath(fixtureRoot).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
        }
    }

    [TestMethod]
    public async Task NativeFolderMigrationPreservesContentsManifestIdentityAndMappings()
    {
        using var fixture = new Fixture();
        var previousState = SyncRootMigrationIdentity.StateName(fixture.Settings, fixture.Source, fixture.Settings.Prefix);
        using var operation = fixture.Create();
        await operation.ApplyAsync(Silent, default);
        await operation.VerifyAsync(default);
        var settings = fixture.Storage.LoadSettings();
        Assert.AreEqual(fixture.Target, settings.RootPath);
        Assert.AreEqual(previousState, SyncRootMigrationIdentity.StateName(settings, fixture.Target, settings.Prefix));
        Assert.IsTrue(SyncRootMigrationIdentity.IsCanonical(settings, fixture.Target));
        Assert.AreEqual(Path.Combine(fixture.Target, "Documents"), fixture.Runtime.Mappings["Documents"]);
        Assert.AreEqual(Path.Combine(fixture.Target, "Documents"), settings.Backups.Single().DestinationPath);
        Assert.AreEqual(fixture.Target, settings.SelectedExclusions.Single().RootPath);
        Assert.AreEqual("preserve the original bytes", File.ReadAllText(Path.Combine(fixture.Target, "Documents", "personal.txt")));
        CollectionAssert.AreEqual(new[] { "unregister:False", "register:True" }, fixture.Runtime.Calls);
        Assert.IsFalse(Directory.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task NativeFolderFailureCanResumeAfterDirectoryRenameWithoutRepeatingHydration()
    {
        using var fixture = new Fixture(); fixture.Runtime.FailMappingOnce = true;
        using (var operation = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        Assert.IsTrue(Directory.Exists(fixture.Target));
        Assert.AreEqual(fixture.Source, fixture.Storage.LoadSettings().RootPath);
        using var resumed = fixture.Resume();
        await resumed.ApplyAsync(Silent, default);
        await resumed.VerifyAsync(default);
        Assert.AreEqual(1, fixture.Runtime.Calls.Count(value => value == "unregister:False"));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task NativeFolderRecoveryPreservesRecreatedWindowsMetadata(bool revert, bool interruptedPreservation)
    {
        using var fixture = new Fixture();
        fixture.AddWindowsBackups(); fixture.Runtime.FailMappingAt = 0; fixture.Runtime.FailMappingAfterWrite = true;
        var originalIdentity = ClientFolderMigration.ReadIdentity(fixture.Source);
        using (var initial = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => initial.ApplyAsync(Silent, default));
        var ini = CreateRecreatedPictures(fixture);
        var recreatedIdentity = ClientFolderMigration.ReadIdentity(fixture.Source);
        var iniBytes = File.ReadAllBytes(ini);
        var messages = new List<MigrationProgress>();
        var reporter = new InlineProgress(value =>
        {
            messages.Add(value);
            if (interruptedPreservation && value.Operation == "Folder moved")
                throw new IOException("fixture interruption after metadata preservation rename");
        });
        if (interruptedPreservation)
        {
            using var interrupted = fixture.Resume();
            await Assert.ThrowsExceptionAsync<IOException>(() => revert
                ? interrupted.RevertAsync(reporter, default) : interrupted.ApplyAsync(reporter, default));
            Assert.IsFalse(Directory.Exists(fixture.Source));
            Assert.AreEqual(originalIdentity, ClientFolderMigration.ReadIdentity(fixture.Target));
        }
        using (var resumed = fixture.Resume())
        {
            if (revert) { await resumed.RevertAsync(Silent, default); await resumed.VerifyRevertedAsync(default); }
            else { await resumed.ApplyAsync(Silent, default); await resumed.VerifyAsync(default); }
        }
        var preserved = Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Single();
        Assert.AreEqual(recreatedIdentity, ClientFolderMigration.ReadIdentity(preserved));
        CollectionAssert.AreEqual(iniBytes, File.ReadAllBytes(Path.Combine(preserved, "Pictures", "desktop.ini")));
        var expected = revert ? fixture.Source : fixture.Target;
        Assert.AreEqual(originalIdentity, ClientFolderMigration.ReadIdentity(expected));
        Assert.AreEqual(expected, fixture.Storage.LoadSettings().RootPath);
        foreach (var name in new[] { "Documents", "Pictures", "Music" })
        {
            Assert.AreEqual(Path.Combine(expected, name), fixture.Runtime.Mappings[name]);
            Assert.AreEqual("original " + name + " bytes", File.ReadAllText(Path.Combine(expected, name, "owned.txt")));
        }
        using var reopened = fixture.Resume();
        if (revert) await reopened.VerifyRevertedAsync(default); else await reopened.VerifyAsync(default);
        if (interruptedPreservation) Assert.IsTrue(messages.Any(value => value.Operation == "Preserving recreated Windows folder"));
    }

    [DataTestMethod]
    [DataRow(false, "file")]
    [DataRow(true, "file")]
    [DataRow(false, "directory")]
    [DataRow(true, "directory")]
    [DataRow(false, "stream")]
    [DataRow(true, "stream")]
    public async Task NativeFolderRecoveryRetainsUnexpectedRecreatedContent(bool revert, string kind)
    {
        using var fixture = new Fixture();
        fixture.AddWindowsBackups(); fixture.Runtime.FailMappingOnce = true;
        using (var initial = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => initial.ApplyAsync(Silent, default));
        var ini = CreateRecreatedPictures(fixture);
        if (kind == "file") File.WriteAllText(Path.Combine(fixture.Source, "Pictures", "new-photo.txt"), "user created this after reboot");
        else if (kind == "directory") Directory.CreateDirectory(Path.Combine(fixture.Source, "foreign"));
        else File.WriteAllText(ini + ":personal", "user stream payload");
        var identity = ClientFolderMigration.ReadIdentity(fixture.Source);
        var settings = File.ReadAllBytes(Path.Combine(fixture.State, "settings.json"));
        var mappings = fixture.Runtime.Mappings.ToArray();
        using var resumed = fixture.Resume();
        await Assert.ThrowsExceptionAsync<IOException>(() => revert
            ? resumed.RevertAsync(Silent, default) : resumed.ApplyAsync(Silent, default));
        Assert.AreEqual(identity, ClientFolderMigration.ReadIdentity(fixture.Source));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Length);
        CollectionAssert.AreEqual(settings, File.ReadAllBytes(Path.Combine(fixture.State, "settings.json")));
        CollectionAssert.AreEqual(mappings, fixture.Runtime.Mappings.ToArray());
        Assert.AreEqual("original Pictures bytes", File.ReadAllText(Path.Combine(fixture.Target, "Pictures", "owned.txt")));
        if (kind == "file") Assert.AreEqual("user created this after reboot", File.ReadAllText(Path.Combine(fixture.Source, "Pictures", "new-photo.txt")));
        if (kind == "stream") Assert.AreEqual("user stream payload", File.ReadAllText(ini + ":personal"));
    }

    [TestMethod]
    public async Task NativeFolderRecoveryRetainsContentAddedDuringPreservation()
    {
        using var fixture = new Fixture();
        fixture.AddWindowsBackups(); fixture.Runtime.FailMappingOnce = true;
        using (var initial = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => initial.ApplyAsync(Silent, default));
        CreateRecreatedPictures(fixture);
        var reporter = new InlineProgress(value =>
        {
            if (value.Operation == "Folder moved")
                File.WriteAllText(Path.Combine(Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Single(), "Pictures", "late.txt"), "concurrent user content");
        });
        using (var resumed = fixture.Resume())
            await Assert.ThrowsExceptionAsync<IOException>(() => resumed.RevertAsync(reporter, default));
        var preserved = Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Single();
        Assert.AreEqual("concurrent user content", File.ReadAllText(Path.Combine(preserved, "Pictures", "late.txt")));
        Assert.AreEqual("original Pictures bytes", File.ReadAllText(Path.Combine(fixture.Target, "Pictures", "owned.txt")));
        using var reopened = fixture.Resume();
        await Assert.ThrowsExceptionAsync<IOException>(() => reopened.ApplyAsync(Silent, default));
    }

    [DataTestMethod]
    [DataRow(false, false, "file-denies-delete")]
    [DataRow(true, false, "file-denies-delete")]
    [DataRow(false, true, "file-denies-delete")]
    [DataRow(true, true, "file-denies-delete")]
    [DataRow(false, false, "file-shares-delete")]
    [DataRow(true, false, "file-shares-delete")]
    [DataRow(false, true, "file-shares-delete")]
    [DataRow(true, true, "file-shares-delete")]
    [DataRow(false, false, "directory-shares-delete")]
    [DataRow(true, false, "directory-shares-delete")]
    [DataRow(false, true, "directory-shares-delete")]
    [DataRow(true, true, "directory-shares-delete")]
    public async Task NativeFolderRecreatedMetadataLockUsesBoundedSessionRecovery(bool revert, bool exhaustRetries, string openKind)
    {
        using var fixture = new Fixture();
        var originalIdentity = ClientFolderMigration.ReadIdentity(fixture.Source);
        var sessionPath = Path.Combine(fixture.Root, "session.json");
        var ini = await PrepareInterruptedRecreatedFolderSession(fixture, sessionPath);
        var recreatedIdentity = ClientFolderMigration.ReadIdentity(fixture.Source);
        var metadata = File.ReadAllBytes(ini);
        var settings = File.ReadAllBytes(Path.Combine(fixture.State, "settings.json"));
        var mappings = fixture.Runtime.Mappings.ToArray();
        var registrationCalls = fixture.Runtime.Calls.ToArray();
        var waits = 0;
        // A descendant open can block its ancestor's rename even when it shares
        // DELETE. The directory case matches the real guest Pictures conflict.
        var blockedPath = openKind == "directory-shares-delete" ? Path.GetDirectoryName(ini)! : ini;
        using var held = OpenMetadataForLockTest(@"\\?\" + blockedPath, 0x81,
            openKind == "file-denies-delete" ? 1u : 7u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.IsFalse(held.IsInvalid, "The real fixture handle must open: " + Marshal.GetLastWin32Error());
        using var operation = fixture.Resume();
        using var session = MigrationSession.Open(sessionPath, [operation],
            new MigrationRetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero), (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                waits++;
                AssertUnchangedWhileLocked();
                if (!exhaustRetries) held.Dispose();
                return Task.CompletedTask;
            });
        var progress = new List<MigrationProgress>();
        session.ProgressChanged += progress.Add;
        if (revert) await session.RevertAsync("sync-folder"); else await session.RetryAsync("sync-folder");

        Assert.AreEqual(exhaustRetries ? 2 : 1, waits, "Only the bounded session retry policy should wait for the actual blocking handle.");
        Assert.AreEqual(waits, session.History.Count(entry => entry.Operation == "Waiting to retry"));
        Assert.AreEqual(waits, progress.Count(entry => entry.Operation == "Waiting to retry"));
        if (exhaustRetries)
        {
            var status = session.Items.Single();
            Assert.AreEqual(revert ? MigrationItemState.RevertFailed : MigrationItemState.Failed, status.State);
            StringAssert.Contains(status.Error!, blockedPath);
            Assert.IsTrue(session.CanRevert("sync-folder"));
            AssertUnchangedWhileLocked();
            return;
        }

        Assert.AreEqual(revert ? MigrationItemState.Reverted : MigrationItemState.Completed, session.Items.Single().State);
        var preserved = Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Single();
        Assert.AreEqual(recreatedIdentity, ClientFolderMigration.ReadIdentity(preserved));
        CollectionAssert.AreEqual(metadata, File.ReadAllBytes(Path.Combine(preserved, "Pictures", "desktop.ini")));
        var expected = revert ? fixture.Source : fixture.Target;
        Assert.AreEqual(originalIdentity, ClientFolderMigration.ReadIdentity(expected));
        Assert.AreEqual(expected, fixture.Storage.LoadSettings().RootPath);
        foreach (var name in new[] { "Documents", "Pictures", "Music" })
        {
            Assert.AreEqual(Path.Combine(expected, name), fixture.Runtime.Mappings[name]);
            Assert.AreEqual("original " + name + " bytes", File.ReadAllText(Path.Combine(expected, name, "owned.txt")));
        }
        Assert.AreEqual(1, fixture.Runtime.Calls.Count(value => value == "unregister:False"));
        if (revert) await operation.VerifyRevertedAsync(default); else await operation.VerifyAsync(default);

        void AssertUnchangedWhileLocked()
        {
            Assert.AreEqual(recreatedIdentity, ClientFolderMigration.ReadIdentity(fixture.Source));
            Assert.AreEqual(originalIdentity, ClientFolderMigration.ReadIdentity(fixture.Target));
            Assert.AreEqual(0, Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Length);
            CollectionAssert.AreEqual(metadata, File.ReadAllBytes(ini));
            CollectionAssert.AreEqual(settings, File.ReadAllBytes(Path.Combine(fixture.State, "settings.json")));
            CollectionAssert.AreEqual(mappings, fixture.Runtime.Mappings.ToArray());
            CollectionAssert.AreEqual(registrationCalls, fixture.Runtime.Calls.ToArray());
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenMetadataForLockTest(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeFolderRecreatedMetadataPermissionDenialIsNotMistakenForContention(bool revert)
    {
        using var fixture = new Fixture();
        var originalIdentity = ClientFolderMigration.ReadIdentity(fixture.Source);
        var sessionPath = Path.Combine(fixture.Records, "session.json");
        var ini = await PrepareInterruptedRecreatedFolderSession(fixture, sessionPath);
        var recreatedIdentity = ClientFolderMigration.ReadIdentity(fixture.Source);
        var metadata = File.ReadAllBytes(ini);
        var settings = File.ReadAllBytes(Path.Combine(fixture.State, "settings.json"));
        var mappings = fixture.Runtime.Mappings.ToArray();
        var parent = new DirectoryInfo(fixture.Root);
        var originalSddl = parent.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var deniedSecurity = parent.GetAccessControl(AccessControlSections.Access);
        deniedSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles, AccessControlType.Deny));
        var waits = 0;
        var denied = false;
        using var operation = fixture.Resume();
        using var session = MigrationSession.Open(sessionPath, [operation],
            new MigrationRetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero), (_, _) => { waits++; return Task.CompletedTask; });
        session.ProgressChanged += progress =>
        {
            if (progress.Operation != "Preserving recreated Windows folder" || denied) return;
            // Deny adding the rename target only on this fixture parent; the
            // non-inheriting rule leaves receipt files and inspected data readable.
            parent.SetAccessControl(deniedSecurity);
            denied = true;
        };
        try
        {
            if (revert) await session.RevertAsync("sync-folder"); else await session.RetryAsync("sync-folder");
            Assert.IsTrue(denied, "The native rename must run under the scoped parent ACL denial.");
            Assert.AreEqual(0, waits, "An access-denied rename without a blocking handle must not be reported as transient contention.");
            var status = session.Items.Single();
            Assert.AreEqual(revert ? MigrationItemState.RevertFailed : MigrationItemState.Failed, status.State);
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.Error));
            Assert.AreEqual(deniedSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                parent.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                "Recovery must not modify permissions to force a rename.");
            Assert.AreEqual(recreatedIdentity, ClientFolderMigration.ReadIdentity(fixture.Source));
            Assert.AreEqual(originalIdentity, ClientFolderMigration.ReadIdentity(fixture.Target));
            Assert.AreEqual(0, Directory.GetDirectories(fixture.Root, "CloudInlet recovered folder *").Length);
            CollectionAssert.AreEqual(metadata, File.ReadAllBytes(ini));
            CollectionAssert.AreEqual(settings, File.ReadAllBytes(Path.Combine(fixture.State, "settings.json")));
            CollectionAssert.AreEqual(mappings, fixture.Runtime.Mappings.ToArray());
        }
        finally
        {
            if (denied)
            {
                var restore = new DirectorySecurity();
                restore.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
                parent.SetAccessControl(restore);
                Assert.AreEqual(originalSddl, parent.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            }
        }
    }

    private static async Task<string> PrepareInterruptedRecreatedFolderSession(Fixture fixture, string sessionPath)
    {
        fixture.AddWindowsBackups();
        fixture.Runtime.FailMappingAt = 0;
        fixture.Runtime.FailMappingAfterWrite = true;
        using (var initial = fixture.Create())
        using (var session = MigrationSession.Create(fixture.Plan, ["sync-folder"], sessionPath, [initial]))
        {
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Failed, session.Items.Single().State);
        }
        return CreateRecreatedPictures(fixture);
    }

    private static string CreateRecreatedPictures(Fixture fixture)
    {
        Directory.CreateDirectory(Path.Combine(fixture.Source, "Pictures"));
        var ini = Path.Combine(fixture.Source, "Pictures", "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]\r\nLocalizedResourceName=@%SystemRoot%\\system32\\shell32.dll,-21779\r\n");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        return ini;
    }

    [DataTestMethod]
    [DataRow(0, false, false)]
    [DataRow(0, true, false)]
    [DataRow(1, false, false)]
    [DataRow(1, true, false)]
    [DataRow(2, false, false)]
    [DataRow(2, true, false)]
    [DataRow(0, false, true)]
    [DataRow(0, true, true)]
    [DataRow(1, false, true)]
    [DataRow(1, true, true)]
    [DataRow(2, false, true)]
    [DataRow(2, true, true)]
    public async Task NativeFolderPartialWindowsMappingsCanResumeOrRestore(int failureIndex, bool changedBeforeFailure, bool restore)
    {
        // Directory moves, protected receipts and settings are real; Shell mapping
        // writes are injected so this test never redirects the user's personal folders.
        using var fixture = new Fixture();
        var names = fixture.AddWindowsBackups();
        var unrelatedMapping = Path.Combine(fixture.Root, "UnrelatedDownloads");
        fixture.Runtime.Mappings["Downloads"] = unrelatedMapping;
        fixture.Runtime.FailMappingAt = failureIndex;
        fixture.Runtime.FailMappingAfterWrite = changedBeforeFailure;
        using (var operation = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));

        var appliedCount = failureIndex + (changedBeforeFailure ? 1 : 0);
        for (var index = 0; index < names.Length; index++)
            Assert.AreEqual(Path.Combine(index < appliedCount ? fixture.Target : fixture.Source, names[index]),
                fixture.Runtime.Mappings[names[index]], "The interruption must leave the expected partial mapping state.");
        Assert.IsTrue(Directory.Exists(fixture.Target));
        Assert.AreEqual(fixture.Source, fixture.Storage.LoadSettings().RootPath,
            "Settings must not commit until every coupled Windows mapping succeeds.");
        fixture.Storage.SaveSettings(fixture.Storage.LoadSettings() with { Theme = "Light" });

        using (var resumed = fixture.Resume())
        {
            if (restore)
            {
                await resumed.RevertAsync(Silent, default);
                await resumed.VerifyRevertedAsync(default);
            }
            else
            {
                await resumed.ApplyAsync(Silent, default);
                await resumed.VerifyAsync(default);
            }
        }
        var expectedRoot = restore ? fixture.Source : fixture.Target;
        var settings = fixture.Storage.LoadSettings();
        Assert.AreEqual(expectedRoot, settings.RootPath);
        Assert.AreEqual("Light", settings.Theme, "Recovery must retain unrelated user settings.");
        foreach (var name in names)
        {
            Assert.AreEqual(Path.Combine(expectedRoot, name), fixture.Runtime.Mappings[name]);
            Assert.AreEqual(Path.Combine(expectedRoot, name), settings.Backups.Single(folder => folder.Name == name).DestinationPath);
            Assert.AreEqual("original " + name + " bytes", File.ReadAllText(Path.Combine(expectedRoot, name, "owned.txt")));
        }
        Assert.AreEqual(unrelatedMapping, fixture.Runtime.Mappings["Downloads"]);
        Assert.AreEqual("preserve the original bytes", File.ReadAllText(Path.Combine(expectedRoot, "Documents", "personal.txt")));
        Assert.AreEqual(1, fixture.Runtime.Calls.Count(value => value == "unregister:False"),
            "Recovery must not repeat original hydration after the directory was moved.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeFolderPartialMappingsPreserveExternalChangeBeforeRetryOrRestore(bool restore)
    {
        using var fixture = new Fixture();
        fixture.AddWindowsBackups();
        fixture.Runtime.FailMappingAt = 1;
        using (var operation = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        var external = Path.Combine(fixture.Root, "AnotherProviderPictures");
        fixture.Runtime.Mappings["Pictures"] = external;
        var mappings = new Dictionary<string, string>(fixture.Runtime.Mappings);
        var settings = File.ReadAllBytes(Path.Combine(fixture.State, "settings.json"));
        var nativeCalls = fixture.Runtime.Calls.ToArray();
        using (var resumed = fixture.Resume())
        {
            var error = await Assert.ThrowsExceptionAsync<IOException>(() => restore
                ? resumed.RevertAsync(Silent, default) : resumed.ApplyAsync(Silent, default));
            StringAssert.Contains(error.Message, "changed outside this migration");
        }
        foreach (var mapping in mappings) Assert.AreEqual(mapping.Value, fixture.Runtime.Mappings[mapping.Key]);
        CollectionAssert.AreEqual(settings, File.ReadAllBytes(Path.Combine(fixture.State, "settings.json")));
        CollectionAssert.AreEqual(nativeCalls, fixture.Runtime.Calls);
        Assert.IsTrue(Directory.Exists(fixture.Target));
        Assert.IsFalse(Directory.Exists(fixture.Source));
        Assert.AreEqual("original Pictures bytes", File.ReadAllText(Path.Combine(fixture.Target, "Pictures", "owned.txt")));
    }

    [TestMethod]
    public async Task NativeFolderFailedRegistrationCanRevertOnlyOwnedSettings()
    {
        using var fixture = new Fixture(); fixture.Runtime.FailRegistrationOnce = true;
        using (var operation = fixture.Create())
            await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        var changed = fixture.Storage.LoadSettings() with { Theme = "Light" };
        fixture.Storage.SaveSettings(changed);
        using var resumed = fixture.Resume();
        await resumed.RevertAsync(Silent, default); await resumed.VerifyRevertedAsync(default);
        var settings = fixture.Storage.LoadSettings();
        Assert.AreEqual("Light", settings.Theme);
        Assert.AreEqual(fixture.Source, settings.RootPath);
        Assert.AreEqual(0, settings.RootBindings.Count);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "Documents", "personal.txt")));
        Assert.IsFalse(Directory.Exists(fixture.Target));
    }

    [TestMethod]
    public async Task NativeFolderRevertBeforeAnyMoveDoesNotRequireDirectoryReceipt()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        Assert.AreEqual(fixture.Source, fixture.Storage.LoadSettings().RootPath);
    }

    [TestMethod]
    public void NativeFolderMigrationRejectsConflictingDestinationAndPendingUploads()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Target);
        using (var operation = fixture.Create())
            Assert.ThrowsException<IOException>(() => operation.CheckAsync(default).GetAwaiter().GetResult());
        Directory.Delete(fixture.Target);
        Directory.CreateDirectory(Path.Combine(fixture.State, "Uploads"));
        File.WriteAllText(Path.Combine(fixture.State, "Uploads", "pending.json"), "{}");
        var error = Assert.ThrowsException<IOException>(() => fixture.Create());
        StringAssert.Contains(error.Message, "uploads");
        Assert.IsTrue(Directory.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task NativeFolderPreflightConflictCanBeDeclinedAfterRestartWithoutTouchingEitherFolder()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Target);
        var unrelated = Path.Combine(fixture.Target, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "belongs to somebody else");
        var settings = File.ReadAllBytes(Path.Combine(fixture.State, "settings.json"));
        var journal = Path.Combine(fixture.Records, "session.json");
        using (var operation = fixture.Create())
        using (var session = MigrationSession.Create(fixture.Plan, ["sync-folder"], journal, [operation], new(1)))
        {
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Failed, session.Items.Single().State);
        }
        using (var operation = fixture.Resume())
        using (var session = MigrationSession.Open(journal, [operation]))
        {
            Assert.IsTrue(session.CanRevert("sync-folder"));
            await session.RevertAsync("sync-folder");
            Assert.AreEqual(MigrationItemState.Reverted, session.Items.Single().State, session.Items.Single().Error);
        }
        Assert.AreEqual("belongs to somebody else", await File.ReadAllTextAsync(unrelated));
        Assert.AreEqual("preserve the original bytes", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "Documents", "personal.txt")));
        CollectionAssert.AreEqual(settings, File.ReadAllBytes(Path.Combine(fixture.State, "settings.json")));
        Assert.AreEqual(0, fixture.Runtime.Calls.Count);
    }

    [TestMethod]
    public async Task NativeFolderPreflightRevertRejectsChangedSourceAndMissingReceipt()
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        var receipt = Directory.GetFiles(fixture.Records, "*.folder.dpapi").Single();
        var retained = fixture.Source + "-retained";
        Directory.Move(fixture.Source, retained);
        Directory.CreateDirectory(fixture.Source);
        using (var operation = fixture.Resume())
            await Assert.ThrowsExceptionAsync<IOException>(() => operation.RevertAsync(Silent, default));
        File.Delete(receipt);
        Assert.ThrowsException<FileNotFoundException>(() => fixture.Resume());
        Assert.IsTrue(File.Exists(Path.Combine(retained, "Documents", "personal.txt")));
    }

    [TestMethod]
    public void NativeFolderMigrationRejectsUnrelatedMappingWithoutMutation()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Mappings["Documents"] = Path.Combine(fixture.Root, "elsewhere");
        Assert.ThrowsException<IOException>(() => fixture.Create());
        Assert.AreEqual(0, fixture.Runtime.Calls.Count);
        Assert.IsTrue(Directory.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task NativeFolderMigrationPreservesCompletedImportHistoryWithTypedDestination()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "import-source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "import.txt"), "historical source");
        var plan = FolderImport.Preview(source, Path.Combine(fixture.Source, "Imported"));
        var record = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, plan,
            "account|bucket|CloudBay/|" + fixture.Source.ToUpperInvariant(), "Completed");
        new ImportJournal(fixture.State).Save(record);
        using var operation = fixture.Create(); await operation.ApplyAsync(Silent, default);
        var history = new ImportJournal(fixture.State).Read("account|bucket|CloudBay/|" + fixture.Target.ToUpperInvariant()).Single();
        Assert.AreEqual(record.Plan.SourcePath, history.Plan.SourcePath);
        Assert.AreEqual(record.Plan.Fingerprint, history.Plan.Fingerprint);
        Assert.AreEqual(Path.Combine(fixture.Target, "Imported"), history.Plan.DestinationPath);
        Assert.AreEqual(record.Id, history.Id);
    }

    [TestMethod]
    public void NativeFolderMigrationRejectsInterruptedImport()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "import-source"); Directory.CreateDirectory(source);
        var plan = FolderImport.Preview(source, Path.Combine(fixture.Source, "Imported"));
        new ImportJournal(fixture.State).Save(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, plan,
            "account|bucket|CloudBay/|" + fixture.Source.ToUpperInvariant(), "Copying"));
        StringAssert.Contains(Assert.ThrowsException<IOException>(() => fixture.Create()).Message, "interrupted folder import");
    }

    [TestMethod]
    public async Task NativeFolderMigrationKeepsCompletedOtherRootBinding()
    {
        using var fixture = new Fixture();
        var other = new SyncRootBinding(Path.Combine(fixture.Root, "Other"), new string('A', 24), true, "account", "bucket", "Other/");
        fixture.Storage.SaveSettings(fixture.Settings with { RootBindings = [other] });
        using var operation = fixture.Create(); await operation.ApplyAsync(Silent, default);
        await operation.RevertAsync(Silent, default);
        Assert.AreEqual(other, fixture.Storage.LoadSettings().RootBindings.Single());
    }

    [TestMethod]
    public void NativeFolderReceiptRejectsTamperingAndWrongStateDirectory()
    {
        using var fixture = new Fixture(); fixture.Create().Dispose();
        var alternative = Path.Combine(fixture.Root, "different-state"); Directory.CreateDirectory(alternative);
        Assert.ThrowsException<InvalidDataException>(() => ClientFolderMigration.Resume(fixture.Plan, "sync-folder", alternative, fixture.Records, fixture.Runtime));
        var path = Directory.GetFiles(fixture.Records, "*.folder.dpapi").Single();
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => fixture.Resume());
    }

    [TestMethod]
    public void RootBindingCannotReuseManifestForADifferentCloudAccountOrPrefix()
    {
        using var fixture = new Fixture();
        var binding = new SyncRootBinding(fixture.Source, new string('A', 24), true, "account", "bucket", "CloudBay/");
        var settings = fixture.Settings with { RootBindings = [binding] };
        Assert.AreEqual(binding.StateName, SyncRootMigrationIdentity.StateName(settings, fixture.Source, settings.Prefix));
        Assert.AreNotEqual(binding.StateName, SyncRootMigrationIdentity.StateName(settings with { AccountId = "other" }, fixture.Source, settings.Prefix));
        Assert.AreNotEqual(binding.StateName, SyncRootMigrationIdentity.StateName(settings, fixture.Source, "another/"));
    }

    [TestMethod]
    public void NativeFolderMigrationRejectsAnUncheckedNestedBackup()
    {
        using var fixture = new Fixture();
        fixture.Storage.SaveSettings(fixture.Settings with { CustomBackups = [new("nested", Path.Combine(fixture.Source, "nested"), "CloudBay/.cloudbay-backups/nested/")] });
        StringAssert.Contains(Assert.ThrowsException<IOException>(() => fixture.Create()).Message, "overlaps another configured backup");
        Assert.IsTrue(Directory.Exists(fixture.Source));
    }

    [TestMethod]
    public void NativeFolderMigrationRejectsChangedBaselineBeforeMoving()
    {
        using var fixture = new Fixture(); using var operation = fixture.Create();
        fixture.Storage.SaveSettings(fixture.Settings with { RootBindings = [new(fixture.Source, new string('F', 24), false, "account", "bucket", "CloudBay/")] });
        StringAssert.Contains(Assert.ThrowsException<IOException>(() => operation.CheckAsync(default).GetAwaiter().GetResult()).Message, "baseline changed");
        Assert.IsTrue(Directory.Exists(fixture.Source));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudInlet-folder-migration-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay");
        public string Target => Path.Combine(Root, BuildInfo.Flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet");
        public string State => Path.Combine(Root, "state");
        public string Records => Path.Combine(Root, "receipts");
        public ClientStorage Storage { get; }
        public AppSettings Settings { get; }
        public FakeRuntime Runtime { get; }
        public MigrationPlan Plan => MigrationPlan.Create([new("sync-folder", MigrationArea.SyncFolder, "Sync folder", Source, Target, "Move exact folder")]);
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Source, "Documents")); Directory.CreateDirectory(Records);
            File.WriteAllText(Path.Combine(Source, "Documents", "personal.txt"), "preserve the original bytes");
            Storage = new ClientStorage(State);
            Settings = new AppSettings { RootPath = Source, Prefix = "CloudBay/", AccountId = "account", BucketId = "bucket", KeyId = "key",
                Backups = [new("Documents", Path.Combine(Root, "OriginalDocuments"), Path.Combine(Source, "Documents"))],
                SelectedExclusions = [new(Source, "excluded", true)] };
            Storage.SaveSettings(Settings); Runtime = new(Source);
        }
        public ClientFolderMigration Create() => ClientFolderMigration.Create(Plan, "sync-folder", State, Records, Runtime);
        public ClientFolderMigration Resume() => ClientFolderMigration.Resume(Plan, "sync-folder", State, Records, Runtime);
        public string[] AddWindowsBackups()
        {
            string[] names = ["Documents", "Pictures", "Music"];
            foreach (var name in names)
            {
                var path = Path.Combine(Source, name);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "owned.txt"), "original " + name + " bytes");
                Runtime.Mappings[name] = path;
            }
            Storage.SaveSettings(Settings with
            {
                Backups = names.Select(name => new BackupFolder(name, Path.Combine(Root, "Original" + name),
                    Path.Combine(Source, name))).ToList()
            });
            return names;
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "CloudInlet-folder-migration-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its owned temporary directory.");
            Directory.Delete(resolved, true);
        }
    }
    private sealed class FakeRuntime(string source) : IClientFolderMigrationRuntime
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, string> Mappings = new() { ["Documents"] = Path.Combine(source, "Documents") };
        private string? _registered = source;
        private bool _canonical;
        public bool FailMappingOnce, FailRegistrationOnce;
        public int? FailMappingAt;
        public bool FailMappingAfterWrite;
        private int _mappingAttempts;
        public bool IsRegistered(string root, string account, bool canonical) => _registered == root && _canonical == canonical;
        public Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials,
            IProgress<string> progress, CancellationToken cancellationToken)
        { Calls.Add("unregister:" + canonical); _registered = null; return Task.CompletedTask; }
        public Task RegisterAsync(string root, string account, bool canonical, string manifestPath, CancellationToken cancellationToken)
        {
            if (FailRegistrationOnce) { FailRegistrationOnce = false; throw new IOException("fixture registration failure"); }
            Calls.Add("register:" + canonical); _registered = root; _canonical = canonical; return Task.CompletedTask;
        }
        public string GetKnownFolder(string name) => Mappings[name];
        public void ExchangeKnownFolder(string name, string expected, string replacement)
        {
            if (Mappings[name] != expected && Mappings[name] != replacement) throw new IOException("mapping changed");
            if (Mappings[name] == replacement) return;
            var fail = FailMappingOnce || FailMappingAt == _mappingAttempts++;
            if (fail)
            {
                FailMappingOnce = false;
                FailMappingAt = null;
                if (!FailMappingAfterWrite) throw new IOException("fixture mapping failure before write");
            }
            Mappings[name] = replacement;
            if (fail) throw new IOException("fixture mapping failure after write");
        }
    }
    private sealed class InlineProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => report(value); }

    internal sealed class FixtureCloud(string account, byte[] payload) : ICloudStore
    {
        public bool FailDownloadOnce;
        public int ConnectCalls;
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default)
        { ConnectCalls++; return Task.FromResult(new CloudAccount(account, "https://fixture.invalid", "https://fixture.invalid", [], "fixture")); }
        public async Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (FailDownloadOnce) { FailDownloadOnce = false; throw new HttpRequestException("Isolated fixture network interruption."); }
            await destination.WriteAsync(payload.AsMemory((int)offset, (int)(length ?? payload.Length - offset)), cancellationToken);
        }
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1, DateTimeOffset modifiedUtc,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

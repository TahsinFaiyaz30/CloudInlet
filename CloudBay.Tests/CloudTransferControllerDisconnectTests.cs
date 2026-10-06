using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class CloudTransferControllerDisconnectTests
{
    [TestMethod]
    public async Task DefaultDisconnectDoesNotRequireDownloadOrOpenCredentials()
    {
        var state = NewState();
        try
        {
            var storage = Configure(state);
            await using var controller = new ClientController(storage, _ => Assert.Fail(), manageStartup: false);
            await controller.DisconnectAsync();
            Assert.IsFalse(controller.Settings.IsConfigured);
            Assert.IsFalse(Directory.Exists(storage.LoadSettings().RootPath));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task DisconnectOnlyWorksOfflineWithoutOpeningVaultDownloadingOrDeletingUserFiles()
    {
        var state = NewState();
        try
        {
            var storage = Configure(state);
            var root = storage.LoadSettings().RootPath;
            Directory.CreateDirectory(root);
            var userFile = Path.Combine(root, "unsynced.txt");
            await File.WriteAllTextAsync(userFile, "unsynced personal data");
            var registrations = new List<string>();
            await using var controller = new ClientController(storage, registrations.Add, manageStartup: false);
            var outcome = await controller.DisconnectAsync(DisconnectMode.DisconnectOnly);
            Assert.IsNull(outcome);
            Assert.IsFalse(controller.Settings.IsConfigured);
            Assert.AreEqual(ClientState.NotConnected, controller.Snapshot.State);
            Assert.AreEqual("unsynced personal data", await File.ReadAllTextAsync(userFile));
            Assert.AreEqual(0, registrations.Count, "Disconnect-only retains registration so offline placeholders can reconnect safely.");
            Assert.IsFalse(File.Exists(Path.Combine(state, "credentials.dpapi")));
            Assert.IsFalse(File.Exists(Path.Combine(state, "disconnect-pending.json")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task InterruptedDisconnectOnlyFinishesBeforeNativeRootOrProviderStarts()
    {
        var state = NewState();
        try
        {
            var storage = Configure(state);
            var settings = storage.LoadSettings();
            storage.SaveAccountDisconnectIntent(new(DisconnectMode.DisconnectOnly, settings.AccountId, settings.BucketId, settings.RootPath));
            await using var controller = new ClientController(storage, _ => Assert.Fail("An offline disconnect must retain registration."), manageStartup: false);
            await controller.StartAsync();
            Assert.IsFalse(controller.Settings.IsConfigured);
            Assert.IsFalse(Directory.Exists(settings.RootPath));
            Assert.IsFalse(File.Exists(Path.Combine(state, "disconnect-pending.json")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task RemoveLocalCopiesRefusesUnverifiableAccountAndPreservesFilesAndRecoveryIntent()
    {
        var state = NewState();
        try
        {
            var storage = Configure(state);
            var settings = storage.LoadSettings();
            Directory.CreateDirectory(settings.RootPath);
            var userFile = Path.Combine(settings.RootPath, "personal.txt");
            await File.WriteAllTextAsync(userFile, "kept");
            await using var controller = new ClientController(storage, _ => Assert.Fail(), manageStartup: false);
            await Assert.ThrowsExceptionAsync<System.Security.Cryptography.CryptographicException>(() =>
                controller.DisconnectAsync(DisconnectMode.RemoveLocalCopyAndDisconnect));
            Assert.IsTrue(controller.Settings.IsConfigured);
            Assert.AreEqual("kept", await File.ReadAllTextAsync(userFile));
            Assert.IsNotNull(storage.LoadAccountDisconnectIntent());
            Assert.AreEqual(ClientState.Attention, controller.Snapshot.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task InvalidDisconnectModeDoesNotPersistOrDisconnect()
    {
        var state = NewState();
        try
        {
            var storage = Configure(state);
            await using var controller = new ClientController(storage, _ => Assert.Fail(), manageStartup: false);
            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => controller.DisconnectAsync((DisconnectMode)99));
            Assert.IsTrue(controller.Settings.IsConfigured);
            Assert.IsNull(storage.LoadAccountDisconnectIntent());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [DataTestMethod]
    [DataRow(DisconnectMode.DisconnectOnly)]
    [DataRow(DisconnectMode.DownloadAndDisconnect)]
    [DataRow(DisconnectMode.RemoveLocalCopyAndDisconnect)]
    public async Task PendingCloudBackupStopRetainsAccountRecoveryBeforeAnyDisconnectMutation(DisconnectMode mode)
    {
        var state = NewState();
        try
        {
            var storage = Configure(state);
            var settings = storage.LoadSettings();
            var source = new TransferLocation("b2", settings.AccountId, settings.BucketId, "", settings.Prefix + "Documents/", "Backblaze B2 · " + settings.BucketName);
            var destination = new TransferLocation("onedrive", "microsoft-account", "drive", "folder", "", "OneDrive");
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source, destination, TransferOperation.Move,
                TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            storage.SaveCloudBackupStopIntent(new("Documents", Path.Combine(state, "PreviousDocuments"), plan));
            var savedStop = File.ReadAllBytes(Path.Combine(state, "cloud-backup-stop.json"));
            var savedVault = File.ReadAllBytes(Path.Combine(state, "credentials.dpapi"));
            await using (var controller = new ClientController(storage, _ => Assert.Fail("No native unregister is needed."), manageStartup: false))
            {
                Assert.AreEqual(0, controller.CloudTransferJobs.Count, "The interrupted stop has not reached durable job creation yet.");
                var error = await Assert.ThrowsExceptionAsync<IOException>(() => controller.DisconnectAsync(mode));
                StringAssert.Contains(error.Message, "interrupted folder backup stop");
                Assert.AreEqual(settings.AccountId, controller.Settings.AccountId);
                Assert.AreEqual(settings.BucketId, controller.Settings.BucketId);
                Assert.IsTrue(controller.Settings.IsConfigured);
                Assert.IsNull(storage.LoadAccountDisconnectIntent(), "Reject before creating a disconnect intent that would block account reconnection.");
                CollectionAssert.AreEqual(savedStop, File.ReadAllBytes(Path.Combine(state, "cloud-backup-stop.json")));
                CollectionAssert.AreEqual(savedVault, File.ReadAllBytes(Path.Combine(state, "credentials.dpapi")));
            }
            await using (var restored = new ClientController(storage, _ => Assert.Fail(), manageStartup: false))
            {
                Assert.IsTrue(restored.Settings.IsConfigured);
                Assert.AreNotEqual(ClientState.Attention, restored.Snapshot.State, "The preserved account must still validate the pending stop after restart.");
                await restored.UpdatePreferencesAsync(new() { Theme = "Dark" });
                Assert.IsNotNull(storage.LoadCloudBackupStopIntent());
                // Represents successful stop recovery: its intent is cleared only
                // after the reviewed mapping and transfer plan have been saved.
                storage.ClearCloudBackupStopIntent();
                await restored.DisconnectAsync(DisconnectMode.DisconnectOnly);
                Assert.IsFalse(restored.Settings.IsConfigured);
                Assert.IsNull(storage.LoadAccountDisconnectIntent());
            }
            Assert.IsFalse(Directory.Exists(settings.RootPath), "The guard must not open a native root, credentials or cloud payload.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    private static ClientStorage Configure(string state)
    {
        var storage = new ClientStorage(state);
        storage.SaveSettings(new() { KeyId = "fixture-key", AccountId = "fixture-account", BucketId = "fixture-bucket",
            BucketName = "fixture", RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
        // Proves DisconnectOnly never decrypts or authorizes this intentionally invalid vault.
        File.WriteAllBytes(Path.Combine(state, "credentials.dpapi"), new byte[64]);
        return storage;
    }
    private static string NewState()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudBay-disconnect-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}

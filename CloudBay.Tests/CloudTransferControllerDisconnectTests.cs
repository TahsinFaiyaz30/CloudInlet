using CloudBay.Application;
using CloudBay.Core;
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

using System.Security.Cryptography;
using CloudBay.Application;
using CloudBay.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace CloudBay.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ClientControllerTests
{
    [DataTestMethod]
    [DataRow("{\"SchemaVersion\":1,\"CustomBackups\":null}")]
    [DataRow("{\"SchemaVersion\":1,\"Backups\":[null]}")]
    public async Task InvalidBackupCollectionsEnterRecoveryBeforeUiOrLifecycleReads(string damagedSettings)
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            var settingsPath = Path.Combine(state, "settings.json");
            await File.WriteAllTextAsync(settingsPath, damagedSettings);
            await using var controller = new ClientController(storage);
            Assert.AreEqual(ClientState.Attention, controller.Snapshot.State);
            Assert.IsNotNull(controller.Settings.Backups);
            Assert.IsNotNull(controller.Settings.CustomBackups);
            await controller.StartAsync();
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.SaveSettingsAsync(new()));
            Assert.AreEqual(damagedSettings, await File.ReadAllTextAsync(settingsPath), "Invalid settings must remain available for recovery.");
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task CorruptSettingsBlockAccountAndSettingsWritesAndPreserveBackupJournal()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            var settingsPath = Path.Combine(state, "settings.json");
            const string interruptedSettings = "{\"SchemaVersion\":1,";
            await File.WriteAllTextAsync(settingsPath, interruptedSettings);
            storage.SaveBackupIntent(new("Desktop", Path.Combine(state, "Original"), Path.Combine(state, "Root", "Desktop")), true);
            var journalPath = Path.Combine(state, "backup-pending.json");
            var journal = await File.ReadAllBytesAsync(journalPath);
            var startup = ReadStartup();
            await using var controller = new ClientController(storage);
            Assert.AreEqual(ClientState.Attention, controller.Snapshot.State);
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.SaveSettingsAsync(new() { StartAtSignIn = true }));
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.ConnectAsync("unused-test-id", "unused-test-key", "unused-test-bucket", Path.Combine(state, "Root"), "CloudBay/"));
            Assert.AreEqual(interruptedSettings, await File.ReadAllTextAsync(settingsPath));
            CollectionAssert.AreEqual(journal, await File.ReadAllBytesAsync(journalPath));
            Assert.AreEqual(startup, ReadStartup(), "Rejected recovery operations must not alter sign-in startup.");
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task SettingsCannotAddBackupRootsWithoutNativeFolderControls()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { StartAtSignIn = false, RootPath = Path.Combine(state, "Root") });
            var original = await File.ReadAllBytesAsync(Path.Combine(state, "settings.json"));
            var startup = ReadStartup();
            await using var controller = new ClientController(storage);
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.SaveSettingsAsync(controller.Settings with
            {
                StartAtSignIn = true,
                CustomBackups = [new("External", Path.Combine(state, "External"), "CloudBay/.cloudbay-backups/External/")]
            }));
            Assert.AreEqual(0, controller.Settings.CustomBackups.Count);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(state, "settings.json")));
            Assert.AreEqual(startup, ReadStartup(), "Rejected folder changes must not alter startup before validation.");
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task CorruptCredentialVaultRetainsRecoveryStateAndDoesNotConnect()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new()
            {
                KeyId = "unused-test-id", BucketId = "unused-test-bucket", BucketName = "test",
                StartAtSignIn = false, RootPath = Path.Combine(state, "Root")
            });
            var settings = await File.ReadAllBytesAsync(Path.Combine(state, "settings.json"));
            var ciphertext = RandomNumberGenerator.GetBytes(128);
            var vaultPath = Path.Combine(state, "credentials.dpapi");
            await File.WriteAllBytesAsync(vaultPath, ciphertext);
            var startup = ReadStartup();
            await using var controller = new ClientController(storage);
            await controller.StartAsync();
            Assert.AreEqual(ClientState.Attention, controller.Snapshot.State);
            Assert.IsTrue(controller.Settings.IsConfigured, "Saved account identity must remain available for recovery.");
            CollectionAssert.AreEqual(ciphertext, await File.ReadAllBytesAsync(vaultPath));
            CollectionAssert.AreEqual(settings, await File.ReadAllBytesAsync(Path.Combine(state, "settings.json")));
            Assert.AreEqual(startup, ReadStartup());
            Assert.IsFalse(Directory.Exists(controller.Settings.RootPath), "Credential failure must occur before root registration.");
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public void ClearingRotatedCredentialVaultRemovesBothCurrentAndBackup()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveCredentials(new("unused-test-id", "first-unused-test-key"));
            storage.SaveCredentials(new("unused-test-id", "second-unused-test-key"));
            Assert.AreEqual("second-unused-test-key", storage.LoadCredentials()!.ApplicationKey);
            Assert.IsTrue(File.Exists(Path.Combine(state, "credentials.dpapi.bak")));
            storage.ClearCredentials();
            Assert.IsFalse(File.Exists(Path.Combine(state, "credentials.dpapi")));
            Assert.IsFalse(File.Exists(Path.Combine(state, "credentials.dpapi.bak")));
        }
        finally { Directory.Delete(state, true); }
    }

    private static string CreateState()
    {
        var state = Path.Combine(Path.GetTempPath(), "CloudBay-ControllerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        return state;
    }

    private static string ReadStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null || !key.GetValueNames().Contains("CloudBay", StringComparer.OrdinalIgnoreCase)) return "absent";
        return key.GetValueKind("CloudBay") + ":" + System.Text.Json.JsonSerializer.Serialize(key.GetValue("CloudBay", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }
}

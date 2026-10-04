using System.Text.Json;
using CloudBay.Application;
using CloudBay.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class BackupIntentChoiceTests
{
    [TestMethod]
    public void SelectedRestoreDestinationSurvivesAtomicJournalRoundTrip()
    {
        var state = Path.Combine(Path.GetTempPath(), "CloudBay-IntentChoice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ClientStorage(state);
            var folder = new BackupFolder("Pictures", Path.Combine(state, "Original"), Path.Combine(state, "CloudBay", "Pictures"));
            var chosen = Path.Combine(state, "Another cloud", "Pictures");
            storage.SaveBackupIntent(folder, false, chosen);
            var reopened = new ClientStorage(state).LoadBackupIntent();
            Assert.AreEqual(folder, reopened!.Folder);
            Assert.AreEqual(chosen, reopened.RestorePath);
            Assert.IsFalse(reopened.Enable);
            Assert.IsFalse(Directory.Exists(chosen), "Writing intent must never create a destination or transfer files.");
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    [TestMethod]
    public void PreviousReleaseIntentRetainsItsOriginalRestoreLocation()
    {
        var legacy = JsonSerializer.Deserialize<ClientStorage.BackupIntent>("""
            {"Folder":{"Name":"Documents","OriginalPath":"C:\\Original","DestinationPath":"C:\\CloudBay\\Documents"},"Enable":false}
            """);
        Assert.IsNotNull(legacy);
        Assert.IsNull(legacy.RestorePath);
        Assert.AreEqual(@"C:\Original", legacy.Folder.OriginalPath);
    }
}

[TestClass]
public sealed class NativeBackupIntentChoiceTests
{
    [TestMethod]
    public async Task RecoveryRecognizesSelectedRestoreMappingWithoutMovingFilesAgain()
    {
        var state = Path.Combine(Path.GetTempPath(), "CloudBay-IntentChoice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ClientStorage(state);
            var root = Path.Combine(state, "CloudBay");
            var folder = new BackupFolder("Pictures", Path.Combine(state, "Previous"), Path.Combine(root, "Pictures"));
            // Read the current real mapping; this test never changes it.
            var actual = CloudBay.Windows.KnownFolderBackup.GetPath("Pictures");
            storage.SaveSettings(new() { RootPath = root, Backups = [folder] });
            storage.SaveBackupIntent(folder, false, actual);
            await using var controller = new ClientController(storage, manageStartup: false);
            Assert.AreEqual(0, controller.Settings.Backups.Count);
            Assert.IsNull(storage.LoadBackupIntent());
            Assert.AreEqual(actual, CloudBay.Windows.KnownFolderBackup.GetPath("Pictures"));
            Assert.IsFalse(Directory.Exists(folder.OriginalPath));
            Assert.IsFalse(Directory.Exists(folder.DestinationPath), "Recovery must not repeat copying or deleting after a committed mapping.");
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }
}

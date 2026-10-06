using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class NotificationPreferenceTests
{
    [TestMethod]
    public void ExistingSettingsWithoutNotificationFieldsUseQuietUsefulDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1,\"Theme\":\"Dark\"}")!;
        Assert.AreEqual("Dark", settings.Theme);
        Assert.IsTrue(settings.Notifications.Enabled);
        Assert.IsTrue(settings.Notifications.BackupProblems);
        Assert.IsTrue(settings.Notifications.FolderChanges);
        Assert.IsTrue(settings.Notifications.Updates);
        Assert.IsFalse(settings.Notifications.SyncCompleted);
        Assert.IsFalse(settings.Notifications.Sound);
    }

    [TestMethod]
    public async Task SavingNotificationPreferencesDoesNotCreateARootOrChangeOtherPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudInlet-NotificationPreferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ClientStorage(directory);
            var initial = new AppSettings { StartAtSignIn = false, Theme = "Dark", RootPath = Path.Combine(directory, "Root"), UploadConcurrency = 3 };
            storage.SaveSettings(initial);
            var preferences = new NotificationPreferences { Enabled = false, Updates = false, Sound = true };
            await using var controller = new ClientController(storage, manageStartup: false);
            await controller.UpdatePreferencesAsync(new() { Notifications = preferences });
            var actual = storage.LoadSettings();
            Assert.AreEqual(preferences, actual.Notifications);
            Assert.AreEqual(JsonSerializer.Serialize(initial with { Notifications = preferences }), JsonSerializer.Serialize(actual));
            Assert.IsFalse(Directory.Exists(initial.RootPath));
            // A later unrelated preference patch must retain notification choices.
            await controller.UpdatePreferencesAsync(new() { Theme = "Light" });
            Assert.AreEqual(preferences, storage.LoadSettings().Notifications);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

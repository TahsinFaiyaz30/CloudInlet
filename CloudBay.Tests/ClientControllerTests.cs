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
    [DataRow("{\"SchemaVersion\":1,\"SelectedExclusions\":null}")]
    [DataRow("{\"SchemaVersion\":1,\"GuidedExclusions\":[null]}")]
    [DataRow("{\"SchemaVersion\":1,\"DisabledLegacyExclusions\":null}")]
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
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.UpdatePreferencesAsync(new() { Theme = "Dark" }));
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
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.UpdatePreferencesAsync(new() { StartAtSignIn = true }));
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

    [TestMethod]
    public async Task ConcurrentPreferencePatchesMergeWithoutReplacingAccountOrBackupOwnership()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            var root = Path.Combine(state, "Root");
            var original = new AppSettings
            {
                RootPath = root, StartAtSignIn = false, KeyId = "test-key", BucketId = "test-bucket",
                BucketName = "test-name", AccountId = "test-account",
                Backups = [new("Documents", Path.Combine(state, "OriginalDocuments"), Path.Combine(root, "Documents"))],
                CustomBackups = [new("Projects", Path.Combine(state, "Projects"), "CloudBay/.cloudbay-backups/Projects/")]
            };
            storage.SaveSettings(original);
            var startup = ReadStartup();
            await using var controller = new ClientController(storage);
            var theme = new PreferenceUpdate { Theme = "Dark" };
            var limits = new PreferenceUpdate { UploadBytesPerSecond = 120_000, DownloadBytesPerSecond = 240_000, UploadConcurrency = 3 };
            var policy = new PreferenceUpdate { PauseOnMetered = false, PauseOnBatterySaver = false };
            var selections = new List<SelectedExclusion> { new(root, "Documents/Private", true) };
            var guided = new List<GuidedExclusion> { new("**/*.tmp", ExclusionTarget.Files, Path.Combine(state, "Projects")) };
            await Task.WhenAll(Task.Run(() => controller.UpdatePreferencesAsync(theme)),
                Task.Run(() => controller.UpdatePreferencesAsync(limits)), Task.Run(() => controller.UpdatePreferencesAsync(policy)),
                Task.Run(() => controller.UpdatePreferencesAsync(new() { SelectedExclusions = selections })),
                Task.Run(() => controller.UpdatePreferencesAsync(new() { GuidedExclusions = guided })),
                Task.Run(() => controller.UpdatePreferencesAsync(new() { DisabledLegacyExclusions = ["~$*"] })));
            var saved = storage.LoadSettings();
            Assert.AreEqual("Dark", saved.Theme);
            Assert.AreEqual(120_000L, saved.UploadBytesPerSecond);
            Assert.AreEqual(240_000L, saved.DownloadBytesPerSecond);
            Assert.AreEqual(3, saved.UploadConcurrency);
            Assert.IsFalse(saved.PauseOnMetered);
            Assert.IsFalse(saved.PauseOnBatterySaver);
            Assert.AreEqual(original.KeyId, saved.KeyId);
            Assert.AreEqual(original.BucketId, saved.BucketId);
            Assert.AreEqual(original.BucketName, saved.BucketName);
            Assert.AreEqual(original.AccountId, saved.AccountId);
            Assert.AreEqual(original.RootPath, saved.RootPath);
            Assert.AreEqual(original.Prefix, saved.Prefix);
            CollectionAssert.AreEqual(original.Backups, saved.Backups);
            CollectionAssert.AreEqual(original.CustomBackups, saved.CustomBackups);
            CollectionAssert.AreEqual(selections, saved.SelectedExclusions);
            CollectionAssert.AreEqual(guided, saved.GuidedExclusions);
            CollectionAssert.AreEqual(new[] { "~$*" }, saved.DisabledLegacyExclusions);
            await using var restarted = new ClientController(new ClientStorage(state), manageStartup: false);
            CollectionAssert.AreEqual(saved.SelectedExclusions, restarted.Settings.SelectedExclusions);
            CollectionAssert.AreEqual(saved.GuidedExclusions, restarted.Settings.GuidedExclusions);
            CollectionAssert.AreEqual(saved.DisabledLegacyExclusions, restarted.Settings.DisabledLegacyExclusions);
            Assert.AreEqual(startup, ReadStartup(), "Theme, transfer, and network preferences must not rewrite the startup entry.");
            Assert.IsFalse(Directory.Exists(root), "Editing preferences must not open a connection or register a root.");
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task PreferencePatchesDoNotChangeCallerCollectionsAndUnchangedValuesDoNotWrite()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            await using var controller = new ClientController(storage);
            var exclusions = new List<string> { "*.tmp" };
            await controller.UpdatePreferencesAsync(new() { Exclusions = exclusions });
            exclusions.Add("*.cache");
            CollectionAssert.AreEqual(new[] { "*.tmp" }, controller.Settings.Exclusions);
            var changes = 0;
            controller.Changed += (_, _) => changes++;
            var settingsPath = Path.Combine(state, "settings.json");
            var before = await File.ReadAllBytesAsync(settingsPath);
            var backup = await File.ReadAllBytesAsync(settingsPath + ".bak");
            var same = await controller.UpdatePreferencesAsync(new() { Theme = controller.Settings.Theme, Exclusions = ["*.tmp"] });
            Assert.AreSame(controller.Settings, same);
            Assert.AreEqual(0, changes);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(settingsPath));
            CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(settingsPath + ".bak"), "A no-op must not rotate durable settings.");
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task WaitingPreferencePatchSnapshotsCallerCollectionAndMergesLatestSavedValues()
    {
        var state = CreateState();
        using var release = new ManualResetEventSlim();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            await using var controller = new ClientController(storage);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changes = 0;
            controller.Changed += (_, _) =>
            {
                if (Interlocked.Increment(ref changes) != 1) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The preference contention test was not released.");
            };
            var first = Task.Run(() => controller.UpdatePreferencesAsync(new() { Theme = "Dark" }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var exclusions = new List<string> { "*.tmp" };
            var disabled = new List<string> { "*.tmp" };
            var selections = new List<SelectedExclusion> { new(controller.Settings.RootPath, "Private", true, Enabled: false) };
            var guided = new List<GuidedExclusion> { new("**/*.cache", ExclusionTarget.Files, controller.Settings.RootPath, "Documents") };
            var waiting = controller.UpdatePreferencesAsync(new() { Exclusions = exclusions, DisabledLegacyExclusions = disabled,
                SelectedExclusions = selections, GuidedExclusions = guided, PauseOnMetered = false });
            Assert.IsFalse(waiting.IsCompleted, "The second patch must wait for the current preference operation.");
            exclusions.Add("*.cache");
            disabled.Clear(); selections.Clear(); guided.Clear();
            release.Set();
            await first;
            var current = await waiting;
            Assert.AreSame(controller.Settings, current);
            Assert.AreEqual("Dark", current.Theme, "A queued patch must preserve the latest completed preference update.");
            Assert.IsFalse(current.PauseOnMetered);
            CollectionAssert.AreEqual(new[] { "*.tmp" }, current.Exclusions,
                "Mutating the caller collection while the patch waits must not change the accepted update.");
            CollectionAssert.AreEqual(new[] { "*.tmp" }, current.DisabledLegacyExclusions);
            Assert.AreEqual(new SelectedExclusion(current.RootPath, "Private", true, Enabled: false), current.SelectedExclusions.Single());
            Assert.AreEqual(new GuidedExclusion("**/*.cache", ExclusionTarget.Files, current.RootPath, "Documents"), current.GuidedExclusions.Single());
            var persisted = storage.LoadSettings();
            Assert.AreEqual(current.Theme, persisted.Theme);
            Assert.AreEqual(current.PauseOnMetered, persisted.PauseOnMetered);
            CollectionAssert.AreEqual(current.Exclusions, persisted.Exclusions);
            CollectionAssert.AreEqual(current.DisabledLegacyExclusions, persisted.DisabledLegacyExclusions);
            CollectionAssert.AreEqual(current.SelectedExclusions, persisted.SelectedExclusions);
            CollectionAssert.AreEqual(current.GuidedExclusions, persisted.GuidedExclusions);
        }
        finally { release.Set(); Directory.Delete(state, true); }
    }

    [DataTestMethod]
    [DataRow("theme")]
    [DataRow("concurrency")]
    [DataRow("limit")]
    [DataRow("poll")]
    [DataRow("exclusions")]
    [DataRow("selected-exclusions")]
    [DataRow("guided-exclusions")]
    [DataRow("disabled-legacy")]
    public async Task InvalidPreferencePatchLeavesSettingsAndStartupUntouched(string scenario)
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var original = await File.ReadAllBytesAsync(Path.Combine(state, "settings.json"));
            var startup = ReadStartup();
            await using var controller = new ClientController(storage);
            var change = scenario switch
            {
                "theme" => new PreferenceUpdate { StartAtSignIn = true, Theme = "Invalid" },
                "concurrency" => new PreferenceUpdate { StartAtSignIn = true, UploadConcurrency = 0 },
                "limit" => new PreferenceUpdate { StartAtSignIn = true, UploadBytesPerSecond = -1 },
                "poll" => new PreferenceUpdate { StartAtSignIn = true, PollSeconds = 1 },
                "exclusions" => new PreferenceUpdate { StartAtSignIn = true, Exclusions = ["../outside"] },
                "selected-exclusions" => new PreferenceUpdate { StartAtSignIn = true, SelectedExclusions = [new(controller.Settings.RootPath, "../outside", false)] },
                "guided-exclusions" => new PreferenceUpdate { StartAtSignIn = true, GuidedExclusions = [new("a/**bad/file.txt", ExclusionTarget.Files)] },
                "disabled-legacy" => new PreferenceUpdate { StartAtSignIn = true, DisabledLegacyExclusions = ["unknown"] },
                _ => throw new AssertFailedException("Unknown preference case.")
            };
            try { await controller.UpdatePreferencesAsync(change); Assert.Fail("Invalid preferences must be rejected."); }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException) { }
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(state, "settings.json")));
            Assert.IsFalse(controller.Settings.StartAtSignIn);
            Assert.AreEqual(startup, ReadStartup());
        }
        finally { Directory.Delete(state, true); }
    }

    [DataTestMethod]
    [DataRow("absent")]
    [DataRow("string")]
    [DataRow("expand")]
    public async Task FailedPreferencePersistenceRestoresExactStartupEntryAndInMemorySettings(string startupScenario)
    {
        var state = CreateState();
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var originalStartupExisted = runKey.GetValueNames().Contains("CloudBay", StringComparer.OrdinalIgnoreCase);
        var originalStartupValue = originalStartupExisted
            ? runKey.GetValue("CloudBay", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var originalStartupKind = originalStartupExisted ? runKey.GetValueKind("CloudBay") : RegistryValueKind.String;
        try
        {
            switch (startupScenario)
            {
                case "absent": runKey.DeleteValue("CloudBay", throwOnMissingValue: false); break;
                case "string": runKey.SetValue("CloudBay", "\"C:\\External Startup\\external.exe\" --external", RegistryValueKind.String); break;
                case "expand": runKey.SetValue("CloudBay", "\"%LOCALAPPDATA%\\External Startup\\external.exe\" --external", RegistryValueKind.ExpandString); break;
                default: Assert.Fail("Unknown startup rollback scenario."); break;
            }
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var settingsPath = Path.Combine(state, "settings.json");
            var original = await File.ReadAllBytesAsync(settingsPath);
            Directory.CreateDirectory(settingsPath + ".bak"); // Prevent atomic rotation after startup was applied.
            var startup = ReadStartup();
            await using var controller = new ClientController(storage);
            var previous = controller.Settings;
            try
            {
                await controller.UpdatePreferencesAsync(new() { StartAtSignIn = true, Theme = "Dark" });
                Assert.Fail("The blocked durable write must fail.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Assert.AreSame(previous, controller.Settings);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(settingsPath));
            Assert.AreEqual(startup, ReadStartup(), "A failed settings write must restore the actual Windows startup value and its registry type.");
        }
        finally
        {
            if (originalStartupExisted) runKey.SetValue("CloudBay", originalStartupValue!, originalStartupKind);
            else runKey.DeleteValue("CloudBay", throwOnMissingValue: false);
            Directory.Delete(state, true);
        }
    }

    [TestMethod]
    public async Task IsolatedPreviewPreferencesPersistWithoutChangingWindowsStartup()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var startup = ReadStartup();
            await using var controller = new ClientController(storage, manageStartup: false);
            var enabled = await controller.UpdatePreferencesAsync(new() { StartAtSignIn = true, Theme = "Dark" });
            Assert.IsTrue(enabled.StartAtSignIn);
            Assert.IsTrue(storage.LoadSettings().StartAtSignIn);
            Assert.AreEqual(startup, ReadStartup(), "An isolated preview must not take over the real sign-in startup entry.");
            await controller.SaveSettingsAsync(controller.Settings with { StartAtSignIn = false });
            Assert.IsFalse(storage.LoadSettings().StartAtSignIn);
            Assert.AreEqual(startup, ReadStartup(), "The full-settings compatibility API must honor isolated-preview startup protection.");
            Assert.IsFalse(Directory.Exists(controller.Settings.RootPath));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task CancelledPreferencePatchDoesNotWriteOrNotify()
    {
        var state = CreateState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var original = await File.ReadAllBytesAsync(Path.Combine(state, "settings.json"));
            await using var controller = new ClientController(storage);
            var changes = 0;
            controller.Changed += (_, _) => changes++;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await controller.UpdatePreferencesAsync(new() { Theme = "Dark" }, cancellation.Token);
                Assert.Fail("A cancelled preference patch must not be accepted.");
            }
            catch (OperationCanceledException) { }
            Assert.AreEqual(0, changes);
            Assert.AreEqual("System", controller.Settings.Theme);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(state, "settings.json")));
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

using System.Security.Cryptography;
using System.Text;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Windows;
using CloudBay.Windows.CloudFiles;
using Microsoft.Win32;
using Windows.Storage.Provider;

internal static class ControllerAcceptance
{
    public static async Task RunAsync(B2CloudStore observer, CloudBucket bucket, B2Credentials credentials,
        string prefix, string id, string temp, Func<string, Func<Task>, Task> check, CancellationToken parentToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var parent = Path.Combine(profile, "CloudBayControllerValidation-" + id);
        var main = Path.Combine(parent, "Main");
        var customA = Path.Combine(parent, "CustomA");
        var customB = Path.Combine(parent, "CustomB");
        var customC = Path.Combine(parent, "CustomC");
        var state = Path.Combine(temp, "controller");
        var storage = new ClientStorage(state);
        storage.SaveSettings(new AppSettings
        {
            RootPath = main, Prefix = prefix + "controller/", StartAtSignIn = false,
            PauseOnBatterySaver = false, PauseOnMetered = false, PollSeconds = 15
        });
        var knownLocations = KnownFolderBackup.FolderIds.Keys.ToDictionary(name => name, SafeKnownLocation, StringComparer.Ordinal);
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var startupExisted = runKey.GetValueNames().Contains("CloudBay", StringComparer.OrdinalIgnoreCase);
        var startupValue = startupExisted ? runKey.GetValue("CloudBay", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var startupKind = startupExisted ? runKey.GetValueKind("CloudBay") : RegistryValueKind.String;
        ClientController? controller = null;
        var failNextUnregister = false;
        const string injectedUnregisterFailure = "Injected validation-only Shell unregistration failure.";
        void Unregister(string registration)
        {
            if (failNextUnregister)
            {
                failNextUnregister = false;
                throw new IOException(injectedUnregisterFailure);
            }
            StorageProviderSyncRootManager.Unregister(registration);
        }
        var registrations = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(main); Directory.CreateDirectory(customA); Directory.CreateDirectory(customB);
            var mainFile = Path.Combine(main, "main.txt");
            var aFile = Path.Combine(customA, "custom.txt");
            var bFile = Path.Combine(customB, "custom-b.txt");
            await File.WriteAllTextAsync(mainFile, "main initial backup", ct);
            await File.WriteAllTextAsync(aFile, "custom initial backup", ct);
            await File.WriteAllTextAsync(bFile, "custom B initial backup", ct);
            Directory.CreateDirectory(Path.Combine(customA, "empty-folder"));
            controller = new ClientController(storage, Unregister);
            await check("controller_connect_settings_vault_and_independent_custom_roots", async () =>
            {
                await controller.ConnectAsync(credentials.KeyId, credentials.ApplicationKey, bucket.Name, main, prefix + "controller/", ct);
                await controller.AddCustomBackupAsync(customA, "CustomA", ct);
                await controller.AddCustomBackupAsync(customB, "CustomB", ct);
                await Sync(controller, ct);
                var saved = storage.LoadSettings();
                Require(saved.IsConfigured && saved.CustomBackups.Count == 2 && !saved.StartAtSignIn, "Connected multi-root settings did not persist.");
                var encrypted = Path.Combine(state, "credentials.dpapi");
                Require(File.Exists(encrypted) && storage.LoadCredentials()?.KeyId == credentials.KeyId, "The isolated DPAPI credential vault did not round trip.");
                Require(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(encrypted, ct)).Contains(credentials.ApplicationKey, StringComparison.Ordinal), "The credential vault contains plaintext key material.");
                Require((await controller.GetVersionsAsync("main.txt")).Any(f => f.Action == "upload"), "Main-root file was not uploaded.");
                Require((await controller.GetVersionsAsync("custom.txt", "CustomA")).Any(f => f.Action == "upload"), "Independent custom-root file was not uploaded.");
                Require(!Directory.Exists(Path.Combine(main, ".cloudbay-backups")), "Custom backups were materialized inside the primary folder.");
                var owned = CurrentOwnedRoots(parent).ToArray();
                Require(owned.Length == 3, "Windows did not expose three independently registered native roots.");
                foreach (var root in owned) registrations.Add(root.Id);
            });
            await check("controller_local_edits_keep_b2_versions_in_each_root", async () =>
            {
                await File.WriteAllTextAsync(mainFile, "main edited backup", ct);
                await File.WriteAllTextAsync(aFile, "custom edited backup", ct);
                await Sync(controller, ct);
                Require((await controller.GetVersionsAsync("main.txt")).Count(f => f.Action == "upload") >= 2, "A main-root edit did not retain version history.");
                Require((await controller.GetVersionsAsync("custom.txt", "CustomA")).Count(f => f.Action == "upload") >= 2, "A custom-root edit did not retain version history.");
            });
            await check("controller_atomic_preferences_keep_native_roots_account_and_global_pause", async () =>
            {
                controller.Pause();
                await controller.SyncNowAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
                var previous = controller.Settings;
                var ownedBefore = CurrentOwnedRoots(parent).Select(root => root.Id).Order(StringComparer.Ordinal).ToArray();
                var connectingEvents = 0;
                EventHandler changed = (_, _) =>
                {
                    if (controller.Snapshot.State == ClientState.Connecting) Interlocked.Increment(ref connectingEvents);
                };
                controller.Changed += changed;
                try
                {
                    await Task.WhenAll(
                        controller.UpdatePreferencesAsync(new() { Theme = "Dark" }, ct),
                        controller.UpdatePreferencesAsync(new() { UploadConcurrency = 2, UploadBytesPerSecond = 1_000_000, DownloadBytesPerSecond = 2_000_000 }, ct),
                        controller.UpdatePreferencesAsync(new() { FilesOnDemand = false, PollSeconds = 30, Exclusions = ["~$*", "*.ignore"] }, ct));
                    var saved = storage.LoadSettings();
                    Require(saved.Theme == "Dark" && saved.UploadConcurrency == 2 && saved.UploadBytesPerSecond == 1_000_000 &&
                        saved.DownloadBytesPerSecond == 2_000_000 && !saved.FilesOnDemand && saved.PollSeconds == 30 &&
                        saved.Exclusions.SequenceEqual(new[] { "~$*", "*.ignore" }), "Concurrent preference patches lost a saved value.");
                    Require(saved.AccountId == previous.AccountId && saved.KeyId == previous.KeyId && saved.BucketId == previous.BucketId &&
                        saved.BucketName == previous.BucketName && saved.RootPath == previous.RootPath && saved.Prefix == previous.Prefix &&
                        saved.Backups.SequenceEqual(previous.Backups) && saved.CustomBackups.SequenceEqual(previous.CustomBackups),
                        "Updating preferences changed account identity or backup ownership.");
                    Require(CurrentOwnedRoots(parent).Select(root => root.Id).Order(StringComparer.Ordinal).SequenceEqual(ownedBefore) &&
                        connectingEvents == 0, "Updating preferences reconnected or replaced a registered native root.");
                    await controller.SyncNowAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
                    Require(controller.Snapshot.State == ClientState.Paused, "Updating preferences cleared the global pause.");
                }
                finally
                {
                    controller.Changed -= changed;
                    await controller.UpdatePreferencesAsync(new()
                    {
                        Theme = previous.Theme, UploadConcurrency = previous.UploadConcurrency,
                        UploadBytesPerSecond = previous.UploadBytesPerSecond, DownloadBytesPerSecond = previous.DownloadBytesPerSecond,
                        FilesOnDemand = previous.FilesOnDemand, PollSeconds = previous.PollSeconds, Exclusions = previous.Exclusions
                    }, ct);
                    controller.Resume();
                }
                await Sync(controller, ct);
            });
            await check("controller_same_size_preserved_timestamp_edit_is_uploaded", async () =>
            {
                var versions = await controller.GetVersionsAsync("custom.txt", "CustomA");
                var originalWrite = File.GetLastWriteTimeUtc(aFile);
                var changed = Encoding.UTF8.GetBytes(new string('x', checked((int)new FileInfo(aFile).Length)));
                await File.WriteAllBytesAsync(aFile, changed, ct);
                File.SetLastWriteTimeUtc(aFile, originalWrite);
                await Sync(controller, ct);
                var current = await controller.GetVersionsAsync("custom.txt", "CustomA");
                Require(current.Count(file => file.Action == "upload") > versions.Count(file => file.Action == "upload"),
                    "A same-size edit with its timestamp preserved was missed by backup.");
                Require(current.First(file => file.Action == "upload").Sha1 == Convert.ToHexString(SHA1.HashData(changed)).ToLowerInvariant(),
                    "The native dirty edit did not upload its changed bytes.");
            });
            await check("controller_files_on_demand_pin_free_space_and_hydrate", async () =>
            {
                var data = RandomNumberGenerator.GetBytes(128 * 1024 + 29);
                await Upload(observer, bucket.Id, controller.Settings.Prefix + "online.bin", data, ct);
                await Upload(observer, bucket.Id, controller.Settings.CustomBackups.Single(b => b.Name == "CustomA").Prefix + "online.bin", data, ct);
                await Upload(observer, bucket.Id, controller.Settings.CustomBackups.Single(b => b.Name == "CustomB").Prefix + "online.bin", data, ct);
                await Sync(controller, ct);
                await using var probe = new WindowsPlaceholderService();
                var mainOnline = Path.Combine(main, "online.bin");
                var customOnline = Path.Combine(customA, "online.bin");
                Require(probe.IsPlaceholder(mainOnline) && probe.IsPlaceholder(customOnline), "Remote files did not appear as native Files On-Demand placeholders.");
                await controller.SetPinAsync("online.bin", PinMode.AlwaysAvailable);
                await controller.SetPinAsync("online.bin", PinMode.AlwaysAvailable, "CustomA");
                Require(probe.IsHydrated(mainOnline) && probe.IsHydrated(customOnline), "Pinning did not hydrate both roots.");
                await controller.FreeSpaceAsync("online.bin");
                await controller.FreeSpaceAsync("online.bin", "CustomA");
                Require(!probe.IsHydrated(mainOnline) && !probe.IsHydrated(customOnline), "Free-space did not evict clean cached data.");
                var bytes = await Task.Run(() => File.ReadAllBytes(customOnline), ct).WaitAsync(TimeSpan.FromMinutes(2), ct);
                Require(bytes.SequenceEqual(data), "An ordinary custom-folder file read did not hydrate the correct B2 bytes.");
                await controller.FreeSpaceAsync("online.bin", "CustomA");
                // Leave online-only files in all roots so removal/disconnection must hydrate before reverting.
            });
            await check("controller_native_activity_waits_for_assembled_cache_validation", async () =>
            {
                const string name = "native-validation.bin";
                var data = RandomNumberGenerator.GetBytes(256 * 1024 + 37);
                await Upload(observer, bucket.Id, controller.Settings.Prefix + name, data, ct);
                await Sync(controller, ct);
                await controller.FreeSpaceAsync(name);
                // Earlier sync/free-space work can legitimately read the file and leave
                // completed history. Isolate the next read instead of rejecting its past.
                var idleDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (controller.Snapshot.Transfers.Any(item => item.RelativePath == name) &&
                    DateTimeOffset.UtcNow < idleDeadline)
                    await Task.Delay(50, ct);
                Require(!controller.Snapshot.Transfers.Any(item => item.RelativePath == name),
                    "Earlier native work did not finish before the validation-order check.");
                int CompletedDownloads() => controller.Activity.Count(item =>
                    item.Kind == ActivityKind.Download && item.Path == "CloudBay/" + name);
                var completedBeforeRead = CompletedDownloads();
                await TransferResources.NativeValidation.WaitAsync(ct);
                Task<byte[]> read;
                try
                {
                    read = Task.Run(() => File.ReadAllBytes(Path.Combine(main, name)), ct);
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                    while (!controller.Snapshot.Transfers.Any(item => item.RelativePath == name && item.Phase == TransferPhase.Verifying) &&
                        DateTimeOffset.UtcNow < deadline)
                        await Task.Delay(50, ct);
                    var row = controller.Snapshot.Transfers.SingleOrDefault(item => item.RelativePath == name && item.Phase == TransferPhase.Verifying);
                    Require(row is not null && row.BytesPerSecond == 0, "The live native file must stay visible as verifying without an old wire rate.");
                    Require(!read.IsCompleted, "Native user I/O must wait until assembled cache validation is acknowledged.");
                    Require(CompletedDownloads() == completedBeforeRead,
                        "Completed history must not precede native cache verification.");
                }
                finally { TransferResources.NativeValidation.Release(); }
                Require((await read.WaitAsync(TimeSpan.FromMinutes(2), ct)).SequenceEqual(data), "Validated native bytes differ from their immutable cloud source.");
                var historyDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (CompletedDownloads() == completedBeforeRead &&
                    DateTimeOffset.UtcNow < historyDeadline)
                    await Task.Delay(50, ct);
                Require(CompletedDownloads() > completedBeforeRead,
                    "Completed native history must appear after validation succeeds.");
                await controller.FreeSpaceAsync(name);
            });
            await check("controller_pause_resume_covers_main_and_custom_backups", async () =>
            {
                controller.Pause();
                await controller.SyncNowAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
                var mainBefore = (await controller.GetVersionsAsync("main.txt")).Count;
                var customBefore = (await controller.GetVersionsAsync("custom.txt", "CustomA")).Count;
                await File.WriteAllTextAsync(mainFile, "main change while paused", ct);
                await File.WriteAllTextAsync(aFile, "custom change while paused", ct);
                Directory.CreateDirectory(customC);
                await File.WriteAllTextAsync(Path.Combine(customC, "paused-added.txt"), "new backup added during global pause", ct);
                await controller.AddCustomBackupAsync(customC, "CustomC", ct);
                await controller.SyncNowAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
                Require((await controller.GetVersionsAsync("main.txt")).Count == mainBefore &&
                    (await controller.GetVersionsAsync("custom.txt", "CustomA")).Count == customBefore, "Global pause still uploaded a root's changes.");
                Require((await controller.GetVersionsAsync("paused-added.txt", "CustomC")).Count == 0,
                    "A custom backup added during global pause uploaded before resume.");
                Require(controller.Snapshot.State == ClientState.Paused, "Aggregate state did not expose a global pause.");
                controller.Resume(); await Sync(controller, ct);
                Require((await controller.GetVersionsAsync("main.txt")).Count > mainBefore &&
                    (await controller.GetVersionsAsync("custom.txt", "CustomA")).Count > customBefore, "Global resume did not upload pending changes in both roots.");
                Require((await controller.GetVersionsAsync("paused-added.txt", "CustomC")).Any(file => file.Action == "upload"),
                    "The custom backup added during pause did not upload after global resume.");
                await controller.RemoveCustomBackupAsync("CustomC", ct);
                Require(controller.Settings.CustomBackups.Count == 2, "The temporary pause-test backup did not stop cleanly.");
            });
            await check("controller_restart_reconnects_saved_native_roots_and_baselines", async () =>
            {
                var before = (await controller.GetVersionsAsync("custom.txt", "CustomA")).Count;
                await controller.DisposeAsync(); controller = new ClientController(new ClientStorage(state), Unregister);
                await controller.StartAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
                await Sync(controller, ct);
                Require(controller.Settings.CustomBackups.Count == 2 && CurrentOwnedRoots(parent).Count() == 3, "Restart did not reconnect all persisted native roots.");
                Require((await controller.GetVersionsAsync("custom.txt", "CustomA")).Count == before, "Restart uploaded an unchanged file because its durable baseline was lost.");
                await File.WriteAllTextAsync(aFile, "custom edit after restart", ct); await Sync(controller, ct);
                Require((await controller.GetVersionsAsync("custom.txt", "CustomA")).Count > before, "A custom root did not sync after process restart.");
            });
            await check("controller_failed_custom_shell_unregistration_retains_settings_and_can_retry", async () =>
            {
                await controller.FreeSpaceAsync("online.bin", "CustomA");
                failNextUnregister = true;
                try
                {
                    await controller.RemoveCustomBackupAsync("CustomA", ct);
                    throw new InvalidOperationException("The injected custom Shell unregistration failure was not exercised.");
                }
                catch (IOException error) when (error.Message == injectedUnregisterFailure) { }
                Require(!failNextUnregister && controller.Settings.CustomBackups.Count == 2 && storage.LoadSettings().CustomBackups.Count == 2,
                    "A failed custom Shell unregistration lost its persisted retry state.");
                Require(controller.Snapshot.State == ClientState.Attention, "A failed custom Shell unregistration did not expose an actionable error.");
                Require(CurrentOwnedRoots(parent).Count() == 3 && File.Exists(aFile) && File.Exists(Path.Combine(customA, "online.bin")),
                    "A failed custom Shell unregistration lost its registration or local files.");
            });
            await check("controller_remove_custom_backup_hydrates_reverts_and_preserves", async () =>
            {
                var prefixA = controller.Settings.CustomBackups.Single(b => b.Name == "CustomA").Prefix;
                await controller.RemoveCustomBackupAsync("CustomA", ct);
                Require(controller.Settings.CustomBackups.Count == 1 && storage.LoadSettings().CustomBackups.Count == 1, "Removing a custom backup did not persist its new state.");
                await using var probe = new WindowsPlaceholderService();
                Require(File.Exists(aFile) && File.Exists(Path.Combine(customA, "online.bin")) &&
                    !probe.IsPlaceholder(aFile) && !probe.IsPlaceholder(Path.Combine(customA, "online.bin")), "Stopping a custom backup lost files or left native placeholder references.");
                Require(CurrentOwnedRoots(parent).Count() == 2, "The removed custom root stayed registered in Explorer.");
                Require((await observer.VersionsAsync(bucket.Id, prefixA + "custom.txt", ct)).Any(f => f.Action == "upload"), "Removing a custom backup removed its retained B2 data.");
                Require(Directory.Exists(Path.Combine(customA, "empty-folder")), "Removing a custom backup lost an empty directory.");
            });
            await check("controller_failed_disconnect_shell_unregistration_retains_vault_and_can_retry", async () =>
            {
                await controller.FreeSpaceAsync("online.bin"); await controller.FreeSpaceAsync("online.bin", "CustomB");
                failNextUnregister = true;
                try
                {
                    await controller.DisconnectAsync(ct);
                    throw new InvalidOperationException("The injected disconnect Shell unregistration failure was not exercised.");
                }
                catch (IOException error) when (error.Message == injectedUnregisterFailure) { }
                Require(!failNextUnregister && controller.Settings.IsConfigured && storage.LoadSettings().IsConfigured &&
                    File.Exists(Path.Combine(state, "credentials.dpapi")), "A failed disconnect cleared the account/vault before Shell unregistration could be retried.");
                Require(controller.Snapshot.State == ClientState.Attention && CurrentOwnedRoots(parent).Count() == 2,
                    "A failed disconnect did not retain its native registration and actionable error state.");
                Require(File.Exists(mainFile) && File.Exists(bFile), "A failed disconnect lost local files.");
            });
            await check("controller_disconnect_reverts_all_roots_clears_vault_and_retains_b2", async () =>
            {
                var prefixMain = controller.Settings.Prefix;
                var prefixB = controller.Settings.CustomBackups.Single(b => b.Name == "CustomB").Prefix;
                await controller.DisconnectAsync(ct);
                Require(!controller.Settings.IsConfigured && controller.Settings.CustomBackups.Count == 0 && controller.Snapshot.State == ClientState.NotConnected,
                    "Full disconnect did not clear the connected multi-root state.");
                Require(!File.Exists(Path.Combine(state, "credentials.dpapi")) && !File.Exists(Path.Combine(state, "credentials.dpapi.bak")), "Full disconnect left an encrypted credential vault or backup.");
                Require(!storage.LoadSettings().IsConfigured, "Full disconnect did not persist the disconnected state.");
                Require(!CurrentOwnedRoots(parent).Any(), "Full disconnect left an Explorer root registered.");
                await using var probe = new WindowsPlaceholderService();
                foreach (var root in new[] { main, customA, customB })
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    Require(!probe.IsPlaceholder(file), "Full disconnect left a native file placeholder.");
                Require(File.Exists(mainFile) && File.Exists(bFile) && new FileInfo(Path.Combine(main, "online.bin")).Length == 128 * 1024 + 29,
                    "Full disconnect did not preserve downloaded local files.");
                Require((await observer.VersionsAsync(bucket.Id, prefixMain + "main.txt", ct)).Any(f => f.Action == "upload") &&
                    (await observer.VersionsAsync(bucket.Id, prefixB + "custom-b.txt", ct)).Any(f => f.Action == "upload"), "Full disconnect removed B2 copies.");
            });
            await check("controller_known_folder_mappings_remain_untouched", () =>
            {
                foreach (var name in KnownFolderBackup.FolderIds.Keys)
                {
                    _ = KnownFolderBackup.GetRestriction(name);
                    Require(SafeKnownLocation(name) == knownLocations[name], "The validation changed a real Windows known-folder mapping.");
                }
                return Task.CompletedTask;
            });
        }
        finally
        {
            try
            {
            if (controller is not null)
            {
                try
                {
                    if (controller.Settings.IsConfigured)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await controller.DisconnectAsync(cleanup.Token);
                    }
                }
                finally { await controller.DisposeAsync(); }
            }
            foreach (var root in CurrentOwnedRoots(parent)) registrations.Add(root.Id);
            foreach (var registration in registrations)
            {
                var root = StorageProviderSyncRootManager.GetCurrentSyncRoots().SingleOrDefault(r => r.Id == registration);
                if (root is not null && Owned(root.Path.Path, parent)) StorageProviderSyncRootManager.Unregister(registration);
            }
            Require(Path.GetDirectoryName(Path.GetFullPath(parent)) == profile && Path.GetFileName(parent) == "CloudBayControllerValidation-" + id,
                "Unsafe controller validation cleanup path.");
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            Require(KnownFolderBackup.FolderIds.Keys.All(name => SafeKnownLocation(name) == knownLocations[name]), "Known-folder mappings changed during cleanup.");
            }
            finally
            {
                // Restore even when hydration/unregistration fails. Keep the exact value type and
                // unexpanded environment strings rather than normalizing the user's startup entry.
                await check("controller_startup_registration_restored_exactly", () =>
                {
                    if (startupExisted) runKey.SetValue("CloudBay", startupValue!, startupKind);
                    else runKey.DeleteValue("CloudBay", throwOnMissingValue: false);
                    Require(runKey.GetValueNames().Contains("CloudBay", StringComparer.OrdinalIgnoreCase) == startupExisted,
                        "Startup entry presence was not restored.");
                    if (startupExisted)
                        Require(runKey.GetValueKind("CloudBay") == startupKind &&
                            System.Text.Json.JsonSerializer.Serialize(runKey.GetValue("CloudBay", null, RegistryValueOptions.DoNotExpandEnvironmentNames)) ==
                            System.Text.Json.JsonSerializer.Serialize(startupValue), "Startup entry value and type were not restored.");
                    return Task.CompletedTask;
                });
            }
        }
    }

    private static async Task Sync(ClientController controller, CancellationToken ct)
    {
        await controller.SyncNowAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
        // A watcher wake can begin a follow-up scan as the manual cycle releases its gate.
        // Wait for that scan to settle instead of treating a transient "Checking" state as failure.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (controller.Snapshot.State is ClientState.Syncing or ClientState.Connecting && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100, ct);
        if (controller.Snapshot.State is ClientState.Attention or ClientState.Offline)
            throw new InvalidOperationException("The controller did not sync: " + controller.Snapshot.Message);
        Require(controller.Snapshot.State == ClientState.UpToDate, "The controller did not finish all roots: " + controller.Snapshot.Message);
    }
    private static async Task Upload(B2CloudStore store, string bucket, string key, byte[] bytes, CancellationToken ct)
    {
        using var source = new MemoryStream(bytes);
        await store.UploadAsync(bucket, key, source, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow, cancellationToken: ct);
    }
    private static bool Owned(string path, string parent) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<StorageProviderSyncRootInfo> CurrentOwnedRoots(string parent) =>
        StorageProviderSyncRootManager.GetCurrentSyncRoots().Where(root => Owned(root.Path.Path, parent));
    private static string SafeKnownLocation(string name)
    {
        try { return KnownFolderBackup.GetPath(name); } catch { return "unavailable"; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

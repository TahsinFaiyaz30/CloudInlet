using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using CloudInlet.Core.Sync;
using CloudInlet.Windows.CloudFiles;
using Microsoft.Win32;
using Windows.Storage.Provider;

namespace CloudInlet.Windows;

/// <summary>Opt-in, isolated native acceptance. Never constructs the normal client or opens its state.</summary>
internal static class StoreParityValidation
{
    private static readonly string[] MappingKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
        @"Software\CloudInlet.StoreParityControl"
    ];

    public static async Task<int> RunAsync(string[] arguments)
    {
        var token = Value(arguments, "--parity-token=");
        if (!Guid.TryParseExact(token, "N", out _)) return 64;
        var phase = Value(arguments, "--parity-phase=");
        if (phase is not ("seed" or "resume" or "disconnect" or "verify" or "cleanup")) return 64;
        var output = Value(arguments, "--validation-output=");
        if (string.IsNullOrWhiteSpace(output)) return 64;
        output = Path.GetFullPath(output);
        RejectLinks(output);
        Directory.CreateDirectory(output);
        var result = new Dictionary<string, object?> { ["phase"] = phase, ["token"] = token,
            ["processId"] = Environment.ProcessId, ["version"] = BuildInfo.Version };
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudInlet-StoreParity-" + token);
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Client", "StoreValidation", token);
        var payload = Encoding.UTF8.GetBytes("CloudInlet isolated native parity fixture: " + token + "\n");
        var placeholder = Path.Combine(root, "online-only.bin");
        var downloaded = Path.Combine(root, "downloaded.bin");
        var pinned = Path.Combine(root, "pinned.bin");
        var fixtureFiles = new[] { placeholder, downloaded, pinned };
        var local = Path.Combine(root, "local.txt");
        var marker = Path.Combine(root, "fixture-owner.txt");
        var account = "store-parity-" + token;
        result["statePath"] = state;
        result["rootPath"] = root;
        result["packaged"] = UpdateInstallation.IsPackaged;
        if (UpdateInstallation.IsPackaged)
        {
            var package = global::Windows.ApplicationModel.Package.Current;
            result["packageName"] = package.Id.Name;
            result["packageFamilyName"] = package.Id.FamilyName;
            // The test must never run its lifecycle against the real Store identity.
            if (package.Id.Name != "CloudInlet.LocalValidation") return 64;
        }
        try
        {
            RejectLinks(state);
            // A native Cloud Files root can be a reparse point; its parent may not be a link.
            RejectLinks(Path.GetDirectoryName(root)!);
            if (phase == "cleanup")
            {
                CleanupFixture(root, marker, token);
                foreach (var key in MappingKeys)
                    Registry.CurrentUser.DeleteSubKeyTree(key + @"\CloudInletStoreParity-" + token, throwOnMissingSubKey: false);
                result["fixtureRemoved"] = !Directory.Exists(root);
            }
            else
            {
                if (phase == "seed")
                {
                    if (Directory.Exists(root) || Directory.Exists(state))
                        throw new IOException("A parity fixture already exists; use a new token.");
                    Directory.CreateDirectory(root);
                    await File.WriteAllTextAsync(marker, token);
                    await File.WriteAllTextAsync(local, "Preserved local fixture " + token);
                    SeedState(state, token, root);
                    foreach (var key in MappingKeys)
                    {
                        using var testKey = Registry.CurrentUser.CreateSubKey(key + @"\CloudInletStoreParity-" + token, writable: true);
                        testKey.SetValue("Token", token, RegistryValueKind.String);
                    }
                }
                else if (!File.Exists(marker) || await File.ReadAllTextAsync(marker) != token)
                    throw new IOException("The native fixture ownership marker is missing or different.");

                ValidateState(state, token, root, result);
                if (phase is "resume" or "disconnect" or "verify")
                {
                    // Check before reconnecting or listing the fake cloud: recovery must not
                    // conceal a package uninstall that discarded already downloaded bytes.
                    foreach (var path in new[] { downloaded, pinned })
                        Require(File.Exists(path) && (await File.ReadAllBytesAsync(path)).SequenceEqual(payload),
                            "The downloaded fixture was not preserved before reconnect: " + Path.GetFileName(path));
                    result["downloadedBytesPreservedBeforeReconnect"] = true;
                    result["pinnedBytesPreservedBeforeReconnect"] = true;
                }
                if (phase is "seed" or "resume" or "disconnect")
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    await using var service = new WindowsPlaceholderService();
                    var syncDatabase = Path.Combine(state, "sync.sqlite");
                    var fixtureCloudMetadata = Path.Combine(state, "fixture-cloud.json");
                    if (phase != "seed") Require(File.Exists(syncDatabase), "The native sync baseline disappeared.");
                    if (phase != "seed") Require(File.Exists(fixtureCloudMetadata), "The fake cloud metadata disappeared.");
                    var manifest = new SyncManifest(syncDatabase);
                    await service.ConnectAsync(root, account, (_, offset, length, destination, cancellation) =>
                        destination.WriteAsync(payload.AsMemory(checked((int)offset), checked((int)length)), cancellation).AsTask(), timeout.Token,
                        manifest.PrepareForNewRegistration);
                    result["registrationId"] = service.RegistrationId;
                    result["registrationVisible"] = StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item =>
                        item.Id == service.RegistrationId && item.Path.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
                    using var cloud = new FixtureCloud(payload, fixtureCloudMetadata);
                    SyncSnapshot? snapshot = null;
                    await using (var engine = new SyncEngine(cloud, service, manifest,
                        new AppSettings { RootPath = root, BucketId = "fixture", KeyId = "fixture", Prefix = "parity/",
                            Exclusions = ["fixture-owner.txt", "local.txt"] }, Path.Combine(state, "Recovery"), _ => { }, value => snapshot = value))
                    {
                        await engine.SyncNowAsync(timeout.Token);
                    }
                    Require(snapshot?.State == ClientState.UpToDate, "Native reconciliation failed: " + snapshot?.Message);
                    Require(cloud.Hidden == 0, "Package removal was incorrectly propagated as cloud deletion.");
                    result["cloudDeletions"] = cloud.Hidden;
                    result["verifiedFixtureUploads"] = cloud.Uploads;
                    result["nativeBaselineRecovered"] = true;
                    if (phase == "seed")
                    {
                        await service.HydrateAsync(downloaded, timeout.Token);
                        await service.SetPinAsync(pinned, PinMode.AlwaysAvailable, timeout.Token);
                        foreach (var path in new[] { downloaded, pinned })
                            Require(service.IsPlaceholder(path) && service.IsHydrated(path) &&
                                (await File.ReadAllBytesAsync(path, timeout.Token)).SequenceEqual(payload),
                                "The downloaded fixture did not hydrate correctly: " + Path.GetFileName(path));
                        Require((File.GetAttributes(pinned) & (FileAttributes)0x80000) != 0, "The pinned fixture has no Windows pin flag.");
                        result["downloadedPlaceholderSeeded"] = true;
                        result["pinnedPlaceholderSeeded"] = true;
                        result["placeholder"] = service.IsPlaceholder(placeholder);
                        result["onlineOnly"] = !service.IsHydrated(placeholder);
                        Require((bool)result["placeholder"]! && (bool)result["onlineOnly"]!, "The seed must remain online-only for direct package removal.");
                    }
                    else
                    {
                        await service.HydrateAsync(placeholder, timeout.Token);
                        Require((await File.ReadAllBytesAsync(placeholder, timeout.Token)).SequenceEqual(payload), "Native hydration changed fixture bytes.");
                        result["hydrationRoundTrip"] = true;
                        if (phase == "disconnect")
                        {
                            await service.PrepareForUnregisterAsync(timeout.Token);
                            foreach (var path in fixtureFiles)
                                Require(!service.IsPlaceholder(path) &&
                                    (await File.ReadAllBytesAsync(path, timeout.Token)).SequenceEqual(payload),
                                    "Disconnect did not preserve an ordinary local file: " + Path.GetFileName(path));
                            await service.DisconnectAsync();
                            StorageProviderSyncRootManager.Unregister(service.RegistrationId!);
                            result["registrationRemoved"] = !StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item => item.Id == service.RegistrationId);
                            result["ordinaryFilePreserved"] = (await File.ReadAllBytesAsync(placeholder, timeout.Token)).SequenceEqual(payload);
                            result["downloadedAndPinnedFilesConverted"] = true;
                        }
                    }
                    await service.DisconnectAsync();
                }
                if (phase == "verify")
                {
                    foreach (var path in fixtureFiles)
                        Require((await File.ReadAllBytesAsync(path)).SequenceEqual(payload),
                            "The ordinary file was not preserved: " + Path.GetFileName(path));
                    result["ordinaryFilePreserved"] = true;
                    result["allResidencyFixturesPreserved"] = true;
                }
                Require(await File.ReadAllTextAsync(local) == "Preserved local fixture " + token, "The user's local fixture changed.");
                result["localFilePreserved"] = true;
            }
            result["passed"] = true;
            await WriteResultAsync(output, phase, result);
            return 0;
        }
        catch (Exception error)
        {
            result["passed"] = false;
            result["error"] = error.ToString();
            await WriteResultAsync(output, phase, result);
            return 1;
        }
    }

    private static void SeedState(string path, string token, string root)
    {
        var storage = new ClientStorage(path);
        storage.SaveSettings(new() { AccountId = token, KeyId = "parity-fake-key", BucketId = "parity-fake-bucket", RootPath = root, StartAtSignIn = false });
        storage.SaveCredentials(new("parity-fake-key", "not-a-real-secret-" + token));
        storage.SaveOneDriveConnections([new(token, "Isolated fixture", "fake-client", "consumers",
            new OneDriveTokenSet("fake-access-" + token, "fake-refresh-" + token, DateTimeOffset.MaxValue, "fixture"))]);
        storage.SaveBackupIntent(new("Synthetic", Path.Combine(root, "original"), Path.Combine(root, "destination")), true);
        storage.SavePolicyPausedCloudTransfers([token]);
        var plan = new TransferJobPlan(token, new("b2", token, "source", "", "source/", "Fake source"),
            new("onedrive", token, "destination", "fixture-destination", "destination/", "Fake destination"),
            TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
        using var journal = new TransferJobJournal(Path.Combine(path, "transfers.db"), new FixtureCheckpointProtector());
        journal.Create(plan);
        storage.SaveCloudBackupStopIntent(new("Synthetic", Path.Combine(root, "restored"), plan));
        File.WriteAllBytes(Path.Combine(path, "checkpoint.dpapi"), new FixtureCheckpointProtector().Protect(Encoding.UTF8.GetBytes(token)));
    }

    private static void ValidateState(string path, string token, string root, Dictionary<string, object?> result)
    {
        // Do not create a replacement database/settings directory if virtualization lost them.
        Require(File.Exists(Path.Combine(path, "settings.json")), "Saved settings are missing after a process/package restart.");
        Require(File.Exists(Path.Combine(path, "transfers.db")), "The recovery journal is missing after a process/package restart.");
        var storage = new ClientStorage(path);
        var settings = storage.LoadSettings();
        Require(settings.AccountId == token && settings.RootPath == root && !settings.StartAtSignIn, "Saved settings differ.");
        Require(storage.LoadCredentials() == new B2Credentials("parity-fake-key", "not-a-real-secret-" + token), "B2 DPAPI reopening failed.");
        var oneDrive = storage.LoadOneDriveConnections().Single();
        Require(oneDrive.Id == token && oneDrive.Tokens.RefreshToken == "fake-refresh-" + token, "OneDrive DPAPI reopening failed.");
        Require(storage.LoadBackupIntent()?.Folder.Name == "Synthetic" && storage.LoadCloudBackupStopIntent()?.Plan.Id == token &&
            storage.LoadPolicyPausedCloudTransfers().SequenceEqual([token]), "Recovery metadata differs.");
        using var journal = new TransferJobJournal(Path.Combine(path, "transfers.db"), new FixtureCheckpointProtector());
        Require(journal.Snapshot(token).Plan.Id == token, "The durable transfer plan could not be reopened.");
        Require(Encoding.UTF8.GetString(new FixtureCheckpointProtector().Unprotect(File.ReadAllBytes(Path.Combine(path, "checkpoint.dpapi")))) == token,
            "Checkpoint DPAPI reopening failed.");
        result["settingsReopened"] = true;
        result["credentialDpapiReopened"] = true;
        result["checkpointDpapiReopened"] = true;
        result["recoveryMetadataReopened"] = true;
        result["transferJournalReopened"] = true;
    }

    private static void CleanupFixture(string root, string marker, string token)
    {
        if (!Directory.Exists(root)) return;
        Require(File.Exists(marker) && File.ReadAllText(marker) == token, "Refusing to clean a fixture without the exact ownership marker.");
        foreach (var registration in StorageProviderSyncRootManager.GetCurrentSyncRoots().Where(item =>
            item.Path.Path.Equals(root, StringComparison.OrdinalIgnoreCase)))
        {
            Require(registration.Id.StartsWith("CloudBay", StringComparison.Ordinal), "Unexpected provider owns the fixture root.");
            StorageProviderSyncRootManager.Unregister(registration.Id);
        }
        // Every payload here is generated by this probe. No recursive deletion or cloud reads.
        foreach (var name in new[] { "online-only.bin", "downloaded.bin", "pinned.bin", "local.txt", "fixture-owner.txt" })
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file)) File.Delete(file);
        }
        Directory.Delete(root, recursive: false);
    }

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Parity state and output paths cannot contain links.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static string Value(string[] arguments, string prefix) => arguments.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? "";
    private static Task WriteResultAsync(string output, string phase, Dictionary<string, object?> result) =>
        File.WriteAllTextAsync(Path.Combine(output, phase + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    private sealed class FixtureCheckpointProtector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, "CloudBay.TransferCheckpoint.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, "CloudBay.TransferCheckpoint.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    }

    private sealed class FixtureCloud : ICloudStore
    {
        private readonly byte[] _payload;
        private readonly string _metadataPath;
        private readonly Dictionary<string, CloudObject> _files;
        private readonly object _gate = new();
        public int Hidden { get; private set; }
        public int Uploads { get; private set; }

        public FixtureCloud(byte[] payload, string metadataPath)
        {
            _payload = payload;
            _metadataPath = metadataPath;
            var files = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<CloudObject[]>(File.ReadAllText(metadataPath)) ?? throw new InvalidDataException("Invalid fake cloud metadata.")
                : new[] { "online-only.bin", "downloaded.bin", "pinned.bin" }.Select(name =>
                    new CloudObject("parity-version-" + name, "parity/" + name, payload.Length,
                        Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant(), DateTimeOffset.Parse("2026-01-01T00:00:00Z"))).ToArray();
            _files = files.ToDictionary(file => file.Key, StringComparer.Ordinal);
            Require(_files.Count == 3 && new[] { "online-only.bin", "downloaded.bin", "pinned.bin" }.All(name =>
                _files.TryGetValue("parity/" + name, out var file) && file.Size == payload.Length &&
                file.Sha1 == Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant()), "The fake cloud fixtures differ.");
            SaveMetadata();
        }

        private void SaveMetadata()
        {
            // Only cloud-object metadata is durable; every test payload stays in bounded RAM.
            var temporary = _metadataPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_files.Values.ToArray()));
            File.Move(temporary, _metadataPath, overwrite: true);
        }

        public async IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            CloudObject[] files;
            lock (_gate) files = _files.Values.ToArray();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
        }
        public Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            destination.WriteAsync(_payload.AsMemory((int)offset, (int)(length ?? _payload.Length - offset)), cancellationToken).AsTask();
        public Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default)
        { Hidden++; throw new InvalidOperationException("A parity fixture must never send a cloud deletion."); }
        public async Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1,
            DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            lock (_gate) Require(_files.ContainsKey(key), "The native fixture tried to upload an unexpected local file.");
            Require(length == _payload.Length && sha1.Equals(Convert.ToHexString(SHA1.HashData(_payload)), StringComparison.OrdinalIgnoreCase),
                "An upload changed the fixture length or checksum.");
            var uploaded = new byte[_payload.Length];
            await source.ReadExactlyAsync(uploaded, cancellationToken);
            Require(uploaded.SequenceEqual(_payload), "An upload changed the fixture bytes.");
            var trailing = new byte[1];
            Require(await source.ReadAsync(trailing, cancellationToken) == 0, "The fixture upload had unexpected trailing bytes.");
            var file = new CloudObject("parity-upload-" + Guid.NewGuid().ToString("N"), key, length, sha1.ToLowerInvariant(), modifiedUtc);
            lock (_gate)
            {
                _files[key] = file;
                Uploads++;
                SaveMetadata();
            }
            progress?.Report(new(length, length));
            return file;
        }
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

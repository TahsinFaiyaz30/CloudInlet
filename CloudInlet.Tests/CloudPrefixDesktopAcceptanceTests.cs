using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using CloudInlet.Windows.CloudFiles;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using Windows.Storage.Provider;

namespace CloudInlet.Tests;

/// <summary>Opt-in native binding acceptance in a fresh ordinary folder, never a personal Known Folder.</summary>
[TestClass, DoNotParallelize]
public sealed class CloudPrefixDesktopAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("LiveProvider")]
    public async Task NativeRealB2BindingPreservesFilesAndReopensMappedPlaceholder()
    {
        if (Environment.GetEnvironmentVariable("CLOUDINLET_RUN_B2_DESKTOP_MIGRATION_ACCEPTANCE") != "1")
            Assert.Inconclusive("Explicit isolated native B2 acceptance opt-in is required. No requests or native changes were made.");
        var expectedBucket = Environment.GetEnvironmentVariable("CLOUDINLET_ACCEPTANCE_B2_BUCKET");
        var evidenceRoot = Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(expectedBucket) || string.IsNullOrWhiteSpace(evidenceRoot) ||
            !Path.IsPathFullyQualified(evidenceRoot) || !Directory.Exists(evidenceRoot))
            Assert.Inconclusive("The reviewed bucket and an existing absolute evidence directory are required.");
        if (!StorageProviderSyncRootManager.IsSupported()) Assert.Inconclusive("Native Cloud Files is unavailable.");
        var credentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        if (!File.Exists(credentialPath)) Assert.Inconclusive("Protected test credentials are not provisioned.");
        var credentialBytes = await File.ReadAllBytesAsync(credentialPath);
        var plain = ProtectedData.Unprotect(credentialBytes, "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        B2Credentials credentials;
        try { credentials = JsonSerializer.Deserialize<B2Credentials>(plain) ?? throw new InvalidDataException("Invalid protected test credentials."); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        var run = Guid.NewGuid();
        var directory = Path.Combine(Path.GetFullPath(evidenceRoot!), "cloud-prefix-desktop-acceptance-" + run.ToString("N"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.Combine(profile, "CloudInlet-CloudBindingLiveAcceptance-" + run.ToString("N"));
        if (Directory.Exists(directory) || Directory.Exists(folder)) throw new IOException("A fresh acceptance identity already exists.");
        Directory.CreateDirectory(directory);
        var beforeRegistrations = Registrations();
        var beforeKnownFolders = KnownFolders();
        var personalStateBefore = PersonalStateDigests();
        var state = Path.Combine(directory, "state");
        var receipts = Path.Combine(directory, "receipts");
        var phase = "preflight";
        var succeeded = false;
        var registrationCleaned = false;
        var localFolderCleaned = false;
        var mappedHydrationObserved = false;
        var bindingReopened = false;
        var guards = new List<SafeFileHandle>();
        SafeFileHandle? folderGuard = null;
        string? oldRegistration = null, newRegistration = null, failureType = null, failureStack = null;
        CloudPrefixAcceptanceFailure? providerFailure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var transport = new CloudPrefixAcceptanceTransport(directory);
        using var cloud = new B2CloudStore(transport);
        try
        {
            ClientRegistrationMigration.PinAncestors(directory, guards);
            ClientRegistrationMigration.PinAncestors(profile, guards);
            var account = await cloud.ConnectAsync(credentials, token);
            var buckets = await cloud.ListBucketsAsync(token);
            if (buckets.Count != 1 || account.AllowedBucketId != buckets[0].Id || buckets[0].Name != expectedBucket)
                Assert.Inconclusive("The key must be restricted to exactly the named test bucket. Nothing was changed.");
            var scope = new CloudPrefixAcceptanceScope(buckets[0].Id, account.AllowedNamePrefix, run);
            transport.Scope = scope;
            var empty = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            if (Environment.GetEnvironmentVariable("CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY") == "1")
            {
                scope.RequirePolicy(empty);
                Assert.Inconclusive("Read-only preflight finished. No test state, local sync folder, native registration or cloud versions were created.");
            }
            scope.EnableWrites(account, empty);
            var settings = new AppSettings { AccountId = account.AccountId, BucketId = scope.Bucket,
                KeyId = credentials.KeyId, RootPath = folder, Prefix = scope.Source };
            var storage = new ClientStorage(state);
            storage.SaveSettings(settings); storage.SaveCredentials(credentials);
            var savedCredentials = File.ReadAllBytes(Path.Combine(state, "credentials.dpapi"));
            credentials = null!;
            Directory.CreateDirectory(folder); Directory.CreateDirectory(receipts);
            ClientMigrationReview.EnsureUnlinked(folder);
            folderGuard = ClientRegistrationMigration.Pin(folder);
            ClientMigrationReview.EnsureUnlinked(folder);
            var onlineBytes = Encoding.UTF8.GetBytes(new string('x', 65536) + " live-native-" + run.ToString("N"));
            var dirtyBytes = Encoding.UTF8.GetBytes("preserve unuploaded local edits " + run.ToString("N"));
            var modified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
            async Task<CloudObject> Seed(string name, byte[] bytes)
            {
                scope.RequirePolicy(await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                using var input = new MemoryStream(bytes, writable: false);
                return await cloud.UploadAsync(scope.Bucket, scope.Source + name, input, bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), modified, cancellationToken: token);
            }
            phase = "seed-isolated-files";
            var online = await Seed("online.bin", onlineBytes);
            var dirty = await Seed("dirty.txt", Encoding.UTF8.GetBytes("original remote baseline"));
            var manifestPath = Path.Combine(state, "State", SyncRootMigrationIdentity.StateName(settings, folder, scope.Source) + ".sqlite");
            var manifest = new SyncManifest(manifestPath);
            manifest.Put(new("online.bin", online, online.Size, online.ModifiedUtc));
            manifest.Put(new("dirty.txt", dirty, dirty.Size, dirty.ModifiedUtc));
            var baselineBefore = manifest.ReadAll();
            var dirtyPath = Path.Combine(folder, "dirty.txt");
            var onlinePath = Path.Combine(folder, "online.bin");
            await File.WriteAllBytesAsync(dirtyPath, dirtyBytes, token);
            string Account(string prefix) => account.AccountId + ":" + scope.Bucket + ":" + prefix;
            oldRegistration = WindowsPlaceholderService.GetRegistrationId(Account(scope.Source));
            newRegistration = WindowsPlaceholderService.GetRegistrationId(Account(scope.Destination), true);
            Assert.IsFalse(beforeRegistrations.Any(value => value.Id == oldRegistration || value.Id == newRegistration));
            phase = "create-isolated-native-placeholder";
            await using (var native = new WindowsPlaceholderService())
            {
                await native.ConnectAsync(folder, Account(scope.Source), (_, _, _, _, _) =>
                    Task.FromException(new IOException("Seed placeholders must remain online-only until migration.")), token);
                await native.CreateOrUpdateAsync(onlinePath, online, true, token);
                Assert.IsTrue(native.IsPlaceholder(onlinePath));
                Assert.IsFalse(native.IsHydrated(onlinePath));
            }
            var beforeCloud = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Isolated native B2 acceptance",
                scope.Source, scope.Destination, "Preserve originals and local files")]);
            var runtime = new ClientFolderMigration.WindowsRuntime(() => new B2CloudStore(new BorrowedTransport(transport)));
            IReadOnlyList<CloudPrefixVersionMapping> mappings;
            phase = "migrate-production-desktop-binding";
            using (var copies = Copies(directory))
            using (var journal = Journal(directory))
            using (var binding = ClientCloudPrefixBinding.Open(plan, "cloud", state, receipts, plan.Fingerprint, runtime))
            {
                var operation = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                    cloud.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
                await operation.ApplyAsync(new Progress(value => TestContext.WriteLine("Native migration: " + value.Operation)), token);
                await operation.VerifyAsync(token);
                Assert.IsFalse(operation.CanRevert);
                mappings = operation.VersionMappings.ToArray();
            }
            Assert.AreEqual(scope.Destination, storage.LoadSettings().Prefix);
            CollectionAssert.AreEqual(savedCredentials, File.ReadAllBytes(Path.Combine(state, "credentials.dpapi")));
            CollectionAssert.AreEqual(onlineBytes, await File.ReadAllBytesAsync(onlinePath, token));
            CollectionAssert.AreEqual(dirtyBytes, await File.ReadAllBytesAsync(dirtyPath, token));
            var afterManifest = new SyncManifest(manifestPath).ReadAll();
            foreach (var pair in baselineBefore)
            {
                var mapping = mappings.Single(value => value.Original.FileId == pair.Value.Remote.FileId);
                Assert.AreEqual(pair.Value with { Remote = pair.Value.Remote with { FileId = mapping.Migrated.FileId, Key = mapping.Migrated.Key }, NativeMarkPending = true }, afterManifest[pair.Key]);
            }
            Assert.IsFalse(Registrations().Any(value => value.Id == oldRegistration));
            Assert.IsTrue(Registrations().Any(value => value.Id == newRegistration && value.Path == folder));
            phase = "reopen-protected-native-binding";
            var mutationCount = transport.Mutations;
            using (var copies = Copies(directory))
            using (var journal = Journal(directory))
            using (var binding = ClientCloudPrefixBinding.Open(plan, "cloud", state, receipts, plan.Fingerprint, runtime))
            {
                var reopened = new CloudPrefixMigration(plan, "cloud", scope.Source, scope.Destination,
                    cloud.CreatePrefixMigrationStore(scope.Bucket, scope.Source, scope.Destination, copies), binding, journal);
                await reopened.ApplyAsync(new Progress(_ => { }), token); await reopened.VerifyAsync(token);
                Assert.AreEqual(mutationCount, transport.Mutations, "Reopening completed desktop binding must not copy again.");
                bindingReopened = true;
            }
            phase = "reopen-native-placeholder-and-read-new-version";
            var migratedOnline = afterManifest["online.bin"].Remote;
            await using (var native = new WindowsPlaceholderService())
            {
                await native.ConnectAsync(folder, Account(scope.Destination), async (file, offset, length, destination, ct) =>
                {
                    Assert.AreEqual(migratedOnline.FileId, file.FileId);
                    Assert.AreEqual(migratedOnline.Key, file.Key);
                    mappedHydrationObserved = true;
                    await cloud.DownloadAsync(file, destination, offset, length, cancellationToken: ct);
                }, token, null, true);
                // The real sync engine uses this verified native marking after registration recovery.
                // It hashes the existing local bytes and rejects stale/dirty baselines before conversion.
                await native.MarkInSyncAsync(onlinePath, migratedOnline, token);
                await native.FreeSpaceAsync(onlinePath, token);
                Assert.IsFalse(native.IsHydrated(onlinePath));
                await native.HydrateAsync(onlinePath, token);
                CollectionAssert.AreEqual(onlineBytes, await File.ReadAllBytesAsync(onlinePath, token));
                Assert.IsTrue(mappedHydrationObserved, "Native hydration must request the migrated immutable version.");
                CollectionAssert.AreEqual(dirtyBytes, await File.ReadAllBytesAsync(dirtyPath, token));
                await native.PrepareForUnregisterAsync(token);
            }
            var afterCloud = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(JsonSerializer.Serialize(beforeCloud.SourceVersions), JsonSerializer.Serialize(afterCloud.SourceVersions));
            Assert.AreEqual(2, afterCloud.DestinationVersions.Count);
            Assert.AreEqual(4, scope.Owned.Count);
            // Native preparation has converted the fixture back to ordinary local files. Keep
            // both handles exclusively open through cleanup so content or path replacement
            // cannot turn a previously reviewed path into an unrelated file.
            using var localFiles = ExactLocalFiles.Open(folder, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["online.bin"] = onlineBytes,
                ["dirty.txt"] = dirtyBytes
            });
            phase = "remove-only-owned-native-registration";
            RemoveOwnedRegistration(newRegistration, folder);
            CollectionAssert.AreEqual(beforeRegistrations, Registrations());
            registrationCleaned = true;
            AssertPersonalStateUnchanged();
            phase = "exact-owned-cloud-cleanup";
            scope.EnableCleanup(afterCloud);
            foreach (var owned in scope.RemainingOwned.ToArray())
            {
                scope.RequireRemainingInventory(await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token));
                using var deleted = await transport.PostAsync("b2_delete_file_version", new { fileName = owned.Key, fileId = owned.Id }, token);
                Assert.AreEqual(owned.Id, deleted.RootElement.GetProperty("fileId").GetString());
                Assert.AreEqual(owned.Key, deleted.RootElement.GetProperty("fileName").GetString());
            }
            var clean = await cloud.InspectPrefixMigrationAsync(scope.Bucket, scope.Source, scope.Destination, token);
            Assert.AreEqual(0, clean.SourceVersions.Count + clean.DestinationVersions.Count + clean.UnfinishedUploads.Count);
            Assert.AreEqual(profile, Path.GetDirectoryName(Path.GetFullPath(folder)));
            Assert.AreEqual("CloudInlet-CloudBindingLiveAcceptance-" + run.ToString("N"), Path.GetFileName(folder));
            localFiles.DeleteVerifiedFiles();
            localFiles.Dispose();
            folderGuard.Dispose(); folderGuard = null;
            // Parent ancestors remain pinned. Never recurse: an unexpected child is retained.
            Directory.Delete(folder, recursive: false); localFolderCleaned = true;
            AssertPersonalStateUnchanged();
            phase = "complete"; succeeded = true;
        }
        catch (AssertInconclusiveException) { throw; }
        catch (Exception error)
        {
            failureType = error.GetType().FullName; failureStack = error.StackTrace; providerFailure = CloudPrefixAcceptanceFailure.From(error);
            Assert.Fail("Native B2 acceptance stopped at " + phase + " (" + error.GetType().Name + "). Isolated evidence and incomplete fixtures were retained.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
                {
                    run, phase, succeeded, failureType, failureStack, providerFailure, fixtureNamespace = transport.Scope?.Root,
                    transport.Mutations, transport.PayloadDownloads, ownedVersions = transport.Scope?.Owned.Count ?? 0,
                    oldRegistration, newRegistration, localFolder = folder, registrationCleaned, localFolderCleaned,
                    bindingReopened, mappedHydrationObserved,
                    scope = "Real B2, protected desktop binding, settings/SQLite/native registration, preserved dirty local bytes, and new-version native hydration. No Known Folder writes or Windows reboot."
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddResultFile(Path.Combine(directory, "report.json"));
            }
            finally
            {
                folderGuard?.Dispose();
                foreach (var guard in guards) guard.Dispose();
            }
        }
        void AssertPersonalStateUnchanged()
        {
            CollectionAssert.AreEqual(beforeKnownFolders, KnownFolders());
            CollectionAssert.AreEqual(personalStateBefore, PersonalStateDigests());
            CollectionAssert.AreEqual(credentialBytes, File.ReadAllBytes(credentialPath));
        }
    }

    private static ProtectedB2CloudPrefixCopyJournal Copies(string directory) => new(Path.Combine(directory, "copies.dpapi"), CloudPrefixProviderAcceptanceTests.Protect, CloudPrefixProviderAcceptanceTests.Unprotect);
    private static ProtectedCloudPrefixMigrationJournal Journal(string directory) => new(Path.Combine(directory, "migration.dpapi"), CloudPrefixProviderAcceptanceTests.Protect, CloudPrefixProviderAcceptanceTests.Unprotect);
    private sealed class Progress(Action<MigrationProgress> report) : IProgress<MigrationProgress> { public void Report(MigrationProgress value) => report(value); }
    private sealed class BorrowedTransport(HttpMessageHandler transport) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(transport, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _invoker.SendAsync(request, token);
        protected override void Dispose(bool disposing) { if (disposing) _invoker.Dispose(); base.Dispose(disposing); }
    }
    private sealed record Registration(string Id, string Path);
    private static Registration[] Registrations() => StorageProviderSyncRootManager.GetCurrentSyncRoots()
        .Select(value => new Registration(value.Id, value.Path.Path)).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
    private static string[] KnownFolders() => new[] { "Desktop", "Documents", "Pictures", "Music", "Videos" }
        .Select(name => name + "|" + KnownFolderBackup.GetPath(name)).ToArray();
    private static string[] PersonalStateDigests()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[] { "CloudBay", "CloudInlet", "CloudBay.Debug", "CloudInlet.Debug" }
            .SelectMany(name => new[] { "settings.json", "credentials.dpapi" }.Select(file => Path.Combine(local, name, file)))
            .Where(File.Exists).Select(path => path + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }
    private static void RemoveOwnedRegistration(string id, string folder)
    {
        var existing = Registrations().SingleOrDefault(value => value.Id == id);
        if (existing is null || existing.Path != folder) throw new IOException("The isolated native registration changed ownership.");
        StorageProviderSyncRootManager.Unregister(id);
    }

    /// <summary>Deletes only the two exact acknowledged fixture payloads, never a directory tree.</summary>
    private sealed class ExactLocalFiles : IDisposable
    {
        private readonly List<FileStream> _files = [];
        internal static ExactLocalFiles Open(string folder, IReadOnlyDictionary<string, byte[]> expected)
        {
            ClientMigrationReview.EnsureUnlinked(folder);
            var names = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(expected.Keys.Order(StringComparer.Ordinal)))
                throw new IOException("Unexpected local entries were retained in the isolated native fixture.");
            var result = new ExactLocalFiles();
            try
            {
                foreach (var pair in expected)
                {
                    var path = Path.Combine(folder, pair.Key);
                    // DELETE | GENERIC_READ, no sharing, OPEN_EXISTING, OPEN_REPARSE_POINT.
                    var handle = CreateFileW(@"\\?\" + path, 0x80010000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    try
                    {
                        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Links != 1)
                            throw new IOException("Cleanup requires an ordinary, exclusively owned fixture file.");
                        var file = new FileStream(handle, FileAccess.Read);
                        result._files.Add(file);
                        if (file.Length != pair.Value.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), SHA256.HashData(pair.Value)))
                            throw new IOException("The isolated local fixture changed; its files were retained.");
                    }
                    catch { if (!result._files.Any(file => ReferenceEquals(file.SafeFileHandle, handle))) handle.Dispose(); throw; }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        internal void DeleteVerifiedFiles()
        {
            foreach (var file in _files)
            {
                var disposition = new FileDisposition { Delete = 1 };
                if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        public void Dispose() { foreach (var file in _files) file.Dispose(); _files.Clear(); }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            public FileAttributes Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public byte Delete; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
    }
}

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Sync;
using CloudBay.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Provider;

namespace CloudBay.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeSyncIntegrationTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task SameMetadataSaveAfterUploadCannotBeMarkedCleanOrEvicted()
    {
        await using var h = await Harness.CreateAsync();
        var path = h.Path("late-save.txt");
        await File.WriteAllTextAsync(path, "AAAA");
        await h.Engine.SyncNowAsync();
        var originalWrite = File.GetLastWriteTimeUtc(path);
        var uploaded = new CloudObject("uploaded-AAAA", "CloudBay/late-save.txt", 4,
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("AAAA"))).ToLowerInvariant(), new DateTimeOffset(originalWrite));
        await using (var edit = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            await edit.WriteAsync(Encoding.UTF8.GetBytes("BBBB"));
        File.SetLastWriteTimeUtc(path, originalWrite);
        Assert.IsTrue(h.Placeholders.IsPlaceholder(path), "This case must edit an existing placeholder rather than replace it with an ordinary file.");
        await Assert.ThrowsExceptionAsync<IOException>(() => h.Placeholders.MarkInSyncAsync(path, uploaded));
        Assert.AreEqual("BBBB", await File.ReadAllTextAsync(path));
        Assert.IsTrue(h.Placeholders.HasLocalChanges(path));
        await Assert.ThrowsExceptionAsync<IOException>(() => h.Placeholders.FreeSpaceAsync(path));
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, h.Snapshot.Message);
        Assert.AreEqual("BBBB", h.Cloud.Text("late-save.txt"));
        await h.Placeholders.FreeSpaceAsync(path);
        Assert.AreEqual("BBBB", await File.ReadAllTextAsync(path));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120_000)]
    public async Task SameMetadataLocalEditsAndAtomicReplacementsStillUpload(bool replace)
    {
        await using var h = await Harness.CreateAsync();
        var path = h.Path("same-metadata.txt");
        await File.WriteAllTextAsync(path, "AAAA");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, h.Snapshot.Message);
        Assert.AreEqual("AAAA", h.Cloud.Text("same-metadata.txt"));
        Assert.IsFalse(h.Placeholders.HasLocalChanges(path));
        var originalWrite = File.GetLastWriteTimeUtc(path);
        if (replace)
        {
            var replacement = h.Path("replacement.tmp");
            await File.WriteAllTextAsync(replacement, "BBBB");
            File.Move(replacement, path, overwrite: true);
            Assert.IsFalse(h.Placeholders.IsPlaceholder(path));
        }
        else
        {
            await using (var edit = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                await edit.WriteAsync(Encoding.UTF8.GetBytes("BBBB"));
            Assert.IsTrue(h.Placeholders.IsPlaceholder(path));
        }
        File.SetLastWriteTimeUtc(path, originalWrite);
        Assert.AreEqual(4L, new FileInfo(path).Length);
        Assert.AreEqual(originalWrite, File.GetLastWriteTimeUtc(path));
        Assert.IsTrue(h.Placeholders.HasLocalChanges(path), "Native dirty state must detect changes even when size and time match the baseline.");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, h.Snapshot.Message);
        Assert.AreEqual("BBBB", h.Cloud.Text("same-metadata.txt"));
        Assert.IsFalse(h.Placeholders.HasLocalChanges(path));
        await h.Placeholders.FreeSpaceAsync(path);
        Assert.AreEqual("BBBB", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task NativeEnginePreservesDirtyConflictsAndHydratedRecovery()
    {
        await using var h = await Harness.CreateAsync();
        h.Cloud.Seed("report.txt", "original cloud bytes");
        await h.Engine.SyncNowAsync();
        Assert.IsFalse(h.Placeholders.IsHydrated(h.Path("report.txt")));
        Assert.AreEqual("original cloud bytes", await File.ReadAllTextAsync(h.Path("report.txt")));
        await File.WriteAllTextAsync(h.Path("report.txt"), "local conflict content");
        h.Cloud.Seed("report.txt", "changed cloud content");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, h.Snapshot.Message);
        Assert.AreEqual("changed cloud content", await File.ReadAllTextAsync(h.Path("report.txt")));
        var conflict = Directory.GetFiles(h.Root, "report (conflict *).txt").Single();
        Assert.AreEqual("local conflict content", await File.ReadAllTextAsync(conflict));
        Assert.AreEqual("local conflict content", h.Cloud.Text(System.IO.Path.GetFileName(conflict)));
        // Uploaded conflict copies must be clean, evictable placeholders with their own B2 identity.
        await h.Placeholders.FreeSpaceAsync(conflict);
        Assert.AreEqual("local conflict content", await File.ReadAllTextAsync(conflict));

        h.Cloud.Remove("report.txt");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, h.Snapshot.Message);
        Assert.IsFalse(File.Exists(h.Path("report.txt")));
        var recovered = Directory.GetFiles(h.Recovery, "report.txt", SearchOption.AllDirectories).Single();
        Assert.AreEqual("changed cloud content", await File.ReadAllTextAsync(recovered));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task OnlineOnlyRecoveryAndExplicitUnregisterRetainOrdinaryLocalFiles()
    {
        await using var h = await Harness.CreateAsync();
        h.Cloud.Seed("online.txt", "recover online version");
        h.Cloud.Seed("keep.txt", "keep these bytes");
        await h.Engine.SyncNowAsync();
        h.Cloud.Remove("online.txt");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, h.Snapshot.Message);
        Assert.IsFalse(File.Exists(h.Path("online.txt")));
        var recovered = Directory.GetFiles(h.Path(".cloudbay"), "online.txt", SearchOption.AllDirectories).Single();
        Assert.IsTrue(h.Placeholders.IsPlaceholder(recovered));
        Assert.IsFalse(h.Placeholders.IsHydrated(recovered));
        // Account disconnect includes internal Recovery, then removes every Cloud Files file tag.
        await h.Engine.QuiesceAsync();
        await h.Placeholders.PrepareForUnregisterAsync();
        Assert.IsFalse(h.Placeholders.IsPlaceholder(recovered));
        Assert.IsFalse(h.Placeholders.IsPlaceholder(h.Path("keep.txt")));
        await h.Placeholders.DisconnectAsync();
        StorageProviderSyncRootManager.Unregister(h.Placeholders.RegistrationId!);
        h.Unregistered = true;
        Assert.AreEqual("recover online version", await File.ReadAllTextAsync(recovered));
        Assert.AreEqual("keep these bytes", await File.ReadAllTextAsync(h.Path("keep.txt")));
        Assert.IsFalse((File.GetAttributes(h.Path("keep.txt")) & FileAttributes.ReparsePoint) != 0);
        Assert.IsFalse(StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(r => r.Id == h.Placeholders.RegistrationId));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudBay-NativeEngine-" + Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public string Recovery => System.IO.Path.Combine(_directory, "Recovery");
        public VersionedCloud Cloud { get; } = new();
        public WindowsPlaceholderService Placeholders { get; } = new();
        public SyncEngine Engine { get; }
        public bool Unregistered;
        public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "");
        private Harness()
        {
            Directory.CreateDirectory(Root);
            Engine = new(Cloud, Placeholders, new SyncManifest(System.IO.Path.Combine(_directory, "state.sqlite")),
                new AppSettings { RootPath = Root, KeyId = "key", BucketId = "bucket" }, Recovery,
                _ => { }, value => Snapshot = value);
        }
        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            try
            {
                await h.Placeholders.ConnectAsync(h.Root, "native-engine-" + Guid.NewGuid().ToString("N"),
                    (file, offset, length, destination, ct) => h.Cloud.DownloadAsync(file, destination, offset, length, cancellationToken: ct));
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Placeholders.DisposeAsync();
            if (!Unregistered && Placeholders.RegistrationId is { } registration) StorageProviderSyncRootManager.Unregister(registration);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class VersionedCloud : ICloudStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CloudObject> _latest = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _versions = new(StringComparer.Ordinal);
        public CloudObject Seed(string name, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), "CloudBay/" + name, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
                DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            lock (_gate) { _latest[file.Key] = file; _versions[file.FileId] = bytes; }
            return file;
        }
        public void Remove(string name) { lock (_gate) _latest.Remove("CloudBay/" + name); }
        public string Text(string name) { lock (_gate) return Encoding.UTF8.GetString(_versions[_latest["CloudBay/" + name].FileId]); }
        public async IAsyncEnumerable<CloudObject> ListAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            CloudObject[] files; lock (_gate) files = _latest.Values.ToArray();
            foreach (var file in files) { ct.ThrowIfCancellationRequested(); yield return file; }
        }
        public async Task<CloudObject> UploadAsync(string bucket, string key, Stream source, long length, string sha1, DateTimeOffset modified,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream();
            await source.CopyToAsync(output, cancellationToken);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), key, length, sha1,
                DateTimeOffset.FromUnixTimeMilliseconds(modified.ToUnixTimeMilliseconds()));
            lock (_gate) { _latest[key] = file; _versions[file.FileId] = output.ToArray(); }
            return file;
        }
        public async Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            byte[] bytes; lock (_gate) bytes = _versions[file.FileId];
            await destination.WriteAsync(bytes.AsMemory((int)offset, (int)(length ?? bytes.Length - offset)), cancellationToken);
        }
        public Task HideAsync(string bucket, string key, CancellationToken ct = default) { lock (_gate) _latest.Remove(key); return Task.CompletedTask; }
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucket, CloudObject file, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

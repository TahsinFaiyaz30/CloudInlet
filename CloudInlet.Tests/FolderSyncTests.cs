using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class FolderSyncTests
{
    [TestMethod]
    public async Task NestedEmptyFoldersUploadZeroByteMarkersAndRemainStableAcrossRestart()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("empty/child/grandchild"));
        await h.Engine.SyncNowAsync();
        CollectionAssert.AreEquivalent(new[] { "CloudInlet/empty/", "CloudInlet/empty/child/", "CloudInlet/empty/child/grandchild/" }, h.Cloud.Keys);
        Assert.IsTrue(h.Cloud.Objects.All(f => f.Size == 0 && f.Sha1 == "da39a3ee5e6b4b0d3255bfef95601890afd80709"));
        Assert.AreEqual(3, h.Manifest.ReadDirectories().Count);
        await h.RestartAsync();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(3, h.Cloud.Uploads);
        Assert.IsTrue(Directory.Exists(h.Path("empty/child/grandchild")));
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
    }

    [TestMethod]
    public async Task RemoteNestedEmptyFoldersAreCreatedWithoutDownloadingFiles()
    {
        await using var h = new Harness();
        h.Cloud.Seed("remote/", "");
        h.Cloud.Seed("remote/child/", "");
        await h.Engine.SyncNowAsync();
        Assert.IsTrue(Directory.Exists(h.Path("remote/child")));
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
        Assert.AreEqual(0, h.Cloud.Uploads);
        Assert.AreEqual(0, h.Cloud.Downloads);
        Assert.AreEqual(2, h.Manifest.ReadDirectories().Count);
    }

    [TestMethod]
    public async Task LocalEmptyFolderRenameAndDeletionHideMarkersWithoutDeletingVersions()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("old"));
        await h.Engine.SyncNowAsync();
        Directory.Move(h.Path("old"), h.Path("renamed"));
        await h.Engine.SyncNowAsync();
        CollectionAssert.Contains(h.Cloud.Hidden, "CloudInlet/old/");
        CollectionAssert.AreEqual(new[] { "CloudInlet/renamed/" }, h.Cloud.Keys);
        Assert.IsTrue(h.Cloud.VersionKeys.Contains("CloudInlet/old/"));
        Directory.Delete(h.Path("renamed"));
        await h.Engine.SyncNowAsync();
        CollectionAssert.Contains(h.Cloud.Hidden, "CloudInlet/renamed/");
        Assert.AreEqual(0, h.Manifest.ReadDirectories().Count);
        Assert.IsTrue(h.Cloud.VersionKeys.Contains("CloudInlet/renamed/"));
    }

    [TestMethod]
    public async Task RemoteDeletionRemovesOnlyEmptyNestedDirectories()
    {
        await using var h = new Harness();
        h.Cloud.Seed("parent/", "");
        h.Cloud.Seed("parent/child/", "");
        await h.Engine.SyncNowAsync();
        h.Cloud.Remove("parent/"); h.Cloud.Remove("parent/child/");
        await h.Engine.SyncNowAsync();
        Assert.IsFalse(Directory.Exists(h.Path("parent")));
        Assert.AreEqual(0, h.Manifest.ReadDirectories().Count);
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
    }

    [TestMethod]
    public async Task CloudFolderMarkerDeletionCannotRemoveNonemptyLocalFolder()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("folder"));
        await File.WriteAllTextAsync(h.Path("folder/safe.txt"), "keep this data");
        await h.Engine.SyncNowAsync();
        h.Cloud.Remove("folder/");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("keep this data", await File.ReadAllTextAsync(h.Path("folder/safe.txt")));
        CollectionAssert.Contains(h.Cloud.Keys, "CloudInlet/folder/safe.txt");
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
    }

    [DataTestMethod]
    [DataRow("same", "same/")]
    [DataRow("parent", "parent/child/")]
    [DataRow("parent", "parent/child.txt")]
    public async Task RemoteFileFolderHierarchyCollisionsBlockAllMutations(string file, string conflicting)
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("local.txt"), "local data");
        h.Cloud.Seed(file, "remote file");
        h.Cloud.Seed(conflicting, conflicting.EndsWith('/') ? "" : "remote descendant");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual(0, h.Cloud.Uploads);
        Assert.AreEqual(0, h.Cloud.Downloads);
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        Assert.AreEqual("local data", await File.ReadAllTextAsync(h.Path("local.txt")));
    }

    [TestMethod]
    public async Task CloudFolderConflictingWithLocalFileRetainsBothWithoutCreatingCloudCollision()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("same"), "local file bytes");
        h.Cloud.Seed("same/", "");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual("local file bytes", await File.ReadAllTextAsync(h.Path("same")));
        CollectionAssert.AreEqual(new[] { "CloudInlet/same/" }, h.Cloud.Keys);
        Assert.AreEqual(0, h.Cloud.Uploads);
    }

    [TestMethod]
    public async Task CloudFileConflictingWithLocalFolderRetainsLocalDescendants()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("same"));
        await File.WriteAllTextAsync(h.Path("same/child.txt"), "local child");
        h.Cloud.Seed("same", "remote file");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual("local child", await File.ReadAllTextAsync(h.Path("same/child.txt")));
        Assert.AreEqual(0, h.Cloud.Uploads);
        Assert.AreEqual(0, h.Cloud.Downloads);
    }

    [TestMethod]
    public async Task BulkEmptyFolderDeletionNeedsReviewAndIncompleteListingCannotHideMarkers()
    {
        await using var h = new Harness();
        for (var i = 0; i < 12; i++) Directory.CreateDirectory(h.Path($"folder-{i}"));
        await h.Engine.SyncNowAsync();
        foreach (var path in Directory.GetDirectories(h.Root)) Directory.Delete(path);
        h.Cloud.FailList = true;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        h.Cloud.FailList = false;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual(12, h.Snapshot.Pending);
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        h.Engine.ApproveDeletions();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(12, h.Cloud.Hidden.Count);
        Assert.AreEqual(0, h.Manifest.ReadDirectories().Count);
    }

    [TestMethod]
    public async Task NonemptyTrailingSlashObjectBlocksSyncBeforeAnyLocalUpload()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("local.txt"), "keep");
        h.Cloud.Seed("invalid/", "cannot be a folder");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual(0, h.Cloud.Uploads);
        Assert.IsFalse(Directory.Exists(h.Path("invalid")));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet.Folder.Tests", Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public MarkerCloud Cloud { get; } = new();
        public SyncManifest Manifest { get; }
        public SyncEngine Engine { get; private set; }
        public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "");
        public Harness()
        {
            Directory.CreateDirectory(Root);
            Manifest = new(System.IO.Path.Combine(_directory, "state.sqlite"));
            Engine = CreateEngine();
        }
        private SyncEngine CreateEngine() => new(Cloud, new NoPlaceholders(), Manifest,
            new AppSettings { RootPath = Root, KeyId = "key", BucketId = "bucket" }, System.IO.Path.Combine(_directory, "Recovery"),
            _ => { }, value => Snapshot = value);
        public string Path(string name) => System.IO.Path.Combine(Root, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public async Task RestartAsync() { await Engine.DisposeAsync(); Engine = CreateEngine(); }
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class NoPlaceholders : IPlaceholderService
    {
        public bool IsPlaceholder(string path) => false;
        public bool IsHydrated(string path) => true;
        public Task CreateOrUpdateAsync(string path, CloudObject file, bool inSync, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkInSyncAsync(string path, CloudObject file, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetPinAsync(string path, PinMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task FreeSpaceAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConnectAsync(string root, string identity, HydrationHandler hydrate, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MarkerCloud : ICloudStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (CloudObject File, byte[] Bytes)> _latest = new(StringComparer.Ordinal);
        private readonly HashSet<string> _versionKeys = new(StringComparer.Ordinal);
        public int Uploads, Downloads;
        public bool FailList;
        public List<string> Hidden { get; } = [];
        public string[] Keys { get { lock (_gate) return _latest.Keys.ToArray(); } }
        public string[] VersionKeys { get { lock (_gate) return _versionKeys.ToArray(); } }
        public CloudObject[] Objects { get { lock (_gate) return _latest.Values.Select(v => v.File).ToArray(); } }
        public void Seed(string name, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), "CloudInlet/" + name, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow);
            lock (_gate) { _latest[file.Key] = (file, bytes); _versionKeys.Add(file.Key); }
        }
        public void Remove(string name) { lock (_gate) _latest.Remove("CloudInlet/" + name); }
        public async IAsyncEnumerable<CloudObject> ListAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (FailList) throw new IOException("Interrupted listing.");
            foreach (var file in Objects) { ct.ThrowIfCancellationRequested(); yield return file; }
        }
        public async Task<CloudObject> UploadAsync(string bucket, string key, Stream source, long length, string sha1, DateTimeOffset modified,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream();
            await source.CopyToAsync(output, cancellationToken);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), key, length, sha1, modified);
            lock (_gate) { Uploads++; _latest[key] = (file, output.ToArray()); _versionKeys.Add(key); }
            return file;
        }
        public async Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            byte[] bytes; lock (_gate) { Downloads++; bytes = _latest[file.Key].Bytes; }
            await destination.WriteAsync(bytes.AsMemory((int)offset, (int)(length ?? bytes.Length - offset)), cancellationToken);
        }
        public Task HideAsync(string bucket, string key, CancellationToken ct = default)
        { lock (_gate) { Hidden.Add(key); _latest.Remove(key); } return Task.CompletedTask; }
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucket, CloudObject version, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

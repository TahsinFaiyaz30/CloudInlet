using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class SyncReconciliationProgressTests
{
    [TestMethod]
    public async Task FileCycleReportsInitialAndFinalChecksBeforeBecomingUpToDate()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("photos"));
        await File.WriteAllTextAsync(h.Path("photos/photo.txt"), "snapshot");

        await h.Engine.SyncNowAsync();

        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        Assert.IsTrue(h.Statuses.Any(status => status.Message.StartsWith("Checking local files:", StringComparison.Ordinal)));
        Assert.IsTrue(h.Statuses.Any(status => status.Message == "Checking folder changes: 1 of 1"));
        Assert.IsTrue(h.Statuses.Any(status => status.Message == "Checking Windows file status: 1 file, 2 folders checked"));
        Assert.IsFalse(h.Statuses.Any(status => status.Message == "Finishing sync"),
            "Folder reconciliation and the final Windows status scan must describe their remaining work.");
        Assert.AreEqual(0, h.Snapshot.Pending);
    }

    [TestMethod]
    public async Task AwaitingFolderUploadReportsTheFolderInsteadOfAnonymousFinishing()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("empty"));
        h.Cloud.FolderUploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Cloud.ContinueFolderUpload = release.Task;
        var cycle = h.Engine.SyncNowAsync();
        try
        {
            await h.Cloud.FolderUploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(ClientState.Syncing, h.Snapshot.State);
            Assert.AreEqual("Backing up folder empty (1 of 1)", h.Snapshot.Message);
            Assert.AreEqual(0, h.Snapshot.Pending, "Folder metadata work must not invent queued file transfers.");
        }
        finally { release.TrySetResult(); await cycle.WaitAsync(TimeSpan.FromSeconds(5)); }

        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        Assert.AreEqual(1, h.Manifest.ReadDirectories().Count);
    }

    [TestMethod]
    public async Task UnchangedFolderPollDoesNotRewriteTheDurableBaseline()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("empty"));
        await h.Engine.SyncNowAsync();
        var before = h.Manifest.ReadDirectories()["empty"];
        using var observer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = h.Database }.ToString());
        observer.Open();
        var version = DataVersion(observer);

        await h.Engine.SyncNowAsync();

        Assert.AreEqual(version, DataVersion(observer),
            "An unchanged folder tree must not issue a SQLite write for every folder on each poll.");
        Assert.AreEqual(before, h.Manifest.ReadDirectories()["empty"]);
        Assert.AreEqual(1, h.Cloud.Uploads);
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
    }

    [TestMethod]
    public async Task ChangedFolderVersionStillUpdatesItsDurableBaseline()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("empty"));
        await h.Engine.SyncNowAsync();
        var changed = h.Cloud.Seed("empty/", "");

        await h.Engine.SyncNowAsync();

        Assert.AreEqual(changed, h.Manifest.ReadDirectories()["empty"].Remote);
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        Assert.IsTrue(Directory.Exists(h.Path("empty")));
    }

    [TestMethod]
    public async Task BothLocalScansUseOneBatchedFileStateInspection()
    {
        await using var h = new Harness();
        var path = h.Path("clean.txt");
        await File.WriteAllTextAsync(path, "clean");
        var remote = h.Cloud.Seed("clean.txt", "clean", new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
        h.Manifest.Put(new("clean.txt", remote, remote.Size, remote.ModifiedUtc));
        h.Placeholders.RejectSeparateDirtyProbe = true;

        await h.Engine.SyncNowAsync();

        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        Assert.AreEqual(2, h.Placeholders.FileStateReads,
            "The initial and final snapshot should each inspect the file state once.");
        Assert.AreEqual(0, h.Cloud.Uploads);
    }

    private static long DataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBay.Progress.Tests", Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public string Database => System.IO.Path.Combine(_directory, "state.sqlite");
        public FakeCloud Cloud { get; } = new();
        public FakePlaceholders Placeholders { get; } = new();
        public SyncManifest Manifest { get; }
        public SyncEngine Engine { get; }
        public ConcurrentQueue<SyncSnapshot> Statuses { get; } = new();
        public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "");
        public Harness()
        {
            Directory.CreateDirectory(Root);
            Manifest = new(Database);
            Engine = new(Cloud, Placeholders, Manifest, new AppSettings { RootPath = Root, BucketId = "bucket", KeyId = "key" },
                System.IO.Path.Combine(_directory, "Recovery"), _ => { }, status => { Snapshot = status; Statuses.Enqueue(status); });
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakePlaceholders : IPlaceholderService
    {
        public bool RejectSeparateDirtyProbe;
        public int FileStateReads;
        public PlaceholderFileState GetFileState(string path)
        { FileStateReads++; return new(false, true, false); }
        public bool IsPlaceholder(string path) => false;
        public bool IsHydrated(string path) => true;
        public bool HasLocalChanges(string path) => RejectSeparateDirtyProbe
            ? throw new InvalidOperationException("Separate native state probes were used instead of the batched inspection.") : false;
        public Task MarkInSyncAsync(string path, CloudObject file, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetPinAsync(string path, PinMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateOrUpdateAsync(string path, CloudObject file, bool inSync, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FreeSpaceAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConnectAsync(string root, string identity, HydrationHandler hydrate, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCloud : ICloudStore
    {
        private readonly ConcurrentDictionary<string, (CloudObject File, byte[] Bytes)> _files = new(StringComparer.Ordinal);
        public int Uploads;
        public TaskCompletionSource? FolderUploadStarted;
        public Task? ContinueFolderUpload;
        public CloudObject Seed(string name, string contents, DateTimeOffset? modified = null)
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), "CloudBay/" + name, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), modified ?? DateTimeOffset.UtcNow);
            _files[file.Key] = (file, bytes);
            return file;
        }
        public async IAsyncEnumerable<CloudObject> ListAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var item in _files.Values.ToArray()) { ct.ThrowIfCancellationRequested(); yield return item.File; }
        }
        public async Task<CloudObject> UploadAsync(string bucket, string key, Stream source, long length, string sha1, DateTimeOffset modified,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (key.EndsWith('/'))
            {
                FolderUploadStarted?.TrySetResult();
                if (ContinueFolderUpload is { } release) await release.WaitAsync(cancellationToken);
            }
            using var output = new MemoryStream();
            await source.CopyToAsync(output, cancellationToken);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), key, length, sha1, modified);
            _files[key] = (file, output.ToArray());
            Interlocked.Increment(ref Uploads);
            return file;
        }
        public Task HideAsync(string bucket, string key, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); _files.TryRemove(key, out _); return Task.CompletedTask; }
        public Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucket, CloudObject version, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

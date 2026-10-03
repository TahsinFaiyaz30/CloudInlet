using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class SyncSafetyTests
{
    [TestMethod]
    public async Task IdenticalInitialFileAdoptsCloudTimestampWithoutDuplicateUpload()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("same.txt"), "same content");
        var remote = h.Cloud.Seed("same.txt", "same content", DateTimeOffset.UtcNow.AddDays(-2));
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        Assert.AreEqual(remote.ModifiedUtc, new DateTimeOffset(File.GetLastWriteTimeUtc(h.Path("same.txt"))));
        Assert.AreEqual(0, h.Cloud.Uploads);
        Assert.AreEqual(0, h.Cloud.Downloads);
        Assert.AreEqual(remote.FileId, h.Manifest.ReadAll()["same.txt"].Remote.FileId);
    }

    [TestMethod]
    public async Task EditDuringDownloadIsPreservedAndUploadedOnNextScan()
    {
        await using var h = new Harness();
        h.Cloud.Seed("report.txt", "base");
        await h.Engine.SyncNowAsync();
        h.Cloud.Seed("report.txt", "new cloud content");
        h.Cloud.AfterDownload = () => File.WriteAllTextAsync(h.Path("report.txt"), "late local edit");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("new cloud content", await File.ReadAllTextAsync(h.Path("report.txt")));
        var conflict = Directory.GetFiles(h.Root, "report (conflict *).txt").Single();
        Assert.AreEqual("late local edit", await File.ReadAllTextAsync(conflict));
        h.Cloud.AfterDownload = null;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("late local edit", h.Cloud.Text(System.IO.Path.GetFileName(conflict)));
    }

    [TestMethod]
    public async Task FileCreatedDuringInitialDownloadIsPreserved()
    {
        await using var h = new Harness();
        h.Cloud.Seed("first.txt", "cloud");
        h.Cloud.AfterDownload = () => File.WriteAllTextAsync(h.Path("first.txt"), "new local file");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("cloud", await File.ReadAllTextAsync(h.Path("first.txt")));
        var conflict = Directory.GetFiles(h.Root, "first (conflict *).txt").Single();
        Assert.AreEqual("new local file", await File.ReadAllTextAsync(conflict));
    }

    [TestMethod]
    public async Task CorruptDownloadCannotReplaceExistingContentsOrBaseline()
    {
        await using var h = new Harness();
        var original = h.Cloud.Seed("safe.txt", "safe original");
        await h.Engine.SyncNowAsync();
        h.Cloud.Seed("safe.txt", "replacement!");
        h.Cloud.CorruptDownloads = true;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("safe original", await File.ReadAllTextAsync(h.Path("safe.txt")));
        Assert.AreEqual(original.FileId, h.Manifest.ReadAll()["safe.txt"].Remote.FileId);
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
    }

    [TestMethod]
    public async Task EditImmediatelyAfterNativeMarkRemainsDirtyInManifest()
    {
        await using var h = new Harness();
        h.Cloud.Seed("save.txt", "remote bytes");
        h.Placeholders.AfterMark = path => File.WriteAllText(path, "save after native marking");
        await h.Engine.SyncNowAsync();
        h.Placeholders.AfterMark = null;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("save after native marking", h.Cloud.Text("save.txt"));
        Assert.AreEqual(1, h.Cloud.Uploads);
    }

    [TestMethod]
    public async Task SuccessfulUploadWithUncommittedBaselineRecoversWithoutConflictCopy()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("crash.txt"), "base");
        await h.Engine.SyncNowAsync();
        await File.WriteAllTextAsync(h.Path("crash.txt"), "already uploaded after crash");
        var completed = h.Cloud.Seed("crash.txt", "already uploaded after crash", new DateTimeOffset(File.GetLastWriteTimeUtc(h.Path("crash.txt"))));
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(1, h.Cloud.Uploads);
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "crash (conflict *).txt").Length);
        Assert.AreEqual(completed.FileId, h.Manifest.ReadAll()["crash.txt"].Remote.FileId);
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
    }

    [TestMethod]
    public async Task ChangedBulkDeletionSetNeedsAnotherApproval()
    {
        await using var h = new Harness();
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(h.Path($"file-{i}.txt"), "content");
        await h.Engine.SyncNowAsync();
        foreach (var path in Directory.GetFiles(h.Root)) File.Delete(path);
        await h.Engine.SyncNowAsync();
        h.Engine.ApproveDeletions();
        var saved = h.Manifest.ReadAll()["file-0.txt"];
        await File.WriteAllTextAsync(h.Path("file-0.txt"), "content");
        File.SetLastWriteTimeUtc(h.Path("file-0.txt"), saved.LocalWriteUtc.UtcDateTime);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        Assert.AreEqual(11, h.Snapshot.Pending);
        h.Engine.ApproveDeletions();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(11, h.Cloud.Hidden.Count);
        Assert.IsTrue(File.Exists(h.Path("file-0.txt")));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task QuiesceCancelsActiveTransferAndPreventsAnotherCycle()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("pending.txt"), "pending content");
        h.Cloud.BlockUpload = true;
        var sync = h.Engine.SyncNowAsync();
        await h.Cloud.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Engine.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await sync;
        Assert.IsTrue(h.Engine.IsPaused);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Cloud.Uploads);
        h.Cloud.BlockUpload = false;
        h.Engine.Resume();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("pending content", h.Cloud.Text("pending.txt"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBay.Safety.Tests", Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public FakeCloud Cloud { get; } = new();
        public FakePlaceholders Placeholders { get; } = new();
        public SyncManifest Manifest { get; }
        public SyncEngine Engine { get; }
        public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "");
        public Harness()
        {
            Directory.CreateDirectory(Root);
            Manifest = new(System.IO.Path.Combine(_directory, "state.sqlite"));
            Engine = new(Cloud, Placeholders, Manifest, new AppSettings
                { RootPath = Root, KeyId = "key", BucketId = "bucket", FilesOnDemand = false },
                System.IO.Path.Combine(_directory, "Recovery"), _ => { }, state => Snapshot = state);
        }
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FakePlaceholders : IPlaceholderService
    {
        public Action<string>? AfterMark;
        public bool IsPlaceholder(string path) => false;
        public bool IsHydrated(string path) => true;
        public Task MarkInSyncAsync(string path, CloudObject file, CancellationToken ct = default)
        {
            Assert.AreEqual(file.Size, new FileInfo(path).Length);
            Assert.AreEqual(file.ModifiedUtc.UtcDateTime, File.GetLastWriteTimeUtc(path), "Native marking requires the exact uploaded snapshot timestamp.");
            AfterMark?.Invoke(path);
            return Task.CompletedTask;
        }
        public Task CreateOrUpdateAsync(string path, CloudObject file, bool inSync, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPinAsync(string path, PinMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task FreeSpaceAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConnectAsync(string root, string identity, HydrationHandler hydration, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCloud : ICloudStore
    {
        private readonly Dictionary<string, (CloudObject File, byte[] Bytes)> _files = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        public int Uploads, Downloads;
        public bool CorruptDownloads, BlockUpload;
        public Func<Task>? AfterDownload;
        public List<string> Hidden { get; } = [];
        public TaskCompletionSource UploadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CloudObject Seed(string name, string content, DateTimeOffset? modified = null)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), "CloudBay/" + name, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), modified ?? DateTimeOffset.UtcNow);
            lock (_gate) _files[file.Key] = (file, bytes);
            return file;
        }
        public string Text(string name) { lock (_gate) return Encoding.UTF8.GetString(_files["CloudBay/" + name].Bytes); }
        public async IAsyncEnumerable<CloudObject> ListAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            CloudObject[] files; lock (_gate) files = _files.Values.Select(v => v.File).ToArray();
            foreach (var file in files) { ct.ThrowIfCancellationRequested(); yield return file; }
        }
        public async Task<CloudObject> UploadAsync(string bucket, string key, Stream source, long length, string sha1, DateTimeOffset modified,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            UploadEntered.TrySetResult();
            if (BlockUpload) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            using var output = new MemoryStream();
            await source.CopyToAsync(output, cancellationToken);
            var file = new CloudObject(Guid.NewGuid().ToString("N"), key, length, sha1, DateTimeOffset.FromUnixTimeMilliseconds(modified.ToUnixTimeMilliseconds()));
            lock (_gate) { _files[key] = (file, output.ToArray()); Uploads++; }
            return file;
        }
        public async Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            byte[] bytes; lock (_gate) { bytes = _files[file.Key].Bytes.ToArray(); Downloads++; }
            if (CorruptDownloads && bytes.Length > 0) bytes[0] ^= 1;
            await destination.WriteAsync(bytes, cancellationToken);
            if (AfterDownload is { } after) await after();
        }
        public Task HideAsync(string bucket, string key, CancellationToken ct = default)
        { lock (_gate) { _files.Remove(key); Hidden.Add(key); } return Task.CompletedTask; }
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucket, CloudObject file, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

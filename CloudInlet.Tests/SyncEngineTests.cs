using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class SyncEngineTests
{
    [TestMethod]
    public async Task NewAndEditedLocalFilesUploadAndPersistAcrossRestart()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("notes.txt"), "first");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("first", h.Store.Text("CloudInlet/notes.txt"));
        Assert.AreEqual(1, new SyncManifest(h.Database).ReadAll().Count);
        await File.WriteAllTextAsync(h.Path("notes.txt"), "second longer");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("second longer", h.Store.Text("CloudInlet/notes.txt"));
        Assert.AreEqual(2, h.Store.Uploads);
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
    }

    [TestMethod]
    public async Task FilesOnDemandCreatesPlaceholdersWithoutDownloading()
    {
        await using var h = new Harness();
        h.Store.Seed("CloudInlet/photo.jpg", "remote image");
        await h.Engine.SyncNowAsync();
        Assert.IsTrue(h.Placeholders.IsPlaceholder(h.Path("photo.jpg")));
        Assert.IsFalse(h.Placeholders.IsHydrated(h.Path("photo.jpg")));
        Assert.AreEqual(0, h.Store.Downloads);
        Assert.AreEqual(0, h.Store.Uploads);
    }

    [TestMethod]
    public async Task RemoteEditUpdatesLocalAndConflictingEditPreservesBothCopies()
    {
        await using var h = new Harness(filesOnDemand: false);
        h.Store.Seed("CloudInlet/report.txt", "base");
        await h.Engine.SyncNowAsync();
        h.Store.Seed("CloudInlet/report.txt", "cloud edit");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("cloud edit", await File.ReadAllTextAsync(h.Path("report.txt")));
        await File.WriteAllTextAsync(h.Path("report.txt"), "local conflict");
        h.Store.Seed("CloudInlet/report.txt", "second cloud edit");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("second cloud edit", await File.ReadAllTextAsync(h.Path("report.txt")));
        var conflicts = Directory.GetFiles(h.Root, "report (conflict *).txt");
        Assert.AreEqual(1, conflicts.Length);
        Assert.AreEqual("local conflict", await File.ReadAllTextAsync(conflicts[0]));
        Assert.IsTrue(h.Store.Keys.Any(k => k.Contains("conflict")));
    }

    [TestMethod]
    public async Task DeletionsUseHideMarkersAndRecoveryInsteadOfDestroyingVersions()
    {
        await using var h = new Harness(filesOnDemand: false);
        h.Store.Seed("CloudInlet/local-delete.txt", "one");
        h.Store.Seed("CloudInlet/cloud-delete.txt", "two");
        await h.Engine.SyncNowAsync();
        File.Delete(h.Path("local-delete.txt"));
        await h.Engine.SyncNowAsync();
        CollectionAssert.Contains(h.Store.Hidden, "CloudInlet/local-delete.txt");
        h.Store.Remove("CloudInlet/cloud-delete.txt");
        await h.Engine.SyncNowAsync();
        Assert.IsFalse(File.Exists(h.Path("cloud-delete.txt")));
        var recovered = Directory.GetFiles(h.Recovery, "cloud-delete.txt", SearchOption.AllDirectories);
        Assert.AreEqual(1, recovered.Length);
        Assert.AreEqual("two", await File.ReadAllTextAsync(recovered[0]));
    }

    [TestMethod]
    public async Task MissingRootAndInterruptedRemoteListingCannotPropagateDeletions()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("safe.txt"), "safe");
        await h.Engine.SyncNowAsync();
        Directory.Move(h.Root, h.Root + "-disconnected");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Store.Hidden.Count);
        Directory.Move(h.Root + "-disconnected", h.Root);
        File.Delete(h.Path("safe.txt"));
        h.Store.FailList = true;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Store.Hidden.Count);
    }

    [TestMethod]
    public async Task BulkDeletionsRequireExplicitApproval()
    {
        await using var h = new Harness();
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(h.Path($"file-{i}.txt"), "content");
        await h.Engine.SyncNowAsync();
        foreach (var file in Directory.GetFiles(h.Root)) File.Delete(file);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual(12, h.Snapshot.Pending);
        Assert.AreEqual(0, h.Store.Hidden.Count);
        h.Engine.ApproveDeletions();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(12, h.Store.Hidden.Count);
    }

    [TestMethod]
    public async Task ExcludedFilesStayLocalAndCaseCollisionsBlockAllWrites()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("node_modules"));
        h.Engine.Configure(new AppSettings { RootPath = h.Root, BucketId = "bucket", KeyId = "key", Exclusions = ["node_modules"] });
        await File.WriteAllTextAsync(h.Path("node_modules/index.js"), "excluded");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Store.Uploads);
        h.Store.Seed("CloudInlet/Case.txt", "one");
        h.Store.Seed("CloudInlet/case.txt", "two");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.IsFalse(File.Exists(h.Path("Case.txt")));
        Assert.AreEqual(0, h.Store.Hidden.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExcludingSyncedFolderRetainsCopiesAndBothBaselinesThenResumesSafely(bool guided)
    {
        await using var h = new Harness(filesOnDemand: false);
        Directory.CreateDirectory(h.Path("private/local-folder"));
        Directory.CreateDirectory(h.Path("private/cloud-folder"));
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(h.Path($"private/file-{i}.txt"), "original");
        await h.Engine.SyncNowAsync();
        var manifest = new SyncManifest(h.Database);
        var files = manifest.ReadAll();
        var folders = manifest.ReadDirectories();
        Assert.AreEqual(12, files.Count);
        Assert.AreEqual(3, folders.Count);
        var settings = new AppSettings { RootPath = h.Root, KeyId = "key", BucketId = "bucket", FilesOnDemand = false,
            SelectedExclusions = guided ? [] : [new(h.Root, "private", true)],
            GuidedExclusions = guided ? [new("**/private", ExclusionTarget.Folders, h.Root)] : [] };
        h.Engine.Configure(settings);
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("original", await File.ReadAllTextAsync(h.Path("private/file-2.txt")));
        Assert.AreEqual("original", h.Store.Text("CloudInlet/private/file-2.txt"));
        File.Delete(h.Path("private/file-0.txt"));
        h.Store.Remove("CloudInlet/private/file-1.txt");
        await File.WriteAllTextAsync(h.Path("private/file-2.txt"), "edited while excluded");
        Directory.Delete(h.Path("private/local-folder"));
        h.Store.Remove("CloudInlet/private/cloud-folder/");
        var uploads = h.Store.Uploads;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State, "Excluded baseline entries must not trigger deletion review.");
        Assert.AreEqual(0, h.Store.Hidden.Count);
        Assert.AreEqual(uploads, h.Store.Uploads);
        Assert.IsTrue(h.Store.Keys.Contains("CloudInlet/private/file-0.txt"));
        Assert.AreEqual("original", await File.ReadAllTextAsync(h.Path("private/file-1.txt")));
        Assert.AreEqual("original", h.Store.Text("CloudInlet/private/file-2.txt"));
        Assert.IsTrue(h.Store.Keys.Contains("CloudInlet/private/local-folder/"));
        Assert.IsTrue(Directory.Exists(h.Path("private/cloud-folder")));
        CollectionAssert.AreEquivalent(files.Values.ToArray(), manifest.ReadAll().Values.ToArray(), "Excluding keeps the durable file baseline intact.");
        CollectionAssert.AreEquivalent(folders.Values.ToArray(), manifest.ReadDirectories().Values.ToArray(), "Excluding keeps the durable directory baseline intact.");
        h.Engine.Configure(settings with { SelectedExclusions = [], GuidedExclusions = [] });
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        CollectionAssert.Contains(h.Store.Hidden, "CloudInlet/private/file-0.txt");
        CollectionAssert.Contains(h.Store.Hidden, "CloudInlet/private/local-folder/");
        Assert.IsFalse(File.Exists(h.Path("private/file-1.txt")));
        Assert.AreEqual("original", await File.ReadAllTextAsync(Directory.GetFiles(h.Recovery, "file-1.txt", SearchOption.AllDirectories).Single()));
        Assert.AreEqual("edited while excluded", h.Store.Text("CloudInlet/private/file-2.txt"));
        Assert.IsFalse(Directory.Exists(h.Path("private/cloud-folder")));
        Assert.AreEqual(10, manifest.ReadAll().Count);
        Assert.AreEqual(1, manifest.ReadDirectories().Count);
    }

    [TestMethod]
    public async Task DisabledLegacyAndFileTargetRulesDoNotPruneSameNamedFolders()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("folder.tmp"));
        await File.WriteAllTextAsync(h.Path("folder.tmp/keep.txt"), "folder content");
        await File.WriteAllTextAsync(h.Path("~$document.docx"), "legacy disabled");
        await File.WriteAllTextAsync(h.Path("file.tmp"), "excluded file");
        h.Engine.Configure(new() { RootPath = h.Root, BucketId = "bucket", KeyId = "key",
            DisabledLegacyExclusions = ["~$*"], GuidedExclusions = [new("*.tmp", ExclusionTarget.Files)] });
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("folder content", h.Store.Text("CloudInlet/folder.tmp/keep.txt"));
        Assert.AreEqual("legacy disabled", h.Store.Text("CloudInlet/~$document.docx"));
        Assert.IsTrue(h.Store.Keys.Contains("CloudInlet/folder.tmp/"));
        Assert.IsFalse(h.Store.Keys.Contains("CloudInlet/file.tmp"));
        Assert.IsTrue(File.Exists(h.Path("file.tmp")));
    }

    [TestMethod]
    public async Task NewlySavedExclusionCancelsOldSnapshotBeforeDeletionCanReachCloud()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("keep.txt"), "retained cloud copy");
        await h.Engine.SyncNowAsync();
        File.Delete(h.Path("keep.txt"));
        h.Store.ListStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.ContinueListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var oldCycle = h.Engine.SyncNowAsync();
        await h.Store.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Engine.Configure(new() { RootPath = h.Root, BucketId = "bucket", KeyId = "key", SelectedExclusions = [new(h.Root, "keep.txt", false)] });
        await oldCycle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, h.Store.Hidden.Count);
        Assert.IsTrue(h.Store.Keys.Contains("CloudInlet/keep.txt"));
        h.Store.ContinueListing = null; h.Store.ListStarted = null;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
        Assert.AreEqual(0, h.Store.Hidden.Count);
        Assert.IsTrue(new SyncManifest(h.Database).ReadAll().ContainsKey("keep.txt"));
    }

    [TestMethod]
    public async Task PauseDefersLocalChangesUntilResume()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("wait.txt"), "queued");
        h.Engine.Pause();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(0, h.Store.Uploads);
        Assert.AreEqual(ClientState.Paused, h.Snapshot.State);
        h.Engine.Resume();
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(1, h.Store.Uploads);
    }

    [TestMethod]
    public async Task ExistingDifferentLocalFileIsPreservedBeforeFirstSync()
    {
        await using var h = new Harness(filesOnDemand: false);
        await File.WriteAllTextAsync(h.Path("same.txt"), "local first");
        h.Store.Seed("CloudInlet/same.txt", "cloud first");
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("cloud first", await File.ReadAllTextAsync(h.Path("same.txt")));
        Assert.AreEqual("local first", await File.ReadAllTextAsync(Directory.GetFiles(h.Root, "same (conflict *).txt").Single()));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet.Engine.Tests", Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public string Database => System.IO.Path.Combine(_directory, "state.sqlite");
        public string Recovery => System.IO.Path.Combine(_directory, "Recovery");
        public MemoryCloud Store { get; } = new();
        public MemoryPlaceholders Placeholders { get; } = new();
        public SyncEngine Engine { get; }
        public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "");
        public Harness(bool filesOnDemand = true)
        {
            Directory.CreateDirectory(Root);
            Engine = new(Store, Placeholders, new SyncManifest(Database), new AppSettings
            { RootPath = Root, BucketId = "bucket", KeyId = "key", FilesOnDemand = filesOnDemand }, Recovery,
            _ => { }, status => Snapshot = status);
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class MemoryCloud : ICloudStore
    {
        private readonly Dictionary<string, (CloudObject File, byte[] Data)> _files = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        public int Uploads, Downloads;
        public bool FailList;
        public TaskCompletionSource? ListStarted;
        public Task? ContinueListing;
        public List<string> Hidden { get; } = [];
        public string[] Keys { get { lock (_gate) return _files.Keys.ToArray(); } }
        public void Seed(string key, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            lock (_gate) _files[key] = (new(Guid.NewGuid().ToString(), key, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow), bytes);
        }
        public string Text(string key) { lock (_gate) return Encoding.UTF8.GetString(_files[key].Data); }
        public void Remove(string key) { lock (_gate) _files.Remove(key); }
        public async IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (FailList) throw new IOException("Incomplete listing");
            ListStarted?.TrySetResult();
            if (ContinueListing is { } pending) await pending.WaitAsync(cancellationToken);
            CloudObject[] values; lock (_gate) values = _files.Values.Select(v => v.File).ToArray();
            foreach (var value in values) { cancellationToken.ThrowIfCancellationRequested(); yield return value; }
        }
        public async Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1, DateTimeOffset modifiedUtc,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream(); await source.CopyToAsync(memory, cancellationToken);
            var file = new CloudObject(Guid.NewGuid().ToString(), key, length, sha1, modifiedUtc);
            lock (_gate) { _files[key] = (file, memory.ToArray()); Uploads++; }
            return file;
        }
        public async Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            byte[] bytes; lock (_gate) { Downloads++; bytes = _files[file.Key].Data; }
            await destination.WriteAsync(bytes.AsMemory((int)offset, (int)(length ?? bytes.Length)), cancellationToken);
        }
        public Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default)
        { lock (_gate) { Hidden.Add(key); _files.Remove(key); } return Task.CompletedTask; }
        public Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class MemoryPlaceholders : IPlaceholderService
    {
        private readonly Dictionary<string, bool> _hydrated = new(StringComparer.OrdinalIgnoreCase);
        public bool IsPlaceholder(string fullPath) => _hydrated.ContainsKey(fullPath);
        public bool IsHydrated(string fullPath) => _hydrated.GetValueOrDefault(fullPath, true);
        public Task CreateOrUpdateAsync(string fullPath, CloudObject file, bool inSync, CancellationToken cancellationToken = default)
        {
            using (var stream = File.Create(fullPath)) stream.SetLength(file.Size);
            File.SetLastWriteTimeUtc(fullPath, file.ModifiedUtc.UtcDateTime);
            _hydrated[fullPath] = false; return Task.CompletedTask;
        }
        public Task MarkInSyncAsync(string fullPath, CloudObject file, CancellationToken cancellationToken = default)
        { _hydrated[fullPath] = true; return Task.CompletedTask; }
        public Task SetPinAsync(string fullPath, PinMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FreeSpaceAsync(string fullPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAsync(string rootPath, string accountIdentity, HydrationHandler hydrate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

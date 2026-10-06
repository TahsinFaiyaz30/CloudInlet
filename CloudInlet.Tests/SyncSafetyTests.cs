using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ActivityEvent = CloudInlet.Core.ActivityEvent;
using ActivityKind = CloudInlet.Core.ActivityKind;

namespace CloudInlet.Tests;

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

    [TestMethod]
    public async Task UnavailableFileRetainsItsBaselineWhileReadableSiblingUploadsAndRecoveryRetries()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("Documents"));
        await File.WriteAllTextAsync(h.Path("Documents/locked.txt"), "verified original");
        await File.WriteAllTextAsync(h.Path("Documents/healthy.txt"), "healthy original");
        await h.Engine.SyncNowAsync();
        var saved = h.Manifest.ReadAll()["Documents/locked.txt"];
        var updated = h.Cloud.Seed("Documents/locked.txt", "new cloud bytes");
        await File.WriteAllTextAsync(h.Path("Documents/healthy.txt"), "new healthy local bytes");
        h.Placeholders.Inspect = path =>
        {
            if (path == h.Path("Documents/locked.txt")) throw new UnauthorizedAccessException("Generated file inspection denial.");
        };
        var uploads = h.Cloud.Uploads; var downloads = h.Cloud.Downloads;
        h.ClearObservations();

        await h.Engine.SyncNowAsync();

        Assert.AreEqual("new healthy local bytes", h.Cloud.Text("Documents/healthy.txt"));
        Assert.AreEqual(uploads + 1, h.Cloud.Uploads, "A denied file must not stop a readable sibling or reupload its own bytes.");
        Assert.AreEqual(downloads, h.Cloud.Downloads);
        Assert.AreEqual(saved, h.Manifest.ReadAll()["Documents/locked.txt"], "The last verified version must survive an unavailable local inspection.");
        Assert.AreEqual("verified original", await File.ReadAllTextAsync(h.Path("Documents/locked.txt")));
        Assert.IsFalse(h.Placeholders.Marked.Contains(h.Path("Documents/locked.txt")));
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        var errors = h.History.Where(item => item.Kind == ActivityKind.Error && item.Path == "Documents/locked.txt").ToArray();
        Assert.AreEqual(1, errors.Length, "The final scan must retain Attention without duplicating the same error.");
        StringAssert.Contains(errors[0].Message, "permissions");

        h.Placeholders.Inspect = null;
        await h.Engine.SyncNowAsync();
        Assert.AreEqual(updated.FileId, h.Manifest.ReadAll()["Documents/locked.txt"].Remote.FileId);
        Assert.AreEqual("new cloud bytes", await File.ReadAllTextAsync(h.Path("Documents/locked.txt")));
        Assert.AreEqual(ClientState.UpToDate, h.Snapshot.State);
    }

    [TestMethod]
    public async Task UnavailableFileAndItsParentMarkersNeverBecomeDeletionCandidates()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("Documents/Nested"));
        await File.WriteAllTextAsync(h.Path("Documents/Nested/locked.txt"), "keep local");
        await File.WriteAllTextAsync(h.Path("Documents/Nested/healthy.txt"), "healthy");
        await h.Engine.SyncNowAsync();
        var file = h.Manifest.ReadAll()["Documents/Nested/locked.txt"];
        var directories = h.Manifest.ReadDirectories();
        h.Cloud.Remove("Documents/Nested/locked.txt");
        h.Cloud.Remove("Documents/Nested/");
        h.Cloud.Remove("Documents/");
        await File.WriteAllTextAsync(h.Path("Documents/Nested/healthy.txt"), "healthy changed while another file is unavailable");
        h.Placeholders.Inspect = path =>
        {
            if (path == h.Path("Documents/Nested/locked.txt")) throw new IOException("Generated sharing violation.");
        };
        h.ClearObservations();

        await h.Engine.SyncNowAsync();

        Assert.AreEqual(file, h.Manifest.ReadAll()["Documents/Nested/locked.txt"]);
        Assert.AreEqual(directories["Documents"], h.Manifest.ReadDirectories()["Documents"]);
        Assert.AreEqual(directories["Documents/Nested"], h.Manifest.ReadDirectories()["Documents/Nested"]);
        Assert.AreEqual("keep local", await File.ReadAllTextAsync(h.Path("Documents/Nested/locked.txt")));
        Assert.AreEqual("healthy changed while another file is unavailable", h.Cloud.Text("Documents/Nested/healthy.txt"));
        Assert.IsFalse(h.History.Any(item => item.Kind == ActivityKind.Delete));
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
    }

    [TestMethod]
    public async Task UnavailableFilesDoNotTriggerBulkDeletionReviewOrStopHealthyUploads()
    {
        await using var h = new Harness();
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(h.Path($"locked-{i}.txt"), "retained");
        await h.Engine.SyncNowAsync();
        var saved = h.Manifest.ReadAll();
        for (var i = 0; i < 12; i++) h.Cloud.Remove($"locked-{i}.txt");
        await File.WriteAllTextAsync(h.Path("healthy.txt"), "healthy upload");
        h.Placeholders.Inspect = path =>
        {
            if (System.IO.Path.GetFileName(path).StartsWith("locked-", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Generated blocked file.");
        };

        await h.Engine.SyncNowAsync();

        Assert.AreEqual("healthy upload", h.Cloud.Text("healthy.txt"));
        foreach (var entry in saved) Assert.AreEqual(entry.Value, h.Manifest.ReadAll()[entry.Key]);
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        Assert.AreEqual(0, h.Snapshot.Pending, "An unreadable file is not an approved or pending deletion.");
        Assert.IsFalse(h.Snapshot.Message.Contains("Review required", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FileThatBecomesUnavailableOnlyDuringFinalScanKeepsAttention()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("locked.txt"), "verified file");
        await File.WriteAllTextAsync(h.Path("healthy.txt"), "healthy original");
        await h.Engine.SyncNowAsync();
        var saved = h.Manifest.ReadAll()["locked.txt"];
        await File.WriteAllTextAsync(h.Path("healthy.txt"), "completed healthy upload");
        h.Placeholders.AfterMark = _ => h.Placeholders.Inspect = path =>
        {
            if (path == h.Path("locked.txt")) throw new UnauthorizedAccessException("Generated post-transfer scan denial.");
        };
        h.ClearObservations();

        await h.Engine.SyncNowAsync();

        Assert.AreEqual("completed healthy upload", h.Cloud.Text("healthy.txt"));
        Assert.AreEqual(saved, h.Manifest.ReadAll()["locked.txt"]);
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State, "A successful transfer must not hide a failure found by the final scan.");
        Assert.IsTrue(h.History.Any(item => item.Kind == ActivityKind.Error && item.Path == "locked.txt"));
    }

    [TestMethod]
    public async Task MissingRootAbortsBeforeAnyCloudMutation()
    {
        await using var h = new Harness();
        await File.WriteAllTextAsync(h.Path("keep.txt"), "cloud baseline");
        await h.Engine.SyncNowAsync();
        var saved = h.Manifest.ReadAll()["keep.txt"];
        Directory.Delete(h.Root, recursive: true);
        var uploads = h.Cloud.Uploads; var downloads = h.Cloud.Downloads;

        await h.Engine.SyncNowAsync();

        Assert.AreEqual(saved, h.Manifest.ReadAll()["keep.txt"]);
        Assert.AreEqual(uploads, h.Cloud.Uploads);
        Assert.AreEqual(downloads, h.Cloud.Downloads);
        Assert.AreEqual(0, h.Cloud.Hidden.Count);
        Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
        StringAssert.Contains(h.Snapshot.Message, "No deletions");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RealDeniedSubtreeRetainsCloudHistoryAndHealthySiblingSyncsWithoutChangingPermissions()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("This scenario requires Windows NTFS permissions.");
        await using var h = new Harness();
        var deniedPath = h.Path("Documents/Private");
        Directory.CreateDirectory(deniedPath);
        await File.WriteAllTextAsync(h.Path("Documents/Private/missing.txt"), "original missing");
        await File.WriteAllTextAsync(h.Path("Documents/Private/changed.txt"), "original local");
        await File.WriteAllTextAsync(h.Path("Documents/healthy.txt"), "healthy original");
        await h.Engine.SyncNowAsync();
        var saved = h.Manifest.ReadAll(); var savedDirectories = h.Manifest.ReadDirectories();
        File.Delete(h.Path("Documents/Private/missing.txt"));
        h.Cloud.Seed("Documents/Private/changed.txt", "new cloud version while inaccessible");
        h.Cloud.Seed("Documents/Private/new.txt", "new file must not be created through denied scan");
        h.Cloud.Remove("Documents/Private/");
        h.Cloud.Remove("Documents/");
        await File.WriteAllTextAsync(h.Path("Documents/healthy.txt"), "healthy sibling uploaded");
        var directory = new DirectoryInfo(deniedPath);
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var denied = new DirectorySecurity();
        denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ListDirectory, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            Assert.ThrowsException<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(deniedPath),
                "The fixture must cause a real directory enumeration denial.");
            var deniedDescriptor = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            var downloads = h.Cloud.Downloads;
            h.ClearObservations();

            await h.Engine.SyncNowAsync();

            Assert.AreEqual("healthy sibling uploaded", h.Cloud.Text("Documents/healthy.txt"));
            Assert.AreEqual(saved["Documents/Private/missing.txt"], h.Manifest.ReadAll()["Documents/Private/missing.txt"]);
            Assert.AreEqual(saved["Documents/Private/changed.txt"], h.Manifest.ReadAll()["Documents/Private/changed.txt"]);
            Assert.AreEqual(savedDirectories["Documents"], h.Manifest.ReadDirectories()["Documents"]);
            Assert.AreEqual(savedDirectories["Documents/Private"], h.Manifest.ReadDirectories()["Documents/Private"]);
            Assert.AreEqual(downloads, h.Cloud.Downloads);
            Assert.AreEqual(0, h.Cloud.Hidden.Count);
            Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
            Assert.IsTrue(h.History.Any(item => item.Kind == ActivityKind.Error && item.Path == "Documents/Private"));
            CollectionAssert.AreEqual(deniedDescriptor, directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(),
                "CloudInlet must never repair user ACLs to make a backup appear successful.");
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restored);
        }
        Assert.IsFalse(File.Exists(h.Path("Documents/Private/new.txt")));
        Assert.AreEqual("original local", await File.ReadAllTextAsync(h.Path("Documents/Private/changed.txt")));
        Assert.IsTrue(h.Cloud.Contains("Documents/Private/missing.txt"));
        await h.Engine.SyncNowAsync();
        Assert.AreEqual("new cloud version while inaccessible", await File.ReadAllTextAsync(h.Path("Documents/Private/changed.txt")));
        Assert.AreEqual("new file must not be created through denied scan", await File.ReadAllTextAsync(h.Path("Documents/Private/new.txt")));
        Assert.IsFalse(h.Manifest.ReadAll().ContainsKey("Documents/Private/missing.txt"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task OrdinaryJunctionIsBlockedWithoutFollowingTargetAndHealthySiblingStillUploads()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("This scenario requires Windows junctions.");
        await using var h = new Harness();
        Directory.CreateDirectory(h.Path("Blocked"));
        await File.WriteAllTextAsync(h.Path("Blocked/keep.txt"), "verified baseline");
        await File.WriteAllTextAsync(h.Path("healthy.txt"), "healthy baseline");
        await h.Engine.SyncNowAsync();
        var saved = h.Manifest.ReadAll()["Blocked/keep.txt"];
        var savedDirectory = h.Manifest.ReadDirectories()["Blocked"];
        var outside = h.OutsidePath("junction-target");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(System.IO.Path.Combine(outside, "keep.txt"), "outside target must remain unchanged");
        Directory.Delete(h.Path("Blocked"), recursive: true);
        await CreateJunctionAsync(h.Path("Blocked"), outside);
        try
        {
            h.Cloud.Remove("Blocked/keep.txt"); h.Cloud.Remove("Blocked/");
            h.Cloud.Seed("Blocked/new.txt", "never download through a link");
            await File.WriteAllTextAsync(h.Path("healthy.txt"), "healthy changed");
            var downloads = h.Cloud.Downloads;

            await h.Engine.SyncNowAsync();

            Assert.AreEqual("healthy changed", h.Cloud.Text("healthy.txt"));
            Assert.AreEqual(saved, h.Manifest.ReadAll()["Blocked/keep.txt"]);
            Assert.AreEqual(savedDirectory, h.Manifest.ReadDirectories()["Blocked"]);
            Assert.AreEqual(downloads, h.Cloud.Downloads);
            Assert.AreEqual(0, h.Cloud.Hidden.Count);
            Assert.AreEqual(ClientState.Attention, h.Snapshot.State);
            Assert.AreEqual("outside target must remain unchanged", await File.ReadAllTextAsync(System.IO.Path.Combine(outside, "keep.txt")));
            Assert.IsFalse(File.Exists(System.IO.Path.Combine(outside, "new.txt")));
            Assert.IsTrue(h.History.Any(item => item.Kind == ActivityKind.Error && item.Path == "Blocked"));
        }
        finally { Directory.Delete(h.Path("Blocked"), recursive: false); }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the generated junction fixture.");
        var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        Assert.AreEqual(0, process.ExitCode, await error);
        Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet.Safety.Tests", Guid.NewGuid().ToString("N"));
        public string Root => System.IO.Path.Combine(_directory, "Root");
        public FakeCloud Cloud { get; } = new();
        public FakePlaceholders Placeholders { get; } = new();
        public SyncManifest Manifest { get; }
        public SyncEngine Engine { get; }
        public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "");
        public List<ActivityEvent> History { get; } = [];
        public Harness()
        {
            Directory.CreateDirectory(Root);
            Manifest = new(System.IO.Path.Combine(_directory, "state.sqlite"));
            Engine = new(Cloud, Placeholders, Manifest, new AppSettings
                { RootPath = Root, KeyId = "key", BucketId = "bucket", FilesOnDemand = false },
                System.IO.Path.Combine(_directory, "Recovery"), item => { lock (History) History.Add(item); }, state => Snapshot = state);
        }
        public string Path(string name) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, name));
        public string OutsidePath(string name) => System.IO.Path.Combine(_directory, name);
        public void ClearObservations() { History.Clear(); Placeholders.Marked.Clear(); }
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
        public Action<string>? Inspect;
        public List<string> Marked { get; } = [];
        public bool IsPlaceholder(string path) { Inspect?.Invoke(path); return false; }
        public bool IsHydrated(string path) => true;
        public Task MarkInSyncAsync(string path, CloudObject file, CancellationToken ct = default)
        {
            Assert.AreEqual(file.Size, new FileInfo(path).Length);
            Assert.AreEqual(file.ModifiedUtc.UtcDateTime, File.GetLastWriteTimeUtc(path), "Native marking requires the exact uploaded snapshot timestamp.");
            lock (Marked) Marked.Add(path);
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
            var file = new CloudObject(Guid.NewGuid().ToString("N"), "CloudInlet/" + name, bytes.Length,
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), modified ?? DateTimeOffset.UtcNow);
            lock (_gate) _files[file.Key] = (file, bytes);
            return file;
        }
        public string Text(string name) { lock (_gate) return Encoding.UTF8.GetString(_files["CloudInlet/" + name].Bytes); }
        public void Remove(string name) { lock (_gate) _files.Remove("CloudInlet/" + name); }
        public bool Contains(string name) { lock (_gate) return _files.ContainsKey("CloudInlet/" + name); }
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.OneDrive;
using CloudBay.Core.Notifications;
using CloudBay.Core.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class CloudTransferControllerTests
{
    [TestMethod]
    public async Task IndependentRecoveryDoesNotRequireB2OrNativeSyncAndContinuesPastAnUnavailableAccount()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var source = Path.Combine(state, "Source"); var destination = Path.Combine(state, "Destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "Durable recovery without a B2 connection");
            var localPlan = new TransferJobPlan(Guid.NewGuid().ToString("N"), LocalTransferEndpoint.ForFolder(source), LocalTransferEndpoint.ForFolder(destination),
                TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            var unavailable = localPlan with { Id = Guid.NewGuid().ToString("N"), Source = new("b2", "missing-account", "bucket", "", "Backup/", "Disconnected B2") };
            using (var journal = new TransferJobJournal(Path.Combine(state, "CloudTransfers", "jobs.sqlite"), new TestProtector()))
            { journal.Create(unavailable); journal.Create(localPlan); }
            await using var controller = new ClientController(storage, _ => Assert.Fail(), manageStartup: false, cloudPauseReason: _ => null);
            await controller.StartAsync();
            var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
            while (controller.CloudTransferJobs.Single(job => job.Plan.Id == localPlan.Id).State != TransferJobState.Completed && DateTimeOffset.UtcNow < timeout)
                await Task.Delay(20);
            Assert.AreEqual(TransferJobState.Completed, controller.CloudTransferJobs.Single(job => job.Plan.Id == localPlan.Id).State);
            Assert.AreEqual(await File.ReadAllTextAsync(Path.Combine(source, "report.txt")), await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            Assert.AreEqual(TransferJobState.Paused, controller.CloudTransferJobs.Single(job => job.Plan.Id == unavailable.Id).State);
            Assert.IsTrue(controller.Activity.Any(item => item.Path == unavailable.Id && item.Kind == ActivityKind.Error));
            Assert.IsFalse(Directory.Exists(storage.LoadSettings().RootPath), "Independent jobs must not start the native B2 root.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task PolicyPausedRecoveryAutomaticallyResumesWhenPolicyClears()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var source = Path.Combine(state, "Source"); var destination = Path.Combine(state, "Destination");
            Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "automatic policy recovery");
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), LocalTransferEndpoint.ForFolder(source), LocalTransferEndpoint.ForFolder(destination),
                TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            using (var journal = new TransferJobJournal(Path.Combine(state, "CloudTransfers", "jobs.sqlite"), new TestProtector())) journal.Create(plan);
            string? reason = "Sync paused while Battery Saver is on";
            await using var controller = new ClientController(storage, _ => Assert.Fail(), manageStartup: false, cloudPauseReason: _ => Volatile.Read(ref reason));
            await controller.StartAsync();
            var policyDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!controller.Snapshot.Message.Contains("Battery Saver", StringComparison.Ordinal) && DateTimeOffset.UtcNow < policyDeadline) await Task.Delay(20);
            Assert.AreEqual(TransferJobState.Paused, controller.CloudTransferJobs.Single().State);
            Assert.IsTrue(controller.Snapshot.Message.Contains("Battery Saver", StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(destination));
            Volatile.Write(ref reason, null);
            var transferDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (controller.CloudTransferJobs.Single().State != TransferJobState.Completed && DateTimeOffset.UtcNow < transferDeadline) await Task.Delay(20);
            Assert.AreEqual(TransferJobState.Completed, controller.CloudTransferJobs.Single().State);
            Assert.AreEqual("automatic policy recovery", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task PolicyPausedRecoveryStartsOnlyAfterPolicyClearsAndManualPauseRemainsPaused()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var source = Path.Combine(state, "Source"); var destination = Path.Combine(state, "Destination");
            Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "policy recovery");
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), LocalTransferEndpoint.ForFolder(source), LocalTransferEndpoint.ForFolder(destination),
                TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            using (var journal = new TransferJobJournal(Path.Combine(state, "CloudTransfers", "jobs.sqlite"), new TestProtector())) journal.Create(plan);
            string? reason = "Sync paused on a metered connection";
            await using var controller = new ClientController(storage, _ => Assert.Fail(), manageStartup: false, cloudPauseReason: _ => Volatile.Read(ref reason));
            await controller.StartAsync();
            Assert.AreEqual(TransferJobState.Paused, controller.CloudTransferJobs.Single().State);
            Assert.IsFalse(Directory.Exists(destination));
            await controller.PauseCloudTransferAsync(plan.Id);
            Volatile.Write(ref reason, null);
            await Task.Delay(1200);
            Assert.AreEqual(TransferJobState.Paused, controller.CloudTransferJobs.Single().State, "A per-job user pause must survive the automatic policy clearing.");
            Assert.IsFalse(Directory.Exists(destination));
            await controller.ResumeCloudTransferAsync(plan.Id);
            await controller.WaitForCloudTransferAsync(plan.Id);
            Assert.AreEqual(TransferJobState.Completed, controller.CloudTransferJobs.Single().State);
            Assert.AreEqual("policy recovery", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task PolicyPauseSurvivesRestartAndContinuesWhenTheConnectionPolicyAllowsIt()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var source = Path.Combine(state, "Source"); var destination = Path.Combine(state, "Destination");
            Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "policy survives restart");
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), LocalTransferEndpoint.ForFolder(source), LocalTransferEndpoint.ForFolder(destination),
                TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            using (var journal = new TransferJobJournal(Path.Combine(state, "CloudTransfers", "jobs.sqlite"), new TestProtector())) journal.Create(plan);
            await using (var paused = new ClientController(storage, _ => Assert.Fail(), manageStartup: false, cloudPauseReason: _ => "Metered connection"))
            {
                await paused.StartAsync();
                Assert.AreEqual(TransferJobState.Paused, paused.CloudTransferJobs.Single().State);
                CollectionAssert.AreEqual(new[] { plan.Id }, storage.LoadPolicyPausedCloudTransfers().ToArray());
            }
            await using var restored = new ClientController(storage, _ => Assert.Fail(), manageStartup: false, cloudPauseReason: _ => null);
            await restored.StartAsync();
            // Journal completion precedes the final controller/tray publication.
            using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await restored.WaitForCloudTransferAsync(plan.Id, completionTimeout.Token);
            Assert.AreEqual(TransferJobState.Completed, restored.CloudTransferJobs.Single().State);
            Assert.AreEqual("policy survives restart", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            Assert.AreEqual(0, storage.LoadPolicyPausedCloudTransfers().Count);
            Assert.AreEqual(ClientState.UpToDate, restored.Snapshot.State, "An independent completed job must feed the existing tray and completion notification state.");
            var policy = new CloudBay.Core.Notifications.NotificationPolicy(new(ClientState.NotConnected, ""), []);
            var now = DateTimeOffset.UtcNow;
            var preferences = new NotificationPreferences { Enabled = true, SyncCompleted = true, FolderChanges = true };
            policy.Observe(restored.Snapshot, restored.Activity, null, preferences, now);
            var batch = policy.Observe(restored.Snapshot, restored.Activity, null, preferences, now.AddSeconds(4));
            Assert.IsTrue(batch.Notices.Any(notice => notice.Group == CloudBay.Core.Notifications.NotificationPolicy.SyncGroup));
            Assert.IsFalse(batch.Notices.Any(notice => notice.Group == CloudBay.Core.Notifications.NotificationPolicy.FoldersGroup));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public void OneDriveRefreshCredentialsAreProtectedAndSurviveVaultRotation()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            var profile = new ClientStorage.OneDriveConnection("account", "Example user", Guid.NewGuid().ToString(), "common",
                new("SECRET_ACCESS_TOKEN", "SECRET_REFRESH_TOKEN", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"));
            storage.SaveOneDriveConnections([profile]);
            storage.SaveOneDriveConnections([profile with { Tokens = profile.Tokens with { AccessToken = "ROTATED_SECRET_ACCESS" } }]);
            var restored = storage.LoadOneDriveConnections().Single();
            Assert.AreEqual("ROTATED_SECRET_ACCESS", restored.Tokens.AccessToken);
            Assert.AreEqual(profile.Tokens.RefreshToken, restored.Tokens.RefreshToken);
            foreach (var path in Directory.GetFiles(state, "onedrive.dpapi*"))
            {
                var encoded = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                Assert.IsFalse(encoded.Contains("SECRET_ACCESS", StringComparison.Ordinal));
                Assert.IsFalse(encoded.Contains("SECRET_REFRESH", StringComparison.Ordinal));
            }
            Assert.IsFalse(File.Exists(Path.Combine(state, "onedrive.json")));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public void DamagedOneDriveVaultIsRetainedAndDoesNotBecomeAnEmptyAccountList()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            var path = Path.Combine(state, "onedrive.dpapi");
            var original = RandomNumberGenerator.GetBytes(128);
            File.WriteAllBytes(path, original);
            Assert.ThrowsException<CryptographicException>(() => storage.LoadOneDriveConnections());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(state, true); }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RestartRestoresCloudFileCountersAndProviderIdentityToExistingActivity(bool discoveryComplete)
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            storage.SaveSettings(new() { RootPath = Path.Combine(state, "Root"), StartAtSignIn = false });
            var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), new("onedrive", "microsoft-account", "drive", "folder", "Reports", "OneDrive · User"),
                new("b2", "b2-account", "bucket", "", "Archive/", "B2 · Archive"), TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            var path = Path.Combine(state, "CloudTransfers", "jobs.sqlite");
            using (var journal = new TransferJobJournal(path, new TestProtector())) journal.Create(plan);
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE transfer_jobs SET discovery_complete=$discovery,state=$state,file_count=2,total_bytes=1000,completed_files=1,transferred_bytes=700,settled_bytes=600 WHERE id=$id; " +
                    "INSERT INTO transfer_items(job_id,id,path,entry,state,bytes) VALUES($id,'remaining','report.txt',$entry,$item,100)";
                update.Parameters.AddWithValue("$id", plan.Id); update.Parameters.AddWithValue("$state", (int)TransferJobState.Running);
                update.Parameters.AddWithValue("$discovery", discoveryComplete ? 1 : 0);
                update.Parameters.AddWithValue("$item", (int)TransferItemState.Transferring);
                update.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(new TransferEntry("remaining", "report.txt", "etag", 400, DateTimeOffset.UtcNow)));
                update.ExecuteNonQuery();
            }
            await using var controller = new ClientController(storage, _ => { }, manageStartup: false);
            var job = controller.CloudTransferJobs.Single();
            Assert.AreEqual(plan.Id, job.Plan.Id);
            Assert.AreEqual(plan.Source, job.Plan.Source);
            Assert.AreEqual(plan.Destination, job.Plan.Destination);
            Assert.AreEqual(plan.Operation, job.Plan.Operation);
            Assert.AreEqual(700, job.TransferredBytes);
            Assert.AreEqual(300, job.RemainingBytes);
            Assert.AreEqual(1, job.CompletedFiles);
            Assert.AreEqual(1, job.QueuedFiles);
            Assert.AreEqual(TransferJobState.Paused, job.State);
            Assert.AreEqual(700, controller.Snapshot.TransferredBytes);
            Assert.AreEqual(1000, controller.Snapshot.TransferTotalBytes);
            Assert.AreEqual(discoveryComplete, controller.Snapshot.TransferTotalKnown,
                "Aggregate totals must stay unknown until every contributing job finishes discovery, including after restart.");
            Assert.AreEqual("report.txt", controller.Snapshot.Transfers.Single().RelativePath);
            Assert.AreEqual(TransferPhase.Paused, controller.Snapshot.Transfers.Single().Phase);
            Assert.IsFalse(Directory.Exists(storage.LoadSettings().RootPath), "Restoring cloud progress must not open or hydrate a local sync root.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task RestartRepairsStoppedCloudFolderExclusionBeforeNativeSyncCanStart()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            var root = Path.Combine(state, "Root");
            storage.SaveSettings(new() { RootPath = root, StartAtSignIn = false });
            storage.SaveCloudBackupRelinquishedRoots([new("Documents", root, "Documents", true)]);
            await using var controller = new ClientController(storage, _ => { }, manageStartup: false);
            CollectionAssert.Contains(controller.Settings.SelectedExclusions, new SelectedExclusion(root, "Documents", true));
            CollectionAssert.Contains(storage.LoadSettings().SelectedExclusions, new SelectedExclusion(root, "Documents", true));
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task MalformedStoppedCloudOwnershipBlocksNativeStartAndPreservesRecords()
    {
        var state = NewState();
        try
        {
            var storage = new ClientStorage(state);
            var root = Path.Combine(state, "Root");
            storage.SaveSettings(new() { RootPath = root, StartAtSignIn = false });
            storage.SaveCloudBackupRelinquishedRoots([new("Documents", root, "../outside", true)]);
            var original = File.ReadAllBytes(Path.Combine(state, "cloud-backup-stopped.json"));
            await using var controller = new ClientController(storage, _ => { }, manageStartup: false);
            await controller.StartAsync();
            Assert.AreEqual(ClientState.Attention, controller.Snapshot.State);
            await Assert.ThrowsExceptionAsync<IOException>(() => controller.UpdatePreferencesAsync(new() { Theme = "Dark" }));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(state, "cloud-backup-stopped.json")));
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(state, true); }
    }

    private static string NewState()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudBay-cloud-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private sealed class TestProtector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }
}

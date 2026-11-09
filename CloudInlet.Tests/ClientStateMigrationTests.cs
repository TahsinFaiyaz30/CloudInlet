using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientStateMigrationTests
{
    private static readonly IProgress<MigrationProgress> Silent = new InlineProgress(_ => { });

    [TestMethod]
    public void NativeFreshProfilesUseCanonicalLocationAndKeepFlavorsSeparate()
    {
        using var fixture = new Fixture();
        using (var lease = fixture.Lease())
        {
            var result = ClientStateMigration.Resolve(lease);
            Assert.AreEqual(lease.CanonicalDirectory, result.DataDirectory);
            Assert.IsFalse(result.RequiresRecovery);
            Assert.IsFalse(Directory.Exists(lease.LegacyDirectory));
            File.WriteAllText(Path.Combine(result.DataDirectory!, "owned.txt"), "Release");
        }
        using (var lease = fixture.Lease()) Assert.AreEqual(lease.CanonicalDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
        using (var debug = fixture.Lease("Debug"))
        {
            Assert.AreEqual(debug.CanonicalDirectory, ClientStateMigration.Resolve(debug).DataDirectory);
            Assert.IsFalse(File.Exists(Path.Combine(debug.CanonicalDirectory, "owned.txt")));
        }
    }

    [TestMethod]
    public void NativeExistingLegacyProfileRemainsUsableWithoutConsentOrRouteRewrite()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var before = Fixture.HashTree(lease.LegacyDirectory);
        Assert.AreEqual(lease.LegacyDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
        Assert.IsFalse(File.Exists(lease.RoutePath)); Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
        Assert.AreEqual(before, Fixture.HashTree(lease.LegacyDirectory));
    }

    [TestMethod]
    public void NativeUnownedCanonicalProfileIsNeverAdoptedOrReplaced()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease();
        Directory.CreateDirectory(lease.CanonicalDirectory); File.WriteAllText(Path.Combine(lease.CanonicalDirectory, "keep"), "Other data");
        Assert.ThrowsException<IOException>(() => ClientStateMigration.Resolve(lease));
        fixture.Seed(lease);
        Assert.ThrowsException<IOException>(() => ClientStateMigration.Resolve(lease));
        Assert.AreEqual("Other data", File.ReadAllText(Path.Combine(lease.CanonicalDirectory, "keep")));
    }

    [TestMethod]
    public void NativeStateUseLeasePreventsAnotherControllerOrMigration()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease();
        Assert.ThrowsException<MigrationTransientException>(() => fixture.Lease());
        using var debug = fixture.Lease("Debug");
        Assert.AreNotEqual(lease.RecoveryDirectory, debug.RecoveryDirectory);
    }

    [TestMethod]
    public void NativeFileOccupyingLegacyProfileCannotBeMistakenForFreshInstallation()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease();
        Directory.CreateDirectory(Path.GetDirectoryName(lease.LegacyDirectory)!);
        File.WriteAllText(lease.LegacyDirectory, "Preserved unexpected file");
        Assert.ThrowsException<IOException>(() => ClientStateMigration.Resolve(lease));
        Assert.AreEqual("Preserved unexpected file", File.ReadAllText(lease.LegacyDirectory));
        Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
    }

    [TestMethod]
    public async Task NativeStateMigrationPreservesEncryptedAccountsDatabaseAndCheckpointsByteForByte()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var before = Fixture.HashTree(lease.LegacyDirectory);
        var plan = fixture.Plan(lease); var journal = fixture.Journal(lease);
        using (var operation = ClientStateMigration.Create(lease, plan, "state", journal))
        using (var session = MigrationSession.Create(plan, ["state"], journal, [operation]))
        {
            Assert.IsTrue(ClientStateMigration.Resolve(lease).RequiresRecovery);
            Assert.IsNull(ClientStateMigration.Resolve(lease).DataDirectory);
            await session.RunAsync();
            Assert.IsTrue(session.IsComplete, string.Join("; ", session.Items.Select(item => item.Error)));
            Assert.IsFalse(session.CanRevert("state"));
            Assert.AreEqual(lease.CanonicalDirectory, ClientStateMigration.Resolve(lease).DataDirectory,
                "A later selected item must resolve the committed directory while this adapter remains alive.");
        }
        var resolved = ClientStateMigration.Resolve(lease);
        Assert.AreEqual(lease.CanonicalDirectory, resolved.DataDirectory);
        Assert.IsFalse(resolved.RequiresRecovery); Assert.IsFalse(Directory.Exists(lease.LegacyDirectory));
        Assert.AreEqual(before, Fixture.HashTree(lease.CanonicalDirectory));
        Assert.AreEqual("fixture-secret", new ClientStorage(lease.CanonicalDirectory).LoadCredentials()!.ApplicationKey);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NativeStateMigrationPreservesCommittedCrashLeftWal(bool retainSharedMemory)
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var path = Path.Combine(lease.LegacyDirectory, "State", "crash # percent%.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = Path.Combine(lease.RecoveryDirectory, "wal-fixture.sqlite");
        // Capture a real SQLite crash image while its sole writer is idle between
        // committed transactions. Copying the unchanged DB/WAL/SHM before closing
        // the writer preserves the exact files an abrupt process exit leaves.
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staging, Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES('committed-only-in-WAL')";
            command.ExecuteNonQuery();
            Assert.IsTrue(new FileInfo(staging + "-wal").Length > 32);
            File.Copy(staging, path); File.Copy(staging + "-wal", path + "-wal");
            if (retainSharedMemory) File.Copy(staging + "-shm", path + "-shm");
        }
        var before = Fixture.HashTree(lease.LegacyDirectory, excludeSharedMemory: true);
        var plan = fixture.Plan(lease); var journal = fixture.Journal(lease);
        using (var operation = ClientStateMigration.Create(lease, plan, "state", journal))
        using (var session = MigrationSession.Create(plan, ["state"], journal, [operation]))
        {
            await session.RunAsync();
            Assert.IsTrue(session.IsComplete, string.Join("; ", session.Items.Select(item => item.Error)));
        }
        Assert.AreEqual(before, Fixture.HashTree(lease.CanonicalDirectory, excludeSharedMemory: true), "Verification may rebuild only the SHM cache, never checkpoint or alter database/WAL content.");
        using var verified = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(lease.CanonicalDirectory, "State", "crash # percent%.sqlite"), Pooling = false }.ToString());
        verified.Open(); using var read = verified.CreateCommand(); read.CommandText = "SELECT value FROM preserved";
        Assert.AreEqual("committed-only-in-WAL", read.ExecuteScalar());
    }

    [TestMethod]
    public void NativeStateMigrationValidatesEncryptedCheckpointStoredOnlyInCrashWal()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var path = Path.Combine(lease.LegacyDirectory, "State", "invalid-checkpoint.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = Path.Combine(lease.RecoveryDirectory, "invalid-wal-fixture.sqlite");
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staging, Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE transfer_items(checkpoint BLOB); INSERT INTO transfer_items VALUES(x'010203')";
            command.ExecuteNonQuery();
            File.Copy(staging, path); File.Copy(staging + "-wal", path + "-wal");
        }
        var before = Fixture.HashTree(lease.LegacyDirectory, excludeSharedMemory: true);
        // The main DB has no transfer_items table yet. This can only fail DPAPI
        // validation if verification actually reads the committed WAL.
        Assert.ThrowsException<CryptographicException>(() => { using var unexpected = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease)); });
        Assert.AreEqual(before, Fixture.HashTree(lease.LegacyDirectory, excludeSharedMemory: true));
        Assert.IsFalse(File.Exists(lease.RoutePath));
    }

    [TestMethod]
    public async Task NativeInterruptedAfterRenameRequiresRecoveryAndResumesOriginalDirectory()
    {
        using var fixture = new Fixture(); string fingerprint;
        using (var lease = fixture.Lease())
        {
            fixture.Seed(lease); var plan = fixture.Plan(lease); fingerprint = plan.Fingerprint;
            using var operation = ClientStateMigration.Create(lease, plan, "state", fixture.Journal(lease));
            await operation.ApplyAsync(Silent, default);
        }
        using (var lease = fixture.Lease())
        {
            var result = ClientStateMigration.Resolve(lease);
            Assert.IsTrue(result.RequiresRecovery); Assert.IsNull(result.DataDirectory); Assert.AreEqual(fingerprint, result.Plan!.Fingerprint);
            using var operation = ClientStateMigration.Resume(lease);
            await operation.CheckAsync(default); await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default);
        }
        using (var lease = fixture.Lease()) Assert.AreEqual(lease.CanonicalDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
    }

    [TestMethod]
    public async Task NativeInterruptedMoveCanRestoreOnlyItsOriginalDataAndRoute()
    {
        using var fixture = new Fixture(); string before;
        using (var lease = fixture.Lease())
        {
            fixture.Seed(lease); before = Fixture.HashTree(lease.LegacyDirectory);
            using var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease));
            await operation.ApplyAsync(Silent, default);
        }
        using (var lease = fixture.Lease())
        using (var operation = ClientStateMigration.Resume(lease))
        {
            await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
            Assert.IsTrue(operation.CanRevert, "Revert remains idempotent until the outer session saves its terminal state.");
        }
        using (var lease = fixture.Lease())
        {
            Assert.AreEqual(lease.LegacyDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
            Assert.AreEqual(before, Fixture.HashTree(lease.LegacyDirectory));
            Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
        }
    }

    [TestMethod]
    public void NativeActiveUpdateWorkerDefersMigrationWithoutMovingOrSavingIntent()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var updates = Path.Combine(lease.LegacyDirectory, "Updates"); Directory.CreateDirectory(updates);
        using var worker = new FileStream(Path.Combine(updates, "update-install.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsException<MigrationTransientException>(() => ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease)));
        Assert.IsFalse(File.Exists(lease.RoutePath)); Assert.IsTrue(Directory.Exists(lease.LegacyDirectory));
    }

    [TestMethod]
    public async Task NativeWorkerBecomingActiveAfterReviewBlocksApplyThenAllowsRetry()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var updates = Path.Combine(lease.LegacyDirectory, "Updates"); Directory.CreateDirectory(updates);
        using var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease));
        using (var worker = new FileStream(Path.Combine(updates, "update-install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsExceptionAsync<MigrationTransientException>(() => operation.ApplyAsync(Silent, default));
        await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default);
        Assert.IsTrue(Directory.Exists(lease.CanonicalDirectory));
    }

    [TestMethod]
    public void NativeInvalidCredentialVaultNeverMovesOrRewritesState()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        File.WriteAllBytes(Path.Combine(lease.LegacyDirectory, "credentials.dpapi"), [1, 2, 3]);
        var before = Fixture.HashTree(lease.LegacyDirectory);
        Assert.ThrowsException<CryptographicException>(() => ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease)));
        Assert.AreEqual(before, Fixture.HashTree(lease.LegacyDirectory)); Assert.IsFalse(File.Exists(lease.RoutePath));
    }

    [TestMethod]
    public void NativeBrokenDatabaseIsPreservedAndBlocksTheMove()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        File.WriteAllText(Path.Combine(lease.LegacyDirectory, "CloudTransfers", "jobs.sqlite"), "broken database");
        var before = Fixture.HashTree(lease.LegacyDirectory);
        Assert.ThrowsException<SqliteException>(() => ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease)));
        Assert.AreEqual(before, Fixture.HashTree(lease.LegacyDirectory)); Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
    }

    [TestMethod]
    public async Task NativeChangedRecoveryBytesFailVerificationAndCanBeSafelyReturned()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        using var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease));
        await operation.ApplyAsync(Silent, default);
        File.AppendAllText(Path.Combine(lease.CanonicalDirectory, "recovery.bin"), "Externally changed");
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.VerifyAsync(default));
        Assert.IsTrue(ClientStateMigration.Resolve(lease).RequiresRecovery);
        await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        StringAssert.EndsWith(File.ReadAllText(Path.Combine(lease.LegacyDirectory, "recovery.bin")), "Externally changed");
    }

    [TestMethod]
    public void NativeRouteCorruptionFailsClosedWithoutInitializingEitherProfile()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        File.WriteAllBytes(lease.RoutePath, [1, 2, 3]);
        Assert.ThrowsException<CryptographicException>(() => ClientStateMigration.Resolve(lease));
        Assert.IsTrue(Directory.Exists(lease.LegacyDirectory)); Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
    }

    [TestMethod]
    public void NativeWrongFlavorOrArbitraryReviewedPathsCannotBeMigrated()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var item = fixture.Plan(lease).Items.Single();
        var plan = MigrationPlan.Create([item with { ProposedLocation = lease.CanonicalDirectory + "-other" }]);
        Assert.ThrowsException<InvalidDataException>(() => ClientStateMigration.Create(lease, plan, "state", fixture.Journal(lease)));
        Assert.ThrowsException<ArgumentException>(() => fixture.Lease("Other"));
    }

    [TestMethod]
    public async Task NativeCompletedRouteRejectsReplacementDirectoryEvenWithSameBytes()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        using (var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease)))
        { await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default); }
        Directory.Move(lease.CanonicalDirectory, lease.CanonicalDirectory + "-preserved");
        Directory.CreateDirectory(lease.CanonicalDirectory);
        Assert.ThrowsException<IOException>(() => ClientStateMigration.Resolve(lease));
        Assert.IsTrue(Directory.Exists(lease.CanonicalDirectory + "-preserved"));
    }

    [TestMethod]
    public async Task NativeCommittedRouteReconcilesInterruptedOuterVerificationWithoutReverting()
    {
        using var fixture = new Fixture();
        using (var lease = fixture.Lease())
        {
            fixture.Seed(lease);
            using var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease));
            await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default);
        }
        using (var lease = fixture.Lease())
        using (var operation = ClientStateMigration.Resume(lease))
        {
            await operation.CheckAsync(default); await operation.ApplyAsync(Silent, default); await operation.VerifyAsync(default);
            Assert.IsFalse(operation.CanRevert);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => operation.RevertAsync(Silent, default));
            Assert.IsFalse(Directory.Exists(lease.LegacyDirectory));
        }
    }

    [TestMethod]
    public async Task NativeRestoredRouteReconcilesInterruptedOuterRevertWithoutMovingAgain()
    {
        using var fixture = new Fixture();
        using (var lease = fixture.Lease())
        {
            fixture.Seed(lease);
            using var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease));
            await operation.ApplyAsync(Silent, default); await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
        }
        using (var lease = fixture.Lease())
        using (var operation = ClientStateMigration.Resume(lease))
        {
            Assert.IsTrue(operation.CanRevert);
            await operation.RevertAsync(Silent, default); await operation.VerifyRevertedAsync(default);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => operation.ApplyAsync(Silent, default));
            Assert.IsTrue(Directory.Exists(lease.LegacyDirectory)); Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
        }
    }

    [TestMethod]
    public void NativeExistingConsentJournalMustMatchTheExactReviewedPlan()
    {
        using var fixture = new Fixture(); using var lease = fixture.Lease(); fixture.Seed(lease);
        var plan = fixture.Plan(lease); var journal = fixture.Journal(lease);
        File.WriteAllBytes(journal, JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Plan = plan, Items = new[] { new { Id = "wrong", State = "Pending" } } }));
        Assert.ThrowsException<InvalidDataException>(() => ClientStateMigration.Create(lease, plan, "state", journal));
        Assert.IsFalse(File.Exists(lease.RoutePath));
        File.WriteAllBytes(journal, JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Plan = plan, Items = new[] { new { Id = "state", State = "Pending" } } }));
        using var operation = ClientStateMigration.Create(lease, plan, "state", journal);
        Assert.IsTrue(ClientStateMigration.Resolve(lease).RequiresRecovery);
    }

    [TestMethod]
    public async Task NativeLockedRouteCommitLeavesRecoverableOriginalFolderAtDestination()
    {
        using var fixture = new Fixture();
        using (var lease = fixture.Lease())
        {
            fixture.Seed(lease);
            using var operation = ClientStateMigration.Create(lease, fixture.Plan(lease), "state", fixture.Journal(lease));
            await operation.ApplyAsync(Silent, default);
            using (var locked = new FileStream(lease.RoutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => operation.VerifyAsync(default));
            Assert.IsTrue(ClientStateMigration.Resolve(lease).RequiresRecovery);
            Assert.IsFalse(Directory.Exists(lease.LegacyDirectory)); Assert.IsTrue(Directory.Exists(lease.CanonicalDirectory));
        }
        using (var lease = fixture.Lease())
        using (var operation = ClientStateMigration.Resume(lease)) await operation.VerifyAsync(default);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CloudInlet.StateMigrationTests", Guid.NewGuid().ToString("N"));
        internal ClientStateMigration.StateUseLease Lease(string flavor = "Release") => ClientStateMigration.AcquireStateUseLease(_root, flavor);
        internal string Journal(ClientStateMigration.StateUseLease lease) => Path.Combine(lease.RecoveryDirectory, Guid.NewGuid().ToString("N") + ".json");
        internal MigrationPlan Plan(ClientStateMigration.StateUseLease lease) => MigrationPlan.Create([
            new("state", MigrationArea.ApplicationState, "Application data", lease.LegacyDirectory, lease.CanonicalDirectory, "Move saved accounts and recovery data together.")]);
        internal void Seed(ClientStateMigration.StateUseLease lease)
        {
            var storage = new ClientStorage(lease.LegacyDirectory);
            storage.SaveSettings(new AppSettings { Theme = "Dark", RootPath = Path.Combine(_root, "Personal", "CloudBay") });
            storage.SaveCredentials(new("fixture-key", "fixture-secret"));
            File.WriteAllText(Path.Combine(lease.LegacyDirectory, "recovery.bin"), "Checkpoint bytes must remain identical");
            var path = Path.Combine(lease.LegacyDirectory, "CloudTransfers", "jobs.sqlite"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "CREATE TABLE transfer_items(checkpoint BLOB); INSERT INTO transfer_items VALUES($checkpoint)";
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new { Provider = "b2", SessionId = "fixture-session", AcknowledgedBytes = 123L });
            command.Parameters.AddWithValue("$checkpoint", ProtectedData.Protect(plaintext, "CloudBay.TransferCheckpoint.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
            command.ExecuteNonQuery();
        }
        internal static string HashTree(string directory, bool excludeSharedMemory = false) => string.Join("|", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("update-install.lock", StringComparison.OrdinalIgnoreCase))
            .Where(path => !excludeSharedMemory || !path.EndsWith(".sqlite-shm", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Order(StringComparer.Ordinal));
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
    private sealed class InlineProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress> { public void Report(MigrationProgress value) => report(value); }
}

using System.Security.Cryptography;
using System.Text;
using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class InstallerProgressJournalTests
{
    [TestMethod]
    public void NativeVerifiedRecoveryWindowHandoffSurvivesLaterProgressAndTerminalReport()
    {
        using var fixture = new Fixture(); using var writer = fixture.Create();
        writer.Report("InstallerRunning", "In-place update", migrationRequested: true);
        Assert.IsFalse(fixture.Read()!.RecoveryWindowShown);
        writer.Report("InstallerRunning", "Verified recovery window is ready", migrationRequested: true, recoveryWindowShown: true);
        writer.Report("Complete", "Verified relocation", "Succeeded", true, true);
        Assert.IsTrue(fixture.Read()!.RecoveryWindowShown);
    }
    [TestMethod]
    public void NativeObserverReadsAtomicAuthenticatedSnapshotsWhileWriterOwnsLease()
    {
        using var fixture = new Fixture();
        using var writer = fixture.Create();
        Assert.IsNull(fixture.Read());
        writer.Report("WaitingForAppExit", "Finishing transfers", migrationRequested: true);
        var first = fixture.Read()!;
        Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual("Active", first.Status);
        Assert.IsFalse(first.MigrationCompleted);
        writer.Report("InstallerRunning", "Applying verified package", migrationRequested: true);
        Assert.AreEqual(2L, fixture.Read()!.Sequence);
        writer.Report("Complete", "Installation verified", "Succeeded", true, true);
        var final = fixture.Read()!;
        Assert.AreEqual(3L, final.Sequence);
        Assert.IsTrue(final.MigrationCompleted);
        Assert.IsFalse(final.RequiresRestart);
        Assert.AreEqual("Succeeded", final.Status);
        Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path)).Contains("Installation verified", StringComparison.Ordinal));
        Assert.ThrowsException<InvalidOperationException>(() => writer.Report("InstallerRunning", "Cannot repeat completed install"));
    }

    [TestMethod]
    public void NativeSecondWriterAndReusingFinishedRequestAreRejected()
    {
        using var fixture = new Fixture();
        using (var writer = fixture.Create())
        {
            Assert.ThrowsException<IOException>(() => fixture.Create());
            writer.Report("Failed", "Destination changed", "Failed", true);
        }
        Assert.ThrowsException<IOException>(() => fixture.Create());
        Assert.AreEqual("Failed", fixture.Read()!.Status);
    }

    [TestMethod]
    public void NativeTamperingWrongRequestOrOversizedRecordIsNotDisplayed()
    {
        using var fixture = new Fixture();
        using (var writer = fixture.Create()) writer.Report("Validating", "Identity verified");
        Assert.ThrowsException<InvalidDataException>(() => InstallerProgressJournal.Read(fixture.Path, Guid.NewGuid().ToString("N"), fixture.Hash));
        Assert.ThrowsException<InvalidDataException>(() => InstallerProgressJournal.Read(fixture.Path, fixture.Id, new string('0', 64)));
        var original = File.ReadAllBytes(fixture.Path);
        var changed = (byte[])original.Clone(); changed[changed.Length / 2] ^= 0x80;
        File.WriteAllBytes(fixture.Path, changed);
        Assert.ThrowsException<CryptographicException>(() => fixture.Read());
        File.WriteAllBytes(fixture.Path, new byte[16 * 1024 + 1]);
        Assert.ThrowsException<InvalidDataException>(() => fixture.Read());
        File.WriteAllBytes(fixture.Path, original);
        Assert.AreEqual("Validating", fixture.Read()!.Stage);
    }

    [TestMethod]
    public void NativeRecordCannotClaimIncompleteRestartAsCompletedMigration()
    {
        using var fixture = new Fixture();
        using var writer = fixture.Create();
        Assert.ThrowsException<InvalidDataException>(() => writer.Report("Complete", "bad", "Succeeded", false, true));
        Assert.ThrowsException<InvalidDataException>(() => writer.Report("RestartRequired", "bad", "RestartRequired", true, true, true));
        Assert.ThrowsException<InvalidDataException>(() => writer.Report("InstallerRunning", "bad", "Succeeded"));
        Assert.ThrowsException<InvalidDataException>(() => writer.Report("UnknownStage", "bad"));
        Assert.ThrowsException<InvalidDataException>(() => writer.Report("Validating", new string('x', 2049)));
        Assert.ThrowsException<InvalidDataException>(() => writer.Report("InstallerRunning", "No migration consent", recoveryWindowShown: true));
        writer.Report("RestartRequired", "Restart Windows to finish migration", "RestartRequired", true, false, true);
        Assert.IsFalse(fixture.Read()!.MigrationCompleted);
        Assert.IsTrue(fixture.Read()!.RequiresRestart);
    }

    [TestMethod]
    public void NativeFailedUnrelatedWorkKeepsVerifiedMigrationCompleted()
    {
        using var fixture = new Fixture();
        using var writer = fixture.Create();
        writer.Report("Failed", "Migration completed, but reopening the app failed", "Failed", true, true);
        Assert.IsTrue(fixture.Read()!.MigrationCompleted);
        Assert.ThrowsException<InvalidOperationException>(() => writer.Report("InstallerRunning", "No retry"));
    }

    [TestMethod]
    public void NativeReadonlyMissingObserverCreatesNothingAndParentCannotMoveDuringWriting()
    {
        using var fixture = new Fixture();
        var missing = System.IO.Path.Combine(fixture.Root, "missing", fixture.Id + ".progress.dpapi");
        Assert.IsNull(InstallerProgressJournal.Read(missing, fixture.Id, fixture.Hash));
        Assert.IsFalse(Directory.Exists(System.IO.Path.GetDirectoryName(missing)));
        using var writer = fixture.Create();
        Assert.ThrowsException<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
    }

    [TestMethod]
    public void NativeLinkedJournalOrLeaseCannotAlterOtherFiles()
    {
        using var fixture = new Fixture();
        var target = System.IO.Path.Combine(fixture.Root, "unrelated.txt");
        File.WriteAllText(target, "must survive");
        File.CreateSymbolicLink(fixture.Path, target);
        try
        {
            Assert.ThrowsException<IOException>(() => fixture.Create());
            Assert.ThrowsException<IOException>(() => fixture.Read());
        }
        finally { File.Delete(fixture.Path); }
        // The rejected writer may have created its empty lease before inspecting
        // the journal. Use a fresh fixture for the independent linked-lease case.
        using var leaseFixture = new Fixture();
        File.CreateSymbolicLink(leaseFixture.Path + ".lock", target);
        try { Assert.ThrowsException<IOException>(() => leaseFixture.Create()); }
        finally { File.Delete(leaseFixture.Path + ".lock"); }
        Assert.AreEqual("must survive", File.ReadAllText(target));
    }

    [TestMethod]
    public void ProgressPathMustExactlyMatchOwnedCacheAndRequestIdentity()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(fixture.Path, InstallerProgressJournal.ValidatePath(fixture.Root, fixture.Path, fixture.Id));
        foreach (var value in new[] { fixture.Path + ":stream", fixture.Path + " ", fixture.Path + ".", System.IO.Path.Combine(fixture.Root, "host", "..", fixture.Id + ".progress.dpapi"), "relative.json" })
            Assert.ThrowsException<IOException>(() => InstallerProgressJournal.ValidatePath(fixture.Root, value, fixture.Id));
        Assert.ThrowsException<IOException>(() => InstallerProgressJournal.ValidatePath(fixture.Root, fixture.Path, Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public void NativeObserverReadinessIsAtomicBoundToRequestAndRepeatable()
    {
        using var fixture = new Fixture();
        Assert.IsFalse(InstallerProgressJournal.IsObserverReady(fixture.Path, fixture.Id, fixture.Hash));
        InstallerProgressJournal.PublishObserverReady(fixture.Path, fixture.Id, fixture.Hash);
        Assert.IsTrue(InstallerProgressJournal.IsObserverReady(fixture.Path, fixture.Id, fixture.Hash));
        InstallerProgressJournal.PublishObserverReady(fixture.Path, fixture.Id, fixture.Hash);
        Assert.ThrowsException<IOException>(() => InstallerProgressJournal.PublishObserverReady(fixture.Path, fixture.Id, new string('0', 64)));
        Assert.AreEqual(fixture.Id + "\n" + fixture.Hash, File.ReadAllText(fixture.Path + ".observer-ready"));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(fixture.Path)!, "*.tmp").Length);
    }

    [TestMethod]
    public async Task NativeConcurrentReadersNeverSeePartialWrites()
    {
        using var fixture = new Fixture();
        using var writer = fixture.Create();
        var writerTask = Task.Run(() =>
        {
            for (var i = 0; i < 40; i++) writer.Report("InstallerRunning", "Applying package step " + i);
            writer.Report("Complete", "Verified", "Succeeded");
        });
        long last = 0;
        try
        {
            while (!writerTask.IsCompleted)
            {
                var record = fixture.Read();
                if (record != null) { Assert.IsTrue(record.Sequence >= last); last = record.Sequence; }
                await Task.Delay(1);
            }
        }
        finally { await writerTask; }
        Assert.AreEqual(41L, fixture.Read()!.Sequence);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet-InstallerProgress-" + Guid.NewGuid().ToString("N"));
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Hash { get; } = InstallerProgressJournal.RequestHash("request fixture"u8.ToArray());
        internal string Path => System.IO.Path.Combine(Root, "host", Id + ".progress.dpapi");
        internal Fixture() => Directory.CreateDirectory(System.IO.Path.Combine(Root, "host"));
        internal InstallerProgressJournal Create() => InstallerProgressJournal.Create(Path, Id, Hash);
        internal InstallerProgressRecord? Read() => InstallerProgressJournal.Read(Path, Id, Hash);
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Root);
            if (System.IO.Path.GetDirectoryName(full) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) ||
                !System.IO.Path.GetFileName(full).StartsWith("CloudInlet-InstallerProgress-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected progress fixture cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}

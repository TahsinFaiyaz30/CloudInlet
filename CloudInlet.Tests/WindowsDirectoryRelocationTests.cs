using System.Security.AccessControl;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CloudInlet.Core.Migration;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Tests;

[TestClass]
public sealed class WindowsDirectoryRelocationTests
{
    private static readonly IProgress<MigrationProgress> Silent = new InlineProgress(_ => { });
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeRenameAndRevertPreservePayloadStreamsSecurityAndTimes()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Source, "private.bin");
        var bytes = RandomNumberGenerator.GetBytes(1027);
        File.WriteAllBytes(file, bytes);
        File.WriteAllText(file + ":fixture", "Named stream stays on the original file");
        var time = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(file, time);
        var security = new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var operation = fixture.Create();
        await operation.ApplyAsync(Silent, default);
        await operation.VerifyAsync(default);
        Assert.IsFalse(Directory.Exists(fixture.Source));
        Check(Path.Combine(fixture.Destination, "private.bin"));
        await operation.ApplyAsync(Silent, default); // Verified location is idempotent, not a second move.
        await operation.RevertAsync(Silent, default);
        await operation.VerifyRevertedAsync(default);
        await operation.RevertAsync(Silent, default);
        Assert.IsFalse(Directory.Exists(fixture.Destination));
        Check(file);

        void Check(string path)
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Assert.AreEqual("Named stream stays on the original file", File.ReadAllText(path + ":fixture"));
            Assert.AreEqual(time, File.GetLastWriteTimeUtc(path));
            Assert.AreEqual(security, new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
    }

    [TestMethod]
    public async Task NativeResumeAfterRenameFindsTheOriginalObjectWithoutReplayingOrDeletingFiles()
    {
        using var fixture = new Fixture();
        using (var operation = fixture.Create()) await operation.ApplyAsync(Silent, default);
        using var resumed = fixture.Resume();
        await resumed.CheckAsync(default);
        await resumed.ApplyAsync(Silent, default);
        await resumed.VerifyAsync(default);
        Assert.AreEqual(Fixture.Contents, File.ReadAllText(Path.Combine(fixture.Destination, "settings.json")));
        Assert.IsFalse(Directory.Exists(fixture.Source));
    }

    [TestMethod]
    public async Task NativeResumeBeforeRenameCanStillRestoreWithoutMovingAnything()
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        using var resumed = fixture.Resume();
        await resumed.RevertAsync(Silent, default);
        await resumed.VerifyRevertedAsync(default);
        fixture.AssertOriginal();
    }

    [TestMethod]
    public async Task NativeDestinationAppearingAfterReviewIsNeverMergedOrReplaced()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        Directory.CreateDirectory(fixture.Destination);
        File.WriteAllText(Path.Combine(fixture.Destination, "keep.txt"), "Other directory");
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        fixture.AssertOriginal();
        Assert.AreEqual("Other directory", File.ReadAllText(Path.Combine(fixture.Destination, "keep.txt")));
    }

    [TestMethod]
    public async Task NativeOriginalLocationRecreatedAfterMoveBlocksRevert()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        await operation.ApplyAsync(Silent, default);
        Directory.CreateDirectory(fixture.Source);
        File.WriteAllText(Path.Combine(fixture.Source, "keep.txt"), "New source directory");
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.RevertAsync(Silent, default));
        Assert.AreEqual("New source directory", File.ReadAllText(Path.Combine(fixture.Source, "keep.txt")));
        Assert.AreEqual(Fixture.Contents, File.ReadAllText(Path.Combine(fixture.Destination, "settings.json")));
    }

    [TestMethod]
    public async Task NativeIdenticalLookingReplacementDirectoryCannotPassTheSavedIdentity()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        var preserved = fixture.Source + "-original";
        Directory.Move(fixture.Source, preserved);
        Directory.CreateDirectory(fixture.Source);
        File.WriteAllText(Path.Combine(fixture.Source, "settings.json"), Fixture.Contents);
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(Silent, default));
        Assert.IsFalse(Directory.Exists(fixture.Destination));
        Assert.AreEqual(Fixture.Contents, File.ReadAllText(Path.Combine(preserved, "settings.json")));
        fixture.AssertOriginal();
    }

    [TestMethod]
    public void NativeResumeRefusesChangedParentIdentity()
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        var parent = Path.GetDirectoryName(fixture.Destination)!;
        Directory.Move(parent, parent + "-original");
        Directory.CreateDirectory(parent);
        Assert.ThrowsException<IOException>(() => fixture.Resume());
        fixture.AssertOriginal();
    }

    [TestMethod]
    public async Task NativeExclusiveOpenBecomesRecoverableAfterTheOtherProcessReleasesIt()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        using (var hold = File.OpenHandle(Path.Combine(fixture.Source, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // An open child can produce either a sharing violation or access denied for a directory rename.
            // Only the former is positively transient. Both preserve the original for an explicit retry.
            try { await operation.ApplyAsync(Silent, default); }
            catch (MigrationTransientException) { fixture.AssertOriginal(readContent: false); }
            catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
            { fixture.AssertOriginal(readContent: false); }
        }
        await operation.ApplyAsync(Silent, default);
        await operation.VerifyAsync(default);
        Assert.AreEqual(Fixture.Contents, File.ReadAllText(Path.Combine(fixture.Destination, "settings.json")));
    }

    [TestMethod]
    public async Task NativeDescendantOpenThatSharesDeleteStillBlocksDirectoryRenameUntilReleased()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        var child = Path.Combine(fixture.Source, "settings.json");
        using (var held = File.OpenHandle(child, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // A DELETE-sharing probe cannot diagnose every descendant open:
            // this real handle shares DELETE, but an ancestor rename still fails.
            using (var deletionAccess = OpenForProbe(@"\\?\" + child, 0x10080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                var probeError = deletionAccess.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                TestContext.WriteLine("Descendant DELETE-access probe error: " + probeError);
                Assert.IsFalse(deletionAccess.IsInvalid, "The existing reader explicitly permits DELETE sharing.");
            }
            using (var exclusiveRead = OpenForProbe(@"\\?\" + child, 0x81, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                var probeError = exclusiveRead.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                TestContext.WriteLine("Descendant exclusive-read probe error: " + probeError);
                Assert.IsTrue(exclusiveRead.IsInvalid);
                Assert.AreEqual(32, probeError, "An exclusive read must detect the actual reader without reading or changing its contents.");
            }
            Exception? failure = null;
            try { await operation.ApplyAsync(Silent, default); }
            catch (IOException error) { failure = error; }
            TestContext.WriteLine("Ancestor rename result while shared reader is held: " + (failure?.ToString() ?? "succeeded"));
            Assert.IsNotNull(failure, "A real open descendant must block this native directory rename before the handle is released.");
            Assert.IsTrue(failure is MigrationTransientException || failure.InnerException is Win32Exception { NativeErrorCode: 5 or 32 or 33 },
                "Expected Windows sharing/access denial, not an unrelated fixture failure.");
            fixture.AssertOriginal();
        }
        await operation.ApplyAsync(Silent, default);
        await operation.VerifyAsync(default);
        Assert.AreEqual(Fixture.Contents, File.ReadAllText(Path.Combine(fixture.Destination, "settings.json")));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenForProbe(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [TestMethod]
    public async Task NativeCancellationDoesNotRenameOrDiscardTheOriginal()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation.ApplyAsync(Silent, cancellation.Token));
        fixture.AssertOriginal();
        Assert.IsFalse(Directory.Exists(fixture.Destination));
    }

    [TestMethod]
    public void NativeRecordTamperingAndWrongReviewAreRefused()
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        var original = File.ReadAllBytes(fixture.Record);
        var changedPlan = MigrationPlan.Create(fixture.Plan.Items.Select(item => item with { Title = "Changed review" }));
        Assert.ThrowsException<InvalidDataException>(() => WindowsDirectoryRelocation.Resume(changedPlan, "folder", fixture.Record));
        var damaged = original.ToArray(); damaged[^1] ^= 0xff;
        File.WriteAllBytes(fixture.Record, damaged);
        Assert.ThrowsException<CryptographicException>(() => fixture.Resume());
        fixture.AssertOriginal();
        File.WriteAllBytes(fixture.Record, original);
        fixture.Resume().Dispose();
    }

    [TestMethod]
    public void NativeConcurrentOwnerAndExistingRecordCannotBeOverwritten()
    {
        using var fixture = new Fixture();
        using (var operation = fixture.Create())
            Assert.ThrowsException<MigrationTransientException>(() => fixture.Resume());
        var original = File.ReadAllBytes(fixture.Record);
        Assert.ThrowsException<IOException>(() => fixture.Create());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.Record));
        fixture.AssertOriginal();
    }

    [TestMethod]
    public async Task NativeDestinationRaceIsRejectedByTheRenameItself()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        var progress = new InlineProgress(_ =>
        {
            Directory.CreateDirectory(fixture.Destination);
            File.WriteAllText(Path.Combine(fixture.Destination, "keep.txt"), "Destination created after the check");
        });
        await Assert.ThrowsExceptionAsync<IOException>(() => operation.ApplyAsync(progress, default));
        fixture.AssertOriginal();
        Assert.AreEqual("Destination created after the check", File.ReadAllText(Path.Combine(fixture.Destination, "keep.txt")));
    }

    [TestMethod]
    public async Task NativeParentGuardsAndReceiptRemainHeldDuringTheRename()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        var inspected = false;
        var progress = new InlineProgress(_ =>
        {
            if (inspected) return;
            inspected = true;
            Assert.ThrowsException<IOException>(() => File.WriteAllBytes(fixture.Record, [1, 2, 3]));
            var parent = Path.GetDirectoryName(fixture.Destination)!;
            Assert.ThrowsException<IOException>(() => Directory.Move(parent, parent + "-replacement"));
        });
        await operation.ApplyAsync(progress, default);
        await operation.VerifyAsync(default);
        Assert.IsTrue(inspected);
    }

    [TestMethod]
    public void NativeLinkedSourceAndLinkedRecoveryRecordAreRefused()
    {
        using var fixture = new Fixture();
        var other = fixture.Source + "-original";
        Directory.Move(fixture.Source, other);
        Directory.CreateSymbolicLink(fixture.Source, other);
        try { Assert.ThrowsException<IOException>(() => fixture.Create()); }
        finally { Directory.Delete(fixture.Source); }
        Directory.Move(other, fixture.Source);
        var recordTarget = Path.Combine(fixture.Root, "keep.txt");
        File.WriteAllText(recordTarget, "Unchanged record target");
        File.CreateSymbolicLink(fixture.Record, recordTarget);
        try
        {
            Assert.ThrowsException<IOException>(() => fixture.Create());
            Assert.ThrowsException<IOException>(() => fixture.Resume());
        }
        finally { File.Delete(fixture.Record); }
        Assert.AreEqual("Unchanged record target", File.ReadAllText(recordTarget));
        fixture.AssertOriginal();
    }

    [TestMethod]
    public void NativeNestedPathsAndRecoveryInsideMovedTreeAreRejected()
    {
        using var fixture = new Fixture();
        var nested = MigrationPlan.Create(fixture.Plan.Items.Select(item => item with { ProposedLocation = Path.Combine(fixture.Source, "nested") }));
        Assert.ThrowsException<IOException>(() => WindowsDirectoryRelocation.Create(nested, "folder", fixture.Record));
        Assert.ThrowsException<IOException>(() => WindowsDirectoryRelocation.Create(fixture.Plan, "folder", Path.Combine(fixture.Source, "record.bin")));
        Assert.IsFalse(File.Exists(fixture.Record));
        fixture.AssertOriginal();
    }

    [TestMethod]
    public async Task NativeSessionRecoversRenameBeforeVerificationAndNeverRevertsCompletedItem()
    {
        using var fixture = new Fixture();
        using var operation = fixture.Create();
        var interrupted = new FailVerificationOnce(operation);
        using (var session = MigrationSession.Create(fixture.Plan, ["folder"], Path.Combine(fixture.Root, "session.json"), [interrupted]))
        {
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Failed, session.Items.Single().State);
            Assert.IsTrue(session.CanRevert("folder"));
        }
        using var resumed = MigrationSession.Open(Path.Combine(fixture.Root, "session.json"), [operation]);
        await resumed.RetryAsync("folder");
        Assert.IsTrue(resumed.IsComplete);
        Assert.IsFalse(resumed.CanRevert("folder"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => resumed.RevertAsync("folder"));
        Assert.AreEqual(Fixture.Contents, File.ReadAllText(Path.Combine(fixture.Destination, "settings.json")));
    }

    private sealed class FailVerificationOnce(IMigrationOperation inner) : IMigrationOperation
    {
        public string ItemId => inner.ItemId;
        public string ReviewedPlanFingerprint => inner.ReviewedPlanFingerprint;
        public bool CanRevert => inner.CanRevert;
        public Task CheckAsync(CancellationToken ct) => inner.CheckAsync(ct);
        public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken ct) => inner.ApplyAsync(progress, ct);
        public Task VerifyAsync(CancellationToken ct) => throw new IOException("Fixture: interrupted before verification.");
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken ct) => inner.RevertAsync(progress, ct);
        public Task VerifyRevertedAsync(CancellationToken ct) => inner.VerifyRevertedAsync(ct);
    }

    private sealed class InlineProgress(Action<MigrationProgress> receive) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => receive(value); }

    private sealed class Fixture : IDisposable
    {
        internal const string Contents = "{\"settings\":\"isolated directory relocation fixture\"}";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudInlet-DirectoryRelocation-" + Guid.NewGuid().ToString("N"));
        internal string Source { get; }
        internal string Destination { get; }
        internal string Record { get; }
        internal MigrationPlan Plan { get; }
        internal Fixture()
        {
            Source = Path.Combine(Root, "old-parent", "CloudBay");
            Destination = Path.Combine(Root, "new-parent", "CloudInlet");
            Record = Path.Combine(Root, "recovery", "folder.bin");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Record)!);
            File.WriteAllText(Path.Combine(Source, "settings.json"), Contents);
            Plan = MigrationPlan.Create([new("folder", MigrationArea.ApplicationState, "Isolated folder", Source, Destination, "A fixture with no active database or sync registration.")]);
        }
        internal WindowsDirectoryRelocation Create() => WindowsDirectoryRelocation.Create(Plan, "folder", Record);
        internal WindowsDirectoryRelocation Resume() => WindowsDirectoryRelocation.Resume(Plan, "folder", Record);
        internal void AssertOriginal(bool readContent = true)
        {
            Assert.IsTrue(Directory.Exists(Source));
            if (readContent) Assert.AreEqual(Contents, File.ReadAllText(Path.Combine(Source, "settings.json")));
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(root).StartsWith("CloudInlet-DirectoryRelocation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected directory fixture cleanup target.");
            Directory.Delete(root, recursive: true);
        }
    }
}

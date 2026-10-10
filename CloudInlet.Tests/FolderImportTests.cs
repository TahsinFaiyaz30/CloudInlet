using CloudInlet.Application;
using CloudInlet.Core.Sync;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class FolderImportTests
{
    [TestMethod]
    public async Task ReviewedImportRetainsSourceAndDestinationCollisionsAndResumesWithoutDuplicateCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), "source content");
            await File.WriteAllTextAsync(Path.Combine(destination, "report.txt"), "existing destination");
            var plan = FolderImport.Preview(source, destination);
            await FolderImport.ExecuteAsync(plan);
            await FolderImport.ExecuteAsync(FolderImport.Preview(source, destination));
            Assert.AreEqual("source content", await File.ReadAllTextAsync(Path.Combine(source, "report.txt")));
            Assert.AreEqual("source content", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
            var conflicts = Directory.GetFiles(destination, "*conflict*");
            Assert.AreEqual(1, conflicts.Length, "Explicit retry must retain the single existing conflict without recopying it.");
            Assert.AreEqual("existing destination", await File.ReadAllTextAsync(conflicts[0]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task ChangedSourceCannotExecuteStaleImportReview()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "report.txt");
            await File.WriteAllTextAsync(file, "original");
            var timestamps = ReadTimestamps(file);
            var plan = FolderImport.Preview(source, destination);
            await File.WriteAllTextAsync(file, "new edit");
            RestoreTimestamps(file, timestamps);
            await Assert.ThrowsExceptionAsync<IOException>(() => FolderImport.ExecuteAsync(plan));
            Assert.IsFalse(Directory.Exists(destination), "A stale review must fail before destination writes.");
            Assert.AreEqual("new edit", await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SaveAfterVerifiedCopyCannotBecomeTheBaselineForWindowsFolderRedirection()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "report.txt");
            await File.WriteAllTextAsync(file, "alpha");
            var verified = await FolderImport.ExecuteAsync(FolderImport.Preview(source, destination));
            VerifiedTreeCopy.EnsureUnchanged(source, verified);
            var timestamps = ReadTimestamps(file);
            // This is the gap after copy returns, before the mapping guard runs.
            await File.WriteAllTextAsync(file, "bravo");
            RestoreTimestamps(file, timestamps);
            Assert.ThrowsException<IOException>(() => VerifiedTreeCopy.EnsureUnchanged(source, verified));
            Assert.AreEqual("bravo", await File.ReadAllTextAsync(file));
            Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SourceWithAnOpenWriterCannotBeReviewedAsAnUnchangingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "report.txt");
            File.WriteAllText(file, "original");
            using (var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                writer.Write("new edit"u8);
                writer.Flush(true);
                Assert.ThrowsException<IOException>(() => FolderImport.Preview(source, destination),
                    "Repeated writes under one open handle can share a USN; a review must reject an active writer.");
            }
            Assert.IsFalse(Directory.Exists(destination));
            Assert.AreEqual("new edit", File.ReadAllText(file));
            Assert.AreEqual(1L, FolderImport.Preview(source, destination).FileCount,
                "Closing the writer should permit a fresh review.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SourceWithAnOpenNamedStreamWriterCannotBeReviewedAsAnUnchangingSnapshot(bool directoryStream)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = directoryStream ? source : Path.Combine(source, "report.txt");
            if (!directoryStream) File.WriteAllText(file, "base contents");
            var stream = file + ":review";
            File.WriteAllText(stream, "original");
            using (var writer = new FileStream(stream, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                writer.Write("new edit"u8);
                writer.Flush(true);
                Assert.ThrowsException<IOException>(() => FolderImport.Preview(source, destination),
                    "A base-file guard alone does not exclude named-stream writers.");
            }
            Assert.IsFalse(Directory.Exists(destination));
            Assert.AreEqual("new edit", File.ReadAllText(stream));
            Assert.IsNotNull(FolderImport.Preview(source, destination),
                "Closing the named-stream writer should permit a fresh review.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CopyUsesItsActualInitialSnapshotToRejectAChangedReviewedSourceBeforeWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        try
        {
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "report.txt");
            await File.WriteAllTextAsync(file, "original");
            var timestamps = ReadTimestamps(file);
            var plan = FolderImport.Preview(source, destination);
            await File.WriteAllTextAsync(file, "new edit");
            RestoreTimestamps(file, timestamps);
            // Exercise the copy's own snapshot guard, independent of FolderImport's
            // earlier preview, so an intervening edit cannot establish a new baseline.
            await Assert.ThrowsExceptionAsync<IOException>(() => VerifiedTreeCopy.CopyVerifiedAsync(source, destination,
                reviewedFingerprint: plan.Fingerprint));
            Assert.IsFalse(Directory.Exists(destination), "A stale snapshot must fail before any destination write.");
            Assert.AreEqual("new edit", await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReviewedImportDoesNotRequireWriteOrDeleteAccessToAvailableSourceContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source"); var destination = Path.Combine(root, "Destination");
        var file = Path.Combine(source, "report.txt");
        byte[]? originalFileSecurity = null;
        byte[]? originalDirectorySecurity = null;
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(file, "read-only source content");
            var identity = WindowsIdentity.GetCurrent().User!;
            var sourceInfo = new DirectoryInfo(source);
            var fileInfo = new FileInfo(file);
            originalFileSecurity = fileInfo.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            originalDirectorySecurity = sourceInfo.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            var sourceSecurity = sourceInfo.GetAccessControl(AccessControlSections.Access);
            sourceSecurity.AddAccessRule(new FileSystemAccessRule(identity,
                FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
            sourceInfo.SetAccessControl(sourceSecurity);
            var fileSecurity = fileInfo.GetAccessControl(AccessControlSections.Access);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(identity,
                FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete, AccessControlType.Deny));
            fileInfo.SetAccessControl(fileSecurity);
            var denied = Assert.ThrowsException<Win32Exception>(() =>
            {
                using var handle = OpenTimestampHandle(file, 0x10000); // DELETE must really be denied by this fixture.
            });
            Assert.AreEqual(5, denied.NativeErrorCode);

            var plan = FolderImport.Preview(source, destination);
            var verified = await FolderImport.ExecuteAsync(plan);
            VerifiedTreeCopy.EnsureUnchanged(source, verified);
            Assert.AreEqual("read-only source content", await File.ReadAllTextAsync(file));
            Assert.AreEqual("read-only source content", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
        }
        finally
        {
            // Persist writes only sections marked as modified. Reusing an untouched
            // GetAccessControl result silently leaves the deny rules in place.
            if (originalFileSecurity is not null && File.Exists(file))
            {
                var restored = new FileSecurity();
                restored.SetSecurityDescriptorBinaryForm(originalFileSecurity, AccessControlSections.Access);
                new FileInfo(file).SetAccessControl(restored);
            }
            if (originalDirectorySecurity is not null && Directory.Exists(source))
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorBinaryForm(originalDirectorySecurity, AccessControlSections.Access);
                new DirectoryInfo(source).SetAccessControl(restored);
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void InterruptedImportJournalNeedsExplicitReviewAndPreservesOriginalPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 3, 300, 3000, true);
            var record = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, plan, "account|bucket|CloudInlet/|" + root, "Copying");
            journal.Save(record);
            var reopened = new ImportJournal(root).Read().Single();
            Assert.AreEqual(plan, reopened.Plan);
            Assert.AreEqual("Needs review", reopened.State);
            StringAssert.Contains(reopened.Error!, "interrupted");
            Assert.IsFalse(Directory.Exists(plan.SourcePath));
            Assert.IsFalse(Directory.Exists(plan.DestinationPath), "Reading recovery history never starts an import.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DamagedOrMisnamedFolderImportRecordsAreRetainedWithoutCreatingImportJobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 1, 10, 1000, false);
            var record = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, plan, "account|bucket|CloudInlet/|" + root, "Copying");
            journal.Save(record);
            var directory = Path.Combine(root, "Imports");
            var validPath = Path.Combine(directory, record.Id + ".json");
            var bytes = File.ReadAllBytes(validPath);
            File.WriteAllBytes(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), bytes);
            File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "{broken");
            var oversized = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            using (var stream = new FileStream(oversized, FileMode.CreateNew)) stream.SetLength(1024 * 1024 + 1);
            Assert.AreEqual(1, journal.Read().Count);
            Assert.AreEqual(3, journal.SkippedRecords);
            Assert.AreEqual(4, Directory.GetFiles(directory, "*.json").Length);
            Assert.IsFalse(Directory.Exists(plan.DestinationPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FilteredFolderHistoryKeepsOldInterruptedWorkAheadOfNewerCompletedAndOtherAccountImports()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var identity = "account|bucket|CloudInlet/|" + root;
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 1, 10, 1000, false);
            var interrupted = new FolderImportRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddYears(-1), plan, identity, "Copying");
            journal.Save(interrupted);
            for (var i = 0; i < 60; i++)
            {
                journal.Save(interrupted with { Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow.AddMinutes(i), State = "Completed" });
                journal.Save(interrupted with { Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow.AddYears(1), DestinationIdentity = "other|bucket|CloudInlet/|" + root });
            }
            var history = journal.Read(identity);
            Assert.AreEqual(50, history.Count);
            Assert.AreEqual(interrupted.Id, history[0].Id);
            Assert.AreEqual("Needs review", history[0].State);
            Assert.IsTrue(history.All(item => item.DestinationIdentity == identity));
            Assert.AreEqual(121, Directory.GetFiles(Path.Combine(root, "Imports"), "*.json").Length, "Bounding history must never remove recovery records.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AggregateHistoryBudgetEvictsCompletedEntriesBeforeInterruptedWork()
    {
        var plan = new FolderImportPlan("C:\\source", "C:\\destination", new string('a', 64), 0, 0, null, false);
        var pending = new FolderImportRecord("pending", DateTimeOffset.UtcNow.AddYears(-1), plan, "identity", "Needs review");
        var olderCompleted = pending with { Id = "older-completed", StartedUtc = DateTimeOffset.UtcNow, State = "Completed" };
        var newerCompleted = olderCompleted with { Id = "newer-completed", StartedUtc = DateTimeOffset.UtcNow.AddDays(1) };
        var selection = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id);
        selection.Add(pending, 32L * 1024 * 1024);
        selection.Add(olderCompleted, 80L * 1024 * 1024);
        selection.Add(newerCompleted, 80L * 1024 * 1024);
        Assert.IsTrue(selection.HasMore, "A byte-limited page can contain fewer than 50 records while older entries remain.");
        CollectionAssert.AreEqual(new[] { pending.Id, newerCompleted.Id }, selection.Records.Select(item => item.Id).ToArray());
        selection.Add(pending with { Id = "second-pending", StartedUtc = pending.StartedUtc.AddDays(-1) }, 80L * 1024 * 1024);
        Assert.AreEqual(2, selection.Records.Count);
        Assert.IsTrue(selection.Records.All(item => item.State == "Needs review"));
    }

    [TestMethod]
    public void ByteLimitedHistoryPageCannotSkipALargeMiddleRecordWhenAdvancingItsCursor()
    {
        var plan = new FolderImportPlan("C:\\source", "C:\\destination", new string('a', 64), 0, 0, null, false);
        var first = new FolderImportRecord("first", DateTimeOffset.UtcNow, plan, "identity", "Needs review");
        var middle = first with { Id = "middle", StartedUtc = first.StartedUtc.AddMinutes(-1) };
        var last = first with { Id = "last", StartedUtc = first.StartedUtc.AddMinutes(-2) };
        var selection = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id);
        selection.Add(first, 80L * 1024 * 1024);
        selection.Add(middle, 80L * 1024 * 1024);
        selection.Add(last, 1);
        CollectionAssert.AreEqual(new[] { first.Id }, selection.Records.Select(item => item.Id).ToArray());
        Assert.IsTrue(selection.HasMore);
        var next = new ImportHistorySelection<FolderImportRecord>(item => item.State != "Completed", item => item.StartedUtc, item => item.Id,
            new(false, first.StartedUtc, first.Id));
        next.Add(first, 80L * 1024 * 1024);
        next.Add(middle, 80L * 1024 * 1024);
        next.Add(last, 1);
        CollectionAssert.AreEqual(new[] { middle.Id, last.Id }, next.Records.Select(item => item.Id).ToArray());
        Assert.IsFalse(next.HasMore);
    }

    [TestMethod]
    public void FolderHistoryCursorIncludesEveryInterruptedRecordWithStableTimestampTies()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ImportJournal(root);
            var identity = "account|bucket|CloudInlet/|" + root;
            var plan = new FolderImportPlan(Path.Combine(root, "Source"), Path.Combine(root, "Destination"), new string('a', 64), 0, 0, null, false);
            var time = DateTimeOffset.UtcNow.AddYears(-1);
            var ids = Enumerable.Range(1, 61).Select(number => number.ToString("x32")).ToArray();
            foreach (var id in ids) journal.Save(new(id, time, plan, identity, "Copying"));
            var completedId = Guid.NewGuid().ToString("N");
            journal.Save(new(completedId, DateTimeOffset.UtcNow, plan, identity, "Completed"));
            var first = journal.Read(identity);
            Assert.IsTrue(journal.HasMoreRecords);
            CollectionAssert.AreEqual(ids.Take(50).ToArray(), first.Select(item => item.Id).ToArray());
            var last = first[^1];
            var second = journal.Read(identity, new(last.State == "Completed", last.StartedUtc, last.Id));
            Assert.IsFalse(journal.HasMoreRecords);
            CollectionAssert.AreEqual(ids.Skip(50).Append(completedId).ToArray(), second.Select(item => item.Id).ToArray());
            CollectionAssert.AreEquivalent(ids, first.Concat(second).Where(item => item.State != "Completed").Select(item => item.Id).ToArray());
            Assert.AreEqual(ids[^1], journal.Read(identity, recordId: ids[^1]).Single().Id);
            Assert.AreEqual(0, journal.Read("other|bucket|CloudInlet/|" + root, recordId: ids[^1]).Count);
            Assert.ThrowsException<ArgumentException>(() => journal.Read(recordId: "../escape"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    public void ImportRejectsNestedSourceAndDestinationBeforeCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudInlet-ImportTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.ThrowsException<IOException>(() => FolderImport.Preview(root, Path.Combine(root, "Child")));
            Assert.ThrowsException<IOException>(() => FolderImport.Preview(Path.Combine(root, "Child"), root));
        }
        finally { Directory.Delete(root, true); }
    }

    // Force the same-size/coarse-timestamp collision deterministically. Restoring only
    // LastWriteTime depends on the filesystem clock advancing between rapid writes.
    private static BasicInformation ReadTimestamps(string path)
    {
        using var handle = OpenTimestampHandle(path, 0x80); // FILE_READ_ATTRIBUTES
        if (!GetFileInformationByHandleEx(handle, 0, out BasicInformation information,
            (uint)Marshal.SizeOf<BasicInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return information;
    }

    private static void RestoreTimestamps(string path, BasicInformation reviewed)
    {
        using (var handle = OpenTimestampHandle(path, 0x100)) // FILE_WRITE_ATTRIBUTES
        {
            var timestamps = new BasicInformation { Modified = reviewed.Modified, Changed = reviewed.Changed };
            if (!SetFileInformationByHandle(handle, 0, ref timestamps, (uint)Marshal.SizeOf<BasicInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var restored = ReadTimestamps(path);
        Assert.AreEqual(reviewed.Modified, restored.Modified, "The fixture must restore LastWriteTime exactly.");
        Assert.AreEqual(reviewed.Changed, restored.Changed, "The fixture must restore ChangeTime exactly.");
    }

    private static SafeFileHandle OpenTimestampHandle(string path, uint access)
    {
        var handle = CreateFileW(path, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation { public long Created, Accessed, Modified, Changed; public uint Attributes; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out BasicInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref BasicInformation information, uint size);
}

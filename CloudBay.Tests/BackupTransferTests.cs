using System.Security.Cryptography;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class NativeBackupTransferTests
{
    [TestMethod]
    public async Task VerifiedMoveRemovesExactOriginalsAndRetainsCopiedStreamsAndReadOnlyAttributes()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Source, "report.txt");
        await File.WriteAllTextAsync(original, "verified user content");
        await File.WriteAllTextAsync(original + ":details", "named metadata");
        File.SetAttributes(original, FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(fixture.Source, "Empty"));
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount);
        Assert.AreEqual(1L, outcome.RemovedFileCount);
        Assert.AreEqual(0L, outcome.RetainedFileCount);
        Assert.IsNull(outcome.RetentionWarning);
        Assert.IsFalse(File.Exists(original));
        var copied = Path.Combine(fixture.Destination, "report.txt");
        Assert.AreEqual("verified user content", await File.ReadAllTextAsync(copied));
        Assert.AreEqual("named metadata", await File.ReadAllTextAsync(copied + ":details"));
        Assert.IsTrue(File.GetAttributes(copied).HasFlag(FileAttributes.ReadOnly));
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.Source, "Empty")), "The move never recursively removes source containers.");
    }

    [TestMethod]
    public async Task MoveRetainsEditedOriginalEvenWhenItsSizeAndModifiedTimeAreRestored()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Source, "report.txt");
        await File.WriteAllTextAsync(original, "alpha");
        var modified = File.GetLastWriteTimeUtc(original);
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        await File.WriteAllTextAsync(original, "bravo");
        File.SetLastWriteTimeUtc(original, modified);
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount);
        Assert.AreEqual(0L, outcome.RemovedFileCount);
        Assert.AreEqual(1L, outcome.RetainedFileCount);
        Assert.IsNotNull(outcome.RetentionWarning);
        Assert.AreEqual("bravo", await File.ReadAllTextAsync(original));
        Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "report.txt")));
    }

    [TestMethod]
    public async Task MoveRehashesDestinationAndKeepsOriginalIfTheVerifiedCopyWasEdited()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Source, "report.txt");
        await File.WriteAllTextAsync(original, "alpha");
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        var copied = Path.Combine(fixture.Destination, "report.txt");
        await File.WriteAllTextAsync(copied, "bravo");
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount);
        Assert.AreEqual(0L, outcome.RemovedFileCount);
        Assert.AreEqual(1L, outcome.RetainedFileCount);
        Assert.AreEqual("alpha", await File.ReadAllTextAsync(original));
        Assert.AreEqual("bravo", await File.ReadAllTextAsync(copied));
    }

    [TestMethod]
    public async Task MoveRetainsOriginalIfDestinationNamedStreamWasEdited()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Source, "report.txt");
        await File.WriteAllTextAsync(original, "content");
        await File.WriteAllTextAsync(original + ":details", "alpha");
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "report.txt") + ":details", "bravo");
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount);
        Assert.AreEqual(0L, outcome.RemovedFileCount);
        Assert.AreEqual(1L, outcome.RetainedFileCount);
        Assert.AreEqual("alpha", await File.ReadAllTextAsync(original + ":details"));
    }

    [TestMethod]
    public async Task MoveKeepsLockedOriginalAndDoesNotDowngradeItsPermissions()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Source, "report.txt");
        await File.WriteAllTextAsync(original, "content");
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        using var heldByAnotherApp = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount);
        Assert.AreEqual(0L, outcome.RemovedFileCount);
        Assert.AreEqual(1L, outcome.RetainedFileCount);
        Assert.IsTrue(File.Exists(original));
        Assert.AreEqual("content", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "report.txt")));
    }

    [TestMethod]
    public async Task CancelledMoveLeavesAllOriginalsAndVerifiedCopiesAvailable()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "report.txt"), "content");
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount, cancellation.Token);
        Assert.AreEqual(0L, outcome.RemovedFileCount);
        Assert.AreEqual(1L, outcome.RetainedFileCount);
        Assert.IsNotNull(outcome.RetentionWarning);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "report.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "report.txt")));
    }

    [TestMethod]
    public void NoTransferDestinationCreatesNoSourceAndRetainsExistingDestinationFiles()
    {
        using var fixture = new Fixture();
        var oldFile = Path.Combine(fixture.Source, "report.txt");
        File.WriteAllText(oldFile, "original");
        Directory.CreateDirectory(fixture.Destination);
        var existing = Path.Combine(fixture.Destination, "existing.txt");
        File.WriteAllText(existing, "already in CloudBay");
        VerifiedTreeCopy.EnsureDestinationDirectory(fixture.Destination);
        Assert.AreEqual("original", File.ReadAllText(oldFile));
        Assert.AreEqual("already in CloudBay", File.ReadAllText(existing));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "report.txt")));
    }

    [TestMethod]
    public async Task MoveRetainsOriginalWhenTheDestinationWasReplacedWithAFileLink()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Source, "report.txt");
        await File.WriteAllTextAsync(original, "content");
        var plan = FolderImport.Preview(fixture.Source, fixture.Destination);
        var fingerprint = await FolderImport.ExecuteAsync(plan);
        var unrelated = Path.Combine(fixture.Root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "content");
        var copied = Path.Combine(fixture.Destination, "report.txt");
        File.Delete(copied);
        File.CreateSymbolicLink(copied, unrelated);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(unrelated));
        var outcome = await VerifiedTreeMove.RemoveCopiedSourcesAsync(fixture.Source, fixture.Destination, fingerprint, plan.FileCount);
        Assert.AreEqual(0L, outcome.RemovedFileCount);
        Assert.AreEqual(1L, outcome.RetainedFileCount);
        Assert.IsTrue(File.Exists(original));
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(unrelated)));
        Assert.IsNotNull(new FileInfo(copied).LinkTarget);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudBay-BackupTransfer-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "Source");
        public string Destination => Path.Combine(Root, "Destination");
        public Fixture() => Directory.CreateDirectory(Source);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CloudBay-BackupTransfer-", StringComparison.Ordinal))
                throw new IOException("The fixture cleanup path is outside its temporary test root.");
            if (!Directory.Exists(resolved)) return;
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                if (new FileInfo(file).LinkTarget is null) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, recursive: true);
        }
    }
}

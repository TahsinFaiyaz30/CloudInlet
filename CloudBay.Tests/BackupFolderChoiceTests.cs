using CloudBay.Core;
using CloudBay.Core.Sync;
using CloudBay.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

/// <summary>Models Shell mappings with fixture-only delegates; no real Windows folder is redirected.</summary>
[TestClass]
public sealed class BackupFolderChoiceTests
{
    [TestMethod]
    public async Task CleanEnableDoesNotInspectOrImportTheOriginalOrSelectedSource()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "old.txt"), "keep at old location");
        // An ordinary import would reject this linked file. Clean enable must not inspect it.
        File.CreateSymbolicLink(Path.Combine(original, "linked.txt"), Path.Combine(original, "old.txt"));
        var destination = Path.Combine(fixture.Root, "CloudBay", "Documents");
        var review = KnownFolderBackup.PreviewForPaths("Documents", original, destination, original,
            Path.Combine(fixture.Root, "Unavailable source"), BackupTransferMode.None, true);
        Assert.IsFalse(review.IncludeCurrentFiles);
        Assert.IsNull(review.AdditionalFiles);
        Assert.AreEqual("not-selected", review.CurrentFiles.Fingerprint);
        var currentMapping = original;
        var result = await KnownFolderBackup.ApplyEnableForPathsAsync(review, () => currentMapping, value => currentMapping = value);
        Assert.AreEqual(destination, currentMapping);
        Assert.AreEqual(destination, result.Folder.DestinationPath);
        Assert.IsNull(result.RetentionWarning);
        Assert.AreEqual("keep at old location", File.ReadAllText(Path.Combine(original, "old.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "old.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "Unavailable source")));
    }

    [TestMethod]
    public async Task SelectedSourceEnableImportsOnlyThatSourceAndLeavesCurrentWindowsFilesAlone()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var selected = Path.Combine(fixture.Root, "OneDrive", "Pictures");
        var destination = Path.Combine(fixture.Root, "CloudBay", "Pictures");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(original, "current.txt"), "current Windows folder");
        File.WriteAllText(Path.Combine(selected, "photo.jpg"), "selected provider");
        var review = KnownFolderBackup.PreviewForPaths("Pictures", original, destination, original,
            selected, BackupTransferMode.Copy, false);
        var currentMapping = original;
        await KnownFolderBackup.ApplyEnableForPathsAsync(review, () => currentMapping, value => currentMapping = value);
        Assert.AreEqual(destination, currentMapping);
        Assert.AreEqual("selected provider", File.ReadAllText(Path.Combine(destination, "photo.jpg")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "current.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(original, "current.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(selected, "photo.jpg")), "Copy retains the selected cloud source.");
    }

    [TestMethod]
    public async Task SelectedSourceMoveRemovesOnlyVerifiedSelectedFilesAfterMappingCommit()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var selected = Path.Combine(fixture.Root, "OneDrive", "Pictures");
        var destination = Path.Combine(fixture.Root, "CloudBay", "Pictures");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(original, "current.txt"), "current Windows folder");
        File.WriteAllText(Path.Combine(selected, "photo.jpg"), "selected provider");
        var review = KnownFolderBackup.PreviewForPaths("Pictures", original, destination, original,
            selected, BackupTransferMode.Move, false);
        var currentMapping = original;
        var result = await KnownFolderBackup.ApplyEnableForPathsAsync(review, () => currentMapping, value =>
        {
            Assert.IsTrue(File.Exists(Path.Combine(selected, "photo.jpg")), "Originals remain until Windows commits its mapping.");
            Assert.IsTrue(File.Exists(Path.Combine(destination, "photo.jpg")));
            currentMapping = value;
        });
        Assert.IsNull(result.RetentionWarning);
        Assert.AreEqual(destination, currentMapping);
        Assert.IsFalse(File.Exists(Path.Combine(selected, "photo.jpg")));
        Assert.IsTrue(File.Exists(Path.Combine(original, "current.txt")));
        Assert.AreEqual("selected provider", File.ReadAllText(Path.Combine(destination, "photo.jpg")));
    }

    [TestMethod]
    public async Task MappingFailureKeepsAllOriginalsWhenMoveWasSelected()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var destination = Path.Combine(fixture.Root, "CloudBay", "Documents");
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "report.txt"), "original");
        var review = KnownFolderBackup.PreviewForPaths("Documents", original, destination, original, null, BackupTransferMode.Move, true);
        await Assert.ThrowsExceptionAsync<IOException>(() => KnownFolderBackup.ApplyEnableForPathsAsync(review,
            () => original, _ => throw new IOException("Windows rejected the mapping")));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(original, "report.txt")));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(destination, "report.txt")));
    }

    [TestMethod]
    public async Task CleanDisableChangesOnlyWindowsLocationAndLeavesCloudBayFilesAvailable()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var cloud = Path.Combine(fixture.Root, "CloudBay", "Documents");
        Directory.CreateDirectory(cloud);
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(cloud, "cloud.txt"), "leave in CloudBay");
        File.WriteAllText(Path.Combine(original, "already-local.txt"), "old local file");
        File.CreateSymbolicLink(Path.Combine(cloud, "linked.txt"), Path.Combine(cloud, "cloud.txt"));
        var folder = new BackupFolder("Documents", original, cloud);
        var review = KnownFolderBackup.PreviewDisableForPaths(folder, null, BackupTransferMode.None);
        Assert.IsNull(review.Files);
        var currentMapping = cloud;
        var result = await KnownFolderBackup.ApplyDisableForPathsAsync(review, () => currentMapping, value => currentMapping = value);
        Assert.AreEqual(original, currentMapping);
        Assert.IsNull(result.RetentionWarning);
        Assert.AreEqual("leave in CloudBay", File.ReadAllText(Path.Combine(cloud, "cloud.txt")));
        Assert.AreEqual("old local file", File.ReadAllText(Path.Combine(original, "already-local.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(original, "cloud.txt")));
    }

    [TestMethod]
    public async Task DisableCanCopyToASelectedProviderWithoutRemovingCloudBayOriginals()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var cloud = Path.Combine(fixture.Root, "CloudBay", "Pictures");
        var selected = Path.Combine(fixture.Root, "OneDrive", "Pictures");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "photo.jpg"), "photo");
        var folder = new BackupFolder("Pictures", original, cloud);
        var review = KnownFolderBackup.PreviewDisableForPaths(folder, selected, BackupTransferMode.Copy);
        var currentMapping = cloud;
        var result = await KnownFolderBackup.ApplyDisableForPathsAsync(review, () => currentMapping, value => currentMapping = value);
        Assert.AreEqual(selected, currentMapping);
        Assert.IsNull(result.RetentionWarning);
        Assert.IsTrue(File.Exists(Path.Combine(cloud, "photo.jpg")));
        Assert.AreEqual("photo", File.ReadAllText(Path.Combine(selected, "photo.jpg")));
        Assert.IsFalse(Directory.Exists(original), "The unused original location is not restored implicitly.");
    }

    [TestMethod]
    public async Task MoveInterruptedAfterWindowsCommitReturnsRetentionWarningAndKeepsBothCopies()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var cloud = Path.Combine(fixture.Root, "CloudBay", "Documents");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "report.txt"), "report");
        var folder = new BackupFolder("Documents", original, cloud);
        var review = KnownFolderBackup.PreviewDisableForPaths(folder, null, BackupTransferMode.Move);
        var currentMapping = cloud;
        using var cancellation = new CancellationTokenSource();
        var result = await KnownFolderBackup.ApplyDisableForPathsAsync(review, () => currentMapping, value =>
        {
            currentMapping = value;
            cancellation.Cancel();
        }, cancellation.Token);
        Assert.AreEqual(original, currentMapping);
        Assert.AreEqual(1L, result.RetainedFileCount);
        Assert.IsNotNull(result.RetentionWarning);
        Assert.AreEqual("report", File.ReadAllText(Path.Combine(cloud, "report.txt")));
        Assert.AreEqual("report", File.ReadAllText(Path.Combine(original, "report.txt")));
    }

    [TestMethod]
    public void SelectingCopyWithoutAnySourceFailsBeforeDestinationWrites()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "Original");
        var cloud = Path.Combine(fixture.Root, "CloudBay", "Documents");
        Assert.ThrowsException<IOException>(() => KnownFolderBackup.PreviewForPaths("Documents", original, cloud,
            original, null, BackupTransferMode.Copy, false));
        Assert.IsFalse(Directory.Exists(original));
        Assert.IsFalse(Directory.Exists(cloud));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudBay-BackupChoices-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CloudBay-BackupChoices-", StringComparison.Ordinal))
                throw new IOException("The fixture cleanup path is outside its temporary test root.");
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                if (new FileInfo(file).LinkTarget is null) File.SetAttributes(file, FileAttributes.Normal);
            foreach (var directory in Directory.EnumerateDirectories(resolved, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(resolved, recursive: true);
        }
    }
}

using CloudInlet.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ActivityLocationResolverTests
{
    private static AppSettings Settings => new()
    {
        KeyId = "test-key", BucketId = "bucket-a", RootPath = @"C:\Users\Example\CloudInlet", Prefix = "Personal/",
        CustomBackups = [new("Projects", @"D:\Projects", "Personal/.cloudbay-backups/Projects/")]
    };
    private static ActivityEvent Event(ActivityKind kind, string relative, ActivityLocation? location = null) =>
        new(DateTimeOffset.UtcNow, kind, relative, "activity")
        { Location = location ?? new(Settings.RootPath, null, relative, Settings.BucketId, Settings.Prefix) };
    private static ActivityEvent Legacy(ActivityKind kind, string path) => new(DateTimeOffset.UtcNow, kind, path, "activity");

    [TestMethod]
    public void UploadedFileOffersItsContainingFolderAndExactB2VersionPath()
    {
        var target = ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "Pictures/photo.jpg"), Settings)!;
        Assert.AreEqual(@"C:\Users\Example\CloudInlet\Pictures", target.ContainingFolder);
        Assert.AreEqual("Pictures/photo.jpg", target.Location.RelativePath);
        Assert.AreEqual("Personal/", target.Location.Prefix);
        Assert.IsTrue(target.CanViewCloud);
    }

    [TestMethod]
    public void ExplicitCustomIdentitySeparatesItsDisplayLabelFromTheRelativePath()
    {
        var location = new ActivityLocation(@"D:\Projects", "Projects", "Report.pdf", "bucket-a", "Personal/.cloudbay-backups/Projects/");
        var target = ActivityLocationResolver.ForActivity(Event(ActivityKind.Download, "Projects/Report.pdf", location), Settings)!;
        Assert.AreEqual(@"D:\Projects", target.ContainingFolder);
        Assert.AreEqual("Report.pdf", target.Location.RelativePath);
        Assert.AreEqual("Projects", target.Location.BackupName);
        Assert.IsTrue(target.CanViewCloud);
    }

    [TestMethod]
    public void ExplicitMainIdentityCanHaveTheSameDisplayNameAsACustomRoot()
    {
        var target = ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "Projects/Report.pdf",
            new(Settings.RootPath, null, "Projects/Report.pdf", Settings.BucketId, Settings.Prefix)), Settings)!;
        Assert.IsNull(target.Location.BackupName);
        Assert.AreEqual(@"C:\Users\Example\CloudInlet\Projects", target.ContainingFolder);
    }

    [TestMethod]
    public void AmbiguousHistoricalCustomAndNativeHydrationLabelsOfferNoGuessedAction()
    {
        Assert.IsNull(ActivityLocationResolver.ForActivity(Legacy(ActivityKind.Download, "Projects/Report.pdf"), Settings));
        Assert.IsNull(ActivityLocationResolver.ForActivity(Legacy(ActivityKind.Download, "CloudInlet/Pictures/photo.jpg"), Settings));
        Assert.IsNull(ActivityLocationResolver.ForActivity(Legacy(ActivityKind.Upload, "Pictures/photo.jpg"), Settings));
    }

    [TestMethod]
    public void OldBucketPrefixOrRootAndRemovedCustomRootsAreRejected()
    {
        var location = new ActivityLocation(Settings.RootPath, null, "photo.jpg", Settings.BucketId, Settings.Prefix);
        foreach (var stale in new[] { location with { BucketId = "bucket-old" }, location with { Prefix = "Old/" },
            location with { RootPath = @"C:\Users\Example\Previous" }, location with { BackupName = "Removed" } })
            Assert.IsNull(ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "photo.jpg", stale), Settings));
    }

    [TestMethod]
    public void RootIdentityCannotUseAParentTraversalOrMissingFields()
    {
        var location = new ActivityLocation(@"C:\Users\Example\Previous\..\CloudInlet", null, "photo.jpg", Settings.BucketId, Settings.Prefix);
        Assert.IsFalse(ActivityLocationResolver.MatchesCurrentRoot(location, Settings));
        Assert.IsNull(ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "photo.jpg", location), Settings));
        Assert.IsNull(ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "photo.jpg", location with { BucketId = null! }), Settings));
    }

    [DataTestMethod]
    [DataRow("../Outside/secret.txt")]
    [DataRow("C:/Outside/secret.txt")]
    [DataRow("//server/share/secret.txt")]
    [DataRow("Pictures/./photo.jpg")]
    [DataRow("Pictures/photo.jpg:secret")]
    [DataRow("https://example.com/photo.jpg?token=secret")]
    [DataRow(".cloudbay-backups/Projects/report.pdf")]
    public void UnsafeOrInternalPathsDoNotProduceActions(string path)
    {
        Assert.IsNull(ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, path), Settings));
    }

    [TestMethod]
    public void BackupAndErrorRowsHaveLocalActionsWithoutPretendingToBeCloudFiles()
    {
        var backup = ActivityLocationResolver.ForActivity(Event(ActivityKind.Backup, "Pictures"), Settings)!;
        Assert.IsTrue(backup.IsFolder);
        Assert.AreEqual(@"C:\Users\Example\CloudInlet\Pictures", backup.ContainingFolder);
        Assert.IsFalse(backup.CanViewCloud);
        var error = ActivityLocationResolver.ForActivity(Event(ActivityKind.Error, "Pictures/photo.jpg"), Settings)!;
        Assert.IsFalse(error.CanViewCloud);
        var custom = ActivityLocationResolver.ForActivity(Event(ActivityKind.Backup, "Projects",
            new(@"D:\Projects", "Projects", "", Settings.BucketId, "Personal/.cloudbay-backups/Projects/")), Settings)!;
        Assert.AreEqual(@"D:\Projects", custom.ContainingFolder);
        Assert.IsFalse(custom.CanViewCloud);
    }

    [TestMethod]
    public void UploadQueueHasLocalOnlyWhileDownloadQueueCanViewItsCloudVersions()
    {
        var transfer = new TransferSnapshot("transfer", "Projects", "photo.jpg", ActivityKind.Upload, TransferPhase.Queued, 0, 100);
        var upload = ActivityLocationResolver.ForTransfer(transfer, Settings)!;
        Assert.AreEqual(@"D:\Projects", upload.ContainingFolder);
        Assert.IsFalse(upload.CanViewCloud);
        Assert.IsTrue(ActivityLocationResolver.ForTransfer(transfer with { Kind = ActivityKind.Download }, Settings)!.CanViewCloud);
        Assert.IsNull(ActivityLocationResolver.ForTransfer(transfer with { RootName = "Removed" }, Settings));
    }

    [TestMethod]
    public void MissingContainingDirectoryFallsBackInsideTheRootAndNeverAboveIt()
    {
        var target = ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "Pictures/Removed/photo.jpg"), Settings)!;
        var visited = new List<string>();
        var folder = ActivityLocationResolver.FindExistingFolder(target, path =>
        {
            visited.Add(path);
            return path == Settings.RootPath;
        });
        Assert.AreEqual(Settings.RootPath, folder);
        CollectionAssert.AreEqual(new[] { @"C:\Users\Example\CloudInlet\Pictures\Removed", @"C:\Users\Example\CloudInlet\Pictures", Settings.RootPath }, visited);
        visited.Clear();
        Assert.IsNull(ActivityLocationResolver.FindExistingFolder(target, path => { visited.Add(path); return false; }));
        Assert.AreEqual(Settings.RootPath, visited[^1]);
    }

    [TestMethod]
    public void UnconfiguredAccountOffersNoActions()
    {
        Assert.IsNull(ActivityLocationResolver.ForActivity(Event(ActivityKind.Upload, "photo.jpg"), new()));
        Assert.IsNull(ActivityLocationResolver.ForTransfer(new("id", "CloudInlet", "photo.jpg", ActivityKind.Download, TransferPhase.Downloading, 0, 1), new()));
    }
}

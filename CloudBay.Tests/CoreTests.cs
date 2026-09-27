using System.Text.RegularExpressions;
using CloudBay.Core.Filters;
using CloudBay.Core.Folders;
using CloudBay.Core.Links;
using CloudBay.Core.Safety;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class CoreTests
{
    [TestMethod]
    public void KnownFolderClassificationAndRecoveryGate()
    {
        var local = Path.Combine(Path.GetTempPath(), "CloudBayDecision", "Documents");
        var cloud = Path.Combine(Path.GetTempPath(), "CloudBayDecision", "CloudDocuments");
        Assert.AreEqual(KnownFolderState.LocalDefault,
            KnownFolderDecision.Classify(local + Path.DirectorySeparatorChar, local, true, false, false));
        Assert.AreEqual(KnownFolderState.CloudManaged,
            KnownFolderDecision.Classify(cloud, local, true, true, false));
        Assert.AreEqual(KnownFolderState.LegacyOneDrive,
            KnownFolderDecision.Classify(cloud, local, true, false, true));
        Assert.AreEqual(KnownFolderState.RedirectedElsewhere,
            KnownFolderDecision.Classify(cloud, local, true, false, false));
        Assert.AreEqual(KnownFolderState.BrokenRedirect,
            KnownFolderDecision.Classify(cloud, local, false, true, false));
        Assert.IsTrue(KnownFolderDecision.RequiresExplicitRecovery(false, false, false));
        Assert.IsFalse(KnownFolderDecision.RequiresExplicitRecovery(true, false, false));
        Assert.IsFalse(KnownFolderDecision.RequiresExplicitRecovery(false, true, false));
        Assert.IsFalse(KnownFolderDecision.RequiresExplicitRecovery(false, false, true));
    }

    [TestMethod]
    public void ProviderExclusionSyntaxAndValidation()
    {
        var custom = DeveloperFilterService.ValidateCustomPatterns(["dist/", "*.log", ".idea/"]);
        var git = DeveloperFilterService.GenerateGitRules(["node_modules"], custom);
        var rclone = DeveloperFilterService.GenerateRcloneRules(["node_modules"], custom);
        var duck = DeveloperFilterService.GenerateFilenameRegex(["node_modules"], custom);
        StringAssert.Contains(git, "node_modules/\n");
        StringAssert.Contains(git, "dist/\n");
        StringAssert.Contains(git, "*.log\n");
        StringAssert.Contains(rclone, "- dist/\n- dist/**\n");
        StringAssert.Contains(rclone, "- *.log\n");
        Assert.IsTrue(Regex.IsMatch("build.log", duck));
        Assert.IsTrue(Regex.IsMatch("node_modules", duck));
        Assert.IsTrue(Regex.IsMatch(".idea", duck));
        Assert.IsFalse(Regex.IsMatch("my-node_modules", duck));
        foreach (var unsafePattern in new[] { "../private", "!important", "*", "foo\n- *", "sub/path" })
            Assert.ThrowsException<ArgumentException>(() =>
                DeveloperFilterService.ValidateCustomPatterns([unsafePattern]));
    }

    [TestMethod]
    public async Task JournalSurvivesSerializationAndStateChanges()
    {
        var temp = Path.Combine(Path.GetTempPath(), "CloudBayTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new OperationJournal(temp);
            var started = await journal.BeginAsync("test.operation", @"C:\source", @"D:\target",
                new Dictionary<string, string> { ["BackupPath"] = @"C:\backup" });
            var reopened = new OperationJournal(temp);
            var loaded = await reopened.GetAsync(started.Id);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(JournalState.Started, loaded.State);
            Assert.AreEqual(@"C:\source", loaded.SourcePath);
            Assert.AreEqual(@"C:\backup", loaded.Metadata["BackupPath"]);
            await reopened.CompleteAsync(started.Id, new Dictionary<string, string> { ["Verified"] = "True" });
            loaded = await journal.GetAsync(started.Id);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(JournalState.Completed, loaded.State);
            Assert.AreEqual("True", loaded.Metadata["Verified"]);
            Assert.IsNotNull(loaded.FinishedAtUtc);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public async Task LinkUnlinkPreservesLocalAndCloudFiles()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows directory links are required.");
        var temp = Path.Combine(Path.GetTempPath(), "CloudBayTests-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(temp, "local", "Work");
        var cloudRoot = Path.Combine(temp, "cloud");
        var localOriginal = Path.Combine(local, "original.txt");
        try
        {
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(cloudRoot);
            await File.WriteAllTextAsync(localOriginal, "original");
            var service = new FolderLinkService(new OperationJournal(Path.Combine(temp, "journals")),
                Path.Combine(temp, "records", "links.json"));
            var plan = await service.PreflightAsync(local, cloudRoot);
            Assert.AreEqual(1L, plan.FileCount);
            Assert.AreEqual(8L, plan.TotalBytes);
            var link = await service.LinkAsync(local, cloudRoot);
            Assert.IsTrue((File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0);
            var cloudOriginal = Path.Combine(link.CloudPath, "original.txt");
            Assert.AreEqual("original", await File.ReadAllTextAsync(cloudOriginal));
            await File.WriteAllTextAsync(Path.Combine(link.LocalBackupPath, "original.txt"), "local revision");
            await File.WriteAllTextAsync(cloudOriginal, "cloud revision");
            await File.WriteAllTextAsync(Path.Combine(link.CloudPath, "new.txt"), "new cloud data");
            var result = await service.UnlinkAsync(link.Id);
            Assert.IsTrue(result.RestoredLocalPath);
            Assert.IsFalse((File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0);
            Assert.AreEqual("cloud revision", await File.ReadAllTextAsync(localOriginal));
            Assert.AreEqual("new cloud data", await File.ReadAllTextAsync(Path.Combine(local, "new.txt")));
            Assert.AreEqual("cloud revision", await File.ReadAllTextAsync(cloudOriginal));
            Assert.AreEqual("new cloud data", await File.ReadAllTextAsync(Path.Combine(link.CloudPath, "new.txt")));
            var preserved = Directory.GetFiles(local, "original.txt.cloudbay-local-*");
            Assert.AreEqual(1, preserved.Length);
            Assert.AreEqual("local revision", await File.ReadAllTextAsync(preserved[0]));
        }
        finally
        {
            if (Directory.Exists(local) && (File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(local); // Only the test-owned link layer.
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public async Task LinkPreflightAcceptsMountedRootButRejectsSourceAlias()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows directory mounts are required.");
        var temp = Path.Combine(Path.GetTempPath(), "CloudBayTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(temp, "local", "Work");
        var physicalCloud = Path.Combine(temp, "remote");
        var mount = Path.Combine(temp, "cloud-mount");
        var sourceAlias = Path.Combine(temp, "source-alias");
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(physicalCloud);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "data");
            Directory.CreateSymbolicLink(mount, physicalCloud);
            Directory.CreateSymbolicLink(sourceAlias, Path.GetDirectoryName(source)!);

            var service = new FolderLinkService(new OperationJournal(Path.Combine(temp, "journals")),
                Path.Combine(temp, "records", "links.json"));
            var plan = await service.PreflightAsync(source, mount);
            Assert.AreEqual(1L, plan.FileCount);
            Assert.AreEqual(Path.Combine(mount, "Work"), plan.CloudPath);
            Assert.IsTrue(DirectoryCapacityProbe.TryGet(mount, out ulong free, out ulong total,
                out string? reason), reason);
            Assert.IsTrue(total >= free);

            await Assert.ThrowsExceptionAsync<IOException>(() =>
                service.PreflightAsync(source, sourceAlias));
        }
        finally
        {
            if (Directory.Exists(mount)) Directory.Delete(mount);
            if (Directory.Exists(sourceAlias)) Directory.Delete(sourceAlias);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
}

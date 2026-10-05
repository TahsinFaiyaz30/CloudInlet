using System.Text.Json;
using CloudBay.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class UpdateMaintenanceTests
{
    [TestMethod]
    public void ManualDeleteRemovesCurrentAndOldWorkerFilesButKeepsPreferences()
    {
        using var fixture = new CacheFixture();
        var host = Directory.CreateDirectory(Path.Combine(fixture.Path, "host")).FullName;
        foreach (var suffix in new[] { ".exe", ".exe.config", ".json", ".ready.json" })
            File.WriteAllText(Path.Combine(host, Guid.NewGuid().ToString("N") + suffix), "fixture");
        var preferences = Path.Combine(fixture.Path, "preferences.json");
        File.WriteAllText(preferences, "preserved");
        File.WriteAllText(Path.Combine(fixture.Path, "install-result.json"), "{}");
        WindowsUpdateInstaller.DeleteInactiveHosts(fixture.Path);
        Assert.AreEqual(0, Directory.EnumerateFiles(host).Count());
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Path, "install-result.json")));
        Assert.AreEqual("preserved", File.ReadAllText(preferences));
    }
    [TestMethod]
    public void ReopeningTheAppDetectsOnlyALiveInstallerLock()
    {
        using var fixture = new CacheFixture();
        Assert.IsFalse(WindowsUpdateInstaller.IsInstallationInProgress(fixture.Path));
        var path = Path.Combine(fixture.Path, "update-install.lock");
        using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.IsTrue(WindowsUpdateInstaller.IsInstallationInProgress(fixture.Path));
        Assert.IsFalse(WindowsUpdateInstaller.IsInstallationInProgress(fixture.Path));
    }
    [DataTestMethod]
    [DataRow("[]")] [DataRow("null")] [DataRow("{\"completedUtc\":123,\"success\":false}")]
    public void UnrelatedResultShapesDoNotPreventStartingTheUpdateScheduler(string json)
    {
        using var fixture = new CacheFixture();
        File.WriteAllText(Path.Combine(fixture.Path, "install-result.json"), json);
        Assert.IsNull(WindowsUpdateInstaller.ReadFailureAndPruneHosts(fixture.Path));
    }

    [TestMethod]
    public void RecentInstallerFailureIsReportedWithoutReadingBackupState()
    {
        using var fixture = new CacheFixture();
        File.WriteAllText(Path.Combine(fixture.Path, "install-result.json"), JsonSerializer.Serialize(new
        { success = false, completedUtc = DateTimeOffset.UtcNow, message = "Installer rollback completed." }));
        Assert.AreEqual("Installer rollback completed.", WindowsUpdateInstaller.ReadFailureAndPruneHosts(fixture.Path));
    }

    [TestMethod]
    public void PruningRemovesOnlyOldUnlockedWorkerFilesAndKeepsActiveOrUnrelatedFiles()
    {
        using var fixture = new CacheFixture();
        var host = Directory.CreateDirectory(Path.Combine(fixture.Path, "host")).FullName;
        var old = Path.Combine(host, Guid.NewGuid().ToString("N") + ".json");
        var active = Path.Combine(host, Guid.NewGuid().ToString("N") + ".exe");
        var recent = Path.Combine(host, Guid.NewGuid().ToString("N") + ".ready.json");
        var unrelated = Path.Combine(host, "notes.json");
        foreach (var path in new[] { old, active, recent, unrelated }) File.WriteAllText(path, "fixture");
        foreach (var path in new[] { old, active, unrelated }) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        using (new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            WindowsUpdateInstaller.ReadFailureAndPruneHosts(fixture.Path);
            Assert.IsFalse(File.Exists(old));
            Assert.IsTrue(File.Exists(active)); Assert.IsTrue(File.Exists(recent)); Assert.IsTrue(File.Exists(unrelated));
        }
    }

    private sealed class CacheFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBayUpdateMaintenance-" + Guid.NewGuid().ToString("N"));
        public CacheFixture()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, ".cloudbay-update-cache-v1"), "CloudBay update cache v1\n");
        }
        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(Path);
            if (!root.StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudBayUpdateMaintenance-"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Test cleanup escaped its owned directory.");
            Directory.Delete(root, true);
        }
    }
}

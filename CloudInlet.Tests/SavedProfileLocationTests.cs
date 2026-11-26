using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class SavedProfileLocationTests
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "CloudInlet-ProfileTests", Guid.NewGuid().ToString("N"));
    private string Canonical => Path.Combine(home, "CloudInlet", "Client");
    private string Previous => Path.Combine(home, "CloudBay", "Client");
    private string State => Path.Combine(home, "CloudInlet", "Migration", "Release", "State");
    private string Resolve() => SavedProfileLocation.Resolve(home, "Release");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    [TestMethod]
    public void FreshInstallIgnoresEmptyScaffoldingAndReopensItsOwnProfile()
    {
        Directory.CreateDirectory(Path.Combine(home, "Microsoft", "OneDrive"));
        Directory.CreateDirectory(Path.Combine(Previous, "Updates"));
        Directory.CreateDirectory(Path.Combine(Canonical, "Cache"));
        Assert.AreEqual(Canonical, SavedProfileLocation.Resolve(home, "Release", () => Assert.Fail("A fresh install requires no earlier version.")));
        File.WriteAllText(Path.Combine(Canonical, "activity.txt"), "first launch");
        Assert.AreEqual(Canonical, Resolve());
        Assert.IsFalse(Directory.Exists(State));
    }

    [TestMethod]
    public void AFailedUpgradeCheckDoesNotClaimTheProfileAndASuccessfulCheckRunsOnce()
    {
        Directory.CreateDirectory(Canonical);
        File.WriteAllBytes(Path.Combine(Canonical, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(new AppSettings()));
        Assert.ThrowsException<IOException>(() => SavedProfileLocation.Resolve(home, "Release", () => throw new IOException("Open 1.2.2 first.")));
        Assert.IsFalse(File.Exists(Path.Combine(Canonical, ".cloudinlet-profile-v1")));
        var checks = 0;
        Assert.AreEqual(Canonical, SavedProfileLocation.Resolve(home, "Release", () => checks++));
        Assert.AreEqual(Canonical, SavedProfileLocation.Resolve(home, "Release", () => checks++));
        Assert.AreEqual(1, checks);
    }

    [TestMethod]
    public void UnrelatedPackageHistoryIsNotProofOfAnUpgrade()
    {
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.IsFalse(CloudInlet.Windows.UpgradeBaseline.WasRegistered("CloudInlet.TestFixture.Unpublished_1.2.2.0_x64__0000000000000", user.User!.Value));
    }

    [DataTestMethod]
    [DataRow("Fresh")]
    [DataRow("Canonical")]
    [DataRow("Legacy")]
    public void HonorsTheProtected122LocationAcrossLaunches(string location)
    {
        var selected = location == "Legacy" ? Previous : Canonical;
        Directory.CreateDirectory(selected);
        var path = Path.Combine(State, "state-route.dpapi");
        Protect(path, "CloudInlet.ClientStateRoute.v1", new { Schema = 1, Home = home, Flavor = "Release", Location = location });
        var original = File.ReadAllBytes(path);
        Assert.AreEqual(selected, Resolve());
        Assert.AreEqual(selected, Resolve());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetainedProfileKeepsItsSettingsBytes(bool keptOriginalLocation)
    {
        var selected = keptOriginalLocation ? Previous : Canonical;
        Directory.CreateDirectory(selected);
        var path = Path.Combine(selected, "settings.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new AppSettings { RootPath = Path.Combine(home, "Backups") });
        File.WriteAllBytes(path, bytes);
        Assert.AreEqual(selected, Resolve());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [DataTestMethod]
    [DataRow("legacy")]
    [DataRow("both")]
    [DataRow("unknown")]
    [DataRow("pending")]
    public void UnresolvedOrUnidentifiedStateIsPreserved(string scenario)
    {
        Directory.CreateDirectory(Canonical);
        Directory.CreateDirectory(Previous);
        if (scenario is "legacy" or "both") File.WriteAllText(Path.Combine(Previous, "unidentified-profile-file"), "keep");
        if (scenario is "unknown" or "both") File.WriteAllText(Path.Combine(Canonical, "user-file"), "keep");
        if (scenario == "pending") Protect(Path.Combine(State, "state-route.dpapi"), "CloudInlet.ClientStateRoute.v1",
            new { Schema = 1, Home = home, Flavor = "Release", Location = "Pending" });
        Assert.ThrowsException<IOException>(() => Resolve());
        Assert.IsFalse(File.Exists(Path.Combine(Canonical, ".cloudinlet-profile-v1")));
        Assert.IsFalse(File.Exists(Path.Combine(Previous, ".cloudinlet-profile-v1")));
        if (scenario is "unknown" or "both") Assert.AreEqual("keep", File.ReadAllText(Path.Combine(Canonical, "user-file")));
    }

    [TestMethod]
    public void FinishedFolderReviewDoesNotBlockFreshStartup()
    {
        var id = Guid.NewGuid().ToString("N");
        Protect(Path.Combine(State, "finished-" + id + ".dpapi"), "CloudInlet.MigrationConsent.v1",
            new { SchemaVersion = 1, Id = id, SelectedIds = new[] { "folder-pictures" } });
        File.WriteAllText(Path.Combine(State, "session-" + id + ".json"), "{}");
        Directory.CreateDirectory(Path.Combine(State, "receipts-" + id));
        Assert.AreEqual(Canonical, Resolve());
        File.WriteAllText(Path.Combine(State, "pending-review.dpapi"), "unfinished");
        Assert.ThrowsException<IOException>(() => Resolve());
    }

    [DataTestMethod]
    [DataRow("Prepared", false)]
    [DataRow("RestartRequired", false)]
    [DataRow("Complete", true)]
    [DataRow("RolledBack", true)]
    public void ReadsHistoricalInstallerOutcomeWithoutChangingIt(string state, bool finished)
    {
        var path = Path.Combine(home, "CloudInlet", "Migration", "Release", "InstallerRecovery", "installation.dpapi");
        Protect(path, "CloudInlet.InstallationMigrationRecovery.v1", new { SchemaVersion = 1, BuildFlavor = "Release", State = state });
        var bytes = File.ReadAllBytes(path);
        if (finished) Assert.AreEqual(Canonical, Resolve());
        else Assert.ThrowsException<IOException>(() => Resolve());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void RejectsAnUnauthenticRoute()
    {
        var path = Path.Combine(State, "state-route.dpapi");
        Protect(path, "another purpose", new { Schema = 1, Home = home, Flavor = "Release", Location = "Canonical" });
        Assert.ThrowsException<CryptographicException>(() => Resolve());
        Assert.IsFalse(Directory.Exists(Canonical));
    }

    [DataTestMethod]
    [DataRow("1.2.1", false)]
    [DataRow("1.2", false)]
    [DataRow("1.2.2", true)]
    [DataRow("1.2.2.0", true)]
    [DataRow("1.2.5", true)]
    public void VersionedInstallationsRequireThe122Baseline(string version, bool supported)
    {
        if (supported) PreviousInstallation.RequireSupported(version);
        else Assert.ThrowsException<IOException>(() => PreviousInstallation.RequireSupported(version));
    }

    private static void Protect(string file, string purpose, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), Encoding.UTF8.GetBytes(purpose),
            DataProtectionScope.CurrentUser));
    }
}

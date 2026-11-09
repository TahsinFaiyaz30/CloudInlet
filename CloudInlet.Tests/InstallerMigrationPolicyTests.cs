using CloudInlet.Packaging;
using CloudInlet.Core.Updates;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class InstallerMigrationPolicyTests
{
    [DataTestMethod]
    [DataRow("Release", @"C:\Apps\CloudBay", @"C:\Apps\CloudInlet")]
    [DataRow("Debug", @"C:\Apps\CloudBay Debug", @"C:\Apps\CloudInlet Debug")]
    [DataRow("Release", @"C:\Apps\CloudInlet", @"C:\Apps\CloudInlet")]
    [DataRow("Release", @"C:\Apps\Personal", @"C:\Apps\Personal")]
    public void MajorUpgradeTargetsOnlyReviewedSibling(string flavor, string source, string expected)
    {
        Assert.AreEqual(expected, InstallationMigrationPolicy.Validate(source, expected, flavor, "Msi", "1.2.0", "1.2.1"));
    }

    [DataTestMethod]
    [DataRow(@"C:\Apps\CloudBay", @"C:\Other\CloudInlet", "Release", "Msi", "1.2.0", "1.3.0")]
    [DataRow(@"C:\Apps\Personal", @"C:\Apps\CloudInlet", "Release", "Msi", "1.2.0", "1.3.0")]
    [DataRow(@"C:\Apps\CloudBay", @"C:\Apps\CloudInlet", "Release", "Msi", "1.2.0", "1.2.0")]
    [DataRow(@"C:\Apps\CloudBay", @"C:\Apps\CloudInlet", "Release", "Msi", "1.2.0", "1.1.2")]
    [DataRow(@"C:\Apps\CloudBay Debug", @"C:\Apps\CloudInlet", "Release", "Msi", "1.2.0", "1.3.0")]
    public void RejectsRelocationOutsideMajorUpgradeContract(string source, string target, string flavor, string kind, string fromVersion, string toVersion) =>
        Assert.ThrowsException<IOException>(() => InstallationMigrationPolicy.Validate(source, target, flavor, kind, fromVersion, toVersion));

    [TestMethod]
    public void ExeRelocationRequiresStrictlyNewerMatchingPackageAndDistinctReviewedSibling()
    {
        Assert.AreEqual(@"C:\Apps\CloudInlet", InstallationMigrationPolicy.Validate(@"C:\Apps\CloudBay", @"C:\Apps\CloudInlet", "Release", "Exe", "1.2.0", "1.2.1"));
        Assert.ThrowsException<IOException>(() => InstallationMigrationPolicy.Validate(@"C:\Apps\CloudBay", @"C:\Apps\CloudInlet", "Release", "Exe", "1.2.0", "1.2.0"));
        Assert.ThrowsException<IOException>(() => InstallationMigrationPolicy.Validate(@"C:\Apps\CloudBay", @"C:\Other\CloudInlet", "Release", "Exe", "1.2.0", "1.2.1"));
    }

    [DataTestMethod]
    [DataRow(@"C:CloudBay")]
    [DataRow(@"\CloudBay")]
    [DataRow(@"\\server\share\CloudBay")]
    [DataRow(@"\\?\C:\Apps\CloudBay")]
    [DataRow(@"C:\Apps\CloudBay:other")]
    [DataRow(@"C:\Apps\CloudBay.")]
    [DataRow(@"C:\Apps\CloudBay ")]
    public void RejectsPathAliases(string path) => Assert.ThrowsException<IOException>(() => InstallationMigrationPolicy.Normalize(path));

    [TestMethod]
    public void AppOffersOnlyInstallationModesWithAnAcceptedCoupledAdapter()
    {
        var legacyExe = new UpdateInstallation(new InstalledUpdateIdentity("1.2.0", UpdateBuildFlavor.Release, UpdateInstallerKind.Exe), @"C:\Apps\CloudBay");
        Assert.IsTrue(legacyExe.CanMigrateInstallation);
        Assert.AreEqual(@"C:\Apps\CloudInlet", legacyExe.MigrationTarget());
        foreach (var path in new[] { @"C:\Apps\Personal", @"C:\Apps\CloudInlet" })
        {
            var customExe = legacyExe with { Directory = path };
            Assert.IsTrue(customExe.CanMigrateInstallation);
            Assert.AreEqual(path, customExe.MigrationTarget());
            Assert.AreEqual(path, InstallationMigrationPolicy.Validate(path, path, "Release", "Exe", "1.2.0", "1.2.1"));
            var msi = customExe with { Identity = customExe.Identity with { InstallerKind = UpdateInstallerKind.Msi } };
            Assert.IsTrue(msi.CanMigrateInstallation); Assert.AreEqual(path, msi.MigrationTarget());
        }
        foreach (var kind in new[] { UpdateInstallerKind.Portable, UpdateInstallerKind.Store })
            Assert.IsFalse((legacyExe with { Identity = legacyExe.Identity with { InstallerKind = kind } }).CanMigrateInstallation);
    }

    [TestMethod]
    public void ReadsDeclinedLegacyRegistrationAndCanonicalRegistrationIndependently()
    {
        foreach (var brand in new[] { "CloudBay", "CloudInlet" })
        {
            var values = Registration(brand);
            var found = DistributionRegistration.Read("Debug", "Msi", (key, name) => values.GetValueOrDefault(key + "|" + name));
            Assert.IsNotNull(found);
            Assert.AreEqual(brand, found.Brand);
            Assert.AreEqual(@"C:\Apps\CloudBay Debug", found.Directory);
        }
    }

    [TestMethod]
    public void ConflictingRegistrationsFailRatherThanGuessing()
    {
        var values = Registration("CloudBay");
        foreach (var pair in Registration("CloudInlet")) values.Add(pair.Key, pair.Value);
        Assert.ThrowsException<IOException>(() => DistributionRegistration.Read("Debug", "Msi", (key, name) => values.GetValueOrDefault(key + "|" + name)));
    }

    [TestMethod]
    public void IncompleteRegistrationFailsRatherThanTreatingInstallationAsFresh()
    {
        var values = Registration("CloudBay");
        values.Remove(DistributionRegistration.Key("CloudBay", "Debug", "Msi") + "|BuildFlavor");
        Assert.ThrowsException<IOException>(() => DistributionRegistration.Read("Debug", "Msi", (key, name) => values.GetValueOrDefault(key + "|" + name)));
    }

    private static Dictionary<string, string> Registration(string brand)
    {
        var key = DistributionRegistration.Key(brand, "Debug", "Msi");
        return new Dictionary<string, string> { [key + "|InstallDirectory"] = @"C:\Apps\CloudBay Debug", [key + "|Version"] = "1.2.0", [key + "|BuildFlavor"] = "Debug" };
    }
}

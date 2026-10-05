using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.Updates;
using CloudBay.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class UpdateInstallationTests
{
    private static UpdateBuildFlavor CompiledFlavor => BuildInfo.Flavor == "Debug" ? UpdateBuildFlavor.Debug : UpdateBuildFlavor.Release;

    [TestMethod]
    public void MissingMetadataIsPortableAndUsesOnlyTheCompiledBuildIdentity()
    {
        using var fixture = new InstallationFixture();
        var installation = UpdateInstallation.LoadAt(fixture.Directory, packaged: false);
        Assert.AreEqual(BuildInfo.Version, installation.Identity.Version);
        Assert.AreEqual(CompiledFlavor, installation.Identity.BuildFlavor);
        Assert.AreEqual(UpdateInstallerKind.Portable, installation.Identity.InstallerKind);
        Assert.IsFalse(installation.CanInstall); Assert.IsNull(installation.Notice);
    }

    [DataTestMethod]
    [DataRow("Exe")]
    [DataRow("Msi")]
    public void ValidInstallerMetadataRetainsItsOriginalPackageKind(string kind)
    {
        using var fixture = new InstallationFixture(); fixture.Write(kind: kind);
        var installation = UpdateInstallation.LoadAt(fixture.Directory + Path.DirectorySeparatorChar, packaged: false);
        Assert.AreEqual(Enum.Parse<UpdateInstallerKind>(kind), installation.Identity.InstallerKind);
        Assert.AreEqual(BuildInfo.Version, installation.Identity.Version);
        Assert.AreEqual(CompiledFlavor, installation.Identity.BuildFlavor);
        Assert.AreEqual(fixture.Directory, installation.Directory);
        Assert.IsTrue(installation.CanInstall); Assert.IsNull(installation.Notice);
    }

    [TestMethod]
    public void MetadataCannotOverrideCompiledVersionFlavorOrArchitecture()
    {
        using var fixture = new InstallationFixture();
        foreach (var replacement in new (string Key, object? Value)[]
        {
            ("version", "9.9.9"), ("buildFlavor", CompiledFlavor == UpdateBuildFlavor.Release ? "Debug" : "Release"),
            ("architecture", "arm64"), ("schemaVersion", 2), ("installerKind", "Store")
        })
        {
            var metadata = fixture.Metadata(); metadata[replacement.Key] = replacement.Value; fixture.Write(metadata);
            AssertUntrusted(Load(fixture));
        }
    }

    [TestMethod]
    public void ExeAndMsiMetadataMustMatchTheCurrentDirectoryAndPerUserScope()
    {
        using var fixture = new InstallationFixture();
        foreach (var kind in new[] { "Exe", "Msi" })
            foreach (var replacement in new (string Key, object? Value)[]
            {
                ("installScope", "perMachine"), ("installScope", null), ("installDirectory", null),
                ("installDirectory", fixture.Directory + "-different"), ("installDirectory", "\0")
            })
            {
                var metadata = fixture.Metadata(kind); metadata[replacement.Key] = replacement.Value; fixture.Write(metadata);
                AssertUntrusted(Load(fixture));
            }
    }

    [TestMethod]
    public void PackagedInstallationUsesStoreEvenWhenAnInstallerMetadataFileIsPresentOrBroken()
    {
        using var fixture = new InstallationFixture();
        foreach (var text in new[] { JsonSerializer.Serialize(fixture.Metadata("Exe")), "{broken" })
        {
            File.WriteAllText(fixture.MetadataPath, text);
            var installation = UpdateInstallation.LoadAt(fixture.Directory, packaged: true);
            Assert.AreEqual(UpdateInstallerKind.Store, installation.Identity.InstallerKind);
            Assert.AreEqual(BuildInfo.Version, installation.Identity.Version);
            Assert.AreEqual(CompiledFlavor, installation.Identity.BuildFlavor);
            Assert.IsFalse(installation.CanInstall); Assert.IsNull(installation.Notice);
        }
    }

    [TestMethod]
    public void NumericAndMissingEnumFieldsCannotBeMistakenForAnInstalledExe()
    {
        using var fixture = new InstallationFixture();
        foreach (var key in new[] { "buildFlavor", "installerKind" })
            foreach (var value in new object?[] { 0, 1, -1, "0", "1", "unknown", null })
            {
                var metadata = fixture.Metadata(); metadata[key] = value; fixture.Write(metadata);
                AssertUntrusted(Load(fixture));
            }
        foreach (var key in new[] { "schemaVersion", "version", "buildFlavor", "installerKind", "architecture" })
        {
            var metadata = fixture.Metadata(); metadata.Remove(key); fixture.Write(metadata);
            AssertUntrusted(Load(fixture));
        }
    }

    [TestMethod]
    public void MalformedOrOversizedMetadataProvidesAManualReinstallationNotice()
    {
        using var fixture = new InstallationFixture();
        foreach (var text in new[] { "{broken", "null", "[]", new string(' ', 64 * 1024 + 1) })
        { File.WriteAllText(fixture.MetadataPath, text); AssertUntrusted(Load(fixture)); }
    }

    [TestMethod]
    public void LinkedAndDanglingMetadataFilesAreRejectedWithoutReadingOrCreatingTheirTargets()
    {
        using var fixture = new InstallationFixture();
        var target = Path.Combine(fixture.Directory, "fixture-target.json");
        File.WriteAllText(target, JsonSerializer.Serialize(fixture.Metadata()));
        File.CreateSymbolicLink(fixture.MetadataPath, target);
        AssertUntrusted(Load(fixture)); File.Delete(fixture.MetadataPath);
        File.Delete(target);
        File.CreateSymbolicLink(fixture.MetadataPath, target);
        AssertUntrusted(Load(fixture)); Assert.IsFalse(File.Exists(target));
        File.Delete(fixture.MetadataPath);
    }

    [TestMethod]
    public void CoreBuildIdentityUsesTheCurrentConfigurationAndKeepsDebugStateSeparate()
    {
        Assert.IsTrue(UpdateVersion.TryParse(BuildInfo.Version, out _));
#if CLOUDBAY_DEBUG
        Assert.AreEqual("Debug", BuildInfo.Flavor); Assert.AreEqual("CloudBay Debug", BuildInfo.ProductName);
        Assert.AreEqual(".Debug", BuildInfo.PipeSuffix); Assert.AreEqual("CloudBayDebug", BuildInfo.StartupRegistryName);
        StringAssert.EndsWith(BuildInfo.DefaultDataDirectory, Path.Combine("CloudBay", "Debug", "Client"));
        StringAssert.EndsWith(BuildInfo.DefaultRootPath, "CloudBay Debug");
#else
        Assert.AreEqual("Release", BuildInfo.Flavor); Assert.AreEqual("CloudBay", BuildInfo.ProductName);
        Assert.AreEqual("", BuildInfo.PipeSuffix); Assert.AreEqual("CloudBay", BuildInfo.StartupRegistryName);
        StringAssert.EndsWith(BuildInfo.DefaultDataDirectory, Path.Combine("CloudBay", "Client"));
        StringAssert.EndsWith(BuildInfo.DefaultRootPath, "CloudBay");
#endif
    }

    private static UpdateInstallation Load(InstallationFixture fixture) => UpdateInstallation.LoadAt(fixture.Directory, packaged: false);
    private static void AssertUntrusted(UpdateInstallation installation)
    {
        Assert.AreEqual(UpdateInstallerKind.Portable, installation.Identity.InstallerKind);
        Assert.AreEqual(BuildInfo.Version, installation.Identity.Version);
        Assert.AreEqual(CompiledFlavor, installation.Identity.BuildFlavor);
        Assert.IsFalse(installation.CanInstall); Assert.IsNotNull(installation.Notice);
    }
    private sealed class InstallationFixture : IDisposable
    {
        public readonly string Directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CloudBayDistributionTests-" + Guid.NewGuid().ToString("N")));
        public string MetadataPath => Path.Combine(Directory, "distribution.json");
        public InstallationFixture() => System.IO.Directory.CreateDirectory(Directory);
        public Dictionary<string, object?> Metadata(string kind = "Exe") => new()
        {
            ["schemaVersion"] = 1, ["version"] = BuildInfo.Version, ["buildFlavor"] = BuildInfo.Flavor,
            ["installerKind"] = kind, ["architecture"] = "x64", ["installScope"] = "perUser", ["installDirectory"] = Directory
        };
        public void Write(string kind = "Exe") => Write(Metadata(kind));
        public void Write(Dictionary<string, object?> metadata) => File.WriteAllText(MetadataPath, JsonSerializer.Serialize(metadata));
        public void Dispose()
        {
            if (!Directory.StartsWith(Path.Combine(Path.GetTempPath(), "CloudBayDistributionTests-"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid distribution test cleanup path.");
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

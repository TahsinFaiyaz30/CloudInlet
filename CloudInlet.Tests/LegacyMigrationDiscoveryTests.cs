using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Updates;
using CloudInlet.Windows;
using CloudInlet.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class LegacyMigrationDiscoveryTests
{
    private const string LocalAppData = @"C:\MigrationFixture\Local";
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private const string OldInstall = @"C:\MigrationFixture\Local\Programs\CloudBay";
    private const string OldRoot = @"C:\MigrationFixture\CloudBay";
    private const string NewRoot = @"C:\MigrationFixture\CloudInlet";
    private const string State = @"C:\MigrationFixture\Local\CloudBay\Client";

    private static AppSettings CurrentSettings() => new() { RootPath = NewRoot, Prefix = "CloudInlet/" };
    private static UpdateInstallation Installation(string path = OldInstall, UpdateInstallerKind kind = UpdateInstallerKind.Exe,
        UpdateBuildFlavor flavor = UpdateBuildFlavor.Release) => new(new("1.2.0", flavor, kind), path);
    private static LegacyMigrationFacts Facts() => new()
    {
        LocalAppDataDirectory = LocalAppData, UserSid = Sid,
        // No actual fixture directories or registry reads are needed for discovery tests.
        InspectPath = _ => new(false)
    };
    private static LegacyDistributionRegistration Registration(UpdateInstallerKind kind = UpdateInstallerKind.Exe,
        string path = OldInstall, UpdateBuildFlavor flavor = UpdateBuildFlavor.Release) =>
        new(@"Software\CloudBay\Distribution\" + flavor + "\\" + kind, path, flavor, kind);
    private static LegacySyncRootRegistration RootRegistration(string path = OldRoot, string? sid = null,
        Guid? provider = null) => new("CloudBay!" + (sid ?? Sid) + "!" + new string('A', 24), path,
            provider ?? WindowsPlaceholderService.ProviderId);

    [TestMethod]
    public void MissingDestinationsAreOrdinaryReviewCandidatesWithoutMutationOrPreselection()
    {
        var inspected = new List<string>();
        var facts = Facts() with { InspectPath = path => { inspected.Add(path); return new(false); }, Distributions = [Registration()] };
        var settings = CurrentSettings() with { RootPath = OldRoot, Prefix = "CloudBay/" };
        var plan = LegacyMigrationDiscovery.Discover(settings, Installation(), State, facts);
        CollectionAssert.AreEquivalent(new[] { "installation", "application-state", "sync-folder", "cloud-prefix" }, plan.Items.Select(item => item.Id).ToArray());
        Assert.IsTrue(plan.Items.All(item => item.BlockedReason == LegacyMigrationDiscovery.ReviewOnlyReason));
        Assert.AreEqual(6, inspected.Count);
        Assert.AreEqual(OldRoot, settings.RootPath);
        Assert.AreEqual("CloudBay/", settings.Prefix);
        var selection = new MigrationSelection(plan);
        Assert.AreEqual(0, selection.SelectedIds.Count);
        Assert.IsFalse(selection.CanContinue);
    }

    [TestMethod]
    public void InstallerRegistryAndFolderAreOneUnitWithRepairOwnership()
    {
        var plan = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(kind: UpdateInstallerKind.Msi),
            @"C:\OtherState", Facts() with { Distributions = [Registration(UpdateInstallerKind.Msi)] });
        var item = plan.Items.Single();
        Assert.AreEqual(MigrationArea.Installation, item.Area);
        Assert.AreEqual(OldInstall, item.CurrentLocation);
        Assert.AreEqual(@"C:\MigrationFixture\Local\Programs\CloudInlet", item.ProposedLocation);
        StringAssert.Contains(item.Description, @"Software\CloudBay\Distribution\Release\Msi");
        StringAssert.Contains(item.Description, "MSI component keys");
        StringAssert.Contains(item.Description, "shortcuts");
    }

    [TestMethod]
    public void AlreadyBrandedInstallStillOffersOwnedRegistryMigration()
    {
        var path = @"C:\MigrationFixture\Local\Programs\CloudInlet";
        var plan = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(path), @"C:\OtherState",
            Facts() with { Distributions = [Registration(path: path)] });
        var item = plan.Items.Single();
        Assert.AreEqual(@"HKEY_CURRENT_USER\Software\CloudBay\Distribution\Release\Exe", item.CurrentLocation);
        Assert.AreEqual(@"HKEY_CURRENT_USER\Software\CloudInlet\Distribution\Release\Exe", item.ProposedLocation);
    }

    [TestMethod]
    public void CanonicalRegistrationStillOffersAnOldNamedFolderButNotACompletedMigration()
    {
        var registration = Registration() with { RegistryPath = @"Software\CloudInlet\Distribution\Release\Exe" };
        var item = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState",
            Facts() with { Distributions = [registration] }).Items.Single();
        Assert.AreEqual(OldInstall, item.CurrentLocation);
        Assert.AreEqual(@"C:\MigrationFixture\Local\Programs\CloudInlet", item.ProposedLocation);
        Assert.AreEqual(LegacyMigrationDiscovery.ReviewOnlyReason, item.BlockedReason);
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(item.ProposedLocation), @"C:\OtherState",
            Facts() with { Distributions = [registration with { InstallDirectory = item.ProposedLocation }] }).Items.Count);
        var conflict = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState",
            Facts() with { Distributions = [Registration(), registration] }).Items.Single();
        StringAssert.Contains(conflict.BlockedReason!, "Conflicting installer registrations");
    }

    [TestMethod]
    public void UnregisteredPortableMismatchedAndInvalidInstallationsAreNotClaimed()
    {
        var facts = Facts() with { Distributions = [Registration(path: @"C:\Unrelated\CloudBay"), Registration(UpdateInstallerKind.Msi)] };
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState", facts).Items.Count);
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(kind: UpdateInstallerKind.Portable),
            @"C:\OtherState", Facts() with { Distributions = [Registration()] }).Items.Count);
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation() with { Notice = "Identity unverified" },
            @"C:\OtherState", Facts() with { Distributions = [Registration()] }).Items.Count);
    }

    [TestMethod]
    public void ArbitraryRegistryNamesAndOtherBuildFlavorsAreNotOwned()
    {
        var facts = Facts() with { Distributions = [Registration() with { RegistryPath = @"Software\OtherApp\CloudBay" }, Registration(flavor: UpdateBuildFlavor.Debug)] };
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState", facts).Items.Count);
    }

    [TestMethod]
    public void StateMigrationRequiresExactCanonicalLegacyStatePath()
    {
        foreach (var path in new[] { @"C:\Elsewhere\CloudBay\Client", State + @"\Validation", @"C:\MigrationFixture\Local\CloudBay\Debug\Client" })
            Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), path, Facts()).Items.Count);
        var plan = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), State + "\\", Facts());
        Assert.AreEqual(@"C:\MigrationFixture\Local\CloudInlet\Client", plan.Items.Single().ProposedLocation);
    }

    [TestMethod]
    public void DebugBuildUsesSeparateStateAndExactDebugFolderNames()
    {
        var install = OldInstall + " Debug";
        var plan = LegacyMigrationDiscovery.Discover(CurrentSettings() with { RootPath = OldRoot + " Debug" },
            Installation(install, flavor: UpdateBuildFlavor.Debug), @"C:\MigrationFixture\Local\CloudBay\Debug\Client",
            Facts() with { Distributions = [Registration(path: install, flavor: UpdateBuildFlavor.Debug)] });
        Assert.AreEqual(3, plan.Items.Count);
        Assert.AreEqual(NewRoot + " Debug", plan.Items.Single(item => item.Id == "sync-folder").ProposedLocation);
        Assert.AreEqual(@"C:\MigrationFixture\Local\CloudInlet\Debug\Client", plan.Items.Single(item => item.Id == "application-state").ProposedLocation);
    }

    [TestMethod]
    public void PersonalFilenameSubstringsAndParentDirectoriesAreNeverRenamed()
    {
        var settings = CurrentSettings() with
        {
            RootPath = OldRoot + @"\Documents",
            CustomBackups = [new("Research", @"C:\MigrationFixture\CloudBay papers", "CloudInlet/.cloudbay-backups/Research/")]
        };
        var plan = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts());
        Assert.IsFalse(plan.Items.Any(item => item.Area is MigrationArea.SyncFolder or MigrationArea.BackupFolder));
        Assert.AreEqual(1, plan.Items.Count); // Only the app-generated cloud backup namespace.
    }

    [TestMethod]
    public void ExplicitCustomBackupRootWithExactLegacyLeafIsIndependentlyReviewable()
    {
        var settings = CurrentSettings() with { CustomBackups = [new("Projects", @"D:\Projects\CloudBay", "user-prefix/")] };
        var item = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts()).Items.Single();
        Assert.AreEqual(MigrationArea.BackupFolder, item.Area);
        Assert.AreEqual(@"D:\Projects\CloudInlet", item.ProposedLocation);
        StringAssert.Contains(item.Description, "explicitly saved custom backup source");
    }

    [TestMethod]
    public void MainRootKnownMappingsAndNativeRegistrationAreOneAtomicItem()
    {
        var settings = CurrentSettings() with { RootPath = OldRoot, Backups = [new("Documents", @"C:\Original\Documents", OldRoot + @"\Documents")] };
        var facts = Facts() with
        {
            KnownFolders = new Dictionary<string, string> { ["Documents"] = OldRoot + @"\Documents" },
            SyncRoots = [RootRegistration()]
        };
        var item = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", facts).Items.Single();
        Assert.AreEqual("sync-folder", item.Id);
        StringAssert.Contains(item.Description, "Documents Windows mapping");
        StringAssert.Contains(item.Description, NewRoot + @"\Documents");
        StringAssert.Contains(item.Description, "Files On-Demand registration");
        StringAssert.Contains(item.Description, "CloudInlet!" + Sid);
    }

    [TestMethod]
    public void ChangedKnownFolderMappingBlocksWholeCoupledRootChange()
    {
        var settings = CurrentSettings() with { RootPath = OldRoot, Backups = [new("Documents", @"C:\Original\Documents", OldRoot + @"\Documents")] };
        var item = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { KnownFolders = new Dictionary<string, string> { ["Documents"] = @"C:\OtherProvider\Documents" } }).Items.Single();
        StringAssert.Contains(item.BlockedReason!, "does not match this saved backup");
    }

    [TestMethod]
    public void NativeRegistrationRequiresExactProviderUserFlavorAndSavedRoot()
    {
        var facts = Facts() with
        {
            SyncRoots = [RootRegistration(NewRoot, sid: "S-1-5-21-other"), RootRegistration(NewRoot, provider: Guid.Empty),
                RootRegistration(@"C:\Other\CloudBay"), RootRegistration(NewRoot) with { Id = "CloudBay!" + Sid + "!BAD" }]
        };
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState", facts).Items.Count);
        var item = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState",
            Facts() with { SyncRoots = [RootRegistration(NewRoot)] }).Items.Single();
        Assert.AreEqual(MigrationArea.WindowsIntegration, item.Area);
    }

    [TestMethod]
    public void CloudPrefixIncludesCoupledBackupNamespacesAndDoesNotRewriteUserPrefixes()
    {
        var settings = CurrentSettings() with
        {
            Prefix = "CloudBay/", CustomBackups = [new("Projects", @"D:\Projects", "CloudBay/.cloudbay-backups/Projects/")]
        };
        var item = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts()).Items.Single();
        Assert.AreEqual("cloud-prefix", item.Id);
        StringAssert.Contains(item.Description, "CloudInlet/.cloudinlet-backups/Projects/");
        var personal = settings with { Prefix = "MyCloudBay/", CustomBackups = [new("Projects", @"D:\Projects", "CloudBay/personal/")] };
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(personal, Installation(), @"C:\OtherState", Facts()).Items.Count);
    }

    [TestMethod]
    public void EmptyNativeProviderGuidRequiresExactPersistedAccountAndRegistrationId()
    {
        var settings = CurrentSettings() with { AccountId = "fixture", BucketId = "bucket" };
        var account = "fixture:bucket:" + settings.Prefix;
        var id = "CloudBay!" + Sid + "!" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account)))[..24];
        var registration = new LegacySyncRootRegistration(id, NewRoot, Guid.Empty, account);
        var plan = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts() with { SyncRoots = [registration] });
        Assert.AreEqual(1, plan.Items.Count);
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { SyncRoots = [registration with { AccountIdentity = "wrong account" }] }).Items.Count);
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { SyncRoots = [registration with { Id = "CloudBay!" + Sid + "!" + new string('A', 24) }] }).Items.Count);
    }

    [TestMethod]
    public void ExistingAndUnreadableDestinationsHaveAccurateDistinctReasons()
    {
        var settings = CurrentSettings() with { RootPath = OldRoot };
        var existing = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { InspectPath = _ => new(true) }).Items.Single();
        StringAssert.Contains(existing.BlockedReason!, "already exists");
        var linked = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { InspectPath = _ => new(false, true) }).Items.Single();
        StringAssert.Contains(linked.BlockedReason!, "reparse-point");
        var unreadable = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { InspectPath = _ => new(false, ReadError: "Access denied") }).Items.Single();
        StringAssert.Contains(unreadable.BlockedReason!, "Access denied");
    }

    [TestMethod]
    public void StoreApplicationAndStartupIdsAreOnePackageChangeOnlyForStore()
    {
        var facts = Facts() with { StoreApplicationId = "CloudBay", StoreStartupTaskId = "CloudBayStartup" };
        var item = LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(kind: UpdateInstallerKind.Store),
            @"C:\OtherState", facts).Items.Single();
        Assert.AreEqual("store-application-id", item.Id);
        StringAssert.Contains(item.Description, "CloudBayStartup → CloudInletStartup");
        StringAssert.Contains(item.Description, "user-disabled");
        Assert.AreEqual(0, LegacyMigrationDiscovery.Discover(CurrentSettings(), Installation(), @"C:\OtherState", facts).Items.Count);
    }

    [TestMethod]
    public void ReviewFingerprintChangesWithDestinationConflictFacts()
    {
        var settings = CurrentSettings() with { RootPath = OldRoot };
        var first = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts());
        var second = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts());
        var conflict = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState", Facts() with { InspectPath = _ => new(true) });
        Assert.AreEqual(first.Fingerprint, second.Fingerprint);
        Assert.AreNotEqual(first.Fingerprint, conflict.Fingerprint);
    }

    [TestMethod]
    public void SourceLinksAndReadFailuresAreNotTreatedAsProvenMovableDirectories()
    {
        var settings = CurrentSettings() with { RootPath = OldRoot };
        var item = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { InspectPath = path => path == OldRoot ? new(true, true, "Source access denied") : new(false) }).Items.Single();
        StringAssert.Contains(item.BlockedReason!, "current location");
        StringAssert.Contains(item.BlockedReason!, "Source access denied");
        StringAssert.Contains(item.BlockedReason!, "reparse-point");
    }

    [TestMethod]
    public void MappingMismatchDoesNotHideDestinationConflict()
    {
        var settings = CurrentSettings() with { RootPath = OldRoot, Backups = [new("Documents", @"C:\Original\Documents", OldRoot + @"\Documents")] };
        var item = LegacyMigrationDiscovery.Discover(settings, Installation(), @"C:\OtherState",
            Facts() with { InspectPath = path => new(path == NewRoot) }).Items.Single();
        StringAssert.Contains(item.BlockedReason!, "already exists");
        StringAssert.Contains(item.BlockedReason!, "does not match this saved backup");
    }
}

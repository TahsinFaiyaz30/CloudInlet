using System.Security.Cryptography;
using CloudInlet.Packaging;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class InstallerMigrationRecoveryTests
{
    [DataTestMethod]
    [DataRow(1, -1)] // advertised after failed rollback
    [DataRow(2, 3)] // installed for a different user
    [DataRow(-1, -1)]
    [DataRow(5, 1)] // advertised main feature
    [DataRow(5, 2)] // absent main feature
    public void NativeMsiRegistrationRefusesUnserviceableProducts(int state, int feature)
    {
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.VerifyMsiRegistration("Debug", "1.2.1", _ =>
            new[] { new InstallerMigrationRecovery.MsiProduct("{36221464-5D70-4AA5-B140-CE9FE88BF798}", state, "1.2.1", feature) }));
    }

    [TestMethod]
    public void NativeMsiRegistrationRequiresOneMatchingInstalledVersion()
    {
        var installed = new InstallerMigrationRecovery.MsiProduct("{36221464-5D70-4AA5-B140-CE9FE88BF798}", 5, "1.2.1", 3);
        string? family = null;
        InstallerMigrationRecovery.VerifyMsiRegistration("Debug", "1.2.1", code => { family = code; return new[] { installed }; });
        Assert.AreEqual("{14BA2C55-3BED-4F8C-927B-2157E7852A96}", family);
        InstallerMigrationRecovery.VerifyMsiRegistration("Release", "1.2.1", code => { family = code; return new[] { installed }; });
        Assert.AreEqual("{7C2B596C-321A-4F07-9A96-C2E83136223D}", family);
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.VerifyMsiRegistration("Debug", "1.2.2", _ => new[] { installed }));
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.VerifyMsiRegistration("Debug", "1.2.1", _ => Array.Empty<InstallerMigrationRecovery.MsiProduct>()));
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.VerifyMsiRegistration("Debug", "1.2.1", _ => new[] { installed, installed }));
    }

    [TestMethod]
    public void NativeFailedRollbackRetainsReceiptWhenNativeRegistrationIsOnlyAdvertised()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open()) { fixture.Begin(recovery); recovery.RecordInstallerExit(fixture.Hash, 1603); }
        var path = Path.Combine(fixture.RecoveryRoot, "installation.dpapi"); var original = File.ReadAllBytes(path);
        using var check = new InstallerMigrationRecovery("Debug", fixture.RecoveryRoot, () => fixture.Boot,
            verifyOriginal: _ => InstallerMigrationRecovery.VerifyMsiRegistration("Debug", "1.2.0", _ =>
                new[] { new InstallerMigrationRecovery.MsiProduct("{36221464-5D70-4AA5-B140-CE9FE88BF798}", 1, "", -1) }));
        var outcome = check.Resolve();
        Assert.IsTrue(outcome.Pending); Assert.IsFalse(outcome.Reverted);
        StringAssert.Contains(outcome.Message, "Windows Installer");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.ThrowsException<IOException>(check.EnsureNoPending);
        Assert.AreEqual("personal", File.ReadAllText(Path.Combine(fixture.Source, "personal.txt")));
    }

    [TestMethod]
    public void NativeMigrationReviewFinalizesVerifiedNewBootBeforeCheckingPendingRecovery()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        var cache = Path.Combine(fixture.RecoveryRoot, "cache"); Directory.CreateDirectory(cache);
        var result = Path.Combine(cache, "install-result.json");
        File.WriteAllText(result, System.Text.Json.JsonSerializer.Serialize(new { targetVersion = "1.3.0", installDirectory = fixture.Destination,
            migrationRequiresRestart = true, migrationCompleted = false, success = true, message = "Restart Windows" }));
        using (var before = fixture.Open()) Assert.ThrowsException<IOException>(before.EnsureNoPending);
        fixture.Boot = Guid.NewGuid().ToString();

        WindowsUpdateInstaller.EnsureRecoveryAllowsMigrationReview(cache, "Debug", fixture.Open);

        using (var completed = fixture.Open()) completed.EnsureNoPending();
        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result));
        Assert.IsTrue(parsed.RootElement.GetProperty("migrationCompleted").GetBoolean());
        Assert.IsFalse(parsed.RootElement.GetProperty("migrationRequiresRestart").GetBoolean());
        Assert.AreEqual("personal", File.ReadAllText(Path.Combine(fixture.Source, "personal.txt")));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeMigrationReviewRetainsSameBootAndChangedInstallationBlocks(bool changedAfterRestart)
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        if (changedAfterRestart)
        {
            fixture.Boot = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(fixture.Destination, "library.dll"), "changed after verification");
        }
        var receipt = Path.Combine(fixture.RecoveryRoot, "installation.dpapi");
        var original = File.ReadAllBytes(receipt);

        Assert.ThrowsException<IOException>(() => WindowsUpdateInstaller.EnsureRecoveryAllowsMigrationReview(
            Path.Combine(fixture.RecoveryRoot, "cache"), "Debug", fixture.Open));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(receipt));
        using var pending = fixture.Open(); Assert.ThrowsException<IOException>(pending.EnsureNoPending);
    }

    [TestMethod]
    public void NativeMigrationReviewNeverResolvesWhileInstallerOwnsCache()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        fixture.Boot = Guid.NewGuid().ToString();
        var receipt = Path.Combine(fixture.RecoveryRoot, "installation.dpapi"); var original = File.ReadAllBytes(receipt);
        var cache = Path.Combine(fixture.RecoveryRoot, "cache"); Directory.CreateDirectory(cache);
        using (new FileStream(Path.Combine(cache, "update-install.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsException<IOException>(() => WindowsUpdateInstaller.EnsureRecoveryAllowsMigrationReview(cache, "Debug", fixture.Open));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(receipt));
    }

    [TestMethod]
    public void NativeMigrationReviewRetainsUnauthenticatedRecoveryEvidence()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open()) fixture.Begin(recovery);
        var receipt = Path.Combine(fixture.RecoveryRoot, "installation.dpapi");
        var bytes = File.ReadAllBytes(receipt); bytes[bytes.Length / 2] ^= 0x40; File.WriteAllBytes(receipt, bytes);

        Assert.ThrowsException<CryptographicException>(() => WindowsUpdateInstaller.EnsureRecoveryAllowsMigrationReview(
            Path.Combine(fixture.RecoveryRoot, "cache"), "Debug", fixture.Open));

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(receipt));
    }

    [TestMethod]
    public void NativeUntouchedRecoveryReleasesStaleRestartWarningWithoutClaimingMigrationComplete()
    {
        using var fixture = new Fixture(); using (var recovery = fixture.Open()) fixture.Begin(recovery);
        var cache = Path.Combine(fixture.RecoveryRoot, "cache"); Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "install-result.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { targetVersion = "1.3.0", installDirectory = fixture.Source,
            migrationRequiresRestart = true, migrationCompleted = false, success = false, attentionRequired = true, message = "Needs recovery" }));
        Assert.IsTrue(WindowsUpdateInstaller.ResolveInstallationRecovery(cache, "Debug", fixture.Open).Reverted);
        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.IsFalse(parsed.RootElement.GetProperty("migrationRequiresRestart").GetBoolean());
        Assert.IsFalse(parsed.RootElement.GetProperty("migrationCompleted").GetBoolean());
        Assert.IsFalse(parsed.RootElement.GetProperty("success").GetBoolean());
        WindowsUpdateInstaller.EnsureRecoveryAllowsCleanup(cache, "Debug", fixture.RecoveryRoot);
    }
    [TestMethod]
    public void NativeStartupFinalizesOnlyMatchingPresentationAfterVerifiedNewBoot()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        var cache = Path.Combine(fixture.RecoveryRoot, "cache"); Directory.CreateDirectory(cache);
        var result = Path.Combine(cache, "install-result.json");
        File.WriteAllText(result, System.Text.Json.JsonSerializer.Serialize(new { targetVersion = "1.3.0", installDirectory = fixture.Destination,
            migrationRequiresRestart = true, migrationCompleted = false, success = true, attentionRequired = false, message = "Restart required" }));
        var before = File.ReadAllBytes(result);
        Assert.IsTrue(WindowsUpdateInstaller.ResolveInstallationRecovery(cache, "Debug", fixture.Open).Pending);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(result));
        Assert.ThrowsException<IOException>(() => WindowsUpdateInstaller.EnsureRecoveryAllowsCleanup(cache, "Debug", fixture.RecoveryRoot));
        fixture.Boot = Guid.NewGuid().ToString();
        Assert.IsTrue(WindowsUpdateInstaller.ResolveInstallationRecovery(cache, "Debug", fixture.Open).Completed);
        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result));
        Assert.IsFalse(parsed.RootElement.GetProperty("migrationRequiresRestart").GetBoolean());
        Assert.IsTrue(parsed.RootElement.GetProperty("migrationCompleted").GetBoolean());
        WindowsUpdateInstaller.EnsureRecoveryAllowsCleanup(cache, "Debug", fixture.RecoveryRoot);
    }
    [TestMethod]
    public void NativeStartupHonorsInstallerLockAndIgnoresForgedSuccessfulResult()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open()) { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); }
        var cache = Path.Combine(fixture.RecoveryRoot, "cache"); Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "install-result.json"), "{\"success\":true,\"migrationCompleted\":true}");
        using (new FileStream(Path.Combine(cache, "update-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsException<IOException>(() => WindowsUpdateInstaller.ResolveInstallationRecovery(cache, "Debug", fixture.Open));
        Assert.IsTrue(WindowsUpdateInstaller.ResolveInstallationRecovery(cache, "Debug", fixture.Open).Pending);
        Assert.ThrowsException<IOException>(() => WindowsUpdateInstaller.EnsureRecoveryAllowsCleanup(cache, "Debug", fixture.RecoveryRoot));
    }
    [TestMethod]
    public void NativeRestartRequiredMigrationRemainsPendingUntilAnotherBoot()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        {
            fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010);
            recovery.RecordVerifiedInstallation(fixture.Hash);
            var pending = recovery.Resolve();
            Assert.IsTrue(pending.Pending); Assert.IsFalse(pending.Completed);
            Assert.ThrowsException<IOException>(recovery.EnsureNoPending);
        }
        fixture.Boot = Guid.NewGuid().ToString();
        using (var recovery = fixture.Open())
        {
            var completed = recovery.Resolve();
            Assert.IsFalse(completed.Pending); Assert.IsTrue(completed.Completed);
            Assert.AreEqual("1.3.0", completed.TargetVersion);
            Assert.AreEqual(fixture.Destination, completed.InstallDirectory);
            recovery.EnsureNoPending();
            Assert.IsTrue(recovery.Resolve().Completed);
            Assert.AreEqual("personal", File.ReadAllText(Path.Combine(fixture.Source, "personal.txt")));
        }
    }

    [TestMethod]
    public void NativeRecoveryCanCompleteVerifiedInstallationWithoutRestartWhenInstallerReturnsZero()
    {
        using var fixture = new Fixture(); using var recovery = fixture.Open();
        fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 0);
        recovery.RecordVerifiedInstallation(fixture.Hash);
        Assert.IsTrue(recovery.Resolve().Completed);
    }

    [TestMethod]
    public void NativeOlderBootProofRemainsPendingWithoutRewritingRecoveryEvidence()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        var path = Path.Combine(fixture.RecoveryRoot, "installation.dpapi");
        var entropy = System.Text.Encoding.UTF8.GetBytes("CloudInlet.InstallationMigrationRecovery.v1");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
        var record = System.Text.Json.Nodes.JsonNode.Parse(plain)!.AsObject();
        Assert.IsTrue(record.Remove("BootIdentityVersion"));
        var oldBytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(record.ToJsonString()), entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, oldBytes);
        fixture.Boot = Guid.NewGuid().ToString("D");
        using var resumed = fixture.Open();
        var result = resumed.Resolve();
        Assert.IsTrue(result.Pending); Assert.IsFalse(result.Completed);
        StringAssert.Contains(result.Message, "older restart check");
        CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void NativeInterruptedAfterInstallerOutcomeRecoversVerificationAfterReboot()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); }
        fixture.Boot = Guid.NewGuid().ToString();
        using var resumed = fixture.Open();
        Assert.IsTrue(resumed.Resolve().Completed);
    }

    [TestMethod]
    public void NativeChangedDestinationCannotBeAcceptedAfterReboot()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        {
            fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010);
            recovery.RecordVerifiedInstallation(fixture.Hash);
        }
        fixture.Boot = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(fixture.Destination, "library.dll"), "modified");
        using var resumed = fixture.Open();
        var result = resumed.Resolve();
        Assert.IsTrue(result.Pending); StringAssert.Contains(result.Message, "changed after verification");
        Assert.ThrowsException<IOException>(resumed.EnsureNoPending);
    }

    [TestMethod]
    public void NativeReplacedDestinationDirectoryCannotBeAcceptedEvenWithIdenticalFiles()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        fixture.Boot = Guid.NewGuid().ToString();
        Directory.Move(fixture.Destination, fixture.Destination + "-saved");
        Directory.CreateDirectory(fixture.Destination);
        foreach (var file in Directory.EnumerateFiles(fixture.Destination + "-saved"))
            File.Copy(file, Path.Combine(fixture.Destination, Path.GetFileName(file)));
        using var resumed = fixture.Open();
        var result = resumed.Resolve(); Assert.IsTrue(result.Pending); StringAssert.Contains(result.Message, "directory was replaced");
    }

    [TestMethod]
    public void NativeRetiredComponentsMustActuallyBeGoneButUnknownFilesStayUntouched()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        {
            fixture.Begin(recovery); fixture.Install(retire: false); recovery.RecordInstallerExit(fixture.Hash, 3010);
            recovery.RecordVerifiedInstallation(fixture.Hash);
        }
        fixture.Boot = Guid.NewGuid().ToString();
        using var resumed = fixture.Open();
        var result = resumed.Resolve(); Assert.IsTrue(result.Pending); StringAssert.Contains(result.Message, "component remains");
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "CloudInlet.exe")));
        fixture.Retire();
        Assert.IsTrue(resumed.Resolve().Completed);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "personal.txt")));
    }

    [TestMethod]
    public void NativeChangedRegistrationStaysPendingAndRetryDoesNotUndoOtherWork()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        { fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 3010); recovery.RecordVerifiedInstallation(fixture.Hash); }
        fixture.Boot = Guid.NewGuid().ToString(); fixture.RegisteredDestination = false;
        using var resumed = fixture.Open();
        Assert.IsTrue(resumed.Resolve().Pending);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(fixture.Destination, "CloudInlet.exe")));
        fixture.RegisteredDestination = true;
        Assert.IsTrue(resumed.Resolve().Completed);
    }

    [TestMethod]
    public void NativeUntouchedPreparedRequestCanBeRetriedAndCompletedReceiptIsNotReverted()
    {
        using var fixture = new Fixture(); using var recovery = fixture.Open();
        fixture.Begin(recovery);
        Assert.IsTrue(recovery.Resolve().Reverted); recovery.EnsureNoPending();
        fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 0); recovery.RecordVerifiedInstallation(fixture.Hash);
        Assert.IsTrue(recovery.Resolve().Completed);
        File.WriteAllText(Path.Combine(fixture.Destination, "library.dll"), "later version");
        Assert.IsTrue(recovery.Resolve().Completed); recovery.EnsureNoPending();
    }

    [TestMethod]
    public void NativeFailedInstallerReleasesGuardOnlyWhenOriginalIsByteIdentical()
    {
        using var fixture = new Fixture(); using var recovery = fixture.Open();
        fixture.Begin(recovery); recovery.RecordInstallerExit(fixture.Hash, 1603);
        File.WriteAllText(Path.Combine(fixture.Source, "CloudInlet.exe"), "changed");
        Assert.IsTrue(recovery.Resolve().Pending);
        File.WriteAllText(Path.Combine(fixture.Source, "CloudInlet.exe"), "old");
        Assert.IsTrue(recovery.Resolve().Reverted);
    }

    [TestMethod]
    public void NativePreparedRequestDoesNotGuessSuccessFromNewDestinationWithoutInstallerOutcome()
    {
        using var fixture = new Fixture(); using var recovery = fixture.Open();
        fixture.Begin(recovery); fixture.Install();
        Assert.IsTrue(recovery.Resolve().Pending);
        Assert.ThrowsException<IOException>(recovery.EnsureNoPending);
    }

    [TestMethod]
    public void NativeCorruptionAndWrongRequestCannotReleaseRecoveryGuard()
    {
        using var fixture = new Fixture();
        using (var recovery = fixture.Open())
        {
            fixture.Begin(recovery);
            Assert.ThrowsException<IOException>(() => recovery.RecordInstallerExit(new string('b', 64), 0));
        }
        var path = Path.Combine(fixture.RecoveryRoot, "installation.dpapi");
        var bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 0x40; File.WriteAllBytes(path, bytes);
        using var resumed = fixture.Open();
        Assert.ThrowsException<CryptographicException>(resumed.EnsureNoPending);
    }

    [TestMethod]
    public void NativeRecoveryLeaseAndParentsCannotBeReplacedWhileOwned()
    {
        using var fixture = new Fixture(); using var recovery = fixture.Open();
        fixture.Begin(recovery);
        Assert.ThrowsException<IOException>(() => fixture.Open());
        Assert.ThrowsException<IOException>(() => Directory.Move(fixture.RecoveryRoot, fixture.RecoveryRoot + "-replaced"));
    }

    [TestMethod]
    public void NativeRegistrationOnlyMigrationRetainsCustomDirectory()
    {
        using var fixture = new Fixture(custom: true); using var recovery = fixture.Open();
        fixture.Begin(recovery); fixture.Install(); recovery.RecordInstallerExit(fixture.Hash, 0); recovery.RecordVerifiedInstallation(fixture.Hash);
        Assert.IsTrue(recovery.Resolve().Completed);
        Assert.AreEqual(fixture.Source, fixture.Destination);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "personal.txt")));
    }

    [TestMethod]
    public void NativeBootIdentityIsStableWithinTheRunningWindowsSession()
    {
        var first = InstallerMigrationRecovery.CurrentBootIdentity();
        Assert.IsTrue(Guid.TryParse(first, out var id)); Assert.AreNotEqual(Guid.Empty, id);
        Assert.AreEqual(first, InstallerMigrationRecovery.CurrentBootIdentity());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudInlet-reboot-" + Guid.NewGuid().ToString("N"));
        internal string Source { get; }
        internal string Destination { get; }
        internal string RecoveryRoot => Path.Combine(_directory, "recovery");
        internal string Hash => new('a', 64);
        internal string Boot { get; set; } = Guid.NewGuid().ToString();
        internal bool RegisteredDestination { get; set; }
        internal Fixture(bool custom = false)
        {
            Source = Path.Combine(_directory, custom ? "ChosenFolder" : "CloudBay Debug");
            Destination = custom ? Source : Path.Combine(_directory, "CloudInlet Debug");
            Directory.CreateDirectory(Source);
            File.WriteAllText(Path.Combine(Source, "CloudInlet.exe"), "old");
            File.WriteAllText(Path.Combine(Source, "personal.txt"), "personal");
        }
        internal InstallerMigrationRecovery Open() => new("Debug", RecoveryRoot, () => Boot,
            receipt => { if (!RegisteredDestination || !File.Exists(Path.Combine(receipt.Destination, "CloudInlet.exe")) || File.ReadAllText(Path.Combine(receipt.Destination, "CloudInlet.exe")) != "new") throw new IOException("The installed registration or version changed."); },
            receipt => { if (RegisteredDestination || !File.Exists(Path.Combine(receipt.Source, "CloudInlet.exe")) || File.ReadAllText(Path.Combine(receipt.Source, "CloudInlet.exe")) != "old") throw new IOException("The original registration or version changed."); });
        internal void Begin(InstallerMigrationRecovery recovery) => recovery.Begin(Hash, Source, Destination, "1.2.0", "1.3.0", "CloudBay", new string('c', 64), 12345);
        internal void Install(bool retire = true)
        {
            Directory.CreateDirectory(Destination);
            File.WriteAllText(Path.Combine(Destination, "CloudInlet.exe"), "new");
            File.WriteAllText(Path.Combine(Destination, "library.dll"), "new library");
            RegisteredDestination = true;
            if (retire && Source != Destination) Retire();
        }
        internal void Retire() => File.Delete(Path.Combine(Source, "CloudInlet.exe"));
        public void Dispose()
        {
            var absolute = Path.GetFullPath(_directory);
            if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(absolute).StartsWith("CloudInlet-reboot-"))
                throw new IOException("Unsafe fixture cleanup path.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}

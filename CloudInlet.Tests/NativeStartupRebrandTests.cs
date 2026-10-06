using CloudInlet.Core;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace CloudInlet.Tests;

[TestClass]
public sealed class NativeStartupRebrandTests
{
    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void StartupApprovalMovesVerbatimWithOnlyTheOwnedLegacyCommand(int status)
    {
        WithRegistry((run, approved) =>
        {
            var approval = new byte[] { (byte)status, 0, 0, 0, 11, 12, 13, 14, 15, 16, 17, 18 };
            run.SetValue(BuildInfo.LegacyStartupRegistryName, "\"C:\\Fixture\\Install\\CloudBay.exe\" --background");
            approved.SetValue(BuildInfo.LegacyStartupRegistryName, approval, RegistryValueKind.Binary);
            SystemIntegration.MigrateLegacyStartupRegistration(run, approved, @"C:\Fixture\Install\CloudInlet.exe");
            Assert.IsNull(run.GetValue(BuildInfo.LegacyStartupRegistryName));
            Assert.AreEqual("\"C:\\Fixture\\Install\\CloudInlet.exe\" --background", run.GetValue(BuildInfo.StartupRegistryName));
            CollectionAssert.AreEqual(approval, (byte[])approved.GetValue(BuildInfo.StartupRegistryName)!);
            Assert.IsNull(approved.GetValue(BuildInfo.LegacyStartupRegistryName));
        });
    }

    [TestMethod]
    public void AbsentOrForeignLegacyStartupIsNeverEnabledOrRemoved()
    {
        WithRegistry((run, approved) =>
        {
            SystemIntegration.MigrateLegacyStartupRegistration(run, approved, @"C:\Fixture\Install\CloudInlet.exe");
            Assert.AreEqual(0, run.ValueCount);
            const string foreign = "\"C:\\Foreign\\CloudBay.exe\" --background";
            run.SetValue(BuildInfo.LegacyStartupRegistryName, foreign);
            SystemIntegration.MigrateLegacyStartupRegistration(run, approved, @"C:\Fixture\Install\CloudInlet.exe");
            Assert.IsNull(run.GetValue(BuildInfo.StartupRegistryName));
            Assert.AreEqual(foreign, run.GetValue(BuildInfo.LegacyStartupRegistryName));
        });
    }

    [TestMethod]
    public void ExistingNewStartupChoiceRemainsAuthoritative()
    {
        WithRegistry((run, approved) =>
        {
            const string current = "\"C:\\Fixture\\Install\\CloudInlet.exe\" --background";
            run.SetValue(BuildInfo.LegacyStartupRegistryName, "\"C:\\Fixture\\Install\\CloudBay.exe\" --background");
            run.SetValue(BuildInfo.StartupRegistryName, current);
            approved.SetValue(BuildInfo.LegacyStartupRegistryName, new byte[] { 2 }, RegistryValueKind.Binary);
            approved.SetValue(BuildInfo.StartupRegistryName, new byte[] { 3 }, RegistryValueKind.Binary);
            SystemIntegration.MigrateLegacyStartupRegistration(run, approved, @"C:\Fixture\Install\CloudInlet.exe");
            Assert.AreEqual(current, run.GetValue(BuildInfo.StartupRegistryName));
            CollectionAssert.AreEqual(new byte[] { 3 }, (byte[])approved.GetValue(BuildInfo.StartupRegistryName)!);
        });
    }

    private static void WithRegistry(Action<RegistryKey, RegistryKey> test)
    {
        var path = @"Software\CloudInlet\Tests\StartupRebrand\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            using var run = root.CreateSubKey("Run");
            using var approved = root.CreateSubKey("Approved");
            test(run, approved);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}

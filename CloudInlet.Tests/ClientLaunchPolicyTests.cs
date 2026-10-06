using CloudInlet.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientLaunchPolicyTests
{
    private static AppSettings ConnectedB2 => new() { KeyId = "test-key", BucketId = "test-bucket" };

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FirstLaunchShowsSetupOnManualAndWindowsStartup(bool windowsStartup)
    {
        var arguments = windowsStartup ? new[] { "CloudInlet.exe", "--background" } : new[] { "CloudInlet.exe" };
        Assert.IsTrue(ClientLaunchPolicy.ShouldShowWindow(new(), false, ClientState.NotConnected, arguments));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ConnectedB2OrOneDriveStartsOnlyInTrayOnManualAndWindowsStartup(bool oneDriveOnly, bool windowsStartup)
    {
        var arguments = windowsStartup ? new[] { "CloudInlet.exe", "--background" } : new[] { "CloudInlet.exe" };
        Assert.IsFalse(ClientLaunchPolicy.ShouldShowWindow(oneDriveOnly ? new() : ConnectedB2, oneDriveOnly, ClientState.NotConnected, arguments),
            "Connected accounts stay in the tray before their controller reconnects.");
        Assert.AreEqual("launch", ClientLaunchPolicy.SecondaryCommand(arguments),
            "A repeated startup must use the same policy instead of forcing the existing window open.");
    }

    [TestMethod]
    public void PackagedWindowsStartupUsesTheSameTrayOnlyPolicyWithoutBackgroundArgument()
    {
        Assert.IsFalse(ClientLaunchPolicy.ShouldShowWindow(ConnectedB2, false, ClientState.NotConnected, ["CloudInlet.exe"]));
    }

    [DataTestMethod]
    [DataRow("--show")]
    [DataRow("--ui-live")]
    [DataRow("--ui-smoke")]
    public void ExplicitWindowAndIsolatedValidationRequestsRemainVisible(string argument)
    {
        string[] arguments = ["CloudInlet.exe", argument];
        Assert.IsTrue(ClientLaunchPolicy.ShouldShowWindow(ConnectedB2, false, ClientState.UpToDate, arguments));
        Assert.AreEqual("show", ClientLaunchPolicy.SecondaryCommand(arguments));
    }

    [TestMethod]
    public void NotificationActivationLeavesWindowChoiceToItsAction()
    {
        Assert.IsFalse(ClientLaunchPolicy.ShouldShowWindow(new(), false, ClientState.Attention, ["CloudInlet.exe"], notificationActivation: true));
    }

    [TestMethod]
    public void StartupSettingsRecoveryStillShowsAttention()
    {
        Assert.IsTrue(ClientLaunchPolicy.ShouldShowWindow(ConnectedB2, false, ClientState.Attention, ["CloudInlet.exe", "--background"]));
    }

    [TestMethod]
    public void ConfiguredRuntimeAttentionDoesNotReopenTheWindowOnRepeatedStartup()
    {
        Assert.IsFalse(ClientLaunchPolicy.ShouldShowWindow(ConnectedB2, false, ClientState.Attention,
            ["CloudInlet.exe", "--background"], isSecondaryLaunch: true));
        Assert.IsTrue(ClientLaunchPolicy.ShouldShowWindow(new(), false, ClientState.Attention,
            ["CloudInlet.exe", "--background"], isSecondaryLaunch: true), "A client without an account still needs setup.");
    }

    [DataTestMethod]
    [DataRow("test-key", "")]
    [DataRow("", "test-bucket")]
    public void IncompleteAccountSetupStillShowsTheWindow(string keyId, string bucketId)
    {
        var settings = new AppSettings { KeyId = keyId, BucketId = bucketId };
        Assert.IsTrue(ClientLaunchPolicy.ShouldShowWindow(settings, false, ClientState.NotConnected, ["CloudInlet.exe", "--background"]));
    }

    [TestMethod]
    public void LiveTrayInspectionKeepsItsExistingActivationCommand()
    {
        Assert.AreEqual("tray", ClientLaunchPolicy.SecondaryCommand(["CloudInlet.exe", "--ui-live", "--show-tray"]));
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class GuestKnownFolderPreflightTests
{
    private string _root = null!;

    [TestInitialize]
    public void CreateFixture() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CloudInlet-GuestPreflight-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void RemoveFixture()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    [TestMethod]
    public void EmptyNativeSubfoldersAndProtectedIniAreRetained()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "Camera Roll")).FullName;
        var ini = Path.Combine(child, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]\r\n");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        GuestKnownFolderPreflight.Validate(_root);
        Assert.IsTrue(Directory.Exists(child));
        Assert.AreEqual("[.ShellClassInfo]\r\n", File.ReadAllText(ini));
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.System, File.GetAttributes(ini));
    }

    [TestMethod]
    public void UserFileInsideNativeSubfolderIsRejectedAndRetained()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "Camera Roll")).FullName;
        var file = Path.Combine(child, "personal.jpg");
        File.WriteAllText(file, "must remain");
        Assert.ThrowsException<IOException>(() => GuestKnownFolderPreflight.Validate(_root));
        Assert.AreEqual("must remain", File.ReadAllText(file));
    }

    [TestMethod]
    public void OrdinaryFileNamedDesktopIniIsNotNativeMetadata()
    {
        var file = Path.Combine(_root, "desktop.ini");
        File.WriteAllText(file, "unprotected content");
        Assert.ThrowsException<IOException>(() => GuestKnownFolderPreflight.Validate(_root));
        Assert.AreEqual("unprotected content", File.ReadAllText(file));
    }

    [TestMethod]
    public void OversizedProtectedIniIsRejectedWithoutTruncation()
    {
        var file = Path.Combine(_root, "desktop.ini");
        File.WriteAllBytes(file, new byte[65 * 1024]);
        File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.System);
        Assert.ThrowsException<IOException>(() => GuestKnownFolderPreflight.Validate(_root));
        Assert.AreEqual(65 * 1024L, new FileInfo(file).Length);
    }
}

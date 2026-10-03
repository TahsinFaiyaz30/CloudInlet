using System.Runtime.InteropServices;
using CloudBay.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

/// <summary>
/// Exercises the actual Shell COM policy interface and path APIs without changing any user mapping.
/// A third-party test folder cannot be registered per-user: RegisterFolder requires HKLM elevation.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NativeKnownFolderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void PersonalFolderPoliciesAndPathsCanBeReadWithoutChangingWindowsDefaults()
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in KnownFolderBackup.FolderIds.Keys)
        {
            try
            {
                var path = KnownFolderBackup.GetPath(name);
                var defaultPath = KnownFolderBackup.GetDefaultPath(name);
                Assert.IsTrue(Path.IsPathFullyQualified(path), name + " must resolve to an absolute current path.");
                Assert.IsTrue(Path.IsPathFullyQualified(defaultPath), name + " must resolve to an absolute default path.");
                var restriction = KnownFolderPolicy.GetRestriction(KnownFolderBackup.FolderIds[name]);
                TestContext.WriteLine($"{name}: {restriction ?? "redirectable"}.");
                paths.Add(name, path);
            }
            catch (Exception error) when (error is COMException or IOException)
            {
                // E.g. 3D Objects can be absent on current Windows releases.
                Assert.IsNotNull(KnownFolderBackup.GetRestriction(name), name + " must expose an unavailable-folder reason.");
                TestContext.WriteLine(name + ": unavailable on this Windows installation.");
            }
        }
        Assert.IsTrue(paths.ContainsKey("Desktop"));
        Assert.IsTrue(paths.ContainsKey("Documents"));
        Assert.IsTrue(paths.ContainsKey("Downloads"));
        foreach (var (name, original) in paths)
            Assert.AreEqual(original, KnownFolderBackup.GetPath(name), name + " must retain its original Windows mapping.");
    }

    [TestMethod]
    public void NativeWindowsSystemFolderPolicyRejectsRedirection()
    {
        var systemFolder = new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7");
        Assert.IsNotNull(KnownFolderPolicy.GetRestriction(systemFolder));
        Assert.ThrowsException<IOException>(() => KnownFolderPolicy.EnsureRedirectable(systemFolder));
    }

    [TestMethod]
    public void NativeUnknownFolderFailsClosedBeforeAnyRedirection()
    {
        var unknown = Guid.NewGuid();
        Assert.ThrowsException<FileNotFoundException>(() => KnownFolderPolicy.EnsureRedirectable(unknown));
    }
}

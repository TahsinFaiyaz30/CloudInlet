using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class StoreStateRoutingPolicyTests
{
    private string _path = null!;
    [TestInitialize] public void Setup() => _path = Path.Combine(Path.GetTempPath(), "CloudInlet-manifest-" + Guid.NewGuid().ToString("N") + ".xml");
    [TestCleanup] public void Cleanup() => File.Delete(_path);

    [TestMethod] public void BothProfilesAndRecoveryNeedExplicitSharedScope()
    {
        Write(StoreStateRoutingPolicy.RequiredDirectories);
        Assert.IsTrue(StoreStateRoutingPolicy.SupportsRouting(_path));
        foreach (var omitted in StoreStateRoutingPolicy.RequiredDirectories)
        {
            Write(StoreStateRoutingPolicy.RequiredDirectories.Where(path => path != omitted));
            Assert.IsFalse(StoreStateRoutingPolicy.SupportsRouting(_path), omitted);
        }
    }
    [TestMethod] public void BroadExclusionDoesNotImplicitlyAuthorizeMigration()
    {
        Write([@"$(KnownFolder:LocalAppData)"]);
        Assert.IsFalse(StoreStateRoutingPolicy.SupportsRouting(_path));
    }
    [TestMethod] public void MissingOlderWindowsPolicyIsRejected()
    {
        Write(StoreStateRoutingPolicy.RequiredDirectories, false);
        Assert.IsFalse(StoreStateRoutingPolicy.SupportsRouting(_path));
    }
    [TestMethod] public void ExternalXmlEntitiesAreRejected()
    {
        File.WriteAllText(_path, "<!DOCTYPE Package [<!ENTITY file SYSTEM 'file:///C:/Windows/win.ini'>]><Package>&file;</Package>");
        Assert.ThrowsException<System.Xml.XmlException>(() => StoreStateRoutingPolicy.SupportsRouting(_path));
    }
    private void Write(IEnumerable<string> paths, bool fallback = true) => File.WriteAllText(_path,
        "<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10' " +
        "xmlns:v='http://schemas.microsoft.com/appx/manifest/virtualization/windows10' " +
        "xmlns:d6='http://schemas.microsoft.com/appx/manifest/desktop/windows10/6'><Properties>" +
        (fallback ? "<d6:FileSystemWriteVirtualization>disabled</d6:FileSystemWriteVirtualization>" : "") +
        "<v:FileSystemWriteVirtualization><v:ExcludedDirectories>" +
        string.Join("", paths.Select(path => "<v:ExcludedDirectory>" + path + "</v:ExcludedDirectory>")) +
        "</v:ExcludedDirectories></v:FileSystemWriteVirtualization></Properties></Package>");
}

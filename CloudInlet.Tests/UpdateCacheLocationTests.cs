using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class UpdateCacheLocationTests
{
    [DataTestMethod]
    [DataRow("CloudBay", "Release")]
    [DataRow("CloudBay", "Debug")]
    [DataRow("CloudInlet", "Release")]
    [DataRow("CloudInlet", "Debug")]
    public void BothOwnedLocationsKeepTheirOwnFlavorAndLockDirectory(string brand, string flavor)
    {
        using var fixture = new Fixture();
        var cache = fixture.CreateCache(brand, flavor);
        fixture.CreateCache(brand == "CloudBay" ? "CloudInlet" : "CloudBay", flavor);
        Assert.AreEqual(cache, fixture.Validate(cache, flavor));
    }

    [DataTestMethod]
    [DataRow("request")]
    [DataRow("package")]
    [DataRow("result")]
    [DataRow("readiness")]
    public void CannotMixOldAndNewCaches(string field)
    {
        using var fixture = new Fixture();
        var old = fixture.CreateCache("CloudBay", "Release");
        var next = fixture.CreateCache("CloudInlet", "Release");
        var files = fixture.Files(old);
        files[field] = Path.Combine(next, Path.GetFileName(files[field]));
        Assert.ThrowsException<IOException>(() => fixture.Validate(files, "Release"));
    }

    [TestMethod]
    public void CannotUseOtherFlavorOrSimilarDirectoryName()
    {
        using var fixture = new Fixture();
        var debug = fixture.CreateCache("CloudInlet", "Debug");
        Assert.ThrowsException<IOException>(() => fixture.Validate(debug, "Release"));
        Assert.ThrowsException<IOException>(() => fixture.Validate(debug, "unknown"));
        Assert.ThrowsException<IOException>(() => fixture.Validate(debug + "-different", "Debug"));
    }

    [DataTestMethod]
    [DataRow("relative")]
    [DataRow("drive-relative")]
    [DataRow("traversal")]
    [DataRow("stream")]
    public void RejectsPathsThatEscapeOrUseAmbiguousForms(string form)
    {
        using var fixture = new Fixture();
        var cache = fixture.CreateCache("CloudInlet", "Release");
        var files = fixture.Files(cache);
        files["result"] = form switch
        {
            "relative" => "result.json",
            "drive-relative" => Path.GetPathRoot(cache)![..2] + "result.json",
            "traversal" => Path.Combine(cache, "..", "result.json"),
            _ => Path.Combine(cache, "result.json:stream")
        };
        Assert.ThrowsException<IOException>(() => fixture.Validate(files, "Release"));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("CloudInlet update cache v1\n")]
    [DataRow("CloudBay update cache v1\r\n")]
    public void RejectsWrongOwnershipMarker(string contents)
    {
        using var fixture = new Fixture();
        var cache = fixture.CreateCache("CloudInlet", "Release");
        File.WriteAllText(Path.Combine(cache, ".cloudbay-update-cache-v1"), contents);
        Assert.ThrowsException<IOException>(() => fixture.Validate(cache, "Release"));
    }

    [TestMethod]
    public void DoesNotCreateAnUnownedOrMissingCache()
    {
        using var fixture = new Fixture();
        var cache = Path.Combine(fixture.Root, "CloudInlet", "Client", "Updates");
        Assert.ThrowsException<IOException>(() => fixture.Validate(cache, "Release"));
        Assert.IsFalse(Directory.Exists(cache));
        Directory.CreateDirectory(cache);
        Assert.ThrowsException<FileNotFoundException>(() => fixture.Validate(cache, "Release"));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(cache).Length);
    }

    [TestMethod]
    public void NativeLinkedCacheAndMarkerAreRefusedWithoutChangingTheirTargets()
    {
        using var fixture = new Fixture();
        var cache = fixture.CreateCache("CloudBay", "Release");
        var target = Path.Combine(fixture.Root, "other-owner.txt");
        const string content = "CloudBay update cache v1\n";
        File.WriteAllText(target, content);
        var owner = Path.Combine(cache, ".cloudbay-update-cache-v1");
        File.Delete(owner);
        File.CreateSymbolicLink(owner, target);
        try { Assert.ThrowsException<IOException>(() => fixture.Validate(cache, "Release")); }
        finally { File.Delete(owner); }
        Assert.AreEqual(content, File.ReadAllText(target));
        File.WriteAllText(owner, content);
        var linked = Path.Combine(fixture.Root, "CloudInlet");
        Directory.CreateSymbolicLink(linked, Path.Combine(fixture.Root, "CloudBay"));
        try { Assert.ThrowsException<IOException>(() => fixture.Validate(Path.Combine(linked, "Client", "Updates"), "Release")); }
        finally { Directory.Delete(linked); }
        Assert.AreEqual(content, File.ReadAllText(owner));
    }

    [TestMethod]
    public void NativeLinkedResultAndDirectoryUsedAsFileAreRefused()
    {
        using var fixture = new Fixture();
        var cache = fixture.CreateCache("CloudInlet", "Release");
        var files = fixture.Files(cache);
        var target = Path.Combine(fixture.Root, "private.txt");
        File.WriteAllText(target, "Preserve this file");
        File.CreateSymbolicLink(files["result"], target);
        try { Assert.ThrowsException<IOException>(() => fixture.Validate(files, "Release")); }
        finally { File.Delete(files["result"]); }
        Assert.AreEqual("Preserve this file", File.ReadAllText(target));
        Directory.CreateDirectory(files["result"]);
        Assert.ThrowsException<IOException>(() => fixture.Validate(files, "Release"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudInlet-UpdateCache-" + Guid.NewGuid().ToString("N"));
        internal string CreateCache(string brand, string flavor)
        {
            var cache = Path.Combine(Root, brand, flavor == "Debug" ? @"Debug\Client\Updates" : @"Client\Updates");
            Directory.CreateDirectory(Path.Combine(cache, "host"));
            File.WriteAllText(Path.Combine(cache, ".cloudbay-update-cache-v1"), "CloudBay update cache v1\n");
            return cache;
        }
        internal Dictionary<string, string> Files(string cache) => new()
        {
            ["request"] = Path.Combine(cache, "host", "request.json"),
            ["package"] = Path.Combine(cache, "pending-CloudInlet-1.2.1-win-x64-release-setup.exe"),
            ["result"] = Path.Combine(cache, "install-result.json"),
            ["readiness"] = Path.Combine(cache, "host", "ready.json")
        };
        internal string Validate(string cache, string flavor) => Validate(Files(cache), flavor);
        internal string Validate(Dictionary<string, string> files, string flavor) => UpdateCacheLocation.Validate(flavor,
            files["request"], files["package"], files["result"], files["readiness"], Root);
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(path).StartsWith("CloudInlet-UpdateCache-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected cache fixture cleanup target.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}

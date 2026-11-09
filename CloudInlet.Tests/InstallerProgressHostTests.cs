using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class InstallerProgressHostTests
{
    private string _root = null!;
    private string App => Path.Combine(_root, "app");
    private string Cache => Path.Combine(_root, "cache");
    private const string Id = "aaaabbbbccccddddeeeeffff00001111";
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "CloudInlet-progress-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(App); Directory.CreateDirectory(Cache);
        File.WriteAllText(Path.Combine(App, "CloudInlet.exe"), "isolated executable fixture, never run");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private void Inventory(params string[] files) => File.WriteAllBytes(Path.Combine(App, "installer-progress-payload.json"),
        JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Files = files.Select(path => new
        { Path = path, Size = new FileInfo(Path.Combine(App, path)).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(App, path)))) }) }));

    [TestMethod]
    public void NativeProgressRuntimeCopiesOnlyVerifiedPackagedInventory()
    {
        Directory.CreateDirectory(Path.Combine(App, "Assets"));
        File.WriteAllText(Path.Combine(App, "Assets", "icon.svg"), "fixture asset");
        File.WriteAllText(Path.Combine(App, "personal.txt"), "not in the package");
        File.WriteAllText(Path.Combine(App, "distribution.json"), "installer-owned mutable metadata");
        Inventory("CloudInlet.exe", @"Assets\icon.svg");
        var executable = InstallerProgressHost.Stage(App, Cache, Id);
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(App, "CloudInlet.exe")), File.ReadAllBytes(executable));
        Assert.AreEqual("fixture asset", File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable)!, "Assets", "icon.svg")));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "personal.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "distribution.json")));
        Assert.ThrowsException<IOException>(() => InstallerProgressHost.Stage(App, Cache, Id));
        Assert.AreEqual("not in the package", File.ReadAllText(Path.Combine(App, "personal.txt")));
    }

    [TestMethod]
    public void NativeProgressPruningPreservesLiveAndUnknownFilesAndRemovesOnlyVerifiedPayload()
    {
        Inventory("CloudInlet.exe");
        var executable = InstallerProgressHost.Stage(App, Cache, Id);
        var directory = Path.GetDirectoryName(executable)!;
        using (InstallerProgressHost.AcquireRuntimeLease(directory))
        {
            InstallerProgressHost.Prune(Cache, true);
            Assert.IsTrue(File.Exists(executable));
        }
        var unknown = Path.Combine(directory, "notes.txt");
        File.WriteAllText(unknown, "retain unrelated contents");
        InstallerProgressHost.Prune(Cache, true);
        Assert.IsTrue(File.Exists(executable));
        Assert.AreEqual("retain unrelated contents", File.ReadAllText(unknown));
        File.Delete(unknown);
        InstallerProgressHost.Prune(Cache);
        Assert.IsTrue(File.Exists(executable), "Recent copies must survive the launch handoff.");
        InstallerProgressHost.Prune(Cache, true);
        Assert.IsFalse(File.Exists(executable));
        Assert.IsTrue(File.Exists(Path.Combine(directory, ".runtime.dpapi")));
        Assert.IsTrue(File.Exists(Path.Combine(App, "CloudInlet.exe")));
        InstallerProgressHost.Prune(Cache, true); // Idempotent after payload cleanup.
    }

    [TestMethod]
    public void NativeOverlappingProgressAndRecoveryWindowsShareRuntimeWithoutPermittingCleanup()
    {
        Inventory("CloudInlet.exe");
        var executable = InstallerProgressHost.Stage(App, Cache, Id);
        var directory = Path.GetDirectoryName(executable)!;
        using (var progress = InstallerProgressHost.AcquireObserverLease(directory))
        {
            using (var recovery = InstallerProgressHost.AcquireObserverLease(directory))
            {
                Assert.ThrowsException<IOException>(() => InstallerProgressHost.AcquireRuntimeLease(directory));
                InstallerProgressHost.Prune(Cache, true);
                Assert.IsTrue(File.Exists(executable));
            }
            InstallerProgressHost.Prune(Cache, true);
            Assert.IsTrue(File.Exists(executable), "The remaining window still owns its runtime.");
        }
        InstallerProgressHost.Prune(Cache, true);
        Assert.IsFalse(File.Exists(executable));
    }

    [TestMethod]
    public void NativeProgressPruningPreservesChangedPayload()
    {
        Inventory("CloudInlet.exe");
        var executable = InstallerProgressHost.Stage(App, Cache, Id);
        File.WriteAllText(executable, "changed");
        InstallerProgressHost.Prune(Cache, true);
        Assert.AreEqual("changed", File.ReadAllText(executable));
    }

    [TestMethod]
    public void NativeProgressRuntimeRejectsChangedPayloadWithoutTouchingInstalledFiles()
    {
        Inventory("CloudInlet.exe");
        File.WriteAllText(Path.Combine(App, "CloudInlet.exe"), "changed runtime");
        Assert.ThrowsException<IOException>(() => InstallerProgressHost.Stage(App, Cache, Id));
        Assert.AreEqual("changed runtime", File.ReadAllText(Path.Combine(App, "CloudInlet.exe")));
    }

    [DataTestMethod]
    [DataRow("../unrelated.txt")]
    [DataRow("C:\\outside.txt")]
    [DataRow("unins000.exe")]
    public void NativeProgressRuntimeRejectsEscapingOrInstallerOwnedNames(string path)
    {
        Inventory("CloudInlet.exe");
        var inventory = Path.Combine(App, "installer-progress-payload.json");
        var invalid = new { Path = path, Size = 0, Sha256 = new string('0', 64) };
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(inventory));
        var valid = parsed.RootElement.GetProperty("Files")[0].Clone();
        File.WriteAllBytes(inventory, JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Files = new object[] { valid, invalid } }));
        Assert.ThrowsException<IOException>(() => InstallerProgressHost.Stage(App, Cache, Id));
        Assert.IsFalse(Directory.Exists(Path.Combine(Cache, "host", "progress-" + Id)));
    }
}

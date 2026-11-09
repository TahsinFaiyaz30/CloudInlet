using System.Runtime.InteropServices;
using CloudInlet.Application;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientMigrationInventoryTests
{
    private string _directory = null!;
    [TestInitialize] public void Setup() => _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "CloudInlet-inventory-" + Guid.NewGuid().ToString("N"))).FullName;
    [TestCleanup] public void Cleanup() => Directory.Delete(_directory, true);

    [DataTestMethod]
    [DataRow("{invalid settings")]
    [DataRow("{\"CustomBackups\":null}")]
    public void InvalidSettingsRemainAvailableForNormalRecoveryWithoutDiscovery(string contents)
    {
        var file = Path.Combine(_directory, "settings.json"); File.WriteAllText(file, contents);
        var discovered = false;
        var result = ClientMigrationInventory.TryCreate(_directory, _ => { discovered = true; return MigrationPlan.Create([]); });
        Assert.IsNotNull(result.Error); Assert.IsNull(result.Plan); Assert.IsFalse(discovered);
        Assert.AreEqual(contents, File.ReadAllText(file));
    }

    [TestMethod]
    public void BusySettingsPreventMigrationInventoryWithoutReplacingTheFile()
    {
        var file = Path.Combine(_directory, "settings.json"); File.WriteAllText(file, "{}");
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = ClientMigrationInventory.TryCreate(_directory, _ => throw new AssertFailedException("Discovery ran on locked settings."));
            Assert.IsNotNull(result.Error); Assert.IsNull(result.Plan);
        }
        Assert.AreEqual("{}", File.ReadAllText(file));
    }

    [TestMethod]
    public void WindowsInventoryFailureIsVisibleAndDoesNotModifySavedSettings()
    {
        var storage = new ClientStorage(_directory); storage.SaveSettings(new());
        var file = Path.Combine(_directory, "settings.json"); var before = File.ReadAllBytes(file);
        var result = ClientMigrationInventory.TryCreate(_directory, _ => throw new COMException("Fixture provider unavailable"));
        StringAssert.Contains(result.Error!, "Fixture provider unavailable"); Assert.IsNull(result.Plan);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
    }
}

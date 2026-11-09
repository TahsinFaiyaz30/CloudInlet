using CloudInlet.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class MigrationMetadataExchangeTests
{
    private string _root = null!, _state = null!, _records = null!, _path = null!;
    private static readonly byte[] Before = "original settings"u8.ToArray(), After = "migrated settings"u8.ToArray();
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "CloudInlet-metadata-exchange-" + Guid.NewGuid().ToString("N"));
        _state = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        _records = Directory.CreateDirectory(Path.Combine(_root, "records")).FullName;
        _path = Path.Combine(_state, "settings.json"); File.WriteAllBytes(_path, Before);
    }
    [TestCleanup] public void Cleanup()
    {
        if (!Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "CloudInlet-metadata-exchange-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixture escaped its owned temporary root.");
        Directory.Delete(_root, true);
    }
    [DataTestMethod]
    [DataRow("prepared")]
    [DataRow("original-retained")]
    [DataRow("replacement-published")]
    public void NativeInterruptedMetadataExchangeRecoversEveryCommitBoundary(string boundary)
    {
        Assert.ThrowsException<IOException>(() => MigrationMetadataExchange.Replace(_path, Before, After, _records,
            stage => { if (stage == boundary) throw new IOException("Fixture interruption"); }));
        MigrationMetadataExchange.Recover(_state, _records);
        CollectionAssert.AreEqual(After, File.ReadAllBytes(_path));
        CollectionAssert.AreEqual(Before, File.ReadAllBytes(Directory.GetFiles(_state, "*.bak").Single()));
        Assert.AreEqual(0, Directory.GetFiles(_records, "*.pending.dpapi").Length);
        MigrationMetadataExchange.Recover(_state, _records);
        CollectionAssert.AreEqual(After, File.ReadAllBytes(_path));
    }
    [TestMethod]
    public void NativeMetadataExchangeBlocksReplacementBetweenValidationAndRename()
    {
        MigrationMetadataExchange.Replace(_path, Before, After, _records, stage =>
        {
            if (stage != "prepared") return;
            Assert.ThrowsException<IOException>(() => File.Move(_path, _path + ".external"));
            Assert.ThrowsException<IOException>(() => File.WriteAllText(_path, "external overwrite"));
            Assert.ThrowsException<IOException>(() => Directory.Move(_state, _state + "-external"));
        });
        CollectionAssert.AreEqual(After, File.ReadAllBytes(_path));
    }
    [TestMethod]
    public void NativeMetadataExchangeRetainsConcurrentDestinationAndOriginal()
    {
        Assert.ThrowsException<IOException>(() => MigrationMetadataExchange.Replace(_path, Before, After, _records,
            stage => { if (stage == "original-retained") File.WriteAllText(_path, "external new file"); }));
        Assert.ThrowsException<IOException>(() => MigrationMetadataExchange.Recover(_state, _records));
        Assert.AreEqual("external new file", File.ReadAllText(_path));
        CollectionAssert.AreEqual(Before, File.ReadAllBytes(Directory.GetFiles(_state, "*.bak").Single()));
        File.Delete(_path); // The fixture owner explicitly resolves its own conflict.
        MigrationMetadataExchange.Recover(_state, _records);
        CollectionAssert.AreEqual(After, File.ReadAllBytes(_path));
    }
    [TestMethod]
    public void NativeMetadataExchangeSupportsLongRecoveryAndMetadataPaths()
    {
        _state = Directory.CreateDirectory(Path.Combine(_state, new string('a', 90), new string('b', 90))).FullName;
        _records = Directory.CreateDirectory(Path.Combine(_records, new string('c', 90), new string('d', 90))).FullName;
        _path = Path.Combine(_state, "settings.json"); File.WriteAllBytes(_path, Before);
        Assert.IsTrue(_path.Length > 260);
        Assert.ThrowsException<IOException>(() => MigrationMetadataExchange.Replace(_path, Before, After, _records,
            stage => { if (stage == "original-retained") throw new IOException("Fixture interruption"); }));
        MigrationMetadataExchange.Recover(_state, _records);
        CollectionAssert.AreEqual(After, File.ReadAllBytes(_path));
    }
    [TestMethod]
    public void NativeMetadataExchangeRejectsChangedOriginalAndTamperedRecovery()
    {
        File.WriteAllText(_path, "newer user settings");
        Assert.ThrowsException<IOException>(() => MigrationMetadataExchange.Replace(_path, Before, After, _records));
        Assert.AreEqual("newer user settings", File.ReadAllText(_path));
        File.WriteAllBytes(_path, Before);
        Assert.ThrowsException<IOException>(() => MigrationMetadataExchange.Replace(_path, Before, After, _records,
            stage => { if (stage == "prepared") throw new IOException("Fixture interruption"); }));
        var record = Directory.GetFiles(_records, "*.pending.dpapi").Single();
        var bytes = File.ReadAllBytes(record); bytes[^1] ^= 1; File.WriteAllBytes(record, bytes);
        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => MigrationMetadataExchange.Recover(_state, _records));
        CollectionAssert.AreEqual(Before, File.ReadAllBytes(_path));
    }
}

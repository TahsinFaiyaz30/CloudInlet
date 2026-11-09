using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class SyncManifestMigrationTests
{
    [TestMethod]
    public void ReviewedNestedBackupNamespaceKeepsSameLocalBaseline()
    {
        const string oldKey = ".cloudbay-backups/Documents/file.txt";
        var before = new SyncEntry("file.txt", Remote("file", oldKey), 42, Modified.AddDays(1)); _manifest.Put(before);
        var mapping = Map("file", oldKey);
        mapping = mapping with { Migrated = mapping.Migrated with { Key = "CloudInlet/.cloudinlet-backups/Documents/file.txt" } };
        var rules = new[] { new CloudPrefixRule("CloudBay/.cloudbay-backups/Documents/", "CloudInlet/.cloudinlet-backups/Documents/") };
        _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", [mapping], rules);
        var migrated = _manifest.ReadAll()["file.txt"];
        Assert.AreEqual(before with { Remote = before.Remote with { FileId = mapping.Migrated.FileId, Key = mapping.Migrated.Key }, NativeMarkPending = true }, migrated);
        _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", [mapping], rules);
        Assert.AreEqual(migrated, _manifest.ReadAll()["file.txt"]);
    }
    private string _directory = null!;
    private SyncManifest _manifest = null!;
    private static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
    private static readonly string Hash = new('a', 40);
    private static CloudPrefixVersionMapping Map(string id, string relative) => new(
        new(id, "CloudBay/" + relative, "upload", 3, Hash, 1800000000000, "text/plain", new Dictionary<string, string>()),
        new("new-" + id, "CloudInlet/" + relative, "upload", 3, Hash, 1900000000000, "text/plain", new Dictionary<string, string>()));
    private static CloudObject Remote(string id, string relative) => new(id, "CloudBay/" + relative, 3, Hash, Modified);
    [TestInitialize] public void Setup()
    {
        _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CloudInlet-manifest-migration-" + Guid.NewGuid().ToString("N"))).FullName;
        _manifest = new SyncManifest(Path.Combine(_directory, "manifest.sqlite"));
    }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }

    [TestMethod]
    public void VersionRemapPreservesLocalChangesTimestampsAndRecoveryAcrossReopen()
    {
        var entry = new SyncEntry("file.txt", Remote("file", "file.txt"), 42, Modified.AddDays(1));
        _manifest.Put(entry); _manifest.PutDirectory(new("folder", Remote("folder", "folder/")));
        _manifest.PrepareForNewRegistration();
        var mappings = new[] { Map("file", "file.txt"), Map("folder", "folder/") };
        _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", mappings);
        _manifest = new SyncManifest(Path.Combine(_directory, "manifest.sqlite"));
        var migrated = _manifest.ReadAll()["file.txt"];
        Assert.AreEqual(entry with { Remote = entry.Remote with { FileId = "new-file", Key = "CloudInlet/file.txt" }, NativeMarkPending = true }, migrated);
        Assert.AreEqual(Modified, _manifest.ReadDirectories()["folder"].Remote.ModifiedUtc);
        Assert.AreEqual("new-folder", _manifest.ReadDirectories()["folder"].Remote.FileId);
        CollectionAssert.AreEquivalent(new[] { "F:file.txt", "D:folder" }, _manifest.ReadRegistrationRecovery().ToArray());
        _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", mappings);
        Assert.AreEqual(migrated, _manifest.ReadAll()["file.txt"]);
    }

    [TestMethod]
    public void MissingDirectoryMappingRollsBackAlreadyProcessedFilesAndKeepsNativeState()
    {
        var entry = new SyncEntry("file.txt", Remote("file", "file.txt"), 3, Modified);
        _manifest.Put(entry); _manifest.PutDirectory(new("folder", Remote("folder", "folder/")));
        Assert.ThrowsException<InvalidDataException>(() => _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", [Map("file", "file.txt")]));
        Assert.AreEqual(entry, _manifest.ReadAll()["file.txt"]);
        Assert.AreEqual("folder", _manifest.ReadDirectories()["folder"].Remote.FileId);
        Assert.AreEqual(0, _manifest.ReadRegistrationRecovery().Count);
    }

    [TestMethod]
    public void ChangedBaselineCannotBeMatchedByFileIdAlone()
    {
        var entry = new SyncEntry("file.txt", Remote("file", "file.txt") with { Size = 99 }, 99, Modified);
        _manifest.Put(entry);
        Assert.ThrowsException<InvalidDataException>(() => _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", [Map("file", "file.txt")]));
        Assert.AreEqual(entry, _manifest.ReadAll()["file.txt"]);
    }

    [TestMethod]
    public void MappingRejectsAliasesAndUnreviewedDestinationBeforeChangingManifest()
    {
        var entry = new SyncEntry("file.txt", Remote("file", "file.txt"), 3, Modified); _manifest.Put(entry);
        var mapping = Map("file", "file.txt");
        Assert.ThrowsException<InvalidDataException>(() => _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/", [mapping, mapping]));
        Assert.ThrowsException<InvalidDataException>(() => _manifest.MigrateCloudPrefixReferences("CloudBay/", "CloudInlet/",
            [mapping with { Migrated = mapping.Migrated with { Key = "Unreviewed/file.txt" } }]));
        Assert.AreEqual(entry, _manifest.ReadAll()["file.txt"]);
    }
}

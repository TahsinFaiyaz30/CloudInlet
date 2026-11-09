using CloudInlet.Application;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientMigrationReviewTests
{
    [TestMethod] public void NativeCloudSelectionRequiresSeparateDurableMaintenanceConfirmation()
    {
        var store = new ClientMigrationReview(_root);
        var plan = MigrationPlan.Create([new("cloud", MigrationArea.CloudPrefix, "Cloud folder", "CloudBay/", "CloudInlet/", "Preserve history")]);
        Assert.ThrowsException<InvalidDataException>(() => store.Create(plan, ["cloud"]));
        Assert.IsNull(store.Load());
        var saved = store.Create(plan, ["cloud"], cloudMaintenanceAcknowledged: true);
        Assert.IsTrue(store.Load()!.CloudMaintenanceAcknowledged);
        Assert.AreEqual(saved.Plan.Fingerprint, store.Load()!.Plan.Fingerprint);
    }
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "CloudInlet-review-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static MigrationPlan Plan() => MigrationPlan.Create([
        new("application-state", MigrationArea.ApplicationState, "Application data", @"C:\Old\Client", @"C:\New\Client", "Preserve settings"),
        new("sync-folder", MigrationArea.SyncFolder, "Sync folder", @"C:\Old\CloudBay", @"C:\Old\CloudInlet", "Preserve files")]);

    [TestMethod] public void NativeReviewRoundTripPreservesExactSelectionAndProtectedPaths()
    {
        var store = new ClientMigrationReview(_root);
        var saved = store.Create(Plan(), ["sync-folder"]);
        var opened = new ClientMigrationReview(_root).Load()!;
        Assert.AreEqual(saved.Plan.Fingerprint, opened.Plan.Fingerprint);
        CollectionAssert.AreEqual(new[] { "sync-folder" }, opened.SelectedIds);
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.ConsentPath)).Contains(@"C:\Old"));
        Assert.IsTrue(store.SessionPath(opened).StartsWith(_root + Path.DirectorySeparatorChar));
    }

    [TestMethod] public void NativeSecondReviewCannotReplacePendingConsent()
    {
        var store = new ClientMigrationReview(_root);
        var saved = store.Create(Plan(), ["application-state"]);
        Assert.ThrowsException<IOException>(() => store.Create(Plan(), ["sync-folder"]));
        Assert.AreEqual(saved.Id, store.Load()!.Id);
    }

    [TestMethod] public void NativeCorruptConsentIsRefusedWithoutChangingIt()
    {
        var store = new ClientMigrationReview(_root); store.Create(Plan(), ["sync-folder"]);
        var bytes = File.ReadAllBytes(store.ConsentPath); bytes[bytes.Length / 2] ^= 0x77;
        File.WriteAllBytes(store.ConsentPath, bytes);
        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => store.Load());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(store.ConsentPath));
    }

    [TestMethod] public void NativeFinishingPreservesTheReviewAndAllowsANewSelection()
    {
        var store = new ClientMigrationReview(_root); var old = store.Create(Plan(), ["application-state"]);
        store.Finish(old); Assert.IsNull(store.Load());
        Assert.IsTrue(File.Exists(Path.Combine(_root, "finished-" + old.Id + ".dpapi")));
        var next = store.Create(Plan(), ["sync-folder"]);
        Assert.AreNotEqual(old.Id, next.Id);
    }

    [TestMethod] public void NativeDifferentReviewCannotFinishPendingMigration()
    {
        var store = new ClientMigrationReview(_root); var old = store.Create(Plan(), ["application-state"]);
        Assert.ThrowsException<InvalidDataException>(() => store.Finish(old with { Id = Guid.NewGuid().ToString("N") }));
        Assert.AreEqual(old.Id, store.Load()!.Id);
    }

    [TestMethod] public void NativeDeclineOnlySuppressesTheSameReviewAndNeverCreatesConsent()
    {
        var store = new ClientMigrationReview(_root); var plan = Plan(); store.MarkOffered(plan.Fingerprint);
        Assert.IsTrue(store.WasOffered(plan.Fingerprint)); Assert.IsNull(store.Load());
        var changed = MigrationPlan.Create(plan.Items.Select(item => item with { Description = "Changed scope" }));
        Assert.IsFalse(store.WasOffered(changed.Fingerprint));
    }

    [TestMethod] public void NativeInvalidSelectionDoesNotCreateConsent()
    {
        var store = new ClientMigrationReview(_root);
        Assert.ThrowsException<InvalidDataException>(() => store.Create(Plan(), []));
        Assert.IsNull(store.Load());
    }
}

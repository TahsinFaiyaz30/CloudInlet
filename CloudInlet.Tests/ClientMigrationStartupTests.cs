using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientMigrationStartupTests
{
    private string _home = null!;
    [TestInitialize] public void Setup() => _home = Path.Combine(Path.GetTempPath(), "CloudInlet-startup-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_home)) Directory.Delete(_home, true); }
    private static MigrationPlan Plan(ClientStateMigration.StateUseLease lease) => MigrationPlan.Create([
        new("application-state", MigrationArea.ApplicationState, "Application data", lease.LegacyDirectory, lease.CanonicalDirectory, "Preserve data")]);

    private static DeferredMigrationOperation Operation(ClientStateMigration.StateUseLease lease, MigrationPlan plan, string journal,
        bool failCreation = false) => new("application-state", plan.Fingerprint,
        () =>
        {
            if (failCreation) throw new IOException("Fixture: access temporarily denied before intent creation.");
            var resolution = ClientStateMigration.Resolve(lease);
            return resolution.SessionJournalPath == journal ? ClientStateMigration.Resume(lease) :
                ClientStateMigration.Create(lease, plan, "application-state", journal);
        },
        () => lease.ReadRoute() is { Location: "Pending" or "Legacy", Plan: { } saved } && saved.Fingerprint == plan.Fingerprint,
        ct => ClientStateMigration.AcknowledgeUnstartedAsync(lease, plan, journal, ct),
        () => ClientStateMigration.CanAcknowledgeUnstarted(lease, plan, journal));

    [TestMethod] public async Task NativeDurableConsentAndSessionPrecedeStateMutation()
    {
        using var lease = ClientStateMigration.AcquireStateUseLease(_home, "Release");
        Directory.CreateDirectory(lease.LegacyDirectory); File.WriteAllText(Path.Combine(lease.LegacyDirectory, "user.txt"), "preserve");
        var review = new ClientMigrationReview(lease.RecoveryDirectory); var plan = Plan(lease);
        var consent = review.Create(plan, ["application-state"]); var journal = review.SessionPath(consent);
        using (var operation = Operation(lease, plan, journal))
        using (var session = MigrationSession.Create(plan, consent.SelectedIds, journal, [operation]))
        {
            Assert.AreEqual(lease.LegacyDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
            Assert.IsNotNull(review.Load());
            await session.RunAsync();
            Assert.IsTrue(session.IsComplete, session.Items[0].Error);
            Assert.AreEqual(lease.CanonicalDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
            review.Finish(consent);
        }
        Assert.IsNull(review.Load());
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(lease.CanonicalDirectory, "user.txt")));
    }

    [TestMethod] public async Task NativeCreationFailureCanBeDismissedByVerifiedNoOpRevert()
    {
        using var lease = ClientStateMigration.AcquireStateUseLease(_home, "Release");
        Directory.CreateDirectory(lease.LegacyDirectory); File.WriteAllText(Path.Combine(lease.LegacyDirectory, "user.txt"), "preserve");
        var plan = Plan(lease); var journal = Path.Combine(lease.RecoveryDirectory, "session.json");
        using var operation = Operation(lease, plan, journal, failCreation: true);
        using var session = MigrationSession.Create(plan, ["application-state"], journal, [operation], new(1));
        await session.RunAsync();
        Assert.AreEqual(MigrationItemState.Failed, session.Items[0].State);
        Assert.IsTrue(session.CanRevert("application-state"));
        await session.RevertAsync("application-state");
        Assert.AreEqual(MigrationItemState.Reverted, session.Items[0].State);
        Assert.AreEqual(lease.LegacyDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
        Assert.IsFalse(Directory.Exists(lease.CanonicalDirectory));
    }

    [TestMethod] public async Task NativeDeferredInterruptedMoveOffersRevertBeforeRetryOrOpeningController()
    {
        string journal; MigrationPlan plan;
        using (var lease = ClientStateMigration.AcquireStateUseLease(_home, "Release"))
        {
            Directory.CreateDirectory(lease.LegacyDirectory); File.WriteAllText(Path.Combine(lease.LegacyDirectory, "user.txt"), "preserve");
            plan = Plan(lease); journal = Path.Combine(lease.RecoveryDirectory, "session.json");
            using var operation = ClientStateMigration.Create(lease, plan, "application-state", journal);
            using var session = MigrationSession.Create(plan, ["application-state"], journal, [new FailAfterMove(operation)], new(1));
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Failed, session.Items[0].State);
            Assert.IsNull(ClientStateMigration.Resolve(lease).DataDirectory);
        }
        using (var lease = ClientStateMigration.AcquireStateUseLease(_home, "Release"))
        using (var operation = Operation(lease, plan, journal))
        using (var session = MigrationSession.Open(journal, [operation]))
        {
            Assert.IsTrue(session.CanRevert("application-state"));
            Assert.IsNull(ClientStateMigration.Resolve(lease).DataDirectory);
            await session.RevertAsync("application-state");
            Assert.AreEqual(MigrationItemState.Reverted, session.Items[0].State);
            Assert.AreEqual(lease.LegacyDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
        }
    }

    [TestMethod] public async Task NativeFolderRecoveryUsesMovedStateAndNeverRevertsCompletedApplicationData()
    {
        var source = Path.Combine(_home, "personal", BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay");
        var target = Path.Combine(_home, "personal", BuildInfo.ProductName);
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "personal.txt"), "preserve");
        var runtime = new FailingRegistration(source); MigrationPlan plan; string journal, receipts;
        using (var lease = ClientStateMigration.AcquireStateUseLease(_home, "Release"))
        {
            new ClientStorage(lease.LegacyDirectory).SaveSettings(new() { RootPath = source, Prefix = "fixture/" });
            plan = MigrationPlan.Create([
                new("application-state", MigrationArea.ApplicationState, "Application data", lease.LegacyDirectory, lease.CanonicalDirectory, "Preserve data"),
                new("sync-folder", MigrationArea.SyncFolder, "Sync folder", source, target, "Preserve files")]);
            var review = new ClientMigrationReview(lease.RecoveryDirectory);
            var consent = review.Create(plan, ["application-state", "sync-folder"]);
            journal = review.SessionPath(consent); receipts = review.ReceiptDirectory(consent);
            using var state = Operation(lease, plan, journal);
            using var folder = FolderOperation();
            using var session = MigrationSession.Create(plan, consent.SelectedIds, journal, [state, folder], new(1));
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(item => item.Id == "application-state").State);
            Assert.AreEqual(MigrationItemState.Failed, session.Items.Single(item => item.Id == "sync-folder").State);
            Assert.AreEqual(target, new ClientStorage(lease.CanonicalDirectory).LoadSettings().RootPath);
            Assert.IsFalse(session.CanRevert("application-state"));

            DeferredMigrationOperation FolderOperation() => new("sync-folder", plan.Fingerprint,
                () => ClientFolderMigration.Create(plan, "sync-folder", ClientStateMigration.Resolve(lease).DataDirectory!, receipts, runtime),
                () => ClientFolderMigration.HasRecovery(plan, "sync-folder", receipts),
                ct => ClientFolderMigration.AcknowledgeUnstartedAsync(plan, "sync-folder", receipts, ct),
                () => ClientFolderMigration.CanAcknowledgeUnstarted(plan, "sync-folder", receipts));
        }
        using (var lease = ClientStateMigration.AcquireStateUseLease(_home, "Release"))
        using (var state = Operation(lease, plan, journal))
        using (var folder = new DeferredMigrationOperation("sync-folder", plan.Fingerprint,
            () => ClientFolderMigration.Resume(plan, "sync-folder", ClientStateMigration.Resolve(lease).DataDirectory!, receipts, runtime),
            () => ClientFolderMigration.HasRecovery(plan, "sync-folder", receipts),
            ct => ClientFolderMigration.AcknowledgeUnstartedAsync(plan, "sync-folder", receipts, ct),
            () => ClientFolderMigration.CanAcknowledgeUnstarted(plan, "sync-folder", receipts)))
        using (var session = MigrationSession.Open(journal, [state, folder]))
        {
            Assert.IsTrue(session.CanRevert("sync-folder"));
            await session.RevertAsync("sync-folder");
            Assert.AreEqual(MigrationItemState.Reverted, session.Items.Single(item => item.Id == "sync-folder").State);
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(item => item.Id == "application-state").State);
            Assert.IsFalse(Directory.Exists(lease.LegacyDirectory));
            Assert.AreEqual(lease.CanonicalDirectory, ClientStateMigration.Resolve(lease).DataDirectory);
            Assert.AreEqual(source, new ClientStorage(lease.CanonicalDirectory).LoadSettings().RootPath);
            Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(source, "personal.txt")));
        }
    }

    private sealed class FailingRegistration(string source) : IClientFolderMigrationRuntime
    {
        private string? _registered = source;
        private bool _failed;
        public bool IsRegistered(string root, string account, bool canonical) => root == _registered;
        public Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials,
            IProgress<string> progress, CancellationToken token) { _registered = null; return Task.CompletedTask; }
        public Task RegisterAsync(string root, string account, bool canonical, string manifest, CancellationToken token)
        {
            if (!_failed) { _failed = true; throw new IOException("Fixture registration interruption"); }
            _registered = root; return Task.CompletedTask;
        }
        public string GetKnownFolder(string name) => throw new NotSupportedException();
        public void ExchangeKnownFolder(string name, string expected, string replacement) => throw new NotSupportedException();
    }

    private sealed class FailAfterMove(IMigrationOperation inner) : IMigrationOperation
    {
        public string ItemId => inner.ItemId; public string ReviewedPlanFingerprint => inner.ReviewedPlanFingerprint;
        public bool CanRevert => inner.CanRevert;
        public Task CheckAsync(CancellationToken ct) => inner.CheckAsync(ct);
        public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
        { await inner.ApplyAsync(progress, ct); throw new IOException("Fixture interruption after move"); }
        public Task VerifyAsync(CancellationToken ct) => inner.VerifyAsync(ct);
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken ct) => inner.RevertAsync(progress, ct);
        public Task VerifyRevertedAsync(CancellationToken ct) => inner.VerifyRevertedAsync(ct);
    }
}

using System.Text.Json.Nodes;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class MigrationKeepOriginalSessionTests
{
    [TestMethod]
    public async Task KeepingFailedCloudItemPreservesCompletedItemsBlocksDependentsAndSurvivesReopen()
    {
        using var f = new Fixture();
        using (var session = f.Create())
        {
            await session.RunAsync();
            Assert.AreEqual(MigrationItemState.Completed, session.Items.Single(value => value.Id == "local").State);
            Assert.IsFalse(session.CanKeepOriginal("local"));
            Assert.IsTrue(session.CanKeepOriginal("cloud"));
            await session.KeepOriginalAsync("cloud");
            Assert.AreEqual(MigrationItemState.OriginalKept, session.Items.Single(value => value.Id == "cloud").State);
            Assert.AreEqual(MigrationItemState.Blocked, session.Items.Single(value => value.Id == "dependent").State);
            Assert.IsFalse(session.IsComplete); Assert.IsFalse(session.CanRetry("cloud")); Assert.IsFalse(session.CanRevert("cloud"));
            Assert.AreEqual(0, f.Local.RevertCalls); Assert.AreEqual(0, f.Dependent.ApplyCalls);
        }
        using var reopened = f.Open();
        await reopened.VerifyKeptOriginalsAsync(); await reopened.RunAsync();
        Assert.AreEqual(1, f.Local.ApplyCalls); Assert.AreEqual(1, f.Cloud.ApplyCalls);
        Assert.AreEqual(0, f.Cloud.RevertCalls); Assert.AreEqual(0, f.Dependent.ApplyCalls);
        f.Cloud.OriginalProof = false;
        await Assert.ThrowsExceptionAsync<IOException>(() => reopened.VerifyKeptOriginalsAsync());
    }

    [TestMethod]
    public async Task InterruptedKeepRequiresExplicitRetryAndNeverClaimsMigrationCompleted()
    {
        using var f = new Fixture();
        using (var session = f.Create())
        {
            await session.RunAsync(); f.Cloud.FailAfterKeep = true;
            await session.KeepOriginalAsync("cloud");
            Assert.AreEqual(MigrationItemState.KeepOriginalFailed, session.Items.Single(value => value.Id == "cloud").State);
            Assert.IsFalse(session.IsComplete);
        }
        var json = JsonNode.Parse(File.ReadAllText(f.Path))!;
        json["Items"]!.AsArray().Single(value => value!["Id"]!.GetValue<string>() == "cloud")!["State"] = "KeepingOriginal";
        File.WriteAllText(f.Path, json.ToJsonString());
        using var reopened = f.Open();
        Assert.AreEqual(MigrationItemState.KeepOriginalFailed, reopened.Items.Single(value => value.Id == "cloud").State);
        Assert.IsTrue(reopened.CanKeepOriginal("cloud"));
        f.Cloud.FailAfterKeep = false; await reopened.KeepOriginalAsync("cloud");
        Assert.AreEqual(MigrationItemState.OriginalKept, reopened.Items.Single(value => value.Id == "cloud").State);
        Assert.AreEqual(1, f.Cloud.ApplyCalls); Assert.AreEqual(1, f.Local.ApplyCalls);
    }

    [TestMethod]
    public async Task EditedSessionTerminalClaimCannotReplaceTheAdaptersAuthenticatedReceipt()
    {
        using var f = new Fixture();
        using (var session = f.Create()) await session.RunAsync();
        var json = JsonNode.Parse(File.ReadAllText(f.Path))!;
        json["Items"]!.AsArray().Single(value => value!["Id"]!.GetValue<string>() == "cloud")!["State"] = "OriginalKept";
        File.WriteAllText(f.Path, json.ToJsonString());
        using var reopened = f.Open();
        await Assert.ThrowsExceptionAsync<IOException>(() => reopened.VerifyKeptOriginalsAsync());
        await Assert.ThrowsExceptionAsync<IOException>(() => reopened.RunAsync());
        Assert.AreEqual(0, f.Dependent.ApplyCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet-keep-original-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "session.json");
        internal Operation Local = new("local"), Cloud = new("cloud") { FailApply = true }, Dependent = new("dependent");
        private readonly MigrationPlan _plan = MigrationPlan.Create([
            new("local", MigrationArea.ApplicationState, "Local", "C:/fixture/old", "C:/fixture/new", "Fixture"),
            new("cloud", MigrationArea.CloudPrefix, "Cloud", "CloudBay/", "CloudInlet/", "Retain originals"),
            new("dependent", MigrationArea.CloudPrefix, "Dependent", "CloudBay/dependent/", "CloudInlet/dependent/", "Fixture", Dependencies: ["cloud"])]);
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            foreach (var operation in new[] { Local, Cloud, Dependent }) operation.ReviewedPlanFingerprint = _plan.Fingerprint;
        }
        internal MigrationSession Create() => MigrationSession.Create(_plan, ["local", "cloud", "dependent"], Path, [Local, Cloud, Dependent]);
        internal MigrationSession Open() => MigrationSession.Open(Path, [Local, Cloud, Dependent]);
        public void Dispose()
        {
            var expected = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudInlet-keep-original-");
            if (!System.IO.Path.GetFullPath(_directory).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid fixture path.");
            Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class Operation(string id) : IMigrationOperation, IKeepOriginalMigrationOperation
    {
        public string ItemId => id;
        public string ReviewedPlanFingerprint { get; set; } = "";
        public bool CanRevert => true;
        public bool CanKeepOriginal => true;
        internal bool FailApply, FailAfterKeep, OriginalProof;
        internal int ApplyCalls, RevertCalls;
        public Task CheckAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
        { ApplyCalls++; return FailApply ? Task.FromException(new IOException("Unconfirmed cloud outcome")) : Task.CompletedTask; }
        public Task VerifyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken ct) { RevertCalls++; return Task.CompletedTask; }
        public Task VerifyRevertedAsync(CancellationToken ct) => Task.CompletedTask;
        public Task KeepOriginalAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
        { OriginalProof = true; return FailAfterKeep ? Task.FromException(new IOException("Interrupted after protected receipt")) : Task.CompletedTask; }
        public Task VerifyOriginalKeptAsync(CancellationToken ct) => OriginalProof ? Task.CompletedTask : Task.FromException(new IOException("Missing authenticated original-location receipt"));
    }
}

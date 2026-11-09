using System.Text.Json;
using System.Text.Json.Nodes;
using CloudInlet.Core.Migration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class MigrationSessionTests
{
    [TestMethod]
    public async Task SelectedLocalChangesFinishBeforeCloudWithoutSelectingDeclinedItems()
    {
        using var fixture = new Fixture(); var trace = new List<string>();
        var cloud = fixture.Operation("a-cloud", trace); var local = fixture.Operation("z-local", trace); var skipped = fixture.Operation("b-skipped", trace);
        var plan = MigrationPlan.Create([Item(cloud.ItemId) with { Area = MigrationArea.CloudPrefix }, Item(local.ItemId), Item(skipped.ItemId)]);
        foreach (var operation in new[] { cloud, local, skipped }) operation.ReviewedPlanFingerprint = plan.Fingerprint;
        using var session = MigrationSession.Create(plan, [cloud.ItemId, local.ItemId], fixture.Journal, [cloud, local, skipped]);
        await session.RunAsync();
        Assert.IsTrue(session.IsComplete);
        Assert.IsTrue(trace.IndexOf("z-local:verify") < trace.IndexOf("a-cloud:check"));
        Assert.AreEqual(0, skipped.ApplyCalls); Assert.IsTrue(File.Exists(skipped.Source));
    }
    private static MigrationItem Item(string id, params string[] dependencies) => new(id, MigrationArea.ApplicationState,
        "Move " + id, "C:/old/" + id, "C:/new/" + id, "Migrate only the reviewed fixture.", Dependencies: dependencies);
    private static MigrationItemState State(MigrationSession session, string id) => session.Items.Single(item => item.Id == id).State;

    [TestMethod]
    public async Task SelectedUnitsRunInDependencyOrderAndUnselectedFilesStayUntouched()
    {
        using var fixture = new Fixture();
        var trace = new List<string>();
        var root = fixture.Operation("a-root", trace);
        var state = fixture.Operation("z-state", trace);
        var skipped = fixture.Operation("unselected", trace);
        var plan = MigrationPlan.Create([Item(root.ItemId, state.ItemId), Item(state.ItemId), Item(skipped.ItemId)]);
        foreach (var operation in new[] { root, state, skipped }) operation.ReviewedPlanFingerprint = plan.Fingerprint;
        using var session = MigrationSession.Create(plan, [root.ItemId, state.ItemId], fixture.Journal, [root, state, skipped]);
        Assert.IsFalse(session.IsComplete);
        Assert.IsTrue(File.Exists(state.Source));
        Assert.IsFalse(File.Exists(state.Destination), "Review creation must not move files.");
        await session.RunAsync();
        Assert.IsTrue(session.IsComplete);
        CollectionAssert.AreEqual(new[] { "z-state:check", "z-state:check", "z-state:apply", "z-state:verify", "a-root:check", "a-root:check", "a-root:apply", "a-root:verify" }, trace);
        Assert.AreEqual(state.Contents, File.ReadAllText(state.Destination));
        Assert.AreEqual(root.Contents, File.ReadAllText(root.Destination));
        Assert.IsTrue(File.Exists(skipped.Source));
        Assert.AreEqual(0, skipped.ApplyCalls);
    }

    [TestMethod]
    public async Task FailureAfterMutationStopsFurtherUnitsAndExplicitRetryPreservesCompletedUnit()
    {
        using var fixture = new Fixture();
        var first = fixture.Operation("a-completed");
        var failed = fixture.Operation("b-failed");
        var later = fixture.Operation("c-later");
        failed.ApplyFailure = attempt => attempt == 1 ? new IOException("Injected after actual move.") : null;
        using var session = fixture.Create([first, failed, later]);
        await session.RunAsync();
        Assert.AreEqual(MigrationItemState.Completed, State(session, first.ItemId));
        Assert.AreEqual(MigrationItemState.Failed, State(session, failed.ItemId));
        Assert.AreEqual(MigrationItemState.Pending, State(session, later.ItemId));
        Assert.IsTrue(File.Exists(failed.Destination), "The fixture must reproduce a mutation before failure.");
        Assert.IsFalse(session.IsComplete);
        await session.RunAsync();
        Assert.AreEqual(1, failed.ApplyCalls, "Run must not implicitly retry an acknowledged failure.");
        await session.RetryAsync(failed.ItemId);
        Assert.AreEqual(MigrationItemState.Completed, State(session, failed.ItemId));
        Assert.AreEqual(1, first.ApplyCalls, "Retry must not repeat completed work.");
        await session.RunAsync();
        Assert.IsTrue(session.IsComplete);
        Assert.AreEqual(1, later.ApplyCalls);
        Assert.AreEqual(failed.Contents, File.ReadAllText(failed.Destination));
    }

    [DataTestMethod]
    [DataRow("check")]
    [DataRow("apply")]
    [DataRow("verify")]
    public async Task OrdinaryStageErrorsFailWithoutAutomaticRetry(string stage)
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.Fail(stage, _ => new IOException("Ordinary " + stage + " failure."));
        var delays = new List<TimeSpan>();
        using var session = fixture.Create([operation], delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        await session.RunAsync();
        Assert.AreEqual(MigrationItemState.Failed, State(session, operation.ItemId));
        Assert.AreEqual(1, operation.Calls(stage));
        Assert.AreEqual(0, delays.Count);
        StringAssert.Contains(session.Items.Single().Error!, "Ordinary " + stage);
        Assert.IsFalse(session.IsComplete);
        if (stage == "check") Assert.IsTrue(File.Exists(operation.Source));
        else Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
    }

    [TestMethod]
    public async Task RevertTouchesOnlyFailedUnitAndCannotUndoCompletedMigration()
    {
        using var fixture = new Fixture();
        var first = fixture.Operation("a-completed");
        var failed = fixture.Operation("b-failed");
        failed.VerifyFailure = _ => new IOException("Verification failed after move.");
        using var session = fixture.Create([first, failed]);
        await session.RunAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RevertAsync(first.ItemId));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RetryAsync(first.ItemId));
        await session.RevertAsync(failed.ItemId);
        Assert.AreEqual(MigrationItemState.Completed, State(session, first.ItemId));
        Assert.AreEqual(MigrationItemState.Reverted, State(session, failed.ItemId));
        Assert.AreEqual(first.Contents, File.ReadAllText(first.Destination));
        Assert.AreEqual(failed.Contents, File.ReadAllText(failed.Source));
        Assert.IsFalse(File.Exists(failed.Destination));
        Assert.IsFalse(session.IsComplete, "Restoring a failed item is not a successful migration.");
        await session.RunAsync();
        Assert.AreEqual(1, failed.ApplyCalls);
        Assert.AreEqual(0, first.RevertCalls);
    }

    [DataTestMethod]
    [DataRow("revert")]
    [DataRow("verify-reverted")]
    public async Task FailedRevertRemainsVisibleAndOnlyAnotherRevertCanFinishIt(string stage)
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.VerifyFailure = _ => new IOException("Migration verification failed.");
        operation.Fail(stage, attempt => attempt == 1 ? new IOException("Restore needs retry.") : null);
        using var session = fixture.Create([operation]);
        await session.RunAsync();
        await session.RevertAsync(operation.ItemId);
        Assert.AreEqual(MigrationItemState.RevertFailed, State(session, operation.ItemId));
        Assert.IsFalse(session.IsComplete);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RetryAsync(operation.ItemId));
        await session.RunAsync();
        Assert.AreEqual(1, operation.ApplyCalls);
        await session.RevertAsync(operation.ItemId);
        Assert.AreEqual(MigrationItemState.Reverted, State(session, operation.ItemId));
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Source));
        Assert.AreEqual(2, operation.RevertCalls);
    }

    [TestMethod]
    public async Task NonRevertibleFailedUnitCannotBeMarkedRestored()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.CanRevert = false;
        operation.ApplyFailure = _ => new IOException("Failure after move.");
        using var session = fixture.Create([operation]);
        await session.RunAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RevertAsync(operation.ItemId));
        Assert.AreEqual(MigrationItemState.Failed, State(session, operation.ItemId));
        Assert.AreEqual(0, operation.RevertCalls);
    }

    [DataTestMethod]
    [DataRow("check")]
    [DataRow("apply")]
    [DataRow("verify")]
    public async Task ExplicitTransientFailureCanSucceedOnEighthAttemptWithoutRepeatingOtherStages(string stage)
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.Fail(stage, attempt => attempt < 8 ? new MigrationTransientException("Temporary provider condition.") : null);
        var delays = new List<TimeSpan>();
        using var session = fixture.Create([operation], delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        await session.RunAsync();
        Assert.IsTrue(session.IsComplete);
        Assert.AreEqual(stage == "check" ? 9 : 8, operation.Calls(stage));
        foreach (var other in new[] { "check", "apply", "verify" }.Where(value => value != stage))
            Assert.AreEqual(other == "check" ? stage == "apply" ? 9 : 2 : 1, operation.Calls(other));
        CollectionAssert.AreEqual(new[] { 1d, 2d, 4d, 8d, 16d, 30d, 30d }, delays.Select(value => value.TotalSeconds).ToArray());
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
    }

    [TestMethod]
    public async Task TransientExhaustionStopsAtEightAndRetainsFailureWithoutWrongSuccess()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.ApplyFailure = _ => new MigrationTransientException("Still busy.");
        var waits = 0;
        using var session = fixture.Create([operation], delay: (_, _) => { waits++; return Task.CompletedTask; });
        await session.RunAsync();
        Assert.AreEqual(8, operation.ApplyCalls);
        Assert.AreEqual(7, waits);
        Assert.AreEqual(0, operation.VerifyCalls);
        Assert.AreEqual(MigrationItemState.Failed, State(session, operation.ItemId));
        Assert.IsFalse(session.IsComplete);
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
        await session.RunAsync();
        Assert.AreEqual(8, operation.ApplyCalls);
    }

    [TestMethod]
    public async Task ProviderRetryAfterIsNotShortenedByLocalBackoffMaximum()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.CheckFailure = attempt => attempt == 1 ? new MigrationTransientException("Provider throttled.", TimeSpan.FromSeconds(75)) : null;
        var delays = new List<TimeSpan>();
        using var session = fixture.Create([operation], delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        await session.RunAsync();
        Assert.IsTrue(session.IsComplete);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(75) }, delays);
    }

    [TestMethod]
    public async Task CancellationDuringBackoffPersistsPausedAndRequiresExplicitRetry()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var operation = fixture.Operation("state");
        operation.ApplyFailure = attempt => attempt == 1 ? new MigrationTransientException("Temporary lock.") : null;
        using var session = fixture.Create([operation], delay: (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await session.RunAsync(cancellation.Token);
        Assert.AreEqual(MigrationItemState.Paused, State(session, operation.ItemId));
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
        await session.RunAsync();
        Assert.AreEqual(1, operation.ApplyCalls);
        await session.RetryAsync(operation.ItemId);
        Assert.IsTrue(session.IsComplete);
        Assert.AreEqual(2, operation.ApplyCalls);
    }

    [TestMethod]
    public async Task CancellationDuringMutationIsVisibleAndRevertRestoresTheMovedFile()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var operation = fixture.Operation("state");
        operation.ApplyFailure = _ => { cancellation.Cancel(); return new OperationCanceledException(cancellation.Token); };
        using var session = fixture.Create([operation]);
        await session.RunAsync(cancellation.Token);
        Assert.AreEqual(MigrationItemState.Paused, State(session, operation.ItemId));
        await session.RevertAsync(operation.ItemId);
        Assert.AreEqual(MigrationItemState.Reverted, State(session, operation.ItemId));
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Source));
    }

    [DataTestMethod]
    [DataRow("Checking")]
    [DataRow("Running")]
    [DataRow("Verifying")]
    public async Task InterruptedActiveJournalReopensPausedAndRetriesIdempotently(string interruptedState)
    {
        using var fixture = new Fixture();
        var completed = fixture.Operation("a-completed");
        var interrupted = fixture.Operation("b-interrupted");
        interrupted.ApplyFailure = _ => new IOException("Crash boundary after move.");
        using (var initial = fixture.Create([completed, interrupted])) await initial.RunAsync();
        fixture.EditJournal(node => node["Items"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == interrupted.ItemId)!["State"] = interruptedState);
        interrupted.ApplyFailure = null;
        using var resumed = MigrationSession.Open(fixture.Journal, [completed, interrupted]);
        Assert.AreEqual(MigrationItemState.Paused, State(resumed, interrupted.ItemId));
        await resumed.RunAsync();
        Assert.AreEqual(1, interrupted.ApplyCalls);
        await resumed.RetryAsync(interrupted.ItemId);
        Assert.IsTrue(resumed.IsComplete);
        Assert.AreEqual(1, completed.ApplyCalls);
        Assert.AreEqual(interrupted.Contents, File.ReadAllText(interrupted.Destination));
    }

    [TestMethod]
    public async Task InterruptedRevertCanOnlyResumeRestorationAndNeverRestartMigration()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.VerifyFailure = _ => new IOException("Verification failed.");
        operation.RevertFailure = _ => new IOException("Interrupted after restore.");
        using (var initial = fixture.Create([operation])) { await initial.RunAsync(); await initial.RevertAsync(operation.ItemId); }
        fixture.EditJournal(node => node["Items"]![0]!["State"] = "Reverting");
        operation.RevertFailure = null;
        using var resumed = MigrationSession.Open(fixture.Journal, [operation]);
        Assert.AreEqual(MigrationItemState.RevertFailed, State(resumed, operation.ItemId));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => resumed.RetryAsync(operation.ItemId));
        await resumed.RevertAsync(operation.ItemId);
        Assert.AreEqual(MigrationItemState.Reverted, State(resumed, operation.ItemId));
        Assert.AreEqual(1, operation.ApplyCalls);
    }

    [TestMethod]
    public async Task CompletedSessionReopensWithoutRepeatingAnyDataAction()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        using (var initial = fixture.Create([operation])) await initial.RunAsync();
        using var reopened = MigrationSession.Open(fixture.Journal, [operation]);
        Assert.IsTrue(reopened.IsComplete);
        await reopened.RunAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => reopened.RetryAsync(operation.ItemId));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => reopened.RevertAsync(operation.ItemId));
        Assert.AreEqual(1, operation.ApplyCalls);
        Assert.AreEqual(0, operation.RevertCalls);
    }

    [TestMethod]
    public void InvalidSelectionOrMissingAdapterCannotCreateRunnableJournal()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        var root = fixture.Operation("root");
        var plan = MigrationPlan.Create([Item("state"), Item("root", "state")]);
        operation.ReviewedPlanFingerprint = root.ReviewedPlanFingerprint = plan.Fingerprint;
        Assert.ThrowsException<ArgumentException>(() => MigrationSession.Create(plan, [], fixture.Journal, [operation, root]));
        Assert.ThrowsException<ArgumentException>(() => MigrationSession.Create(plan, ["root"], fixture.Journal, [operation, root]));
        Assert.ThrowsException<ArgumentException>(() => MigrationSession.Create(plan, ["missing"], fixture.Journal, [operation, root]));
        Assert.ThrowsException<InvalidOperationException>(() => MigrationSession.Create(plan, ["state"], fixture.Journal, []));
        Assert.ThrowsException<ArgumentException>(() => MigrationSession.Create(plan, ["state"], fixture.Journal, [operation, operation]));
        Assert.IsFalse(File.Exists(fixture.Journal));
        Assert.IsTrue(File.Exists(operation.Source));
        using var valid = MigrationSession.Create(plan, ["state"], fixture.Journal, [operation]);
        Assert.AreEqual(MigrationItemState.Pending, valid.Items.Single().State);
    }

    [TestMethod]
    public void ExistingSessionLeasePreventsSecondWorkerAndReleasesAfterDispose()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        using (var active = fixture.Create([operation]))
        {
            Assert.ThrowsException<IOException>(() => MigrationSession.Open(fixture.Journal, [operation]));
            Assert.ThrowsException<IOException>(() => fixture.Create([operation]));
        }
        using var reopened = MigrationSession.Open(fixture.Journal, [operation]);
        Assert.AreEqual(MigrationItemState.Pending, reopened.Items.Single().State);
    }

    [TestMethod]
    public void ExistingJournalCannotBeOverwrittenAndFailedOpenReleasesLease()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        using (fixture.Create([operation])) { }
        var before = File.ReadAllBytes(fixture.Journal);
        Assert.ThrowsException<IOException>(() => fixture.Create([operation]));
        Assert.ThrowsException<InvalidOperationException>(() => MigrationSession.Open(fixture.Journal, []));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Journal));
        using var valid = MigrationSession.Open(fixture.Journal, [operation]);
    }

    [DataTestMethod]
    [DataRow("fingerprint")]
    [DataRow("selection")]
    [DataRow("completed-dependency")]
    [DataRow("duplicate")]
    [DataRow("schema")]
    [DataRow("unknown-field")]
    [DataRow("invalid-state")]
    [DataRow("missing-state")]
    [DataRow("started-dependency")]
    public void TamperedJournalCannotRunOrAlterFiles(string tamper)
    {
        using var fixture = new Fixture();
        var state = fixture.Operation("state");
        var root = fixture.Operation("root");
        var plan = MigrationPlan.Create([Item("state"), Item("root", "state")]);
        state.ReviewedPlanFingerprint = root.ReviewedPlanFingerprint = plan.Fingerprint;
        using (MigrationSession.Create(plan, ["state", "root"], fixture.Journal, [state, root])) { }
        fixture.EditJournal(node => {
            var items = node["Items"]!.AsArray();
            switch (tamper)
            {
                case "fingerprint": node["Plan"]!["Items"]![0]!["ProposedLocation"] = "C:/unexpected"; break;
                case "selection": items.Remove(items.Single(item => item!["Id"]!.GetValue<string>() == "state")); break;
                case "completed-dependency": items.Single(item => item!["Id"]!.GetValue<string>() == "root")!["State"] = "Completed"; break;
                case "duplicate": items.Add(items[0]!.DeepClone()); break;
                case "schema": node["SchemaVersion"] = 99; break;
                case "unknown-field": node["UnexpectedApproval"] = true; break;
                case "invalid-state": items[0]!["State"] = "PretendCompleted"; break;
                case "missing-state": items[0]!.AsObject().Remove("State"); break;
                case "started-dependency": items.Single(item => item!["Id"]!.GetValue<string>() == "root")!["State"] = "Running"; break;
            }
        });
        var before = File.ReadAllBytes(fixture.Journal);
        try { using var invalid = MigrationSession.Open(fixture.Journal, [state, root]); Assert.Fail("Tampered journal was accepted."); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Journal));
        Assert.IsTrue(File.Exists(state.Source));
        Assert.IsTrue(File.Exists(root.Source));
        Assert.AreEqual(0, state.ApplyCalls + root.ApplyCalls);
    }

    [TestMethod]
    public void MalformedAndOversizedJournalAreRejectedWithoutMutatingOriginal()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        File.WriteAllText(fixture.Journal, "{broken");
        Assert.ThrowsException<JsonException>(() => MigrationSession.Open(fixture.Journal, [operation]));
        Assert.AreEqual("{broken", File.ReadAllText(fixture.Journal));
        using (var oversized = new FileStream(fixture.Journal, FileMode.Create)) oversized.SetLength(4 * 1024 * 1024 + 1);
        Assert.ThrowsException<InvalidDataException>(() => MigrationSession.Open(fixture.Journal, [operation]));
        Assert.AreEqual(4 * 1024 * 1024 + 1L, new FileInfo(fixture.Journal).Length);
    }

    [TestMethod]
    public void LinkedJournalLockOrParentCannotRedirectMigrationRecords()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        var target = Path.Combine(fixture.Root, "outside.json");
        File.WriteAllText(target, "private fixture data");
        File.CreateSymbolicLink(fixture.Journal, target);
        Assert.ThrowsException<IOException>(() => fixture.Create([operation]));
        Assert.ThrowsException<IOException>(() => MigrationSession.Open(fixture.Journal, [operation]));
        File.Delete(fixture.Journal);
        File.CreateSymbolicLink(fixture.Journal + ".lock", target);
        Assert.ThrowsException<IOException>(() => fixture.Create([operation]));
        File.Delete(fixture.Journal + ".lock");
        var linked = Path.Combine(fixture.Root, "linked-parent");
        var directory = Path.Combine(fixture.Root, "other-directory");
        Directory.CreateDirectory(directory);
        Directory.CreateSymbolicLink(linked, directory);
        try
        {
            Assert.ThrowsException<IOException>(() => MigrationSession.Create(MigrationPlan.Create([Item("state")]), ["state"],
                Path.Combine(linked, "journal.json"), [operation]));
            Assert.AreEqual("private fixture data", File.ReadAllText(target));
            Assert.AreEqual(0, Directory.EnumerateFiles(directory).Count());
        }
        finally { Directory.Delete(linked); }
    }

    [TestMethod]
    public async Task ProgressAndStateChangesDescribeVerifiedCompletionAndRejectWrongItemProgress()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        var states = new List<MigrationItemState>();
        var progress = new List<MigrationProgress>();
        using var session = fixture.Create([operation]);
        session.Changed += () => states.Add(State(session, operation.ItemId));
        session.ProgressChanged += progress.Add;
        await session.RunAsync();
        CollectionAssert.AreEqual(new[] { MigrationItemState.Checking, MigrationItemState.Running, MigrationItemState.Verifying, MigrationItemState.Completed }, states);
        Assert.IsTrue(progress.Any(item => item.ItemId == "state" && item.Completed == 1 && item.Total == 1));
        Assert.IsTrue(session.IsComplete);
    }

    [DataTestMethod]
    [DataRow("other", 0, 1)]
    [DataRow("state", -1, 1)]
    [DataRow("state", 2, 1)]
    [DataRow("state", 0, -1)]
    public async Task InvalidProgressCannotMarkAnItemCompleted(string id, int completed, int total)
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.Progress = new(id, "Moving", "Invalid adapter progress.", completed, total);
        using var session = fixture.Create([operation]);
        await session.RunAsync();
        Assert.AreEqual(MigrationItemState.Failed, State(session, operation.ItemId));
        Assert.AreEqual(0, operation.VerifyCalls);
        Assert.IsFalse(session.IsComplete);
    }

    [TestMethod]
    public async Task ConcurrentActionAndDisposeCannotBypassActiveWorker()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.BeforeCheck = async _ => { entered.TrySetResult(); await release.Task; };
        using var session = fixture.Create([operation]);
        var running = session.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RunAsync());
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RetryAsync("state"));
            Assert.ThrowsException<InvalidOperationException>(session.Dispose);
        }
        finally { release.SetResult(); await running; }
        Assert.IsTrue(session.IsComplete);
    }

    [TestMethod]
    public async Task UnknownOrUnselectedItemCannotTriggerRecoveryAction()
    {
        using var fixture = new Fixture();
        var selected = fixture.Operation("state");
        var other = fixture.Operation("other");
        var plan = MigrationPlan.Create([Item("state"), Item("other")]);
        selected.ReviewedPlanFingerprint = other.ReviewedPlanFingerprint = plan.Fingerprint;
        using var session = MigrationSession.Create(plan, ["state"], fixture.Journal, [selected, other]);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => session.RetryAsync("other"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => session.RevertAsync("missing"));
        Assert.AreEqual(0, selected.ApplyCalls + other.ApplyCalls + other.RevertCalls);
    }

    [TestMethod]
    public void InvalidRetryPolicyCannotLeaveRunnableJournal()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        MigrationRetryPolicy[] invalid = [new(0), new(101), new(8, TimeSpan.FromSeconds(-1)),
            new(8, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2)), new(8, MaximumDelay: TimeSpan.FromMinutes(6))];
        foreach (var policy in invalid)
            Assert.ThrowsException<ArgumentException>(() => fixture.Create([operation], policy));
        Assert.IsFalse(File.Exists(fixture.Journal));
        using var valid = fixture.Create([operation]);
    }

    [TestMethod]
    public void AdapterMustMatchExactReviewedFingerprintOnCreateAndResume()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        var plan = MigrationPlan.Create([Item("state")]);
        operation.ReviewedPlanFingerprint = MigrationPlan.Create([Item("state") with { ProposedLocation = "C:/another-destination" }]).Fingerprint;
        Assert.ThrowsException<InvalidOperationException>(() => MigrationSession.Create(plan, ["state"], fixture.Journal, [operation]));
        Assert.IsFalse(File.Exists(fixture.Journal));
        using (fixture.Create([operation])) { }
        operation.ReviewedPlanFingerprint = new string('0', 64);
        Assert.ThrowsException<InvalidOperationException>(() => MigrationSession.Open(fixture.Journal, [operation]));
        Assert.IsTrue(File.Exists(operation.Source));
        Assert.AreEqual(0, operation.ApplyCalls);
    }

    [TestMethod]
    public async Task DurableJournalFailureAfterMutationStopsAllActionsUntilReopen()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        FileStream? journalBlocker = null;
        operation.ApplyFailure = _ => {
            journalBlocker = new FileStream(fixture.Journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            return null;
        };
        using (var session = fixture.Create([operation]))
        {
            try { await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => session.RunAsync()); }
            finally { journalBlocker?.Dispose(); }
            Assert.IsTrue(session.NeedsJournalRecovery);
            Assert.IsFalse(session.CanRetry(operation.ItemId));
            Assert.IsFalse(session.CanRevert(operation.ItemId));
            Assert.AreEqual(0, operation.VerifyCalls);
            Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RunAsync());
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RetryAsync(operation.ItemId));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RevertAsync(operation.ItemId));
            Assert.AreEqual(1, operation.ApplyCalls);
        }
        operation.ApplyFailure = null;
        using var reopened = MigrationSession.Open(fixture.Journal, [operation]);
        Assert.IsFalse(reopened.NeedsJournalRecovery);
        Assert.AreEqual(MigrationItemState.Paused, State(reopened, operation.ItemId));
        await reopened.RetryAsync(operation.ItemId);
        Assert.IsTrue(reopened.IsComplete);
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
    }

    [TestMethod]
    public async Task RecoveryChoicesFollowFailureAndRevertStateWithoutEnablingCompletedItems()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.ApplyFailure = _ => new IOException("Failed after move.");
        using var session = fixture.Create([operation]);
        Assert.IsFalse(session.CanRetry("state"));
        Assert.IsFalse(session.CanRevert("state"));
        Assert.IsFalse(session.CanRetry("unknown"));
        Assert.IsFalse(session.CanRevert("unknown"));
        await session.RunAsync();
        Assert.IsTrue(session.CanRetry("state"));
        Assert.IsTrue(session.CanRevert("state"));
        await session.RevertAsync("state");
        Assert.IsFalse(session.CanRetry("state"));
        Assert.IsFalse(session.CanRevert("state"));
        Assert.IsFalse(session.IsComplete);
    }

    [TestMethod]
    public async Task RetryDetailsAndPerItemOutcomeSurviveSessionReopen()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.CheckFailure = attempt => attempt < 3 ? new MigrationTransientException("Fixture temporarily busy.") : null;
        using (var session = fixture.Create([operation], delay: (_, _) => Task.CompletedTask))
        {
            await session.RunAsync();
            Assert.IsTrue(session.History.Any(entry => entry.ItemId == "state" && entry.Operation == "Waiting to retry"));
            Assert.AreEqual("Completed", session.History.Last().Operation);
        }
        using var reopened = MigrationSession.Open(fixture.Journal, [operation]);
        Assert.AreEqual(2, reopened.History.Count(entry => entry.Operation == "Waiting to retry"));
        Assert.AreEqual("Completed", reopened.History.Last().Operation);
        Assert.IsTrue(reopened.History.Zip(reopened.History.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        Assert.IsTrue(reopened.IsComplete);
    }

    [TestMethod]
    public async Task RestoredDependencyBlocksAllPendingDependentsWithoutTouchingTheirFiles()
    {
        using var fixture = new Fixture();
        var state = fixture.Operation("state");
        var root = fixture.Operation("root");
        var cloud = fixture.Operation("cloud");
        var plan = MigrationPlan.Create([Item("state"), Item("root", "state"), Item("cloud", "root")]);
        foreach (var operation in new[] { state, root, cloud }) operation.ReviewedPlanFingerprint = plan.Fingerprint;
        state.ApplyFailure = _ => new IOException("Failed after moving state.");
        using var session = MigrationSession.Create(plan, ["state", "root", "cloud"], fixture.Journal, [state, root, cloud]);
        await session.RunAsync();
        await session.RevertAsync("state");
        Assert.AreEqual(MigrationItemState.Reverted, State(session, "state"));
        Assert.AreEqual(MigrationItemState.Blocked, State(session, "root"));
        Assert.AreEqual(MigrationItemState.Blocked, State(session, "cloud"));
        Assert.IsFalse(session.CanRetry("root"));
        Assert.IsFalse(session.CanRevert("cloud"));
        await session.RunAsync();
        Assert.AreEqual(0, root.ApplyCalls + cloud.ApplyCalls);
        Assert.IsTrue(File.Exists(root.Source));
        Assert.IsTrue(File.Exists(cloud.Source));
        Assert.IsFalse(session.IsComplete);
    }

    [TestMethod]
    public async Task OwnershipIsRecheckedAfterBackoffBeforeRepeatingMutation()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.ApplyFailure = _ => new MigrationTransientException("Transient error after move.");
        using var session = fixture.Create([operation], delay: (_, _) => {
            File.WriteAllText(operation.Source, "Unrelated data created during retry delay.");
            return Task.CompletedTask;
        });
        await session.RunAsync();
        Assert.AreEqual(MigrationItemState.Failed, State(session, "state"));
        Assert.AreEqual(1, operation.ApplyCalls, "A changed destination must stop the next mutation attempt.");
        Assert.AreEqual(3, operation.CheckCalls);
        Assert.AreEqual("Unrelated data created during retry delay.", File.ReadAllText(operation.Source));
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
        StringAssert.Contains(session.Items.Single().Error!, "ambiguous");
    }

    [TestMethod]
    public async Task LateProgressFromCompletedStageCannotChangeCurrentReviewOrJournal()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        var reported = 0;
        using var session = fixture.Create([operation]);
        session.ProgressChanged += _ => reported++;
        await session.RunAsync();
        var beforeCount = reported;
        var beforeJournal = File.ReadAllBytes(fixture.Journal);
        operation.LastReporter!.Report(new("other-item", "Late callback", "This callback must be ignored.", -1, -2));
        Assert.AreEqual(beforeCount, reported);
        Assert.IsTrue(session.IsComplete);
        CollectionAssert.AreEqual(beforeJournal, File.ReadAllBytes(fixture.Journal));
    }

    [TestMethod]
    public async Task LateProgressFromFailedAttemptCannotInterruptItsRetry()
    {
        using var fixture = new Fixture();
        var operation = fixture.Operation("state");
        operation.ApplyFailure = attempt => attempt == 1 ? new MigrationTransientException("Fixture needs a retry.") : null;
        var lateUpdates = 0;
        using var session = fixture.Create([operation], delay: (_, _) => {
            // An adapter callback from an abandoned attempt must be closed before backoff begins.
            operation.LastReporter!.Report(new("other-item", "Late callback", "Do not use stale progress.", -1, -2));
            return Task.CompletedTask;
        });
        session.ProgressChanged += progress => { if (progress.Operation == "Late callback") lateUpdates++; };
        await session.RunAsync();
        Assert.IsTrue(session.IsComplete);
        Assert.AreEqual(2, operation.ApplyCalls);
        Assert.AreEqual(0, lateUpdates);
        Assert.AreEqual(operation.Contents, File.ReadAllText(operation.Destination));
    }

    [TestMethod]
    public async Task RevertedDependencyAndBlockedDescendantsSurviveRestartWhileIndependentWorkContinues()
    {
        using var fixture = new Fixture();
        var completed = fixture.Operation("a-completed");
        var failed = fixture.Operation("b-failed");
        var dependent = fixture.Operation("c-dependent");
        var independent = fixture.Operation("d-independent");
        var plan = MigrationPlan.Create([Item(completed.ItemId), Item(failed.ItemId),
            Item(dependent.ItemId, failed.ItemId), Item(independent.ItemId)]);
        var operations = new[] { completed, failed, dependent, independent };
        foreach (var operation in operations) operation.ReviewedPlanFingerprint = plan.Fingerprint;
        failed.ApplyFailure = _ => new IOException("Failed after moving the second item.");
        using (var session = MigrationSession.Create(plan, operations.Select(operation => operation.ItemId), fixture.Journal, operations))
        {
            await session.RunAsync();
            await session.RevertAsync(failed.ItemId);
        }
        using var reopened = MigrationSession.Open(fixture.Journal, operations);
        Assert.AreEqual(MigrationItemState.Completed, State(reopened, completed.ItemId));
        Assert.AreEqual(MigrationItemState.Reverted, State(reopened, failed.ItemId));
        Assert.AreEqual(MigrationItemState.Blocked, State(reopened, dependent.ItemId));
        Assert.IsFalse(reopened.CanRetry(dependent.ItemId));
        Assert.IsFalse(reopened.CanRevert(dependent.ItemId));
        await reopened.RunAsync();
        Assert.AreEqual(MigrationItemState.Completed, State(reopened, independent.ItemId));
        Assert.AreEqual(1, completed.ApplyCalls);
        Assert.AreEqual(0, completed.RevertCalls + dependent.ApplyCalls);
        Assert.AreEqual(failed.Contents, File.ReadAllText(failed.Source));
        Assert.IsTrue(File.Exists(dependent.Source));
        Assert.IsFalse(reopened.IsComplete, "A restored or blocked selection must not become a successful migration after restart.");
    }

    [TestMethod]
    public async Task JournalFailureAfterRestoreRequiresVerifiedRevertAndProtectsCompletedItemsAcrossRestart()
    {
        using var fixture = new Fixture();
        var completed = fixture.Operation("a-completed");
        var failed = fixture.Operation("b-failed");
        failed.VerifyFailure = _ => new IOException("Migration verification failed.");
        FileStream? blocker = null;
        failed.RevertFailure = _ => {
            blocker = new FileStream(fixture.Journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            return null;
        };
        using (var session = fixture.Create([completed, failed]))
        {
            await session.RunAsync();
            try { await Assert.ThrowsExceptionAsync<MigrationJournalException>(() => session.RevertAsync(failed.ItemId)); }
            finally { blocker?.Dispose(); }
            Assert.IsTrue(session.NeedsJournalRecovery);
            Assert.AreEqual(MigrationItemState.Reverting, State(session, failed.ItemId));
            Assert.AreEqual(0, failed.VerifyRevertedCalls);
            Assert.IsFalse(session.IsComplete);
            Assert.IsFalse(session.CanRevert(failed.ItemId));
            Assert.AreEqual(failed.Contents, File.ReadAllText(failed.Source));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RevertAsync(failed.ItemId));
        }
        failed.RevertFailure = null;
        using var reopened = MigrationSession.Open(fixture.Journal, [completed, failed]);
        Assert.AreEqual(MigrationItemState.RevertFailed, State(reopened, failed.ItemId));
        Assert.IsFalse(reopened.CanRetry(failed.ItemId));
        Assert.IsTrue(reopened.CanRevert(failed.ItemId));
        await reopened.RunAsync();
        Assert.AreEqual(1, failed.ApplyCalls);
        await reopened.RevertAsync(failed.ItemId);
        Assert.AreEqual(MigrationItemState.Reverted, State(reopened, failed.ItemId));
        Assert.AreEqual(1, failed.VerifyRevertedCalls);
        Assert.AreEqual(1, completed.ApplyCalls);
        Assert.AreEqual(0, completed.RevertCalls);
        Assert.AreEqual(completed.Contents, File.ReadAllText(completed.Destination));
        Assert.AreEqual(failed.Contents, File.ReadAllText(failed.Source));
        Assert.IsFalse(reopened.IsComplete);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CloudInlet-MigrationSession-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(Root, "migration.json");
        public Fixture() => Directory.CreateDirectory(Root);
        public FileOperation Operation(string id, List<string>? trace = null) => new(Root, id, trace);
        public MigrationSession Create(FileOperation[] operations, MigrationRetryPolicy? retryPolicy = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            var plan = MigrationPlan.Create(operations.Select(operation => Item(operation.ItemId)));
            foreach (var operation in operations) operation.ReviewedPlanFingerprint = plan.Fingerprint;
            return MigrationSession.Create(plan, operations.Select(operation => operation.ItemId), Journal, operations, retryPolicy, delay);
        }
        public void EditJournal(Action<JsonNode> edit)
        {
            var node = JsonNode.Parse(File.ReadAllText(Journal))!;
            edit(node);
            File.WriteAllText(Journal, node.ToJsonString());
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("CloudInlet-MigrationSession-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup target.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private sealed class FileOperation : IMigrationOperation
    {
        private readonly List<string>? _trace;
        public string ItemId { get; }
        public string ReviewedPlanFingerprint { get; set; } = "";
        public bool CanRevert { get; set; } = true;
        public string Source { get; }
        public string Destination { get; }
        public string Contents { get; }
        public int CheckCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public int VerifyRevertedCalls { get; private set; }
        public Func<int, Exception?>? CheckFailure { get; set; }
        public Func<int, Exception?>? ApplyFailure { get; set; }
        public Func<int, Exception?>? VerifyFailure { get; set; }
        public Func<int, Exception?>? RevertFailure { get; set; }
        public Func<int, Exception?>? VerifyRevertedFailure { get; set; }
        public Func<CancellationToken, Task>? BeforeCheck { get; set; }
        public MigrationProgress? Progress { get; set; }
        public IProgress<MigrationProgress>? LastReporter { get; private set; }
        public FileOperation(string root, string id, List<string>? trace)
        {
            ItemId = id; _trace = trace;
            Source = Path.Combine(root, id + ".source"); Destination = Path.Combine(root, id + ".destination");
            Contents = "Exact original content: " + id;
            File.WriteAllText(Source, Contents);
        }
        public async Task CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace?.Add(ItemId + ":check"); CheckCalls++;
            if (BeforeCheck is not null) await BeforeCheck(cancellationToken);
            Throw(CheckFailure, CheckCalls);
            if (File.Exists(Source) == File.Exists(Destination)) throw new IOException("Fixture ownership is ambiguous.");
            Assert.AreEqual(Contents, File.ReadAllText(File.Exists(Source) ? Source : Destination));
        }
        public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace?.Add(ItemId + ":apply"); ApplyCalls++;
            LastReporter = progress;
            if (File.Exists(Source)) File.Move(Source, Destination);
            Assert.AreEqual(Contents, File.ReadAllText(Destination));
            progress.Report(Progress ?? new(ItemId, "Moving", "Fixture file moved.", 1, 1));
            Throw(ApplyFailure, ApplyCalls);
            return Task.CompletedTask;
        }
        public Task VerifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace?.Add(ItemId + ":verify"); VerifyCalls++;
            Throw(VerifyFailure, VerifyCalls);
            Assert.IsFalse(File.Exists(Source)); Assert.AreEqual(Contents, File.ReadAllText(Destination));
            return Task.CompletedTask;
        }
        public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace?.Add(ItemId + ":revert"); RevertCalls++;
            if (File.Exists(Destination)) File.Move(Destination, Source);
            progress.Report(new(ItemId, "Restoring", "Fixture file restored.", 1, 1));
            Throw(RevertFailure, RevertCalls);
            return Task.CompletedTask;
        }
        public Task VerifyRevertedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace?.Add(ItemId + ":verify-reverted"); VerifyRevertedCalls++;
            Throw(VerifyRevertedFailure, VerifyRevertedCalls);
            Assert.IsFalse(File.Exists(Destination)); Assert.AreEqual(Contents, File.ReadAllText(Source));
            return Task.CompletedTask;
        }
        public void Fail(string stage, Func<int, Exception?> failure)
        {
            switch (stage)
            {
                case "check": CheckFailure = failure; break;
                case "apply": ApplyFailure = failure; break;
                case "verify": VerifyFailure = failure; break;
                case "revert": RevertFailure = failure; break;
                case "verify-reverted": VerifyRevertedFailure = failure; break;
                default: throw new ArgumentException("Unknown fixture stage.");
            }
        }
        public int Calls(string stage) => stage switch { "check" => CheckCalls, "apply" => ApplyCalls, "verify" => VerifyCalls,
            "revert" => RevertCalls, "verify-reverted" => VerifyRevertedCalls, _ => throw new ArgumentException("Unknown fixture stage.") };
        private static void Throw(Func<int, Exception?>? failure, int attempt) { if (failure?.Invoke(attempt) is { } error) throw error; }
    }
}

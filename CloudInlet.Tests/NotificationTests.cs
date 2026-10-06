using System.Text;
using System.Xml.Linq;
using CloudInlet.Core;
using CloudInlet.Core.Notifications;
using CloudInlet.Core.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static SyncSnapshot State(ClientState state) => new(state, "private raw error C:\\secret.txt applicationKey=hidden");
    private static UpdateSnapshot Update(UpdateState state, string version = "1.0.0") => new(state, "internal", new(version, "v" + version,
        new(UpdateBuildFlavor.Release, UpdateInstallerKind.Exe, "x64", "setup.exe", 100, new string('a', 64))));

    [TestMethod]
    public void DefaultsAvoidTransferCompletionNoiseAndSound()
    {
        var value = new NotificationPreferences();
        Assert.IsTrue(value.Enabled && value.BackupProblems && value.FolderChanges && value.Updates);
        Assert.IsFalse(value.SyncCompleted || value.Sound);
    }

    [TestMethod]
    public void HistoryIsNotReplayedAndDisabledStartupClearsPreviousSessionGroups()
    {
        var old = new ActivityEvent(Now.AddHours(-1), ActivityKind.Backup, "personal", "private");
        var policy = new NotificationPolicy(State(ClientState.UpToDate), [old]);
        var first = policy.Observe(State(ClientState.UpToDate), [old], null, new(Enabled: false), Now);
        Assert.AreEqual(0, first.Notices.Count);
        CollectionAssert.AreEquivalent(new[] { NotificationPolicy.ProblemsGroup, NotificationPolicy.FoldersGroup, NotificationPolicy.SyncGroup, NotificationPolicy.UpdatesGroup }, first.ClearGroups.ToArray());
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [old], null, new(), Now.AddSeconds(20)).Notices.Count);
        Assert.IsFalse(policy.HasPending);
    }

    [TestMethod]
    public void FolderChangesCoalesceAndNeverContainPrivateSourceDetails()
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        ActivityEvent[] activity = [new(Now, ActivityKind.Backup, "C:\\private\\Desktop", "sensitive"), new(Now.AddTicks(1), ActivityKind.Backup, "Photos", "secret")];
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), activity, null, new(), Now).Notices.Count);
        Assert.IsTrue(policy.HasPending);
        var notice = policy.Observe(State(ClientState.UpToDate), activity, null, new(), Now.AddSeconds(3)).Notices.Single();
        StringAssert.Contains(notice.Message, "2 folder");
        var xml = NotificationContent.CreateXml(notice, false);
        Assert.IsFalse(xml.Contains("private") || xml.Contains("Photos") || xml.Contains("secret"));
        Assert.IsFalse(policy.HasPending);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), activity, null, new(), Now.AddMinutes(10)).Notices.Count);
    }

    [TestMethod]
    public void AttentionEdgeNotifiesOnceAndPollingDoesNotRepeatIt()
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        policy.Observe(State(ClientState.Attention), [], null, new(), Now);
        var notice = policy.Observe(State(ClientState.Attention), [], null, new(), Now.AddSeconds(3)).Notices.Single();
        Assert.AreEqual(NotificationPolicy.ProblemsGroup, notice.Group);
        Assert.IsFalse(NotificationContent.CreateXml(notice, false).Contains("private raw"));
        Assert.AreEqual(0, policy.Observe(State(ClientState.Attention), [], null, new(), Now.AddHours(1)).Notices.Count);
        Assert.IsFalse(policy.HasPending);
        var recovered = policy.Observe(State(ClientState.UpToDate), [], null, new(), Now.AddHours(1).AddSeconds(1));
        CollectionAssert.Contains(recovered.ClearGroups.ToArray(), NotificationPolicy.ProblemsGroup);
        policy.Observe(State(ClientState.Attention), [], null, new(), Now.AddHours(2));
        Assert.AreEqual(1, policy.Observe(State(ClientState.Attention), [], null, new(), Now.AddHours(2).AddSeconds(3)).Notices.Count);
    }

    [TestMethod]
    public void DuplicateFileErrorsShareOneNoticeAndCooldownSuppressesAnotherBurst()
    {
        var policy = new NotificationPolicy(State(ClientState.Syncing), []);
        ActivityEvent[] errors = [new(Now, ActivityKind.Error, "secret1", "denied"), new(Now.AddTicks(1), ActivityKind.Error, "secret2", "denied")];
        policy.Observe(State(ClientState.Attention), errors, null, new(), Now);
        Assert.AreEqual(1, policy.Observe(State(ClientState.Attention), errors, null, new(), Now.AddSeconds(3)).Notices.Count);
        var more = errors.Append(new ActivityEvent(Now.AddSeconds(4), ActivityKind.Error, "secret3", "denied")).ToArray();
        Assert.AreEqual(0, policy.Observe(State(ClientState.Attention), more, null, new(), Now.AddSeconds(4)).Notices.Count);
        Assert.IsFalse(policy.HasPending);
    }

    [TestMethod]
    public void CompletionRequiresRealTransferAndExplicitOptIn()
    {
        var prefs = new NotificationPreferences(SyncCompleted: true);
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        policy.Observe(State(ClientState.Syncing), [], null, prefs, Now);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [], null, prefs, Now.AddSeconds(1)).Notices.Count);
        policy.Observe(State(ClientState.Syncing) with { ActiveTransfers = 1 }, [], null, prefs, Now.AddSeconds(2));
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [], null, prefs, Now.AddSeconds(3)).Notices.Count);
        Assert.AreEqual(NotificationPolicy.SyncGroup, policy.Observe(State(ClientState.UpToDate), [], null, prefs, Now.AddSeconds(6)).Notices.Single().Group);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [], null, prefs, Now.AddSeconds(7)).Notices.Count);
        policy.Observe(State(ClientState.Syncing) with { ActiveTransfers = 1 }, [], null, new(), Now.AddSeconds(8));
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [], null, new(), Now.AddSeconds(9)).Notices.Count);
    }

    [TestMethod]
    public void TinyTransfersBetweenUpToDateObservationsProduceOneQuietCompletionSummary()
    {
        var completed = new ActivityEvent(Now, ActivityKind.Upload, "private-small-file", "secret", 1);
        var next = new ActivityEvent(Now.AddSeconds(2), ActivityKind.Download, "another-private-file", "secret", 2);
        var preferences = new NotificationPreferences(SyncCompleted: true);
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [completed], null, preferences, Now).Notices.Count);
        Assert.IsTrue(policy.HasPending, "The observed activity records a real cycle even when both snapshots are already idle.");
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [completed, next], null, preferences, Now.AddSeconds(2)).Notices.Count);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [completed, next], null, preferences, Now.AddSeconds(3)).Notices.Count,
            "The next tiny transfer extends the quiet window rather than generating another banner.");
        var result = policy.Observe(State(ClientState.UpToDate), [completed, next], null, preferences, Now.AddSeconds(5));
        Assert.AreEqual(NotificationPolicy.SyncGroup, result.Notices.Single().Group);
        Assert.IsFalse(policy.HasPending);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [completed, next], null, preferences, Now.AddSeconds(30)).Notices.Count);
        var quiet = new NotificationPolicy(State(ClientState.UpToDate), []);
        quiet.Observe(State(ClientState.UpToDate), [completed, next], null, new(), Now);
        Assert.IsFalse(quiet.HasPending);
        Assert.AreEqual(0, quiet.Observe(State(ClientState.UpToDate), [completed, next], null, new(), Now.AddSeconds(30)).Notices.Count,
            "Completion summaries remain off by default.");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EnablingCompletionAfterADisabledTransferDoesNotReplayIt(bool masterDisabled)
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        var disabled = masterDisabled ? new NotificationPreferences(Enabled: false, SyncCompleted: true) : new NotificationPreferences();
        var completed = new ActivityEvent(Now.AddSeconds(1), ActivityKind.Upload, "private", "secret", 1);
        policy.Observe(State(ClientState.Syncing) with { ActiveTransfers = 1 }, [], null, disabled, Now);
        policy.Observe(State(ClientState.Syncing), [completed], null, disabled, Now.AddSeconds(1));
        var enabled = new NotificationPreferences(SyncCompleted: true);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [completed], null, enabled, Now.AddSeconds(2)).Notices.Count);
        Assert.IsFalse(policy.HasPending, "Disabled transfer intent must not survive the saved preference change.");
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [completed], null, enabled, Now.AddSeconds(10)).Notices.Count);
    }

    [TestMethod]
    public void LateCompletionActivityAndANewActiveCycleWaitForOneFinalQuietSummary()
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        var preferences = new NotificationPreferences(SyncCompleted: true);
        var first = new ActivityEvent(Now.AddSeconds(1), ActivityKind.Upload, "first", "done", 1);
        var second = new ActivityEvent(Now.AddSeconds(5), ActivityKind.Download, "second", "done", 2);
        policy.Observe(State(ClientState.Syncing) with { ActiveTransfers = 1 }, [], null, preferences, Now);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [], null, preferences, Now.AddMilliseconds(500)).Notices.Count);
        Assert.IsTrue(policy.HasPending);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [first], null, preferences, Now.AddSeconds(1)).Notices.Count);
        policy.Observe(State(ClientState.Syncing) with { ActiveTransfers = 1 }, [first], null, preferences, Now.AddSeconds(2));
        Assert.IsFalse(policy.HasPending, "A new active cycle defers completion without keeping an idle timer running.");
        Assert.AreEqual(0, policy.Observe(State(ClientState.Syncing) with { ActiveTransfers = 1 }, [first], null, preferences, Now.AddSeconds(4)).Notices.Count);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [first, second], null, preferences, Now.AddSeconds(5)).Notices.Count);
        var result = policy.Observe(State(ClientState.UpToDate), [first, second], null, preferences, Now.AddSeconds(8));
        Assert.AreEqual(NotificationPolicy.SyncGroup, result.Notices.Single().Group);
        Assert.IsFalse(policy.HasPending);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [first, second], null, preferences, Now.AddMinutes(1)).Notices.Count);
    }

    [TestMethod]
    public void DisablingImmediatelyClearsOwnedGroupsAndCancelsPendingBatches()
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        policy.Observe(State(ClientState.UpToDate), [], Update(UpdateState.Available), new(), Now);
        policy.Observe(State(ClientState.Attention), [new(Now, ActivityKind.Backup, "private", "secret")], Update(UpdateState.Available), new(), Now.AddSeconds(1));
        var disabled = policy.Observe(State(ClientState.Attention), [], Update(UpdateState.Available), new(Enabled: false), Now.AddSeconds(2));
        Assert.AreEqual(0, disabled.Notices.Count);
        Assert.IsFalse(policy.HasPending);
        CollectionAssert.Contains(disabled.ClearGroups.ToArray(), NotificationPolicy.UpdatesGroup);
        Assert.AreEqual(0, policy.Observe(State(ClientState.Attention), [], Update(UpdateState.Ready), new(Enabled: false), Now.AddMinutes(10)).Notices.Count);
    }

    [DataTestMethod]
    [DataRow(NotificationPolicy.ProblemsGroup)]
    [DataRow(NotificationPolicy.FoldersGroup)]
    [DataRow(NotificationPolicy.SyncGroup)]
    [DataRow(NotificationPolicy.UpdatesGroup)]
    public void CategoryDisableRemovesPreviousSessionEntriesOnceWithoutCurrentSessionNotices(string group)
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        var enabled = new NotificationPreferences(SyncCompleted: true);
        var baseline = policy.Observe(State(ClientState.UpToDate), [], null, enabled, Now);
        Assert.AreEqual(0, baseline.Notices.Count, "This process has never shown a notice or tracked a visible group.");
        var disabled = group switch
        {
            NotificationPolicy.ProblemsGroup => enabled with { BackupProblems = false },
            NotificationPolicy.FoldersGroup => enabled with { FolderChanges = false },
            NotificationPolicy.SyncGroup => enabled with { SyncCompleted = false },
            _ => enabled with { Updates = false }
        };
        var changed = policy.Observe(State(ClientState.UpToDate), [], null, disabled, Now.AddSeconds(1));
        CollectionAssert.AreEqual(new[] { group }, changed.ClearGroups.ToArray(),
            "The category's entries from the previous Windows session must be removed on disable.");
        var unrelated = policy.Observe(State(ClientState.Syncing), [], null, disabled, Now.AddSeconds(2));
        Assert.AreEqual(0, unrelated.ClearGroups.Count, "An unrelated sync observation must not repeat group removal.");
        var stillDisabled = policy.Observe(State(ClientState.UpToDate), [], null, disabled, Now.AddSeconds(3));
        Assert.AreEqual(0, stillDisabled.ClearGroups.Count);
    }

    [TestMethod]
    public void UpdatesUseCurrentVersionActionsAndPortableOnlyOffersNavigation()
    {
        var policy = new NotificationPolicy(State(ClientState.UpToDate), []);
        var available = policy.Observe(State(ClientState.UpToDate), [], Update(UpdateState.Available), new(), Now).Notices.Single();
        Assert.AreEqual(new NotificationCommand(NotificationAction.DownloadUpdate, "1.0.0"), available.Buttons[0].Command);
        Assert.AreEqual(0, policy.Observe(State(ClientState.UpToDate), [], Update(UpdateState.Available), new(), Now.AddSeconds(1)).Notices.Count);
        var ready = policy.Observe(State(ClientState.UpToDate), [], Update(UpdateState.Ready), new(), Now.AddSeconds(2)).Notices.Single();
        Assert.AreEqual(new NotificationCommand(NotificationAction.InstallUpdate, "1.0.0"), ready.Buttons[0].Command);
        var next = policy.Observe(State(ClientState.UpToDate), [], Update(UpdateState.Available, "1.0.1"), new(), Now.AddSeconds(3), false).Notices.Single();
        Assert.AreEqual(1, next.Buttons.Count);
        Assert.AreEqual(NotificationAction.ViewUpdates, next.Buttons[0].Command.Action);
    }

    [TestMethod]
    public void XmlEscapesTextAndCarriesOnlyExactAllowlistedActions()
    {
        foreach (var action in Enum.GetValues<NotificationAction>())
        {
            var command = new NotificationCommand(action, action is NotificationAction.DownloadUpdate or NotificationAction.InstallUpdate ? "1.0.0" : null);
            var notice = new NotificationNotice("group", "tag", "Title <&>", "Message <&>", command, [new("Act <&>", command)]);
            var xml = XDocument.Parse(NotificationContent.CreateXml(notice, false));
            Assert.AreEqual("Title <&>", xml.Descendants("text").First().Value);
            Assert.IsTrue(NotificationCommandCodec.TryDecode(xml.Root!.Attribute("launch")!.Value, out var body));
            Assert.AreEqual(command, body);
            Assert.IsTrue(NotificationCommandCodec.TryDecode(xml.Descendants("action").Single().Attribute("arguments")!.Value, out var button));
            Assert.AreEqual(command, button);
            Assert.AreEqual("true", xml.Descendants("audio").Single().Attribute("silent")!.Value);
            Assert.IsFalse(XDocument.Parse(NotificationContent.CreateXml(notice, true)).Descendants("audio").Any());
        }
    }

    [DataTestMethod]
    [DataRow(null)] [DataRow("")] [DataRow("cbn1:quit")] [DataRow("cbn1:folder:C:\\secret")]
    [DataRow("cbn1:install")] [DataRow("cbn1:install:1.0.0:other")] [DataRow("cbn1:install:01.0.0")]
    [DataRow("cbn1:download:999.0.0")] [DataRow("cbn1:Activity")] [DataRow("cbn1:activity&path=secret")]
    public void UnknownOrPathBearingArgumentsAreRejected(string? value) => Assert.IsFalse(NotificationCommandCodec.TryDecode(value, out _));

    [TestMethod]
    public async Task PipeReadsCompletePacketsAndRejectsTruncatedOrOversizedCommands()
    {
        await using var split = new FragmentedStream(Encoding.UTF8.GetBytes("cbn1:install:1.0.0"));
        Assert.AreEqual("cbn1:install:1.0.0", await ClientActivation.ReadCommandAsync(split));
        Assert.IsNull(await ClientActivation.ReadCommandAsync(new MemoryStream(Encoding.UTF8.GetBytes("cbn1:install:"))));
        Assert.IsNull(await ClientActivation.ReadCommandAsync(new MemoryStream(Encoding.UTF8.GetBytes("show" + new string('x', 100)))));
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}

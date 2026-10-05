using CloudBay.Core.Updates;

namespace CloudBay.Core.Notifications;

/// <summary>Coalesces new events into private summaries and suppresses polling/history replay.</summary>
public sealed class NotificationPolicy
{
    public const string ProblemsGroup = "backup-problems";
    public const string FoldersGroup = "folder-changes";
    public const string SyncGroup = "sync-complete";
    public const string UpdatesGroup = "updates";
    private readonly HashSet<ActivityEvent> _seen = [];
    private readonly Queue<ActivityEvent> _history = new();
    private readonly HashSet<string> _visible = [];
    private NotificationPreferences _previous = new();
    private DateTimeOffset? _problemDue;
    private DateTimeOffset? _lastProblem;
    private DateTimeOffset? _folderDue;
    private int _folders;
    private bool _transferred;
    private bool _completionPending;
    private string? _updateKey;
    private ClientState _state;
    private bool _firstObservation = true;
    public bool HasPending => _problemDue is not null || _folderDue is not null;

    public NotificationPolicy(SyncSnapshot initial, IEnumerable<ActivityEvent> history)
    {
        _state = initial.State;
        foreach (var item in history) Remember(item);
    }

    public NotificationBatch Observe(SyncSnapshot snapshot, IEnumerable<ActivityEvent> activity,
        UpdateSnapshot? update, NotificationPreferences preferences, DateTimeOffset now, bool canInstallUpdates = true)
    {
        var notices = new List<NotificationNotice>();
        var clear = new HashSet<string>();
        foreach (var item in activity)
        {
            if (!Remember(item)) continue;
            if (item.Kind == ActivityKind.Backup && preferences.Enabled && preferences.FolderChanges)
            { _folders++; _folderDue ??= now.AddSeconds(3); }
            if (item.Kind is ActivityKind.Upload or ActivityKind.Download && item.Completed) _transferred = true;
            if (item.Kind is ActivityKind.Error or ActivityKind.Conflict) ScheduleProblem();
        }
        if (snapshot.ActiveTransfers > 0) _transferred = true;
        if (snapshot.State is ClientState.Attention or ClientState.Offline &&
            (_firstObservation || snapshot.State != _state || !_previous.Enabled || !_previous.BackupProblems)) ScheduleProblem();
        else if (snapshot.State is not (ClientState.Attention or ClientState.Offline) && _state is ClientState.Attention or ClientState.Offline)
        { _problemDue = null; Clear(ProblemsGroup); }
        if (snapshot.State == ClientState.UpToDate && _state != ClientState.UpToDate && _transferred)
        { _completionPending = true; _transferred = false; }
        _state = snapshot.State;

        if (!preferences.Enabled)
        {
            foreach (var group in _visible.ToArray()) Clear(group);
            if (_firstObservation || _previous.Enabled)
                clear.UnionWith([ProblemsGroup, FoldersGroup, SyncGroup, UpdatesGroup]);
            _folders = 0; _folderDue = null; _problemDue = null; _completionPending = false; _updateKey = null;
            _previous = preferences;
            _firstObservation = false;
            return new(notices, clear.ToArray());
        }
        if (!preferences.BackupProblems) { Clear(ProblemsGroup); if (_firstObservation) clear.Add(ProblemsGroup); _problemDue = null; }
        if (!preferences.FolderChanges) { Clear(FoldersGroup); if (_firstObservation) clear.Add(FoldersGroup); _folders = 0; _folderDue = null; }
        if (!preferences.SyncCompleted) { Clear(SyncGroup); if (_firstObservation) clear.Add(SyncGroup); _completionPending = false; }
        if (!preferences.Updates) { Clear(UpdatesGroup); if (_firstObservation) clear.Add(UpdatesGroup); _updateKey = null; }
        if (_firstObservation && snapshot.State is not (ClientState.Attention or ClientState.Offline)) clear.Add(ProblemsGroup);

        if (preferences.BackupProblems && _problemDue <= now &&
            (_lastProblem is null || now - _lastProblem >= TimeSpan.FromMinutes(5)))
        {
            Add(new(ProblemsGroup, "attention", "CloudBay needs your attention",
                snapshot.State == ClientState.Offline ? "Backup will continue when the connection is available. Review activity for details."
                    : "Some files or backup operations need a review. Open activity for details.",
                new(NotificationAction.ViewActivity), [new("View activity", new(NotificationAction.ViewActivity)), new("Retry sync", new(NotificationAction.RetrySync))]));
            _lastProblem = now; _problemDue = null;
        }
        if (preferences.FolderChanges && _folderDue <= now && _folders > 0)
        {
            Add(new(FoldersGroup, "changed", "Folder backup updated",
                _folders == 1 ? "A folder backup setting changed. Review the current folders in CloudBay." : $"{_folders} folder backup settings changed. Review the current folders in CloudBay.",
                new(NotificationAction.ManageBackup), [new("Manage backup", new(NotificationAction.ManageBackup)), new("Open folder", new(NotificationAction.OpenFolder))]));
            _folders = 0; _folderDue = null;
        }
        if (preferences.SyncCompleted && _completionPending && snapshot.State == ClientState.UpToDate)
        {
            Add(new(SyncGroup, "complete", "Your files are up to date", "CloudBay finished transferring your files.",
                new(NotificationAction.ViewActivity), [new("View activity", new(NotificationAction.ViewActivity)), new("Open folder", new(NotificationAction.OpenFolder))]));
            _completionPending = false;
        }
        if (preferences.Updates && update?.Candidate is { } candidate && update.State is UpdateState.Available or UpdateState.Ready)
        {
            var key = candidate.Version + ":" + update.State;
            if (_updateKey != key || !_previous.Enabled || !_previous.Updates)
            {
                var ready = update.State == UpdateState.Ready;
                var buttons = canInstallUpdates
                    ? new NotificationButton[] { new(ready ? "Install update" : "Download update", new(ready ? NotificationAction.InstallUpdate : NotificationAction.DownloadUpdate, candidate.Version)), new("View update", new(NotificationAction.ViewUpdates)) }
                    : [new("View update", new(NotificationAction.ViewUpdates))];
                Add(new(UpdatesGroup, "version", ready ? "CloudBay update is ready" : "A CloudBay update is available",
                    ready ? $"Version {candidate.Version} was downloaded and verified. Install when you are ready." : $"Version {candidate.Version} is available for your installed edition.",
                    new(NotificationAction.ViewUpdates), buttons));
                _updateKey = key;
            }
        }
        else { Clear(UpdatesGroup); if (_firstObservation) clear.Add(UpdatesGroup); _updateKey = null; }
        _previous = preferences;
        _firstObservation = false;
        return new(notices, clear.ToArray());

        void Add(NotificationNotice notice) { notices.Add(notice); _visible.Add(notice.Group); }
        void Clear(string group) { if (_visible.Remove(group)) clear.Add(group); }
        void ScheduleProblem()
        {
            // New problems during a burst share one notice. An unchanged attention state
            // never creates another timer or reminder merely because polling continues.
            if (_lastProblem is null || now - _lastProblem >= TimeSpan.FromMinutes(5))
                _problemDue ??= now.AddSeconds(3);
        }
    }

    private bool Remember(ActivityEvent item)
    {
        if (!_seen.Add(item)) return false;
        _history.Enqueue(item);
        while (_history.Count > 512) _seen.Remove(_history.Dequeue());
        return true;
    }
}

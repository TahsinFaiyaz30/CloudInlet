using System.Collections.ObjectModel;
using System.ComponentModel;
using CloudBay.Application;
using CloudBay.Core;

namespace CloudBay.ViewModels;

public sealed class ClientViewModel : INotifyPropertyChanged
{
    private readonly ClientController _controller;
    private ActivityEvent[] _lastActivity = [];
    private readonly Dictionary<string, TransferItem> _transferItems = new(StringComparer.Ordinal);
    private string _activityFilter = "All";
    public ClientPreview? Preview { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];
    public ObservableCollection<TransferItem> ActiveTransfers { get; } = [];
    public ObservableCollection<TransferItem> QueuedTransfers { get; } = [];
    public ObservableCollection<TransferItem> RecentTransfers { get; } = [];
    public ObservableCollection<object> ActivityRows { get; private set; } = [];
    public string StatusTitle { get; private set; } = "Ready to connect";
    public string StatusDescription { get; private set; } = "Connect your Backblaze B2 bucket to start protecting your files.";
    public string StatusGlyph { get; private set; } = "\uE753";
    public string PendingLabel { get; private set; } = "Connect your account to start syncing";
    public string LastSyncLabel { get; private set; } = "No sync completed yet";
    public string LastSyncFullLabel { get; private set; } = "";
    public string BucketLabel { get; private set; } = "Backblaze B2";
    public string PauseLabel { get; private set; } = "Pause syncing";
    public bool HasActivity { get; private set; }
    public bool IsConfigured { get; private set; }
    public bool IsProgressVisible { get; private set; }
    public bool IsProgressIndeterminate { get; private set; }
    public double Progress { get; private set; }
    public string ProgressLabel { get; private set; } = "";
    public string StorageSummary { get; private set; } = "";
    public bool HasStatusDetail { get; private set; }
    public bool HasLastSync { get; private set; }
    public bool HasPending { get; private set; }
    public bool HasStorageSummary { get; private set; }
    public bool HasTransfers { get; private set; }
    public bool HasQueue { get; private set; }
    public bool HasActivityRows { get; private set; }
    public bool HasTransferSummary { get; private set; }
    public string TransferSummary { get; private set; } = "";
    public string AdditionalTransfersSummary { get; private set; } = "";
    public bool HasAdditionalTransfers { get; private set; }
    public string QueueSummary { get; private set; } = "";
    public string ActivityEmptyMessage { get; private set; } = "No activity yet";
    public string QueueCoverage { get; private set; } = "";
    public bool HasQueueCoverage { get; private set; }
    public string ActivityFilter => _activityFilter;
    public string TransferSpeedSummary { get; private set; } = "";
    public bool HasTransferSpeed { get; private set; }

    public ClientViewModel(ClientController controller)
    {
        _controller = controller;
        Refresh();
    }

    // Changed can originate from the background worker or a Cloud Files callback.
    // Both windows refresh this model on their own dispatcher.
    public void Refresh()
    {
        var snapshot = Preview?.Snapshot ?? _controller.Snapshot;
        var settings = Preview?.Settings ?? _controller.Settings;
        IsConfigured = settings.IsConfigured;
        StatusTitle = snapshot.State switch
        {
            ClientState.NotConnected => "Ready to connect",
            ClientState.Connecting => "Connecting to your cloud",
            ClientState.Syncing => "Syncing your files",
            ClientState.UpToDate => "Your files are up to date",
            ClientState.Paused => "Syncing is paused",
            ClientState.Offline => "Waiting for a connection",
            _ => "Your attention is needed"
        };
        StatusGlyph = snapshot.State switch
        {
            ClientState.UpToDate => "\uE73E",
            ClientState.Syncing or ClientState.Connecting => "\uE895",
            ClientState.Paused => "\uE769",
            ClientState.Attention => "\uE7BA",
            ClientState.Offline => "\uEB55",
            _ => "\uE753"
        };
        StatusDescription = snapshot.Message;
        HasStatusDetail = (snapshot.State is ClientState.Attention or ClientState.Offline or ClientState.Paused or ClientState.Syncing) &&
            !string.IsNullOrWhiteSpace(snapshot.Message) &&
            snapshot.Message is not ("Sync paused" or "Syncing is paused" or "Paused" or "Syncing" or "Syncing your files");
        HasLastSync = snapshot.LastSync.HasValue && snapshot.State == ClientState.UpToDate;
        HasPending = IsConfigured && snapshot.Pending > 0;
        HasStorageSummary = IsConfigured && (snapshot.LastSync.HasValue || snapshot.FileCount > 0);
        PendingLabel = snapshot.Pending > 0 ? $"{snapshot.Pending:N0} file{(snapshot.Pending == 1 ? "" : "s")} waiting to sync" : "";
        LastSyncLabel = snapshot.LastSync is { } time ? FormatLastSync(time) : "";
        LastSyncFullLabel = snapshot.LastSync is { } fullTime ? fullTime.ToLocalTime().ToString("f") : "";
        StorageSummary = HasStorageSummary ? $"{snapshot.FileCount:N0} files · {FormatSize(snapshot.CloudBytes)} in cloud · {FormatSize(snapshot.LocalBytes)} on this PC" : "";
        BucketLabel = IsConfigured ? settings.BucketName : "Backblaze B2";
        PauseLabel = snapshot.State == ClientState.Paused ? "Resume syncing" : "Pause syncing";
        IsProgressVisible = snapshot.State is ClientState.Syncing or ClientState.Connecting;
        var progress = ProgressPresentation.ForSnapshot(snapshot);
        Progress = progress.Value;
        ProgressLabel = progress.Label;
        IsProgressIndeterminate = progress.IsIndeterminate;
        TransferSpeedSummary = snapshot.State != ClientState.Paused ? string.Join(" · ", new[]
        {
            snapshot.UploadBytesPerSecond > 0 ? $"↑ {FormatSpeed(snapshot.UploadBytesPerSecond)}" : "",
            snapshot.DownloadBytesPerSecond > 0 ? $"↓ {FormatSpeed(snapshot.DownloadBytesPerSecond)}" : ""
        }.Where(value => value.Length > 0)) : "";
        HasTransferSpeed = TransferSpeedSummary.Length > 0;
        var events = (Preview?.Activity ?? _controller.Activity)
            .Where(item => item.Completed || item.Kind is not (ActivityKind.Upload or ActivityKind.Download))
            .OrderByDescending(item => item.Time).ToArray();
        if (!_lastActivity.SequenceEqual(events))
        {
            _lastActivity = events;
            // A completed transfer adds one history entry. Retain all other
            // row identities so the native virtualizer keeps its viewport and
            // existing render state instead of receiving a full remove/add.
            var retained = Activity.GroupBy(row => row.Event)
                .ToDictionary(group => group.Key, group => new Queue<ActivityItem>(group));
            var history = new List<ActivityItem>(events.Length);
            foreach (var item in events)
            {
                var display = retained.TryGetValue(item, out var matches) && matches.Count > 0
                    ? matches.Dequeue() : new ActivityItem(item);
                history.Add(display);
            }
            ReplaceIfChanged(Activity, history);
            ReplaceIfChanged(RecentActivity, history.Take(3).ToArray());
        }
        HasActivity = events.Length > 0;
        RefreshTransfers(snapshot);
        foreach (var row in Activity)
            row.Actions.SetTarget(ActivityLocationResolver.ForActivity(row.Event, settings), Preview is not null);
        foreach (var row in _transferItems.Values)
            row.Actions.SetTarget(ActivityLocationResolver.ForTransfer(row.Transfer, settings), Preview is not null);
        if (HasTransferSummary && snapshot.State == ClientState.Syncing)
        {
            StatusDescription = TransferSummary;
            HasStatusDetail = true;
        }
        RefreshActivityRows();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void SetActivityFilter(string filter)
    {
        if (filter is not ("All" or "Active" or "Queue" or "History")) throw new ArgumentOutOfRangeException(nameof(filter));
        if (_activityFilter == filter) return;
        _activityFilter = filter;
        // A filter changes the list's complete logical view. Replace its source
        // atomically so the native virtualizer cannot retain a stale anchor
        // through hundreds of remove/insert notifications. Progress refreshes
        // still update existing rows and retain the current scroll position.
        RefreshActivityRows(resetView: true);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void RefreshTransfers(SyncSnapshot snapshot)
    {
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<TransferItem>();
        var queued = new List<TransferItem>();
        foreach (var transfer in snapshot.Transfers)
        {
            if (!liveIds.Add(transfer.Id)) continue;
            // A root needing attention does not stop another root's live
            // traffic. Global pause suppresses all displayed wire rates.
            var displayed = snapshot.State == ClientState.Paused ? transfer with { BytesPerSecond = 0 } : transfer;
            if (!_transferItems.TryGetValue(transfer.Id, out var row))
            {
                row = new TransferItem(displayed);
                _transferItems.Add(transfer.Id, row);
            }
            else row.Update(displayed);
            if (transfer.Phase is TransferPhase.Queued or TransferPhase.Paused or TransferPhase.Retrying) queued.Add(row);
            else active.Add(row);
        }
        foreach (var id in _transferItems.Keys.Where(id => !liveIds.Contains(id)).ToArray()) _transferItems.Remove(id);
        ReplaceIfChanged(ActiveTransfers, active);
        ReplaceIfChanged(QueuedTransfers, queued);
        ReplaceIfChanged(RecentTransfers, active.Take(3).ToArray());
        var activeCount = Math.Max(snapshot.ActiveTransfers, active.Count);
        var queueCount = Math.Max(snapshot.QueuedTransfers, queued.Count);
        HasTransfers = activeCount > 0;
        HasQueue = queueCount > 0;
        HasTransferSummary = HasTransfers || HasQueue;
        AdditionalTransfersSummary = activeCount > RecentTransfers.Count ? $"{activeCount - RecentTransfers.Count:N0} more in progress" : "";
        HasAdditionalTransfers = AdditionalTransfersSummary.Length > 0;
        TransferSummary = string.Join(" · ", new[]
        {
            activeCount > 0 ? $"{activeCount:N0} in progress" : "",
            queueCount > 0 ? $"{queueCount:N0} queued" : ""
        }.Where(value => value.Length > 0));
        QueueSummary = queueCount > 0 ? $"{queueCount:N0} file{(queueCount == 1 ? "" : "s")} queued" : "";
        QueueCoverage = queueCount > queued.Count ? $"Showing the next {queued.Count:N0} of {queueCount:N0} queued files. This list updates as files start." : "";
        HasQueueCoverage = QueueCoverage.Length > 0 && _activityFilter is "All" or "Queue";
    }

    private void RefreshActivityRows(bool resetView = false)
    {
        IEnumerable<object> rows = _activityFilter switch
        {
            "Active" => ActiveTransfers,
            "Queue" => QueuedTransfers,
            "History" => Activity,
            _ => ActiveTransfers.Cast<object>().Concat(QueuedTransfers).Concat(Activity)
        };
        var displayedRows = rows.ToArray();
        // A wholly different view has no retained scroll anchor. Swapping its
        // source atomically avoids a native virtualizer retaining containers
        // from an emptied source after a resize or complete history refresh.
        var retainedRows = new HashSet<object>(ActivityRows);
        if (resetView || ActivityRows.Count > 0 && !displayedRows.Any(retainedRows.Contains))
            ActivityRows = new ObservableCollection<object>(displayedRows);
        else ReplaceIfChanged(ActivityRows, displayedRows);
        HasActivityRows = ActivityRows.Count > 0;
        ActivityEmptyMessage = _activityFilter switch
        {
            "Active" => "No files are transferring right now",
            "Queue" => "No files are waiting to sync",
            "History" => "No completed activity yet",
            _ => "No activity yet"
        };
        HasQueueCoverage = QueueCoverage.Length > 0 && _activityFilter is "All" or "Queue";
    }

    private static void ReplaceIfChanged<T>(ObservableCollection<T> collection, IReadOnlyList<T> rows)
    {
        // Progress updates reuse row objects. Keeping their containers avoids
        // resetting scroll position and rebuilding a virtualized list each tick.
        if (collection.SequenceEqual(rows)) return;
        var retained = new HashSet<T>(rows);
        for (var index = collection.Count - 1; index >= 0; index--)
            if (!retained.Contains(collection[index])) collection.RemoveAt(index);
        for (var index = 0; index < rows.Count; index++)
        {
            if (index < collection.Count && EqualityComparer<T>.Default.Equals(collection[index], rows[index])) continue;
            var previous = collection.IndexOf(rows[index]);
            if (previous >= 0) collection.Move(previous, index); else collection.Insert(index, rows[index]);
        }
    }

    public void SetPreview(ClientPreview? preview)
    {
        if (preview is not null && !Environment.GetCommandLineArgs().Contains("--ui-smoke"))
            throw new InvalidOperationException("Presentation fixtures are available only during isolated UI validation.");
        Preview = preview;
        Refresh();
    }

    public static string FormatSize(long bytes) => ProgressPresentation.FormatSize(bytes);

    public static string FormatSpeed(double bytesPerSecond)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s"];
        var value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatLastSync(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.UtcNow - time;
        if (elapsed.TotalMinutes < 1) return "Last synced just now";
        if (elapsed.TotalHours < 1) return $"Last synced {(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 24) return $"Last synced {(int)elapsed.TotalHours} hr ago";
        return $"Last synced {time.ToLocalTime():g}";
    }
}

public sealed class ActivityItem(ActivityEvent activity) : IActivityActionRow
{
    public ActivityEvent Event { get; } = activity;
    public ActivityRowActions Actions { get; } = new();
    public string SpeedText => "";
    public Microsoft.UI.Xaml.Visibility SpeedVisibility => Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ProgressVisibility => Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ProgressLabelVisibility => Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ProgressLabel => "";
    public double Progress => 0;
    public bool IsIndeterminate => false;
    public string Location => Path;
    public string ProgressAccessibleName => "";
    public string Title { get; } = activity.Kind switch
    {
        ActivityKind.Upload => activity.Completed ? "Uploaded" : "Uploading",
        ActivityKind.Download => activity.Completed ? "Downloaded" : "Downloading",
        ActivityKind.Delete => "Deleted",
        ActivityKind.Restore => "Restored",
        ActivityKind.Backup when activity.Message.Contains("enabled", StringComparison.OrdinalIgnoreCase) => "Backup enabled",
        ActivityKind.Backup when activity.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase) || activity.Message.Contains("restored to", StringComparison.OrdinalIgnoreCase) => "Backup stopped",
        ActivityKind.Backup when activity.Message.Contains("Recovered", StringComparison.OrdinalIgnoreCase) => "Backup recovered",
        ActivityKind.Backup => "Folder backup",
        ActivityKind.Conflict => "Conflict preserved",
        ActivityKind.Error => "Needs attention",
        _ => "CloudBay"
    };
    public string FileName { get; } = string.IsNullOrEmpty(activity.Path) ? activity.Message : activity.Path.Replace('\\', '/').Split('/').Last();
    public string Detail { get; } = activity.Message;
    public string Path { get; } = activity.Path;
    public string TimeText { get; } = activity.Time.LocalDateTime.Date == DateTime.Today ? activity.Time.ToLocalTime().ToString("t") : activity.Time.ToLocalTime().ToString("g");
    public string TimeFullText { get; } = activity.Time.ToLocalTime().ToString("f");
    public string SizeText { get; } = activity.Bytes > 0 ? ClientViewModel.FormatSize(activity.Bytes) : "";
    public string Summary => string.Join(" · ", new[] { Title, SizeText, TimeText }.Where(value => value.Length > 0));
    public Microsoft.UI.Xaml.Visibility DetailVisibility { get; } = activity.Kind is ActivityKind.Error or ActivityKind.Conflict ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string Glyph { get; } = activity.Kind switch
    {
        ActivityKind.Upload => "\uE898",
        ActivityKind.Download => "\uE896",
        ActivityKind.Delete => "\uE74D",
        ActivityKind.Restore => "\uE777",
        ActivityKind.Backup => "\uE8B7",
        ActivityKind.Conflict or ActivityKind.Error => "\uE7BA",
        _ => "\uE946"
    };
    public override string ToString() => $"{Title}: {FileName}. {Detail}. {TimeText}.";
}

public sealed class TransferItem : INotifyPropertyChanged, IActivityActionRow
{
    private TransferSnapshot _transfer;
    public TransferSnapshot Transfer => _transfer;
    public ActivityRowActions Actions { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public TransferItem(TransferSnapshot transfer) => _transfer = transfer;
    public string Id => _transfer.Id;
    public string FileName => _transfer.RelativePath.Replace('\\', '/').Split('/').Last();
    public string Path => _transfer.RelativePath;
    public string Location => string.IsNullOrEmpty(_transfer.RootName) ? Path : $"{_transfer.RootName} · {Path}";
    public string Glyph => _transfer.Kind == ActivityKind.Download ? "\uE896" : "\uE898";
    public string Phase => _transfer.Phase switch
    {
        TransferPhase.Queued => _transfer.Kind == ActivityKind.Download ? "Queued for download" : "Queued for upload",
        TransferPhase.Hashing => _transfer.Kind == ActivityKind.Download ? "Preparing download" : "Preparing upload",
        TransferPhase.Uploading => "Uploading",
        TransferPhase.Downloading => "Downloading",
        TransferPhase.Verifying => _transfer.Kind == ActivityKind.Download ? "Verifying download" : "Verifying upload",
        TransferPhase.Retrying => _transfer.Kind == ActivityKind.Download ? "Retrying download" : "Retrying upload",
        TransferPhase.Paused => _transfer.Kind == ActivityKind.Download ? "Download paused" : "Upload paused",
        _ => "Transferring"
    };
    public string Summary => _transfer.TotalBytes > 0 && IsIndeterminate
        ? $"{Phase} · {ClientViewModel.FormatSize(_transfer.TotalBytes)}" : Phase;
    public string ProgressLabel => ProgressPresentation.ForTransfer(_transfer).Label;
    public Microsoft.UI.Xaml.Visibility ProgressLabelVisibility => ProgressLabel.Length > 0 && _transfer.Phase != TransferPhase.Queued
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string SpeedText => _transfer.Phase is TransferPhase.Uploading or TransferPhase.Downloading &&
        _transfer.BytesPerSecond > 0 ? ClientViewModel.FormatSpeed(_transfer.BytesPerSecond) : "";
    public Microsoft.UI.Xaml.Visibility SpeedVisibility => SpeedText.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public double Progress => ProgressPresentation.ForTransfer(_transfer).Value;
    public bool IsIndeterminate => ProgressPresentation.ForTransfer(_transfer).IsIndeterminate;
    public Microsoft.UI.Xaml.Visibility ProgressVisibility => _transfer.Phase == TransferPhase.Queued ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility DetailVisibility => Microsoft.UI.Xaml.Visibility.Collapsed;
    public string Detail => "";
    public string TimeFullText => "";
    public string ProgressAccessibleName => $"{Location}: {Summary}{(ProgressLabel.Length > 0 ? $" · {ProgressLabel}" : "")}{(SpeedText.Length > 0 ? $" · {SpeedText}" : "")}";
    public void Update(TransferSnapshot transfer)
    {
        if (_transfer == transfer) return;
        _transfer = transfer;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public override string ToString() => $"{Location}. {Summary}.";
}

public sealed class VersionItem(CloudObject file)
{
    public CloudObject File { get; } = file;
    public string Modified { get; } = file.ModifiedUtc.ToLocalTime().ToString("g");
    public string Size { get; } = ClientViewModel.FormatSize(file.Size);
    public string State { get; } = file.Action == "upload" ? "File version" : "Deletion marker";
    public string Identifier { get; } = file.FileId;
    public override string ToString() => $"{State}, {Modified}, {Size}.";
}

public sealed record SyncFolderItem(string Name, string? BackupName, string RootPath)
{
    public override string ToString() => Name;
}

// Rendering fixtures have no connection to controller state, storage, Windows
// registration, or transfer code. Never used outside the --ui-smoke process.
public sealed record ClientPreview(AppSettings Settings, SyncSnapshot Snapshot, IReadOnlyList<ActivityEvent> Activity)
{
    public IReadOnlyDictionary<string, string>? WindowsFolderPaths { get; init; }

    public static ClientPreview ForState(ClientState state)
    {
        if (state == ClientState.NotConnected)
            return new(new AppSettings(), new(ClientState.NotConnected, "Connect your Backblaze B2 bucket to start protecting your files."), []);
        var connected = Connected(state == ClientState.Syncing);
        var message = state switch
        {
            ClientState.Paused => "Paused for 1 hour. Syncing resumes at 3:30 PM.",
            ClientState.Offline => "CloudBay will reconnect automatically when this PC is online.",
            ClientState.Attention => "Review required: 24 files were removed from this PC. Review them before syncing these deletions to B2.",
            _ => connected.Snapshot.Message
        };
        return connected with { Snapshot = connected.Snapshot with { State = state, Message = message, Pending = state == ClientState.Attention ? 24 : connected.Snapshot.Pending } };
    }

    public static ClientPreview Connected(bool transferring = false)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudBay");
        var settings = new AppSettings { KeyId = "presentation-only", BucketId = "presentation-only", BucketName = "Personal files", RootPath = root,
            Backups = [new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(root, "Desktop")),
                new("Saved Games", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), Path.Combine(root, "Saved Games"))],
            CustomBackups = [new("Projects", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects"), "CloudBay/Projects/")] };
        var now = DateTimeOffset.UtcNow;
        var activity = new ActivityEvent[] {
            new(now.AddMinutes(-1), ActivityKind.Upload, "Documents/Proposal.docx", "Uploaded", 184320)
                { Location = new(root, null, "Documents/Proposal.docx", settings.BucketId, settings.Prefix) },
            new(now.AddMinutes(-3), ActivityKind.Download, "Pictures/Weekend.jpg", "Downloaded on demand", 2460000)
                { Location = new(root, null, "Pictures/Weekend.jpg", settings.BucketId, settings.Prefix) },
            new(now.AddMinutes(-8), ActivityKind.Backup, "Desktop", "Folder backup enabled")
                { Location = new(root, null, "Desktop", settings.BucketId, settings.Prefix) } };
        var snapshot = transferring
            ? new SyncSnapshot(ClientState.Syncing, "Syncing your files", 5, 1284, 7516192768, 3221225472, 98304, 184320, now.AddMinutes(-10))
            {
                ActiveTransfers = 2, QueuedTransfers = 3,
                UploadBytesPerSecond = 2621440, DownloadBytesPerSecond = 524288,
                Transfers = [
                    new("main:proposal", "CloudBay", "Documents/Proposal.docx", ActivityKind.Upload, TransferPhase.Uploading, 98304, 184320) { BytesPerSecond = 2621440 },
                    new("main:weekend", "CloudBay", "Pictures/Weekend.jpg", ActivityKind.Download, TransferPhase.Downloading, 1228800, 2460000) { BytesPerSecond = 524288 },
                    new("main:notes", "CloudBay", "Desktop/Meeting notes.txt", ActivityKind.Upload, TransferPhase.Queued, 0, 5120),
                    new("projects:report", "Projects", "Reports/Proposal.docx", ActivityKind.Upload, TransferPhase.Queued, 0, 122880),
                    new("main:guide", "CloudBay", "Documents/Guide.pdf", ActivityKind.Download, TransferPhase.Queued, 0, 2457600)]
            }
            : new SyncSnapshot(ClientState.UpToDate, "All files are in sync", 0, 1284, 7516192768, 3221225472, LastSync: now.AddMinutes(-1));
        return new(settings, snapshot, activity);
    }

    public static ClientPreview ActivityActions()
    {
        var preview = Connected();
        var settings = preview.Settings;
        var custom = settings.CustomBackups[0];
        var now = DateTimeOffset.UtcNow;
        const string path = "Screenshots/A longer screenshot file name from a Windows desktop session.png";
        return preview with { Activity = [
            new(now, ActivityKind.Upload, "Pictures/" + path, "Uploaded", 75612)
                { Location = new(settings.RootPath, null, "Pictures/" + path, settings.BucketId, settings.Prefix) },
            new(now.AddMinutes(-1), ActivityKind.Download, custom.Name + "/Reports/Quarterly report.pdf", "Downloaded", 3145728)
                { Location = new(custom.SourcePath, custom.Name, "Reports/Quarterly report.pdf", settings.BucketId, custom.Prefix) },
            new(now.AddMinutes(-2), ActivityKind.Backup, "Desktop", "Folder backup enabled")
                { Location = new(settings.RootPath, null, "Desktop", settings.BucketId, settings.Prefix) },
            new(now.AddMinutes(-3), ActivityKind.Upload, "Pictures/Old account.png", "Retained history from another account", 1234)
                { Location = new(settings.RootPath, null, "Pictures/Old account.png", "previous-bucket", settings.Prefix) },
            new(now.AddMinutes(-4), ActivityKind.Upload, "Pictures/Legacy history.png", "History without a recorded cloud location", 1234)
        ] };
    }

    public static ClientPreview TransferQueue(int queueCount = 360)
    {
        var preview = Connected(transferring: true);
        var active = new TransferSnapshot[]
        {
            new("main:video", "CloudBay", "Videos/Screen recording from the weekend.mp4", ActivityKind.Upload, TransferPhase.Uploading, 536870912, 2007883776) { BytesPerSecond = 13107200 },
            new("main:download", "CloudBay", "Pictures/A long file name from the camera collection.jpg", ActivityKind.Download, TransferPhase.Downloading, 1228800, 2460000) { BytesPerSecond = 786432 },
            new("projects:verify", "Projects", "Reports/Quarterly report.pdf", ActivityKind.Upload, TransferPhase.Verifying, 2457600, 2457600)
        };
        var queued = Enumerable.Range(0, Math.Min(253, queueCount)).Select(index => new TransferSnapshot(
            $"queue:{index}", index % 3 == 0 ? "Projects" : "CloudBay",
            $"Documents/Project {index:D3}/A document with a longer file name {index:D3}.docx",
            index % 2 == 0 ? ActivityKind.Upload : ActivityKind.Download, TransferPhase.Queued, 0, 184320)).ToArray();
        return preview with
        {
            Snapshot = preview.Snapshot with
            {
                ActiveTransfers = active.Length, QueuedTransfers = queueCount, Pending = active.Length + queueCount,
                UploadBytesPerSecond = 13107200, DownloadBytesPerSecond = 786432,
                Transfers = active.Concat(queued).ToArray(), TransferredBytes = 538099712, TransferTotalBytes = 2012801376
            }
        };
    }
}

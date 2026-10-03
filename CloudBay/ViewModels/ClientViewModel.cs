using System.Collections.ObjectModel;
using System.ComponentModel;
using CloudBay.Application;
using CloudBay.Core;

namespace CloudBay.ViewModels;

public sealed class ClientViewModel : INotifyPropertyChanged
{
    private readonly ClientController _controller;
    private ActivityEvent[] _lastActivity = [];
    public ClientPreview? Preview { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];
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
    public double Progress { get; private set; }
    public string ProgressLabel { get; private set; } = "";
    public string StorageSummary { get; private set; } = "";
    public bool HasStatusDetail { get; private set; }
    public bool HasLastSync { get; private set; }
    public bool HasPending { get; private set; }
    public bool HasStorageSummary { get; private set; }

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
        Progress = snapshot.TransferTotalBytes > 0 ? Math.Clamp(100d * snapshot.TransferredBytes / snapshot.TransferTotalBytes, 0, 100) : 0;
        ProgressLabel = snapshot.TransferTotalBytes > 0 ? $"{FormatSize(snapshot.TransferredBytes)} of {FormatSize(snapshot.TransferTotalBytes)}" : "";
        var events = (Preview?.Activity ?? _controller.Activity).OrderByDescending(item => item.Time).ToArray();
        if (!_lastActivity.SequenceEqual(events))
        {
            _lastActivity = events;
            Activity.Clear();
            RecentActivity.Clear();
            foreach (var item in events)
            {
                var display = new ActivityItem(item);
                Activity.Add(display);
                if (RecentActivity.Count < 3) RecentActivity.Add(display);
            }
        }
        HasActivity = events.Length > 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void SetPreview(ClientPreview? preview)
    {
        if (preview is not null && !Environment.GetCommandLineArgs().Contains("--ui-smoke"))
            throw new InvalidOperationException("Presentation fixtures are available only during isolated UI validation.");
        Preview = preview;
        Refresh();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes) * 1d;
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

public sealed class ActivityItem(ActivityEvent activity)
{
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
            new(now.AddMinutes(-1), ActivityKind.Upload, "Documents/Proposal.docx", "Uploaded", 184320),
            new(now.AddMinutes(-3), ActivityKind.Download, "Pictures/Weekend.jpg", "Downloaded on demand", 2460000),
            new(now.AddMinutes(-8), ActivityKind.Backup, "Desktop", "Folder backup enabled") };
        var snapshot = transferring
            ? new SyncSnapshot(ClientState.Syncing, "Uploading Proposal.docx", 3, 1284, 7516192768, 3221225472, 98304, 184320, now.AddMinutes(-10))
            : new SyncSnapshot(ClientState.UpToDate, "All files are in sync", 0, 1284, 7516192768, 3221225472, LastSync: now.AddMinutes(-1));
        return new(settings, snapshot, activity);
    }
}

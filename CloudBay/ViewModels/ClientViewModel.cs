using System.Collections.ObjectModel;
using System.ComponentModel;
using CloudBay.Application;
using CloudBay.Core;

namespace CloudBay.ViewModels;

public sealed class ClientViewModel : INotifyPropertyChanged
{
    private readonly ClientController _controller;
    private ActivityEvent[] _lastActivity = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];
    public string StatusTitle { get; private set; } = "Ready to connect";
    public string StatusDescription { get; private set; } = "Connect your Backblaze B2 bucket to start protecting your files.";
    public string StatusGlyph { get; private set; } = "\uE753";
    public string StatusLabel { get; private set; } = "Not connected";
    public string FileCount { get; private set; } = "—";
    public string CloudSize { get; private set; } = "—";
    public string LocalSize { get; private set; } = "—";
    public string PendingLabel { get; private set; } = "Connect your account to start syncing";
    public string LastSyncLabel { get; private set; } = "No sync completed yet";
    public string BucketLabel { get; private set; } = "Backblaze B2";
    public string RootLabel { get; private set; } = "";
    public string BackupLabel { get; private set; } = "No folders selected";
    public string PauseLabel { get; private set; } = "Pause syncing";
    public bool HasActivity { get; private set; }
    public bool IsConfigured { get; private set; }
    public bool IsProgressVisible { get; private set; }
    public double Progress { get; private set; }
    public string ProgressLabel { get; private set; } = "";

    public ClientViewModel(ClientController controller)
    {
        _controller = controller;
        Refresh();
    }

    // Changed can originate from the background worker or a Cloud Files callback.
    // Both windows refresh this model on their own dispatcher.
    public void Refresh()
    {
        var snapshot = _controller.Snapshot;
        var settings = _controller.Settings;
        IsConfigured = settings.IsConfigured;
        StatusTitle = snapshot.State switch
        {
            ClientState.NotConnected => "Ready to connect",
            ClientState.Connecting => "Connecting to your cloud",
            ClientState.Syncing => "Keeping your files in sync",
            ClientState.UpToDate => "Your files are up to date",
            ClientState.Paused => "Syncing is paused",
            ClientState.Offline => "Waiting for a connection",
            _ => "Your attention is needed"
        };
        StatusLabel = snapshot.State switch
        {
            ClientState.NotConnected => "Not connected",
            ClientState.UpToDate => "Up to date",
            _ => snapshot.State.ToString()
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
        FileCount = IsConfigured ? snapshot.FileCount.ToString("N0") : "—";
        CloudSize = IsConfigured ? FormatSize(snapshot.CloudBytes) : "—";
        LocalSize = IsConfigured ? FormatSize(snapshot.LocalBytes) : "—";
        PendingLabel = snapshot.Pending > 0 ? $"{snapshot.Pending:N0} file{(snapshot.Pending == 1 ? "" : "s")} waiting to sync" : IsConfigured ? "No files waiting to sync" : "Connect your account to start syncing";
        LastSyncLabel = snapshot.LastSync is { } time ? $"Last sync: {time.ToLocalTime():g}" : "No sync completed yet";
        BucketLabel = IsConfigured ? settings.BucketName : "Backblaze B2";
        RootLabel = settings.RootPath;
        var backups = settings.Backups.Count + settings.CustomBackups.Count;
        BackupLabel = backups == 0 ? "No folders selected" : $"{backups} folder{(backups == 1 ? "" : "s")} protected";
        PauseLabel = snapshot.State == ClientState.Paused ? "Resume syncing" : "Pause syncing";
        IsProgressVisible = snapshot.State is ClientState.Syncing or ClientState.Connecting;
        Progress = snapshot.TransferTotalBytes > 0 ? Math.Clamp(100d * snapshot.TransferredBytes / snapshot.TransferTotalBytes, 0, 100) : 0;
        ProgressLabel = snapshot.TransferTotalBytes > 0 ? $"{FormatSize(snapshot.TransferredBytes)} of {FormatSize(snapshot.TransferTotalBytes)}" : snapshot.Message;
        var events = _controller.Activity.OrderByDescending(item => item.Time).ToArray();
        if (!_lastActivity.SequenceEqual(events))
        {
            _lastActivity = events;
            Activity.Clear();
            RecentActivity.Clear();
            foreach (var item in events)
            {
                var display = new ActivityItem(item);
                Activity.Add(display);
                if (RecentActivity.Count < 5) RecentActivity.Add(display);
            }
        }
        HasActivity = events.Length > 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes) * 1d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
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
        ActivityKind.Backup => "Folder backup",
        ActivityKind.Conflict => "Conflict preserved",
        ActivityKind.Error => "Needs attention",
        _ => "CloudBay"
    };
    public string FileName { get; } = string.IsNullOrEmpty(activity.Path) ? activity.Message : activity.Path.Replace('\\', '/').Split('/').Last();
    public string Detail { get; } = activity.Message;
    public string Path { get; } = activity.Path;
    public string TimeText { get; } = activity.Time.ToLocalTime().ToString("g");
    public string SizeText { get; } = activity.Bytes > 0 ? ClientViewModel.FormatSize(activity.Bytes) : "";
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

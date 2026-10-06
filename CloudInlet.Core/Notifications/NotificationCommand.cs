using CloudInlet.Core.Updates;

namespace CloudInlet.Core.Notifications;

public enum NotificationAction { ViewActivity, ManageBackup, OpenFolder, RetrySync, ViewUpdates, DownloadUpdate, InstallUpdate }
public sealed record NotificationCommand(NotificationAction Action, string? Version = null);

/// <summary>Notification arguments carry a fixed command and, for update actions, a stable version. Never a path or URI.</summary>
public static class NotificationCommandCodec
{
    public const int MaximumLength = 64;
    public static string Encode(NotificationCommand command)
    {
        var action = command.Action switch
        {
            NotificationAction.ViewActivity => "activity", NotificationAction.ManageBackup => "backup",
            NotificationAction.OpenFolder => "folder", NotificationAction.RetrySync => "retry",
            NotificationAction.ViewUpdates => "updates", NotificationAction.DownloadUpdate => "download",
            NotificationAction.InstallUpdate => "install", _ => throw new ArgumentException("Unsupported notification action.")
        };
        var updating = command.Action is NotificationAction.DownloadUpdate or NotificationAction.InstallUpdate;
        if (updating ? !UpdateVersion.TryParse(command.Version, out _) : command.Version is not null)
            throw new ArgumentException("Invalid notification version.");
        return "cbn1:" + action + (updating ? ":" + command.Version : "");
    }

    public static bool TryDecode(string? argument, out NotificationCommand command)
    {
        command = new(NotificationAction.ViewActivity);
        if (argument is null || argument.Length > MaximumLength || !argument.StartsWith("cbn1:", StringComparison.Ordinal)) return false;
        var parts = argument[5..].Split(':');
        var action = parts[0] switch
        {
            "activity" => NotificationAction.ViewActivity, "backup" => NotificationAction.ManageBackup,
            "folder" => NotificationAction.OpenFolder, "retry" => NotificationAction.RetrySync,
            "updates" => NotificationAction.ViewUpdates, "download" => NotificationAction.DownloadUpdate,
            "install" => NotificationAction.InstallUpdate, _ => (NotificationAction)(-1)
        };
        if (!Enum.IsDefined(action)) return false;
        var updating = action is NotificationAction.DownloadUpdate or NotificationAction.InstallUpdate;
        if (updating ? parts.Length != 2 || !UpdateVersion.TryParse(parts[1], out _) : parts.Length != 1) return false;
        command = new(action, updating ? parts[1] : null);
        return true;
    }
}

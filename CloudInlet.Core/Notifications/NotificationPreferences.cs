namespace CloudInlet.Core.Notifications;

/// <summary>Windows notifications are optional; transfer completion is quiet by default.</summary>
public sealed record NotificationPreferences(bool Enabled = true, bool BackupProblems = true,
    bool FolderChanges = true, bool SyncCompleted = false, bool Updates = true, bool Sound = false);

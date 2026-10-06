using CloudInlet.Core.Notifications;
using Microsoft.Windows.AppNotifications;

namespace CloudInlet.Windows;

/// <summary>Windows owns notification display and Do Not Disturb. Failures never stop backup or updates.</summary>
internal sealed class WindowsNotifications : IAsyncDisposable
{
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private readonly Action<NotificationCommand> _activation;
    private readonly Action<string> _unavailable;
    private readonly Func<NotificationPreferences> _preferences;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _registered;
    private bool _disposed;
    private bool _reported;

    public WindowsNotifications(Action<NotificationCommand> activation, Action<string> unavailable, Func<NotificationPreferences> preferences)
    { _activation = activation; _unavailable = unavailable; _preferences = preferences; }

    public bool Register()
    {
        if (_disposed) return false;
        try
        {
            // Register can deliver a cold COM invocation immediately, before the main window exists.
            _manager.NotificationInvoked += Invoked;
            if (UpdateInstallation.IsPackaged) _manager.Register();
            else _manager.Register(CloudInlet.Core.BuildInfo.ProductName, new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", CloudInlet.Core.BuildInfo.NotificationIconFileName)));
            _registered = true;
            return true;
        }
        catch (Exception) { _manager.NotificationInvoked -= Invoked; ReportUnavailable(); return false; }
    }

    private void Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try { if (!_disposed && NotificationCommandCodec.TryDecode(args.Argument, out var command)) _activation(command); }
        catch (Exception) { /* Invalid/native callback failure must not terminate backup. */ }
    }

    public async Task PublishAsync(NotificationBatch batch)
    {
        if (!_registered || _disposed) return;
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var group in batch.ClearGroups) await _manager.RemoveByGroupAsync(group);
            foreach (var notice in batch.Notices)
            {
                var current = _preferences();
                if (!current.Enabled || !(notice.Group switch
                {
                    NotificationPolicy.ProblemsGroup => current.BackupProblems, NotificationPolicy.FoldersGroup => current.FolderChanges,
                    NotificationPolicy.SyncGroup => current.SyncCompleted, NotificationPolicy.UpdatesGroup => current.Updates, _ => false
                })) continue;
                var notification = new AppNotification(NotificationContent.CreateXml(notice, current.Sound))
                {
                    Group = notice.Group, Tag = notice.Tag, Expiration = DateTimeOffset.UtcNow.AddDays(1)
                };
                _manager.Show(notification);
            }
        }
        catch (Exception) { ReportUnavailable(); }
        finally { _operations.Release(); }
    }

    private void ReportUnavailable()
    {
        if (_reported || _disposed) return;
        _reported = true;
        _unavailable("Windows notifications are unavailable. Backup and updates will continue; check Windows notification settings.");
    }

    /// <summary>Permanent uninstall only. This touches this executable's notification registration, never client settings.</summary>
    public static async Task<bool> RemoveRegistrationAsync()
    {
        AppNotificationManager? manager = null;
        var registered = false;
        var removed = false;
        try
        {
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += IgnoreActivation;
            if (UpdateInstallation.IsPackaged) manager.Register();
            else manager.Register(CloudInlet.Core.BuildInfo.ProductName, new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", CloudInlet.Core.BuildInfo.NotificationIconFileName)));
            registered = true;
            await RemoveAllAsync(manager).WaitAsync(TimeSpan.FromSeconds(5));
            removed = true;
        }
        catch (Exception) { /* The caller receives failure without exposing notification payloads or blocking indefinitely. */ }
        finally
        {
            if (registered)
            {
                try { manager!.UnregisterAll(); }
                catch (Exception) { removed = false; }
            }
            if (manager is not null) { try { manager.NotificationInvoked -= IgnoreActivation; } catch (Exception) { } }
        }
        return removed;

        static async Task RemoveAllAsync(AppNotificationManager manager) => await manager.RemoveAllAsync();
        static void IgnoreActivation(AppNotificationManager manager, AppNotificationActivatedEventArgs args) { }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            try { _manager.NotificationInvoked -= Invoked; } catch (Exception) { }
            if (_registered)
            {
                // Keep Windows activation registration for existing Notification Center entries.
                // UnregisterAll is reserved for permanent uninstall/isolated validation cleanup.
                try { _manager.Unregister(); } catch (Exception) { }
                _registered = false;
            }
        }
        finally { _operations.Release(); }
    }
}

using System.Collections.Concurrent;
using CloudBay.Core;
using CloudBay.Core.Notifications;
using CloudBay.Windows;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace CloudBay;

public partial class App
{
    private WindowsNotifications? _notifications;
    private NotificationPolicy? _notificationPolicy;
    private bool _notificationCanInstallUpdates;
    private readonly ConcurrentQueue<NotificationCommand> _notificationCommands = new();
    private readonly TaskCompletionSource<NotificationCommand> _firstNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _updatesInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Timer? _notificationTimer;
    private int _notificationPumpPending;
    private int _notificationPumpAgain;
    private bool _notificationCommandsReady;
    private string? _notificationRegistrationWarning;
    private string? _lastNotificationCommand;
    private DateTimeOffset _lastNotificationCommandUtc;
    private readonly object _notificationActivationGate = new();

    private void RegisterNotifications()
    {
        try
        {
            _notifications = new WindowsNotifications(QueueNotificationCommand, message =>
            {
                _notificationRegistrationWarning = message;
                MainWindow?.ShowNotificationStatus(message);
            }, () => _controller?.Settings.Notifications ?? new());
            _notifications.Register();
        }
        catch (Exception)
        { _notificationRegistrationWarning = "Windows notifications are unavailable. Backup and updates will continue."; }
    }

    private void QueueNotificationCommand(NotificationCommand command)
    {
        if (_exiting || _isUiSmoke || _isUiLive) return;
        lock (_notificationActivationGate)
        {
            // The lifecycle and COM event can deliver the same startup invocation.
            var encoded = NotificationCommandCodec.Encode(command);
            var now = DateTimeOffset.UtcNow;
            if (_lastNotificationCommand == encoded && now - _lastNotificationCommandUtc < TimeSpan.FromSeconds(2)) return;
            _lastNotificationCommand = encoded; _lastNotificationCommandUtc = now;
            if (_notificationCommands.Count < 8) _notificationCommands.Enqueue(command);
            _firstNotification.TrySetResult(command);
        }
        DrainNotificationCommands();
    }

    private void DrainNotificationCommands()
    {
        if (!_notificationCommandsReady || MainWindow is not { } window || _exiting) return;
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            while (!_exiting && _notificationCommands.TryDequeue(out var command))
                await RunNotificationCommandAsync(command);
        });
    }

    private async Task RunNotificationCommandAsync(NotificationCommand command)
    {
        if (_controller is null || MainWindow is null || _exiting) return;
        try
        {
            switch (command.Action)
            {
                case NotificationAction.ViewActivity: MainWindow.ShowActivity(); MainWindow.ShowWindow(); break;
                case NotificationAction.ManageBackup: MainWindow.ShowBackupSettings(); break;
                case NotificationAction.OpenFolder:
                    MainWindow.ShowWindow();
                    if (_controller.Settings.IsConfigured) _controller.LaunchFolder();
                    else MainWindow.ShowBackupSettings();
                    break;
                case NotificationAction.RetrySync:
                    MainWindow.ShowActivity(); MainWindow.ShowWindow();
                    if (_controller.Settings.IsConfigured)
                    { await _controller.StartAsync(); await _controller.SyncNowAsync(); }
                    break;
                case NotificationAction.ViewUpdates: MainWindow.ShowUpdates(); break;
                case NotificationAction.DownloadUpdate:
                case NotificationAction.InstallUpdate:
                    MainWindow.ShowUpdates();
                    await _updatesInitialized.Task.WaitAsync(_lifetime.Token);
                    if (!_exiting) await MainWindow.RunNotificationUpdateAsync(command.Action == NotificationAction.InstallUpdate, command.Version!);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { MainWindow?.ShowNotificationStatus("This notification action could not finish. Review the current activity or update page and try again."); }
    }

    private void StartNotificationPolicy()
    {
        if (_notifications is null || _controller is null) return;
        _notificationPolicy = new(_controller.Snapshot, _controller.Activity);
        if (_notificationRegistrationWarning is { } warning) MainWindow?.ShowNotificationStatus(warning);
        _notificationTimer = new Timer(_ => PumpNotifications(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        PumpNotifications();
    }

    private void PumpNotifications()
    {
        if (_exiting || _notificationPolicy is null) return;
        if (Interlocked.Exchange(ref _notificationPumpPending, 1) != 0)
        { Interlocked.Exchange(ref _notificationPumpAgain, 1); return; }
        if (MainWindow is not { } window || !window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!_exiting && _controller is { } controller && _notifications is { } service && _notificationPolicy is { } policy)
                {
                    var preferences = controller.Settings.Notifications;
                    var batch = policy.Observe(controller.Snapshot, controller.Activity, _updates?.Snapshot, preferences, DateTimeOffset.UtcNow, _notificationCanInstallUpdates);
                    await service.PublishAsync(batch);
                    if (!_exiting) _notificationTimer?.Change(policy.HasPending ? TimeSpan.FromSeconds(3) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _notificationPumpPending, 0);
                if (Interlocked.Exchange(ref _notificationPumpAgain, 0) != 0) PumpNotifications();
            }
        })) Interlocked.Exchange(ref _notificationPumpPending, 0);
    }

    private void ReadNotificationLifecycleActivation()
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs arguments &&
                NotificationCommandCodec.TryDecode(arguments.Argument, out var command)) QueueNotificationCommand(command);
        }
        catch (Exception) { /* Registration or lifecycle support must not prevent normal backup startup. */ }
    }

    private void Updates_NotificationChanged(CloudBay.Core.Updates.UpdateSnapshot snapshot) => PumpNotifications();
}

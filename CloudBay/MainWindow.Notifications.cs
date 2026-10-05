using System.Diagnostics;
using CloudBay.Application;
using CloudBay.Core.Notifications;
using CloudBay.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudBay;

public sealed partial class MainWindow
{
    private bool _savingNotifications;

    public void ShowUpdates() { RequestNavigationRoute("settings/about"); ShowWindow(); }
    public void ShowBackupSettings() { RequestNavigationRoute("backup"); ShowWindow(); }

    public async Task RunNotificationUpdateAsync(bool install, string version)
    {
        ShowUpdates();
        if (_updates is null || _installation?.CanInstall != true) return;
        var snapshot = _updates.Snapshot;
        // An old Notification Center button cannot authorize a different release.
        if (snapshot.Candidate?.Version != version || snapshot.State != (install ? UpdateState.Ready : UpdateState.Available)) return;
        await RunUpdateActionAsync(() => install ? _updates.InstallVersionAsync(version) : _updates.DownloadVersionAsync(version));
    }

    public void ShowNotificationStatus(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        NotificationNotice.Message = message;
        NotificationNotice.IsOpen = true;
    });

    private void LoadNotificationPreferences()
    {
        if (_savingNotifications || NotificationsSwitch is null) return;
        var value = DisplaySettings.Notifications;
        NotificationsSwitch.IsOn = value.Enabled;
        NotificationProblemsSwitch.IsOn = value.BackupProblems;
        NotificationFoldersSwitch.IsOn = value.FolderChanges;
        NotificationSyncSwitch.IsOn = value.SyncCompleted;
        NotificationUpdatesSwitch.IsOn = value.Updates;
        NotificationSoundSwitch.IsOn = value.Sound;
        NotificationChoices.IsEnabled = value.Enabled;
    }

    private async void NotificationPreference_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingPreferences || _savingNotifications || _viewModel is null || _viewModel.Preview is not null) return;
        var previous = _controller.Settings.Notifications;
        var value = sender switch
        {
            ToggleSwitch toggle when ReferenceEquals(toggle, NotificationsSwitch) => previous with { Enabled = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, NotificationProblemsSwitch) => previous with { BackupProblems = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, NotificationFoldersSwitch) => previous with { FolderChanges = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, NotificationSyncSwitch) => previous with { SyncCompleted = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, NotificationUpdatesSwitch) => previous with { Updates = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, NotificationSoundSwitch) => previous with { Sound = toggle.IsOn },
            _ => previous
        };
        _savingNotifications = true;
        NotificationSettingsDetail.IsEnabled = false;
        try
        {
            await _controller.UpdatePreferencesAsync(new PreferenceUpdate { Notifications = value });
            NotificationNotice.IsOpen = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowNotificationStatus("The preference could not be saved. " + error.Message); }
        catch (OperationCanceledException) { }
        finally
        {
            _savingNotifications = false;
            if (!_closed)
            {
                _loadingPreferences = true;
                try { LoadNotificationPreferences(); } finally { _loadingPreferences = false; }
                NotificationSettingsDetail.IsEnabled = true;
            }
        }
    }

    private void WindowsNotifications_Click(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ShowNotificationStatus("Windows notification settings could not be opened."); }
    }

    private async Task RunNotificationUiValidationAsync(string output, string suffix)
    {
        var prior = _viewModel.Preview;
        try
        {
            foreach (var enabled in new[] { true, false })
            {
                var preview = prior ?? CloudBay.ViewModels.ClientPreview.Connected();
                _viewModel.SetPreview(preview with { Settings = preview.Settings with
                { Notifications = new NotificationPreferences { Enabled = enabled } } });
                LoadSettings(reloadPreferences: true);
                RequestNavigationRoute("settings/notifications");
                foreach (var width in new[] { 800, 1300 })
                {
                    AppWindow.Resize(new global::Windows.Graphics.SizeInt32(width, 840));
                    SettingsPage.ChangeView(null, 0, null, true);
                    await Task.Delay(180);
                    RootGrid.UpdateLayout();
                    AssertNavigationPresentation("settings/notifications");
                    if (NotificationsSwitch.IsOn != enabled || NotificationChoices.IsEnabled != enabled ||
                        NotificationSyncSwitch.IsOn || NotificationSoundSwitch.IsOn ||
                        NotificationSettingsDetail.ActualWidth <= 0 || NotificationSettingsDetail.ActualWidth > SettingsPage.ActualWidth)
                        throw new InvalidOperationException("Notification controls must show their defaults and fit the page.");
                    await CloudBay.Views.UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output,
                        $"notifications-{(enabled ? "on" : "off")}-{width}{suffix}.png"));
                }
            }
        }
        finally { _viewModel.SetPreview(prior); LoadSettings(reloadPreferences: true); }
    }
}

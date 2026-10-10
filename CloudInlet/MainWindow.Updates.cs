using System.Diagnostics;
using CloudInlet.Core;
using CloudInlet.Core.Updates;
using CloudInlet.ViewModels;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private UpdateCoordinator? _updates;
    private UpdateInstallation? _installation;
    private bool _loadingUpdates;
    private bool _updateAction;
    private int _updateRefreshQueued;

    public void AttachUpdates(UpdateCoordinator updates, UpdateInstallation installation)
    {
        _updates = updates; _installation = installation;
        updates.Changed += Updates_Changed;
        VersionCard.Header = BuildInfo.ProductName;
        VersionCard.Description = $"Version {updates.Identity.Version} · {updates.Identity.BuildFlavor} · {updates.Identity.InstallerKind}";
        if (installation.Notice is { } notice) ShowUpdateInitializationError(notice);
        RefreshUpdates();
    }

    public void ShowUpdateInitializationError(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        UpdateNotice.Message = message; UpdateNotice.Severity = InfoBarSeverity.Warning; UpdateNotice.Visibility = Visibility.Visible; UpdateNotice.IsOpen = true;
    });

    private void Updates_Changed(UpdateSnapshot snapshot)
    {
        if (_closed || Interlocked.Exchange(ref _updateRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref _updateRefreshQueued, 0); if (!_closed) RefreshUpdates(); }))
            Interlocked.Exchange(ref _updateRefreshQueued, 0);
    }

    private void RefreshUpdates()
    {
        if (_updates is null)
        {
            CheckUpdateButton.IsEnabled = DeleteUpdateButton.IsEnabled = false;
            UpdatePreferencesPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var state = _updates.Snapshot;
        var store = _updates.Identity.InstallerKind == UpdateInstallerKind.Store;
        var installs = _installation?.CanInstall == true;
        var working = _updateAction || state.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;
        UpdateStatusText.Text = state.Message;
        UpdateTimingText.Text = string.Join(" · ", new[]
        {
            state.LastCheckedUtc is { } last ? $"Last checked {last.ToLocalTime():g}" : null,
            state.NextCheckUtc is { } next && _updates.Preferences.AutomaticChecks ? $"Next check {next.ToLocalTime():g}" : null
        }.Where(text => text is not null));
        CheckUpdateButton.Content = store ? "Open Microsoft Store" : "Check for updates";
        CheckUpdateButton.IsEnabled = !working;
        UpdatePreferencesPanel.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
        AutoDownloadUpdateCard.Visibility = AutoInstallUpdateCard.Visibility = installs ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.Visibility = installs && state.State == UpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = installs && state.State == UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = !working;
        DeleteUpdateButton.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
        DeleteUpdateButton.IsEnabled = !working;
        UpdateReleaseLink.NavigateUri = new Uri(state.Candidate is { } candidate
            ? $"https://github.com/TahsinFaiyaz30/CloudInlet/releases/tag/{candidate.Tag}" : "https://github.com/TahsinFaiyaz30/CloudInlet/releases");
        UpdateReleaseLink.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
        RefreshUpdateDownloadProgress(state);
        _loadingUpdates = true;
        try
        {
            var preferences = _updates.Preferences;
            AutoCheckUpdateSwitch.IsOn = preferences.AutomaticChecks;
            AutoDownloadUpdateSwitch.IsOn = preferences.AutomaticallyDownload;
            AutoInstallUpdateSwitch.IsOn = preferences.AutomaticallyInstall;
            AutoDownloadUpdateSwitch.IsEnabled = !preferences.AutomaticallyInstall && !working;
            AutoInstallUpdateSwitch.IsEnabled = AutoCheckUpdateSwitch.IsEnabled = !working;
            UpdateIntervalBox.IsEnabled = preferences.AutomaticChecks && !working;
            UpdateIntervalBox.SelectedItem = UpdateIntervalBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                int.TryParse(item.Tag?.ToString(), out var hours) && hours == preferences.CheckIntervalHours);
        }
        finally { _loadingUpdates = false; }
        UpdateSettingsDetailGrids();
    }

    private void RefreshUpdateDownloadProgress(UpdateSnapshot state)
    {
        UpdateDownloadProgress.Visibility = state.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        var progress = ProgressPresentation.ForBytes(state.DownloadedBytes, state.TotalBytes);
        UpdateDownloadProgress.IsIndeterminate = progress.IsIndeterminate;
        UpdateDownloadProgress.Value = progress.Value;
        UpdateDownloadDetail.Visibility = UpdateDownloadProgress.Visibility;
        UpdateDownloadDetail.Text = progress.Label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UpdateDownloadProgress, progress.Label);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (_updates is null) return;
        if (_updates.Identity.InstallerKind == UpdateInstallerKind.Store)
        { await RunUpdateActionAsync(() => { Process.Start(new ProcessStartInfo("ms-windows-store://downloadsandupdates") { UseShellExecute = true }); return Task.CompletedTask; }); }
        else await RunUpdateActionAsync(() => _updates.CheckAsync());
    }
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs args) =>
        await RunUpdateActionAsync(() => _updates!.DownloadAsync());
    private async void InstallUpdate_Click(object sender, RoutedEventArgs args) =>
        await RunUpdateActionAsync(() => _updates!.InstallAsync());
    private async void DeleteUpdate_Click(object sender, RoutedEventArgs args) =>
        await RunUpdateActionAsync(async () =>
        {
            await _updates!.DeleteDownloadsAsync();
            WindowsUpdateInstaller.DeleteInactiveHosts(Path.Combine(BuildInfo.DefaultDataDirectory, "Updates"));
            UpdateNotice.IsOpen = false;
            UpdateNotice.Visibility = Visibility.Collapsed;
        });
    private async void UpdatePreference_Changed(object sender, RoutedEventArgs args) => await SaveUpdatePreferencesAsync();
    private async void UpdateInterval_Changed(object sender, SelectionChangedEventArgs args) => await SaveUpdatePreferencesAsync();

    private async Task SaveUpdatePreferencesAsync()
    {
        if (_loadingUpdates || _updates is null || _updateAction) return;
        var hours = UpdateIntervalBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var selected)
            ? selected : _updates.Preferences.CheckIntervalHours;
        var preferences = new UpdatePreferences(AutoCheckUpdateSwitch.IsOn, AutoDownloadUpdateSwitch.IsOn,
            AutoInstallUpdateSwitch.IsOn, hours);
        await RunUpdateActionAsync(() => _updates.SavePreferencesAsync(preferences));
    }

    private async Task RunUpdateActionAsync(Func<Task> action)
    {
        if (_updateAction || _updates is null) return;
        _updateAction = true; RefreshUpdates();
        try { await action(); }
        catch (OperationCanceledException) { /* Application shutdown cancels active update operations. */ }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { ShowUpdateInitializationError(error.Message); }
        finally { _updateAction = false; if (!_closed) RefreshUpdates(); }
    }
}

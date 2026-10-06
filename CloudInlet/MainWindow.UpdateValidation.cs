using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.Updates;
using CloudInlet.Windows;
using CloudInlet.Views;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace CloudInlet;

public sealed partial class MainWindow
{
    public async Task RunUpdateOnlyUiValidationAsync(string output)
    {
        Directory.CreateDirectory(output);
        RootGrid.RequestedTheme = Environment.GetCommandLineArgs().Contains("--ui-smoke-theme=Light") ? ElementTheme.Light : ElementTheme.Dark;
        await InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(30));
        await RunUpdateUiValidationAsync(output, RootGrid.RequestedTheme == ElementTheme.Light ? "-light" : "");
        await RunNotificationUiValidationAsync(output, RootGrid.RequestedTheme == ElementTheme.Light ? "-light" : "");
    }
    private async Task RunUpdateUiValidationAsync(string output, string suffix)
    {
        var prior = _updates;
        var priorInstallation = _installation;
        var flavor = BuildInfo.Flavor == "Debug" ? UpdateBuildFlavor.Debug : UpdateBuildFlavor.Release;
        var identity = new InstalledUpdateIdentity(BuildInfo.Version, flavor, UpdateInstallerKind.Msi);
        var folder = Path.Combine(output, "update-fixture-" + Guid.NewGuid().ToString("N"));
        var payload = "UI fixture only; this is never executed"u8.ToArray();
        var version = new Version(BuildInfo.Version);
        var target = $"{version.Major}.{version.Minor}.{version.Build + 1}";
        var asset = new UpdateAsset(flavor, UpdateInstallerKind.Msi, "x64",
            $"CloudInlet-{target}-win-x64-{BuildInfo.Flavor.ToLowerInvariant()}-setup.msi", payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        var manifest = new UpdateManifest(2, UpdateManifestRules.Repository, target, "v" + target, [asset]);
        await using var updates = new UpdateCoordinator(identity, folder, handler: new UiUpdateTransport(manifest, payload));
        try
        {
            AttachUpdates(updates, new UpdateInstallation(identity, AppContext.BaseDirectory));
            await updates.CheckAsync(); RefreshUpdates();
            if (DownloadUpdateButton.Visibility != Visibility.Visible || InstallUpdateButton.Visibility != Visibility.Collapsed ||
                AutoInstallUpdateCard.Visibility != Visibility.Visible || !CheckUpdateButton.IsEnabled ||
                !VersionCard.Description.ToString()!.Contains("Msi", StringComparison.Ordinal))
                throw new InvalidOperationException("A matching MSI update must expose download and identify the installed build.");
            await InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(30));
            RequestNavigationRoute("settings/about");
            await Task.Delay(180);
            AssertNavigationPresentation("settings/about");
            foreach (var width in new[] { 800, 1300 })
            {
                AppWindow.Resize(new SizeInt32(width, 840)); SettingsPage.ChangeView(null, 0, null, true);
                await Task.Delay(180); RootGrid.UpdateLayout();
                if (UpdateStatusText.ActualWidth <= 0 || UpdateStatusText.ActualWidth > SettingsPage.ActualWidth || DownloadUpdateButton.ActualWidth <= 0)
                    throw new InvalidOperationException("The update status and action must fit the settings page.");
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"updates-available-{width}{suffix}.png"));
            }
            foreach (var fixture in new[]
            {
                new UpdateSnapshot(UpdateState.Downloading, "Downloading update", DownloadedBytes: 1048576, TotalBytes: 2097152),
                new UpdateSnapshot(UpdateState.Downloading, "Downloading update", DownloadedBytes: 1048576, TotalBytes: 0)
            })
            {
                RefreshUpdateDownloadProgress(fixture);
                UpdateStatusText.Text = fixture.Message;
                DownloadUpdateButton.Visibility = Visibility.Collapsed;
                await Task.Delay(180); RootGrid.UpdateLayout();
                if (UpdateDownloadDetail is not { ActualHeight: > 0, Visibility: Visibility.Visible } ||
                    UpdateDownloadDetail.Text != (fixture.TotalBytes > 0 ? "50% · 1 MiB of 2 MiB" : "1 MiB transferred") ||
                    UpdateDownloadProgress.IsIndeterminate != (fixture.TotalBytes <= 0))
                    throw new InvalidOperationException("Updater progress must visibly show measured percent and both sizes, and stay indeterminate without a total.");
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output,
                    $"updates-downloading-{(fixture.TotalBytes > 0 ? "known" : "unknown")}{suffix}.png"));
            }
            RefreshUpdates();
            await updates.DownloadAsync(); RefreshUpdates();
            if (InstallUpdateButton.Visibility != Visibility.Visible || DownloadUpdateButton.Visibility != Visibility.Collapsed ||
                UpdateDownloadProgress.Visibility != Visibility.Collapsed || !InstallUpdateButton.IsEnabled)
                throw new InvalidOperationException("A verified download must expose Install and restart.");
            await updates.SavePreferencesAsync(new(true, true, true, 6)); RefreshUpdates();
            if (!AutoCheckUpdateSwitch.IsOn || !AutoDownloadUpdateSwitch.IsOn || !AutoInstallUpdateSwitch.IsOn ||
                AutoDownloadUpdateSwitch.IsEnabled || UpdateIntervalBox.SelectedItem is not Microsoft.UI.Xaml.Controls.ComboBoxItem { Tag: "6" })
                throw new InvalidOperationException("Automatic installation must include downloading and show the saved check interval.");
            foreach (var width in new[] { 800, 1300 })
            {
                AppWindow.Resize(new SizeInt32(width, 840)); SettingsPage.ChangeView(null, 0, null, true);
                await Task.Delay(180);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"updates-ready-{width}{suffix}.png"));
                SettingsPage.ChangeView(null, SettingsPage.ScrollableHeight, null, true);
                await Task.Delay(180);
                AutoInstallUpdateSwitch.StartBringIntoView();
                await Task.Delay(180);
                if (AutoInstallUpdateSwitch.ActualWidth <= 0 || !AutoInstallUpdateSwitch.IsOn)
                    throw new InvalidOperationException("Automatic update settings must remain reachable at narrow widths.");
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"updates-options-{width}{suffix}.png"));
            }
            await updates.DeleteDownloadsAsync(); RefreshUpdates();
            if (InstallUpdateButton.Visibility != Visibility.Collapsed || DownloadUpdateButton.Visibility != Visibility.Visible ||
                !DeleteUpdateButton.IsEnabled || Directory.EnumerateFiles(folder, "pending-*").Any())
                throw new InvalidOperationException("Deleting a downloaded update must remove its installer and preserve the manual download action.");
            AppWindow.Resize(new SizeInt32(800, 840)); SettingsPage.ChangeView(null, 0, null, true);
            await Task.Delay(180);
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"updates-deleted-800{suffix}.png"));
        }
        finally
        {
            updates.Changed -= Updates_Changed; _updates = prior; _installation = priorInstallation;
            VersionCard.Description = $"Version {BuildInfo.Version} · {BuildInfo.Flavor}";
            RefreshUpdates();
        }
    }

    private sealed class UiUpdateTransport(UpdateManifest manifest, byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri == UpdateManifestRules.FeedUri
                ? JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateManifestRules.JsonOptions) : payload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content), RequestMessage = request });
        }
    }
}

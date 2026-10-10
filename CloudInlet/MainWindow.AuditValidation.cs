using CloudInlet.Core;
using CloudInlet.Core.Transfers;
using CloudInlet.ViewModels;
using CloudInlet.Views;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private async Task RunAuditRegressionValidationAsync(string outputDirectory, ElementTheme theme)
    {
        var suffix = theme == ElementTheme.Light ? "-light" : "";
        var originalPreview = _viewModel.Preview;
        try
        {
            _viewModel.SetPreview(ClientPreview.Connected());
            Refresh();
            RequestNavigationRoute("settings/sync");
            await InvokeNavigationItemAsync(Navigation.SettingsItem as NavigationViewItem);
            await Task.Delay(120);
            if (CurrentRoute != "settings" || SettingsHub.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Invoking the selected Settings section must return from its detail page.");
            RequestNavigationRoute("settings/account");
            await InvokeNavigationItemAsync(ServicesNavigationItem);
            await Task.Delay(120);
            if (CurrentRoute != "services" || ServicesPage.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Invoking the selected Cloud services section must return from its account page.");

            var folderIcons = ProtectedFolderRows.Children.OfType<Button>()
                .Select(button => button.Content).OfType<StackPanel>()
                .Select(panel => panel.Children[0]).OfType<ImageIcon>().ToArray();
            if (folderIcons.Length == 0)
                throw new InvalidOperationException("The connected fixture must provide folder icons for display-scale validation.");
            foreach (var icon in folderIcons) icon.Source = null;
            _folderIconPixels = 0;
            RefreshFolderIconSources();
            if (folderIcons.Any(icon => icon.Source is null))
                throw new InvalidOperationException("Refreshing display scale must update the ImageIcon sources on protected folder tiles.");

            var oneDriveOnly = ClientPreview.ForState(ClientState.NotConnected) with
            {
                Settings = new AppSettings { Theme = theme.ToString() }, ConnectedOneDriveAccounts = 1
            };
            _viewModel.SetPreview(oneDriveOnly);
            Refresh();
            RequestNavigationRoute("settings/general");
            if (!StartAtSignInBox.IsEnabled || !StartupSettingsCard.Description.ToString()!.StartsWith("Start CloudInlet", StringComparison.Ordinal))
                throw new InvalidOperationException("A connected OneDrive account must enable automatic startup without a B2 account.");
            SetBusy(true, "Checking startup availability");
            if (StartAtSignInBox.IsEnabled)
                throw new InvalidOperationException("Startup must not change during an in-flight settings operation.");
            SetBusy(false, "");
            if (!StartAtSignInBox.IsEnabled)
                throw new InvalidOperationException("OneDrive startup must become available again after the operation.");
            _viewModel.SetPreview(oneDriveOnly with { ConnectedOneDriveAccounts = 0 });
            Refresh();
            if (StartAtSignInBox.IsEnabled)
                throw new InvalidOperationException("Automatic startup requires at least one connected account.");

            var transfer = ClientPreview.TransferQueue();
            foreach (var state in new[] { ClientState.Syncing, ClientState.Paused })
            {
                _viewModel.SetPreview(transfer with
                {
                    Settings = oneDriveOnly.Settings, ConnectedOneDriveAccounts = 1,
                    Snapshot = transfer.Snapshot with { State = state }, Activity = []
                });
                Refresh();
                RequestNavigationRoute("overview");
                RootGrid.UpdateLayout();
                if (ConnectedOverview.Visibility != Visibility.Visible || WelcomePanel.Visibility != Visibility.Collapsed ||
                    DashboardPauseButton.Visibility != Visibility.Visible || !DashboardPauseButton.IsEnabled ||
                    SyncNowButton.Visibility != Visibility.Collapsed || OpenFolderButton.Visibility != Visibility.Collapsed ||
                    (DashboardPauseButton.Content.ToString()!.StartsWith("Resume", StringComparison.Ordinal)) != (state == ClientState.Paused))
                    throw new InvalidOperationException("OneDrive-only transfers must show their Home status and pause/resume without B2-only actions.");
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory,
                    $"home-onedrive-only-{state.ToString().ToLowerInvariant()}{suffix}.png"));
            }

            _viewModel.SetPreview(transfer with
            {
                Settings = oneDriveOnly.Settings, ConnectedOneDriveAccounts = 1, ManualPauseActive = true,
                Snapshot = transfer.Snapshot with { State = ClientState.Attention, Message = "A separate account needs attention" }
            });
            Refresh();
            RequestNavigationRoute("overview");
            if (DashboardPauseButton.Visibility != Visibility.Visible || !DashboardPauseButton.IsEnabled ||
                DashboardPauseButton.Content as string != "Resume syncing")
                throw new InvalidOperationException("An attention diagnostic must not hide global Resume.");
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"home-attention-resume{suffix}.png"));

            // The smoke controller has isolated, empty account storage. Opening
            // this disconnected picker exercises no sign-in or cloud request.
            var destination = LocalTransferEndpoint.ForFolder(@"C:\CloudInlet UI validation\Destination", "This PC · Destination");
            var draft = new CloudTransferDraft(null, new("local", null, destination), 1, 3,
                [new("*.tmp", true), new("cache/**", false)]);
            var picker = new CloudTransferDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this),
                preferOneDrive: true, draft: draft) { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
            var showing = picker.ShowAsync();
            CloudTransferDraft saved;
            try
            {
                await Task.Delay(220);
                picker.UpdateLayout();
                var connect = FindDescendant<Button>(picker, button => ButtonText(button) == "Connect OneDrive")
                    ?? throw new InvalidOperationException("The disconnected source picker must offer its OneDrive connection action.");
                if (picker.IsPrimaryButtonEnabled)
                    throw new InvalidOperationException("A disconnected source must not be accepted as a transfer location.");
                var invoke = FrameworkElementAutomationPeer.CreatePeerForElement(connect)?.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                    ?? throw new InvalidOperationException("The connection action must support accessible invocation.");
                invoke.Invoke();
                await showing.AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                saved = picker.CaptureDraft();
                if (picker.RequestedConnectionProvider != "onedrive" || saved.EditingSide != "source" ||
                    saved.PendingLocation?.Provider != "onedrive" || saved.Destination?.Location != destination ||
                    saved.Operation != 1 || saved.Conflicts != 3 || !saved.Exclusions.SequenceEqual(draft.Exclusions))
                    throw new InvalidOperationException("Opening a connection must preserve the edited side, destination, action, conflicts, and exclusions.");
            }
            finally { picker.Hide(); await showing; }
            var restored = new CloudTransferDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), draft: saved)
            { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
            var restoring = restored.ShowAsync();
            try
            {
                await Task.Delay(220);
                restored.UpdateLayout();
                if (restored.Title as string != "Choose source" || restored.Destination != destination ||
                    FindDescendant<Button>(restored, button => ButtonText(button) == "Connect OneDrive") is null)
                    throw new InvalidOperationException("Returning from connection setup must resume the original picker and retain its destination.");
                await UiSmokeCapture.SaveAsync(restored, Path.Combine(outputDirectory, $"transfer-connection-draft{suffix}.png"));
            }
            finally { restored.Hide(); await restoring; }
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "audit-regressions.txt"),
                $"PASS {theme}: selected-section navigation, OneDrive startup and busy state, display-scale folder icons, OneDrive-only Home pause/resume, accessible picker connection and draft restoration.{Environment.NewLine}");
        }
        finally
        {
            _viewModel.SetPreview(originalPreview);
            SetBusy(false, "");
            Refresh();
        }
    }

    private async Task InvokeNavigationItemAsync(NavigationViewItem? item)
    {
        if (item is null) throw new InvalidOperationException("The navigation item must exist before invocation.");
        Navigation.IsPaneOpen = true;
        await Task.Delay(120);
        Navigation.UpdateLayout();
        // NavigationView exposes SelectionItem (not Invoke). Its Select method
        // raises ItemInvoked even when this section is already selected.
        var selection = FrameworkElementAutomationPeer.CreatePeerForElement(item)?.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
            ?? throw new InvalidOperationException("Navigation items must support accessible selection.");
        selection.Select();
    }

    private static string? ButtonText(Button button) => button.Content is TextBlock text ? text.Text : button.Content as string;
}

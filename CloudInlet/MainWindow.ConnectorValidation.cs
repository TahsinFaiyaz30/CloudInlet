using CloudInlet.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet;

public sealed partial class MainWindow
{
    internal async Task RunConnectorUiValidationAsync(string output)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke") ||
            Manager.Snapshot.Installed.Count != 0 || Manager.Sources.Length == 0 || Manager.Sources.Any(s => !new Uri(s.Location).IsFile))
            throw new InvalidOperationException("Connector UI validation requires fresh isolated storage and a local signed fixture catalog.");
        await InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(30));
        async Task Until(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? stage = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException($"Connector UI did not reach {stage}. {_connectorMessage}");
                await Task.Delay(40);
            }
        }
        static void Invoke(Button button) =>
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        Button? Action(DependencyObject parent, string label) => FindDescendant<Button>(parent, b => ButtonText(b) == label && b.IsEnabled);
        async Task CheckPickerAsync(string side, bool hasB2)
        {
            var dialog = new CloudTransferDialog(_controller, PickerOwner) { XamlRoot = RootGrid.XamlRoot };
            var open = dialog.ShowAsync();
            try
            {
                await Until(() => Action(dialog, "Choose " + side) is not null);
                Invoke(Action(dialog, "Choose " + side)!);
                bool Has(string service) => FindDescendant<Button>(dialog, b =>
                    AutomationProperties.GetName(b).Contains(": " + service + ".", StringComparison.Ordinal)) is not null;
                await Until(() => Has("This PC"));
                if (Has("Backblaze B2") != hasB2 || Has("OneDrive"))
                    throw new InvalidOperationException($"The {side} picker listed a provider inconsistent with the installed packs.");
                if (hasB2 && FindDescendant<Button>(dialog, b => AutomationProperties.GetName(b).EndsWith("Backblaze B2. Not connected", StringComparison.Ordinal)) is null)
                    throw new InvalidOperationException("An installed B2 connector without an account must offer connection.");
            }
            finally { dialog.Hide(); await open; }
        }
        RequestNavigationRoute("overview");
        if (WelcomePanel.Visibility != Visibility.Visible || Action(WelcomePanel, "Open Cloud services") is null)
            throw new InvalidOperationException("Home must offer one provider-neutral way to get started.");
        Invoke(Action(WelcomePanel, "Open Cloud services")!);
        await Until(() => !_connectorBusy && Action(ConnectorRows, "Install") is not null);
        AssertNavigationPresentation("services");
        await CheckPickerAsync("source", false);
        await CheckPickerAsync("destination", false);
        if (AvailableServiceRows.Children.OfType<Border>().Any() ||
            Navigation.MenuItems.OfType<NavigationViewItem>().Any(i => i.Tag as string == "connectors"))
            throw new InvalidOperationException("A fresh profile should have one Cloud services page and no available providers.");
        ConnectorSearch.Text = "Backblaze";
        await Until(() => ConnectorRows.Children.Count == 1 && Action(ConnectorRows, "Install") is not null);
        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, "catalog.png"));
        Invoke(Action(ConnectorRows, "Install")!);
        await Until(() => _activeDialog is { } dialog && Action(dialog, "Install") is not null);
        await UiSmokeCapture.SaveAsync(_activeDialog!, Path.Combine(output, "publisher-review.png"));
        Invoke(Action(_activeDialog!, "Install")!);
        await Until(() => !_connectorBusy && Manager.Snapshot.Installed.ContainsKey("b2"));
        if (Action(AvailableServiceRows, "Connect") is null || AvailableServiceRows.Children.OfType<Border>().Count() != 1)
            throw new InvalidOperationException("Only the installed provider should offer Connect.");
        await CheckPickerAsync("source", true);
        await CheckPickerAsync("destination", true);
        Invoke(Action(AvailableServiceRows, "Disable")!);
        await Until(() => !_connectorBusy && !Manager.Snapshot.Installed["b2"].Enabled);
        RequestNavigationRoute("settings/about");
        await Until(() => FindDescendant<TextBlock>(ConnectorUpdatesPanel, t => t.Text.Contains("Backblaze B2")) is not null);
        ConnectorUpdatesPanel.StartBringIntoView();
        await Task.Delay(250);
        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, "connector-updates.png"));
        RequestNavigationRoute("connectors");
        Invoke(Action(ConnectorRows, "Uninstall")!);
        await Until(() => _activeDialog is { } dialog && Action(dialog, "Uninstall") is not null);
        Invoke(Action(_activeDialog!, "Uninstall")!);
        await Until(() => !_connectorBusy && Manager.Snapshot.Installed.Count == 0);

        var bothMissing = new CloudTransferDraft(new("onedrive", null, null), new("b2", null, null), 0, 0, []);
        var transfer = new CloudTransferDialog(_controller, PickerOwner, draft: bothMissing) { XamlRoot = RootGrid.XamlRoot };
        var choosing = transfer.ShowAsync();
        try
        {
            await Until(() => Action(transfer, "Install required connectors") is not null);
            if (transfer.IsPrimaryButtonEnabled) throw new InvalidOperationException("A transfer with missing endpoints must remain paused.");
            Invoke(Action(transfer, "Install required connectors")!);
            await choosing.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { transfer.Hide(); await choosing; }
        var saved = transfer.CaptureDraft();
        if (transfer.RequestedConnectionProvider is null || saved.Source?.Provider != "onedrive" || saved.Destination?.Provider != "b2")
            throw new InvalidOperationException("The transfer must retain both endpoints while requesting the installation plan.");
        var recovery = InstallRequiredAsync(saved.Source.Provider, saved.Destination.Provider);
        await Until(() => _activeDialog is { } dialog && Action(dialog, "Install") is not null &&
            FindDescendant<TextBlock>(dialog, t => t.Text.StartsWith("Backblaze B2 1.")) is not null &&
            FindDescendant<TextBlock>(dialog, t => t.Text.StartsWith("OneDrive 1.")) is not null);
        await UiSmokeCapture.SaveAsync(_activeDialog!, Path.Combine(output, "required-connectors.png"));
        Invoke(Action(_activeDialog!, "Install")!);
        if (!await recovery || Manager.Issue("b2") is not null || Manager.Issue("onedrive") is not null)
            throw new InvalidOperationException("The reviewed plan must make both transfer connectors available.");
    }
}

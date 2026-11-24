using CloudInlet.Connectors;
using CloudInlet.Core.Connectors;
using CloudInlet.Views;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private bool _connectorBusy, _catalogChecked;
    private string _connectorMessage = "";
    private ConnectorManager Manager => _controller.Connectors;
    private CancellationToken ConnectorToken => _backupUiLifetime.Token;
    private nint PickerOwner => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void OnConnectorProgress(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        ConnectorStatus.Text = _connectorMessage = message;
        if (_settingsRoute == "about") RenderConnectorUpdates();
    });

    private void RefreshConnectorRecovery()
    {
        try { HomeConnectorRecovery.Visibility = _controller.RequiredConnectors().Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        catch (Exception e) { ShowWarning("Connector recovery", e.Message); }
    }
    private async void RequiredConnectors_Click(object sender, RoutedEventArgs args) => await InstallRequiredAsync([]);
    private async Task<bool> InstallRequiredAsync(params string[] ids)
    {
        bool ready = false;
        await RunConnectorActionAsync(async () =>
        {
            var required = _controller.RequiredConnectors(ids);
            if (required.Length == 0) { ready = true; return; }
            ready = await RecoverConnectorsAsync(required);
        });
        return ready;
    }

    private async Task<bool> RecoverConnectorsAsync(string[] ids)
    {
        ConnectorPlan plan;
        try { plan = Manager.Plan(ids); }
        catch (InvalidDataException) when (Manager.Sources.Length > 0)
        {
            await Manager.RefreshAsync(ConnectorToken);
            plan = Manager.Plan(ids);
        }
        return await ReviewInstallAsync(plan);
    }

    private void EnterConnectors()
    {
        RenderConnectors();
        if (_catalogChecked || Manager.Sources.Length == 0) return;
        _catalogChecked = true;
        _ = RunConnectorActionAsync(() => Manager.RefreshAsync(ConnectorToken));
    }
    private void ConnectorSearch_Changed(object sender, TextChangedEventArgs args)
    { if (ConnectorRows is not null && _controller is not null) RenderConnectors(); }

    private Button ConnectorButton(string label, Func<Task> action)
    {
        var button = SourceImportDialog.ActionButton(label);
        button.IsEnabled = !_connectorBusy;
        button.Click += async (_, _) => await RunConnectorActionAsync(action);
        return button;
    }
    private async Task RunConnectorActionAsync(Func<Task> action)
    {
        if (_connectorBusy || _closed) return;
        _connectorBusy = true;
        try
        {
            RenderConnectors(); RenderConnectorUpdates();
            await action();
        }
        catch (OperationCanceledException) when (ConnectorToken.IsCancellationRequested) { }
        catch (Exception e)
        { _connectorMessage = e.GetBaseException().Message; if (!_closed) ShowWarning("Connector needs attention", _connectorMessage); }
        finally
        {
            _connectorBusy = false;
            if (!_closed) { RenderConnectors(); RenderConnectorUpdates(); RefreshConnectorRecovery(); }
        }
    }

    private static readonly (ConnectorCapabilities Capability, string Label)[] ConnectorFeatures =
    [
        (ConnectorCapabilities.Browse, "Browse folders"), (ConnectorCapabilities.Read, "Download"),
        (ConnectorCapabilities.Write, "Upload"), (ConnectorCapabilities.DeleteExactVersion, "Delete verified file versions"),
        (ConnectorCapabilities.Verify, "Verify transfers"), (ConnectorCapabilities.ResumableUpload, "Resume uploads"),
        (ConnectorCapabilities.ServerCopy, "Server-side copy"), (ConnectorCapabilities.FolderBackup, "Folder backup"),
        (ConnectorCapabilities.ObjectKeys, "Object keys"), (ConnectorCapabilities.HierarchicalFolders, "Folder hierarchy"),
        (ConnectorCapabilities.Checksums, "Checksums"), (ConnectorCapabilities.AccountSetup, "Connect accounts")
    ];

    private void RenderConnectors()
    {
        ConnectorRows.Children.Clear(); ConnectorActions.Children.Clear(); ConnectorStatus.Text = _connectorMessage;
        var state = Manager.Snapshot;
        var offers = Manager.Offers;
        ConnectorActions.Children.Add(ConnectorButton("Refresh catalog", () => Manager.RefreshAsync(ConnectorToken)));
        ConnectorActions.Children.Add(ConnectorButton("Import packages…", ImportConnectorsAsync));
        ConnectorActions.Children.Add(ConnectorButton("Add source…", AddConnectorSourceAsync));
        var required = _controller.RequiredConnectors();
        if (required.Length > 0)
            ConnectorActions.Children.Add(ConnectorButton("Install required connectors", async () => await RecoverConnectorsAsync(required)));
        var ids = new[] { "b2", "onedrive" }.Concat(state.Installed.Keys).Concat(offers.Select(o => o.Manifest.Id)).Distinct();
        foreach (var id in ids)
        {
            var installed = state.Installed.GetValueOrDefault(id);
            var available = offers.Where(o => o.Manifest.Id == id &&
                (installed is null || o.Source == installed.Current.Source && o.Manifest.Fingerprint == installed.Current.Manifest.Fingerprint))
                .OrderByDescending(o => ConnectorFormat.VersionOf(o.Manifest.Version)).ToArray();
            var candidate = available.FirstOrDefault();
            var m = candidate?.Manifest ?? installed?.Current.Manifest;
            var name = m?.Name ?? (id == "b2" ? "Backblaze B2" : "OneDrive");
            var features = m is null ? (id == "b2" ? "Object storage, folder backup, verified transfers" : "Drive browsing, verified transfers") :
                string.Join(" · ", ConnectorFeatures.Where(f => m.Capabilities.HasFlag(f.Capability)).Select(f => f.Label));
            if (!(name + " " + id + " " + features + " " + m?.Publisher).Contains(ConnectorSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(SourceImportDialog.Heading(name));
            panel.Children.Add(SourceImportDialog.Text(features, true));
            var source = installed?.Current.Source ?? candidate?.Source;
            panel.Children.Add(SourceImportDialog.Text($"{m?.Publisher ?? "CloudInlet"} · {source ?? "Catalog unavailable"}", true));
            if (m is not null) panel.Children.Add(SourceImportDialog.Text($"SDK [{m.Sdk.Min}, {m.Sdk.Max}) · {string.Join(", ", m.Platforms)} {string.Join(", ", m.Architectures)}", true));
            if (installed is not null || candidate is not null)
                panel.Children.Add(SourceImportDialog.Text($"Installed {installed?.Current.Manifest.Version ?? "—"} · Latest {candidate?.Manifest.Version ?? installed?.Current.Manifest.Version}"));
            var issue = installed is null ? m?.CompatibilityIssue : Manager.Issue(id);
            if (candidate is not null && installed is null)
            {
                try { _ = Manager.Plan([id]); }
                catch (Exception e) { issue = e.Message; }
            }
            if (issue is not null) panel.Children.Add(SourceImportDialog.Text(issue));
            if (Manager.NeedsRestart(id)) panel.Children.Add(SourceImportDialog.Text("Restart CloudInlet to activate this change."));
            var actions = new ActionWrapPanel();
            if (installed is null && candidate is not null && issue is null)
                actions.Children.Add(ConnectorButton("Install", async () => await ReviewInstallAsync(Manager.Plan([id]))));
            else if (installed is null && candidate is null)
                panel.Children.Add(SourceImportDialog.Text("Refresh the configured publisher catalog to install this connector.", true));
            if (installed is not null)
            {
                if (installed.Enabled && installed.Current.Manifest.Capabilities.HasFlag(CloudInlet.Connectors.ConnectorCapabilities.AccountSetup))
                    actions.Children.Add(ConnectorButton("Connect account", async () => await ConnectCustomAsync(id)));
                actions.Children.Add(ConnectorButton(installed.Enabled ? "Disable" : "Enable", async () =>
                {
                    if (installed.Enabled) _controller.ChangeConnector(id, installed.Enabled ? "disable" : "enable");
                    else await ReviewInstallAsync(Manager.Plan([id]));
                }));
                if (issue is not null && installed.Enabled)
                    actions.Children.Add(ConnectorButton("Repair", async () => await ReviewInstallAsync(Manager.Plan([id]))));
                if (installed.Enabled && candidate is not null && ConnectorFormat.VersionOf(candidate.Manifest.Version) > ConnectorFormat.VersionOf(installed.Current.Manifest.Version))
                    actions.Children.Add(ConnectorButton("Update", async () => await ReviewInstallAsync(Manager.Plan([id], true))));
                if (installed.Previous is not null) actions.Children.Add(ConnectorButton("Roll back", () => { _controller.ChangeConnector(id, "rollback"); return Task.CompletedTask; }));
                actions.Children.Add(ConnectorButton("Uninstall", async () =>
                {
                    if (await ConfirmConnectorAsync($"Uninstall {name}?", SourceImportDialog.Text("Remove this connector's code. CloudInlet retains its accounts, settings and transfer checkpoints."), "Uninstall"))
                        _controller.ChangeConnector(id, "uninstall");
                }));
            }
            panel.Children.Add(actions);
            ConnectorRows.Children.Add(SourceImportDialog.Surface(panel));
        }
        if (ConnectorRows.Children.Count == 0) ConnectorRows.Children.Add(SourceImportDialog.Text("No matching connectors."));
        if (string.IsNullOrWhiteSpace(ConnectorSearch.Text) && state.Sources.Count > 0)
        {
            ConnectorRows.Children.Add(SourceImportDialog.Heading("Subscribed sources"));
            foreach (var source in state.Sources)
            {
                var details = new StackPanel { Spacing = 6 };
                details.Children.Add(SourceImportDialog.Text(source.Location));
                details.Children.Add(SourceImportDialog.Text(ConnectorFormat.Fingerprint(source.PublicKey), true));
                details.Children.Add(ConnectorButton("Remove source", () => { Manager.RemoveSource(source.Location); return Task.CompletedTask; }));
                ConnectorRows.Children.Add(SourceImportDialog.Surface(details));
            }
        }
    }

    private async Task<bool> ConfirmConnectorAsync(string title, UIElement content, string primary)
    {
        var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { Content = content, MaxHeight = 410 },
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        return await ShowModalAsync(dialog) == ContentDialogResult.Primary;
    }
    private async Task<bool> ReviewInstallAsync(ConnectorPlan plan)
    {
        var dialog = new ConnectorInstallDialog(plan) { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        if (await ShowModalAsync(dialog) != ContentDialogResult.Primary) return false;
        var selected = dialog.SelectedPlan;
        await Task.Run(() => Manager.InstallAsync(selected, ConnectorToken), ConnectorToken);
        await _controller.StartAsync();
        return true;
    }
    private async Task<bool> ConnectCustomAsync(string id)
    {
        var setup = (CloudInlet.Connectors.IConnectorAccountSetup)Manager.Provider(id);
        var panel = new StackPanel { Spacing = 10 };
        var fields = new Dictionary<string, Func<string>>();
        foreach (var field in setup.Fields)
        {
            if (field.Secret)
            {
                var password = new PasswordBox { Header = field.Label };
                panel.Children.Add(password); fields.Add(field.Id, () => password.Password);
            }
            else
            {
                var text = new TextBox { Header = field.Label };
                panel.Children.Add(text); fields.Add(field.Id, () => text.Text);
            }
        }
        if (!await ConfirmConnectorAsync("Connect " + Manager.Snapshot.Installed[id].Current.Manifest.Name, panel, "Connect")) return false;
        await _controller.ConnectCustomAccountAsync(id, fields.ToDictionary(f => f.Key, f => f.Value()), ConnectorToken);
        return true;
    }
    private async Task ImportConnectorsAsync()
    {
        var files = await DesktopPickers.PickFilesAsync(PickerOwner, "Review packages", [".cipack"], ConnectorToken);
        if (files.Count == 0) return;
        var imports = await Task.Run(() => files.Select(ConnectorManager.InspectLocal).ToArray(), ConnectorToken);
        await ReviewInstallAsync(Manager.Plan(imports.Select(o => o.Manifest.Id), true, imports));
    }
    private async Task AddConnectorSourceAsync()
    {
        var address = new TextBox { Header = "Signed catalog URL or local catalog file", PlaceholderText = "https://…/catalog.json" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(address);
        var choose = SourceImportDialog.ActionButton("Choose catalog file…");
        choose.Click += async (_, _) =>
        {
            try { var path = await DesktopPickers.PickFileAsync(PickerOwner, "Choose catalog", [".json"], ConnectorToken);
                if (path is not null) address.Text = new Uri(path).AbsoluteUri; }
            catch (Exception e) { _connectorMessage = e.Message; }
        };
        panel.Children.Add(choose);
        if (!await ConfirmConnectorAsync("Add connector source", panel, "Inspect publisher")) return;
        var (source, offers) = await ConnectorCatalogReader.ReadAsync(address.Text.Trim(), null, ConnectorToken);
        var publishers = string.Join(", ", offers.Select(o => o.Manifest.Publisher).Distinct());
        if (await ConfirmConnectorAsync("Trust this catalog publisher?", SourceImportDialog.Text(
            $"{publishers}\n{source.Location}\nSHA-256 publisher fingerprint {ConnectorFormat.Fingerprint(source.PublicKey)}\nVerify this fingerprint with the publisher before trusting future signed updates."), "Trust source"))
            await Manager.SubscribeAsync(source, ConnectorToken);
    }
    private void RenderConnectorUpdates()
    {
        ConnectorUpdatesPanel.Children.Clear();
        ConnectorUpdatesPanel.Children.Add(SourceImportDialog.Heading("Connector updates"));
        var state = Manager.Snapshot;
        var actions = new ActionWrapPanel();
        actions.Children.Add(ConnectorButton("Check connectors", () => Manager.RefreshAsync(ConnectorToken)));
        var updates = _controller.AvailableConnectorUpdates(false);
        if (updates.Length > 0) actions.Children.Add(ConnectorButton("Update all compatible", async () => await ReviewInstallAsync(Manager.Plan(updates, true))));
        var automatic = new CheckBox { Content = "Automatically update compatible connectors", IsChecked = state.Automatic, IsEnabled = !_connectorBusy };
        automatic.Click += async (_, _) => await RunConnectorActionAsync(() => { Manager.SetAutomatic(automatic.IsChecked == true); return Task.CompletedTask; });
        ConnectorUpdatesPanel.Children.Add(actions); ConnectorUpdatesPanel.Children.Add(automatic);
        if (_connectorMessage.Length > 0) ConnectorUpdatesPanel.Children.Add(SourceImportDialog.Text(_connectorMessage));
        foreach (var (id, installed) in state.Installed)
        {
            var m = installed.Current.Manifest;
            var latest = Manager.Offers.Where(o => o.Manifest.Id == id && o.Source == installed.Current.Source && o.Manifest.PublicKey == m.PublicKey)
                .OrderByDescending(o => ConnectorFormat.VersionOf(o.Manifest.Version)).FirstOrDefault()?.Manifest;
            ConnectorUpdatesPanel.Children.Add(SourceImportDialog.Text($"{m.Name} · Installed {m.Version} · Latest {latest?.Version ?? (installed.Current.Source == ConnectorManager.Local ? "local import" : "check catalog")}"));
            var release = latest ?? m;
            ConnectorUpdatesPanel.Children.Add(SourceImportDialog.Text(release.ReleaseNotes + " " + release.CompatibilityIssue, true));
            if (Manager.NeedsRestart(id)) ConnectorUpdatesPanel.Children.Add(SourceImportDialog.Text("Restart required to activate this version."));
            if (updates.Contains(id)) ConnectorUpdatesPanel.Children.Add(ConnectorButton("Update " + m.Name, async () => await ReviewInstallAsync(Manager.Plan([id], true))));
            if (installed.Current.Source == ConnectorManager.Local) continue;
            var preference = new CheckBox { Content = "Automatically update " + m.Name, IsChecked = installed.Automatic, IsEnabled = !_connectorBusy };
            preference.Click += async (_, _) => await RunConnectorActionAsync(() => { Manager.Change(id, "automatic"); return Task.CompletedTask; });
            ConnectorUpdatesPanel.Children.Add(preference);
        }
    }
}

using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

internal sealed record CloudLocationSelection(string Provider, string? AccountId, TransferLocation? Location);

/// <summary>An independent, searchable service/account and folder picker.</summary>
internal sealed class CloudLocationPicker : IDisposable
{
    private sealed record Choice(string Provider, string? AccountId, string Name, string Detail, bool Connected)
    {
        public override string ToString() => Name;
    }
    private readonly ClientController _controller;
    private readonly nint _owner;
    private readonly string _heading;
    private readonly Action _changed;
    private readonly Action<Exception> _error;
    private readonly Action<string> _connect;
    private readonly CancellationTokenSource _lifetime;
    private readonly StackPanel _view = new() { Spacing = 12 };
    private readonly ContentControl _interaction = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _search = new() { PlaceholderText = "Search services or accounts", MinHeight = 36 };
    private readonly StackPanel _choices = new() { Spacing = 8 };
    private readonly ComboBox _container = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name", MinHeight = 36 };
    private readonly TextBox _folderSearch = new() { PlaceholderText = "Search loaded folders", MinHeight = 36 };
    private readonly StackPanel _folders = new() { Spacing = 4 };
    private readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer _folderScroll;
    private readonly TextBlock _path = SourceImportDialog.Text("", true);
    private readonly TextBlock _empty = SourceImportDialog.Text("No folders here. You can use this location.", true);
    private readonly Button _up = SourceImportDialog.ActionButton("Up one folder");
    private readonly Button _more = SourceImportDialog.ActionButton("Load more folders");
    private readonly Stack<TransferLocation> _parents = new();
    private readonly List<(string Id, string Path, string Name)> _loadedFolders = [];
    private Choice? _choice;
    private string? _cursor;
    private bool _loadingControls, _disposed, _lifetimeDisposed;
    private int _pending;
    internal Border Surface { get; }
    internal TransferLocation? Location { get; private set; }
    internal bool IsWorking => _pending > 0;
    internal CloudLocationSelection? Selection => _choice is null ? null : new(_choice.Provider, _choice.AccountId, Location);

    internal CloudLocationPicker(ClientController controller, nint owner, string heading, CancellationToken token,
        Action changed, Action<Exception> error, Action<string> connect)
    {
        _controller = controller; _owner = owner; _heading = heading; _changed = changed; _error = error; _connect = connect;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _interaction.Content = _view;
        Surface = SourceImportDialog.Surface(_interaction);
        _navigation.Children.Add(_up); _navigation.Children.Add(_more);
        _folderScroll = new ScrollViewer { Content = _folders, MaxHeight = 220, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(_search, "Search " + heading.ToLowerInvariant() + " services and accounts");
        AutomationProperties.SetName(_folderSearch, "Search loaded " + heading.ToLowerInvariant() + " folders");
        _search.TextChanged += (_, _) => RenderChoices();
        _folderSearch.TextChanged += (_, _) => RenderFolders();
        _container.SelectionChanged += async (_, _) => { if (!_loadingControls) await RunAsync(() => SelectContainerAsync()); };
        _up.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_parents.TryPeek(out var parent)) { await BrowseAsync(parent); _parents.Pop(); _up.IsEnabled = _parents.Count > 0; }
        });
        _more.Click += async (_, _) => await RunAsync(async () => { if (Location is { } location) await BrowseAsync(location, true); });
        ShowChoices();
    }

    internal async Task InitializeAsync(TransferLocation? fixedLocation, CloudLocationSelection? selected)
    {
        if (fixedLocation is not null)
        {
            _choice = new(fixedLocation.Provider, fixedLocation.AccountId, fixedLocation.DisplayName, "", true);
            Location = fixedLocation;
            _view.Children.Clear(); _view.Children.Add(SourceImportDialog.Heading(_heading));
            _view.Children.Add(SourceImportDialog.Text(fixedLocation.DisplayName));
            _view.Children.Add(SourceImportDialog.Text(fixedLocation.Path.Length == 0 ? "Root folder" : fixedLocation.Path, true));
            _changed(); return;
        }
        if (selected is null) { ShowChoices(); return; }
        var choice = Choices().FirstOrDefault(item => item.Provider == selected.Provider &&
            (string.IsNullOrEmpty(selected.AccountId) || item.AccountId == selected.AccountId));
        if (choice is not null) await RunAsync(() => SelectAsync(choice, selected.Location));
        else ShowChoices();
    }

    private IEnumerable<Choice> Choices()
    {
        yield return new("local", null, "This PC", "Choose a local folder", true);
        yield return new("b2", _controller.Settings.IsConfigured ? _controller.Settings.AccountId : null, "Backblaze B2",
            _controller.Settings.IsConfigured ? _controller.Settings.BucketName : "Not connected", _controller.Settings.IsConfigured);
        if (_controller.OneDriveAccounts.Count == 0)
            yield return new("onedrive", null, "OneDrive", "Not connected", false);
        else
            foreach (var account in _controller.OneDriveAccounts)
                yield return new("onedrive", account.Id, "OneDrive · " + account.Name, "Connected account", true);
    }

    private void ShowChoices()
    {
        Location = null; _choice = null;
        _view.Children.Clear();
        _view.Children.Add(SourceImportDialog.Heading("Services and accounts"));
        _view.Children.Add(_search); _view.Children.Add(_choices);
        RenderChoices(); _changed();
    }

    private void RenderChoices()
    {
        _choices.Children.Clear();
        var words = _search.Text.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var choice in Choices().Where(item => words.All(word => (item.Name + " " + item.Detail).Contains(word, StringComparison.OrdinalIgnoreCase))))
        {
            var copy = new StackPanel { Spacing = 3 };
            copy.Children.Add(SourceImportDialog.Text(choice.Name));
            copy.Children.Add(SourceImportDialog.Text(choice.Detail, true));
            var button = new Button { Content = copy, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), MinHeight = 64 };
            AutomationProperties.SetName(button, $"{_heading}: {choice.Name}. {choice.Detail}");
            button.Click += async (_, _) => await RunAsync(() => SelectAsync(choice));
            _choices.Children.Add(button);
        }
        if (_choices.Children.Count == 0) _choices.Children.Add(SourceImportDialog.Text("No matching services or accounts.", true));
    }

    private async Task SelectAsync(Choice choice, TransferLocation? restore = null)
    {
        _choice = choice; Location = null; _parents.Clear(); _loadedFolders.Clear();
        _cursor = null; _folderSearch.Text = "";
        _view.Children.Clear();
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(SourceImportDialog.Heading(_heading));
        var change = SourceImportDialog.ActionButton("Change");
        AutomationProperties.SetName(change, "Change " + _heading.ToLowerInvariant());
        change.Click += (_, _) => { if (!IsWorking) ShowChoices(); };
        Grid.SetColumn(change, 1); heading.Children.Add(change); _view.Children.Add(heading);
        _view.Children.Add(SourceImportDialog.Text(choice.Name));
        if (!choice.Connected)
        {
            _view.Children.Add(SourceImportDialog.Text("Connect this service to choose an account and folder.", true));
            var connect = SourceImportDialog.ActionButton("Connect " + choice.Name);
            connect.Click += (_, _) => _connect(choice.Provider);
            _view.Children.Add(connect); return;
        }
        if (choice.Provider == "local")
        {
            Location = restore;
            _path.Text = restore?.Path ?? "Choose a folder on this PC.";
            _view.Children.Add(_path);
            var browse = SourceImportDialog.ActionButton("Choose folder…");
            browse.Click += async (_, _) => await RunAsync(async () =>
            {
                var path = await DesktopPickers.PickFolderAsync(_owner, "Choose transfer " + _heading.ToLowerInvariant(), _lifetime.Token);
                if (path is not null && !_lifetime.IsCancellationRequested)
                { Location = LocalTransferEndpoint.ForFolder(path, "This PC · " + Path.GetFileName(path)); _path.Text = path; }
            });
            _view.Children.Add(browse); return;
        }
        _container.Header = choice.Provider == "onedrive" ? "Drive" : "Bucket";
        _container.PlaceholderText = choice.Provider == "onedrive" ? "Choose a drive" : "Choose a bucket";
        AutomationProperties.SetName(_container, _heading + " " + _container.Header.ToString()!.ToLowerInvariant());
        _view.Children.Add(_container); _view.Children.Add(_path); _view.Children.Add(_folderSearch);
        _view.Children.Add(_navigation); _view.Children.Add(_folderScroll);
        _view.Children.Add(_empty);
        if (choice.Provider == "onedrive")
        {
            var another = SourceImportDialog.ActionButton("Connect another account");
            another.Click += (_, _) => _connect("onedrive"); _view.Children.Add(another);
        }
        _path.Text = "Choose a " + (choice.Provider == "onedrive" ? "drive" : "bucket") + " to browse folders.";
        _up.IsEnabled = false; _more.Visibility = Visibility.Collapsed;
        _loadingControls = true;
        try
        {
            _container.ItemsSource = null;
            if (choice.Provider == "onedrive")
            {
                var drives = await _controller.GetOneDriveDrivesAsync(choice.AccountId!, _lifetime.Token);
                if (_disposed) return;
                _container.ItemsSource = drives;
                _container.SelectedItem = drives.FirstOrDefault(drive => drive.Id == restore?.ContainerId);
            }
            else
            {
                var buckets = await _controller.GetImportBucketsAsync(_lifetime.Token);
                if (_disposed) return;
                _container.ItemsSource = buckets;
                _container.SelectedItem = buckets.FirstOrDefault(bucket => bucket.Id == restore?.ContainerId);
            }
            if (_container.SelectedItem is null && _container.Items.Count == 1) _container.SelectedIndex = 0;
            _container.IsEnabled = _container.Items.Count > 0;
        }
        finally { _loadingControls = false; }
        await SelectContainerAsync(restore);
    }

    private async Task SelectContainerAsync(TransferLocation? restore = null)
    {
        Location = null; _parents.Clear(); _loadedFolders.Clear(); _folderSearch.Text = ""; _cursor = null;
        _up.IsEnabled = false; _more.Visibility = Visibility.Collapsed; _changed(); RenderFolders();
        TransferLocation? root = null;
        if (_choice?.Provider == "onedrive" && _container.SelectedItem is OneDriveDrive drive)
            root = await _controller.GetOneDriveRootAsync(_choice.AccountId!, drive.Id, _lifetime.Token);
        else if (_choice?.Provider == "b2" && _container.SelectedItem is CloudBucket bucket)
            root = _controller.GetB2TransferRoot(bucket.Id, bucket.Name);
        if (root is null) return;
        if (restore is not null && restore.ContainerId == root.ContainerId && restore.AccountId == root.AccountId)
        {
            if (restore.Path != root.Path) _parents.Push(root);
            await BrowseAsync(restore);
        }
        else await BrowseAsync(root);
    }

    private async Task BrowseAsync(TransferLocation location, bool append = false)
    {
        var previous = Location;
        Location = null; _changed();
        try
        {
            var page = await _controller.BrowseTransferFoldersAsync(location, append ? _cursor : null, _lifetime.Token);
            if (_disposed) return;
            Location = location; _path.Text = location.Path.Length == 0 ? "Root folder" : location.Path;
            if (!append) { _loadedFolders.Clear(); _folderSearch.Text = ""; }
            foreach (var folder in page.Folders) _loadedFolders.Add((folder.Id, folder.Path, folder.Name));
            _cursor = page.NextCursor;
            _more.Visibility = _cursor is null ? Visibility.Collapsed : Visibility.Visible;
            _up.IsEnabled = _parents.Count > 0;
            RenderFolders();
        }
        catch { Location = previous; throw; }
    }

    private void RenderFolders()
    {
        _folders.Children.Clear();
        foreach (var folder in _loadedFolders.Where(folder => folder.Name.Contains(_folderSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var button = SourceImportDialog.ActionButton(folder.Name);
            button.HorizontalAlignment = button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(button, _heading + " folder " + folder.Name);
            button.Click += async (_, _) => await RunAsync(async () =>
            {
                if (Location is not { } parent) return;
                await BrowseAsync(parent with { FolderId = folder.Id, Path = folder.Path });
                _parents.Push(parent); _up.IsEnabled = true;
            });
            _folders.Children.Add(button);
        }
        _empty.Text = Location is null ? "Choose a location to see its folders." : _folderSearch.Text.Length > 0
            ? "No matching loaded folders. Load more folders or change your search." : "No subfolders here. You can use this location.";
        _empty.Visibility = _folders.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_disposed || IsWorking) return;
        _pending++; _interaction.IsEnabled = false; _changed();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed) _error(error); }
        finally { _pending--; if (!_disposed) _interaction.IsEnabled = true; _changed(); DisposeWhenIdle(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); DisposeWhenIdle(); }
    private void DisposeWhenIdle() { if (_disposed && _pending == 0 && !_lifetimeDisposed) { _lifetimeDisposed = true; _lifetime.Dispose(); } }
}

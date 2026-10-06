using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.OneDrive;
using CloudBay.Core.Transfers;
using CloudBay.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CloudBay.Views;

/// <summary>Chooses real provider locations; a cloud destination never becomes a Windows path.</summary>
internal sealed class CloudTransferDialog : ContentDialog
{
    private readonly ClientController _controller;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly ComboBox _direction = new() { Header = "Direction", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _operation = new() { Header = "Action", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _conflicts = new() { Header = "If the destination file exists", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _exclusions = new() { Header = "Exclude names or paths (one pattern per line)", AcceptsReturn = true, MinHeight = 70,
        PlaceholderText = "For example, *.tmp or Archive/*", TextWrapping = TextWrapping.Wrap };
    private readonly CloudFolderSelector _source, _destination;
    private readonly TransferLocation? _fixedSource, _fixedDestination;
    private static readonly (string Source, string Destination)[] Directions = [("onedrive", "b2"), ("b2", "onedrive"),
        ("local", "b2"), ("b2", "local"), ("local", "onedrive"), ("onedrive", "local")];
    private bool _review, _working, _closed, _signInPresentation;
    private int _pending;
    internal TransferLocation? Source => _source.Location;
    internal TransferLocation? Destination => _destination.Location;
    internal TransferOperation Operation => _operation.SelectedIndex == 1 ? TransferOperation.Move : TransferOperation.Copy;
    internal TransferConflictPolicy Conflicts => (TransferConflictPolicy)Math.Max(0, _conflicts.SelectedIndex);
    internal IReadOnlyList<string> Exclusions => _exclusions.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal CloudTransferDialog(ClientController controller, nint owner, TransferLocation? source = null, TransferLocation? destination = null)
    {
        _controller = controller; _fixedSource = source; _fixedDestination = destination;
        Title = "Transfer between clouds"; PrimaryButtonText = "Review transfer"; CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.None; Resources["ContentDialogMaxWidth"] = 760d;
        _direction.Items.Add("OneDrive → Backblaze B2"); _direction.Items.Add("Backblaze B2 → OneDrive");
        _direction.Items.Add("This PC → Backblaze B2"); _direction.Items.Add("Backblaze B2 → This PC");
        _direction.Items.Add("This PC → OneDrive"); _direction.Items.Add("OneDrive → This PC");
        var fixedDirection = Array.FindIndex(Directions, direction =>
            (source is null || source.Provider == direction.Source) &&
            (destination is null || destination.Provider == direction.Destination));
        _direction.SelectedIndex = Math.Max(0, fixedDirection);
        _direction.IsEnabled = source is null && destination is null;
        _operation.Items.Add("Copy — keep originals"); _operation.Items.Add("Move — remove verified, unchanged originals"); _operation.SelectedIndex = 0;
        _conflicts.Items.Add("Stop and ask for review"); _conflicts.Items.Add("Skip existing files");
        _conflicts.Items.Add("Replace destination files"); _conflicts.Items.Add("Keep both with a different name"); _conflicts.SelectedIndex = 0;
        _exclusions.Text = string.Join(Environment.NewLine, controller.Settings.Exclusions);
        _source = new CloudFolderSelector(controller, owner, "Source", _lifetime.Token, ShowError, SelectionChanged);
        _destination = new CloudFolderSelector(controller, owner, "Destination", _lifetime.Token, ShowError, SelectionChanged);
        Content = new ScrollViewer { Content = _body, MaxHeight = 620, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _direction.SelectionChanged += async (_, _) => await ConfigureLocationsAsync();
        PrimaryButtonClick += Primary_Click;
        SecondaryButtonClick += (_, args) => { args.Cancel = true; _review = false; RenderChoices(); };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); DisposeWhenIdle(); };
        Loaded += async (_, _) => { if (!_signInPresentation && _source.Location is null) await ConfigureLocationsAsync(); };
        RenderChoices();
    }

    private void RenderChoices()
    {
        Title = "Transfer between clouds"; PrimaryButtonText = "Review transfer"; SecondaryButtonText = "";
        _body.Children.Clear();
        _body.Children.Add(SourceImportDialog.Text("CloudBay runs the transfer through this PC. Between clouds, file contents use bounded memory. A folder on this PC is used only when you explicitly select it as a source or destination.", true));
        _body.Children.Add(_direction); _body.Children.Add(_source.View); _body.Children.Add(_destination.View);
        _body.Children.Add(_operation); _body.Children.Add(_conflicts); _body.Children.Add(_exclusions); _body.Children.Add(_error);
        SelectionChanged();
    }

    private async Task ConfigureLocationsAsync()
    {
        if (_closed || _working) return;
        _working = true; _pending++; IsPrimaryButtonEnabled = false;
        _direction.IsEnabled = false;
        try
        {
            var direction = Directions[Math.Max(0, _direction.SelectedIndex)];
            await _source.ConfigureAsync(direction.Source, _fixedSource);
            await _destination.ConfigureAsync(direction.Destination, _fixedDestination);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
        finally { _working = false; _direction.IsEnabled = _fixedSource is null && _fixedDestination is null; _pending--; DisposeWhenIdle(); SelectionChanged(); }
    }

    private void SelectionChanged()
    {
        var working = _working || _source.IsWorking || _destination.IsWorking;
        IsPrimaryButtonEnabled = !working && Source is not null && Destination is not null;
        _direction.IsEnabled = !working && _fixedSource is null && _fixedDestination is null;
    }
    private void ShowError(Exception error) { if (_closed) return; _error.Message = error.Message; _error.IsOpen = true; }
    private void DisposeWhenIdle() { if (_closed && _pending == 0) { _source.Dispose(); _destination.Dispose(); _lifetime.Dispose(); } }
    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_working || Source is null || Destination is null) { args.Cancel = true; return; }
        if (_review) return;
        args.Cancel = true;
        RenderReview();
    }
    private void RenderReview()
    {
        if (Source is null || Destination is null) return;
        _review = true; Title = "Review cloud transfer"; PrimaryButtonText = "Start " + Operation.ToString().ToLowerInvariant(); SecondaryButtonText = "Back";
        _body.Children.Clear();
        _body.Children.Add(SourceImportDialog.PathCard("Source", Source.DisplayName, Source.Path.Length == 0 ? "Root folder" : Source.Path));
        _body.Children.Add(SourceImportDialog.PathCard("Destination", Destination.DisplayName, Destination.Path.Length == 0 ? "Root folder" : Destination.Path));
        _body.Children.Add(SourceImportDialog.Text(Operation == TransferOperation.Move
            ? "Each destination is verified before CloudBay deletes the exact, unchanged source file. Changed files stay at the source and need review."
            : "CloudBay verifies each destination copy. Original source files stay in the selected folder.", true));
        _body.Children.Add(SourceImportDialog.Text("Conflicts: " + _conflicts.SelectedItem + Environment.NewLine +
            (Exclusions.Count == 0 ? "No exclusions" : "Exclusions: " + string.Join(", ", Exclusions)), true));
        _body.Children.Add(SourceImportDialog.Text("Activity shows discovery, the queue, transferred and remaining bytes, speeds, and pause, resume, or cancel controls. Completed work and checkpoints survive an interruption.", true));
        _body.Children.Add(_error);
    }
    internal async Task ShowReviewPresentationAsync()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) throw new InvalidOperationException("Cloud transfer presentation requires isolated UI validation.");
        await ConfigureLocationsAsync();
        RenderReview();
    }
    internal void ShowSignInPresentation(bool advanced = false)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) throw new InvalidOperationException("Sign-in presentation requires isolated UI validation.");
        _signInPresentation = true;
        Title = "Connect OneDrive"; PrimaryButtonText = ""; SecondaryButtonText = "";
        _body.Children.Clear();
        _source.ShowSignIn(advanced);
        _body.Children.Add(_source.View); _body.Children.Add(_error);
    }

    private sealed class CloudFolderSelector : IDisposable
    {
        private readonly ClientController _controller;
        private readonly nint _owner;
        private readonly CancellationTokenSource _lifetime;
        private readonly Action<Exception> _error;
        private readonly Action _changed;
        private readonly string _heading;
        private readonly ComboBox _account = new() { Header = "Account", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
        private readonly ComboBox _container = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
        private readonly TextBlock _path = SourceImportDialog.Text("Choose an account and folder", true);
        private readonly ListView _folders = new() { SelectionMode = ListViewSelectionMode.None, MaxHeight = 180, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly Button _up = new() { Content = "Up", IsEnabled = false };
        private readonly Button _more = new() { Content = "More folders", Visibility = Visibility.Collapsed };
        private readonly Button _connect = new() { Content = "Connect OneDrive account…" };
        private readonly Stack<TransferLocation> _parents = new();
        private string _provider = "";
        private string? _cursor;
        private bool _loading, _disposed;
        private int _pending, _generation;
        internal StackPanel View { get; } = new() { Spacing = 8 };
        internal TransferLocation? Location { get; private set; }
        internal bool IsWorking => _pending > 0;

        internal CloudFolderSelector(ClientController controller, nint owner, string heading, CancellationToken parentToken, Action<Exception> error, Action changed)
        {
            _controller = controller; _owner = owner; _heading = heading; _error = error; _changed = changed;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _account.SelectionChanged += async (_, _) => { if (!_loading) await AccountChangedAsync(); };
            _container.SelectionChanged += async (_, _) => { if (!_loading) await ContainerChangedAsync(); };
            _connect.Click += (_, _) => ShowSignIn();
            _up.Click += async (_, _) => { if (_parents.TryPop(out var parent)) await BrowseAsync(parent); };
            _more.Click += async (_, _) => { if (Location is { } location) await BrowseAsync(location, append: true); };
        }

        internal async Task ConfigureAsync(string provider, TransferLocation? fixedLocation)
        {
            _provider = provider; _loading = true; Location = null; _parents.Clear(); _folders.Items.Clear();
            View.Children.Clear(); View.Children.Add(SourceImportDialog.Text(_heading, false));
            if (fixedLocation is not null)
            {
                Location = fixedLocation;
                View.Children.Add(SourceImportDialog.PathCard(_heading, fixedLocation.DisplayName, fixedLocation.Path));
                _loading = false; _changed(); return;
            }
            if (provider == "local")
            {
                View.Children.Add(_path); _path.Text = "Choose an explicit folder on this PC";
                var browse = new Button { Content = "Choose folder on this PC…", HorizontalAlignment = HorizontalAlignment.Left };
                browse.Click += async (_, _) => await WorkAsync(async () =>
                {
                    var path = await DesktopPickers.PickFolderAsync(_owner, "Choose transfer " + _heading.ToLowerInvariant(), _lifetime.Token);
                    if (path is not null) { Location = LocalTransferEndpoint.ForFolder(path, "This PC · " + System.IO.Path.GetFileName(path)); _path.Text = path; }
                });
                View.Children.Add(browse); _loading = false; _changed(); return;
            }
            View.Children.Add(_account); View.Children.Add(_container);
            if (provider == "onedrive") { _account.ItemsSource = _controller.OneDriveAccounts; _account.SelectedIndex = _controller.OneDriveAccounts.Count == 1 ? 0 : -1; View.Children.Add(_connect); }
            else { _account.ItemsSource = new[] { new CloudBucket(_controller.Settings.AccountId, "Connected B2 · " + _controller.Settings.BucketName) }; _account.SelectedIndex = 0; }
            View.Children.Add(_path);
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; navigation.Children.Add(_up); navigation.Children.Add(_more);
            View.Children.Add(navigation); View.Children.Add(_folders);
            _loading = false;
            await AccountChangedAsync();
        }

        private async Task WorkAsync(Func<Task> work)
        {
            if (_disposed) return;
            _pending++; View.IsHitTestVisible = false; _changed();
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_lifetime.IsCancellationRequested) _error(error); }
            finally { _pending--; if (!_lifetime.IsCancellationRequested) View.IsHitTestVisible = _pending == 0; _changed(); DisposeWhenIdle(); }
        }

        private Task AccountChangedAsync() => WorkAsync(async () =>
        {
            Location = null; _changed(); _parents.Clear(); _folders.Items.Clear(); _loading = true;
            try
            {
                if (_provider == "onedrive" && _account.SelectedItem is ClientStorage.OneDriveConnection account)
                { _container.Header = "OneDrive"; _container.ItemsSource = await _controller.GetOneDriveDrivesAsync(account.Id, _lifetime.Token); }
                else if (_provider == "b2") { _container.Header = "B2 bucket"; _container.ItemsSource = await _controller.GetImportBucketsAsync(_lifetime.Token); }
                else { _container.ItemsSource = null; return; }
                _container.SelectedIndex = _container.Items.Count == 1 ? 0 : -1;
            }
            finally { _loading = false; }
            await ContainerChangedAsync();
        });

        private Task ContainerChangedAsync() => WorkAsync(async () =>
        {
            Location = null; _changed(); _parents.Clear();
            TransferLocation? root = null;
            if (_provider == "onedrive" && _account.SelectedItem is ClientStorage.OneDriveConnection account && _container.SelectedItem is OneDriveDrive drive)
                root = await _controller.GetOneDriveRootAsync(account.Id, drive.Id, _lifetime.Token);
            else if (_provider == "b2" && _container.SelectedItem is CloudBucket bucket)
                root = _controller.GetB2TransferRoot(bucket.Id, bucket.Name);
            if (root is not null) await BrowseAsync(root);
        });

        private Task BrowseAsync(TransferLocation location, bool append = false) => WorkAsync(async () =>
        {
            Location = null; _changed();
            var generation = ++_generation;
            var page = await _controller.BrowseTransferFoldersAsync(location, append ? _cursor : null, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _generation) return;
            Location = location; _path.Text = location.DisplayName + " / " + (location.Path.Length == 0 ? "Root" : location.Path);
            if (!append) _folders.Items.Clear();
            foreach (var folder in page.Folders)
            {
                var selected = folder;
                var button = new Button { Content = "\uE8B7  " + folder.Name, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(button, _heading + " folder " + folder.Name);
                button.Click += async (_, _) => { _parents.Push(location); await BrowseAsync(location with { FolderId = selected.Id, Path = selected.Path }); };
                _folders.Items.Add(button);
            }
            _cursor = page.NextCursor; _more.Visibility = _cursor is null ? Visibility.Collapsed : Visibility.Visible;
            _up.IsEnabled = _parents.Count > 0;
            _changed();
        });

        internal void ShowSignIn(bool showAdvanced = false)
        {
            Location = null; _changed();
            var clientId = new TextBox { Header = "Microsoft application ID", Text = Environment.GetEnvironmentVariable(OneDriveSignInConfiguration.ClientIdEnvironmentVariable) ?? OneDriveSignInConfiguration.DefaultClientId };
            var tenant = new TextBox { Header = "Sign-in authority", Text = Environment.GetEnvironmentVariable(OneDriveSignInConfiguration.TenantEnvironmentVariable) ?? OneDriveSignInConfiguration.DefaultTenant,
                PlaceholderText = "common, consumers, or an organization tenant" };
            var organization = new Button { Content = "Use yxrcz organization" };
            organization.Click += (_, _) => tenant.Text = OneDriveSignInConfiguration.YxrczTenantId;
            var settings = new StackPanel { Spacing = 8 };
            settings.Children.Add(SourceImportDialog.Text("Change these only when using a custom Microsoft application or organization-specific sign-in.", true));
            settings.Children.Add(clientId); settings.Children.Add(tenant); settings.Children.Add(organization);
            var advanced = new Expander { Header = "Advanced connection settings", Content = settings, IsExpanded = showAdvanced,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var status = SourceImportDialog.Text("Connect your personal or work Microsoft account. Microsoft will ask you to approve access to your OneDrive. Your sign-in is stored securely for this Windows account.", true);
            var signIn = new Button { Content = "Sign in with Microsoft" };
            var back = new Button { Content = "Back to accounts" };
            var link = new HyperlinkButton { Content = "Open Microsoft sign-in", Visibility = Visibility.Collapsed };
            View.Children.Clear(); View.Children.Add(SourceImportDialog.Text(_heading + " · Connect OneDrive"));
            View.Children.Add(status); View.Children.Add(signIn); View.Children.Add(link); View.Children.Add(advanced); View.Children.Add(back);
            back.Click += async (_, _) => await ConfigureAsync("onedrive", null);
            signIn.Click += async (_, _) =>
            {
                // Keep the verification link interactive while the token endpoint is polled.
                _pending++; _changed(); signIn.IsEnabled = false; back.IsEnabled = false; advanced.IsEnabled = false;
                link.Visibility = Visibility.Collapsed;
                try
                {
                    var options = OneDriveSignInConfiguration.Resolve(clientId.Text, tenant.Text);
                    var code = await _controller.BeginOneDriveSignInAsync(options.ClientId, options.Tenant, _lifetime.Token);
                    status.Text = code.Message;
                    link.Content = "Open Microsoft sign-in · " + code.UserCode; link.NavigateUri = code.VerificationUri; link.Visibility = Visibility.Visible;
                    var account = await _controller.CompleteOneDriveSignInAsync(options.ClientId, options.Tenant, code, _lifetime.Token);
                    await ConfigureAsync("onedrive", null);
                    _account.SelectedItem = _controller.OneDriveAccounts.Single(item => item.Id == account.Id);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!_lifetime.IsCancellationRequested) _error(error); }
                finally { _pending--; signIn.IsEnabled = true; back.IsEnabled = true; advanced.IsEnabled = true; _changed(); DisposeWhenIdle(); }
            };
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); DisposeWhenIdle(); }
        private void DisposeWhenIdle() { if (_disposed && _pending == 0) _lifetime.Dispose(); }
    }
}

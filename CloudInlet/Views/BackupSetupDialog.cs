using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace CloudInlet.Views;

/// <summary>Collects explicit file and Windows-location choices without inspecting or transferring contents.</summary>
internal sealed class BackupSetupDialog : ContentDialog
{
    private readonly ClientController _controller;
    private readonly AppSettings _settings;
    private readonly nint _owner;
    private readonly string _name;
    private readonly bool _stopping;
    private readonly string _currentPath;
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly StackPanel _locations = new() { Spacing = 8 };
    private readonly StackPanel _transfer = new() { Spacing = 8 };
    private readonly TextBlock _locationHeading;
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly TextBlock _effect = SourceImportDialog.Text("", true);
    private readonly ComboBox _mode = new() { Header = "Existing files", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _freeSpace = new() { Content = "Free downloaded CloudInlet copies; keep files in B2" };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<RadioButton> _locationButtons = [];
    private readonly string _group = Guid.NewGuid().ToString("N");
    private readonly Button _browse = new() { Content = "Choose another folder…", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly RadioButton _none;
    private readonly RadioButton _bring;
    private readonly RadioButton _cloud;
    private RadioButton? _manual;
    private bool _working, _closed;
    private bool _discoveryStarted, _disposed;
    private int _pending;
    private string _selectedPath;
    private string? _presentationPath;

    internal string? SourcePath => _stopping || TransferMode == BackupTransferMode.None || SamePath(_selectedPath, _currentPath) ? null : _selectedPath;
    internal string? DestinationPath => _stopping ? _selectedPath : null;
    internal bool IncludeCurrentFiles => !_stopping && TransferMode != BackupTransferMode.None && SamePath(_selectedPath, _currentPath);
    internal bool DirectCloudTransferRequested => _cloud?.IsChecked == true;
    internal BackupTransferMode TransferMode => _none.IsChecked == true || DirectCloudTransferRequested ? BackupTransferMode.None :
        _mode.SelectedIndex == 1 ? BackupTransferMode.Move : BackupTransferMode.Copy;
    internal bool FreeLocalSpace => _stopping && TransferMode != BackupTransferMode.Move && _freeSpace.IsChecked == true;
    internal FrameworkElement CaptureContent => _body;

    internal BackupSetupDialog(ClientController controller, nint owner, string name, bool stopping = false,
        AppSettings? presentationSettings = null, IReadOnlyList<ImportSourceCandidate>? presentationCandidates = null)
    {
        _controller = controller; _settings = presentationSettings ?? controller.Settings;
        _owner = owner; _name = name; _stopping = stopping;
        var backup = _settings.Backups.SingleOrDefault(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _currentPath = stopping ? backup?.OriginalPath ?? KnownFolderBackup.GetDefaultPath(name) :
            presentationSettings is null ? KnownFolderBackup.GetPath(name) : Path.Combine(@"C:\Users\Example", name);
        _selectedPath = _currentPath;
        Title = stopping ? "Stop backing up " + name : "Set up " + name + " backup";
        PrimaryButtonText = "Review choices"; CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.None;
        Resources["ContentDialogMaxWidth"] = 680d;

        _body.Children.Add(SourceImportDialog.Text(stopping
            ? "Choose what happens to your files when Windows stops opening this folder in CloudInlet."
            : "Choose the files to bring into CloudInlet, or turn on backup without importing existing files.", true));
        _bring = new RadioButton { GroupName = _group + "action", IsChecked = true,
            Content = stopping ? "Copy or move files to another location" : "Bring existing files" };
        _none = new RadioButton { GroupName = _group + "action",
            Content = stopping ? "Turn off without restoring files" : "Turn on without importing files" };
        _cloud = new RadioButton { GroupName = _group + "action",
            Content = stopping ? "Transfer B2 files directly to OneDrive" : "Bring files directly from OneDrive" };
        _bring.Checked += (_, _) => UpdateEffect(); _none.Checked += (_, _) => UpdateEffect();
        _cloud.Checked += (_, _) => UpdateEffect();
        _body.Children.Add(_bring); _body.Children.Add(_cloud); _body.Children.Add(_none);
        _locationHeading = SourceImportDialog.Text(stopping ? "Windows will open " + name + " here" : "Choose a source", false);
        _body.Children.Add(_locationHeading);
        AddLocation(stopping ? "Previous Windows location" : "Current Windows location", _currentPath, true);
        if (stopping)
        {
            var local = presentationSettings is null ? KnownFolderBackup.GetDefaultPath(name) : Path.Combine(@"C:\Users\Example", name);
            if (!SamePath(local, _currentPath)) AddLocation("This PC · " + name, local);
        }
        _body.Children.Add(_locations);
        _browse.Click += async (_, _) => await BrowseAsync();
        _body.Children.Add(_browse);
        _mode.Items.Add("Copy — keep originals"); _mode.Items.Add("Move — remove verified originals");
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) => UpdateEffect();
        _transfer.Children.Add(_mode);
        _body.Children.Add(_transfer);
        if (stopping)
        {
            _body.Children.Add(_freeSpace);
            _body.Children.Add(SourceImportDialog.Text("Freeing space keeps online-only files in CloudInlet. Files still waiting to upload remain on this PC.", true));
        }
        _body.Children.Add(_effect); _body.Children.Add(_error);
        Content = new ScrollViewer { Content = _body, MaxHeight = 600, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); DisposeWhenIdle(); };
        PrimaryButtonClick += (_, args) => { if (_working || _closed) args.Cancel = true; };
        if (presentationCandidates is not null) RenderCandidates(presentationCandidates);
        else Loaded += async (_, _) => await LoadCandidatesAsync();
        Loaded += (_, _) => { if (_presentationPath is not null) SelectLocation(_presentationPath); };
        UpdateEffect();
    }

    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private RadioButton AddLocation(string label, string path, bool selected = false)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(SourceImportDialog.Text(label));
        content.Children.Add(SourceImportDialog.Text(path, true));
        var button = new RadioButton { GroupName = _group + "location", Content = content,
            IsChecked = false, Tag = path, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
        AutomationProperties.SetName(button, label + ". " + path);
        button.Checked += (_, _) => { _selectedPath = path; UpdateEffect(); };
        _locationButtons.Add(button); _locations.Children.Add(button);
        if (selected) SelectLocation(path);
        return button;
    }

    private void RenderCandidates(IReadOnlyList<ImportSourceCandidate> candidates)
    {
        foreach (var candidate in candidates)
            if (!SamePath(candidate.Path, _currentPath) && !_locationButtons.Any(button =>
                (string?)button.Tag is { } path && SamePath(path, candidate.Path)))
            {
                var button = AddLocation(candidate.DisplayName, candidate.Path);
                button.Tag = candidate.Path;
            }
        UpdateEffect();
    }

    private async Task LoadCandidatesAsync()
    {
        if (_discoveryStarted || _closed) return;
        _discoveryStarted = true; _pending++;
        var task = Task.Run(() => ImportSourceDiscovery.FindCandidates(_settings, _name));
        _ = task.ContinueWith(done => { _ = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            var candidates = await task.WaitAsync(TimeSpan.FromSeconds(8), _lifetime.Token);
            if (!_closed) RenderCandidates(candidates);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { if (!_closed) ShowError("Windows is taking longer to find cloud folders. You can choose a folder manually."); }
        catch (Exception error) { if (!_closed) ShowError(Describe(error, "Cloud folders could not be listed. Choose a folder manually.")); }
        finally { _pending--; DisposeWhenIdle(); }
    }

    private async Task BrowseAsync()
    {
        if (_working || _closed) return;
        _pending++;
        _working = true; IsPrimaryButtonEnabled = false; _browse.IsEnabled = false; _error.IsOpen = false;
        try
        {
            var path = await DesktopPickers.PickFolderAsync(_owner, _stopping ? "Choose Windows location" : "Choose source folder", _lifetime.Token);
            if (path is null || _closed) return;
            SelectLocation(path);
            UpdateEffect();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(Describe(error, "The folder chooser could not open. Try again.")); }
        finally { _working = false; _pending--; DisposeWhenIdle(); if (!_closed) { IsPrimaryButtonEnabled = true; UpdateEffect(); } }
    }

    private static string Describe(Exception error, string fallback) => FileSystemError.Describe(error) is { Length: > 0 } message
        ? message : fallback + $" Windows error 0x{error.HResult:X8}.";

    private void DisposeWhenIdle()
    { if (_closed && _pending == 0 && !_disposed) { _disposed = true; _lifetime.Dispose(); } }

    private void ShowError(string message) { _error.Message = message; _error.IsOpen = true; }

    private void SelectLocation(string path)
    {
        var selected = _locationButtons.FirstOrDefault(button => button.Tag is string existing && SamePath(existing, path));
        if (selected is null)
        {
            if (_manual is not null) { _locations.Children.Remove(_manual); _locationButtons.Remove(_manual); }
            _manual = selected = AddLocation("Chosen folder", path);
        }
        // Keep one selected control before WinUI registers groups on load, too.
        foreach (var button in _locationButtons) if (!ReferenceEquals(button, selected)) button.IsChecked = false;
        selected.IsChecked = true;
        _selectedPath = path;
    }

    private void UpdateEffect()
    {
        // Checked events fire while native controls are being constructed.
        if (_none is null || _mode is null) return;
        var none = TransferMode == BackupTransferMode.None;
        _mode.IsEnabled = !none;
        _transfer.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        if (!_stopping)
        {
            _locations.Visibility = _browse.Visibility = _locationHeading.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            foreach (var button in _locationButtons) button.IsEnabled = !none && !_working;
            _browse.IsEnabled = !none && !_working;
            _effect.Text = none
                ? "Existing files stay in their current folders. Windows will open " + _name + " in CloudInlet. Any files already in that CloudInlet folder remain available."
                : TransferMode == BackupTransferMode.Move
                    ? "Files are copied and verified before their originals are removed. If the source is in another cloud app, removing originals can delete them from that cloud too. Windows will use CloudInlet."
                    : "Files are copied and verified. Originals stay in the chosen source. Windows will use CloudInlet; other locations are not imported automatically.";
            if (DirectCloudTransferRequested)
                _effect.Text = "Next, browse the actual OneDrive account and folder and choose Copy or Move. Files transfer directly into this folder's B2 backup before Windows starts using CloudInlet.";
        }
        else
        {
            _freeSpace.IsEnabled = TransferMode != BackupTransferMode.Move;
            if (!_freeSpace.IsEnabled) _freeSpace.IsChecked = false;
            _effect.Text = none
                ? "Files stay in CloudInlet. Windows uses the location selected above. Files already there remain; no files are restored or deleted."
                : TransferMode == BackupTransferMode.Move
                    ? "Verified files move to the selected Windows location. Removing originals from CloudInlet also removes their current B2 copies through sync. Another cloud app manages uploads at its own location."
                    : "Verified files are copied to the selected Windows location. CloudInlet and B2 copies stay available. Another cloud app manages uploads at its own location.";
            if (DirectCloudTransferRequested)
            {
                _freeSpace.IsEnabled = false; _freeSpace.IsChecked = false;
                _effect.Text = "Next, choose the OneDrive destination and transfer options. Windows first uses the local location selected above; no files are restored there. CloudInlet stops syncing the old folder, then transfers its B2 contents directly to OneDrive.";
            }
        }
    }

    internal void SetPresentationChoice(BackupTransferMode mode, string? location = null, bool freeLocalSpace = false)
    {
        _mode.SelectedIndex = mode == BackupTransferMode.Move ? 1 : 0;
        _none.IsChecked = mode == BackupTransferMode.None;
        _bring.IsChecked = mode != BackupTransferMode.None;
        if (location is not null) { _presentationPath = location; SelectLocation(location); }
        _freeSpace.IsChecked = freeLocalSpace;
        UpdateEffect();
    }

    internal void ValidateVisibleSelection()
    {
        var selected = _locationButtons.Where(button => button.IsChecked == true).ToArray();
        if (selected.Length != 1 || selected[0].Tag is not string path || !SamePath(path, _selectedPath) ||
            new[] { _bring, _none, _cloud }.Count(button => button.IsChecked == true) != 1)
            throw new InvalidOperationException("The visible backup selection must match its reviewed source, destination and action.");
    }
}

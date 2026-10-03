using System.Diagnostics;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.ViewModels;
using CloudBay.Views;
using CloudBay.Windows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace CloudBay;

public sealed partial class MainWindow : Window
{
    private readonly ClientController _controller;
    private readonly ClientViewModel _viewModel;
    private readonly Dictionary<string, ToggleSwitch> _backupSwitches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _backupPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _backupAvailability = new(StringComparer.OrdinalIgnoreCase);
    private string _customBackupRevision = "";
    private string _fileScopesRevision = "";
    private bool _refreshingFileScopes;
    private bool _busy;
    private bool _refreshingBackups;
    private bool _closed;
    private AppSettings? _loadedSettings;
    private string _versionPath = "";
    private bool? _compactLayout;
    private readonly UISettings _uiSettings = new();

    public bool AllowClose { get; set; }

    public MainWindow(ClientController controller)
    {
        _controller = controller;
        InitializeComponent();
        _viewModel = new ClientViewModel(controller);
        RootGrid.DataContext = _viewModel;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = "CloudBay";
        AppWindow.Resize(new SizeInt32(1180, 840));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += (_, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _controller.Changed -= Controller_Changed;
            _uiSettings.TextScaleFactorChanged -= TextScaleFactor_Changed;
        };
        CreateBackupRows();
        LoadSettings();
        Refresh();
        _controller.Changed += Controller_Changed;
        RootGrid.Loaded += (_, _) =>
        {
            if (!_controller.Settings.IsConfigured) ShowSettings();
        };
        Navigation.SelectedItem = Navigation.MenuItems[0];
        RootGrid.SizeChanged += (_, _) => UpdateResponsiveLayout();
        OverviewPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        BackupPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        FilesPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        SettingsPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        Navigation.DisplayModeChanged += (_, _) => UpdateResponsiveLayout();
        _uiSettings.TextScaleFactorChanged += TextScaleFactor_Changed;
        RootGrid.Loaded += (_, _) => MeasureNavigationPane();
    }

    public void ShowWindow()
    {
        if (_closed) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
    }

    public void ShowSettings()
    {
        Navigation.SelectedItem = Navigation.SettingsItem;
        ShowPage("settings");
        ShowWindow();
    }

    public void ShowActivity()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowPage("activity");
        ShowWindow();
    }

    private void Controller_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, Refresh);

    private void Refresh()
    {
        if (_closed) return;
        _viewModel.Refresh();
        OverviewEmptyActivity.Visibility = ActivityEmpty.Visibility = _viewModel.HasActivity ? Visibility.Collapsed : Visibility.Visible;
        TransferProgressPanel.Visibility = _viewModel.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        TransferProgress.IsIndeterminate = _controller.Snapshot.TransferTotalBytes <= 0;
        ReviewDeletionsButton.Visibility = _controller.Snapshot.State == ClientState.Attention && _controller.Snapshot.Message.StartsWith("Review required:", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        BackupCount.Text = (_controller.Settings.Backups.Count + _controller.Settings.CustomBackups.Count).ToString();
        DisconnectButton.Visibility = _controller.Settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.IsEnabled = _viewModel.IsConfigured;
        ConnectAccountButton.Visibility = _viewModel.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        OpenFolderButton.Visibility = SyncNowButton.Visibility = DashboardPauseButton.Visibility = _viewModel.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        SyncNowButton.IsEnabled = DashboardPauseButton.IsEnabled = _viewModel.IsConfigured && !_busy;
        RefreshBackups();
        RefreshCustomBackups();
        RefreshFileScopes();
        ApplyTheme(_controller.Settings.Theme);
        if (!ReferenceEquals(_loadedSettings, _controller.Settings) && !_busy) LoadSettings();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // NavigationView can raise SelectionChanged while InitializeComponent is
        // still wiring the named content panels.
        if (OverviewPage is null) return;
        ShowPage(args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "overview");
    }

    private void ShowPage(string page)
    {
        FrameworkElement[] pages = [OverviewPage, ActivityPage, BackupPage, FilesPage, SettingsPage];
        foreach (var item in pages) item.Visibility = Visibility.Collapsed;
        var selected = page switch
        {
            "activity" => (FrameworkElement)ActivityPage,
            "backup" => BackupPage,
            "files" => FilesPage,
            "settings" => SettingsPage,
            _ => OverviewPage
        };
        selected.Visibility = Visibility.Visible;
        if (selected is ScrollViewer viewer) viewer.ChangeView(null, 0, null, true);
    }

    private void ApplyTheme(string theme) => RootGrid.RequestedTheme = theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void LoadSettings(bool reloadAccount = false, bool reloadPreferences = false)
    {
        var settings = _controller.Settings;
        var previous = _loadedSettings;
        _loadedSettings = settings;
        // Backup changes, reconnection, and tray quick settings also replace the
        // settings object. Refresh untouched fields while preserving edits the
        // user has made in this window until their own save succeeds.
        if (reloadAccount || previous is null || BucketNameBox.Text == previous.BucketName) BucketNameBox.Text = settings.BucketName;
        if (reloadAccount || previous is null || KeyIdBox.Text == previous.KeyId) KeyIdBox.Text = settings.KeyId;
        if (reloadAccount || previous is null || RootPathBox.Text == previous.RootPath) RootPathBox.Text = settings.RootPath;
        if (reloadAccount || previous is null || PrefixBox.Text == previous.Prefix) PrefixBox.Text = settings.Prefix;
        if (reloadPreferences || previous is null || FilesOnDemandSwitch.IsOn == previous.FilesOnDemand) FilesOnDemandSwitch.IsOn = settings.FilesOnDemand;
        if (reloadPreferences || previous is null || UploadLimitBox.Value == previous.UploadBytesPerSecond / 1024d) UploadLimitBox.Value = settings.UploadBytesPerSecond / 1024d;
        if (reloadPreferences || previous is null || DownloadLimitBox.Value == previous.DownloadBytesPerSecond / 1024d) DownloadLimitBox.Value = settings.DownloadBytesPerSecond / 1024d;
        if (reloadPreferences || previous is null || ConcurrencyBox.Value == previous.UploadConcurrency) ConcurrencyBox.Value = settings.UploadConcurrency;
        if (reloadPreferences || previous is null || MeteredBox.IsChecked == previous.PauseOnMetered) MeteredBox.IsChecked = settings.PauseOnMetered;
        if (reloadPreferences || previous is null || BatterySaverBox.IsChecked == previous.PauseOnBatterySaver) BatterySaverBox.IsChecked = settings.PauseOnBatterySaver;
        if (reloadPreferences || previous is null || StartAtSignInBox.IsChecked == previous.StartAtSignIn) StartAtSignInBox.IsChecked = settings.StartAtSignIn;
        if (reloadPreferences || previous is null || ExclusionsBox.Text == string.Join(Environment.NewLine, previous.Exclusions)) ExclusionsBox.Text = string.Join(Environment.NewLine, settings.Exclusions);
        if (reloadPreferences || previous is null || (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string == previous.Theme)
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == settings.Theme) ?? ThemeBox.Items[0];
        AccountHeading.Text = settings.IsConfigured ? $"Connected to {settings.BucketName}" : "Connect your private bucket";
        AccountDescription.Text = settings.IsConfigured ? "CloudBay protects your files using this Backblaze B2 bucket." : "Use a B2 application key with access to your chosen bucket.";
        ConnectButton.Content = settings.IsConfigured ? "Update connection" : "Connect to Backblaze B2";
        // The sync root identity and key namespace are fixed once registered with
        // Windows. A credential refresh must not silently start another tree.
        BucketNameBox.IsReadOnly = RootPathBox.IsReadOnly = PrefixBox.IsReadOnly = settings.Backups.Count + settings.CustomBackups.Count > 0;
    }

    private void UpdateResponsiveLayout()
    {
        if (Navigation.ActualWidth <= 0) return;
        // A max-width StackPanel inside ScrollViewer can be centered using its
        // natural desired width even when it is arranged wider. Give it the
        // current viewport width explicitly so cards never slide offscreen.
        foreach (var (viewer, content) in new[] { (OverviewPage, OverviewContent), (BackupPage, BackupContent), (FilesPage, FilesContent), (SettingsPage, SettingsContent) })
            if (viewer.ActualWidth > 0) content.Width = Math.Min(1020, viewer.ActualWidth);
        var pane = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded ? Navigation.OpenPaneLength :
            Navigation.DisplayMode == NavigationViewDisplayMode.Compact ? Navigation.CompactPaneLength : 0;
        var compact = Navigation.ActualWidth - pane < 670;
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        DashboardActions.Orientation = FileActions.Orientation = SettingsActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        CloudStatsGrid.ColumnDefinitions.Clear();
        CloudStatsGrid.RowDefinitions.Clear();
        CloudStatsGrid.RowSpacing = compact ? 12 : 0;
        FrameworkElement[] cards = [CloudFileCard, LocalFileCard, BackupFileCard];
        for (var i = 0; i < cards.Length; i++)
        {
            if (compact) CloudStatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            else CloudStatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cards[i], compact ? i : 0);
            Grid.SetColumn(cards[i], compact ? 0 : i);
        }
    }

    private void TextScaleFactor_Changed(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { MeasureNavigationPane(); _compactLayout = null; UpdateResponsiveLayout(); });

    private void MeasureNavigationPane()
    {
        var width = 240d;
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            var text = new TextBlock { Text = item.Content?.ToString(), FontFamily = Navigation.FontFamily, FontSize = 14 };
            text.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, text.DesiredSize.Width + 108);
        }
        Navigation.OpenPaneLength = Math.Ceiling(width);
    }

    private void CreateBackupRows()
    {
        foreach (var name in KnownFolderBackup.FolderIds.Keys)
        {
            var glyph = name switch
            {
                "Desktop" => "\uE7F4", "Documents" => "\uE8A5", "Pictures" => "\uEB9F",
                "Music" => "\uE8D6", "Videos" => "\uE714", "Downloads" => "\uE896",
                "Favorites" => "\uE734", "Contacts" => "\uE77B", "Saved Games" => "\uE7FC",
                "Links" => "\uE71B", "Searches" => "\uE721", _ => "\uE8B7"
            };
            string description;
            try
            {
                description = KnownFolderBackup.GetPath(name);
                _backupAvailability[name] = KnownFolderBackup.GetRestriction(name);
                if (_backupAvailability[name] is { } restriction) description += Environment.NewLine + restriction;
            }
            catch { description = "This Windows folder is unavailable on this device"; _backupAvailability[name] = description; }
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = glyph, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayFolderIconStyle"], VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(icon);
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var path = new TextBlock { Text = description, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBaySecondaryTextStyle"], FontSize = 12 };
            labels.Children.Add(path);
            Grid.SetColumn(labels, 1);
            grid.Children.Add(labels);
            var toggle = new ToggleSwitch { Tag = name, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, $"Back up {name}");
            Grid.SetColumn(toggle, 2);
            toggle.Toggled += Backup_Toggled;
            grid.Children.Add(toggle);
            BackupRows.Children.Add(new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayCardStyle"], Child = grid });
            _backupSwitches.Add(name, toggle);
            _backupPaths.Add(name, path);
        }
    }

    private void RefreshBackups()
    {
        _refreshingBackups = true;
        try
        {
            foreach (var (name, toggle) in _backupSwitches)
            {
                var backup = _controller.Settings.Backups.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                toggle.IsOn = backup is not null;
                toggle.IsEnabled = _controller.Settings.IsConfigured && !_busy && _backupAvailability[name] is null;
                if (backup is not null) _backupPaths[name].Text = backup.DestinationPath;
                else if (_backupAvailability[name] is { } unavailable)
                {
                    try { _backupPaths[name].Text = KnownFolderBackup.GetPath(name) + Environment.NewLine + unavailable; }
                    catch { _backupPaths[name].Text = unavailable; }
                }
                else
                {
                    try { _backupPaths[name].Text = KnownFolderBackup.GetPath(name); }
                    catch { _backupPaths[name].Text = "This Windows folder is unavailable on this device"; toggle.IsEnabled = false; }
                }
            }
        }
        finally { _refreshingBackups = false; }
    }

    private void RefreshCustomBackups()
    {
        var settings = _controller.Settings;
        var revision = string.Join("|", settings.CustomBackups.Select(folder => $"{folder.Name}:{folder.SourcePath}:{folder.Prefix}")) + $"|{_busy}";
        AddCustomBackupButton.IsEnabled = settings.IsConfigured && !_busy;
        CustomBackupEmpty.Visibility = settings.CustomBackups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_customBackupRevision == revision) return;
        _customBackupRevision = revision;
        CustomBackupRows.Children.Clear();
        foreach (var folder in settings.CustomBackups)
        {
            var content = new StackPanel { Spacing = 12 };
            var heading = new Grid { ColumnSpacing = 14 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.Children.Add(new FontIcon { Glyph = "\uE8B7", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayFolderIconStyle"], VerticalAlignment = VerticalAlignment.Top });
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(new TextBlock { Text = folder.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            labels.Children.Add(new TextBlock { Text = folder.SourcePath, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBaySecondaryTextStyle"], FontSize = 12 });
            labels.Children.Add(new TextBlock { Text = $"Backed up to {settings.BucketName}", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBaySecondaryTextStyle"], FontSize = 12 });
            Grid.SetColumn(labels, 1);
            heading.Children.Add(labels);
            content.Children.Add(heading);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var open = new Button { Content = "Open folder", Tag = folder.SourcePath };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(folder.SourcePath) { UseShellExecute = true }); }
                catch (Exception error) { ShowError(error); }
            };
            actions.Children.Add(open);
            var stop = new Button { Content = "Stop backup", Tag = folder.Name, IsEnabled = !_busy };
            stop.Click += RemoveCustomBackup_Click;
            actions.Children.Add(stop);
            content.Children.Add(actions);
            CustomBackupRows.Children.Add(new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayCardStyle"], Child = content });
        }
    }

    private async void ChooseCustomBackup_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Choose a personal folder to protect…", async () =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) CustomBackupSourceBox.Text = folder.Path;
        });

    private string? SelectedBackupName => (FileScopeBox.SelectedItem as SyncFolderItem)?.BackupName;
    private string SelectedFileRoot => (FileScopeBox.SelectedItem as SyncFolderItem)?.RootPath ?? _controller.Settings.RootPath;

    private void RefreshFileScopes()
    {
        var settings = _controller.Settings;
        var revision = settings.RootPath + "|" + string.Join("|", settings.CustomBackups.Select(folder => $"{folder.Name}:{folder.SourcePath}"));
        if (_fileScopesRevision == revision) return;
        _fileScopesRevision = revision;
        var currentName = SelectedBackupName;
        var choices = new List<SyncFolderItem> { new("CloudBay folder and Windows backups", null, settings.RootPath) };
        choices.AddRange(settings.CustomBackups.Select(folder => new SyncFolderItem(folder.Name, folder.Name, folder.SourcePath)));
        _refreshingFileScopes = true;
        try
        {
            FileScopeBox.ItemsSource = choices;
            FileScopeBox.SelectedItem = choices.FirstOrDefault(item => item.BackupName == currentName) ?? choices[0];
            UpdateFileScopeLabels();
        }
        finally { _refreshingFileScopes = false; }
        VersionsPanel.Visibility = Visibility.Collapsed;
        VersionsList.ItemsSource = null;
        FilePathBox.Text = "";
    }

    private void FileScope_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshingFileScopes || FileFolderPath is null) return;
        UpdateFileScopeLabels();
        FilePathBox.Text = "";
        VersionsPanel.Visibility = Visibility.Collapsed;
        VersionsList.ItemsSource = null;
    }

    private void UpdateFileScopeLabels()
    {
        if (FileScopeBox.SelectedItem is not SyncFolderItem scope) return;
        FileFolderName.Text = scope.BackupName is null ? "CloudBay folder" : scope.Name;
        FileFolderPath.Text = scope.RootPath;
    }

    private void OpenFileFolder_Click(object sender, RoutedEventArgs args)
    {
        try { _controller.LaunchFolder(SelectedBackupName); }
        catch (Exception error) { ShowError(error); }
    }

    private async void AddCustomBackup_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Setting up folder backup…", async () =>
        {
            var source = CustomBackupSourceBox.Text.Trim();
            if (source.Length == 0) throw new InvalidOperationException("Choose the personal folder you want to back up.");
            var name = CustomBackupNameBox.Text.Trim();
            if (name.Length == 0) name = new DirectoryInfo(source).Name;
            if (name.Length == 0 || name.Length > 80 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
                throw new InvalidOperationException("Choose a backup name up to 80 characters without slashes or Windows filename symbols.");
            await _controller.AddCustomBackupAsync(source, name);
            CustomBackupSourceBox.Text = CustomBackupNameBox.Text = "";
            ShowInfo($"{name} is now backed up. Its folder remains in its current location.");
        });

    private async void RemoveCustomBackup_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: string name }) return;
        var folder = _controller.Settings.CustomBackups.SingleOrDefault(item => item.Name == name);
        if (folder is null) return;
        await RunAsync($"Preparing {name} before stopping backup…", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme,
                Title = $"Stop backing up {name}?",
                Content = $"CloudBay will download all online-only files in {folder.SourcePath} before removing this folder's sync connection. Your local files and files in Backblaze B2 are retained. Keep CloudBay running until this finishes.",
                PrimaryButtonText = "Download and stop backup",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _controller.RemoveCustomBackupAsync(name);
            ShowInfo($"{name} backup stopped. Your local files and B2 files were retained.");
        });
    }

    private async void Backup_Toggled(object sender, RoutedEventArgs args)
    {
        if (_refreshingBackups || _busy || sender is not ToggleSwitch toggle) return;
        var name = (string)toggle.Tag;
        var enabled = toggle.IsOn;
        await RunAsync(enabled ? $"Setting up {name} backup…" : $"Restoring {name} to its original location…",
            () => _controller.SetBackupAsync(name, enabled), enabled ? $"{name} backup is on." : $"{name} backup is off.");
        RefreshBackups();
    }

    private async void Connect_Click(object sender, RoutedEventArgs args)
    {
        await RunAsync("Connecting securely to Backblaze B2…", async () =>
        {
            if (string.IsNullOrWhiteSpace(ApplicationKeyBox.Password)) throw new InvalidOperationException("Enter your B2 application key to connect.");
            await _controller.ConnectAsync(KeyIdBox.Text.Trim(), ApplicationKeyBox.Password, BucketNameBox.Text.Trim(), RootPathBox.Text.Trim(), PrefixBox.Text.Trim());
            ApplicationKeyBox.Password = "";
            LoadSettings(reloadAccount: true);
        }, "Your Backblaze B2 bucket is connected. CloudBay is running in the background.");
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs args)
    {
        await RunAsync("Saving your settings…", async () =>
        {
            var upload = ReadWholeNumber(UploadLimitBox, "Upload limit", 0, 1048576);
            var download = ReadWholeNumber(DownloadLimitBox, "Download limit", 0, 1048576);
            var concurrency = ReadWholeNumber(ConcurrencyBox, "Concurrent uploads", 1, 16);
            var settings = _controller.Settings with
            {
                FilesOnDemand = FilesOnDemandSwitch.IsOn,
                UploadBytesPerSecond = upload * 1024L,
                DownloadBytesPerSecond = download * 1024L,
                UploadConcurrency = concurrency,
                PauseOnMetered = MeteredBox.IsChecked == true,
                PauseOnBatterySaver = BatterySaverBox.IsChecked == true,
                StartAtSignIn = StartAtSignInBox.IsChecked == true,
                Theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag,
                Exclusions = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            await _controller.SaveSettingsAsync(settings);
            LoadSettings(reloadPreferences: true);
        }, "Your settings are saved.");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        await RunAsync("Preparing your files before disconnecting…", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme,
                Title = "Disconnect this account?",
                Content = "CloudBay will download all cloud files in your sync and backup folders, restore backed-up Windows folders to their original local locations, and then disconnect. Other personal folders stay in their current locations. Your files in Backblaze B2 are retained. Keep CloudBay running until this finishes; it may take time and use disk space.",
                PrimaryButtonText = "Download and disconnect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _controller.DisconnectAsync();
            ApplicationKeyBox.Password = "";
            LoadSettings(reloadAccount: true, reloadPreferences: true);
            ShowInfo("The account is disconnected. Your local files and B2 files were retained.");
        });
    }

    private static int ReadWholeNumber(NumberBox box, string name, int min, int max)
    {
        if (double.IsNaN(box.Value) || !double.IsFinite(box.Value) || box.Value < min || box.Value > max || box.Value != Math.Truncate(box.Value))
            throw new InvalidOperationException($"{name} must be a whole number between {min:N0} and {max:N0}.");
        return (int)box.Value;
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Checking for file changes…", () => _controller.SyncNowAsync());

    private void Pause_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        try
        {
            if (_controller.Snapshot.State == ClientState.Paused) _controller.Resume();
            else _controller.Pause();
            Refresh();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        try { _controller.LaunchFolder(); }
        catch (Exception error) { ShowError(error); }
    }

    private void ManageBackup_Click(object sender, RoutedEventArgs args) => Navigation.SelectedItem = Navigation.MenuItems[2];
    private void ViewActivity_Click(object sender, RoutedEventArgs args) => ShowActivity();
    private void Settings_Click(object sender, RoutedEventArgs args) => ShowSettings();

    private async void ChooseRoot_Click(object sender, RoutedEventArgs args)
    {
        if (_controller.Settings.Backups.Count + _controller.Settings.CustomBackups.Count > 0)
        {
            ShowInfo("Turn off system folder backups before changing the account or sync folder.");
            return;
        }
        await RunAsync("Choose a CloudBay folder…", async () =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) RootPathBox.Text = folder.Path;
        });
    }

    private async void BrowseFile_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Choose a file in CloudBay…", async () =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var relative = Path.GetRelativePath(SelectedFileRoot, file.Path);
            ValidateRelativePath(relative);
            FilePathBox.Text = relative;
        });

    private string SelectedFilePath()
    {
        if (!_controller.Settings.IsConfigured) throw new InvalidOperationException("Connect your Backblaze B2 bucket first.");
        var path = FilePathBox.Text.Trim().Replace('\\', '/');
        ValidateRelativePath(path);
        return path;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split(['/', '\\']).Any(part => part is ".." or "."))
            throw new InvalidOperationException("Select a file inside the selected sync folder, or enter its relative path.");
    }

    private async void PinFile_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Making your file available offline…", () => _controller.SetPinAsync(SelectedFilePath(), PinMode.AlwaysAvailable, SelectedBackupName), "This file is set to always stay on this device.");

    private async void FreeSpace_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Freeing downloaded file space…", () => _controller.FreeSpaceAsync(SelectedFilePath(), SelectedBackupName), "Downloaded content was released. Your cloud copy is still available.");

    private async void Versions_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Loading retained B2 versions…", async () =>
        {
            var path = SelectedFilePath();
            var versions = await _controller.GetVersionsAsync(path, SelectedBackupName);
            _versionPath = $"{FileFolderName.Text} / {path}";
            VersionPathLabel.Text = _versionPath;
            VersionsList.ItemsSource = versions.OrderByDescending(item => item.ModifiedUtc).Select(item => new VersionItem(item)).ToList();
            VersionsEmptyLabel.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VersionsPanel.Visibility = Visibility.Visible;
            RestoreVersionButton.IsEnabled = false;
        });

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        RestoreVersionButton.IsEnabled = !_busy && VersionsList.SelectedItem is VersionItem { File.Action: "upload" };

    private async void RestoreVersion_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || VersionsList.SelectedItem is not VersionItem version || version.File.Action != "upload") return;
        await RunAsync("Restoring the selected version…", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Restore this version?",
                Content = $"Restore {_versionPath} from {version.Modified}? This becomes the current file in CloudBay and syncs to Backblaze B2.",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = RootGrid.RequestedTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _controller.RestoreVersionAsync(version.File);
            ShowInfo("The selected version was restored.");
        });
    }

    private async void StorageSense_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Opening Windows Storage Sense…", async () =>
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:storagepolicies")))
                throw new InvalidOperationException("Windows could not open Storage Sense. Open Settings → System → Storage instead.");
        });

    private async void ReviewDeletions_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        await RunAsync("Reviewing pending deletions…", async () =>
        {
            var snapshot = _controller.Snapshot;
            if (snapshot.State != ClientState.Attention || !snapshot.Message.StartsWith("Review required:", StringComparison.Ordinal)) return;
            var count = snapshot.Pending;
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme,
                Title = "Approve these deletions?",
                Content = $"CloudBay detected {count:N0} pending deletion{(count == 1 ? "" : "s")}. {snapshot.Message}\n\nApproving applies these deletions to the other side of sync. Review your files before continuing. Retained B2 versions and local recovery copies depend on the type of deletion.",
                PrimaryButtonText = $"Approve {count:N0} deletion{(count == 1 ? "" : "s")}",
                CloseButtonText = "Keep paused",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            // Recheck after the dialog: a new scan may replace the pending set.
            if (_controller.Snapshot.Pending != count || !_controller.Snapshot.Message.Equals(snapshot.Message, StringComparison.Ordinal))
                throw new InvalidOperationException("The pending deletions changed. Review them again before approving.");
            _controller.ApprovePendingDeletions();
            ShowInfo("The reviewed deletions were approved. CloudBay will continue syncing.");
        });
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = _controller.DiagnosticsPath;
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task RunAsync(string message, Func<Task> operation, string? success = null)
    {
        if (_busy) return;
        _busy = true;
        SetBusy(true, message);
        try
        {
            await operation();
            if (success is not null) ShowInfo(success);
        }
        catch (OperationCanceledException) { ShowInfo("The operation was canceled."); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            _busy = false;
            SetBusy(false, "CloudBay continues working in the background");
            Refresh();
            RestoreVersionButton.IsEnabled = VersionsList.SelectedItem is VersionItem { File.Action: "upload" };
        }
    }

    private void SetBusy(bool busy, string message)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FooterStatus.Text = message;
        SettingsPage.IsEnabled = FilesPage.IsEnabled = !busy;
        CustomBackupSourceBox.IsEnabled = CustomBackupNameBox.IsEnabled = !busy;
        SyncNowButton.IsEnabled = DashboardPauseButton.IsEnabled = !busy && _controller.Settings.IsConfigured;
        RefreshBackups();
        RefreshCustomBackups();
    }

    private void ShowError(Exception error)
    {
        StatusInfoBar.Title = "CloudBay needs your attention";
        StatusInfoBar.Message = error.Message;
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.ActionButton = error.Message.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? CreateWindowsBackupSettingsButton() : null;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowInfo(string message)
    {
        StatusInfoBar.Title = "CloudBay";
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.ActionButton = null;
        StatusInfoBar.IsOpen = true;
    }

    private Button CreateWindowsBackupSettingsButton()
    {
        var button = new Button { Content = "Windows backup settings" };
        button.Click += async (_, _) => await RunAsync("Opening Windows backup settings…", async () =>
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:backup")))
                throw new InvalidOperationException("Open Windows Settings → Accounts → Windows backup to review OneDrive folder protection.");
        });
        return button;
    }

    public async Task RunUiSmokeAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "layout.txt"), "");
        ShowWindow();
        // Allow the first-run Loaded navigation to finish before selecting the
        // first capture page. Otherwise its Settings redirect races Overview.
        await Task.Delay(400);
        // These checks exercise only local controls in the isolated UI-smoke
        // profile. They never register a sync root, set startup, or contact B2.
        var originalCustomSource = CustomBackupSourceBox.Text;
        var originalCustomName = CustomBackupNameBox.Text;
        CustomBackupSourceBox.Text = @"C:\CloudBay UI validation\Personal files";
        CustomBackupNameBox.Text = "Personal files";
        UploadLimitBox.Value = 512;
        ExclusionsBox.Text = "*.ui-validation";
        RootPathBox.Text = @"C:\CloudBay UI validation\CloudBay";
        LoadSettings();
        Refresh();
        if (UploadLimitBox.Value != 512 || ExclusionsBox.Text != "*.ui-validation" ||
            RootPathBox.Text != @"C:\CloudBay UI validation\CloudBay" ||
            CustomBackupSourceBox.Text != @"C:\CloudBay UI validation\Personal files" ||
            CustomBackupNameBox.Text != "Personal files")
            throw new InvalidOperationException("Refreshing the UI discarded an unsaved edit.");
        LoadSettings(reloadAccount: true, reloadPreferences: true);
        CustomBackupSourceBox.Text = originalCustomSource;
        CustomBackupNameBox.Text = originalCustomName;
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            RootGrid.RequestedTheme = theme;
            var suffix = theme == ElementTheme.Light ? "-light" : "";
            foreach (var width in new[] { 1300, 1100, 800 })
            {
                AppWindow.Resize(new SizeInt32(width, 840));
                foreach (var page in new[] { "overview", "activity", "backup", "files", "settings" })
                {
                    Navigation.SelectedItem = page == "settings" ? Navigation.SettingsItem : Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == page);
                    ShowPage(page);
                    await Task.Delay(350);
                    RootGrid.UpdateLayout();
                    if (page != "activity")
                    {
                        var viewer = page switch { "backup" => BackupPage, "files" => FilesPage, "settings" => SettingsPage, _ => OverviewPage };
                        var content = (FrameworkElement)viewer.Content;
                        var position = content.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
                        var viewportPosition = viewer.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
                        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                            $"{theme} {page} {width}: root={RootGrid.ActualWidth}, viewport={viewer.ActualWidth}, content={content.ActualWidth}, x={position.X}{Environment.NewLine}");
                        if (position.X < viewportPosition.X - 1 || position.X + content.ActualWidth > viewportPosition.X + viewer.ActualWidth + 1)
                            throw new InvalidOperationException($"The {page} page extends beyond its visible area at width {width}.");
                    }
                    else
                    {
                        var position = ActivityPage.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
                        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                            $"{theme} activity {width}: root={RootGrid.ActualWidth}, content={ActivityPage.ActualWidth}, x={position.X}{Environment.NewLine}");
                        if (position.X < -1 || position.X + ActivityPage.ActualWidth > RootGrid.ActualWidth + 1)
                            throw new InvalidOperationException($"The activity page extends beyond its visible area at width {width}.");
                    }
                    await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"{page}-{width}{suffix}.png"));
                    if (page == "backup")
                    {
                        BackupPage.ChangeView(null, Math.Min(650, BackupPage.ScrollableHeight), null, true);
                        await Task.Delay(250);
                        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"backup-middle-{width}{suffix}.png"));
                        BackupPage.ChangeView(null, BackupPage.ScrollableHeight, null, true);
                        await Task.Delay(250);
                        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"backup-bottom-{width}{suffix}.png"));
                    }
                    if (page == "settings")
                    {
                        SettingsPage.ChangeView(null, Math.Min(650, SettingsPage.ScrollableHeight), null, true);
                        await Task.Delay(250);
                        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"settings-middle-{width}{suffix}.png"));
                        SettingsPage.ChangeView(null, SettingsPage.ScrollableHeight, null, true);
                        await Task.Delay(250);
                        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"settings-bottom-{width}{suffix}.png"));
                    }
                }
            }
        }
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("overview");
        AppWindow.Resize(new SizeInt32(1100, 840));
        RootGrid.RequestedTheme = ElementTheme.Light;
        await Task.Delay(300);
        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, "overview-light.png"));
        Navigation.SelectedItem = Navigation.SettingsItem;
        ShowPage("settings");
        SettingsPage.ChangeView(null, Math.Min(650, SettingsPage.ScrollableHeight), null, true);
        await Task.Delay(300);
        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, "settings-middle-light.png"));
        Navigation.SelectedItem = Navigation.MenuItems[2];
        ShowPage("backup");
        BackupPage.ChangeView(null, Math.Min(650, BackupPage.ScrollableHeight), null, true);
        await Task.Delay(300);
        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, "backup-middle-light.png"));
        ApplyTheme(_controller.Settings.Theme);
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("overview");
    }
}

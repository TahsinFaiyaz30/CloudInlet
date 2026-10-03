using System.Diagnostics;
using System.Runtime.InteropServices;
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
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;
using ActivityEvent = CloudBay.Core.ActivityEvent;
using ActivityKind = CloudBay.Core.ActivityKind;

namespace CloudBay;

public sealed partial class MainWindow : Window
{
    private readonly ClientController _controller;
    private const double PageColumnWidth = 1120;
    private string _statusStyleKey = "";
    private readonly ClientViewModel _viewModel;
    private readonly Dictionary<string, ToggleSwitch> _backupSwitches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _backupPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _backupAvailability = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _backupMetadataPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _backupDefaultPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SettingsCard> _backupCards = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CommonBackups = new(StringComparer.OrdinalIgnoreCase) { "Desktop", "Documents", "Pictures", "Downloads" };
    private bool _updatingCatalog;
    private AppSettings DisplaySettings => _viewModel.Preview?.Settings ?? _controller.Settings;
    private SyncSnapshot DisplaySnapshot => _viewModel.Preview?.Snapshot ?? _controller.Snapshot;
    private string _customBackupRevision = "";
    private string _backupPresentationRevision = "";
    private int _backupMetadataRevision;
    private string _fileScopesRevision = "";
    private string _protectedFoldersRevision = "";
    private bool _refreshingFileScopes;
    private bool _busy;
    private bool _refreshingBackups;
    private bool _loadingPreferences;
    private bool _closed;
    private int _refreshPending;
    private AppSettings? _loadedSettings;
    private string _versionPath = "";
    private bool? _compactLayout;
    private string _settingsRoute = "home";
    private int _folderIconPixels;
    private Control? _settingsOrigin;
    private readonly UISettings _uiSettings = new();
    private readonly Style? _openFolderAccentStyle;
    private readonly string? _livePagePath = Environment.GetCommandLineArgs().Contains("--ui-live")
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "UiLive", "window-page.txt") : null;

    public bool AllowClose { get; set; }

    public MainWindow(ClientController controller)
    {
        _controller = controller;
        InitializeComponent();
        _openFolderAccentStyle = OpenFolderButton.Style;
        _viewModel = new ClientViewModel(controller);
        RootGrid.DataContext = _viewModel;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = "CloudBay";
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var dpiScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (dpiScale <= 0) dpiScale = 1;
        var margin = (int)Math.Round(32 * dpiScale);
        var initialWidth = Math.Min((int)Math.Round(1360 * dpiScale), workArea.Width - margin);
        var initialHeight = Math.Min((int)Math.Round(900 * dpiScale), workArea.Height - margin);
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - initialWidth) / 2,
            workArea.Y + (workArea.Height - initialHeight) / 2, initialWidth, initialHeight));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += (_, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        AppWindow.Changed += (_, _) =>
        {
            if (_closed || _viewModel is null) return;
            RefreshFolderIconSources();
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
        if (Navigation.SelectedItem is NavigationViewItem { Tag: "backup" }) RefreshBackups(refreshMetadata: true);
        _controller.Changed += Controller_Changed;
        var initialPage = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--page=", StringComparison.Ordinal))?[7..];
        if (initialPage is null && _livePagePath is not null)
        {
            try { if (File.Exists(_livePagePath)) initialPage = File.ReadAllText(_livePagePath).Trim(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Navigation.SelectedItem = initialPage?.StartsWith("settings", StringComparison.Ordinal) == true ? Navigation.SettingsItem :
            Navigation.MenuItems.Cast<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == initialPage) ?? Navigation.MenuItems[0];
        if (initialPage?.StartsWith("settings/", StringComparison.Ordinal) == true) OpenSettingsRoute(initialPage[9..]);
        Navigation.ItemInvoked += (_, args) => { if (args.IsSettingsInvoked) OpenSettingsRoute("home"); };
        RootGrid.KeyDown += (_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.GoBack ||
                (args.Key == global::Windows.System.VirtualKey.Left &&
                 Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Menu).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)))
            {
                if (SettingsPage.Visibility == Visibility.Visible && _settingsRoute != "home") { ReturnToSettingsHome(); args.Handled = true; }
            }
        };
        RootGrid.SizeChanged += (_, _) => UpdateResponsiveLayout();
        OverviewPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        BackupPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        FilesPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        SettingsPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        ActivityPage.SizeChanged += (_, _) => UpdateResponsiveLayout();
        Navigation.DisplayModeChanged += (_, _) => UpdateResponsiveLayout();
        _uiSettings.TextScaleFactorChanged += TextScaleFactor_Changed;
        RootGrid.Loaded += (_, _) => { MeasureNavigationPane(); RefreshFolderIconSources(); };
    }

    public void ShowWindow()
    {
        if (_closed) return;
        if (Navigation.SelectedItem is NavigationViewItem { Tag: "backup" }) RefreshBackups(refreshMetadata: true);
        Refresh();
        AppWindow.Show(!Environment.GetCommandLineArgs().Contains("--ui-smoke"));
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) Activate();
    }

    public void ShowSettings()
    {
        Navigation.SelectedItem = Navigation.SettingsItem;
        ShowPage("settings");
        OpenSettingsRoute("home");
        ShowWindow();
    }

    public void ShowAccount()
    {
        Navigation.SelectedItem = Navigation.SettingsItem;
        ShowPage("settings");
        _settingsOrigin = SettingsAccountAction;
        OpenSettingsRoute("account");
        ShowWindow();
        FocusSettingsAfterLayout(DisplaySettings.IsConfigured ? ApplicationKeyBox : BucketNameBox, "account");
    }

    public void ShowActivity()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowPage("activity");
        ShowWindow();
    }

    public void ShowOverview()
    {
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("overview");
        ShowWindow();
    }

    private void Controller_Changed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            if (!_closed && AppWindow.IsVisible) Refresh();
        })) Interlocked.Exchange(ref _refreshPending, 0);
    }

    private void Refresh()
    {
        if (_closed) return;
        _viewModel.Refresh();
        var settings = DisplaySettings;
        var snapshot = DisplaySnapshot;
        var badgeStyle = snapshot.State switch
        {
            ClientState.UpToDate => "CloudBayStatusSuccessIconStyle",
            ClientState.Attention => "CloudBayStatusCautionIconStyle",
            ClientState.Offline or ClientState.Paused => "CloudBayStatusNeutralIconStyle",
            _ => "CloudBayStatusAccentIconStyle"
        };
        if (_statusStyleKey != badgeStyle)
        {
            _statusStyleKey = badgeStyle;
            OverviewStatusIcon.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[badgeStyle];
        }
        WelcomePanel.Visibility = FilesConnectPanel.Visibility = settings.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        ConnectedOverview.Visibility = FilesConnectedPanel.Visibility = settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        RecentActivitySection.Visibility = settings.IsConfigured && _viewModel.HasActivity ? Visibility.Visible : Visibility.Collapsed;
        ActivityEmpty.Visibility = _viewModel.HasActivity ? Visibility.Collapsed : Visibility.Visible;
        OverviewStatusDetail.Visibility = _viewModel.HasStatusDetail ? Visibility.Visible : Visibility.Collapsed;
        OverviewLastSync.Visibility = _viewModel.HasLastSync ? Visibility.Visible : Visibility.Collapsed;
        ActivityPending.Visibility = _viewModel.HasPending ? Visibility.Visible : Visibility.Collapsed;
        StorageSummary.Visibility = _viewModel.HasStorageSummary ? Visibility.Visible : Visibility.Collapsed;
        BackupSuggestion.Visibility = settings.IsConfigured && settings.Backups.Count + settings.CustomBackups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupConnectInfo.IsOpen = !settings.IsConfigured;
        CancelConnectionButton.Visibility = settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        TransferProgressPanel.Visibility = _viewModel.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        TransferProgress.IsIndeterminate = snapshot.TransferTotalBytes <= 0;
        ReviewDeletionsButton.Visibility = snapshot.State == ClientState.Attention && snapshot.Message.StartsWith("Review required:", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Style = snapshot.State == ClientState.Attention ? null : _openFolderAccentStyle;
        DisconnectButton.Visibility = settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        DisconnectAccountCard.Visibility = settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        StartAtSignInBox.IsEnabled = settings.IsConfigured && !_busy;
        StartupSettingsCard.Description = settings.IsConfigured ? "Start CloudBay automatically when you sign in." : "Connect an account to use automatic startup.";
        SettingsAccountHeading.Text = settings.IsConfigured ? settings.BucketName : "Backblaze B2";
        SettingsAccountAction.Content = settings.IsConfigured ? "Manage account" : "Connect account";
        SettingsAccountCard.Description = settings.IsConfigured ? "Backblaze B2 · Native backup" : "Connect a private bucket to start protecting your files.";
        SyncCategory.Description = settings.FilesOnDemand ? "Files on demand is on · Windows manages downloaded files." : "Keep your files downloaded on this PC.";
        AppearanceCategory.Description = settings.Theme == "System" ? "Use your Windows theme." : $"{settings.Theme} theme";
        OpenFolderButton.IsEnabled = _viewModel.IsConfigured;
        OpenFolderButton.Visibility = SyncNowButton.Visibility = DashboardPauseButton.Visibility = _viewModel.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        SyncNowButton.Visibility = _viewModel.IsConfigured && snapshot.State is not (ClientState.Syncing or ClientState.Connecting or ClientState.Attention) ? Visibility.Visible : Visibility.Collapsed;
        DashboardPauseButton.Visibility = _viewModel.IsConfigured && snapshot.State != ClientState.Attention ? Visibility.Visible : Visibility.Collapsed;
        SyncNowButton.IsEnabled = DashboardPauseButton.IsEnabled = _viewModel.IsConfigured && !_busy;
        RefreshBackups();
        RefreshCustomBackups();
        RefreshFileScopes();
        RefreshProtectedFolders();
        ApplyTheme(settings.Theme);
        if (!ReferenceEquals(_loadedSettings, settings) && !_busy) LoadSettings();
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
        if (page == "backup") RefreshBackups(refreshMetadata: true);
        if (selected is ScrollViewer viewer) viewer.ChangeView(null, 0, null, true);
        if (_livePagePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_livePagePath)!);
                File.WriteAllText(_livePagePath, page == "settings" && _settingsRoute != "home" ? $"settings/{_settingsRoute}" : page);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ApplyTheme(string theme) => RootGrid.RequestedTheme = theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void LoadSettings(bool reloadAccount = false, bool reloadPreferences = false)
    {
        var settings = DisplaySettings;
        var previous = _loadedSettings;
        _loadedSettings = settings;
        // Backup changes, reconnection, and tray quick settings also replace the
        // settings object. Refresh untouched fields while preserving edits the
        // user has made in this window until their own save succeeds.
        if (reloadAccount || previous is null || BucketNameBox.Text == previous.BucketName) BucketNameBox.Text = settings.BucketName;
        if (reloadAccount || previous is null || KeyIdBox.Text == previous.KeyId) KeyIdBox.Text = settings.KeyId;
        if (reloadAccount || previous is null || RootPathBox.Text == previous.RootPath) RootPathBox.Text = settings.RootPath;
        if (reloadAccount || previous is null || PrefixBox.Text == previous.Prefix) PrefixBox.Text = settings.Prefix;
        _loadingPreferences = true;
        try
        {
            if (reloadPreferences || previous is null || FilesOnDemandSwitch.IsOn == previous.FilesOnDemand) FilesOnDemandSwitch.IsOn = settings.FilesOnDemand;
            if (reloadPreferences || previous is null || NumberTextMatches(UploadLimitBox, previous.UploadBytesPerSecond / 1024d)) UploadLimitBox.Value = settings.UploadBytesPerSecond / 1024d;
            if (reloadPreferences || previous is null || NumberTextMatches(DownloadLimitBox, previous.DownloadBytesPerSecond / 1024d)) DownloadLimitBox.Value = settings.DownloadBytesPerSecond / 1024d;
            if (reloadPreferences || previous is null || NumberTextMatches(ConcurrencyBox, previous.UploadConcurrency)) ConcurrencyBox.Value = settings.UploadConcurrency;
            if (reloadPreferences || previous is null || MeteredBox.IsOn == previous.PauseOnMetered) MeteredBox.IsOn = settings.PauseOnMetered;
            if (reloadPreferences || previous is null || BatterySaverBox.IsOn == previous.PauseOnBatterySaver) BatterySaverBox.IsOn = settings.PauseOnBatterySaver;
            if (reloadPreferences || previous is null || StartAtSignInBox.IsOn == previous.StartAtSignIn) StartAtSignInBox.IsOn = settings.StartAtSignIn;
            if (reloadPreferences || previous is null || ExclusionsBox.Text == string.Join(Environment.NewLine, previous.Exclusions)) ExclusionsBox.Text = string.Join(Environment.NewLine, settings.Exclusions);
            if (reloadPreferences || previous is null || (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string == previous.Theme)
                ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == settings.Theme) ?? ThemeBox.Items[0];
        }
        finally { _loadingPreferences = false; }
        AccountHeading.Text = "Backblaze B2";
        AccountDescription.Text = settings.IsConfigured ? "Replace your application key or review connection options." : "Enter your private bucket and application key.";
        ConnectButton.Content = settings.IsConfigured ? "Update connection" : "Connect account";
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
            if (viewer.ActualWidth > 0) content.Width = Math.Min(PageColumnWidth, viewer.ActualWidth);
        if (ContentLayoutGrid.ActualWidth > 0) ActivityPage.Width = Math.Min(PageColumnWidth, ContentLayoutGrid.ActualWidth);
        ArrangeTiles(SettingsCategories, SettingsContent.Width - 64 >= 660 * _uiSettings.TextScaleFactor ? 2 : 1);
        if (SettingsContent.Width > 64) ConnectionPanel.Width = Math.Min(720, SettingsContent.Width - 64);
        var pane = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded ? Navigation.OpenPaneLength :
            Navigation.DisplayMode == NavigationViewDisplayMode.Compact ? Navigation.CompactPaneLength : 0;
        var compact = Navigation.ActualWidth - pane < 670;
        var backupColumns = BackupContent.Width - 64 >= 660 * _uiSettings.TextScaleFactor ? 2 : 1;
        ArrangeTiles(BackupRows, backupColumns);
        ArrangeTiles(MoreBackupRows, backupColumns);
        ArrangeTiles(CustomBackupRows, BackupContent.Width - 64 >= 760 * _uiSettings.TextScaleFactor ? 2 : 1);
        UpdateOverviewLayout();
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        DashboardActions.Orientation = FileActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
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
        RefreshBackupMetadata();
        foreach (var name in KnownFolderBackup.FolderIds.Keys)
        {
            var glyph = name switch
            {
                "Desktop" => "\uE7F4", "Documents" => "\uE8A5", "Pictures" => "\uEB9F",
                "Music" => "\uE8D6", "Videos" => "\uE714", "Downloads" => "\uE896",
                "Favorites" => "\uE734", "Contacts" => "\uE77B", "Saved Games" => "\uE7FC",
                "Links" => "\uE71B", "Searches" => "\uE721", _ => "\uE8B7"
            };
            var description = _backupMetadataPaths[name];
            var path = new TextBlock { Text = description, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBaySecondaryTextStyle"],
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(path, description);
            var toggle = new ToggleSwitch { Tag = name, OnContent = "", OffContent = "", Width = 44, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayToggleStyle"] };
            AutomationProperties.SetName(toggle, $"Back up {name}");
            toggle.Toggled += Backup_Toggled;
            var folderIcon = FolderIconProvider.GetIcon(name, IconPixels(40));
            var card = new SettingsCard
            {
                Header = new TextBlock { Text = name, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis },
                Description = path,
                HeaderIcon = folderIcon is not null ? new ImageIcon { Source = folderIcon, Width = 40, Height = 40 } : new FontIcon { Glyph = glyph, FontSize = 32 },
                Content = toggle,
                MinHeight = 112,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(20)
            };
            card.Resources["SettingsCardHeaderIconMaxSize"] = 40d;
            card.Resources["SettingsCardWrapThreshold"] = 0d;
            if (CommonBackups.Contains(name)) BackupRows.Children.Add(card);
            else MoreBackupRows.Children.Add(card);
            _backupCards.Add(name, card);
            _backupSwitches.Add(name, toggle);
            _backupPaths.Add(name, path);
        }
    }

    private void RefreshBackupMetadata()
    {
        foreach (var name in KnownFolderBackup.FolderIds.Keys)
        {
            try
            {
                _backupMetadataPaths[name] = KnownFolderBackup.GetPath(name);
                _backupDefaultPaths[name] = KnownFolderBackup.GetDefaultPath(name);
                _backupAvailability[name] = KnownFolderBackup.GetRestriction(name);
            }
            catch
            {
                _backupMetadataPaths[name] = "This Windows folder is unavailable on this device";
                _backupDefaultPaths[name] = "";
                _backupAvailability[name] = _backupMetadataPaths[name];
            }
        }
        _backupMetadataRevision++;
    }

    private void RefreshBackups(bool refreshMetadata = false)
    {
        if (refreshMetadata) RefreshBackupMetadata();
        var settings = DisplaySettings;
        var revision = $"{settings.RootPath}|{settings.IsConfigured}|{_busy}|{_backupMetadataRevision}|" +
            string.Join('|', settings.Backups.Select(folder => $"{folder.Name}:{folder.DestinationPath}")) + "|" +
            string.Join('|', _viewModel.Preview?.WindowsFolderPaths?.Select(folder => $"{folder.Key}:{folder.Value}") ?? []);
        if (_backupPresentationRevision == revision) return;
        _backupPresentationRevision = revision;
        _refreshingBackups = true;
        try
        {
            foreach (var (name, toggle) in _backupSwitches)
            {
                var backup = DisplaySettings.Backups.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var card = _backupCards[name];
                var primary = CommonBackups.Contains(name) || backup is not null;
                if (primary != BackupRows.Children.Contains(card))
                {
                    // An unexpanded Expander's content has no visual Parent,
                    // but its collection still owns the card. Remove from the
                    // owning collection before promoting an enabled folder.
                    BackupRows.Children.Remove(card);
                    MoreBackupRows.Children.Remove(card);
                    if (primary) BackupRows.Children.Add(card);
                    else MoreBackupRows.Children.Add(card);
                }
                var currentPath = _viewModel.Preview is not null ? _viewModel.Preview.WindowsFolderPaths?.GetValueOrDefault(name) ??
                    backup?.DestinationPath ?? _backupDefaultPaths[name] : _backupMetadataPaths[name];
                var notice = _viewModel.Preview is not null ? null : _backupAvailability[name];
                var ownsMapping = backup is not null && SameFolderPath(currentPath, backup.DestinationPath);
                var changedMapping = backup is not null && !ownsMapping;
                var externalMapping = backup is null && notice is null && !SameFolderPath(currentPath, _backupDefaultPaths[name]);
                if (changedMapping) notice = "Windows folder location changed. CloudBay will not overwrite another app's mapping. Restore this folder to its CloudBay location before stopping backup.";
                else if (externalMapping) notice = "This folder is redirected by Windows, OneDrive, or another app. Restore it to its default location before enabling CloudBay backup.";
                toggle.IsOn = ownsMapping;
                toggle.IsEnabled = settings.IsConfigured && !_busy && notice is null;
                var caption = _backupPaths[name];
                caption.Text = changedMapping ? "Windows folder location changed" : externalMapping ? "Managed by another app" : notice is not null ? "Unavailable for backup" : "";
                caption.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
                caption.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayAttentionTextStyle"];
                var tooltip = currentPath + (notice is not null ? Environment.NewLine + notice : "");
                ToolTipService.SetToolTip(card, tooltip);
                AutomationProperties.SetHelpText(toggle, tooltip);
            }
        }
        finally { _refreshingBackups = false; }
        UpdateResponsiveLayout();
    }

    private void RefreshCustomBackups()
    {
        var settings = DisplaySettings;
        var revision = string.Join("|", settings.CustomBackups.Select(folder => $"{folder.Name}:{folder.SourcePath}:{folder.Prefix}")) + $"|{_busy}";
        AddCustomBackupButton.IsEnabled = settings.IsConfigured && !_busy;
        CustomBackupSection.Visibility = settings.CustomBackups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_customBackupRevision == revision) return;
        _customBackupRevision = revision;
        CustomBackupRows.Children.Clear();
        foreach (var folder in settings.CustomBackups)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var open = new Button { Content = "Open folder", Tag = folder.SourcePath };
            open.Click += (_, _) =>
            {
                if (_viewModel.Preview is not null) return;
                try { Process.Start(new ProcessStartInfo(folder.SourcePath) { UseShellExecute = true }); }
                catch (Exception error) { ShowError(error); }
            };
            actions.Children.Add(open);
            var stop = new MenuFlyoutItem { Text = "Stop backup", Tag = folder.Name, IsEnabled = !_busy, Icon = new FontIcon { Glyph = "\uE711" } };
            stop.Click += RemoveCustomBackup_Click;
            var menu = new MenuFlyout();
            menu.Items.Add(stop);
            var more = new Button { Content = new FontIcon { Glyph = "\uE712" }, Flyout = menu, IsEnabled = !_busy };
            AutomationProperties.SetName(more, $"More options for {folder.Name}");
            ToolTipService.SetToolTip(more, "More options");
            actions.Children.Add(more);
            CustomBackupRows.Children.Add(new SettingsCard
            {
                Header = new TextBlock { Text = folder.Name, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] },
                HeaderIcon = FolderIconProvider.GetCustomFolderIcon(IconPixels(40)) is { } folderIcon
                    ? new ImageIcon { Source = folderIcon, Width = 40, Height = 40 } : new FontIcon { Glyph = "\uE8B7" },
                Content = actions,
                MinHeight = 112,
                Padding = new Thickness(20)
            });
            ToolTipService.SetToolTip(CustomBackupRows.Children[^1], folder.SourcePath);
            ((SettingsCard)CustomBackupRows.Children[^1]).Resources["SettingsCardHeaderIconMaxSize"] = 40d;
        }
        UpdateResponsiveLayout();
    }

    private void RefreshProtectedFolders()
    {
        var settings = DisplaySettings;
        var revision = $"{settings.IsConfigured}|{_busy}|" + string.Join('|', settings.Backups.Select(folder => $"{folder.Name}:{folder.DestinationPath}")) +
            string.Join('|', settings.CustomBackups.Select(folder => $"{folder.Name}:{folder.SourcePath}"));
        ProtectedFoldersSection.Visibility = settings.IsConfigured && settings.Backups.Count + settings.CustomBackups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_protectedFoldersRevision == revision) { UpdateOverviewLayout(); return; }
        _protectedFoldersRevision = revision;
        ProtectedFolderRows.Children.Clear();
        var folders = settings.Backups.Select(folder => new ProtectedFolderLink(folder.Name, false))
            .Concat(settings.CustomBackups.Select(folder => new ProtectedFolderLink(folder.Name, true))).Take(6);
        foreach (var folder in folders)
        {
            var contents = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var source = folder.Custom ? FolderIconProvider.GetCustomFolderIcon(IconPixels(40)) : FolderIconProvider.GetIcon(folder.Name, IconPixels(40));
            contents.Children.Add(source is not null ? new Image { Source = source, Width = 40, Height = 40 } : new FontIcon { Glyph = "\uE8B7", FontSize = 32 });
            contents.Children.Add(new TextBlock { Text = folder.Name, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button
            {
                Content = contents, Tag = folder, Padding = new Thickness(12), MinHeight = 104, IsEnabled = !_busy,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(button, $"Open {folder.Name} in File Explorer");
            ToolTipService.SetToolTip(button, folder.Name);
            button.Click += ProtectedFolder_Click;
            ProtectedFolderRows.Children.Add(button);
        }
        UpdateOverviewLayout();
    }

    private void ProtectedFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _viewModel.Preview is not null || sender is not FrameworkElement { Tag: ProtectedFolderLink folder }) return;
        try
        {
            // Resolve against the latest settings; the folder list can change
            // after the shortcut was rendered. Opening does not hydrate files.
            var path = folder.Custom ? _controller.Settings.CustomBackups.FirstOrDefault(item => item.Name == folder.Name)?.SourcePath :
                _controller.Settings.Backups.FirstOrDefault(item => item.Name == folder.Name)?.DestinationPath;
            if (path is null) throw new InvalidOperationException("This folder is no longer backed up. Review Folder backup to choose a current folder.");
            SystemIntegration.OpenFolder(path);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void UpdateOverviewLayout()
    {
        var innerWidth = OverviewContent.Width - 64;
        if (!double.IsFinite(innerWidth) || innerWidth <= 0) return;
        var protectedVisible = ProtectedFoldersSection.Visibility == Visibility.Visible;
        var recentVisible = RecentActivitySection.Visibility == Visibility.Visible;
        var wide = innerWidth >= 820 * _uiSettings.TextScaleFactor && protectedVisible && recentVisible;
        OverviewSections.ColumnSpacing = wide ? 24 : 0;
        OverviewSections.ColumnDefinitions[0].Width = new GridLength(wide ? 3 : 1, GridUnitType.Star);
        OverviewSections.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(RecentActivitySection, wide ? 1 : 0);
        Grid.SetRow(RecentActivitySection, !wide && protectedVisible ? 1 : 0);
        var protectedWidth = wide ? (innerWidth - 24) * 3 / 5 : innerWidth;
        ArrangeTiles(ProtectedFolderRows, Math.Clamp((int)((protectedWidth - 48) / (128 * _uiSettings.TextScaleFactor)), 1, 3));
    }

    private sealed record ProtectedFolderLink(string Name, bool Custom);

    private int IconPixels(double logicalSize) => Math.Clamp((int)Math.Ceiling(logicalSize * GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d), 16, 128);

    private void RefreshFolderIconSources()
    {
        var pixels = IconPixels(40);
        if (_folderIconPixels == pixels) return;
        _folderIconPixels = pixels;
        foreach (var (name, card) in _backupCards)
            if (FolderIconProvider.GetIcon(name, pixels) is { } source) card.HeaderIcon = new ImageIcon { Source = source, Width = 40, Height = 40 };
        foreach (var card in CustomBackupRows.Children.OfType<SettingsCard>())
            if (FolderIconProvider.GetCustomFolderIcon(pixels) is { } source) card.HeaderIcon = new ImageIcon { Source = source, Width = 40, Height = 40 };
        foreach (var button in ProtectedFolderRows.Children.OfType<Button>())
            if (button.Tag is ProtectedFolderLink folder && button.Content is StackPanel panel && panel.Children[0] is Image image)
                image.Source = folder.Custom ? FolderIconProvider.GetCustomFolderIcon(pixels) : FolderIconProvider.GetIcon(folder.Name, pixels);
        UpdateFileScopeLabels();
    }

    private static bool SameFolderPath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

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
        var settings = DisplaySettings;
        FileScopeBox.Visibility = settings.CustomBackups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        FileLocationCard.HeaderIcon = FolderIconProvider.GetCustomFolderIcon(IconPixels(48)) is { } folderIcon
            ? new ImageIcon { Source = folderIcon, Width = 48, Height = 48 } : new FontIcon { Glyph = "\uE8B7", FontSize = 40 };
    }

    private void OpenFileFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
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
            if (name.Length == 0 || name.Length > 64 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
                throw new InvalidOperationException("Choose a backup name up to 64 characters without slashes or Windows filename symbols.");
            await _controller.AddCustomBackupAsync(source, name);
            CustomBackupSourceBox.Text = CustomBackupNameBox.Text = "";
            ShowInfo($"{name} is now backed up. Its folder remains in its current location.");
        });

    private async void RemoveCustomBackup_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not FrameworkElement { Tag: string name }) return;
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
        RefreshBackups(refreshMetadata: true);
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

    private async void Preference_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingPreferences || _busy || _viewModel is null || _viewModel.Preview is not null) return;
        var update = sender switch
        {
            ToggleSwitch toggle when ReferenceEquals(toggle, FilesOnDemandSwitch) => new PreferenceUpdate { FilesOnDemand = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, MeteredBox) => new PreferenceUpdate { PauseOnMetered = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, BatterySaverBox) => new PreferenceUpdate { PauseOnBatterySaver = toggle.IsOn },
            ToggleSwitch toggle when ReferenceEquals(toggle, StartAtSignInBox) => new PreferenceUpdate { StartAtSignIn = toggle.IsOn },
            _ => null
        };
        if (update is not null) await SavePreferenceAsync(update);
    }

    private async void Theme_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingPreferences || _busy || _viewModel is null || _viewModel.Preview is not null || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        ApplyTheme(theme);
        await SavePreferenceAsync(new() { Theme = theme });
    }

    private async Task SavePreferenceAsync(PreferenceUpdate update)
    {
        await RunAsync("Applying your preference…", async () =>
        {
            await _controller.UpdatePreferencesAsync(update);
        });
        // A failed immediate toggle returns to its actual persisted value. Keep
        // numeric and exclusion drafts untouched, including invalid input.
        _loadingPreferences = true;
        try
        {
            FilesOnDemandSwitch.IsOn = DisplaySettings.FilesOnDemand;
            MeteredBox.IsOn = DisplaySettings.PauseOnMetered;
            BatterySaverBox.IsOn = DisplaySettings.PauseOnBatterySaver;
            StartAtSignInBox.IsOn = DisplaySettings.StartAtSignIn;
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == DisplaySettings.Theme);
            ApplyTheme(DisplaySettings.Theme);
        }
        finally { _loadingPreferences = false; }
    }

    private async void ApplyNetwork_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Applying bandwidth limits…", async () =>
        {
            var upload = ReadWholeNumber(UploadLimitBox, "Upload limit", 0, 1048576);
            var download = ReadWholeNumber(DownloadLimitBox, "Download limit", 0, 1048576);
            var concurrency = ReadWholeNumber(ConcurrencyBox, "Concurrent uploads", 1, 16);
            await _controller.UpdatePreferencesAsync(new()
            {
                UploadBytesPerSecond = upload * 1024L,
                DownloadBytesPerSecond = download * 1024L,
                UploadConcurrency = concurrency
            });
        }, "Bandwidth limits are applied.");

    private async void ApplyExclusions_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Applying excluded files…", async () =>
        {
            var exclusions = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await _controller.UpdatePreferencesAsync(new() { Exclusions = exclusions });
        }, "File exclusions are applied.");

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
            RefreshBackups(refreshMetadata: true);
            ShowInfo("The account is disconnected. Your local files and B2 files were retained.");
        });
    }

    private static int ReadWholeNumber(NumberBox box, string name, int min, int max)
    {
        if (!double.TryParse(box.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var value) ||
            !double.IsFinite(value) || value < min || value > max || value != Math.Truncate(value))
            throw new InvalidOperationException($"{name} must be a whole number between {min:N0} and {max:N0}.");
        return (int)value;
    }

    private static bool NumberTextMatches(NumberBox box, double value) =>
        double.TryParse(box.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var displayed) && displayed == value;

    private async void SyncNow_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Checking for file changes…", () => _controller.SyncNowAsync());

    private void Pause_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _viewModel.Preview is not null) return;
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
        if (_viewModel.Preview is not null) return;
        try { _controller.LaunchFolder(); }
        catch (Exception error) { ShowError(error); }
    }

    private void ManageBackup_Click(object sender, RoutedEventArgs args) => Navigation.SelectedItem = Navigation.MenuItems[2];
    private void ViewActivity_Click(object sender, RoutedEventArgs args) => ShowActivity();
    private void Settings_Click(object sender, RoutedEventArgs args) => ShowAccount();

    private void SettingsAccount_Click(object sender, RoutedEventArgs args) => Settings_Click(sender, args);

    private void SyncSettings_Click(object sender, RoutedEventArgs args)
    {
        ShowSettings();
        _settingsOrigin = SyncCategory;
        OpenSettingsRoute("sync");
        FocusSettingsAfterLayout(SettingsBackButton, "sync");
    }

    private async void SettingsCategory_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string route } element) return;
        if (route == "catalog") { await OpenCatalogAsync(); return; }
        _settingsOrigin = element as Control;
        OpenSettingsRoute(route);
        FocusSettingsAfterLayout(SettingsBackButton, route);
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs args) => ReturnToSettingsHome();

    private void ReturnToSettingsHome()
    {
        OpenSettingsRoute("home");
        if (_settingsOrigin is { } origin) FocusSettingsAfterLayout(origin, "home");
    }

    private void FocusSettingsAfterLayout(Control target, string route)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Visibility changes must be arranged before native focus can move
            // to a newly exposed control. A later navigation cancels this work.
            if (_closed || _settingsRoute != route || SettingsPage.Visibility != Visibility.Visible) return;
            if (route == "home" && !ReferenceEquals(_settingsOrigin, target)) return;
            RootGrid.UpdateLayout();
            target.Focus(FocusState.Programmatic);
        });
    }

    private void OpenSettingsRoute(string route)
    {
        var routes = new Dictionary<string, (FrameworkElement Panel, string Title)>(StringComparer.Ordinal)
        {
            ["account"] = (AccountSettingsDetail, "Account"), ["sync"] = (SyncSettingsDetail, "Sync"),
            ["network"] = (NetworkSettingsDetail, "Network and power"), ["appearance"] = (AppearanceSettingsDetail, "Appearance"),
            ["general"] = (GeneralSettingsDetail, "General"), ["about"] = (AboutSettingsDetail, "About CloudBay")
        };
        _settingsRoute = routes.ContainsKey(route) ? route : "home";
        foreach (var item in routes.Values) item.Panel.Visibility = Visibility.Collapsed;
        SettingsHub.Visibility = _settingsRoute == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsDetail.Visibility = _settingsRoute == "home" ? Visibility.Collapsed : Visibility.Visible;
        if (routes.TryGetValue(_settingsRoute, out var detail))
        {
            detail.Panel.Visibility = Visibility.Visible;
            SettingsDetailTitle.Text = detail.Title;
        }
        if (SettingsPage.Visibility == Visibility.Visible) ShowPage("settings");
    }

    private void CancelConnection_Click(object sender, RoutedEventArgs args)
    {
        ApplicationKeyBox.Password = "";
        LoadSettings(reloadAccount: true);
        Refresh();
        ReturnToSettingsHome();
    }

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
        if (_viewModel.Preview is not null) return;
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
        if (_viewModel.Preview is not null) return;
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
        BusyFooter.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FooterStatus.Text = message;
        SettingsPage.IsEnabled = FilesPage.IsEnabled = !busy;
        CustomBackupSourceBox.IsEnabled = CustomBackupNameBox.IsEnabled = !busy;
        SyncNowButton.IsEnabled = DashboardPauseButton.IsEnabled = !busy && _controller.Settings.IsConfigured;
        RefreshBackups();
        RefreshCustomBackups();
    }

    private async void Catalog_Click(object sender, RoutedEventArgs args) => await OpenCatalogAsync();

    private async Task OpenCatalogAsync()
    {
        if (_busy) return;
        CatalogDialog.XamlRoot = RootGrid.XamlRoot;
        CatalogDialog.RequestedTheme = RootGrid.RequestedTheme;
        UpdateCatalog(resetCategories: true);
        await CatalogDialog.ShowAsync();
    }

    private void CatalogFilter_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (CatalogList is null || _updatingCatalog) return;
        UpdateCatalog(resetCategories: ReferenceEquals(sender, CatalogKind));
    }

    private void CatalogSearch_Changed(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (CatalogList is not null) UpdateCatalog();
    }

    private void UpdateCatalog(bool resetCategories = false)
    {
        var modes = CatalogKind.SelectedIndex == 1;
        var options = modes ? ProductCatalog.Modes : ProductCatalog.Providers;
        _updatingCatalog = true;
        try
        {
            if (resetCategories || CatalogGroup.ItemsSource is null)
            {
                CatalogGroup.ItemsSource = new[] { "All categories" }.Concat(options.Select(item => item.Group).Distinct()).ToArray();
                CatalogGroup.SelectedIndex = 0;
            }
            CatalogGroup.Visibility = modes ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(CatalogKind, modes ? 2 : 1);
            CatalogSearch.PlaceholderText = modes ? "Search modes" : "Search providers";
            var category = CatalogGroup.SelectedItem as string;
            var search = CatalogSearch.Text.Trim();
            var matches = options.Where(item => (category is null or "All categories" || item.Group == category) &&
                (search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Description.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Group.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
            CatalogList.ItemsSource = matches;
            CatalogEmpty.Text = modes ? "No matching modes" : "No matching providers";
            CatalogEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingCatalog = false; }
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
        var themeArgument = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--ui-smoke-theme=", StringComparison.Ordinal))?[17..];
        if (themeArgument != "Light")
        {
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "layout.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "capture-metrics.txt"), "");
        }
        ShowWindow();
        await Task.Delay(300);
        RootGrid.UpdateLayout();
        var title = FindDescendant<TextBlock>(AppTitleBar, item => item.Text == "CloudBay");
        if (AppTitleBar.Title != "CloudBay" || title is null || title.ActualWidth <= 0 || title.ActualHeight <= 0)
            throw new InvalidOperationException("The native title bar brand must be visible and laid out.");
        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
            $"native-title: title={title.Text}, bounds={title.ActualWidth:0.##}x{title.ActualHeight:0.##}; native Mica/compositor window frame is outside RenderTargetBitmap{Environment.NewLine}");
        // Fixtures affect presentation only. This isolated process never writes
        // account settings, registers Windows roots, or connects to a provider.
        CustomBackupSourceBox.Text = @"C:\CloudBay UI validation\Personal files";
        CustomBackupNameBox.Text = "Personal files";
        UploadLimitBox.Value = 512;
        ExclusionsBox.Text = "*.ui-validation";
        RootPathBox.Text = @"C:\CloudBay UI validation\CloudBay";
        _viewModel.SetPreview(ClientPreview.Connected());
        Refresh();
        if (UploadLimitBox.Value != 512 || ExclusionsBox.Text != "*.ui-validation" ||
            RootPathBox.Text != @"C:\CloudBay UI validation\CloudBay" ||
            CustomBackupSourceBox.Text != @"C:\CloudBay UI validation\Personal files" ||
            CustomBackupNameBox.Text != "Personal files")
            throw new InvalidOperationException("A background settings refresh discarded an unsaved edit.");
        UploadLimitBox.Text = "invalid draft";
        _viewModel.SetPreview(ClientPreview.Connected() with { Settings = ClientPreview.Connected().Settings with { Theme = "Light" } });
        Refresh();
        if (UploadLimitBox.Text != "invalid draft") throw new InvalidOperationException("A refresh discarded an invalid numeric draft.");
        try
        {
            ReadWholeNumber(UploadLimitBox, "Upload limit", 0, 1048576);
            throw new InvalidOperationException("An invalid numeric draft was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Upload limit must", StringComparison.Ordinal)) { }
        ShowPage("settings");
        SettingsCategory_Click(SyncCategory, new RoutedEventArgs());
        await Task.Delay(80);
        if (SettingsHub.Visibility != Visibility.Collapsed || SyncSettingsDetail.Visibility != Visibility.Visible ||
            AccountSettingsDetail.Visibility != Visibility.Collapsed || UploadLimitBox.Text != "invalid draft" || ExclusionsBox.Text != "*.ui-validation")
            throw new InvalidOperationException("Opening a settings category must retain drafts and expose only its focused detail.");
        ReturnToSettingsHome();
        await WaitForUiAsync(() => SyncCategory.FocusState != FocusState.Unfocused,
            "Back must restore the originating category's keyboard focus.");
        if (SettingsHub.Visibility != Visibility.Visible || SettingsDetail.Visibility != Visibility.Collapsed ||
            SyncCategory.FocusState == FocusState.Unfocused || UploadLimitBox.Text != "invalid draft")
            throw new InvalidOperationException("Back must restore the category, keyboard focus, and unsaved drafts.");
        ShowAccount();
        if (AccountSettingsDetail.Visibility != Visibility.Visible || SettingsHub.Visibility != Visibility.Collapsed || UploadLimitBox.Text != "invalid draft")
            throw new InvalidOperationException("Connect account must open Account directly and preserve other drafts.");
        await WaitForUiAsync(() => ApplicationKeyBox.FocusState != FocusState.Unfocused,
            "Manage account must open and focus the application key editor directly.");
        if (ConnectionPanel.Visibility != Visibility.Visible || UploadLimitBox.Text != "invalid draft")
            throw new InvalidOperationException("Managing the account must retain other preference drafts.");
        Refresh();
        ReturnToSettingsHome();
        await WaitForUiAsync(() => SettingsAccountAction.FocusState != FocusState.Unfocused,
            "Back from a direct Account action must restore its home action focus.");
        ShowSettings();
        if (SettingsHub.Visibility != Visibility.Visible || SettingsDetail.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("All settings must open the Settings home.");
        CustomBackupSourceBox.Text = CustomBackupNameBox.Text = "";
        _viewModel.SetPreview(null);
        LoadSettings(reloadAccount: true, reloadPreferences: true);
        Refresh();

        var states = new[] { ClientState.NotConnected, ClientState.Connecting, ClientState.UpToDate, ClientState.Syncing,
            ClientState.Paused, ClientState.Offline, ClientState.Attention };
        var themes = themeArgument == "Light" ? new[] { ElementTheme.Light } : new[] { ElementTheme.Dark };
        foreach (var theme in themes)
        {
            var suffix = theme == ElementTheme.Light ? "-light" : "";
            foreach (var width in new[] { 1300, 1100, 800 })
            {
                AppWindow.Resize(new SizeInt32(width, 840));
                foreach (var state in states)
                {
                    SetPresentation(ClientPreview.ForState(state), theme);
                    ValidatePresentation(state);
                    await CapturePageAsync("overview", $"overview-{state}-{width}{suffix}");
                }
                SetPresentation(ClientPreview.Connected(), theme);
                if (BackupRows.Children.Count != 5 || BackupRows.Children.Count + MoreBackupRows.Children.Count != KnownFolderBackup.FolderIds.Count)
                    throw new InvalidOperationException("Common and enabled folders must remain visible, with all other Windows folders accessible.");
                foreach (var page in new[] { "activity", "backup", "files", "settings" })
                    await CapturePageAsync(page, $"{page}-{width}{suffix}");
            var longHistory = Enumerable.Range(0, 180).Select(index => new ActivityEvent(DateTimeOffset.UtcNow.AddMinutes(-index),
                    ActivityKind.Upload, $"Documents/Project {index:D3}/A document with a longer file name {index:D3}.docx", "Uploaded", 184320)).ToArray();
                SetPresentation(ClientPreview.Connected() with { Activity = longHistory }, theme);
                await CapturePageAsync("activity", $"activity-history-{width}{suffix}");
                ActivityList.ScrollIntoView(_viewModel.Activity[^1]);
                await Task.Delay(250);
                var activityScroller = FindDescendant<ScrollViewer>(ActivityList);
                if (activityScroller is null || activityScroller.ScrollableHeight <= 0 || activityScroller.VerticalOffset <= 0)
                    throw new InvalidOperationException("A long activity history must allow the oldest item to be reached.");
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"activity-history-bottom-{width}{suffix}.png"));
                SetPresentation(ClientPreview.Connected(), theme);
                MoreWindowsFolders.IsExpanded = true;
                await CapturePageAsync("backup", $"backup-all-folders-{width}{suffix}");
                BackupPage.ChangeView(null, BackupPage.ScrollableHeight, null, true);
                await Task.Delay(180);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"backup-bottom-{width}{suffix}.png"));
                MoreWindowsFolders.IsExpanded = false;
                CustomBackupExpander.IsExpanded = true;
                await CapturePageAsync("backup", $"backup-custom-{width}{suffix}");
                BackupPage.ChangeView(null, BackupPage.ScrollableHeight, null, true);
                await Task.Delay(180);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"backup-custom-bottom-{width}{suffix}.png"));
                CustomBackupExpander.IsExpanded = false;
                foreach (var route in new[] { "account", "sync", "network", "appearance", "general", "about" })
                    await CapturePageAsync("settings", $"settings-{route}-{width}{suffix}", route);
                NetworkSettingsExpander.IsExpanded = ExclusionsExpander.IsExpanded = true;
                await CapturePageAsync("settings", $"settings-sync-expanded-{width}{suffix}", "sync");
                SettingsPage.ChangeView(null, SettingsPage.ScrollableHeight, null, true);
                await Task.Delay(180);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"settings-sync-bottom-{width}{suffix}.png"));
                await CapturePageAsync("settings", $"settings-network-expanded-{width}{suffix}", "network");
                SettingsPage.ChangeView(null, SettingsPage.ScrollableHeight, null, true);
                await Task.Delay(180);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"settings-network-bottom-{width}{suffix}.png"));
                NetworkSettingsExpander.IsExpanded = ExclusionsExpander.IsExpanded = false;
                SetPresentation(ClientPreview.ForState(ClientState.NotConnected), theme);
                await CapturePageAsync("settings", $"settings-connect-{width}{suffix}", "account");
                await CapturePageAsync("settings", $"settings-home-disconnected-{width}{suffix}");
            }
            AppWindow.Resize(new SizeInt32(800, 840));
            var externalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Another provider", "Desktop");
            SetPresentation(ClientPreview.Connected() with { WindowsFolderPaths = new Dictionary<string, string>
            {
                ["Desktop"] = externalPath,
                ["Documents"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Another provider", "Documents")
            } }, theme);
            await CapturePageAsync("backup", $"backup-location-attention-800{suffix}");
            if (_backupSwitches["Desktop"].IsOn || _backupSwitches["Desktop"].IsEnabled || _backupSwitches["Documents"].IsEnabled ||
                _backupPaths["Desktop"].Text != "Windows folder location changed" || _backupPaths["Documents"].Text != "Managed by another app" ||
                !AutomationProperties.GetHelpText(_backupSwitches["Desktop"]).Contains(externalPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Changed or externally managed Windows mappings must show their actual location and cannot advertise a protected mapping.");
            AppWindow.Resize(new SizeInt32(600, 840));
            SetPresentation(ClientPreview.Connected(), theme);
            await CapturePageAsync("overview", $"overview-minimal-600{suffix}");
            await CapturePageAsync("settings", $"settings-minimal-600{suffix}");
            AppWindow.Resize(new SizeInt32(1100, 840));
            SetPresentation(ClientPreview.Connected() with { Activity = [] }, theme);
            await CapturePageAsync("overview", $"overview-quiet-1100{suffix}");
            if (RecentActivitySection.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("An empty activity history must not take space on Overview.");
            SetBusy(true, "Preparing your files before disconnecting…");
            await CapturePageAsync("settings", $"settings-busy-1100{suffix}");
            if (BusyFooter.Visibility != Visibility.Visible || !BusyRing.IsActive)
                throw new InvalidOperationException("A running operation must expose its progress status.");
            SetBusy(false, "");
            SetPresentation(ClientPreview.Connected(), theme);
            await CapturePageAsync("settings", $"settings-catalog{suffix}");
            CatalogDialog.XamlRoot = RootGrid.XamlRoot;
            CatalogDialog.RequestedTheme = theme;
            CatalogKind.SelectedIndex = 0;
            CatalogSearch.Text = "";
            UpdateCatalog(resetCategories: true);
            if (((ProductOption[])CatalogList.ItemsSource).Length != ProductCatalog.Providers.Count ||
                ProductCatalog.Providers.Count(item => item.Available) != 1 || ProductCatalog.Modes.Count(item => item.Available) != 1)
                throw new InvalidOperationException("The catalog must expose every planned provider and only the implemented options as available.");
            // InPlace keeps the real dialog template in the window's visual
            // tree. RenderTargetBitmap cannot capture popup-hosted content.
            var dialogTask = CatalogDialog.ShowAsync(ContentDialogPlacement.InPlace);
            await Task.Delay(300);
            await CaptureCatalogAsync($"catalog-providers{suffix}");
            CatalogSearch.Text = "Amazon";
            await WaitForUiAsync(() => ((ProductOption[])CatalogList.ItemsSource) is [{ Id: "aws-s3", Available: false }], "Catalog search returned an incorrect provider or availability.");
            await Task.Delay(180);
            await CaptureCatalogAsync($"catalog-search{suffix}");
            CatalogKind.SelectedIndex = 1;
            CatalogSearch.Text = "";
            await WaitForUiAsync(() => ((ProductOption[])CatalogList.ItemsSource).SequenceEqual(ProductCatalog.Modes), "The mode catalog must expose Native backup and both planned modes.");
            await Task.Delay(180);
            await CaptureCatalogAsync($"catalog-modes{suffix}");
            CatalogDialog.Hide();
            await dialogTask;
        }
        _viewModel.SetPreview(null);
        LoadSettings(reloadAccount: true, reloadPreferences: true);
        Refresh();
        ShowPage("overview");

        void SetPresentation(ClientPreview preview, ElementTheme theme)
        {
            _viewModel.SetPreview(preview with { Settings = preview.Settings with { Theme = theme.ToString() } });
            LoadSettings(reloadAccount: true, reloadPreferences: true);
            Refresh();
        }

        void ValidatePresentation(ClientState state)
        {
            if ((WelcomePanel.Visibility == Visibility.Visible) != (state == ClientState.NotConnected) ||
                (TransferProgressPanel.Visibility == Visibility.Visible) != (state is ClientState.Syncing or ClientState.Connecting) ||
                (ReviewDeletionsButton.Visibility == Visibility.Visible) != (state == ClientState.Attention))
                throw new InvalidOperationException($"The {state} overview exposed controls from another state.");
        }

        async Task CaptureCatalogAsync(string fileName)
        {
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"), $"{fileName}: in-place dialog rendering{Environment.NewLine}");
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"{fileName}.png"));
        }

        static async Task WaitForUiAsync(Func<bool> condition, string message)
        {
            // Native focus and AutoSuggestBox changes can follow the current dispatcher
            // work item, including when text is assigned programmatically.
            var timeout = Stopwatch.StartNew();
            while (!condition())
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(message);
                await Task.Delay(20);
            }
        }

        async Task CapturePageAsync(string page, string fileName, string settingsRoute = "home")
        {
            Navigation.SelectedItem = page == "settings" ? Navigation.SettingsItem : Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == page);
            ShowPage(page);
            if (page == "settings") OpenSettingsRoute(settingsRoute);
            await Task.Delay(500);
            RootGrid.UpdateLayout();
            // Navigation items also have a ContentGrid. Only the ancestor of
            // our page host is the native foreground content surface.
            DependencyObject? surface = VisualTreeHelper.GetParent(ContentLayoutGrid);
            while (surface is not null && surface is not Grid { Name: "ContentGrid" })
                surface = VisualTreeHelper.GetParent(surface);
            if (surface is not Grid nativeContent || nativeContent.Background is null)
                throw new InvalidOperationException("The native NavigationView content surface was not found.");
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                $"{fileName}: native surface background={nativeContent.Background?.GetType().Name ?? "none"}, margin={nativeContent.Margin}, corners={nativeContent.CornerRadius}, mode={Navigation.DisplayMode}{Environment.NewLine}");
            var foregroundPosition = nativeContent.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
            if (foregroundPosition.X < -1 || foregroundPosition.Y < AppTitleBar.ActualHeight - 1 ||
                foregroundPosition.X + nativeContent.ActualWidth > RootGrid.ActualWidth + 1 ||
                foregroundPosition.Y + nativeContent.ActualHeight > RootGrid.ActualHeight + 1)
                throw new InvalidOperationException("The native content layer must remain within the Mica window.");
            if (page != "activity")
            {
                var viewer = page switch { "backup" => BackupPage, "files" => FilesPage, "settings" => SettingsPage, _ => OverviewPage };
                var content = (FrameworkElement)viewer.Content;
                var position = content.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
                var viewportPosition = viewer.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                    $"{fileName}: root={RootGrid.ActualWidth}, viewport={viewer.ActualWidth}, content={content.ActualWidth}, x={position.X}{Environment.NewLine}");
                if (position.X < viewportPosition.X - 1 || position.X + content.ActualWidth > viewportPosition.X + viewer.ActualWidth + 1)
                    throw new InvalidOperationException($"The {page} page extends beyond its visible area.");
                if (page == "backup" && _uiSettings.TextScaleFactor <= 1.01)
                {
                    foreach (var name in CommonBackups)
                    {
                        if (_backupCards[name].Header is not TextBlock label) continue;
                        var natural = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight };
                        natural.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (label.ActualWidth + 1 < natural.DesiredSize.Width || label.ActualHeight > natural.DesiredSize.Height + 1)
                            throw new InvalidOperationException($"The {name} folder name must fit on one line at normal text size.");
                    }
                }
            }
            else
            {
                var position = ActivityPage.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
                if (position.X < -1 || position.X + ActivityPage.ActualWidth > RootGrid.ActualWidth + 1)
                    throw new InvalidOperationException("The activity page extends beyond its visible area.");
            }
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"{fileName}.png"));
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && (predicate is null || predicate(match))) return match;
            if (FindDescendant<T>(child, predicate) is { } descendant) return descendant;
        }
        return null;
    }

    private static void ArrangeTiles(Grid grid, int columns)
    {
        if (grid.ColumnDefinitions.Count != columns)
        {
            grid.ColumnDefinitions.Clear();
            for (var index = 0; index < columns; index++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rows = (grid.Children.Count + columns - 1) / columns;
        while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var index = 0; index < grid.Children.Count; index++)
        {
            Grid.SetRow((FrameworkElement)grid.Children[index], index / columns);
            Grid.SetColumn((FrameworkElement)grid.Children[index], index % columns);
        }
    }

    private static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}

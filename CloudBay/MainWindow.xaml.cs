using System.Diagnostics;
using System.Runtime.InteropServices;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.Sync;
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
    private readonly Dictionary<string, ProgressRing> _backupRings = new(StringComparer.OrdinalIgnoreCase);
    private sealed record BackupUiOperation(bool Enabling, string Status);
    private readonly Dictionary<string, BackupUiOperation> _backupJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _backupResultWarnings = new(StringComparer.OrdinalIgnoreCase);
    private const string CustomAddJobKey = "custom:add";
    private static string CustomStopJobKey(string name) => "custom:stop:" + name;
    private readonly SemaphoreSlim _modalQueue = new(1, 1);
    private readonly CancellationTokenSource _backupUiLifetime = new();
    private ContentDialog? _activeDialog;
    private int _modalUsers, _cloudDiscoveryUsers;
    private bool _backupUiDisposed;
    private string _cloudImportRevision = "";
    private int _cloudImportGeneration;
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
    private bool _importInProgress;
    private string _importStatus = "";
    private bool _refreshingBackups;
    private bool _loadingPreferences;
    private bool _closed;
    private int _refreshPending;
    private int _activityViewRevision;
    private AppSettings? _loadedSettings;
    private string _versionPath = "";
    private ActivityLocation? _versionLocation;
    private int _activityCloudRevision;
    private string? _activityActionValidationPath;
    private bool? _compactLayout;
    private string _currentPage = "overview";
    private string _settingsRoute = "home";
    private string _pendingInitialRoute = "overview";
    private string _restoredInitialRoute = "overview";
    private bool _navigationTemplateReady;
    private bool _applyingNavigationRoute;
    private readonly TaskCompletionSource<bool> _initialNavigationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _folderIconPixels;
    private Control? _settingsOrigin;
    private readonly UISettings _uiSettings = new();
    private readonly Style? _openFolderAccentStyle;
    private readonly string? _livePagePath = Environment.GetCommandLineArgs().Contains("--ui-live")
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "UiLive", "window-page.txt") : null;

    public bool AllowClose { get; set; }
    public Task InitialNavigationReady => _initialNavigationReady.Task;
    public string CurrentPageTitle => PageTitle.Text;
    public string CurrentRoute => SettingsPage.Visibility == Visibility.Visible
        ? _settingsRoute == "home" ? "settings" : $"settings/{_settingsRoute}"
        : ActivityPage.Visibility == Visibility.Visible ? "activity"
        : BackupPage.Visibility == Visibility.Visible ? "backup"
        : FilesPage.Visibility == Visibility.Visible ? "files" : "overview";

    public MainWindow(ClientController controller)
    {
        _controller = controller;
        // Read restoration before XAML or NavigationView can raise selection
        // events. Its SettingsItem is created when the native template loads.
        _pendingInitialRoute = ReadInitialNavigationRoute();
        InitializeComponent();
        _openFolderAccentStyle = OpenFolderButton.Style;
        _viewModel = new ClientViewModel(controller);
        ExclusionsEditor.OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExclusionsEditor.SaveChangesAsync = async update =>
        {
            if (_viewModel.Preview is not null) return;
            await _controller.UpdatePreferencesAsync(update);
        };
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
            _backupUiLifetime.Cancel();
            _activeDialog?.Hide();
            DisposeBackupUiWhenIdle();
            _initialNavigationReady.TrySetCanceled();
            _controller.Changed -= Controller_Changed;
            _uiSettings.TextScaleFactorChanged -= TextScaleFactor_Changed;
        };
        CreateBackupRows();
        LoadSettings();
        Refresh();
        if (Navigation.SelectedItem is NavigationViewItem { Tag: "backup" }) RefreshBackups(refreshMetadata: true);
        _controller.Changed += Controller_Changed;
        Navigation.Loaded += Navigation_Loaded;
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
        RequestNavigationRoute("settings");
        ShowWindow();
    }

    public void ShowAccount()
    {
        _settingsOrigin = SettingsAccountAction;
        RequestNavigationRoute("settings/account");
        ShowWindow();
        FocusSettingsAfterLayout(DisplaySettings.IsConfigured ? ApplicationKeyBox : BucketNameBox, "account");
    }

    public void ShowActivity()
    {
        ActivityFilterBox.SelectedIndex = 0;
        RequestNavigationRoute("activity");
        ShowWindow();
    }

    public void ShowQueuedActivity()
    {
        ActivityFilterBox.SelectedItem = ActivityFilterBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == "Queue");
        RequestNavigationRoute("activity");
        ShowWindow();
    }

    public void ShowOverview()
    {
        RequestNavigationRoute("overview");
        ShowWindow();
    }

    private string ReadInitialNavigationRoute()
    {
        var route = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--page=", StringComparison.Ordinal))?[7..];
        if (route is null && _livePagePath is not null)
        {
            try { if (File.Exists(_livePagePath)) route = File.ReadAllText(_livePagePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return NormalizeNavigationRoute(route);
    }

    private static string NormalizeNavigationRoute(string? route) => route?.Trim() switch
    {
        "activity" => "activity", "backup" => "backup", "files" => "files", "settings" or "settings/home" => "settings",
        "settings/account" => "settings/account", "settings/sync" => "settings/sync",
        "settings/network" => "settings/network", "settings/appearance" => "settings/appearance",
        "settings/general" => "settings/general", "settings/about" => "settings/about",
        _ => "overview"
    };

    private void Navigation_Loaded(object sender, RoutedEventArgs args)
    {
        if (_closed || _initialNavigationReady.Task.IsCompleted) return;
        try
        {
            Navigation.ApplyTemplate();
            _navigationTemplateReady = true;
            ApplyNavigationRoute(_pendingInitialRoute);
            AssertNavigationPresentation(_pendingInitialRoute);
            _restoredInitialRoute = CurrentRoute;
            _initialNavigationReady.TrySetResult(true);
            PersistNavigationRoute();
        }
        catch (Exception error)
        {
            _initialNavigationReady.TrySetException(error);
            throw;
        }
    }

    private void RequestNavigationRoute(string route)
    {
        route = NormalizeNavigationRoute(route);
        // A tray or activation action may arrive before Loaded. It takes
        // precedence over the captured startup route when restoration runs.
        if (!_initialNavigationReady.Task.IsCompletedSuccessfully) _pendingInitialRoute = route;
        ApplyNavigationRoute(route);
        PersistNavigationRoute();
    }

    private void ApplyNavigationRoute(string route)
    {
        _applyingNavigationRoute = true;
        try
        {
            var isSettings = route == "settings" || route.StartsWith("settings/", StringComparison.Ordinal);
            var page = isSettings ? "settings" : route;
            if (isSettings) OpenSettingsRoute(route == "settings" ? "home" : route[9..]);
            ShowPage(page);
            if (_navigationTemplateReady)
            {
                var selectedItem = isSettings ? Navigation.SettingsItem
                    : Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == page);
                if (selectedItem is null) throw new InvalidOperationException("The loaded navigation template did not create its Settings item.");
                Navigation.SelectedItem = selectedItem;
            }
        }
        finally { _applyingNavigationRoute = false; }
    }

    private void PersistNavigationRoute()
    {
        if (_livePagePath is null || !_initialNavigationReady.Task.IsCompletedSuccessfully || _applyingNavigationRoute) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_livePagePath)!);
            File.WriteAllText(_livePagePath, CurrentRoute);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AssertNavigationPresentation(string route)
    {
        var settings = route == "settings" || route.StartsWith("settings/", StringComparison.Ordinal);
        var page = settings ? "settings" : route;
        FrameworkElement expectedPage = page switch
        {
            "activity" => ActivityPage, "backup" => BackupPage, "files" => FilesPage,
            "settings" => SettingsPage, _ => OverviewPage
        };
        FrameworkElement[] pages = [OverviewPage, ActivityPage, BackupPage, FilesPage, SettingsPage];
        var expectedSelection = settings ? Navigation.SettingsItem
            : Navigation.MenuItems.Cast<NavigationViewItem>().First(item => (string)item.Tag == page);
        if (CurrentRoute != route || expectedSelection is null || !ReferenceEquals(Navigation.SelectedItem, expectedSelection) ||
            pages.Any(item => (item.Visibility == Visibility.Visible) != ReferenceEquals(item, expectedPage)) ||
            string.IsNullOrWhiteSpace(PageTitle.Text) || (settings && PageTitle.Text == "Overview"))
            throw new InvalidOperationException("Restored navigation must select the requested native item and display its actual page and header.");
        if (route == "settings/account" && (AccountSettingsDetail.Visibility != Visibility.Visible ||
            SettingsHub.Visibility != Visibility.Collapsed || SettingsDetail.Visibility != Visibility.Visible || PageTitle.Text != "Account"))
            throw new InvalidOperationException("Restoring Account must display the Account editor and its page header.");
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
        ActivityEmpty.Visibility = _viewModel.HasActivityRows ? Visibility.Collapsed : Visibility.Visible;
        ActivityQueueCoverage.Visibility = _viewModel.HasQueueCoverage ? Visibility.Visible : Visibility.Collapsed;
        OverviewStatusDetail.Visibility = _viewModel.HasStatusDetail ? Visibility.Visible : Visibility.Collapsed;
        OverviewLastSync.Visibility = _viewModel.HasLastSync ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageHeader();
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
        if (_versionLocation is { } versionLocation && !ActivityLocationResolver.MatchesCurrentRoot(versionLocation, settings))
        {
            _activityCloudRevision++;
            _versionLocation = null;
            VersionsPanel.Visibility = Visibility.Collapsed;
            VersionsList.ItemsSource = null;
        }
        RestoreVersionButton.IsEnabled = !_busy && CanRestoreSelectedVersion();
        RefreshProtectedFolders();
        ApplyTheme(settings.Theme);
        if (!ReferenceEquals(_loadedSettings, settings) && !_busy) LoadSettings();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // NavigationView can raise SelectionChanged while InitializeComponent is
        // still wiring the named content panels.
        if (OverviewPage is null || !_navigationTemplateReady || _applyingNavigationRoute ||
            !_initialNavigationReady.Task.IsCompletedSuccessfully) return;
        ShowPage(args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "overview");
    }

    private void ShowPage(string page)
    {
        _currentPage = page;
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
        UpdatePageHeader();
        if (page == "backup") RefreshBackups(refreshMetadata: true);
        if (selected is ScrollViewer viewer) viewer.ChangeView(null, 0, null, true);
        PersistNavigationRoute();
    }

    private void UpdatePageHeader()
    {
        var settingsDetail = _currentPage == "settings" && _settingsRoute != "home";
        PageTitle.Text = _currentPage switch
        {
            "activity" => "Activity",
            "backup" => "Folder backup",
            "files" => "Files",
            "settings" => _settingsRoute switch
            {
                "account" => "Account",
                "sync" => "Files and storage",
                "network" => "Transfers and power",
                "appearance" => "Appearance",
                "general" => "Startup",
                "about" => "About CloudBay",
                _ => "Settings"
            },
            _ => "Overview"
        };
        SettingsBackButton.Visibility = SettingsContextLabel.Visibility = settingsDetail ? Visibility.Visible : Visibility.Collapsed;
        PageDescription.Text = _currentPage == "backup" ? "Keep your Windows folders backed up and available in File Explorer." : "";
        PageDescription.Visibility = PageDescription.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityPending.Visibility = _currentPage == "activity" && _viewModel.HasTransferSummary ? Visibility.Visible : Visibility.Collapsed;
        ActivityTransferSpeed.Visibility = _currentPage == "activity" && _viewModel.HasTransferSpeed && _viewModel.HasTransfers ? Visibility.Visible : Visibility.Collapsed;
        StorageSummary.Visibility = _currentPage == "files" && _viewModel.HasStorageSummary ? Visibility.Visible : Visibility.Collapsed;
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
            if (reloadPreferences || previous is null || NumberTextMatches(DownloadConcurrencyBox, previous.DownloadConcurrency)) DownloadConcurrencyBox.Value = settings.DownloadConcurrency;
            if (reloadPreferences || previous is null || SelectedUploadMode == previous.UploadMode)
                UploadModeBox.SelectedItem = UploadModeBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == settings.UploadMode.ToString());
            if (reloadPreferences || previous is null || MeteredBox.IsOn == previous.PauseOnMetered) MeteredBox.IsOn = settings.PauseOnMetered;
            if (reloadPreferences || previous is null || BatterySaverBox.IsOn == previous.PauseOnBatterySaver) BatterySaverBox.IsOn = settings.PauseOnBatterySaver;
            if (reloadPreferences || previous is null || StartAtSignInBox.IsOn == previous.StartAtSignIn) StartAtSignInBox.IsOn = settings.StartAtSignIn;
            ExclusionsEditor.SetSettings(settings, presentationOnly: _viewModel.Preview is not null);
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
        var headerWidth = ContentLayoutGrid.ActualWidth - PageHeader.Margin.Left - PageHeader.Margin.Right;
        if (headerWidth > 0) PageHeader.Width = Math.Min(PageColumnWidth, headerWidth);
        if (ForegroundLayout.ActualWidth > 0) ActivityPage.Width = Math.Min(PageColumnWidth, ForegroundLayout.ActualWidth);
        var settingsWidth = SettingsContent.Width - SettingsContent.Padding.Left - SettingsContent.Padding.Right;
        ArrangeTiles(SettingsCategories, settingsWidth >= 660 * _uiSettings.TextScaleFactor ? 2 : 1);
        if (settingsWidth > 0)
        {
            ConnectionPanel.Width = Math.Min(720, settingsWidth);
            // Keep the secret full width. The shorter account identifiers share
            // a row only when their labels and inputs have comfortable space.
            var paired = ConnectionPanel.Width - ConnectionPanel.Padding.Left - ConnectionPanel.Padding.Right >= 560 * _uiSettings.TextScaleFactor;
            AccountCredentialFields.ColumnSpacing = paired ? 16 : 0;
            AccountCredentialFields.ColumnDefinitions[1].Width = paired ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(KeyIdBox, paired ? 1 : 0);
            Grid.SetRow(KeyIdBox, paired ? 0 : 1);
            Grid.SetRow(ApplicationKeyBox, paired ? 1 : 2);
            Grid.SetColumnSpan(ApplicationKeyBox, paired ? 2 : 1);
            while (AccountCredentialFields.RowDefinitions.Count < (paired ? 2 : 3)) AccountCredentialFields.RowDefinitions.Add(new() { Height = GridLength.Auto });
            while (AccountCredentialFields.RowDefinitions.Count > (paired ? 2 : 3)) AccountCredentialFields.RowDefinitions.RemoveAt(AccountCredentialFields.RowDefinitions.Count - 1);
        }
        var pane = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded ? Navigation.OpenPaneLength :
            Navigation.DisplayMode == NavigationViewDisplayMode.Compact ? Navigation.CompactPaneLength : 0;
        var compact = Navigation.ActualWidth - pane < 670;
        var backupWidth = BackupContent.Width - BackupContent.Padding.Left - BackupContent.Padding.Right;
        var backupColumns = backupWidth >= 660 * _uiSettings.TextScaleFactor ? 2 : 1;
        ArrangeTiles(BackupRows, backupColumns);
        ArrangeTiles(MoreBackupRows, backupColumns);
        ArrangeTiles(CustomBackupRows, backupWidth >= 760 * _uiSettings.TextScaleFactor ? 2 : 1);
        ArrangeTiles(CloudImportRows, backupWidth >= 760 * _uiSettings.TextScaleFactor ? 2 : 1);
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
            var pendingRing = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(pendingRing);
            actions.Children.Add(toggle);
            var folderIcon = GetKnownFolderVisual(name, IconPixels(40));
            var card = new SettingsCard
            {
                Header = new TextBlock { Text = name, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis },
                Description = path,
                HeaderIcon = folderIcon is not null ? new ImageIcon { Source = folderIcon, Width = 40, Height = 40 } : new FontIcon { Glyph = glyph, FontSize = 32 },
                Content = actions,
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
            _backupRings.Add(name, pendingRing);
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
        if (refreshMetadata)
        {
            RefreshBackupMetadata();
            _cloudImportRevision = "";
        }
        var settings = DisplaySettings;
        var revision = $"{settings.RootPath}|{settings.IsConfigured}|{_busy}|{_backupMetadataRevision}|" +
            string.Join('|', settings.Backups.Select(folder => $"{folder.Name}:{folder.DestinationPath}")) + "|" +
            string.Join('|', _viewModel.Preview?.WindowsFolderPaths?.Select(folder => $"{folder.Key}:{folder.Value}") ?? []) + "|" +
            string.Join('|', _backupJobs.Select(job => $"{job.Key}:{job.Value.Enabling}:{job.Value.Status}")) + "|" +
            string.Join('|', _backupResultWarnings.Select(item => $"{item.Key}:{item.Value}"));
        RefreshCloudImportSources();
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
                toggle.IsOn = ownsMapping;
                var pending = _backupJobs.GetValueOrDefault(name);
                toggle.IsEnabled = settings.IsConfigured && !_busy && pending is null && notice is null;
                _backupRings[name].Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
                _backupRings[name].IsActive = pending is not null;
                var caption = _backupPaths[name];
                var resultWarning = _backupResultWarnings.GetValueOrDefault(name);
                caption.Text = pending?.Status ?? (changedMapping ? "Windows folder location changed" : externalMapping ? "Review current Windows location" : notice is not null ? "Unavailable for backup" : resultWarning ?? "");
                caption.Visibility = caption.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                caption.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[pending is null ? "CloudBayAttentionTextStyle" : "CloudBaySecondaryTextStyle"];
                var tooltip = currentPath + (notice is not null ? Environment.NewLine + notice : externalMapping
                    ? Environment.NewLine + "This Windows folder is redirected. Review its current files and new CloudBay location before enabling backup." : "");
                if (pending is not null) tooltip += Environment.NewLine + pending.Status;
                if (resultWarning is not null) tooltip += Environment.NewLine + resultWarning;
                ToolTipService.SetToolTip(card, tooltip);
                AutomationProperties.SetHelpText(toggle, tooltip);
                if (GetKnownFolderVisual(name, IconPixels(40)) is { } folderIcon)
                    card.HeaderIcon = new ImageIcon { Source = folderIcon, Width = 40, Height = 40 };
            }
        }
        finally { _refreshingBackups = false; }
        UpdateResponsiveLayout();
    }

    private void RefreshCloudImportSources()
    {
        foreach (var card in CloudImportRows.Children.OfType<SettingsCard>()) card.IsEnabled = DisplaySettings.IsConfigured && !_busy && !_importInProgress;
        var settings = DisplaySettings;
        var revision = $"{settings.IsConfigured}|{settings.RootPath}|{_viewModel.Preview is not null}|" +
            string.Join('|', settings.CustomBackups.Select(folder => folder.SourcePath));
        if (revision == _cloudImportRevision) return;
        _cloudImportRevision = revision;
        var generation = ++_cloudImportGeneration;
        CloudImportRows.Children.Clear();
        CloudImportSection.Visibility = Visibility.Collapsed;
        if (!settings.IsConfigured) return;
        if (_viewModel.Preview is not null)
        {
            // Presentation fixtures never inspect real accounts or cloud folders.
            RenderCloudImportSources([
                new("synthetic-personal", "OneDrive", "Microsoft OneDrive", @"C:\Users\Example\OneDrive", "Existing Windows folder"),
                new("synthetic-work", "OneDrive · Work", "Microsoft OneDrive", @"D:\Personal files\OneDrive - Work", "Registered cloud folder")
            ]);
            return;
        }
        _ = DiscoverCloudImportSourcesAsync(settings, generation);
    }

    private async Task DiscoverCloudImportSourcesAsync(AppSettings settings, int generation)
    {
        _cloudDiscoveryUsers++;
        var discovery = Task.Run(() => ImportSourceDiscovery.FindCandidates(settings));
        // Discovery may continue after a timeout on an unavailable mounted drive.
        // Observe its eventual error and retain manual browsing as the fallback.
        _ = discovery.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var candidates = await discovery.WaitAsync(TimeSpan.FromSeconds(8), _backupUiLifetime.Token);
            if (_closed || generation != _cloudImportGeneration) return;
            RenderCloudImportSources(candidates);
        }
        catch (Exception)
        {
            // This optional shortcut must never prevent the Windows folder
            // switches or the explicit import chooser from being used.
        }
        finally { _cloudDiscoveryUsers--; DisposeBackupUiWhenIdle(); }
    }

    private void RenderCloudImportSources(IReadOnlyList<ImportSourceCandidate> candidates)
    {
        CloudImportRows.Children.Clear();
        foreach (var candidate in candidates)
        {
            var source = candidate;
            var card = new SettingsCard
            {
                Header = candidate.DisplayName,
                Description = candidate.ProviderName,
                HeaderIcon = new FontIcon { Glyph = "\uE753", FontSize = 28 },
                IsClickEnabled = true,
                IsEnabled = DisplaySettings.IsConfigured && !_busy && !_importInProgress,
                MinHeight = 96,
                Padding = new Thickness(20),
                Tag = candidate.Path
            };
            AutomationProperties.SetName(card, $"Import from {candidate.DisplayName}");
            AutomationProperties.SetHelpText(card, candidate.Path + ". Choose this whole cloud folder, then review what to copy into CloudBay.");
            ToolTipService.SetToolTip(card, candidate.Path);
            card.Click += async (_, _) => await OpenSourceImportAsync(source.Path);
            CloudImportRows.Children.Add(card);
        }
        CloudImportSection.Visibility = candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout();
    }

    private void RefreshCustomBackups()
    {
        var settings = DisplaySettings;
        var revision = string.Join("|", settings.CustomBackups.Select(folder => $"{folder.Name}:{folder.SourcePath}:{folder.Prefix}")) + $"|{_busy}|" +
            string.Join('|', _backupJobs.Where(job => job.Key.StartsWith("custom:", StringComparison.Ordinal)).Select(job => $"{job.Key}:{job.Value.Status}"));
        AddCustomBackupButton.IsEnabled = settings.IsConfigured && !_busy && !_backupJobs.ContainsKey(CustomAddJobKey);
        CustomBackupExpander.Description = _backupJobs.GetValueOrDefault(CustomAddJobKey)?.Status ?? "Back up a personal folder from its current location.";
        ImportFilesCard.IsEnabled = settings.IsConfigured && !_busy && !_importInProgress;
        CustomBackupSection.Visibility = settings.CustomBackups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_customBackupRevision == revision) return;
        _customBackupRevision = revision;
        CustomBackupRows.Children.Clear();
        foreach (var folder in settings.CustomBackups)
        {
            var pending = _backupJobs.GetValueOrDefault(CustomStopJobKey(folder.Name));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (pending is not null) actions.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            var open = new Button { Content = "Open folder", Tag = folder.SourcePath };
            open.Click += (_, _) =>
            {
                if (_viewModel.Preview is not null) return;
                try { Process.Start(new ProcessStartInfo(folder.SourcePath) { UseShellExecute = true }); }
                catch (Exception error) { ShowError(error); }
            };
            actions.Children.Add(open);
            var stop = new MenuFlyoutItem { Text = "Stop backup", Tag = folder.Name, IsEnabled = !_busy && pending is null, Icon = new FontIcon { Glyph = "\uE711" } };
            stop.Click += RemoveCustomBackup_Click;
            var menu = new MenuFlyout();
            menu.Items.Add(stop);
            var more = new Button { Content = new FontIcon { Glyph = "\uE712" }, Flyout = menu, IsEnabled = !_busy && pending is null };
            AutomationProperties.SetName(more, $"More options for {folder.Name}");
            ToolTipService.SetToolTip(more, "More options");
            actions.Children.Add(more);
            CustomBackupRows.Children.Add(new SettingsCard
            {
                Header = new TextBlock { Text = folder.Name, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] },
                HeaderIcon = GetFolderVisual(folder.SourcePath, IconPixels(40)) is { } folderIcon
                    ? new ImageIcon { Source = folderIcon, Width = 40, Height = 40 } : new FontIcon { Glyph = "\uE8B7" },
                Tag = folder.SourcePath,
                Description = pending is null ? string.Empty : (object)SourceImportDialog.Text(pending.Status, true),
                Content = actions,
                MinHeight = 112,
                Padding = new Thickness(20)
            });
            ToolTipService.SetToolTip(CustomBackupRows.Children[^1], folder.SourcePath + (pending is null ? "" : Environment.NewLine + pending.Status));
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
            var customSource = settings.CustomBackups.FirstOrDefault(item => item.Name == folder.Name)?.SourcePath;
            var source = folder.Custom ? GetFolderVisual(customSource, IconPixels(40)) : GetKnownFolderVisual(folder.Name, IconPixels(40));
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
        var innerWidth = OverviewContent.Width - OverviewContent.Padding.Left - OverviewContent.Padding.Right;
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
        var protectedPadding = ProtectedFolderSurface.Padding.Left + ProtectedFolderSurface.Padding.Right;
        ArrangeTiles(ProtectedFolderRows, Math.Clamp((int)((protectedWidth - protectedPadding) / (128 * _uiSettings.TextScaleFactor)), 1, 3));
    }

    private sealed record ProtectedFolderLink(string Name, bool Custom);

    private int IconPixels(double logicalSize) => Math.Clamp((int)Math.Ceiling(logicalSize * GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d), 16, 128);

    private ImageSource? GetFolderVisual(string? path, int pixels, string? knownFolderName = null)
    {
        // Presentation fixtures never inspect real user folder metadata.
        if (_viewModel.Preview is not null || string.IsNullOrWhiteSpace(path))
            return knownFolderName is null ? FolderIconProvider.GetCustomFolderIcon(pixels) : FolderIconProvider.GetIcon(knownFolderName, pixels);
        return FolderIconProvider.GetFolderIcon(path, pixels, knownFolderName);
    }

    private ImageSource? GetKnownFolderVisual(string name, int pixels)
    {
        var backup = DisplaySettings.Backups.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var path = backup is not null && Directory.Exists(backup.OriginalPath) ? backup.OriginalPath
            : _backupMetadataPaths.GetValueOrDefault(name) ?? backup?.DestinationPath;
        return GetFolderVisual(path, pixels, name);
    }

    private void RefreshFolderIconSources()
    {
        var pixels = IconPixels(40);
        if (_folderIconPixels == pixels) return;
        _folderIconPixels = pixels;
        foreach (var (name, card) in _backupCards)
            if (GetKnownFolderVisual(name, pixels) is { } source) card.HeaderIcon = new ImageIcon { Source = source, Width = 40, Height = 40 };
        foreach (var card in CustomBackupRows.Children.OfType<SettingsCard>())
            if (GetFolderVisual(card.Tag as string, pixels) is { } source) card.HeaderIcon = new ImageIcon { Source = source, Width = 40, Height = 40 };
        foreach (var button in ProtectedFolderRows.Children.OfType<Button>())
            if (button.Tag is ProtectedFolderLink folder && button.Content is StackPanel panel && panel.Children[0] is Image image)
                image.Source = folder.Custom ? GetFolderVisual(DisplaySettings.CustomBackups.FirstOrDefault(item => item.Name == folder.Name)?.SourcePath, pixels)
                    : GetKnownFolderVisual(folder.Name, pixels);
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
        await RunFolderJobAsync(CustomAddJobKey, true, "Choosing a personal folder…", async token =>
        {
            var folder = await DesktopPickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), "Choose backup folder", token);
            if (folder is not null && !_closed) CustomBackupSourceBox.Text = folder;
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
        _versionLocation = null;
        FilePathBox.Text = "";
    }

    private void FileScope_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshingFileScopes || FileFolderPath is null) return;
        UpdateFileScopeLabels();
        FilePathBox.Text = "";
        VersionsPanel.Visibility = Visibility.Collapsed;
        VersionsList.ItemsSource = null;
        _versionLocation = null;
    }

    private void UpdateFileScopeLabels()
    {
        if (FileScopeBox.SelectedItem is not SyncFolderItem scope) return;
        FileFolderName.Text = scope.BackupName is null ? "CloudBay folder" : scope.Name;
        FileFolderPath.Text = scope.RootPath;
        var knownFolderName = DisplaySettings.Backups.FirstOrDefault(folder => SameFolderPath(folder.DestinationPath, scope.RootPath))?.Name;
        FileLocationCard.HeaderIcon = GetFolderVisual(scope.RootPath, IconPixels(48), knownFolderName) is { } folderIcon
            ? new ImageIcon { Source = folderIcon, Width = 48, Height = 48 } : new FontIcon { Glyph = "\uE8B7", FontSize = 40 };
    }

    private void OpenFileFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
        try { _controller.LaunchFolder(SelectedBackupName); }
        catch (Exception error) { ShowError(error); }
    }

    private async void AddCustomBackup_Click(object sender, RoutedEventArgs args) =>
        await RunFolderJobAsync(CustomAddJobKey, true, "Preparing backup; starts when ready…", async token =>
        {
            var source = CustomBackupSourceBox.Text.Trim();
            if (source.Length == 0) throw new InvalidOperationException("Choose the personal folder you want to back up.");
            var submittedName = CustomBackupNameBox.Text.Trim();
            var name = submittedName;
            if (name.Length == 0) name = new DirectoryInfo(source).Name;
            if (name.Length == 0 || name.Length > 64 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
                throw new InvalidOperationException("Choose a backup name up to 64 characters without slashes or Windows filename symbols.");
            await _controller.AddCustomBackupAsync(source, name, token);
            if (!_closed)
            {
                // The user can prepare the next folder while this one waits.
                // Completing this job must not erase a newer form draft.
                if (CustomBackupSourceBox.Text.Trim() == source && CustomBackupNameBox.Text.Trim() == submittedName)
                    CustomBackupSourceBox.Text = CustomBackupNameBox.Text = "";
                ShowInfo($"{name} is now backed up. Its folder remains in its current location.");
            }
        });

    private async void RemoveCustomBackup_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _closed || _viewModel.Preview is not null || sender is not FrameworkElement { Tag: string name } || _backupJobs.ContainsKey(CustomStopJobKey(name))) return;
        var folder = _controller.Settings.CustomBackups.SingleOrDefault(item => item.Name == name);
        if (folder is null) return;
        await RunFolderJobAsync(CustomStopJobKey(name), false, "Waiting for stop review…", async token =>
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
            if (await ShowModalAsync(dialog, () => SetFolderBackupStatus(CustomStopJobKey(name), "Reviewing the stop choice…")) != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            SetFolderBackupStatus(CustomStopJobKey(name), "Preparing local files; starts when ready…");
            await _controller.RemoveCustomBackupAsync(name, token);
            if (!_closed) ShowInfo($"{name} backup stopped. Your local files and B2 files were retained.");
        });
    }

    private async Task RunFolderJobAsync(string key, bool enabling, string status, Func<CancellationToken, Task> operation)
    {
        if (_closed || _busy || _viewModel.Preview is not null || _backupJobs.ContainsKey(key)) return;
        _backupJobs.Add(key, new(enabling, status));
        RefreshBackups();
        RefreshCustomBackups();
        UpdateBackupBusyFooter();
        try { await operation(_backupUiLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally
        {
            _backupJobs.Remove(key);
            if (!_closed)
            {
                RefreshBackups(refreshMetadata: true);
                RefreshCustomBackups();
                UpdateBackupBusyFooter();
                Refresh();
            }
            DisposeBackupUiWhenIdle();
        }
    }

    private async void Backup_Toggled(object sender, RoutedEventArgs args)
    {
        if (_refreshingBackups || _busy || _viewModel.Preview is not null || sender is not ToggleSwitch toggle) return;
        var name = (string)toggle.Tag;
        if (_backupJobs.ContainsKey(name)) return;
        var enabling = toggle.IsOn;
        var job = new BackupUiOperation(enabling, "Waiting for choices…");
        _backupResultWarnings.Remove(name);
        _backupJobs.Add(name, job);
        RefreshBackups();
        UpdateBackupBusyFooter();
        var token = _backupUiLifetime.Token;
        try
        {
            // Only this folder is busy. Other folders can be selected while the
            // controller serializes reviewed file transfers in the background.
            var setup = new BackupSetupDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), name, stopping: !enabling)
            { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
            if (await ShowModalAsync(setup, () => SetFolderBackupStatus(name, "Choosing files and location…")) != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            SetFolderBackupStatus(name, "Queued for review…");
            if (enabling)
            {
                var review = await _controller.PreviewBackupAsync(name, setup.SourcePath, setup.TransferMode, setup.IncludeCurrentFiles, token);
                token.ThrowIfCancellationRequested();
                var dialog = new BackupReviewDialog(review) { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
                SetFolderBackupStatus(name, "Waiting for review…");
                if (await ShowModalAsync(dialog, () => SetFolderBackupStatus(name, "Reviewing the backup choice…")) != ContentDialogResult.Primary) return;
                SetFolderBackupStatus(name, "Queued to turn backup on…");
                var warning = await _controller.EnableReviewedBackupAsync(review, token, BackupProgress(name));
                if (!_closed)
                {
                    if (warning is not null)
                    {
                        _backupResultWarnings[name] = warning;
                        ShowWarning($"{name} backup is on", warning);
                    }
                    else ShowInfo($"{name} backup is on. Windows now opens it in CloudBay.");
                }
            }
            else
            {
                var review = await _controller.PreviewStopBackupAsync(name, setup.DestinationPath, setup.TransferMode, token);
                token.ThrowIfCancellationRequested();
                var dialog = new BackupReviewDialog(review, setup.FreeLocalSpace) { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
                SetFolderBackupStatus(name, "Waiting for review…");
                if (await ShowModalAsync(dialog, () => SetFolderBackupStatus(name, "Reviewing the stop choice…")) != ContentDialogResult.Primary) return;
                SetFolderBackupStatus(name, "Queued to turn backup off…");
                var warning = await _controller.DisableReviewedBackupAsync(review, token, BackupProgress(name), setup.FreeLocalSpace);
                if (!_closed)
                {
                    if (warning is not null)
                    {
                        _backupResultWarnings[name] = warning;
                        ShowWarning($"{name} backup is off", warning);
                    }
                    else ShowInfo($"{name} backup is off. Windows now uses the reviewed location.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally
        {
            _backupJobs.Remove(name);
            if (!_closed)
            {
                RefreshBackups(refreshMetadata: true);
                UpdateBackupBusyFooter();
                Refresh();
            }
            DisposeBackupUiWhenIdle();
        }
    }

    private IProgress<string> BackupProgress(string name) => new Progress<string>(status =>
        DispatcherQueue.TryEnqueue(() => { if (!_closed && _backupJobs.ContainsKey(name)) SetFolderBackupStatus(name, status); }));

    private void SetFolderBackupStatus(string name, string status)
    {
        if (!_backupJobs.TryGetValue(name, out var job)) return;
        _backupJobs[name] = job with { Status = status };
        RefreshBackups();
        if (name.StartsWith("custom:", StringComparison.Ordinal)) RefreshCustomBackups();
        UpdateBackupBusyFooter();
    }

    private void UpdateBackupBusyFooter()
    {
        if (_closed) return;
        ConnectButton.IsEnabled = DisconnectButton.IsEnabled = !_busy && _backupJobs.Count == 0 && !_importInProgress;
        if (_busy) return;
        var folders = _backupJobs.Count;
        var pending = folders + (_importInProgress ? 1 : 0);
        BusyFooter.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = pending > 0;
        BusyRing.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        FooterStatus.Text = _importInProgress
            ? folders == 0 ? _importStatus + " You can choose another Windows folder." :
                $"1 import and {folders:N0} folder {(folders == 1 ? "change" : "changes")} in progress. File transfers start in turn."
            : pending == 1 ? "1 folder change in progress. You can choose another folder." :
                pending > 1 ? $"{pending:N0} folder changes in progress. Choices and file transfers are queued safely." : "";
    }

    private async Task<ContentDialogResult> ShowModalAsync(ContentDialog dialog, Action? opening = null)
    {
        if (_closed) throw new OperationCanceledException();
        _modalUsers++;
        var entered = false;
        try
        {
            await _modalQueue.WaitAsync(_backupUiLifetime.Token);
            entered = true;
            _backupUiLifetime.Token.ThrowIfCancellationRequested();
            _activeDialog = dialog;
            opening?.Invoke();
            return await dialog.ShowAsync();
        }
        finally
        {
            if (entered) { _activeDialog = null; _modalQueue.Release(); }
            _modalUsers--;
            DisposeBackupUiWhenIdle();
        }
    }

    private void DisposeBackupUiWhenIdle()
    {
        if (!_closed || _backupUiDisposed || _backupJobs.Count > 0 || _importInProgress || _modalUsers > 0 || _cloudDiscoveryUsers > 0) return;
        _backupUiDisposed = true;
        _backupUiLifetime.Dispose();
        _modalQueue.Dispose();
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs args) => await OpenSourceImportAsync();

    private async Task OpenSourceImportAsync(string? sourcePath = null)
    {
        if (_closed || _busy || _importInProgress || _viewModel.Preview is not null) return;
        _importInProgress = true;
        _importStatus = "Waiting for import choices.";
        RefreshBackups();
        RefreshCustomBackups();
        UpdateBackupBusyFooter();
        var token = _backupUiLifetime.Token;
        try
        {
            var dialog = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), initialSourcePath: sourcePath)
            { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
            if (await ShowModalAsync(dialog, () =>
            {
                _importStatus = "Reviewing files to import.";
                UpdateBackupBusyFooter();
            }) != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            _importStatus = "Import in progress; transfers start when ready.";
            UpdateBackupBusyFooter();
            if (dialog.ResumeCloudId is { } id) await _controller.ResumeCloudImportAsync(id, token);
            else if (dialog.CloudPlan is { } cloud) await _controller.ImportCloudAsync(cloud, token);
            else if (dialog.FolderPlan is { } folder) await _controller.ImportFolderAsync(folder, token);
            else return;
            if (!_closed) ShowInfo("Import completed. The original source files were retained.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally
        {
            _importInProgress = false;
            _importStatus = "";
            if (!_closed)
            {
                RefreshBackups();
                RefreshCustomBackups();
                UpdateBackupBusyFooter();
                Refresh();
            }
            DisposeBackupUiWhenIdle();
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs args)
    {
        if (_backupJobs.Count > 0 || _importInProgress)
        {
            ShowInfo("Finish or cancel the pending import and folder backup changes before updating this connection.");
            return;
        }
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

    private UploadMode SelectedUploadMode => Enum.TryParse<UploadMode>((UploadModeBox.SelectedItem as ComboBoxItem)?.Tag as string, out var mode) ? mode : UploadMode.Intelligent;

    private void UploadMode_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ManualConcurrencyCard is null || ManualDownloadConcurrencyCard is null || UploadPerformanceCard is null) return;
        var mode = SelectedUploadMode;
        ManualConcurrencyCard.Visibility = ManualDownloadConcurrencyCard.Visibility = mode == UploadMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        UploadPerformanceCard.Description = mode switch
        {
            UploadMode.MaximumThroughput => "Prioritizes speed with more parallel transfers. Can use more network, CPU and disk resources.",
            UploadMode.Manual => "Choose separate limits for simultaneous upload requests and downloads.",
            _ => "Balances parallel transfers with available system resources."
        };
    }

    private void ActivityFilter_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_viewModel is null || ActivityEmpty is null) return;
        _viewModel.SetActivityFilter((ActivityFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "All");
        ActivityEmpty.Visibility = _viewModel.HasActivityRows ? Visibility.Collapsed : Visibility.Visible;
        ActivityQueueCoverage.Visibility = _viewModel.HasQueueCoverage ? Visibility.Visible : Visibility.Collapsed;
        var revision = ++_activityViewRevision;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_closed || revision != _activityViewRevision || _viewModel.ActivityRows.Count == 0) return;
            ActivityList.UpdateLayout();
            ActivityList.ScrollIntoView(_viewModel.ActivityRows[0], ScrollIntoViewAlignment.Leading);
            FindDescendant<ScrollViewer>(ActivityList)?.ChangeView(null, 0, null, true);
        });
    }

    private async void ApplyNetwork_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Applying transfer settings…", async () =>
        {
            var upload = ReadWholeNumber(UploadLimitBox, "Upload limit", 0, 1048576);
            var download = ReadWholeNumber(DownloadLimitBox, "Download limit", 0, 1048576);
            var concurrency = SelectedUploadMode == UploadMode.Manual
                ? ReadWholeNumber(ConcurrencyBox, "Upload slots", 1, 16) : DisplaySettings.UploadConcurrency;
            var downloadConcurrency = SelectedUploadMode == UploadMode.Manual
                ? ReadWholeNumber(DownloadConcurrencyBox, "Download slots", 1, 16) : DisplaySettings.DownloadConcurrency;
            await _controller.UpdatePreferencesAsync(new()
            {
                UploadBytesPerSecond = upload * 1024L,
                DownloadBytesPerSecond = download * 1024L,
                UploadMode = SelectedUploadMode,
                UploadConcurrency = concurrency,
                DownloadConcurrency = downloadConcurrency
            });
        }, "Transfer settings are applied.");

    private async void Disconnect_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (_backupJobs.Count > 0 || _importInProgress)
        {
            ShowInfo("Finish or cancel the pending import and folder backup changes before disconnecting this account.");
            return;
        }
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
            if (await ShowModalAsync(dialog) != ContentDialogResult.Primary) return;
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

    private async void ActivityRow_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: IActivityActionRow row })
            await row.Actions.RefreshLocalAvailabilityAsync(_viewModel.Preview is not null);
    }

    private void ActivityRow_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is DependencyObject element && FindDescendant<StackPanel>(element, panel => panel.Name == "ActivityActions") is { } actions)
            actions.Orientation = args.NewSize.Width < 740 ? Orientation.Vertical : Orientation.Horizontal;
    }

    private async void ActivityOpenFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null || sender is not FrameworkElement { DataContext: IActivityActionRow row } || row.Actions.Target is not { } target) return;
        try
        {
            var folder = await ActivityRowNavigation.ResolveFolderAsync(target, _controller.Settings);
            if (!_closed && ActivityLocationResolver.MatchesCurrentRoot(target.Location, _controller.Settings)) SystemIntegration.OpenFolder(folder);
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ActivityViewCloud_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null || sender is not FrameworkElement { DataContext: IActivityActionRow row } || row.Actions.Target is not { CanViewCloud: true } target) return;
        try { await ShowActivityCloudAsync(target); }
        catch (Exception error) { ShowError(error); }
    }

    public async Task ShowActivityCloudAsync(ActivityActionTarget target)
    {
        if (_viewModel.Preview is not null || _closed) return;
        if (!target.CanViewCloud || !ActivityLocationResolver.MatchesCurrentRoot(target.Location, _controller.Settings))
            throw new IOException("This activity belongs to a backup location that is no longer connected.");
        var request = ++_activityCloudRevision;
        RefreshFileScopes();
        if (FileScopeBox.ItemsSource is not IEnumerable<SyncFolderItem> scopes ||
            scopes.SingleOrDefault(scope => scope.BackupName == target.Location.BackupName) is not { } selected)
            throw new IOException("The activity's backup folder is no longer connected.");
        FileScopeBox.SelectedItem = selected;
        FilePathBox.Text = target.Location.RelativePath;
        FileToolsExpander.IsExpanded = true;
        RequestNavigationRoute("files");
        ShowWindow();
        _versionPath = $"{selected.Name} / {target.Location.RelativePath}";
        _versionLocation = target.Location;
        VersionPathLabel.Text = _versionPath;
        VersionsPanel.Visibility = Visibility.Visible;
        VersionsList.ItemsSource = null;
        VersionsEmptyLabel.Text = "Loading versions from Backblaze B2…";
        VersionsEmptyLabel.Visibility = Visibility.Visible;
        RestoreVersionButton.IsEnabled = false;
        BringActivityVersionsIntoView(request, target.Location);
        // Viewing cloud metadata never reads or downloads local file content.
        // Independent requests keep navigation and folder choices responsive.
        try
        {
            var versions = await _controller.GetVersionsAsync(target.Location.RelativePath, target.Location.BackupName);
            if (_closed || request != _activityCloudRevision ||
                !ActivityLocationResolver.MatchesCurrentRoot(target.Location, _controller.Settings) ||
                SelectedBackupName != target.Location.BackupName || FilePathBox.Text.Trim().Replace('\\', '/') != target.Location.RelativePath) return;
            VersionsList.ItemsSource = versions.OrderByDescending(item => item.ModifiedUtc).Select(item => new VersionItem(item)).ToList();
            VersionsEmptyLabel.Text = "No retained versions found.";
            VersionsEmptyLabel.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BringActivityVersionsIntoView(request, target.Location);
        }
        catch
        {
            if (_closed || request != _activityCloudRevision ||
                !ActivityLocationResolver.MatchesCurrentRoot(target.Location, _controller.Settings) ||
                SelectedBackupName != target.Location.BackupName || FilePathBox.Text.Trim().Replace('\\', '/') != target.Location.RelativePath) return;
            if (!_closed && request == _activityCloudRevision)
                VersionsEmptyLabel.Text = "Cloud versions could not be loaded. Try Version history again.";
            throw;
        }
    }

    private void BringActivityVersionsIntoView(int request, ActivityLocation location) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_closed || request != _activityCloudRevision || CurrentRoute != "files" ||
                !ActivityLocationResolver.MatchesCurrentRoot(location, _controller.Settings) ||
                SelectedBackupName != location.BackupName || FilePathBox.Text.Trim().Replace('\\', '/') != location.RelativePath) return;
            RootGrid.UpdateLayout();
            VersionPathLabel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        });

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

    private void ExclusionsSettings_Click(object sender, RoutedEventArgs args)
    {
        SyncSettings_Click(sender, args);
        ExclusionsExpander.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || CurrentRoute != "settings/sync") return;
            ExclusionsExpander.StartBringIntoView();
        });
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
        var routes = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal)
        {
            ["account"] = AccountSettingsDetail, ["sync"] = SyncSettingsDetail,
            ["network"] = NetworkSettingsDetail, ["appearance"] = AppearanceSettingsDetail,
            ["general"] = GeneralSettingsDetail, ["about"] = AboutSettingsDetail
        };
        _settingsRoute = routes.ContainsKey(route) ? route : "home";
        foreach (var panel in routes.Values) panel.Visibility = Visibility.Collapsed;
        SettingsHub.Visibility = _settingsRoute == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsDetail.Visibility = _settingsRoute == "home" ? Visibility.Collapsed : Visibility.Visible;
        if (routes.TryGetValue(_settingsRoute, out var detail))
        {
            detail.Visibility = Visibility.Visible;
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
            var folder = await DesktopPickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), "Choose CloudBay folder");
            if (folder is not null && !_closed) RootPathBox.Text = folder;
        });
    }

    private async void BrowseFile_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Choose a file in CloudBay…", async () =>
        {
            var root = SelectedFileRoot;
            var file = await DesktopPickers.PickFileAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), "Choose file");
            if (file is null || _closed || !SameFolderPath(root, SelectedFileRoot)) return;
            var relative = Path.GetRelativePath(root, file);
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
            var backupName = SelectedBackupName;
            var settings = _controller.Settings;
            var custom = backupName is null ? null : settings.CustomBackups.Single(folder => folder.Name == backupName);
            var location = new ActivityLocation(custom?.SourcePath ?? settings.RootPath, backupName, path, settings.BucketId, custom?.Prefix ?? settings.Prefix);
            var request = ++_activityCloudRevision;
            var versions = await _controller.GetVersionsAsync(path, backupName);
            if (_closed || request != _activityCloudRevision || SelectedBackupName != backupName || FilePathBox.Text.Trim().Replace('\\', '/') != path ||
                !ActivityLocationResolver.MatchesCurrentRoot(location, _controller.Settings)) return;
            VersionsEmptyLabel.Text = "No retained versions found.";
            _versionPath = $"{FileFolderName.Text} / {path}";
            _versionLocation = location;
            VersionPathLabel.Text = _versionPath;
            VersionsList.ItemsSource = versions.OrderByDescending(item => item.ModifiedUtc).Select(item => new VersionItem(item)).ToList();
            VersionsEmptyLabel.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VersionsPanel.Visibility = Visibility.Visible;
            RestoreVersionButton.IsEnabled = false;
        });

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        RestoreVersionButton.IsEnabled = !_busy && CanRestoreSelectedVersion();

    private bool CanRestoreSelectedVersion() => _versionLocation is { } location &&
        ActivityLocationResolver.MatchesCurrentRoot(location, _controller.Settings) &&
        VersionsList.SelectedItem is VersionItem { File.Action: "upload" } selected &&
        selected.File.Key.Equals(location.Prefix + location.RelativePath, StringComparison.Ordinal);

    private async void RestoreVersion_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || !CanRestoreSelectedVersion() || _versionLocation is not { } location || VersionsList.SelectedItem is not VersionItem version) return;
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
            if (await ShowModalAsync(dialog) != ContentDialogResult.Primary) return;
            if (!ActivityLocationResolver.MatchesCurrentRoot(location, _controller.Settings))
                throw new IOException("The backup location changed. Load its version history again before restoring.");
            await _controller.RestoreVersionAsync(version.File, location.BackupName);
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
            if (await ShowModalAsync(dialog) != ContentDialogResult.Primary) return;
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
            if (success is not null && !_closed) ShowInfo(success);
        }
        catch (OperationCanceledException) { if (!_closed) ShowInfo("The operation was canceled."); }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                SetBusy(false, "CloudBay continues working in the background");
                UpdateBackupBusyFooter();
                Refresh();
                RestoreVersionButton.IsEnabled = CanRestoreSelectedVersion();
            }
        }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        BusyFooter.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FooterStatus.Text = message;
        SettingsPage.IsEnabled = FilesPage.IsEnabled = !busy;
        CustomBackupSourceBox.IsEnabled = CustomBackupNameBox.IsEnabled = !busy;
        SyncNowButton.IsEnabled = DashboardPauseButton.IsEnabled = !busy && _controller.Settings.IsConfigured;
        RefreshBackups();
        RefreshCustomBackups();
        UpdateBackupBusyFooter();
    }


    private async Task OpenCatalogAsync()
    {
        if (_busy) return;
        CatalogDialog.XamlRoot = RootGrid.XamlRoot;
        CatalogDialog.RequestedTheme = RootGrid.RequestedTheme;
        UpdateCatalog(resetCategories: true);
        try { await ShowModalAsync(CatalogDialog); }
        catch (OperationCanceledException) { }
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
        var message = string.IsNullOrWhiteSpace(error.Message)
            ? "Windows could not complete this action. Try again or reopen CloudBay."
            : error.Message;
        StatusInfoBar.Title = "CloudBay needs your attention";
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.ActionButton = message.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? CreateWindowsBackupSettingsButton() : null;
        StatusInfoBar.Visibility = Visibility.Visible;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowInfo(string message)
    {
        StatusInfoBar.Title = "CloudBay";
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.ActionButton = null;
        StatusInfoBar.Visibility = Visibility.Visible;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowWarning(string title, string message)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = InfoBarSeverity.Warning;
        StatusInfoBar.ActionButton = null;
        StatusInfoBar.Visibility = Visibility.Visible;
        StatusInfoBar.IsOpen = true;
    }

    private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        sender.Visibility = Visibility.Collapsed;

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
        _activityActionValidationPath = Path.Combine(outputDirectory, "activity-action-assertions.txt");
        Directory.CreateDirectory(outputDirectory);
        var themeArgument = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--ui-smoke-theme=", StringComparison.Ordinal))?[17..];
        if (themeArgument != "Light")
        {
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "layout.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "capture-metrics.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "exclusions-validation.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "transfers-validation.txt"), "");
        }
        ShowWindow();
        await InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(300);
        RootGrid.UpdateLayout();
        AssertNavigationPresentation(_restoredInitialRoute);
        if (StatusInfoBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("A closed notice must not reserve space between the heading and page content.");
        ShowInfo("UI validation notice");
        RootGrid.UpdateLayout();
        if (StatusInfoBar.Visibility != Visibility.Visible || StatusInfoBar.ActualHeight <= 0)
            throw new InvalidOperationException("An open notice must remain visible and accessible.");
        StatusInfoBar.IsOpen = false;
        await Task.Delay(100);
        RootGrid.UpdateLayout();
        if (StatusInfoBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Dismissing a notice must restore the page's normal spacing.");
        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
            $"startup-navigation-{themeArgument}: actual route={CurrentRoute}, title={CurrentPageTitle}, selected={Navigation.SelectedItem is NavigationViewItem}{Environment.NewLine}");
        await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"startup-navigation-{themeArgument?.ToLowerInvariant() ?? "dark"}.png"));
        foreach (var earlyRoute in new[] { "settings", "settings/account" })
        {
            // Exercise the real native window/template lifecycle. A tray
            // action before Loaded must override the captured startup route.
            var earlyWindow = new MainWindow(_controller);
            try
            {
                if (earlyRoute == "settings/account") earlyWindow.ShowAccount(); else earlyWindow.ShowSettings();
                await earlyWindow.InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(3));
                earlyWindow.AssertNavigationPresentation(earlyRoute);
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                    $"preload-navigation-{themeArgument}: requested={earlyRoute}, actual={earlyWindow.CurrentRoute}, title={earlyWindow.CurrentPageTitle}{Environment.NewLine}");
            }
            finally { earlyWindow.AllowClose = true; earlyWindow.Close(); }
        }
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
        RootPathBox.Text = @"C:\CloudBay UI validation\CloudBay";
        _viewModel.SetPreview(ClientPreview.Connected());
        Refresh();
        if (UploadLimitBox.Value != 512 ||
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
            AccountSettingsDetail.Visibility != Visibility.Collapsed || UploadLimitBox.Text != "invalid draft")
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
            var initialHistory = ClientPreview.ActivityActions();
            SetPresentation(initialHistory, theme);
            ActivityFilterBox.SelectedIndex = 3;
            var retainedHistoryRow = _viewModel.Activity[0];
            var retainedHistorySource = _viewModel.ActivityRows;
            var newEvent = initialHistory.Activity[0] with { Time = initialHistory.Activity[0].Time.AddSeconds(1), Path = "Pictures/New screenshot.png",
                Location = initialHistory.Activity[0].Location! with { RelativePath = "Pictures/New screenshot.png" } };
            SetPresentation(initialHistory with { Activity = new[] { newEvent }.Concat(initialHistory.Activity).ToArray() }, theme);
            if (!ReferenceEquals(retainedHistoryRow, _viewModel.Activity[1]) ||
                !ReferenceEquals(retainedHistoryRow, _viewModel.RecentActivity[1]) ||
                !ReferenceEquals(retainedHistorySource, _viewModel.ActivityRows))
                throw new InvalidOperationException("Adding one completed activity must retain existing history rows and the list's scroll source.");
            foreach (var width in new[] { 800, 1300 })
            {
                AppWindow.Resize(new SizeInt32(width, 840));
                var oldHistorySource = _viewModel.ActivityRows;
                SetPresentation(ClientPreview.ActivityActions(), theme);
                if (ReferenceEquals(oldHistorySource, _viewModel.ActivityRows))
                    throw new InvalidOperationException("A wholly replaced history must swap its native list source atomically.");
                ActivityFilterBox.SelectedIndex = 3;
                await CapturePageAsync("activity", $"activity-row-actions-{width}{suffix}");
                AssertActivityActions(_viewModel.Activity[0], openFolder: true, cloud: true);
                AssertActivityActions(_viewModel.Activity[1], openFolder: true, cloud: true);
                AssertActivityActions(_viewModel.Activity[2], openFolder: true, cloud: false);
                foreach (var row in _viewModel.Activity.Skip(3))
                {
                    ActivityList.ScrollIntoView(row);
                    await WaitForUiAsync(() => IsActivityRowVisible(row), "Retained history must remain reachable without actions that guess its account.");
                    AssertActivityActions(row, openFolder: false, cloud: false);
                }
                var historyCapture = Path.Combine(outputDirectory, $"activity-row-actions-history-{width}{suffix}.png");
                await UiSmokeCapture.SaveAsync(RootGrid, historyCapture);
                await AssertCapturedActivityInkAsync(historyCapture);
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "activity-action-assertions.txt"),
                    $"PASS: {theme} {width}px exact main/custom file rows offer local folder and B2-version actions; folder rows offer only local actions; old-account/untagged history has no guessed target; actions stay inside their row and leave filenames visible.{Environment.NewLine}");
            }
            AppWindow.Resize(new SizeInt32(1100, 840));
            var transferPreview = ClientPreview.TransferQueue();
            SetPresentation(transferPreview, theme);
            ActivityFilterBox.SelectedIndex = 0;
            if (_viewModel.ActiveTransfers.Count != 3 || _viewModel.QueuedTransfers.Count != 253 ||
                _viewModel.RecentTransfers.Count != 3 || !_viewModel.QueueCoverage.Contains("360", StringComparison.Ordinal) ||
                _viewModel.ActivityRows.Take(3).Any(row => row is not TransferItem))
                throw new InvalidOperationException("Active uploads and downloads must precede queued files and history, with an honest bounded-queue count.");
            var retainedTransferRow = _viewModel.ActiveTransfers[0];
            var progressedTransfers = transferPreview.Snapshot.Transfers.Select((transfer, index) =>
                index == 0 ? transfer with { Bytes = transfer.Bytes + 1048576 } : transfer).ToArray();
            SetPresentation(transferPreview with { Snapshot = transferPreview.Snapshot with { Transfers = progressedTransfers } }, theme);
            if (!ReferenceEquals(retainedTransferRow, _viewModel.ActiveTransfers[0]))
                throw new InvalidOperationException("A progress refresh must update its existing row rather than rebuild the transfer list.");
            ActivityFilterBox.SelectedIndex = 1;
            if (_viewModel.ActivityRows.Count != 3 || _viewModel.ActivityRows.Any(row => row is not TransferItem))
                throw new InvalidOperationException("The In progress filter must show only live uploads and downloads.");
            await CapturePageAsync("activity", $"activity-transferring{suffix}");
            AssertVisibleActivityRow(_viewModel.ActiveTransfers[0]);
            AssertActivityActions(_viewModel.ActiveTransfers[0], openFolder: true, cloud: false);
            AssertActivityActions(_viewModel.ActiveTransfers[1], openFolder: true, cloud: true);
            if (ActivityTransferSpeed.Visibility != Visibility.Visible ||
                !ActivityTransferSpeed.Text.Contains("MiB/s", StringComparison.Ordinal) ||
                !ActivityTransferSpeed.Text.Contains("KiB/s", StringComparison.Ordinal) ||
                FindDescendant<TextBlock>((DependencyObject)ActivityList.ContainerFromItem(_viewModel.ActiveTransfers[0]),
                    label => label.Name == "ActivityFileSpeed") is not { ActualHeight: > 0, Visibility: Visibility.Visible } uploadSpeed ||
                uploadSpeed.Text != _viewModel.ActiveTransfers[0].SpeedText ||
                FindDescendant<TextBlock>((DependencyObject)ActivityList.ContainerFromItem(_viewModel.ActiveTransfers[1]),
                    label => label.Name == "ActivityFileSpeed") is not { ActualHeight: > 0, Visibility: Visibility.Visible } downloadSpeed ||
                downloadSpeed.Text != _viewModel.ActiveTransfers[1].SpeedText)
                throw new InvalidOperationException("Activity must render measured upload and download speed for each transferring file and their aggregate directions.");
            foreach (var mixedState in new[] { ClientState.Attention, ClientState.Offline })
            {
                SetPresentation(transferPreview with { Snapshot = transferPreview.Snapshot with
                {
                    State = mixedState,
                    Message = "Another sync folder needs attention while these files continue transferring."
                } }, theme);
                await CapturePageAsync("activity", $"activity-transferring-{mixedState.ToString().ToLowerInvariant()}{suffix}");
                if (ActivityTransferSpeed.Visibility != Visibility.Visible ||
                    _viewModel.ActiveTransfers.Count(row => row.SpeedVisibility == Visibility.Visible) != 2)
                    throw new InvalidOperationException("A root needing attention or waiting for a connection must not hide another root's measured live transfer rates.");
            }
            SetPresentation(transferPreview with { Snapshot = transferPreview.Snapshot with { State = ClientState.Paused } }, theme);
            if (ActivityTransferSpeed.Visibility != Visibility.Collapsed ||
                _viewModel.ActiveTransfers.Any(row => row.SpeedVisibility == Visibility.Visible))
                throw new InvalidOperationException("Global pause must suppress both aggregate and per-file wire rates even if a native request still retains its transfer phase.");
            SetPresentation(transferPreview, theme);
            ActivityFilterBox.SelectedIndex = 2;
            if (_viewModel.ActivityRows.Count != 253 || ActivityQueueCoverage.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The queue filter must show queued files and disclose its complete queue count.");
            await CapturePageAsync("activity", $"activity-queue{suffix}");
            AssertVisibleActivityRow(_viewModel.QueuedTransfers[0]);
            if (_viewModel.QueuedTransfers.Any(row => row.SpeedVisibility != Visibility.Collapsed))
                throw new InvalidOperationException("Queued files must not display transfer speeds.");
            ActivityList.ScrollIntoView(_viewModel.QueuedTransfers[^1]);
            await Task.Delay(250);
            var queueScroller = FindDescendant<ScrollViewer>(ActivityList);
            if (queueScroller is null || queueScroller.ScrollableHeight <= 0 || queueScroller.VerticalOffset <= 0)
                throw new InvalidOperationException("The virtualized queue must allow its final displayed item to be reached.");
            AssertVisibleActivityRow(_viewModel.QueuedTransfers[^1]);
            var queueCapture = Path.Combine(outputDirectory, $"activity-queue-bottom{suffix}.png");
            await UiSmokeCapture.SaveAsync(RootGrid, queueCapture);
            await AssertCapturedActivityInkAsync(queueCapture);
            ActivityFilterBox.SelectedIndex = 3;
            if (_viewModel.ActivityRows.Count != transferPreview.Activity.Count || _viewModel.ActivityRows.Any(row => row is TransferItem))
                throw new InvalidOperationException("History must remain separate from in-progress and queued transfers.");
            await CapturePageAsync("activity", $"activity-history-filter{suffix}");
            var pausedPreview = transferPreview with { Snapshot = transferPreview.Snapshot with
            {
                State = ClientState.Paused, ActiveTransfers = 0, QueuedTransfers = 363,
                Transfers = transferPreview.Snapshot.Transfers.Select(transfer => transfer with { Phase = TransferPhase.Paused }).ToArray()
            } };
            SetPresentation(pausedPreview, theme);
            ActivityFilterBox.SelectedIndex = 1;
            if (_viewModel.HasTransfers || _viewModel.ActiveTransfers.Count != 0 || _viewModel.ActivityRows.Count != 0)
                throw new InvalidOperationException("Paused files must remain pending without appearing to be actively transferring.");
            ActivityFilterBox.SelectedIndex = 2;
            if (_viewModel.QueuedTransfers.Count != 256 || !_viewModel.QueueCoverage.Contains("363", StringComparison.Ordinal))
                throw new InvalidOperationException("Paused files must retain their pending status and honest queue count.");
            await CapturePageAsync("activity", $"activity-paused{suffix}");
            if (ActivityTransferSpeed.Visibility != Visibility.Collapsed ||
                _viewModel.QueuedTransfers.Any(row => row.SpeedVisibility != Visibility.Collapsed))
                throw new InvalidOperationException("Paused files and the paused Activity header must not display stale transfer speeds.");
            SetPresentation(transferPreview, theme);
            ActivityFilterBox.SelectedIndex = 0;
            OpenSettingsRoute("network");
            UploadModeBox.SelectedIndex = 2;
            ConcurrencyBox.Value = 7;
            DownloadConcurrencyBox.Value = 3;
            _viewModel.SetPreview(transferPreview with { Settings = transferPreview.Settings with { Theme = theme.ToString(), PauseOnMetered = false } });
            LoadSettings(reloadAccount: true, reloadPreferences: false);
            Refresh();
            if (SelectedUploadMode != UploadMode.Manual || ManualConcurrencyCard.Visibility != Visibility.Visible ||
                ManualDownloadConcurrencyCard.Visibility != Visibility.Visible || ConcurrencyBox.Value != 7 || DownloadConcurrencyBox.Value != 3)
                throw new InvalidOperationException("A background refresh must retain manual transfer mode and both concurrency drafts.");
            await CapturePageAsync("settings", $"settings-transfers-manual{suffix}", "network");
            UploadModeBox.SelectedIndex = 1;
            if (ManualConcurrencyCard.Visibility != Visibility.Collapsed || ManualDownloadConcurrencyCard.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Automatic transfer modes must hide irrelevant manual controls.");
            await CapturePageAsync("settings", $"settings-transfers-maximum{suffix}", "network");
            UploadModeBox.SelectedIndex = 0;
            LoadSettings(reloadPreferences: true);
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "transfers-validation.txt"),
                $"PASS: {theme} rendered live upload/download rows precede history, measured per-file and aggregate directional speeds are visible, queued/paused rates are hidden, filtered queue first/last filenames intersect the viewport, progress reuses containers, and transfer mode preserves both manual drafts.{Environment.NewLine}");
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
                var bottomCapture = Path.Combine(outputDirectory, $"activity-history-bottom-{width}{suffix}.png");
                await UiSmokeCapture.SaveAsync(RootGrid, bottomCapture);
                await AssertCapturedActivityInkAsync(bottomCapture);
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
            AppWindow.Resize(new SizeInt32(1600, 840));
            SetPresentation(ClientPreview.Connected(), theme);
            await CapturePageAsync("settings", $"settings-home-1600{suffix}");
            AppWindow.Resize(new SizeInt32(1100, 840));
            SetPresentation(ClientPreview.Connected(), theme);
            ExclusionsExpander.IsExpanded = true;
            Navigation.SelectedItem = Navigation.SettingsItem;
            ShowPage("settings");
            OpenSettingsRoute("sync");
            await Task.Delay(100);
            ExclusionsExpander.StartBringIntoView();
            await ExclusionsEditor.RunUiValidationAsync(outputDirectory, async name =>
            {
                await Task.Delay(100);
                if (name.Contains("save-failure", StringComparison.Ordinal))
                    SettingsPage.ChangeView(null, SettingsPage.ScrollableHeight, null, true);
                await Task.Delay(100);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, name + ".png"));
            });
            ExclusionsExpander.IsExpanded = false;
            AppWindow.Resize(new SizeInt32(800, 840));
            var externalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Another provider", "Desktop");
            SetPresentation(ClientPreview.Connected() with { WindowsFolderPaths = new Dictionary<string, string>
            {
                ["Desktop"] = externalPath,
                ["Documents"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Another provider", "Documents")
            } }, theme);
            await CapturePageAsync("backup", $"backup-location-attention-800{suffix}");
            if (_backupSwitches["Desktop"].IsOn || _backupSwitches["Desktop"].IsEnabled || !_backupSwitches["Documents"].IsEnabled ||
                _backupPaths["Desktop"].Text != "Windows folder location changed" || _backupPaths["Documents"].Text != "Review current Windows location" ||
                !AutomationProperties.GetHelpText(_backupSwitches["Desktop"]).Contains(externalPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Changed owned mappings must remain protected; external Windows mappings must expose their actual location for explicit review.");
            foreach (var importWidth in new[] { 800, 1300 })
            {
                AppWindow.Resize(new SizeInt32(importWidth, 840));
                SetPresentation(ClientPreview.Connected(), theme);
                await CapturePageAsync("backup", $"backup-import-entry-{importWidth}{suffix}");
                if (CloudImportSection.Visibility != Visibility.Visible || CloudImportRows.Children.Count != 2 ||
                    CloudImportRows.Children.OfType<SettingsCard>().Any(card => (card.Tag as string)?.EndsWith("Documents", StringComparison.Ordinal) == true))
                    throw new InvalidOperationException("The quick import section must show distinct whole cloud accounts, separate from Windows folder choices.");
                CloudImportSection.StartBringIntoView();
                await Task.Delay(180);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"backup-cloud-accounts-{importWidth}{suffix}.png"));
                // No controller operation or Windows mapping changes occur in
                // these fixtures. They prove that a queued second folder and an
                // active first folder do not disable unrelated folder switches.
                _backupJobs["Desktop"] = new(false, "Copying and verifying files…");
                _backupJobs["Documents"] = new(true, "Queued for review…");
                try
                {
                    RefreshBackups();
                    UpdateBackupBusyFooter();
                    await CapturePageAsync("backup", $"backup-per-folder-queue-{importWidth}{suffix}");
                    if (_backupSwitches["Desktop"].IsEnabled || _backupSwitches["Documents"].IsEnabled ||
                        !_backupSwitches["Pictures"].IsEnabled || !_backupSwitches["Downloads"].IsEnabled ||
                        !_backupSwitches["Desktop"].IsOn || _backupSwitches["Documents"].IsOn ||
                        !_backupRings["Desktop"].IsActive || !_backupRings["Documents"].IsActive ||
                        _backupPaths["Documents"].Text != "Queued for review…")
                        throw new InvalidOperationException("Folder changes must keep effective backup state until committed and leave unrelated folders selectable.");
                }
                finally
                {
                    _backupJobs.Clear();
                    RefreshBackups();
                    UpdateBackupBusyFooter();
                }
                _importInProgress = true;
                _importStatus = "Import in progress; transfers start when ready.";
                try
                {
                    RefreshBackups();
                    RefreshCustomBackups();
                    UpdateBackupBusyFooter();
                    await CapturePageAsync("backup", $"backup-import-does-not-block-folders-{importWidth}{suffix}");
                    if (_busy || !_backupSwitches["Desktop"].IsEnabled || !_backupSwitches["Documents"].IsEnabled ||
                        !_backupSwitches["Pictures"].IsEnabled || !_backupSwitches["Downloads"].IsEnabled ||
                        ImportFilesCard.IsEnabled || CloudImportRows.Children.OfType<SettingsCard>().Any(card => card.IsEnabled) ||
                        ConnectButton.IsEnabled || DisconnectButton.IsEnabled || BusyFooter.Visibility != Visibility.Visible)
                        throw new InvalidOperationException("A background import must block duplicate imports and connection changes while leaving unrelated Windows folder choices available.");
                }
                finally
                {
                    _importInProgress = false;
                    _importStatus = "";
                    RefreshBackups();
                    RefreshCustomBackups();
                    UpdateBackupBusyFooter();
                }
                var customFixture = DisplaySettings.CustomBackups.Single();
                var customFixtureKey = CustomStopJobKey(customFixture.Name);
                _backupJobs[customFixtureKey] = new(false, "Preparing local files; starts when ready…");
                try
                {
                    RefreshBackups();
                    RefreshCustomBackups();
                    UpdateBackupBusyFooter();
                    await CapturePageAsync("backup", $"backup-custom-stop-does-not-block-folders-{importWidth}{suffix}");
                    var customCard = CustomBackupRows.Children.OfType<SettingsCard>().Single(card =>
                        card.Tag as string == customFixture.SourcePath);
                    var actions = (StackPanel)customCard.Content;
                    var more = actions.Children.OfType<Button>().Single(button => button.Flyout is MenuFlyout);
                    if (_busy || !_backupSwitches["Desktop"].IsEnabled || !_backupSwitches["Documents"].IsEnabled ||
                        !_backupSwitches["Pictures"].IsEnabled || !_backupSwitches["Downloads"].IsEnabled ||
                        more.IsEnabled || ((MenuFlyout)more.Flyout).Items.OfType<MenuFlyoutItem>().Any(item => item.IsEnabled) ||
                        !AddCustomBackupButton.IsEnabled || !ImportFilesCard.IsEnabled)
                        throw new InvalidOperationException("A pending custom-folder stop must disable only that row's stop actions while unrelated folder and import choices remain available.");
                    _backupJobs[CustomAddJobKey] = new(true, "Preparing backup; starts when ready…");
                    RefreshBackups();
                    RefreshCustomBackups();
                    UpdateBackupBusyFooter();
                    if (AddCustomBackupButton.IsEnabled || !_backupSwitches["Pictures"].IsEnabled || !ImportFilesCard.IsEnabled)
                        throw new InvalidOperationException("A custom-folder setup must block duplicate adds without blocking unrelated Windows folders or imports.");
                    CustomBackupSection.StartBringIntoView();
                    await Task.Delay(180);
                    await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"backup-custom-folder-queue-{importWidth}{suffix}.png"));
                }
                finally
                {
                    _backupJobs.Remove(customFixtureKey);
                    _backupJobs.Remove(CustomAddJobKey);
                    RefreshBackups();
                    RefreshCustomBackups();
                    UpdateBackupBusyFooter();
                }
                var candidates = new ImportSourceCandidate[]
                {
                    new("synthetic-onedrive", "OneDrive · Documents", "Microsoft OneDrive", @"C:\Users\Example\OneDrive\Documents", "Registered cloud folder"),
                    new("synthetic-existing", "OneDrive - Work", "Microsoft OneDrive", @"D:\Personal files\OneDrive - Work", "Existing Windows folder")
                };
                var source = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
                { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                await CaptureImportDialogAsync(source, $"import-source-chooser-{importWidth}{suffix}");
                var existing = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
                { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                await existing.ShowExistingFoldersAsync();
                await CaptureImportDialogAsync(existing, $"import-existing-folders-{importWidth}{suffix}");
                var providers = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
                { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                providers.ShowProviders();
                await CaptureImportDialogAsync(providers, $"import-cloud-providers-{importWidth}{suffix}");
                var buckets = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
                { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                buckets.ShowB2Configuration([new("synthetic-source-bucket", "Personal archive")]);
                if (!buckets.IsPrimaryButtonEnabled) throw new InvalidOperationException("An accessible bucket and destination must allow cloud import review.");
                await CaptureImportDialogAsync(buckets, $"import-b2-bucket-{importWidth}{suffix}");
                var cloudPlan = new CloudBay.Core.Sync.CloudImportPlan(Guid.NewGuid().ToString("N"), "synthetic-source-bucket", "Documents/",
                    @"C:\Users\Example\CloudBay\Imported B2 files", [new("report.pdf", new("immutable-source-version", "Documents/report.pdf", 42_017,
                        "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3", DateTimeOffset.UtcNow))], 1, 42_017);
                var cloudReview = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
                { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                cloudReview.ShowCloudReview(cloudPlan, bucketName: "Personal archive");
                if (cloudReview.CloudPlan != cloudPlan || !cloudReview.IsPrimaryButtonEnabled)
                    throw new InvalidOperationException("A cloud review must retain its immutable source versions and exact destination plan.");
                await CaptureImportDialogAsync(cloudReview, $"import-b2-review-{importWidth}{suffix}");
                var syntheticPlan = new CloudBay.Core.Sync.FolderImportPlan(@"C:\Users\Example\OneDrive\Documents",
                    @"C:\Users\Example\CloudBay\Documents", "presentation-only", 1248, 2_742_910_976L, 128_849_018_880L, true);
                var review = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
                { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                review.ShowFolderReview(syntheticPlan, "Documents");
                if (review.FolderPlan != syntheticPlan || !review.IsPrimaryButtonEnabled)
                    throw new InvalidOperationException("An import review must retain the exact plan its user sees.");
                await CaptureImportDialogAsync(review, $"import-folder-review-{importWidth}{suffix}");
                var matchingCandidates = new ImportSourceCandidate[]
                {
                    new("synthetic-personal-documents", "OneDrive · Documents", "Microsoft OneDrive", @"C:\Users\Example\OneDrive\Documents", "Windows folder"),
                    new("synthetic-work-documents", "OneDrive · Work · Documents", "Microsoft OneDrive", @"D:\Personal files\OneDrive - Work\Documents", "Windows folder")
                };
                var fixtureSettings = DisplaySettings with { RootPath = @"C:\Users\Example\CloudBay", Backups = [], CustomBackups = [] };
                foreach (var mode in new[] { BackupTransferMode.Copy, BackupTransferMode.Move, BackupTransferMode.None })
                {
                    var modeName = mode.ToString().ToLowerInvariant();
                    var setup = new BackupSetupDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), "Documents",
                        presentationSettings: fixtureSettings, presentationCandidates: matchingCandidates)
                    { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                    setup.SetPresentationChoice(mode, mode == BackupTransferMode.None ? null : matchingCandidates[0].Path);
                    if (setup.TransferMode != mode || (mode == BackupTransferMode.None ? setup.IncludeCurrentFiles :
                        setup.SourcePath != matchingCandidates[0].Path || setup.IncludeCurrentFiles))
                        throw new InvalidOperationException("Setup must keep the exact selected source separate from unselected current Windows files.");
                    await CaptureImportDialogAsync(setup, $"backup-setup-{modeName}-{importWidth}{suffix}");
                    setup.ValidateVisibleSelection();
                    if (setup.TransferMode != mode || (mode == BackupTransferMode.None ? setup.IncludeCurrentFiles :
                        setup.SourcePath != matchingCandidates[0].Path || setup.IncludeCurrentFiles))
                        throw new InvalidOperationException("The rendered backup setup must preserve the source and mode that its controls visibly selected.");
                    var nativeReview = new BackupReviewDialog(new BackupSourceReview("Documents", @"C:\Users\Example\Documents",
                        syntheticPlan with { SourcePath = @"C:\Users\Example\Documents", FileCount = 0, TotalBytes = 0 },
                        mode == BackupTransferMode.None ? null : syntheticPlan, false, mode, false))
                    { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                    await CaptureImportDialogAsync(nativeReview, $"backup-source-review-{modeName}-{importWidth}{suffix}");
                    var folder = new BackupFolder("Documents", @"C:\Users\Example\Documents", syntheticPlan.DestinationPath);
                    var stopSetup = new BackupSetupDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), "Documents", stopping: true,
                        presentationSettings: fixtureSettings with { Backups = [folder] }, presentationCandidates: matchingCandidates)
                    { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                    stopSetup.SetPresentationChoice(mode, mode == BackupTransferMode.None ? null : matchingCandidates[1].Path,
                        freeLocalSpace: mode == BackupTransferMode.None);
                    if (stopSetup.TransferMode != mode || stopSetup.DestinationPath !=
                        (mode == BackupTransferMode.None ? folder.OriginalPath : matchingCandidates[1].Path) ||
                        stopSetup.FreeLocalSpace != (mode == BackupTransferMode.None))
                        throw new InvalidOperationException("Stopping backup must retain the chosen Windows destination, transfer mode, and optional local-space choice.");
                    await CaptureImportDialogAsync(stopSetup, $"backup-stop-setup-{modeName}-{importWidth}{suffix}");
                    stopSetup.ValidateVisibleSelection();
                    if (stopSetup.TransferMode != mode || stopSetup.DestinationPath !=
                        (mode == BackupTransferMode.None ? folder.OriginalPath : matchingCandidates[1].Path) ||
                        stopSetup.FreeLocalSpace != (mode == BackupTransferMode.None))
                        throw new InvalidOperationException("The rendered stop setup must preserve its visible Windows destination, transfer mode, and local-space choice.");
                    var stopDestination = mode == BackupTransferMode.None ? folder.OriginalPath : matchingCandidates[1].Path;
                    var restorePlan = mode == BackupTransferMode.None ? null : syntheticPlan with
                    { SourcePath = folder.DestinationPath, DestinationPath = stopDestination };
                    var stopReview = new BackupReviewDialog(new BackupRestoreReview(folder, stopDestination, mode, restorePlan),
                        freeLocalSpace: mode == BackupTransferMode.None)
                    { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
                    await CaptureImportDialogAsync(stopReview, $"backup-stop-review-{modeName}-{importWidth}{suffix}");
                }
                await CaptureImportHistoryAsync(candidates, importWidth, theme, suffix);
            }
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

        async Task CaptureImportDialogAsync(ContentDialog dialog, string name)
        {
            var showing = dialog.ShowAsync();
            try
            {
                await Task.Delay(350);
                dialog.UpdateLayout();
                if (dialog.ActualWidth <= 0 || dialog.ActualHeight <= 0 || dialog.ActualWidth > RootGrid.ActualWidth + 1)
                    throw new InvalidOperationException("The import dialog must fit the live window and expose visible bounds.");
                await UiSmokeCapture.SaveAsync(dialog, Path.Combine(outputDirectory, name + ".png"));
            }
            finally { dialog.Hide(); await showing; }
        }

        async Task CaptureImportHistoryAsync(IReadOnlyList<ImportSourceCandidate> candidates, int width, ElementTheme theme, string suffix)
        {
            // Presentation records never read a checkpoint, touch a source folder,
            // or call a provider. Mixed timestamps prove that interrupted work
            // remains reachable even when newer completed work spans several pages.
            var now = DateTimeOffset.UtcNow;
            const string fixtureRoot = @"C:\Users\Example\CloudBay";
            var folders = Enumerable.Range(0, 12).Select(index =>
            {
                var destination = Path.Combine(fixtureRoot, $"Local import {index + 1:00}");
                var plan = new CloudBay.Core.Sync.FolderImportPlan(@"D:\Previous cloud\Archive " + index,
                    destination, new string('a', 64), 120 + index, 2_742_910_976L, 128_849_018_880L, index == 0);
                return new FolderImportRecord(Guid.NewGuid().ToString("N"), index == 11 ? now.AddDays(-120) : now.AddHours(-index),
                    plan, "presentation-only", index < 3 ? "Needs review" : "Completed",
                    index < 3 ? "The copy was interrupted. The source and verified destination files were retained." : null);
            }).ToArray();
            var clouds = Enumerable.Range(0, 12).Select(index =>
            {
                var id = Guid.NewGuid().ToString("N");
                var plan = new CloudBay.Core.Sync.CloudImportPlan(id, "synthetic-source-bucket", "Previous files/",
                    Path.Combine(fixtureRoot, $"Cloud import {index + 1:00}"),
                    [new("report.pdf", new("immutable-source-" + index, "Previous files/report.pdf", 42_017,
                        "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3", now))], 1, 42_017);
                return new CloudBay.Core.Sync.CloudImportRecord(id, index == 0 ? now.AddDays(-90) : now.AddHours(-index - 12),
                    plan, "presentation-only", index == 0 ? "Needs review" : "Completed", new Dictionary<string, CloudObject>(),
                    index == 0 ? "The cloud import was interrupted. Completed versions were retained." : null);
            }).ToArray();
            var history = new SourceImportDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this), presentationCandidates: candidates)
            { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
            history.ShowHistoryPresentation(folders, clouds, issues: 1);
            var oldestPending = clouds[0].Plan.DestinationPath;
            var oldestCompleted = folders[^1].Plan.DestinationPath;
            if (!history.IsHistoryDestinationDisplayed(oldestPending) ||
                folders.Take(3).Any(item => !history.IsHistoryDestinationDisplayed(item.Plan.DestinationPath)) ||
                history.IsHistoryDestinationDisplayed(oldestCompleted) || !history.CanAdvanceHistoryPage)
                throw new InvalidOperationException("Import history must merge source kinds, prioritize every interrupted fixture job, and expose paging for completed work.");
            var showing = history.ShowAsync();
            try
            {
                await Task.Delay(350);
                history.UpdateLayout();
                if (history.ActualWidth <= 0 || history.ActualWidth > RootGrid.ActualWidth + 1)
                    throw new InvalidOperationException("Import history must fit the live window.");
                if (FindDescendant<InfoBar>(history, item => item.IsOpen && item.Message?.Contains("damaged or unreadable", StringComparison.Ordinal) == true) is null)
                    throw new InvalidOperationException("Unreadable import checkpoints must expose a visible review warning.");
                history.ShowHistoryDestinationInViewport(oldestPending);
                await Task.Delay(250);
                AssertHistoryDestinationVisible(oldestPending);
                await UiSmokeCapture.SaveAsync(history, Path.Combine(outputDirectory, $"import-history-first-{width}{suffix}.png"));
                var pages = 1;
                while (history.CanAdvanceHistoryPage)
                {
                    if (++pages > 4) throw new InvalidOperationException("Synthetic import history paging failed to terminate.");
                    history.NavigateHistoryPage(1);
                    await Task.Delay(100);
                }
                if (pages != 3 || !history.IsHistoryDestinationDisplayed(oldestCompleted) || history.IsHistoryDestinationDisplayed(oldestPending))
                    throw new InvalidOperationException("Every mixed import history page must be reachable, including its oldest completed record.");
                history.ShowHistoryDestinationInViewport(oldestCompleted);
                await Task.Delay(250);
                AssertHistoryDestinationVisible(oldestCompleted);
                await UiSmokeCapture.SaveAsync(history, Path.Combine(outputDirectory, $"import-history-last-{width}{suffix}.png"));
                for (var page = 1; page < pages; page++) history.NavigateHistoryPage(-1);
                await Task.Delay(100);
                if (!history.IsHistoryDestinationDisplayed(oldestPending) || !history.CanAdvanceHistoryPage)
                    throw new InvalidOperationException("Previous must return from the oldest import page to interrupted work.");
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "imports-validation.txt"),
                    $"PASS: {theme} {width}px import history merges 24 local/cloud records across {pages} reachable pages; old interrupted cloud work remains on page one; damaged checkpoints are visible; oldest completed record intersects the scroll viewport; Previous returns to pending work.{Environment.NewLine}");
            }
            finally { history.Hide(); await showing; }

            void AssertHistoryDestinationVisible(string destination)
            {
                history.UpdateLayout();
                var row = FindDescendant<SettingsCard>(history, item => item.Tag as string == destination) ??
                    throw new InvalidOperationException("The selected history record has no rendered row.");
                var viewer = FindDescendant<ScrollViewer>(history, item => ReferenceEquals(item.Content, history.CaptureContent)) ??
                    throw new InvalidOperationException("Import history must retain its scroll viewport.");
                var position = row.TransformToVisual(viewer).TransformPoint(new global::Windows.Foundation.Point());
                if (row.ActualWidth <= 0 || row.ActualHeight <= 0 || position.X + row.ActualWidth <= 0 || position.X >= viewer.ActualWidth ||
                    position.Y + row.ActualHeight <= 0 || position.Y >= viewer.ActualHeight)
                    throw new InvalidOperationException("The selected import record must intersect the visible scroll viewport.");
            }
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
            // Verify the native NavigationView content layer at runtime. Its
            // rounded surface contains both the fixed header and task content;
            // there must not be another painted shell around the account form.
            DependencyObject? surface = VisualTreeHelper.GetParent(ContentLayoutGrid);
            while (surface is not null && surface is not Grid { Name: "ContentGrid" })
                surface = VisualTreeHelper.GetParent(surface);
            if (surface is Grid measuredContent)
            {
                var fill = measuredContent.Background is SolidColorBrush solid ? solid.Color.ToString() : measuredContent.Background?.GetType().Name ?? "none";
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                    $"{fileName}: native content surface fill={fill}, border={measuredContent.BorderThickness}, mode={Navigation.DisplayMode}{Environment.NewLine}");
            }
            if (surface is not Grid nativeContent || nativeContent.Background is not SolidColorBrush { Color.A: > 0 } ||
                !nativeContent.BorderThickness.Equals(new Thickness(0)) ||
                !nativeContent.CornerRadius.Equals(new CornerRadius(8, 0, 0, 0)))
                throw new InvalidOperationException("The native content layer must paint a single foreground surface with its rounded upper-left corner.");
            // These values belong to the pinned WinUI runtime's normal theme
            // dictionaries. Comparing every channel catches an alias frozen
            // to the prior theme even though its opacity remains nonzero.
            var lightTheme = RootGrid.ActualTheme == ElementTheme.Light;
            var expectedSurfaceColor = lightTheme ? global::Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                : global::Windows.UI.Color.FromArgb(0x4C, 0x3A, 0x3A, 0x3A);
            if (!((SolidColorBrush)nativeContent.Background).Color.Equals(expectedSurfaceColor))
                throw new InvalidOperationException("The native content brush must follow the current window theme after a theme change.");
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                $"{fileName}: native surface background={nativeContent.Background?.GetType().Name ?? "none"}, margin={nativeContent.Margin}, corners={nativeContent.CornerRadius}, mode={Navigation.DisplayMode}{Environment.NewLine}");
            var foregroundPosition = nativeContent.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
            if (foregroundPosition.X < -1 || foregroundPosition.Y < AppTitleBar.ActualHeight - 1 ||
                foregroundPosition.X + nativeContent.ActualWidth > RootGrid.ActualWidth + 1 ||
                foregroundPosition.Y + nativeContent.ActualHeight > RootGrid.ActualHeight + 1)
                throw new InvalidOperationException("The native content layer must remain within the Mica window.");
            var headerPosition = PageHeader.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
            var selectedPage = page switch
            {
                "activity" => (FrameworkElement)ActivityPage, "backup" => BackupPage,
                "files" => FilesPage, "settings" => SettingsPage, _ => OverviewPage
            };
            var pagePosition = selectedPage.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
            var contentPosition = ForegroundLayout.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
            if (headerPosition.Y < AppTitleBar.ActualHeight - 1 ||
                contentPosition.Y + 1 < headerPosition.Y + PageHeader.ActualHeight ||
                pagePosition.Y + 1 < contentPosition.Y || !IsWithin(PageTitle, PageHeader) ||
                !IsWithin(PageHeader, nativeContent) || !IsWithin(selectedPage, nativeContent) ||
                IsWithin(PageTitle, selectedPage) || !IsWithin(selectedPage, ForegroundLayout) ||
                PageHeader.ActualWidth > ContentLayoutGrid.ActualWidth + 1 ||
                ForegroundLayout.Background is SolidColorBrush { Color.A: > 0 } ||
                ConnectionPanel.Background is not null || !ConnectionPanel.BorderThickness.Equals(new Thickness(0)))
                throw new InvalidOperationException("The fixed header and scrolling pages must share the native foreground layer without a second shell around the content or account form.");
            if ((SettingsBackButton.Visibility == Visibility.Visible) != (page == "settings" && _settingsRoute != "home"))
                throw new InvalidOperationException("Only a settings detail may expose the shared header's Back action.");
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                $"{fileName}: page header y={headerPosition.Y}, height={PageHeader.ActualHeight}, content host y={contentPosition.Y}, task content y={pagePosition.Y}, title={PageTitle.Text}{Environment.NewLine}");
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
                if (page == "settings" && _settingsRoute == "home")
                {
                    var headerLeft = headerPosition.X + PageHeader.Padding.Left;
                    var headerRight = headerPosition.X + PageHeader.ActualWidth - PageHeader.Padding.Right;
                    var contentLeft = position.X + SettingsContent.Padding.Left;
                    var contentRight = position.X + SettingsContent.ActualWidth - SettingsContent.Padding.Right;
                    if (Math.Abs(headerLeft - contentLeft) > 1 || Math.Abs(headerRight - contentRight) > 1)
                        throw new InvalidOperationException("The page header and Settings home must share their inner gutters, including at the maximum content width.");
                    await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"),
                        $"{fileName}: aligned header/content gutters left={headerLeft:0.##}/{contentLeft:0.##}, right={headerRight:0.##}/{contentRight:0.##}, content width={SettingsContent.ActualWidth:0.##}, theme={RootGrid.ActualTheme}{Environment.NewLine}");
                }
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
                if (_viewModel.ActivityRows.Count > 0 && !_viewModel.ActivityRows.Any(IsActivityRowVisible))
                    throw new InvalidOperationException("A nonempty Activity view must render at least one actual filename inside its viewport.");
            }
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(outputDirectory, $"{fileName}.png"));
            if (page == "activity" && _viewModel.HasActivityRows)
                await AssertCapturedActivityInkAsync(Path.Combine(outputDirectory, $"{fileName}.png"));
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

    private async Task AssertCapturedActivityInkAsync(string path)
    {
        var row = _viewModel.ActivityRows.FirstOrDefault(IsActivityRowVisible)
            ?? throw new InvalidOperationException("The captured Activity view must have a visible row.");
        var container = (FrameworkElement)ActivityList.ContainerFromItem(row);
        var label = FindDescendant<TextBlock>(container, item => item.Name == "ActivityFileName")!;
        var position = label.TransformToVisual(RootGrid).TransformPoint(new global::Windows.Foundation.Point());
        var scale = RootGrid.XamlRoot.RasterizationScale;
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.Read);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var provider = await decoder.GetPixelDataAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new global::Windows.Graphics.Imaging.BitmapTransform(),
            global::Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            global::Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var pixels = provider.DetachPixelData();
        var left = Math.Clamp((int)Math.Floor(position.X * scale), 0, (int)decoder.PixelWidth);
        var top = Math.Clamp((int)Math.Floor(position.Y * scale), 0, (int)decoder.PixelHeight);
        var right = Math.Clamp((int)Math.Ceiling((position.X + label.ActualWidth) * scale), left, (int)decoder.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((position.Y + label.ActualHeight) * scale), top, (int)decoder.PixelHeight);
        var lowest = 255;
        var highest = 0;
        var opaque = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var index = checked((y * (int)decoder.PixelWidth + x) * 4);
                if (pixels[index + 3] < 128) continue;
                opaque++;
                var tone = (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3;
                lowest = Math.Min(lowest, tone);
                highest = Math.Max(highest, tone);
            }
        if (_activityActionValidationPath is not null)
            await File.AppendAllTextAsync(_activityActionValidationPath,
                $"PIXELS: {Path.GetFileName(path)}; label={label.Text}; roi={left},{top}-{right},{bottom}; opaque={opaque}; tone={lowest}-{highest}{Environment.NewLine}");
        // Geometry alone can stay valid while a native virtualized container
        // loses its render state. Validate text ink in the saved PNG itself,
        // against either light or dark backgrounds, before accepting it.
        if (opaque < 16 || highest - lowest < 64)
            throw new InvalidOperationException("The captured Activity row has layout bounds but its file name was not rendered into the image.");
    }

    private bool IsActivityRowVisible(object row)
    {
        if (ActivityList.ContainerFromItem(row) is not FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } container ||
            FindDescendant<TextBlock>(container, label => label.Name == "ActivityFileName") is not
                { ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible } fileName) return false;
        var expected = row switch { TransferItem transfer => transfer.FileName, ActivityItem history => history.FileName, _ => "" };
        if (expected.Length == 0 || fileName.Text != expected) return false;
        var position = fileName.TransformToVisual(ActivityList).TransformPoint(new global::Windows.Foundation.Point());
        return position.X < ActivityList.ActualWidth && position.X + fileName.ActualWidth > 0 &&
            position.Y < ActivityList.ActualHeight && position.Y + fileName.ActualHeight > 0;
    }

    private void AssertVisibleActivityRow(object row)
    {
        if (!IsActivityRowVisible(row))
            throw new InvalidOperationException($"The Activity row '{row}' must render its filename inside the visible list viewport.");
    }

    private void AssertActivityActions(object row, bool openFolder, bool cloud)
    {
        var rendered = IsActivityRowVisible(row);
        if (_activityActionValidationPath is not null)
            File.AppendAllText(_activityActionValidationPath, $"ROW: {row}; visible={rendered}; folder={openFolder}; cloud={cloud}; target={(row as IActivityActionRow)?.Actions.Target}{Environment.NewLine}");
        AssertVisibleActivityRow(row);
        var container = (FrameworkElement)ActivityList.ContainerFromItem(row);
        if (_activityActionValidationPath is not null)
            File.AppendAllText(_activityActionValidationPath, $"CONTAINER: {container.ActualWidth}x{container.ActualHeight}; filename-width={FindDescendant<TextBlock>(container, item => item.Name == "ActivityFileName")?.ActualWidth}{Environment.NewLine}");
        foreach (var (name, expected) in new[] { ("ActivityOpenFolder", openFolder), ("ActivityViewCloud", cloud) })
        {
            var button = FindDescendant<Button>(container, item => item.Name == name)
                ?? throw new InvalidOperationException("The Activity row must contain its contextual action buttons.");
            var position = button.TransformToVisual(container).TransformPoint(new global::Windows.Foundation.Point());
            if (_activityActionValidationPath is not null)
                File.AppendAllText(_activityActionValidationPath, $"BUTTON: {name}; expected={expected}; actual={button.Visibility}; size={button.ActualWidth}x{button.ActualHeight}; position={position.X},{position.Y}{Environment.NewLine}");
            if ((button.Visibility == Visibility.Visible) != expected)
                throw new InvalidOperationException("Activity actions must reflect the row's exact current backup and cloud identity.");
            if (!expected) continue;
            if (button.ActualWidth < 100 || button.ActualHeight < 32 || position.X < 0 || position.Y < 0 ||
                position.X + button.ActualWidth > container.ActualWidth + 1 || position.Y + button.ActualHeight > container.ActualHeight + 1)
                throw new InvalidOperationException("Activity actions must stay fully inside the row at narrow and wide window sizes.");
        }
        if (FindDescendant<TextBlock>(container, item => item.Name == "ActivityFileName") is not
            { ActualWidth: > 0, Parent: FrameworkElement { ActualWidth: >= 80 } })
            throw new InvalidOperationException("Activity actions must leave room for the file name.");
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

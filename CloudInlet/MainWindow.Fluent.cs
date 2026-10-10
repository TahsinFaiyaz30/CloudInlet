using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CloudInlet.Views;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private void UpdateCaptionTheme()
    {
        // Caption buttons belong to AppWindow, not the XAML requested theme.
        // Keep them legible when the app theme differs from the Windows theme.
        var foreground = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            ? _uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground)
            : RootGrid.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private CloudServicesView? _cloudServicesView;
    private CloudServicesView? _homeServicesView;
    private int _connectedOneDriveAccounts;

    private void InitializeCloudServices()
    {
        _cloudServicesView = new CloudServicesView();
        _cloudServicesView.ConnectBackblazeRequested += (_, _) => ShowAccount();
        _cloudServicesView.OpenOneDriveRequested += (_, _) => RequestNavigationRoute("settings/onedrive");
        ServicesPage.Content = _cloudServicesView;
        _homeServicesView = new CloudServicesView(showComingSoon: false);
        _homeServicesView.ConnectBackblazeRequested += (_, _) => ShowAccount();
        _homeServicesView.OpenOneDriveRequested += (_, _) => RequestNavigationRoute("settings/onedrive");
        HomeServicesHost.Content = _homeServicesView;
        RefreshCloudServices();
    }

    private void RefreshCloudServices()
    {
        try
        {
            var count = _viewModel.Preview?.ConnectedOneDriveAccounts ?? _controller.OneDriveAccounts.Count;
            if (_viewModel.Preview is null) _connectedOneDriveAccounts = count;
            _cloudServicesView?.RefreshProviders(DisplaySettings.IsConfigured, count);
            _homeServicesView?.RefreshProviders(DisplaySettings.IsConfigured, count);
            RefreshStartupAvailability(DisplaySettings.IsConfigured || count > 0);
        }
        catch (Exception error)
        {
            RefreshStartupAvailability(DisplaySettings.IsConfigured);
            ShowError(error);
        }
    }

    private void RefreshStartupAvailability(bool hasConnectedAccount)
    {
        StartAtSignInBox.IsEnabled = hasConnectedAccount && !_busy;
        StartupSettingsCard.Description = hasConnectedAccount ? "Start CloudInlet automatically when you sign in." : "Connect an account to use automatic startup.";
    }

    private sealed record NavigationDestination(string Title, string Route, string Keywords)
    {
        public override string ToString() => Title;
    }

    // This index contains destinations only. Searching never reads file names,
    // account secrets, or cloud contents and never executes a transfer.
    private static readonly NavigationDestination[] NavigationDestinations =
    [
        new("Home", "overview", "overview status sync pause recent"),
        new("Activity", "activity", "transfer queue progress history speed cloud OneDrive copy move resume cancel"),
        new("Folder backup", "backup", "desktop documents pictures downloads folders custom import"),
        new("Files", "files", "explorer storage versions restore download pin free space"),
        new("Cloud services", "services", "providers connect accounts OneDrive Backblaze B2 Google Drive Dropbox S3 coming soon"),
        new("Backblaze account", "settings/account", "connect B2 bucket application key root prefix credentials"),
        new("OneDrive accounts", "settings/onedrive", "connect Microsoft personal work account sign in"),
        new("Files and storage", "settings/sync", "on demand exclusions exclude filters storage sense disk"),
        new("Transfers and power", "settings/network", "network bandwidth upload download limits speed concurrency metered battery"),
        new("Appearance", "settings/appearance", "theme light dark system"),
        new("Startup", "settings/general", "start Windows sign in tray launch"),
        new("Notifications", "settings/notifications", "alerts banners sound problems"),
        new("Updates and about", "settings/about", "version update install release diagnostics logs"),
        new("Settings", "settings", "preferences options backup capabilities compatibility connection modes")
    ];

    private static IEnumerable<NavigationDestination> FindDestinations(string query)
    {
        var words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return NavigationDestinations.Where(item => words.All(word =>
            (item.Title + " " + item.Keywords).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void QuickNavigate_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string route }) RequestNavigationRoute(route);
    }

    private object? NavigationItemForRoute(string route) => route switch
    {
        "settings/account" or "settings/onedrive" => ServicesNavigationItem,
        "settings" => Navigation.SettingsItem,
        _ when route.StartsWith("settings/", StringComparison.Ordinal) => Navigation.SettingsItem,
        _ => Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == route)
    };

    private void SynchronizeSettingsNavigation()
    {
        if (!_navigationTemplateReady || _applyingNavigationRoute || _currentPage != "settings") return;
        _applyingNavigationRoute = true;
        try { SelectNavigationSection(NavigationItemForRoute(_settingsRoute == "home" ? "settings" : "settings/" + _settingsRoute)); }
        finally { _applyingNavigationRoute = false; }
    }

    private void SelectNavigationSection(object? selected)
    {
        if (selected is not NavigationViewItem selectedItem) return;
        // A subpage may retain the same SelectedItem while native containers
        // have been retemplated. Keep every visible container in agreement.
        foreach (var item in Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>())
            if (!ReferenceEquals(item, selectedItem)) item.IsSelected = false;
        if (Navigation.SettingsItem is NavigationViewItem settings && !ReferenceEquals(settings, selectedItem))
            settings.IsSelected = false;
        Navigation.SelectedItem = selectedItem;
        selectedItem.IsSelected = true;
    }

    private string PageDescriptionForRoute() => _currentPage switch
    {
        "overview" => "Your files, folders, and cloud activity at a glance.",
        "activity" => "Follow your transfers and see what changed.",
        "backup" => "Choose what to protect. Your folders stay close in File Explorer.",
        "files" => "Open your backed-up files, manage disk space, and recover earlier versions.",
        "services" => "Choose the cloud storage that works for you.",
        "settings" => _settingsRoute switch
        {
            "account" => "Connect your Backblaze B2 bucket and manage its connection.",
            "onedrive" => "Manage the accounts you use for cloud transfers.",
            "sync" => "Choose how files are stored and what stays out of your backup.",
            "network" => "Balance transfer speed, bandwidth, and battery life.",
            "appearance" => "Choose a look that feels at home on your desktop.",
            "general" => "Keep CloudInlet ready when you sign in to Windows.",
            "notifications" => "Stay informed about the things that matter to you.",
            "about" => "Keep CloudInlet up to date and find app information.",
            _ => "Make CloudInlet work the way you do."
        },
        _ => ""
    };

    private void UpdateFluentLayout()
    {
        if (HomeQuickActions is null || _viewModel is null) return;
        var scale = Math.Max(1, _uiSettings.TextScaleFactor);
        ArrangeThemeChoices(ThemeChoiceGrid.ActualWidth);
        var homeWidth = OverviewContent.Width - OverviewContent.Padding.Left - OverviewContent.Padding.Right;
        if (double.IsFinite(homeWidth) && homeWidth > 0)
            ArrangeTiles(HomeQuickActions, homeWidth >= 576 * scale ? 2 : 1);
        HomeCloudTransferCard.IsEnabled = !_busy && _viewModel.Preview is null;
        if (_cloudServicesView is not null) _cloudServicesView.IsEnabled = !_busy && _viewModel.Preview is null;
        if (_homeServicesView is not null) _homeServicesView.IsEnabled = !_busy && _viewModel.Preview is null;

        NavigationStatusCard.Visibility = Navigation.IsPaneOpen && Navigation.DisplayMode == NavigationViewDisplayMode.Expanded && _viewModel.IsConfigured
            ? Visibility.Visible : Visibility.Collapsed;
        if (RootGrid.ActualWidth > 0)
        {
            // Reserve space for navigation, the app title, caption buttons and
            // a draggable gap without reducing the search to a clipped label.
            NavigationSearch.Width = Math.Clamp(RootGrid.ActualWidth - 420, 240, 520);
            NavigationSearch.Visibility = RootGrid.ActualWidth >= 600 ? Visibility.Visible : Visibility.Collapsed;
        }

        ReflowActivityToolbar();
        if (ActivityPage.ActualHeight > 0)
            CloudTransferJobsPanel.MaxHeight = Math.Max(72, Math.Min(320, ActivityPage.ActualHeight - 240));
    }

    private void ActivityToolbar_SizeChanged(object sender, SizeChangedEventArgs args) => ReflowActivityToolbar();

    private void ReflowActivityToolbar()
    {
        if (ActivityToolbar.ActualWidth <= 0) return;
        ActivityToolbarTitle.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        CloudTransferAction.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var stackTransferAction = ActivityToolbar.ActualWidth < ActivityToolbarTitle.DesiredSize.Width +
            ActivityToolbar.ColumnSpacing + CloudTransferAction.DesiredSize.Width;
        Grid.SetColumn(CloudTransferAction, stackTransferAction ? 0 : 1);
        Grid.SetRow(CloudTransferAction, stackTransferAction ? 1 : 0);
        CloudTransferAction.HorizontalAlignment = stackTransferAction ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private sealed record PageCrumb(string Label, string Route, double Opacity = 1)
    {
        public override string ToString() => Label;
    }

    private string _breadcrumbRoute = "";
    private bool CanNavigateToParent => _currentPage == "settings" && _settingsRoute != "home";

    private void UpdateBreadcrumbNavigation()
    {
        if (PageBreadcrumb is null) return;
        AppTitleBar.IsBackButtonEnabled = CanNavigateToParent;
        PageBreadcrumb.Visibility = CanNavigateToParent ? Visibility.Visible : Visibility.Collapsed;
        if (_breadcrumbRoute == CurrentRoute) return;
        _breadcrumbRoute = CurrentRoute;
        PageBreadcrumb.ItemsSource = !CanNavigateToParent ? Array.Empty<PageCrumb>() : _settingsRoute is "account" or "onedrive"
            ? new[] { new PageCrumb("Cloud services", "services", 0.65), new PageCrumb(_settingsRoute == "account" ? "Backblaze B2" : "OneDrive", CurrentRoute) }
            : new[] { new PageCrumb("Settings", "settings", 0.65), new PageCrumb(PageTitle.Text, CurrentRoute) };
    }

    private void PageBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is PageCrumb crumb && crumb.Route != CurrentRoute)
        {
            if (crumb.Route == "settings") ReturnToSettingsHome();
            else RequestNavigationRoute(crumb.Route);
        }
    }

    private void NavigateToParent()
    {
        if (!CanNavigateToParent) return;
        if (_settingsRoute is "account" or "onedrive") RequestNavigationRoute("services");
        else ReturnToSettingsHome();
    }

    private void ShellBackRequested(object sender, object args) => NavigateToParent();

    private void ShellPaneToggleRequested(object sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
}

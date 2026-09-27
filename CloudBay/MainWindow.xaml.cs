using System;
using CloudBay.Application;
using CloudBay.ViewModels;
using CloudBay.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace CloudBay;

public sealed partial class MainWindow : Window
{
    private readonly DashboardPage _dashboard;
    private readonly SystemFoldersPage _folders;
    private readonly CustomLinksPage _links;
    private readonly DeveloperFiltersPage _filters;
    private readonly SettingsPage _settings;
    private readonly DispatcherTimer _healthTimer;

    public ShellViewModel ViewModel { get; }

    public MainWindow(ICloudBayFacade facade)
    {
        InitializeComponent();
        ViewModel = new ShellViewModel(facade);
        RootGrid.DataContext = ViewModel;

        _dashboard = new DashboardPage { DataContext = ViewModel };
        _folders = new SystemFoldersPage { DataContext = ViewModel };
        _links = new CustomLinksPage { DataContext = ViewModel };
        _filters = new DeveloperFiltersPage { DataContext = ViewModel };
        _settings = new SettingsPage { DataContext = ViewModel };
        RegisterScrollReset(_dashboard);
        RegisterScrollReset(_folders);
        RegisterScrollReset(_links);
        RegisterScrollReset(_filters);
        RegisterScrollReset(_settings);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = "CloudBay";
        AppWindow.Resize(new SizeInt32(1180, 780));

        Navigation.SelectedItem = Navigation.MenuItems[0];
        PageHost.Content = _dashboard;
        ApplyTheme(ViewModel.SelectedTheme);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.SelectedTheme))
            {
                ApplyTheme(ViewModel.SelectedTheme);
            }
        };

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _healthTimer.Tick += async (_, _) => await ViewModel.RefreshSilentlyAsync();
        _healthTimer.Start();
        Closed += (_, _) => { _healthTimer.Stop(); ViewModel.Dispose(); };
        RootGrid.Loaded += async (_, _) =>
        {
            if (Navigation.MenuItems[0] is NavigationViewItem dashboardItem)
            {
                dashboardItem.Focus(FocusState.Programmatic);
            }
            ResetCurrentPageScroll();
            await ViewModel.RefreshAsync();
            ResetCurrentPageScroll();
        };
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PageHost is null || _dashboard is null)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            PageHost.Content = _settings;
            ResetCurrentPageScroll();
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        PageHost.Content = tag switch
        {
            "folders" => _folders,
            "links" => _links,
            "filters" => _filters,
            _ => _dashboard
        };
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            selectedItem.Focus(FocusState.Programmatic);
        }
        ResetCurrentPageScroll();
    }

    private void ResetCurrentPageScroll()
    {
        if (PageHost.Content is not Page { Content: ScrollViewer viewer }) return;
        QueueScrollReset(viewer);
    }

    private void RegisterScrollReset(Page page)
    {
        if (page.Content is ScrollViewer viewer)
        {
            viewer.Loaded += (_, _) => QueueScrollReset(viewer);
        }
    }

    private void QueueScrollReset(ScrollViewer viewer)
    {
        viewer.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (PageHost.Content is not Page { Content: ScrollViewer current } ||
                !ReferenceEquals(current, viewer)) return;
            viewer.UpdateLayout();
            viewer.ChangeView(null, 0, null, true);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => viewer.ChangeView(null, 0, null, true));
        });
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}

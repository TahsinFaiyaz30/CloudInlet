using System;
using System.Linq;
using System.IO;
using CloudBay.Application;
using CloudBay.ViewModels;
using CloudBay.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace CloudBay;

public sealed partial class MainWindow : Window
{
    private readonly DashboardPage _dashboard;
    private readonly SystemFoldersPage _folders;
    private readonly CustomLinksPage _links;
    private readonly DeveloperFiltersPage _filters;
    private readonly SettingsPage _settings;
    private readonly DispatcherTimer _healthTimer;
    private readonly UISettings _uiSettings = new();
    private bool _paneMeasureQueued;
    private bool _isClosed;

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
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        AppWindow.Resize(new SizeInt32(1180, 780));

        Navigation.SelectedItem = Navigation.MenuItems[0];
        PageHost.Content = _dashboard;
        ApplyTheme(ViewModel.SelectedTheme);
        UpdatePaneWidth();
        Navigation.Loaded += (_, _) => SchedulePaneMeasure();
        Navigation.SizeChanged += (_, _) => SchedulePaneMeasure();
        PaneSectionLabel.SizeChanged += (_, _) => SchedulePaneMeasure();
        PaneRootLabel.SizeChanged += (_, _) => SchedulePaneMeasure();
        PaneRootStatus.SizeChanged += (_, _) => SchedulePaneMeasure();
        _uiSettings.TextScaleFactorChanged += UISettings_TextScaleFactorChanged;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.SelectedTheme))
            {
                ApplyTheme(ViewModel.SelectedTheme);
            }
            if (e.PropertyName is nameof(ShellViewModel.RootHealth) or nameof(ShellViewModel.SelectedTheme))
            {
                SchedulePaneMeasure();
            }
        };

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _healthTimer.Tick += async (_, _) => await ViewModel.RefreshSilentlyAsync();
        _healthTimer.Start();
        Closed += (_, _) =>
        {
            _isClosed = true;
            _healthTimer.Stop();
            _uiSettings.TextScaleFactorChanged -= UISettings_TextScaleFactorChanged;
            ViewModel.Dispose();
        };
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
        SchedulePaneMeasure();
    }

    private void UISettings_TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(SchedulePaneMeasure);

    private void SchedulePaneMeasure()
    {
        if (_isClosed || _paneMeasureQueued) return;
        _paneMeasureQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _paneMeasureQueued = false;
            if (!_isClosed) UpdatePaneWidth();
        });
    }

    private void UpdatePaneWidth()
    {
        var required = Navigation.CompactPaneLength;
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            required = Math.Max(required, MeasureNavigationItem(item));
        }
        foreach (var item in Navigation.FooterMenuItems.OfType<NavigationViewItem>())
        {
            required = Math.Max(required, MeasureNavigationItem(item));
        }
        if (Navigation.SettingsItem is NavigationViewItem settingsItem)
        {
            required = Math.Max(required, MeasureNavigationItem(settingsItem));
        }

        required = Math.Max(required, MeasurePaneText(PaneSectionLabel));
        required = Math.Max(required, MeasurePaneText(PaneRootLabel));
        required = Math.Max(required, MeasurePaneText(PaneRootStatus));
        required = Math.Ceiling(required);
        if (Math.Abs(Navigation.OpenPaneLength - required) >= 1)
        {
            Navigation.OpenPaneLength = required;
        }
    }

    private double MeasureNavigationItem(NavigationViewItem item)
    {
        if (item.Content is null) return Navigation.CompactPaneLength;
        var label = item.Content.ToString() ?? string.Empty;
        var rendered = FindTextBlock(item, label);
        var textWidth = MeasureText(label, rendered, item);
        var left = rendered is not null
            ? GetLeftOrFallback(rendered, Navigation.CompactPaneLength + item.Margin.Left)
            : Navigation.CompactPaneLength + item.Margin.Left;
        // NavigationView reserves additional space after the content presenter for
        // its selection and focus visuals. Without it, the widest label is clipped
        // even when the text itself measures smaller than the pane.
        var right = Math.Max(40, item.Margin.Right + item.Padding.Right + 36);
        return left + textWidth + right;
    }

    private double MeasurePaneText(TextBlock textBlock)
    {
        if (string.IsNullOrEmpty(textBlock.Text)) return Navigation.CompactPaneLength;
        var left = GetLeftOrFallback(textBlock, textBlock.Margin.Left + 16);
        return left + MeasureText(textBlock.Text, textBlock, null) + textBlock.Margin.Right + 12;
    }

    private double GetLeftOrFallback(FrameworkElement element, double fallback)
    {
        if (!element.IsLoaded || !Navigation.IsLoaded) return fallback;
        try
        {
            return element.TransformToVisual(Navigation).TransformPoint(new Point()).X;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static double MeasureText(string text, TextBlock? rendered, Control? item)
    {
        var probe = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None
        };
        if (rendered is not null)
        {
            probe.FontFamily = rendered.FontFamily;
            probe.FontSize = rendered.FontSize;
            probe.FontWeight = rendered.FontWeight;
            probe.FontStyle = rendered.FontStyle;
            probe.CharacterSpacing = rendered.CharacterSpacing;
            probe.IsTextScaleFactorEnabled = rendered.IsTextScaleFactorEnabled;
        }
        else if (item is not null)
        {
            probe.FontFamily = item.FontFamily;
            probe.FontSize = item.FontSize;
            probe.FontWeight = item.FontWeight;
            probe.FontStyle = item.FontStyle;
            probe.CharacterSpacing = item.CharacterSpacing;
            probe.IsTextScaleFactorEnabled = item.IsTextScaleFactorEnabled;
        }
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static TextBlock? FindTextBlock(DependencyObject parent, string text)
    {
        if (parent is TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = FindTextBlock(VisualTreeHelper.GetChild(parent, i), text);
            if (child is not null) return child;
        }
        return null;
    }
}

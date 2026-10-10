using CloudInlet.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace CloudInlet.Views;

/// <summary>Capability-aware service discovery. Planned providers never create connections.</summary>
public sealed partial class CloudServicesView : UserControl
{
    private readonly UISettings _uiSettings = new();
    private readonly List<Grid> _plannedGrids = [];
    private readonly bool _showComingSoon;
    private bool _observingTextScale;

    public event EventHandler? ConnectBackblazeRequested;
    public event EventHandler? OpenOneDriveRequested;

    public CloudServicesView() : this(showComingSoon: true) { }

    /// <param name="showComingSoon">False embeds just the available services, without page gutters or an inner vertical scroll region.</param>
    public CloudServicesView(bool showComingSoon)
    {
        _showComingSoon = showComingSoon;
        InitializeComponent();
        if (showComingSoon) RenderPlannedServices("");
        else
        {
            PlannedServicesSection.Visibility = Visibility.Collapsed;
            AvailableServiceHeading.Visibility = Visibility.Collapsed;
            BackblazeCapabilities.Visibility = OneDriveCapabilities.Visibility = Visibility.Collapsed;
            CompactEmbeddedCard(BackblazeCardLayout, ConnectBackblazeButton);
            CompactEmbeddedCard(OneDriveCardLayout, OpenOneDriveButton);
            ServicesContent.Padding = new Thickness(0);
            ServicesScroll.VerticalScrollMode = ScrollMode.Disabled;
            ServicesScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        Loaded += (_, _) =>
        {
            if (!_observingTextScale)
            {
                _uiSettings.TextScaleFactorChanged += TextScaleFactor_Changed;
                _observingTextScale = true;
            }
            UpdateServiceLayout();
        };
        Unloaded += (_, _) =>
        {
            if (!_observingTextScale) return;
            _uiSettings.TextScaleFactorChanged -= TextScaleFactor_Changed;
            _observingTextScale = false;
        };
    }

    public void RefreshProviders(bool b2Connected, int oneDriveAccounts)
    {
        BackblazeStatus.Text = b2Connected ? "Connected" : "Available";
        ConnectBackblazeButton.Content = b2Connected ? "Manage connection" : "Connect Backblaze B2";
        AutomationProperties.SetName(ConnectBackblazeButton, b2Connected ? "Manage Backblaze B2 connection" : "Connect Backblaze B2");
        OneDriveStatus.Text = oneDriveAccounts switch
        {
            > 1 => $"{oneDriveAccounts} accounts connected",
            1 => "1 account connected",
            _ => "Available"
        };
        OpenOneDriveButton.Content = oneDriveAccounts > 0 ? "Manage OneDrive accounts" : "Connect OneDrive";
        AutomationProperties.SetName(OpenOneDriveButton,
            oneDriveAccounts > 0 ? "Manage OneDrive accounts" : "Connect OneDrive for cloud transfers");
    }

    private void ConnectBackblaze_Click(object sender, RoutedEventArgs args) => ConnectBackblazeRequested?.Invoke(this, EventArgs.Empty);
    private void OpenOneDrive_Click(object sender, RoutedEventArgs args) => OpenOneDriveRequested?.Invoke(this, EventArgs.Empty);

    private static void CompactEmbeddedCard(Grid layout, Button action)
    {
        Grid.SetRow(action, 2);
        layout.RowDefinitions.RemoveAt(3);
        layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        layout.RowDefinitions[2].Height = GridLength.Auto;
    }

    private void PlannedServicesSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) RenderPlannedServices(sender.Text);
    }

    private void RenderPlannedServices(string query)
    {
        if (!_showComingSoon || PlannedServiceGroups is null) return;
        PlannedServiceGroups.Children.Clear();
        _plannedGrids.Clear();
        var terms = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var providers = ProductCatalog.Providers
            .Where(provider => provider.Id is not ("b2" or "onedrive"))
            .Where(provider => terms.All(term =>
                $"{provider.Name} {provider.Description} {provider.Group}".Contains(term, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(provider => provider.Group);
        foreach (var group in providers)
        {
            var section = new StackPanel { Spacing = 12 };
            var heading = new TextBlock
            {
                Text = group.Key, TextWrapping = TextWrapping.Wrap,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"]
            };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
            section.Children.Add(heading);
            var cards = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
            foreach (var provider in group) cards.Children.Add(CreatePlannedCard(provider));
            _plannedGrids.Add(cards);
            section.Children.Add(cards);
            PlannedServiceGroups.Children.Add(section);
        }
        NoPlannedServices.Visibility = _plannedGrids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateServiceLayout();
    }

    private Border CreatePlannedCard(ProductOption provider)
    {
        var copy = new Grid { RowSpacing = 6 };
        copy.RowDefinitions.Add(new() { Height = GridLength.Auto });
        copy.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        copy.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = provider.Name, TextWrapping = TextWrapping.Wrap,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"]
        };
        copy.Children.Add(title);
        var description = new TextBlock
        {
            Text = provider.Description,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudInletBodySecondaryTextStyle"]
        };
        Grid.SetRow(description, 1);
        copy.Children.Add(description);
        var status = new TextBlock
        {
            Text = "Coming soon", Margin = new Thickness(0, 4, 0, 0),
            Style = (Style)Resources["ServiceBadgeTextStyle"]
        };
        Grid.SetRow(status, 2);
        copy.Children.Add(status);
        var card = new Border
        {
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudInletSurfaceStyle"],
            Padding = new Thickness(16), MinHeight = 120, Child = copy
        };
        AutomationProperties.SetName(card, $"{provider.Name}. Coming soon. {provider.Description}");
        return card;
    }

    private void ServicesScroll_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateServiceLayout();

    private void TextScaleFactor_Changed(UISettings sender, object args) => DispatcherQueue.TryEnqueue(UpdateServiceLayout);

    private void UpdateServiceLayout()
    {
        if (ServicesScroll is null || ServicesContent is null || ServicesScroll.ActualWidth <= 0) return;
        ServicesContent.Width = Math.Min(1240, ServicesScroll.ActualWidth);
        var innerWidth = ServicesContent.Width - ServicesContent.Padding.Left - ServicesContent.Padding.Right;
        var scale = Math.Max(1, _uiSettings.TextScaleFactor);
        var inlineSearch = innerWidth >= 740 * scale;
        PlannedServicesSearch.Width = Math.Min(300, Math.Max(0, innerWidth));
        Grid.SetRow(PlannedServicesSearch, inlineSearch ? 0 : 1);
        Grid.SetColumn(PlannedServicesSearch, inlineSearch ? 1 : 0);
        Grid.SetColumnSpan(PlannedServicesSearch, inlineSearch ? 1 : 2);
        PlannedServicesSearch.HorizontalAlignment = inlineSearch ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        ArrangeCards(AvailableServices, innerWidth >= 680 * scale ? 2 : 1);
        var plannedColumns = innerWidth >= 1020 * scale ? 3 : innerWidth >= 660 * scale ? 2 : 1;
        foreach (var grid in _plannedGrids) ArrangeCards(grid, plannedColumns);
    }

    private static void ArrangeCards(Grid grid, int columns)
    {
        var rows = (grid.Children.Count + columns - 1) / columns;
        if (grid.ColumnDefinitions.Count != columns)
        {
            grid.ColumnDefinitions.Clear();
            for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        }
        while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        for (var index = 0; index < grid.Children.Count; index++)
        {
            if (grid.Children[index] is FrameworkElement element)
            {
                Grid.SetColumn(element, index % columns);
                Grid.SetRow(element, index / columns);
            }
        }
    }
}

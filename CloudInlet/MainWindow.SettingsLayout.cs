using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private readonly HashSet<Grid> _settingsDetailGrids = [];

    private void SettingsDetailGrid_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid grid) return;
        _settingsDetailGrids.Add(grid);
        ArrangeSettingsDetailGrid(grid);
    }

    private void UpdateSettingsDetailGrids()
    {
        foreach (var grid in _settingsDetailGrids) ArrangeSettingsDetailGrid(grid);
    }

    private void ArrangeSettingsDetailGrid(Grid grid)
    {
        if (grid.ActualWidth <= 0) return;
        var scale = Math.Max(1, _uiSettings.TextScaleFactor);
        var maximum = int.TryParse(grid.Tag?.ToString(), out var parsed) ? Math.Clamp(parsed, 1, 3) : 2;
        // Numeric pairs need less width than account sections and described
        // switches. Compact cards put their input below the label as needed.
        var minimumWidth = grid == NetworkBandwidthGrid || grid == NetworkCapacityGrid ? 280 : 360;
        var columns = Math.Clamp((int)((grid.ActualWidth + grid.ColumnSpacing) / (minimumWidth * scale + grid.ColumnSpacing)), 1, maximum);
        var children = grid.Children.OfType<FrameworkElement>().Where(child => child.Visibility == Visibility.Visible).ToArray();
        if (grid.ColumnDefinitions.Count != columns)
        {
            grid.ColumnDefinitions.Clear();
            for (var column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rows = (children.Length + columns - 1) / columns;
        while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index];
            if (child is CommunityToolkit.WinUI.Controls.SettingsCard card && card.Content is NumberBox)
                card.ContentAlignment = (grid.ActualWidth - grid.ColumnSpacing * (columns - 1)) / columns < 400 * scale
                    ? CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical : CommunityToolkit.WinUI.Controls.ContentAlignment.Right;
            child.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(child, index % columns);
            Grid.SetRow(child, index / columns);
        }
    }
}

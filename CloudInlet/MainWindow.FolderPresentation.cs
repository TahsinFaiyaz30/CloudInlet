using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;
using CardContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> _backupLocations = new(StringComparer.OrdinalIgnoreCase);

    private void MoreWindowsFolders_Toggled(object sender, RoutedEventArgs args)
    {
        if (MoreBackupRows is null) return;
        var expanded = MoreWindowsFolders.IsChecked == true;
        MoreBackupRows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        MoreWindowsFolders.Content = expanded ? "Show fewer folders" : "Show all folders";
        UpdateResponsiveLayout();
    }

    private static TextBlock CreateFolderTitle(string name) => new()
    {
        Text = name,
        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"],
        FontSize = 16,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock CreateFolderLocation(string path)
    {
        var text = new TextBlock
        {
            Text = path,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudInletSecondaryTextStyle"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(text, path);
        return text;
    }

    private static StackPanel CreateFolderDescription(string state, string path)
    {
        var details = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        details.Children.Add(new TextBlock
        {
            Text = state,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudInletBodySecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(CreateFolderLocation(path));
        return details;
    }

    private void UpdateFolderCardLayouts()
    {
        var width = BackupContent.Width - BackupContent.Padding.Left - BackupContent.Padding.Right;
        if (!double.IsFinite(width) || width <= 0) return;
        var scale = Math.Max(1, _uiSettings.TextScaleFactor);

        // A folder's 44 px switch fits beside its text even in the narrower
        // tiles in the folder grid. Reserve a second action row only when
        // the card is genuinely too narrow, including at larger text scales.
        foreach (var card in _backupCards.Values)
            card.ContentAlignment = CardWidth(card, width) < 300 * scale
                ? CardContentAlignment.Vertical : CardContentAlignment.Right;
        foreach (var card in CustomBackupRows.Children.OfType<SettingsCard>())
        {
            var cardWidth = CardWidth(card, width);
            card.ContentAlignment = cardWidth < 680 * scale
                ? CardContentAlignment.Vertical : CardContentAlignment.Right;
            if (card.Content is StackPanel actions)
                actions.Orientation = cardWidth < 420 * scale
                    ? Orientation.Vertical : Orientation.Horizontal;
        }
    }

    private static double CardWidth(SettingsCard card, double fallback) => card.Parent is Grid grid && grid.ColumnDefinitions.Count > 0
        ? ((grid.ActualWidth > 0 ? grid.ActualWidth : fallback) - grid.ColumnSpacing * (grid.ColumnDefinitions.Count - 1)) / grid.ColumnDefinitions.Count
        : card.ActualWidth > 0 ? card.ActualWidth : fallback;
}

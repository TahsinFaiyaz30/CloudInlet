using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.UI.ViewManagement;

namespace CloudInlet;

public sealed partial class MainWindow
{
    private bool _appearanceInitialized;

    private void AppearanceSettings_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_appearanceInitialized)
        {
            _appearanceInitialized = true;
            // All theme writes still use Theme_SelectionChanged. This listener also
            // reflects settings reloads and a failed save's persisted-value rollback.
            ThemeBox.SelectionChanged += ThemePreview_SelectionChanged;
            _uiSettings.ColorValuesChanged += AppearanceColors_Changed;
            Closed += (_, _) => _uiSettings.ColorValuesChanged -= AppearanceColors_Changed;
        }

        SynchronizeThemeChoices();
        RefreshSystemThemePreview();
        ArrangeThemeChoices(ThemeChoiceGrid.ActualWidth);
    }

    private void ThemePreview_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        SynchronizeThemeChoices();

    private void ThemeChoice_Click(object sender, RoutedEventArgs args)
    {
        if (!_loadingPreferences && !_busy && _viewModel is not null && _viewModel.Preview is null &&
            sender is ToggleButton { Tag: string theme })
        {
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == theme);
        }

        // A selected choice stays selected when clicked again. Preview and busy
        // states must never imply a preference that was not actually accepted.
        SynchronizeThemeChoices();
    }

    private void SynchronizeThemeChoices()
    {
        var theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        SystemThemeChoice.IsChecked = theme == "System";
        LightThemeChoice.IsChecked = theme == "Light";
        DarkThemeChoice.IsChecked = theme == "Dark";
    }

    private void AppearanceColors_Changed(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) RefreshSystemThemePreview();
        });

    private void RefreshSystemThemePreview()
    {
        // The system preview follows Windows even when the app has an explicit
        // light or dark override. All preview colors remain native theme resources.
        var background = _uiSettings.GetColorValue(UIColorType.Background);
        SystemThemePreview.RequestedTheme =
            299 * background.R + 587 * background.G + 114 * background.B >= 128000
                ? ElementTheme.Light : ElementTheme.Dark;
    }

    private void ThemeChoices_SizeChanged(object sender, SizeChangedEventArgs args) =>
        ArrangeThemeChoices(args.NewSize.Width);

    private void ArrangeThemeChoices(double width)
    {
        if (width <= 0) return;
        var columns = Math.Clamp((int)((width + ThemeChoiceGrid.ColumnSpacing) /
            (176 * Math.Max(1, _uiSettings.TextScaleFactor) + ThemeChoiceGrid.ColumnSpacing)), 1, 3);
        var rows = (ThemeChoiceGrid.Children.Count + columns - 1) / columns;
        if (ThemeChoiceGrid.ColumnDefinitions.Count != columns || ThemeChoiceGrid.RowDefinitions.Count != rows)
        {
            ThemeChoiceGrid.ColumnDefinitions.Clear();
            ThemeChoiceGrid.RowDefinitions.Clear();
            for (var column = 0; column < columns; column++)
                ThemeChoiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < rows; row++)
                ThemeChoiceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var index = 0; index < ThemeChoiceGrid.Children.Count; index++)
        {
            if (ThemeChoiceGrid.Children[index] is not FrameworkElement choice) continue;
            Grid.SetColumn(choice, index % columns);
            Grid.SetRow(choice, index / columns);
        }
    }

    private async void WindowsAppearance_Click(object sender, RoutedEventArgs args) =>
        await RunAsync("Opening Windows appearance settings…", async () =>
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:colors")))
                throw new InvalidOperationException("Windows could not open Colors. Open Settings → Personalization → Colors instead.");
        });
}

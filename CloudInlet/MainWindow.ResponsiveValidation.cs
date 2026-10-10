using CloudInlet.Core;
using CloudInlet.Core.Transfers;
using CloudInlet.ViewModels;
using CloudInlet.Views;
using CloudInlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace CloudInlet;

public sealed partial class MainWindow
{
    public async Task RunResponsiveUiValidationAsync(string output)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke"))
            throw new InvalidOperationException("Responsive validation requires an isolated smoke process.");
        var theme = Environment.GetCommandLineArgs().Contains("--ui-smoke-theme=Light") ? ElementTheme.Light : ElementTheme.Dark;
        var suffix = theme == ElementTheme.Light ? "-light" : "";
        var log = Path.Combine(output, "responsive-layout.txt");
        await InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(30));
        // Retemplating can clear native selection; it is not a request to
        // navigate Home or discard a nested settings route.
        foreach (var route in new[] { "activity", "settings/about" })
        {
            RequestNavigationRoute(route);
            Navigation.SelectedItem = null;
            await Task.Delay(120);
            await File.AppendAllTextAsync(log, $"Native selection reset: requested={route}, actual={CurrentRoute}, selected={Navigation.SelectedItem is not null}.{Environment.NewLine}");
            AssertNavigationPresentation(route);
        }
        await ValidateActionWrappingAsync();
        foreach (var width in new[] { 1100, 760, 980, 800, 760 })
        {
            AppWindow.Resize(new SizeInt32(width, 600));
            Present(ClientPreview.Connected());
            await Page("overview", "home");
            AssertActionLayoutWhenFits(DashboardActions);
            await Page("backup", "backup");
            AssertGridLayout(BackupRows, "Windows folders");
            await Page("settings", "settings");
            AssertGridLayout(SettingsCategories, "Settings categories");
            await Page("settings/appearance", "appearance");
            if (_uiSettings.TextScaleFactor <= 1.01 && ThemeChoiceGrid.ActualWidth >= 600)
                Require(ThemeChoiceGrid.ColumnDefinitions.Count == 3, "Three theme previews must share a row when standard-size previews have enough logical width.");
            AssertGridLayout(ThemeChoiceGrid, "Theme choices");
            await Page("settings/network", "network");
            AssertGridLayout(NetworkBandwidthGrid, "Bandwidth fields");
            await Page("files", "files");
            FileToolsExpander.IsExpanded = true;
            await Task.Delay(180); RootGrid.UpdateLayout();
            AssertActionLayoutWhenFits(FileActions);
            await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"responsive-files-expanded-{width}{suffix}.png"));
            FileToolsExpander.IsExpanded = false;
            Present(ClientPreview.TransferQueue());
            await Page("activity", "transfers");
            var activityTitle = ActivityToolbar.Children.OfType<TextBlock>().First();
            if (NaturalTextWidth(activityTitle) + CloudTransferAction.DesiredSize.Width + ActivityToolbar.ColumnSpacing <= ActivityToolbar.ActualWidth)
                Require(Grid.GetRow(CloudTransferAction) == 0, "Activity title and transfer action must share a row when they fit.");
            AssertVisibleActivityRow(_viewModel.ActiveTransfers[0]);
            var activeTransfer = _viewModel.ActiveTransfers[0];
            var transferStatus = FindDescendant<TextBlock>((DependencyObject)ActivityList.ContainerFromItem(activeTransfer),
                item => item.Name == "ActivityFileStatus") ?? throw new InvalidOperationException("The live transfer must render a compact status line.");
            Require(transferStatus.Text == activeTransfer.StatusLine && transferStatus.Text.Contains(activeTransfer.SpeedText, StringComparison.Ordinal) &&
                transferStatus.Text.Contains(activeTransfer.ProgressLabel, StringComparison.Ordinal),
                "The live transfer status must include its measured rate, percentage and sizes.");
            Require(ActivityPending.Text == _viewModel.ActivityStatusSummary && ActivityPending.Text.Contains("/s", StringComparison.Ordinal),
                "The Activity summary must include measured aggregate rates.");
            AssertSingleLineWhenFits(transferStatus, "Transfer status, speed and completion");
            AssertSingleLineWhenFits(ActivityPending, "Activity summary and aggregate speed");
            Present(ClientPreview.ActivityActions());
            ActivityFilterBox.SelectedIndex = 3;
            await Page("activity", "row-actions");
            foreach (var index in new[] { 0, 2 })
            {
                var row = _viewModel.Activity[index];
                ActivityList.ScrollIntoView(row);
                await Task.Delay(100); RootGrid.UpdateLayout();
                AssertActivityActions(row, openFolder: true, cloud: index == 0);
                AssertActivityActionPlacement(row);
            }
            if (width == 1100) await ValidateActivityActionAvailabilityTransitionAsync(_viewModel.Activity[0]);
            ActivityFilterBox.SelectedIndex = 0;

            var dialog = new CloudTransferDialog(_controller, WinRT.Interop.WindowNative.GetWindowHandle(this),
                new("onedrive", "fixture", "drive", "source", "Documents", "OneDrive · Personal"),
                new("b2", "fixture", "bucket", "", "Cloud archive/Documents", "Backblaze B2 · Personal archive"))
            { XamlRoot = RootGrid.XamlRoot, RequestedTheme = theme };
            var showing = dialog.ShowAsync();
            try
            {
                await dialog.ShowReviewPresentationAsync();
                await Task.Delay(180); dialog.UpdateLayout();
                Require(dialog.ActualWidth <= RootGrid.ActualWidth + 1, "Transfer review fits the window.");
                await UiSmokeCapture.SaveAsync(dialog, Path.Combine(output, $"responsive-transfer-review-{width}{suffix}.png"));
            }
            finally { dialog.Hide(); await showing; }
            await File.AppendAllTextAsync(log, $"PASS {theme} {width}x600 physical; logical={RootGrid.ActualWidth:0.##}x{RootGrid.ActualHeight:0.##}, text-scale={_uiSettings.TextScaleFactor:0.##}: actions, tile geometry, theme choices, numeric pairs, activity actions and transfer review.{Environment.NewLine}");

            async Task Page(string route, string name)
            {
                RequestNavigationRoute(route);
                await Task.Delay(220);
                UpdateResponsiveLayout(); RootGrid.UpdateLayout();
                foreach (var scroll in new[] { OverviewPage, BackupPage, SettingsPage, FilesPage }) scroll.ChangeView(null, 0, null, true);
                await Task.Delay(100);
                await UiSmokeCapture.SaveAsync(RootGrid, Path.Combine(output, $"responsive-{name}-{width}{suffix}.png"));
            }
            void Require(bool condition, string message)
            {
                if (condition) return;
                File.AppendAllText(log, $"FAIL {theme} {width}: {message}{Environment.NewLine}");
                throw new InvalidOperationException(message);
            }
            void AssertGridLayout(Grid grid, string name)
            {
                var children = grid.Children.OfType<FrameworkElement>().Where(child => child.Visibility == Visibility.Visible).ToArray();
                var bounds = children.Select(child => child.TransformToVisual(grid).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight))).ToArray();
                for (var index = 0; index < bounds.Length; index++)
                {
                    var bound = bounds[index];
                    Require(bound.Width > 0 && bound.Height > 0 && bound.X >= -1 && bound.Right <= grid.ActualWidth + 1,
                        name + " must remain within its grid.");
                    for (var other = 0; other < index; other++)
                        Require(bound.Right <= bounds[other].X + 1 || bounds[other].Right <= bound.X + 1 ||
                            bound.Bottom <= bounds[other].Y + 1 || bounds[other].Bottom <= bound.Y + 1, name + " must not overlap.");
                    if (FindDescendant<Grid>(children[index], item => item.Name == "PART_RootGrid") is { } painted)
                    {
                        var paintedBounds = painted.TransformToVisual(children[index]).TransformBounds(new Rect(0, 0, painted.ActualWidth, painted.ActualHeight));
                        Require(paintedBounds.X >= -1 && paintedBounds.Right <= children[index].ActualWidth + 1 &&
                            paintedBounds.Y >= -1 && paintedBounds.Bottom <= children[index].ActualHeight + 1,
                            name + " painted surface must remain within its card.");
                    }
                }
            }
        }
        await RunUpdateUiValidationAsync(output, suffix);

        void Present(ClientPreview preview)
        {
            _viewModel.SetPreview(preview with { Settings = preview.Settings with { Theme = theme.ToString() } });
            LoadSettings(reloadAccount: true, reloadPreferences: true);
            ApplyTheme(theme.ToString());
            Refresh();
        }
    }

    private static void AssertActionLayoutWhenFits(ActionWrapPanel panel)
    {
        var visible = panel.Children.Where(child => child.Visibility == Visibility.Visible).ToArray();
        var needed = visible.Sum(child => child.DesiredSize.Width) + Math.Max(0, visible.Length - 1) * panel.Spacing;
        AssertActionLayout(panel, requireOneRow: needed <= panel.ActualWidth + 0.5);
    }

    private static double NaturalTextWidth(TextBlock text)
    {
        var measure = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
            FontWeight = text.FontWeight, FontStyle = text.FontStyle, CharacterSpacing = text.CharacterSpacing,
            IsTextScaleFactorEnabled = text.IsTextScaleFactorEnabled, TextWrapping = TextWrapping.NoWrap };
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return measure.DesiredSize.Width;
    }

    private static void AssertSingleLineWhenFits(TextBlock text, string name)
    {
        var natural = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
            FontStyle = text.FontStyle, FontStretch = text.FontStretch, CharacterSpacing = text.CharacterSpacing,
            IsTextScaleFactorEnabled = text.IsTextScaleFactorEnabled, LineHeight = text.LineHeight,
            LineStackingStrategy = text.LineStackingStrategy, TextWrapping = TextWrapping.NoWrap
        };
        natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (text.Visibility != Visibility.Visible || text.ActualHeight <= 0)
            throw new InvalidOperationException(name + " must remain visible.");
        if (natural.DesiredSize.Width <= text.ActualWidth + 0.5 &&
            (text.IsTextTrimmed || text.ActualHeight > natural.DesiredSize.Height + 1))
            throw new InvalidOperationException(name + " must share one line when its complete text fits.");
    }

    private void AssertActivityActionPlacement(object row)
    {
        var container = ActivityList.ContainerFromItem(row) as FrameworkElement
            ?? throw new InvalidOperationException("The activity fixture must have a realized row.");
        var actions = FindDescendant<ActionWrapPanel>(container, item => item.Name == "ActivityActions")
            ?? throw new InvalidOperationException("Activity actions must use the wrapping action layout.");
        var fileName = FindDescendant<TextBlock>(container, item => item.Name == "ActivityFileName")
            ?? throw new InvalidOperationException("The activity fixture must show its filename.");
        if (actions.Parent is not Grid grid || fileName.Parent is not FrameworkElement text)
            throw new InvalidOperationException("Activity text and actions must share the row layout.");
        AssertActionLayoutWhenFits(actions);
        var textBounds = text.TransformToVisual(grid).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
        var actionBounds = actions.TransformToVisual(grid).TransformBounds(new Rect(0, 0, actions.ActualWidth, actions.ActualHeight));
        if (textBounds.X < -1 || textBounds.Right > grid.ActualWidth + 1 || actionBounds.X < -1 || actionBounds.Right > grid.ActualWidth + 1 ||
            !(textBounds.Right <= actionBounds.X + 1 || actionBounds.Right <= textBounds.X + 1 ||
              textBounds.Bottom <= actionBounds.Y + 1 || actionBounds.Bottom <= textBounds.Y + 1))
            throw new InvalidOperationException("Activity text and actions must remain separate and inside their row.");
        var visible = actions.Children.Where(child => child.Visibility == Visibility.Visible).ToArray();
        var actionWidth = visible.Sum(child => child.DesiredSize.Width) + Math.Max(0, visible.Length - 1) * actions.Spacing;
        // Assess the actual leading text position, including the rendered icon
        // and gutters. Keep a useful text column beside an unwrapped action row.
        var remainingTextWidth = grid.ActualWidth - textBounds.X - grid.ColumnSpacing - actionWidth;
        if (remainingTextWidth >= 240 * Math.Max(1, _uiSettings.TextScaleFactor) + 1 && Grid.GetRow(actions) != 0)
            throw new InvalidOperationException("Activity actions must stay beside text when there is room for readable text and the complete action row.");
    }

    private async Task ValidateActivityActionAvailabilityTransitionAsync(ActivityItem row)
    {
        ActivityList.ScrollIntoView(row);
        await Task.Delay(100); RootGrid.UpdateLayout();
        var container = ActivityList.ContainerFromItem(row) as FrameworkElement
            ?? throw new InvalidOperationException("The availability fixture must have a realized row.");
        var actions = FindDescendant<ActionWrapPanel>(container, item => item.Name == "ActivityActions")
            ?? throw new InvalidOperationException("The availability fixture must have action controls.");
        if (actions.Parent is not Grid grid || row.Actions.Target is not { CanViewCloud: true } originalTarget)
            throw new InvalidOperationException("The availability fixture must begin with both contextual actions.");
        var buttons = actions.Children.OfType<Button>().ToArray();
        if (buttons.Length != 2 || buttons.Any(button => button.Visibility != Visibility.Visible))
            throw new InvalidOperationException("The availability fixture requires two visible buttons.");
        foreach (var button in buttons) button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var originalWidth = grid.ReadLocalValue(FrameworkElement.WidthProperty);
        try
        {
            // The full action row needs a second line, while the remaining
            // folder action fits beside the text after cloud availability changes.
            grid.Width = 20 + 2 * grid.ColumnSpacing + 240 * Math.Max(1, _uiSettings.TextScaleFactor)
                + buttons[0].DesiredSize.Width + (actions.Spacing + buttons[1].DesiredSize.Width) / 2;
            await Task.Delay(60); RootGrid.UpdateLayout();
            if (Grid.GetRow(actions) != 1 || Math.Abs(buttons[0].ActualHeight - buttons[1].ActualHeight) > 0.5)
                throw new InvalidOperationException("The availability fixture must start with a stacked, equal-height action row.");
            var panelWidth = actions.ActualWidth;
            var panelHeight = actions.ActualHeight;
            row.Actions.SetTarget(originalTarget with { CanViewCloud = false }, presentation: true);
            // Property bindings update before another layout pass. The old
            // SizeChanged-only implementation leaves the row stacked here.
            if (buttons[1].Visibility != Visibility.Collapsed || Math.Abs(actions.ActualWidth - panelWidth) > 0.5 ||
                Math.Abs(actions.ActualHeight - panelHeight) > 0.5 || Grid.GetRow(actions) != 0)
                throw new InvalidOperationException("Changing action availability must reflow the row even before the stretched panel changes size.");
            RootGrid.UpdateLayout();
            AssertActivityActionPlacement(row);
            row.Actions.SetTarget(originalTarget, presentation: true);
            if (Grid.GetRow(actions) != 1)
                throw new InvalidOperationException("A newly visible action must use its natural width and return below the text when needed.");
            RootGrid.UpdateLayout();
            AssertActivityActionPlacement(row);
        }
        finally
        {
            row.Actions.SetTarget(originalTarget, presentation: true);
            if (originalWidth == DependencyProperty.UnsetValue) grid.ClearValue(FrameworkElement.WidthProperty);
            else grid.SetValue(FrameworkElement.WidthProperty, originalWidth);
            RootGrid.UpdateLayout();
        }
    }

    private static void AssertActionLayout(ActionWrapPanel panel, bool requireOneRow)
    {
        var children = panel.Children.OfType<FrameworkElement>().Where(child => child.Visibility == Visibility.Visible).ToArray();
        var bounds = children.Select(child => child.TransformToVisual(panel).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight))).ToArray();
        foreach (var bound in bounds)
            if (bound.Width <= 0 || bound.Height <= 0 || bound.X < -1 || bound.Y < -1 ||
                bound.Right > panel.ActualWidth + 1 || bound.Bottom > panel.ActualHeight + 1)
                throw new InvalidOperationException("Action buttons must stay within their measured rows.");
        if (requireOneRow && bounds.Length > 1 && bounds.Max(bound => bound.Y + bound.Height / 2) - bounds.Min(bound => bound.Y + bound.Height / 2) > 3)
            throw new InvalidOperationException("Actions that fit must remain in one row.");
        for (var index = 0; index < bounds.Length; index++)
            for (var other = 0; other < index; other++)
                if (bounds[index].X < bounds[other].Right - 1 && bounds[other].X < bounds[index].Right - 1 &&
                    bounds[index].Y < bounds[other].Bottom - 1 && bounds[other].Y < bounds[index].Bottom - 1)
                    throw new InvalidOperationException("Wrapped action buttons must not overlap.");
    }

    private async Task ValidateActionWrappingAsync()
    {
        // Exercise true wrapping and larger labels without changing the user's
        // Windows text scale or interacting with any account state.
        foreach (var fontSize in new[] { 14d, 28d })
        {
            var panel = new ActionWrapPanel { Spacing = 8 };
            foreach (var label in new[] { "Open folder", "Sync now", "Resume syncing" })
                panel.Children.Add(new Button { Content = label, FontSize = fontSize });
            panel.Children.Add(new Button { Content = "Hidden action", Visibility = Visibility.Collapsed });
            var host = new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRowSpan(host, 2);
            RootGrid.Children.Add(host);
            try
            {
                foreach (var width in new[] { 200d, 400d, 700d })
                {
                    host.Width = width;
                    await Task.Delay(60);
                    RootGrid.UpdateLayout();
                    AssertActionLayoutWhenFits(panel);
                }
            }
            finally { RootGrid.Children.Remove(host); }
        }
    }
}

using System.Runtime.InteropServices;
using CloudBay.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace CloudBay.Views;

/// <summary>A standalone Fluent tray menu, using WinUI's standard menu controls.</summary>
public sealed partial class TrayContextMenuWindow : Window
{
    private readonly Func<TrayMenuAvailability> _availability;
    private readonly TrayIconActions _actions;
    private readonly Func<string> _theme;
    private readonly MenuFlyout _items = new();
    private readonly MenuFlyoutItem _openApp;
    private readonly MenuFlyoutItem _openFolder;
    private readonly MenuFlyoutItem _pause;
    private readonly MenuFlyoutItem _settings;
    private readonly MenuFlyoutItem _quit;
    private readonly NativeFrameProc _frameProc;
    private readonly nint _window;
    private readonly bool _frameHookInstalled;
    private bool _opening;
    private bool _closed;
    private bool _inspection;
    private TrayMenuAvailability? _validationAvailability;
    private MenuCommand? _validationCommand;
    private string? _validationTheme;
    private int _showRevision;

    public TrayContextMenuWindow(Func<TrayMenuAvailability> availability, TrayIconActions actions, Func<string> theme)
    {
        _availability = availability;
        _actions = actions;
        _theme = theme;
        InitializeComponent();
        _openApp = AddItem("Open CloudBay", "\uE80F", MenuCommand.OpenApp);
        _openFolder = AddItem("Open folder", "\uE8B7", MenuCommand.OpenFolder);
        _pause = AddItem("Pause syncing", "\uE769", MenuCommand.TogglePause);
        _settings = AddItem("Settings", "\uE713", MenuCommand.Settings);
        _items.Items.Add(new MenuFlyoutSeparator());
        _quit = AddItem("Quit CloudBay", "\uE7E8", MenuCommand.Quit);
        // WinUI's presenter expects the canonical IVector<MenuFlyoutItemBase>.
        // It calculates icon alignment and supplies built-in arrow-key cycling.
        // https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/MenuFlyoutPresenter_Partial.cpp
        MenuPresenter.ItemsSource = _items.Items;
        // Desktop Acrylic belongs to this window. The stock presenter fill and
        // stroke remain theme resources, including the high-contrast fallback.
        MenuPresenter.SystemBackdrop = null;
        ExtendsContentIntoTitleBar = true;
        AppWindow.Title = CloudBay.Core.BuildInfo.ProductName + " tray menu";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        _window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _frameProc = FrameWindowProc;
        _frameHookInstalled = SetWindowSubclass(_window, _frameProc, 0xCB05, 0);
        if (_frameHookInstalled) SetWindowPos(_window, 0, 0, 0, 0, 0, 0x0037);
        MenuRoot.ActualThemeChanged += (_, _) => ApplyFrame();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && GetForegroundWindow() != _window && !_opening && !_inspection)
                Hide();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            if (_frameHookInstalled) RemoveWindowSubclass(_window, _frameProc, 0xCB05);
        };
    }

    public bool IsVisible => !_closed && AppWindow.IsVisible;
    internal FocusState FocusStateForValidation => _openApp.FocusState;

    /// <summary>Returns keyboard focus to the Shell icon after Escape cancellation.</summary>
    public Action? ReturnKeyboardFocus { get; set; }

    /// <summary>Screen coordinates are physical pixels, as supplied by the Shell.</summary>
    public void ShowAt(PointInt32 screenPoint, bool forceActivationForValidation = false)
    {
        if (_closed) return;
        _opening = true;
        try
        {
            Refresh();
            UpdateGeometry(screenPoint, useRenderedColumns: false);
            ApplyFrame();
            var requestFocus = !_inspection && (forceActivationForValidation || !Environment.GetCommandLineArgs().Contains("--ui-smoke"));
            AppWindow.Show(false);
            var revision = ++_showRevision;
            // Icon/check placeholder states can settle only after the first
            // Loaded pass. One geometry-only pass measures their actual columns;
            // it cannot activate, reopen, or resize a superseded/hidden menu.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_closed && IsVisible && revision == _showRevision)
                    UpdateGeometry(screenPoint, useRenderedColumns: true);
            });
            if (requestFocus)
            {
                // A direct user tray gesture permits foreground activation.
                // Request it once; never attach input queues or retry later.
                Activate();
                SetForegroundWindow(_window);
                FocusInitialItem();
                // The first Show can precede XAML's Loaded pass. One queued
                // focus assignment is safe only while this window already owns
                // the foreground; it never requests activation or reopens it.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusInitialItem);
            }
        }
        finally { _opening = false; }
    }

    public void Hide()
    {
        _showRevision++;
        if (!_closed && AppWindow.IsVisible) AppWindow.Hide();
    }

    private void UpdateGeometry(PointInt32 screenPoint, bool useRenderedColumns)
    {
        if (useRenderedColumns) MenuRoot.UpdateLayout();
        var area = DisplayArea.GetFromPoint(screenPoint, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var monitor = MonitorFromPoint(new NativePoint(screenPoint.X, screenPoint.Y), 2);
        var dpi = GetDpiForWindow(_window);
        if (GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0) dpi = monitorDpi;
        var scale = dpi > 0 ? dpi / 96d : 1;
        var margin = (int)Math.Ceiling(8 * scale);
        var maximumWidth = Math.Max(1, work.Width - margin * 2);
        var maximumHeight = Math.Max(1, work.Height - margin * 2);
        var naturalWidth = MeasureNaturalWidth(useRenderedColumns);
        var logicalWidth = Math.Min(naturalWidth, maximumWidth / scale);
        MenuPresenter.Width = logicalWidth;
        MenuRoot.Measure(new global::Windows.Foundation.Size(logicalWidth, maximumHeight / scale));
        var width = Math.Min((int)Math.Ceiling(logicalWidth * scale), maximumWidth);
        var height = Math.Max(1, Math.Min((int)Math.Ceiling(MenuRoot.DesiredSize.Height * scale), maximumHeight));
        var x = Math.Clamp(screenPoint.X - width, work.X + margin, Math.Max(work.X + margin, work.X + work.Width - width - margin));
        var y = Math.Clamp(screenPoint.Y - height, work.Y + margin, Math.Max(work.Y + margin, work.Y + work.Height - height - margin));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void FocusInitialItem()
    {
        if (!_closed && AppWindow.IsVisible && GetForegroundWindow() == _window)
            _openApp.Focus(FocusState.Programmatic);
    }

    private double MeasureNaturalWidth(bool useRenderedColumns)
    {
        var items = (MenuPresenter.ItemsSource as IEnumerable<MenuFlyoutItemBase> ?? MenuPresenter.Items.OfType<MenuFlyoutItemBase>())
            .Where(item => item.Visibility == Visibility.Visible).ToArray();
        // Read the arranged allocation before an unconstrained measure changes
        // the stock check/icon state. ItemsSource is the currently hosted menu;
        // the presenter's item collection can still reflect its previous source
        // while a replacement menu's templates are loading.
        var rendered = useRenderedColumns ? items.Select(item =>
        {
            var caption = Caption(item);
            return caption is { ActualWidth: > 0 } && item.ActualWidth > 0
                ? (Required: MeasureCaptionWidth(caption), Reserved: Math.Max(0, item.ActualWidth - caption.ActualWidth))
                : (Required: 0d, Reserved: 0d);
        }).ToArray() : [];
        // A closed presenter initially reports only its minimum width. Measure
        // the actual stock item templates without a width constraint so longer
        // captions retain the checkbox/icon columns and submenu arrow space.
        MenuPresenter.Width = double.NaN;
        MenuPresenter.ApplyTemplate();
        MenuRoot.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var chrome = MenuPresenter.Padding.Left + MenuPresenter.Padding.Right +
            MenuPresenter.BorderThickness.Left + MenuPresenter.BorderThickness.Right;
        var width = MenuPresenter.MinWidth;
        foreach (var allocation in rendered)
            width = Math.Max(width, allocation.Required + allocation.Reserved + chrome);
        foreach (var item in items)
        {
            item.ApplyTemplate();
            item.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, item.DesiredSize.Width + chrome);
        }
        return Math.Ceiling(width);
    }

    private MenuFlyoutItem AddItem(string text, string glyph, MenuCommand command)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => Invoke(command);
        _items.Items.Add(item);
        return item;
    }

    private TrayMenuAvailability CurrentAvailability => _validationAvailability ?? _availability();

    private void Refresh()
    {
        var state = CurrentAvailability;
        _openFolder.IsEnabled = state.CanOpenFolder;
        _pause.IsEnabled = state.CanPause;
        _pause.Text = state.IsPaused ? "Resume syncing" : "Pause syncing";
        _pause.Icon = new FontIcon { Glyph = state.IsPaused ? "\uE768" : "\uE769" };
        AutomationProperties.SetName(_pause, _pause.Text);
        MenuRoot.RequestedTheme = (_validationTheme ?? _theme()) switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void Invoke(MenuCommand command)
    {
        try
        {
            var state = CurrentAvailability;
            if ((command == MenuCommand.OpenFolder && !state.CanOpenFolder) ||
                (command == MenuCommand.TogglePause && !state.CanPause))
            {
                Refresh();
                return;
            }
            Hide();
            // Isolated render checks must never invoke the user's real actions.
            if (_inspection) { _validationCommand = command; return; }
            var action = command switch
            {
                MenuCommand.OpenApp => _actions.ShowApp,
                MenuCommand.OpenFolder => _actions.OpenFolder,
                MenuCommand.TogglePause => _actions.TogglePause,
                MenuCommand.Settings => _actions.Settings,
                _ => _actions.Quit
            };
            action();
        }
        catch (Exception error) { _actions.Error?.Invoke(error); }
    }

    private void MenuPresenter_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        // The standard presenter handles Up/Down, skips disabled/separator
        // entries, and the standard items handle Enter/Space and accessibility.
        if (HandleDismissKey(args.Key)) args.Handled = true;
    }

    private bool HandleDismissKey(VirtualKey key)
    {
        if (key != VirtualKey.Escape) return false;
        Hide();
        if (!_inspection) ReturnKeyboardFocus?.Invoke();
        return true;
    }

    private void ApplyFrame()
    {
        if (_closed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var dark = MenuRoot.ActualTheme == ElementTheme.Dark ? 1 : 0;
        var rounded = 2;
        var border = unchecked((int)0xFFFFFFFE); // XAML draws the theme-aware flyout stroke.
        DwmSetWindowAttribute(_window, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(_window, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(_window, 34, ref border, sizeof(int));
    }

    private nint FrameWindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0006 && (wParam & 0xffff) == 0)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && IsVisible && !_opening && !_inspection && GetForegroundWindow() != _window) Hide();
            });
        // The whole window is the Fluent menu, without a classic non-client strip.
        if (!_closed && message == 0x0083) return 0;
        if (message is 0x001A or 0x031A) DispatcherQueue.TryEnqueue(ApplyFrame);
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public async Task RunUiValidationAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var themeArgument = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--ui-smoke-theme=", StringComparison.Ordinal))?[17..];
        var suffix = themeArgument == "Light" ? "-light" : "";
        _inspection = true;
        _validationTheme = themeArgument == "Light" ? "Light" : "Dark";
        try
        {
            foreach (var fixture in new[]
            {
                (Name: "disconnected", State: new TrayMenuAvailability(false, false, false)),
                (Name: "connected", State: new TrayMenuAvailability(true, true, false)),
                (Name: "paused", State: new TrayMenuAvailability(true, true, true))
            })
            {
                _validationAvailability = fixture.State;
                var area = DisplayArea.Primary.WorkArea;
                ShowAt(new PointInt32(area.X + area.Width - 12, area.Y + area.Height - 12));
                await Task.Delay(180);
                MenuRoot.UpdateLayout();
                AssertCaptionsFit(outputDirectory, fixture.Name + suffix);
                if (MenuPresenter.ActualWidth < 248 || MenuPresenter.ActualHeight < 150 || MenuPresenter.Items.Count != 6)
                    throw new InvalidOperationException("The Fluent tray menu is not fully laid out.");
                if (_openFolder.IsEnabled != fixture.State.CanOpenFolder || _pause.IsEnabled != fixture.State.CanPause ||
                    _pause.Text != (fixture.State.IsPaused ? "Resume syncing" : "Pause syncing"))
                    throw new InvalidOperationException("The Fluent tray menu availability is incorrect.");
                var menuPeer = FrameworkElementAutomationPeer.CreatePeerForElement(MenuPresenter);
                if (menuPeer?.GetAutomationControlType() != AutomationControlType.Menu)
                    throw new InvalidOperationException("The tray presenter does not expose standard menu accessibility.");
                foreach (var item in new[] { _openApp, _openFolder, _pause, _settings, _quit })
                {
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
                    if (peer?.GetAutomationControlType() != AutomationControlType.MenuItem ||
                        peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider ||
                        string.IsNullOrWhiteSpace(peer.GetName()))
                        throw new InvalidOperationException("A tray command is missing native menu-item accessibility.");
                    if (item.ActualHeight < 28 || item.ActualWidth < 160)
                        throw new InvalidOperationException("A tray command is clipped or too small.");
                }
                await SaveMenuCaptureAsync(Path.Combine(outputDirectory, $"tray-context-menu-{fixture.Name}{suffix}.png"));
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "tray-context-menu-assertions.txt"),
                    $"PASS {fixture.Name}{suffix}: Fluent presenter, five standard accessible commands, separator, correct availability, {MenuRoot.ActualWidth:0.##}x{MenuRoot.ActualHeight:0.##}{Environment.NewLine}");
            }
            _validationAvailability = new TrayMenuAvailability(true, true, false);
            _validationCommand = null;
            var invokeProvider = (IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(_settings).GetPattern(PatternInterface.Invoke);
            invokeProvider.Invoke();
            await Task.Delay(80);
            if (_validationCommand != MenuCommand.Settings || IsVisible)
                throw new InvalidOperationException("Invoking a Fluent menu command did not dismiss and dispatch it.");
            ShowAt(new PointInt32(DisplayArea.Primary.WorkArea.X + 300, DisplayArea.Primary.WorkArea.Y + 300));
            if (!HandleDismissKey(VirtualKey.Escape) || IsVisible)
                throw new InvalidOperationException("Escape did not dismiss the tray menu.");
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "tray-context-menu-assertions.txt"),
                $"PASS dismiss{suffix}: command invocation and Escape hide the menu; native Up/Down and Enter/Space retained by standard controls; compositor Acrylic/frame excluded from bitmap capture{Environment.NewLine}");
        }
        finally
        {
            Hide();
            _validationAvailability = null;
            _validationCommand = null;
            _validationTheme = null;
            _inspection = false;
        }
    }

    /// <summary>
    /// Renders an app-owned closed flyout with its stock templates. This avoids
    /// claiming that a bitmap of the activity window captures a popup surface.
    /// </summary>
    internal async Task CaptureMenuAsync(MenuFlyout menu, string path, string theme,
        Action<MenuFlyoutPresenter>? verify = null)
    {
        if (_closed) throw new ObjectDisposedException(nameof(TrayContextMenuWindow));
        var previousInspection = _inspection;
        var previousTheme = _validationTheme;
        var previousSource = MenuPresenter.ItemsSource;
        _inspection = true;
        _validationTheme = theme;
        try
        {
            // The caller closes the real popup before moving its items into this
            // validation presenter. Do not duplicate templates or recreate icons.
            MenuPresenter.ItemsSource = menu.Items;
            var work = DisplayArea.Primary.WorkArea;
            ShowAt(new PointInt32(work.X + work.Width - 12, work.Y + work.Height - 12));
            await Task.Delay(180);
            MenuRoot.UpdateLayout();
            // Checked/unchecked template transitions may finish after ShowAt's
            // first queued pass. Measure the actual hosted menu once settled.
            UpdateGeometry(new PointInt32(work.X + work.Width - 12, work.Y + work.Height - 12), useRenderedColumns: true);
            await WaitForGeometryAsync();
            AssertCaptionsFit(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
            verify?.Invoke(MenuPresenter);
            await SaveMenuCaptureAsync(path);
        }
        finally
        {
            Hide();
            MenuPresenter.ItemsSource = previousSource;
            _validationTheme = previousTheme;
            _inspection = previousInspection;
        }
    }

    private void AssertCaptionsFit(string outputDirectory, string fixture)
    {
        foreach (var item in MenuPresenter.Items.OfType<MenuFlyoutItemBase>().Where(item =>
                     item.Visibility == Visibility.Visible && item is MenuFlyoutItem or MenuFlyoutSubItem))
        {
            var caption = Caption(item) ??
                throw new InvalidOperationException("A Fluent menu command has no rendered caption.");
            var available = caption.ActualWidth;
            var required = MeasureCaptionWidth(caption);
            File.AppendAllText(Path.Combine(outputDirectory, "tray-context-menu-caption-assertions.txt"),
                $"{fixture}: {caption.Text}; required={required:0.###}; available={available:0.###}; item={item.ActualWidth:0.###}; presenter={MenuPresenter.ActualWidth:0.###}; margin={caption.Margin.Left:0.###},{caption.Margin.Right:0.###}; fits={required <= available + 1}{Environment.NewLine}");
            if (required > available + 1)
                throw new InvalidOperationException($"The Fluent menu caption '{caption.Text}' is clipped: needs {required:0.##}, has {available:0.##}.");
            // Restore the constrained stock layout after measuring its caption.
        }
        MenuRoot.UpdateLayout();
    }

    private static TextBlock? Caption(MenuFlyoutItemBase item) =>
        Descendants(item).OfType<TextBlock>().FirstOrDefault(text => text.Name == "TextBlock");

    private static double MeasureCaptionWidth(TextBlock caption)
    {
        caption.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = caption.DesiredSize.Width - caption.Margin.Left - caption.Margin.Right;
        caption.InvalidateMeasure();
        return Math.Max(0, width);
    }

    private async Task SaveMenuCaptureAsync(string path)
    {
        // RenderTargetBitmap excludes compositor Desktop Acrylic. A capture-only
        // fallback shows the same menu shape and themed foreground/stroke against
        // a readable solid surface; shipping keeps the real Desktop Acrylic.
        var previousStyle = MenuPresenter.Style;
        try
        {
            MenuPresenter.Style = (Style)MenuRoot.Resources["MenuCaptureSurfaceStyle"];
            await WaitForGeometryAsync();
            await UiSmokeCapture.SaveAsync(MenuRoot, path);
        }
        finally { MenuPresenter.Style = previousStyle; }
    }

    private async Task WaitForGeometryAsync()
    {
        // MoveAndResize updates the HWND immediately; XAML consumes its size
        // on a later dispatcher turn. Caption bounds alone do not prove that
        // the containing viewport is wide enough to display those captions.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            MenuRoot.UpdateLayout();
            if (MenuRoot.ActualWidth + 1 >= MenuPresenter.Width && MenuRoot.ActualHeight + 1 >= MenuPresenter.ActualHeight) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("The Fluent menu viewport did not settle to its measured command width.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private enum MenuCommand { OpenApp, OpenFolder, TogglePause, Settings, Quit }
    private delegate nint NativeFrameProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, NativeFrameProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, NativeFrameProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

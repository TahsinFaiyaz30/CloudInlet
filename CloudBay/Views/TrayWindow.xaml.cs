using System.Runtime.InteropServices;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CloudBay.Views;

public sealed partial class TrayWindow : Window
{
    private readonly ClientController _controller;
    private readonly ClientViewModel _viewModel;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private bool _menuOpen;
    private bool _active;
    private bool _busy;
    private bool _closed;
    private int _refreshPending;
    private int _lastHeight;
    private readonly PointerEventHandler _settingsPressedHandler;
    private readonly PointerEventHandler _settingsReleasedHandler;
    private bool _anchorToTop;
    private AppSettings DisplaySettings => _viewModel.Preview?.Settings ?? _controller.Settings;
    private SyncSnapshot DisplaySnapshot => _viewModel.Preview?.Snapshot ?? _controller.Snapshot;

    public TrayWindow(ClientController controller, Action openSettings, Action quit)
    {
        _controller = controller;
        _openSettings = openSettings;
        _quit = quit;
        InitializeComponent();
        _settingsPressedHandler = QuickSettings_PointerPressed;
        _settingsReleasedHandler = QuickSettings_PointerReleased;
        QuickSettingsButton.AddHandler(UIElement.PointerPressedEvent, _settingsPressedHandler, handledEventsToo: true);
        QuickSettingsButton.AddHandler(UIElement.PointerReleasedEvent, _settingsReleasedHandler, handledEventsToo: true);
        AnimatedIcon.SetState(QuickSettingsIcon, "Normal");
        _viewModel = new ClientViewModel(controller);
        TrayRoot.DataContext = _viewModel;
        AppWindow.Title = "CloudBay activity";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var preference = 2;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 33, ref preference, sizeof(int));
        Activated += (_, args) =>
        {
            _active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (args.WindowActivationState == WindowActivationState.Deactivated && !_menuOpen) AppWindow.Hide();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _controller.Changed -= Controller_Changed;
            QuickSettingsButton.RemoveHandler(UIElement.PointerPressedEvent, _settingsPressedHandler);
            QuickSettingsButton.RemoveHandler(UIElement.PointerReleasedEvent, _settingsReleasedHandler);
        };
        _controller.Changed += Controller_Changed;
        Refresh();
    }

    public void ShowAtTray()
    {
        if (_closed) return;
        Refresh();
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var monitor = MonitorFromPoint(cursor, 2);
        if (GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0) dpi = monitorDpi;
        var scale = dpi > 0 ? dpi / 96d : 1;
        var margin = (int)Math.Round(12 * scale);
        var width = Math.Min((int)Math.Round(420 * scale), Math.Max(320, work.Width - margin * 2));
        TrayRoot.Measure(new global::Windows.Foundation.Size(width / scale, double.PositiveInfinity));
        var height = Math.Min((int)Math.Ceiling(Math.Max(210, TrayRoot.DesiredSize.Height) * scale), work.Height - margin * 2);
        _lastHeight = height;
        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;
        // The notification area may live along any edge and on any monitor.
        // WorkArea excludes the taskbar, so the flyout never covers its icon.
        if (cursor.X < work.X) x = work.X + margin;
        if (cursor.Y < work.Y) y = work.Y + margin;
        if (cursor.Y < work.Y || cursor.Y >= work.Y + work.Height)
            x = Math.Clamp(cursor.X - width + (int)(24 * scale), work.X + margin, Math.Max(work.X + margin, work.X + work.Width - width - margin));
        else if (cursor.X < work.X || cursor.X >= work.X + work.Width)
            y = Math.Clamp(cursor.Y - height + (int)(24 * scale), work.Y + margin, Math.Max(work.Y + margin, work.Y + work.Height - height - margin));
        var sideTaskbar = cursor.X < work.X || cursor.X >= work.X + work.Width;
        _anchorToTop = cursor.Y < work.Y || (sideTaskbar && cursor.Y < work.Y + work.Height && y <= work.Y + margin);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.Show(!Environment.GetCommandLineArgs().Contains("--ui-smoke"));
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) Activate();
    }

    private void Controller_Changed(object? sender, EventArgs args)
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
        TrayActivityHeading.Visibility = TrayActivitySection.Visibility = _viewModel.HasActivity ? Visibility.Visible : Visibility.Collapsed;
        TrayViewActivity.Visibility = _viewModel.HasActivity ? Visibility.Visible : Visibility.Collapsed;
        TrayActivityColumn.Width = _viewModel.HasActivity ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TrayBucket.Visibility = settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        TrayStatusDetail.Visibility = _viewModel.HasStatusDetail ? Visibility.Visible : Visibility.Collapsed;
        TrayLastSync.Visibility = _viewModel.HasLastSync ? Visibility.Visible : Visibility.Collapsed;
        TrayTransferPanel.Visibility = _viewModel.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        TrayProgress.IsIndeterminate = snapshot.TransferTotalBytes <= 0;
        TrayOpenFolder.Content = settings.IsConfigured ? "Open folder" : "Connect account";
        QuickSyncNow.IsEnabled = QuickOpenFolder.IsEnabled = settings.IsConfigured;
        PauseMenu.IsEnabled = settings.IsConfigured && snapshot.State != ClientState.Paused;
        ResumeMenu.IsEnabled = settings.IsConfigured && snapshot.State == ClientState.Paused;
        MeteredQuickSetting.IsChecked = settings.PauseOnMetered;
        BatteryQuickSetting.IsChecked = settings.PauseOnBatterySaver;
        MeteredQuickSetting.IsEnabled = BatteryQuickSetting.IsEnabled = !_busy;
        TrayRoot.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (AppWindow.IsVisible && !_menuOpen) ResizeToContent();
    }

    private void ResizeToContent()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96d : 1;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var work = DisplayArea.GetFromPoint(position, DisplayAreaFallback.Nearest).WorkArea;
        TrayRoot.Measure(new global::Windows.Foundation.Size(size.Width / scale, double.PositiveInfinity));
        var height = Math.Min((int)Math.Ceiling(Math.Max(210, TrayRoot.DesiredSize.Height) * scale), work.Height - (int)(24 * scale));
        if (height == _lastHeight) return;
        _lastHeight = height;
        var y = Math.Clamp(_anchorToTop ? position.Y : position.Y + size.Height - height,
            work.Y + (int)(12 * scale), Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(position.X, y, size.Width, height));
    }

    private void QuickSettings_Opening(object sender, object args) => _menuOpen = true;
    private void QuickSettings_Closed(object sender, object args)
    {
        _menuOpen = false;
        if (!_active) AppWindow.Hide();
    }

    private void PauseFor_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
        try
        {
            var hours = int.Parse((string)((MenuFlyoutItem)sender).Tag);
            _controller.Pause(hours == 0 ? null : TimeSpan.FromHours(hours));
            Refresh();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void Resume_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
        try { _controller.Resume(); Refresh(); }
        catch (Exception error) { ShowError(error); }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _viewModel.Preview is not null) return;
        _busy = true;
        try { await _controller.SyncNowAsync(); }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; Refresh(); }
    }

    private async void MeteredQuickSetting_Click(object sender, RoutedEventArgs args) =>
        await SaveQuickSettingAsync(new() { PauseOnMetered = MeteredQuickSetting.IsChecked });

    private async void BatteryQuickSetting_Click(object sender, RoutedEventArgs args) =>
        await SaveQuickSettingAsync(new() { PauseOnBatterySaver = BatteryQuickSetting.IsChecked });

    private async Task SaveQuickSettingAsync(PreferenceUpdate update)
    {
        if (_busy || _viewModel.Preview is not null) { Refresh(); return; }
        _busy = true;
        Refresh();
        try { await _controller.UpdatePreferencesAsync(update); }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; Refresh(); }
    }

    private void QuickSettings_PointerEntered(object sender, PointerRoutedEventArgs args) => AnimatedIcon.SetState(QuickSettingsIcon, "PointerOver");
    private void QuickSettings_PointerExited(object sender, PointerRoutedEventArgs args) => AnimatedIcon.SetState(QuickSettingsIcon, "Normal");
    private void QuickSettings_PointerPressed(object sender, PointerRoutedEventArgs args) => AnimatedIcon.SetState(QuickSettingsIcon, "Pressed");
    private void QuickSettings_PointerReleased(object sender, PointerRoutedEventArgs args) => AnimatedIcon.SetState(QuickSettingsIcon, "PointerOver");
    private void QuickSettings_GotFocus(object sender, RoutedEventArgs args) => AnimatedIcon.SetState(QuickSettingsIcon, "PointerOver");
    private void QuickSettings_LostFocus(object sender, RoutedEventArgs args) => AnimatedIcon.SetState(QuickSettingsIcon, "Normal");

    private void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
        if (!_controller.Settings.IsConfigured) { AllSettings_Click(sender, args); return; }
        try { _controller.LaunchFolder(); AppWindow.Hide(); }
        catch (Exception error) { ShowError(error); }
    }

    private void AllSettings_Click(object sender, RoutedEventArgs args)
    {
        AppWindow.Hide();
        _openSettings();
    }

    private void ViewActivity_Click(object sender, RoutedEventArgs args)
    {
        AppWindow.Hide();
        App.MainWindow?.ShowActivity();
    }

    private void Quit_Click(object sender, RoutedEventArgs args) => _quit();

    private void ShowError(Exception error)
    {
        TrayError.Title = "Needs attention";
        TrayError.Message = error.Message;
        TrayError.IsOpen = true;
    }

    public async Task RunUiSmokeAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var themeArgument = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--ui-smoke-theme=", StringComparison.Ordinal))?[17..];
        foreach (var theme in themeArgument == "Light" ? new[] { ElementTheme.Light } : new[] { ElementTheme.Dark })
        {
            var suffix = theme == ElementTheme.Light ? "-light" : "";
            foreach (var state in new[] { ClientState.NotConnected, ClientState.Connecting, ClientState.UpToDate, ClientState.Syncing, ClientState.Paused, ClientState.Offline, ClientState.Attention })
            {
                var preview = ClientPreview.ForState(state);
                _viewModel.SetPreview(preview with { Settings = preview.Settings with { Theme = theme.ToString() } });
                ShowAtTray();
                await Task.Delay(220);
                ResizeToContent();
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "layout.txt"), $"tray-{state}{suffix}: width={AppWindow.Size.Width}, height={AppWindow.Size.Height}{Environment.NewLine}");
                await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, $"tray-{state}{suffix}.png"));
            }
            var historyHeight = AppWindow.Size.Height;
            var quiet = ClientPreview.Connected() with { Activity = [] };
            _viewModel.SetPreview(quiet with { Settings = quiet.Settings with { Theme = theme.ToString() } });
            ShowAtTray();
            await Task.Delay(220);
            ResizeToContent();
            if (TrayActivitySection.Visibility != Visibility.Collapsed || AppWindow.Size.Height >= historyHeight)
                throw new InvalidOperationException("The tray must shrink when there is no activity to display.");
            await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, $"tray-quiet{suffix}.png"));
        }
        _viewModel.SetPreview(null);
        Refresh();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

using System.Runtime.InteropServices;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public TrayWindow(ClientController controller, Action openSettings, Action quit)
    {
        _controller = controller;
        _openSettings = openSettings;
        _quit = quit;
        InitializeComponent();
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
        var height = Math.Min((int)Math.Round(660 * scale), work.Height - margin * 2);
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
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.Show();
        Activate();
    }

    private void Controller_Changed(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, Refresh);

    private void Refresh()
    {
        if (_closed) return;
        _viewModel.Refresh();
        TrayEmptyActivity.Visibility = _viewModel.HasActivity ? Visibility.Collapsed : Visibility.Visible;
        TrayTransferPanel.Visibility = _viewModel.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        TrayProgress.IsIndeterminate = _controller.Snapshot.TransferTotalBytes <= 0;
        TrayOpenFolder.IsEnabled = _controller.Settings.IsConfigured;
        PauseMenu.IsEnabled = _controller.Settings.IsConfigured && _controller.Snapshot.State != ClientState.Paused;
        ResumeMenu.IsEnabled = _controller.Settings.IsConfigured && _controller.Snapshot.State == ClientState.Paused;
        MeteredQuickSetting.IsChecked = _controller.Settings.PauseOnMetered;
        BatteryQuickSetting.IsChecked = _controller.Settings.PauseOnBatterySaver;
        MeteredQuickSetting.IsEnabled = BatteryQuickSetting.IsEnabled = !_busy;
        TrayRoot.RequestedTheme = _controller.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void QuickSettings_Opening(object sender, object args) => _menuOpen = true;
    private void QuickSettings_Closed(object sender, object args)
    {
        _menuOpen = false;
        if (!_active) AppWindow.Hide();
    }

    private void PauseFor_Click(object sender, RoutedEventArgs args)
    {
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
        try { _controller.Resume(); Refresh(); }
        catch (Exception error) { ShowError(error); }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        _busy = true;
        try { await _controller.SyncNowAsync(); }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; Refresh(); }
    }

    private async void MeteredQuickSetting_Click(object sender, RoutedEventArgs args) =>
        await SaveQuickSettingAsync(_controller.Settings with { PauseOnMetered = MeteredQuickSetting.IsChecked });

    private async void BatteryQuickSetting_Click(object sender, RoutedEventArgs args) =>
        await SaveQuickSettingAsync(_controller.Settings with { PauseOnBatterySaver = BatteryQuickSetting.IsChecked });

    private async Task SaveQuickSettingAsync(AppSettings settings)
    {
        if (_busy) { Refresh(); return; }
        _busy = true;
        try { await _controller.SaveSettingsAsync(settings); }
        catch (Exception error) { ShowError(error); }
        finally { _busy = false; Refresh(); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs args)
    {
        try { _controller.LaunchFolder(); AppWindow.Hide(); }
        catch (Exception error) { ShowError(error); }
    }

    private void AllSettings_Click(object sender, RoutedEventArgs args)
    {
        AppWindow.Hide();
        _openSettings();
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
        ShowAtTray();
        await Task.Delay(350);
        await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, "tray.png"));
        TrayRoot.RequestedTheme = ElementTheme.Light;
        await Task.Delay(250);
        await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, "tray-light.png"));
        Refresh();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

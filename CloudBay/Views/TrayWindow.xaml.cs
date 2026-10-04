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
    private bool _footerStacked;
    private bool _suppressAutoResize;
    private bool _keepOpenForInspection;
    private int _appliedBorderColor;
    private int _borderApplyResult;
    private readonly NativeFrameProc _nativeFrameProc;
    private readonly nint _frameWindow;
    private readonly bool _frameHookInstalled;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private AppSettings DisplaySettings => _viewModel.Preview?.Settings ?? _controller.Settings;
    private SyncSnapshot DisplaySnapshot => _viewModel.Preview?.Snapshot ?? _controller.Snapshot;

    public TrayWindow(ClientController controller, Action openSettings, Action quit)
    {
        _controller = controller;
        _openSettings = openSettings;
        _quit = quit;
        InitializeComponent();
        TrayRoot.ActualThemeChanged += TrayRoot_ActualThemeChanged;
        _settingsPressedHandler = QuickSettings_PointerPressed;
        _settingsReleasedHandler = QuickSettings_PointerReleased;
        QuickSettingsButton.AddHandler(UIElement.PointerPressedEvent, _settingsPressedHandler, handledEventsToo: true);
        QuickSettingsButton.AddHandler(UIElement.PointerReleasedEvent, _settingsReleasedHandler, handledEventsToo: true);
        AnimatedIcon.SetState(QuickSettingsIcon, "Normal");
        _viewModel = new ClientViewModel(controller);
        TrayRoot.DataContext = _viewModel;
        TrayRoot.SizeChanged += (_, _) => UpdateFooterLayout();
        TrayPrimaryLabel.SizeChanged += (_, _) => UpdateFooterLayout();
        TrayActivityLabel.SizeChanged += (_, _) => UpdateFooterLayout();
        TrayFooter.SizeChanged += (_, _) =>
        {
            if (!_closed && AppWindow.IsVisible && !_menuOpen && !_suppressAutoResize) ResizeToContent();
        };
        AppWindow.Title = "CloudBay activity";
        AppWindow.IsShownInSwitchers = false;
        // The tray owns all of its chrome. Make WinUI host the XAML surface
        // across the full window before removing the presenter title bar.
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        _frameWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _nativeFrameProc = NativeFrameWindowProc;
        _frameHookInstalled = SetWindowSubclass(_frameWindow, _nativeFrameProc, 0xCB03, 0);
        // Recalculate the native client rectangle after installing its handler.
        // A borderless presenter can otherwise retain a classic non-client
        // strip, which DWMWA_BORDER_COLOR cannot suppress.
        if (_frameHookInstalled)
            SetWindowPos(_frameWindow, 0, 0, 0, 0, 0, 0x0037);
        Activated += (_, args) =>
        {
            ApplyNativeFrame();
            _active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (args.WindowActivationState == WindowActivationState.Deactivated && !_menuOpen && !_keepOpenForInspection) AppWindow.Hide();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _controller.Changed -= Controller_Changed;
            TrayRoot.ActualThemeChanged -= TrayRoot_ActualThemeChanged;
            if (_frameHookInstalled) RemoveWindowSubclass(_frameWindow, _nativeFrameProc, 0xCB03);
            QuickSettingsButton.RemoveHandler(UIElement.PointerPressedEvent, _settingsPressedHandler);
            QuickSettingsButton.RemoveHandler(UIElement.PointerReleasedEvent, _settingsReleasedHandler);
        };
        _controller.Changed += Controller_Changed;
        Refresh();
    }

    public void ShowAtTray(bool keepOpenForInspection = false)
    {
        if (_closed) return;
        _keepOpenForInspection = keepOpenForInspection;
        Refresh();
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var monitor = MonitorFromPoint(cursor, 2);
        if (GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0) dpi = monitorDpi;
        var scale = dpi > 0 ? dpi / 96d : 1;
        var margin = (int)Math.Round(12 * scale);
        var width = Math.Min((int)Math.Round(448 * scale), Math.Max(1, work.Width - margin * 2));
        var frameWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        var frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        // AppWindow.Size includes the remaining native frame even without a
        // title bar. Measure wrapping against client pixels and retain enough
        // outer height for the full content rather than silently clipping it.
        TrayRoot.Measure(new global::Windows.Foundation.Size(Math.Max(1, width - frameWidth) / scale, double.PositiveInfinity));
        var height = Math.Min((int)Math.Ceiling(Math.Max(210, TrayRoot.DesiredSize.Height) * scale) + frameHeight,
            Math.Max(1, work.Height - margin * 2));
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
        TrayViewActivity.Visibility = _viewModel.HasActivity || _viewModel.HasTransferSummary ? Visibility.Visible : Visibility.Collapsed;
        TrayBucket.Visibility = settings.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        TrayStatusDetail.Visibility = (_viewModel.HasStatusDetail && !_viewModel.HasTransferSummary) || snapshot.State == ClientState.NotConnected
            ? Visibility.Visible : Visibility.Collapsed;
        TrayLastSync.Visibility = _viewModel.HasLastSync ? Visibility.Visible : Visibility.Collapsed;
        TrayTransferPanel.Visibility = _viewModel.IsProgressVisible && !_viewModel.HasTransfers ? Visibility.Visible : Visibility.Collapsed;
        TrayLiveTransfers.Visibility = _viewModel.HasTransfers ? Visibility.Visible : Visibility.Collapsed;
        TrayTransferSpeed.Visibility = _viewModel.HasTransferSpeed && _viewModel.HasTransfers ? Visibility.Visible : Visibility.Collapsed;
        TrayQueueAction.Visibility = _viewModel.HasQueue ? Visibility.Visible : Visibility.Collapsed;
        TrayAdditionalTransfers.Visibility = _viewModel.HasAdditionalTransfers ? Visibility.Visible : Visibility.Collapsed;
        TrayProgress.IsIndeterminate = snapshot.TransferTotalBytes <= 0;
        TrayProgressDetail.Visibility = string.IsNullOrEmpty(_viewModel.ProgressLabel) ? Visibility.Collapsed : Visibility.Visible;
        TrayPrimaryLabel.Text = snapshot.State == ClientState.Attention
            ? snapshot.Message.StartsWith("Review required:", StringComparison.Ordinal) ? "Review changes" : "Open CloudBay"
            : settings.IsConfigured ? "Open folder" : "Connect account";
        TrayPrimaryGlyph.Glyph = snapshot.State == ClientState.Attention ? "\uE7BA" : settings.IsConfigured ? "\uE8B7" : "\uE753";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TrayOpenFolder, TrayPrimaryLabel.Text);
        QuickSyncNow.IsEnabled = settings.IsConfigured && !_busy;
        QuickOpenFolder.IsEnabled = settings.IsConfigured;
        PauseMenu.Visibility = snapshot.State == ClientState.Paused ? Visibility.Collapsed : Visibility.Visible;
        ResumeMenu.Visibility = snapshot.State == ClientState.Paused ? Visibility.Visible : Visibility.Collapsed;
        PauseMenu.IsEnabled = ResumeMenu.IsEnabled = settings.IsConfigured && !_busy;
        MeteredQuickSetting.IsChecked = settings.PauseOnMetered;
        BatteryQuickSetting.IsChecked = settings.PauseOnBatterySaver;
        MeteredQuickSetting.IsEnabled = BatteryQuickSetting.IsEnabled = !_busy;
        TrayRoot.RequestedTheme = settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyNativeFrame();
        var statusStyle = snapshot.State switch
        {
            ClientState.UpToDate => "TraySuccessIconStyle",
            ClientState.Attention => "TrayCautionIconStyle",
            ClientState.Offline or ClientState.Paused => "TrayNeutralIconStyle",
            _ => "TrayAccentIconStyle"
        };
        TrayStatusGlyph.Style = (Style)TrayRoot.Resources[statusStyle];
        UpdateFooterLayout();
        if (AppWindow.IsVisible && !_menuOpen) ResizeToContent();
    }

    private void TrayRoot_ActualThemeChanged(FrameworkElement sender, object args) => ApplyNativeFrame();

    private nint NativeFrameWindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Leave the proposed rectangle intact: the entire outer window is
        // the client area. Windows retains the compositor backdrop, rounded
        // corners and shadow; only the standard non-client frame is removed.
        // https://learn.microsoft.com/windows/win32/winmsg/wm-nccalcsize
        if (!_closed && message == 0x0083) return 0;
        // Desktop accessibility changes arrive through WM_SETTINGCHANGE.
        // The UWP HighContrastChanged event can fail to register in an
        // unpackaged desktop app, preventing notification-icon startup.
        if (message is 0x001A or 0x031A)
            DispatcherQueue.TryEnqueue(() => ApplyNativeFrame());
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static bool UseContrastFrame()
    {
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        return !SystemParametersInfo(0x0042, contrast.Size, ref contrast, 0) || (contrast.Flags & 1) != 0;
    }

    private void ApplyNativeFrame()
    {
        if (_closed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dark = TrayRoot.ActualTheme == ElementTheme.Dark ? 1 : 0;
        var rounded = 2;
        // XAML's theme does not opt the native frame into dark mode. Keep its
        // theme in step, and suppress the flyout stroke through DWM so Windows
        // still owns the rounded corners and shadow. Contrast themes retain
        // the system frame for a visible boundary.
        var border = UseContrastFrame() ? DwmColorDefault : DwmColorNone;
        DwmSetWindowAttribute(window, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        DwmSetWindowAttribute(window, DwmCornerPreference, ref rounded, sizeof(int));
        _borderApplyResult = DwmSetWindowAttribute(window, DwmBorderColor, ref border, sizeof(int));
        _appliedBorderColor = border;
    }

    private void UpdateFooterLayout()
    {
        if (_closed || TrayRoot.ActualWidth <= 0) return;
        var hasActivity = TrayViewActivity.Visibility == Visibility.Visible;
        var availableWidth = TrayRoot.ActualWidth - TrayFooter.Padding.Left - TrayFooter.Padding.Right;
        double NaturalWidth(Button button)
        {
            if (button.Content is not FrameworkElement content) return button.MinWidth;
            content.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(button.MinWidth, content.DesiredSize.Width + button.Padding.Left + button.Padding.Right +
                button.BorderThickness.Left + button.BorderThickness.Right);
        }
        // Native text scaling and changing captions participate in the actual
        // measure, so large text gets full-width actions without a fixed scale
        // threshold or a change to the user's Windows accessibility setting.
        _footerStacked = hasActivity && Math.Max(NaturalWidth(TrayOpenFolder), NaturalWidth(TrayViewActivity)) * 2 + 12 > availableWidth;
        TrayPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        TrayActivityColumn.Width = hasActivity && !_footerStacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TrayFooter.ColumnSpacing = hasActivity && !_footerStacked ? 12 : 0;
        TrayFooter.RowSpacing = hasActivity && _footerStacked ? 12 : 0;
        Grid.SetRow(TrayViewActivity, _footerStacked ? 1 : 0);
        Grid.SetColumn(TrayViewActivity, _footerStacked ? 0 : 1);
    }

    private void ResizeToContent()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96d : 1;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var clientSize = AppWindow.ClientSize;
        var frameHeight = Math.Max(0, size.Height - clientSize.Height);
        var work = DisplayArea.GetFromPoint(position, DisplayAreaFallback.Nearest).WorkArea;
        TrayRoot.Measure(new global::Windows.Foundation.Size(Math.Max(1, clientSize.Width) / scale, double.PositiveInfinity));
        var height = Math.Min((int)Math.Ceiling(Math.Max(210, TrayRoot.DesiredSize.Height) * scale) + frameHeight,
            Math.Max(1, work.Height - (int)(24 * scale)));
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
        Refresh();
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

    private void TrayPrimary_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
        if (DisplaySnapshot.State == ClientState.Attention)
        {
            AppWindow.Hide();
            App.MainWindow?.ShowOverview();
            return;
        }
        if (!DisplaySettings.IsConfigured)
        {
            AppWindow.Hide();
            App.MainWindow?.ShowAccount();
            return;
        }
        OpenFolder_Click(sender, args);
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

    private void ViewQueue_Click(object sender, RoutedEventArgs args)
    {
        AppWindow.Hide();
        App.MainWindow?.ShowQueuedActivity();
    }

    private void Quit_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.Preview is not null) return;
        _quit();
    }

    private void TrayError_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!_closed && AppWindow.IsVisible && !_menuOpen) ResizeToContent();
    }

    private void ShowError(Exception error)
    {
        TrayError.Title = "Needs attention";
        TrayError.Message = error.Message;
        TrayError.IsOpen = true;
        if (AppWindow.IsVisible && !_menuOpen) ResizeToContent();
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
                TrayRoot.UpdateLayout();
                AssertTrayLayout(state, outputDirectory);
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "tray-assertions.txt"),
                    $"PASS: {state}{suffix} has the correct contextual content, native gear dimensions, and reachable footer actions.{Environment.NewLine}");
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

            var live = ClientPreview.TransferQueue();
            _viewModel.SetPreview(live with { Settings = live.Settings with { Theme = theme.ToString() } });
            ShowAtTray();
            await Task.Delay(220);
            ResizeToContent();
            TrayContentScroll.ChangeView(null, 0, null, true);
            TrayRoot.UpdateLayout();
            AssertTrayLayout(ClientState.Syncing, outputDirectory);
            var livePosition = TrayLiveTransfers.TransformToVisual(TrayContentScroll).TransformPoint(new global::Windows.Foundation.Point());
            var historyPosition = TrayActivityHeading.TransformToVisual(TrayContentScroll).TransformPoint(new global::Windows.Foundation.Point());
            if (_viewModel.RecentTransfers.Count != 3 ||
                !_viewModel.RecentTransfers.Any(row => row.Glyph == "\uE898") ||
                !_viewModel.RecentTransfers.Any(row => row.Glyph == "\uE896") ||
                livePosition.Y + TrayLiveTransfers.ActualHeight > historyPosition.Y || TrayQueueAction.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The tray must show live uploads and downloads above recent history and expose its queue action.");
            var fileSpeedLabels = new List<TextBlock>();
            CollectSpeedLabels(TrayTransferList, fileSpeedLabels);
            if (TrayTransferSpeed.Visibility != Visibility.Visible ||
                !TrayTransferSpeed.Text.Contains("MiB/s", StringComparison.Ordinal) ||
                !TrayTransferSpeed.Text.Contains("KiB/s", StringComparison.Ordinal) ||
                fileSpeedLabels.Count(label => label.Visibility == Visibility.Visible && label.ActualHeight > 0 &&
                    label.Text.Length > 0) != 2 ||
                ClientViewModel.FormatSpeed(512) != "512 B/s" || ClientViewModel.FormatSpeed(65536) != "64 KiB/s")
                throw new InvalidOperationException("The tray must render measured per-file upload/download rates and aggregate directional rates using B/s, KiB/s or MiB/s.");
            await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, $"tray-live-transfers{suffix}.png"));
            foreach (var mixedState in new[] { ClientState.Attention, ClientState.Offline })
            {
                _viewModel.SetPreview(live with { Settings = live.Settings with { Theme = theme.ToString() },
                    Snapshot = live.Snapshot with { State = mixedState, Message = "Another folder needs attention; these transfers continue." } });
                ShowAtTray();
                await Task.Delay(220);
                ResizeToContent();
                TrayRoot.UpdateLayout();
                if (TrayTransferSpeed.Visibility != Visibility.Visible ||
                    _viewModel.RecentTransfers.Count(row => row.SpeedVisibility == Visibility.Visible) != 2)
                    throw new InvalidOperationException("The tray must retain live directional rates while another sync root needs attention or is offline.");
                await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory,
                    $"tray-live-transfers-{mixedState.ToString().ToLowerInvariant()}{suffix}.png"));
            }
            _viewModel.SetPreview(live with { Settings = live.Settings with { Theme = theme.ToString() },
                Snapshot = live.Snapshot with { State = ClientState.Paused } });
            Refresh();
            if (TrayTransferSpeed.Visibility != Visibility.Collapsed ||
                _viewModel.RecentTransfers.Any(row => row.SpeedVisibility == Visibility.Visible))
                throw new InvalidOperationException("Global pause must hide the tray's aggregate and per-file wire rates.");

            var onlyQueued = live with
            {
                Activity = [],
                Snapshot = live.Snapshot with
                {
                    ActiveTransfers = 0,
                    Transfers = live.Snapshot.Transfers.Where(transfer => transfer.Phase == TransferPhase.Queued).ToArray()
                }
            };
            _viewModel.SetPreview(onlyQueued with { Settings = onlyQueued.Settings with { Theme = theme.ToString() } });
            ShowAtTray();
            await Task.Delay(220);
            ResizeToContent();
            AssertTrayLayout(ClientState.Syncing, outputDirectory);
            if (TrayActivitySection.Visibility != Visibility.Collapsed || TrayViewActivity.Visibility != Visibility.Visible ||
                TrayQueueAction.Visibility != Visibility.Visible || TrayLiveTransfers.Visibility != Visibility.Collapsed ||
                TrayTransferSpeed.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A queue without completed history must still offer Activity and a direct queue action.");
            await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, $"tray-queue-only{suffix}.png"));
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "tray-assertions.txt"),
                $"PASS: transfers{suffix} places live upload/download rows and measured per-file/aggregate rates above history, hides inactive rates, caps visible live rows at three, keeps the complete queue count, and exposes queue/activity actions without history.{Environment.NewLine}");

            var bounded = ClientPreview.ForState(ClientState.Attention);
            _viewModel.SetPreview(bounded with
            {
                Settings = bounded.Settings with { Theme = theme.ToString() },
                Snapshot = bounded.Snapshot with
                {
                    Message = string.Join(" ", Enumerable.Repeat(
                        "CloudBay is waiting for you to review changes before it syncs these deletions to Backblaze B2.", 6))
                }
            });
            ShowAtTray();
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
            AppWindow.Resize(new SizeInt32((int)Math.Round(448 * scale), (int)Math.Round(320 * scale)));
            await Task.Delay(220);
            TrayRoot.UpdateLayout();
            AssertFooterWithinViewport();
            if (TrayContentScroll.ScrollableHeight <= 0)
                throw new InvalidOperationException("A height-constrained tray must scroll its content while retaining the footer.");
            await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, $"tray-bounded{suffix}.png"));
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "tray-assertions.txt"),
                $"PASS: quiet{suffix} shrinks and hides empty history; bounded{suffix} scrolls long content while retaining both footer actions.{Environment.NewLine}");

            var primaryFontSize = TrayPrimaryLabel.FontSize;
            var activityFontSize = TrayActivityLabel.FontSize;
            try
            {
                _suppressAutoResize = true;
                TrayPrimaryLabel.FontSize = 28;
                TrayActivityLabel.FontSize = 28;
                UpdateFooterLayout();
                AppWindow.Resize(new SizeInt32((int)Math.Round(448 * scale), (int)Math.Round(380 * scale)));
                await Task.Delay(220);
                TrayRoot.UpdateLayout();
                UpdateFooterLayout();
                if (!_footerStacked || Grid.GetRow(TrayViewActivity) != 1 || Grid.GetColumn(TrayViewActivity) != 0)
                    throw new InvalidOperationException("Large tray action text must stack rather than overflow two narrow columns.");
                if (AppWindow.Size.Height != (int)Math.Round(380 * scale) || TrayContentScroll.ScrollableHeight <= 0)
                    throw new InvalidOperationException("The large-text proof must retain its bounded height and a scrolling body.");
                AssertFooterWithinViewport();
                foreach (var button in new[] { TrayOpenFolder, TrayViewActivity })
                {
                    var content = (FrameworkElement)button.Content;
                    if (content.DesiredSize.Width + button.Padding.Left + button.Padding.Right +
                        button.BorderThickness.Left + button.BorderThickness.Right > button.ActualWidth + 1)
                        throw new InvalidOperationException("A large-text tray action must fit completely within its button.");
                }
                await UiSmokeCapture.SaveAsync(TrayRoot, Path.Combine(outputDirectory, $"tray-large-text{suffix}.png"));
                await File.AppendAllTextAsync(Path.Combine(outputDirectory, "tray-assertions.txt"),
                    $"PASS: large-text{suffix} measures native action content, stacks the footer, and retains complete captions within the bounded window.{Environment.NewLine}");
            }
            finally
            {
                TrayPrimaryLabel.FontSize = primaryFontSize;
                TrayActivityLabel.FontSize = activityFontSize;
                UpdateFooterLayout();
                _suppressAutoResize = false;
            }
        }
        _viewModel.SetPreview(null);
        Refresh();
    }

    private void AssertTrayLayout(ClientState state, string outputDirectory)
    {
        var clientOrigin = new Point();
        if (!GetWindowRect(_frameWindow, out var windowBounds) ||
            !GetClientRect(_frameWindow, out var clientBounds) || !ClientToScreen(_frameWindow, ref clientOrigin))
            throw new InvalidOperationException("The native tray window geometry could not be inspected.");
        var width = windowBounds.Right - windowBounds.Left;
        var height = windowBounds.Bottom - windowBounds.Top;
        var clientWidth = clientBounds.Right - clientBounds.Left;
        var clientHeight = clientBounds.Bottom - clientBounds.Top;
        File.AppendAllText(Path.Combine(outputDirectory, "tray-native-frame.txt"),
            $"{state}: theme={TrayRoot.ActualTheme}; style={unchecked((uint)GetWindowLongPtr(_frameWindow, -16).ToInt64()):X8}; exStyle={unchecked((uint)GetWindowLongPtr(_frameWindow, -20).ToInt64()):X8}; outer={width}x{height}; client={clientWidth}x{clientHeight}; clientOrigin={clientOrigin.X - windowBounds.Left},{clientOrigin.Y - windowBounds.Top}; subclass={_frameHookInstalled}; border={_appliedBorderColor:X8}/{_borderApplyResult:X8}{Environment.NewLine}");
        if (!_frameHookInstalled || width != clientWidth || height != clientHeight ||
            clientOrigin.X != windowBounds.Left || clientOrigin.Y != windowBounds.Top)
            throw new InvalidOperationException("The tray's XAML client surface must cover its entire native window without a classic frame strip.");
        if (AppWindow.Presenter is not OverlappedPresenter { HasBorder: false, HasTitleBar: false })
            throw new InvalidOperationException("The tray must retain its borderless native presenter.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // DWMWA_BORDER_COLOR is a setter-only attribute. DWM rejects
            // reading it with E_INVALIDARG, even after successfully applying it.
            var borderResult = _borderApplyResult;
            var border = _appliedBorderColor;
            var darkResult = DwmGetWindowAttribute(window, DwmUseImmersiveDarkMode, out var dark, sizeof(int));
            var cornerResult = DwmGetWindowAttribute(window, DwmCornerPreference, out var corners, sizeof(int));
            if (borderResult < 0 || border != (UseContrastFrame() ? DwmColorDefault : DwmColorNone) ||
                darkResult < 0 || dark != (TrayRoot.ActualTheme == ElementTheme.Dark ? 1 : 0) ||
                cornerResult < 0 || corners != 2)
            {
                File.AppendAllText(Path.Combine(outputDirectory, "tray-assertions.txt"),
                    $"FAIL native frame: theme={TrayRoot.ActualTheme}; contrast={UseContrastFrame()}; border={border:X8}/{borderResult:X8}; dark={dark}/{darkResult:X8}; corners={corners}/{cornerResult:X8}{Environment.NewLine}");
                throw new InvalidOperationException("The native tray frame must match its theme and suppress its stroke while retaining rounded corners.");
            }
        }
        var hasHistory = _viewModel.HasActivity;
        var hasProgress = (state is ClientState.Syncing or ClientState.Connecting) && !_viewModel.HasTransfers;
        var hasActivityAction = hasHistory || _viewModel.HasTransferSummary;
        if ((TrayActivitySection.Visibility == Visibility.Visible) != hasHistory ||
            (TrayActivityHeading.Visibility == Visibility.Visible) != hasHistory ||
            (TrayViewActivity.Visibility == Visibility.Visible) != hasActivityAction ||
            (TrayTransferPanel.Visibility == Visibility.Visible) != hasProgress ||
            (TrayLiveTransfers.Visibility == Visibility.Visible) != _viewModel.HasTransfers ||
            (TrayQueueAction.Visibility == Visibility.Visible) != _viewModel.HasQueue ||
            _viewModel.RecentTransfers.Count > 3 ||
            (ResumeMenu.Visibility == Visibility.Visible) != (state == ClientState.Paused))
            throw new InvalidOperationException($"The {state} tray contains an irrelevant section or omits a relevant action.");
        if (state == ClientState.Attention && TrayPrimaryLabel.Text != "Review changes")
            throw new InvalidOperationException("A pending-deletion warning must offer its review action instead of opening Explorer.");
        if (Math.Abs(QuickSettingsIcon.ActualWidth - 20) > 0.5 ||
            Math.Abs(QuickSettingsIcon.ActualHeight - 20) > 0.5 ||
            QuickSettingsButton.ActualWidth < 40 || QuickSettingsButton.ActualHeight < 40)
            throw new InvalidOperationException("The settings animation must stay within a 20 px icon and a 40 px button.");
        if (!hasActivityAction && Math.Abs(TrayOpenFolder.ActualWidth - (TrayRoot.ActualWidth - TrayFooter.Padding.Left - TrayFooter.Padding.Right)) > 1)
            throw new InvalidOperationException("A quiet tray's primary action must fill its footer without a gap for the hidden action.");
        if (hasHistory && TrayContentScroll.ScrollableHeight <= 0.5 &&
            TrayActivitySection.ContainerFromIndex(_viewModel.RecentActivity.Count - 1) is FrameworkElement lastEntry)
        {
            var origin = lastEntry.TransformToVisual(TrayContentScroll).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (origin.Y + lastEntry.ActualHeight > TrayContentScroll.ActualHeight - TrayContentScroll.Padding.Bottom + 1)
                throw new InvalidOperationException("The final recent activity entry must fit completely inside the tray viewport.");
        }
        AssertFooterWithinViewport();
    }

    private void AssertFooterWithinViewport()
    {
        foreach (var button in new[] { TrayOpenFolder, TrayViewActivity }.Where(button => button.Visibility == Visibility.Visible))
        {
            var origin = button.TransformToVisual(TrayRoot).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (button.ActualHeight < 48 || origin.Y < 0 || origin.Y + button.ActualHeight > TrayRoot.ActualHeight + 1)
                throw new InvalidOperationException("Tray footer actions must retain their target size and remain inside the window.");
        }
    }

    private static void CollectSpeedLabels(DependencyObject parent, List<TextBlock> labels)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock { Name: "TrayFileSpeed" } label) labels.Add(label);
            CollectSpeedLabels(child, labels);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct HighContrast { public uint Size, Flags; public nint DefaultScheme; }
    private delegate nint NativeFrameProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, NativeFrameProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, NativeFrameProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}

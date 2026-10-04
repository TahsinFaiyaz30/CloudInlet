using CloudBay.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloudBay.Windows;

public sealed record TrayMenuAvailability(bool CanOpenFolder = true, bool CanPause = true, bool IsPaused = false);
public sealed record TrayIconActions(Action ShowApp, Action OpenFolder, Action TogglePause, Action Settings, Action Quit)
{
    public Func<TrayMenuAvailability>? Availability { get; init; }
    public Action? BeforeMenuOpen { get; init; }
    public Action<Exception>? Error { get; init; }
}

/// <summary>Per-user Shell notification icon. All updates and timers run on its owner UI thread.</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8043;
    private const uint TimerMessage = 0x113;
    private const uint AnimationTimerId = 0xCB03;
    private const uint RestoreTimerId = 0xCB04;
    private const uint IconId = 1;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly TrayIconImages? _images;
    private readonly SubclassProc _callback;
    private readonly Action _show;
    private readonly Action _open;
    private readonly TrayIconActions? _actions;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private TrayIconVisualState _visualState = TrayIconVisualState.Disconnected;
    private UIntPtr _animationTimer;
    private UIntPtr _restoreTimer;
    private int _restoreAttempts;
    private int _frame;
    private bool _version4;
    private bool _shellRegistered;
    private bool _animationEnabled;
    private bool _menuOpen;
    private bool _disposed;

    public TrayIcon(IntPtr window, string iconPath, Action show, Action open, TrayIconActions? actions = null)
    {
        _window = window; _show = show; _open = open; _actions = actions; _callback = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _animationEnabled = ClientAnimationEnabled();
        // Keep smooth alpha edges when the Shell scales to its small metric.
        var size = Math.Clamp(Math.Max(32, GetSystemMetrics(49)), 32, 64); // SM_CYSMICON
        _icon = LoadImage(IntPtr.Zero, iconPath, 1, size, size, 0x10);
        if (_icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the CloudBay tray icon.");
        try { _images = new TrayIconImages(_icon, size); }
        catch (Win32Exception error)
        {
            // Decoration failure must not remove the usable base icon/menu.
            ReportError(error);
        }
        _data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window,
            Id = IconId, Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = _icon,
            Tip = "CloudBay – Backblaze B2", Info = "", InfoTitle = "" };
        if (!SetWindowSubclass(window, _callback, 0xCB02, UIntPtr.Zero))
        {
            _images?.Dispose(); DestroyIcon(_icon);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            if (!Add()) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not add the CloudBay notification icon.");
        }
        catch
        {
            RemoveWindowSubclass(window, _callback, 0xCB02); _images?.Dispose(); DestroyIcon(_icon); throw;
        }
    }

    public bool IsAnimating => _animationTimer != UIntPtr.Zero;
    public TrayIconVisualState VisualState => _visualState;

    private bool Add()
    {
        _data.Flags = 1 | 2 | 4 | 0x80; // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
        if (!ShellNotifyIcon(0, ref _data)) { _shellRegistered = false; return false; }
        _shellRegistered = true;
        _data.Version = 4;
        _version4 = ShellNotifyIcon(4, ref _data);
        return true;
    }

    public void Update(SyncSnapshot snapshot)
    {
        if (_disposed) return;
        var visualState = TrayIconPresentation.VisualState(snapshot);
        if (_visualState != visualState)
        {
            _visualState = visualState;
            _frame = 0;
            SetIcon();
        }
        Update(snapshot.Message);
        UpdateAnimation();
    }

    /// <summary>Tooltip-only compatibility overload. Activity-driven motion uses the snapshot overload.</summary>
    public void Update(string state)
    {
        if (_disposed) return;
        var tip = TrayIconPresentation.Tooltip(state);
        if (string.Equals(_data.Tip, tip, StringComparison.Ordinal)) return;
        _data.Tip = tip;
        Modify(4 | 0x80);
    }

    private void SetIcon()
    {
        var icon = _images?.Get(_visualState, _frame) ?? _icon;
        if (_data.Icon == icon) return;
        _data.Icon = icon;
        Modify(2);
    }

    private void Modify(uint flags)
    {
        _data.Flags = flags;
        if (_shellRegistered) ShellNotifyIcon(1, ref _data);
    }

    private void UpdateAnimation()
    {
        var animate = !_disposed && _animationEnabled && _images is not null && _visualState == TrayIconVisualState.Transferring;
        if (animate && _animationTimer == UIntPtr.Zero)
            _animationTimer = SetTimer(_window, AnimationTimerId, TrayIconPresentation.FrameIntervalMilliseconds, IntPtr.Zero);
        else if (!animate && _animationTimer != UIntPtr.Zero)
        {
            KillTimer(_window, _animationTimer); _animationTimer = UIntPtr.Zero;
            _frame = 0; SetIcon();
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (_disposed) return DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == _taskbarCreated)
        {
            _shellRegistered = false;
            if (Add())
            {
                if (_restoreTimer != UIntPtr.Zero) KillTimer(_window, _restoreTimer);
                _restoreTimer = UIntPtr.Zero;
            }
            else
            {
                _restoreAttempts = 0;
                if (_restoreTimer == UIntPtr.Zero) _restoreTimer = SetTimer(_window, RestoreTimerId, 500, IntPtr.Zero);
            }
            return IntPtr.Zero;
        }
        if (message == TimerMessage)
        {
            if (_animationTimer != UIntPtr.Zero && wParam == _animationTimer)
            {
                _frame = (_frame + 1) % TrayIconPresentation.FrameCount;
                SetIcon();
                return IntPtr.Zero;
            }
            if (_restoreTimer != UIntPtr.Zero && wParam == _restoreTimer)
            {
                // Explorer may broadcast before its tray is ready. Recover
                // only for five seconds, without an idle background timer.
                if (Add() || ++_restoreAttempts >= 10)
                {
                    KillTimer(_window, _restoreTimer); _restoreTimer = UIntPtr.Zero;
                }
                return IntPtr.Zero;
            }
        }
        if (message == 0x1a) // WM_SETTINGCHANGE, including reduced motion/high contrast
        {
            _animationEnabled = ClientAnimationEnabled();
            UpdateAnimation();
        }
        if (message == CallbackMessage)
        {
            try
            {
                switch (TrayIconPresentation.DecodeCallback(wParam.ToUInt64(), lParam.ToInt64(), _version4, IconId))
                {
                    case TrayIconInteraction.ShowActivity: _show(); break;
                    case TrayIconInteraction.OpenApp: _open(); break;
                    case TrayIconInteraction.ContextMenu: ShowContextMenu(); break;
                }
            }
            catch (Exception error) { ReportError(error); }
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_menuOpen || _disposed) return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _menuOpen = true;
        uint command = 0;
        try
        {
            var availability = _actions?.Availability?.Invoke() ?? new TrayMenuAvailability(false, false);
            Append(menu, 1, "Open CloudBay", true);
            Append(menu, 2, "Open CloudBay folder", _actions is not null && availability.CanOpenFolder);
            AppendSeparator(menu);
            Append(menu, 3, availability.IsPaused ? "Resume syncing" : "Pause syncing", _actions is not null && availability.CanPause);
            Append(menu, 4, "Settings", _actions is not null);
            AppendSeparator(menu);
            Append(menu, 5, "Quit CloudBay", _actions is not null);
            _actions?.BeforeMenuOpen?.Invoke();
            var position = ContextMenuPosition();
            // Foreground ownership and WM_NULL make native notification menus
            // dismiss reliably instead of staying open/behind the taskbar.
            SetForegroundWindow(_window);
            command = TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x8 | 0x2, position.X, position.Y, _window, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
            PostMessage(_window, 0, UIntPtr.Zero, IntPtr.Zero);
            if (!_disposed && command == 0) ShellNotifyIcon(3, ref _data); // NIM_SETFOCUS after cancellation
        }
        if (_disposed || command == 0) return;
        switch (command)
        {
            case 1: (_actions?.ShowApp ?? _open)(); break;
            case 2: _actions?.OpenFolder(); break;
            case 3: _actions?.TogglePause(); break;
            case 4: _actions?.Settings(); break;
            case 5: _actions?.Quit(); break;
        }
    }

    private Point ContextMenuPosition()
    {
        var point = new Point();
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _window, Id = IconId };
        var hasPosition = GetCursorPos(out point);
        if (ShellNotifyIconGetRect(ref identifier, out var rect) >= 0)
        {
            // WM_CONTEXTMENU's callback wParam is undefined in version 4.
            // Keyboard invocation belongs beside the actual icon, including
            // monitors with negative screen coordinates.
            if (!hasPosition || point.X < rect.Left || point.X > rect.Right || point.Y < rect.Top || point.Y > rect.Bottom)
                point = new Point { X = rect.Right, Y = rect.Top };
        }
        return point;
    }

    private static void Append(IntPtr menu, uint command, string label, bool enabled)
    {
        if (!AppendMenu(menu, enabled ? 0u : 3u, command, label)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void AppendSeparator(IntPtr menu)
    {
        if (!AppendMenu(menu, 0x800, 0, null)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static bool ClientAnimationEnabled()
    {
        var animated = true;
        if (SystemParametersInfo(0x1042, 0, ref animated, 0) && !animated) return false; // SPI_GETCLIENTAREAANIMATION
        var highContrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        return !SystemParametersInfo(0x42, highContrast.Size, ref highContrast, 0) || (highContrast.Flags & 1) == 0;
    }
    private void ReportError(Exception error)
    {
        // Exceptions must not unwind through an unmanaged window procedure.
        try { _actions?.Error?.Invoke(error); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_animationTimer != UIntPtr.Zero) KillTimer(_window, _animationTimer);
        if (_restoreTimer != UIntPtr.Zero) KillTimer(_window, _restoreTimer);
        _animationTimer = UIntPtr.Zero; _restoreTimer = UIntPtr.Zero;
        if (_menuOpen) EndMenu();
        ShellNotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _callback, 0xCB02);
        _images?.Dispose(); DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct HighContrast { public uint Size, Flags; public IntPtr DefaultScheme; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
}

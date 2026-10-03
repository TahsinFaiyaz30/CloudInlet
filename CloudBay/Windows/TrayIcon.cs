using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloudBay.Windows;

/// <summary>Per-user notification area icon, owned by the hidden WinUI main window.</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8043;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly SubclassProc _callback;
    private readonly Action _show;
    private readonly Action _open;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;

    public TrayIcon(IntPtr window, string iconPath, Action show, Action open)
    {
        _window = window; _show = show; _open = open; _callback = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10);
        if (_icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the CloudBay tray icon.");
        _data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window,
            Id = 1, Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon, Tip = "CloudBay – Backblaze B2", Info = "", InfoTitle = "" };
        if (!SetWindowSubclass(window, _callback, 0xCB02, UIntPtr.Zero))
        { DestroyIcon(_icon); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { Add(); }
        catch { RemoveWindowSubclass(window, _callback, 0xCB02); DestroyIcon(_icon); throw; }
    }
    private void Add()
    {
        _data.Flags = 1 | 2 | 4;
        if (!ShellNotifyIcon(0, ref _data)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not add the CloudBay notification icon.");
        _data.Version = 4; ShellNotifyIcon(4, ref _data);
    }
    public void Update(string state)
    {
        if (_disposed) return;
        _data.Tip = ("CloudBay – " + state)[..Math.Min(127, ("CloudBay – " + state).Length)];
        _data.Flags = 4; ShellNotifyIcon(1, ref _data);
    }
    private IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == _taskbarCreated) { try { Add(); } catch { } return IntPtr.Zero; }
        if (message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xffff);
            if (notification == 0x203) _open();
            else if (notification is 0x202 or 0x205 or 0x7b or 0x400 or 0x401) _show();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ShellNotifyIcon(2, ref _data); RemoveWindowSubclass(_window, _callback, 0xCB02); DestroyIcon(_icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloudBay.Windows;

/// <summary>Small, cached alpha icons. No bitmap or icon is allocated during animation.</summary>
internal sealed class TrayIconImages : IDisposable
{
    private readonly Dictionary<TrayIconVisualState, IntPtr[]> _icons = [];
    private bool _disposed;

    internal TrayIconImages(IntPtr baseIcon, int size)
    {
        try
        {
            var source = ReadIcon(baseIcon, size);
            foreach (var state in Enum.GetValues<TrayIconVisualState>())
            {
                var frames = new IntPtr[state == TrayIconVisualState.Transferring ? TrayIconPresentation.FrameCount : 1];
                _icons.Add(state, frames);
                for (var frame = 0; frame < frames.Length; frame++)
                    frames[frame] = CreateIcon(TrayIconPixels.WithBadge(source, size, state, frame), size);
            }
        }
        catch { Dispose(); throw; }
    }

    internal IntPtr Get(TrayIconVisualState state, int frame) => _icons[state][frame % _icons[state].Length];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var frames in _icons.Values)
            foreach (var icon in frames)
                if (icon != IntPtr.Zero) DestroyIcon(icon);
        _icons.Clear();
    }

    private static byte[] ReadIcon(IntPtr icon, int size)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            bitmap = CreateBitmapSection(dc, size, out var pixels);
            previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // DIB sections are zero-initialized and the alpha-aware icon draw
            // retains premultiplied BGRA needed by CreateIconIndirect.
            if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 3))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[checked(size * size * 4)];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static IntPtr CreateIcon(byte[] bytes, int size)
    {
        var color = CreateBitmapSection(IntPtr.Zero, size, out var pixels);
        IntPtr mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, pixels, bytes.Length);
            var stride = checked((size + 31) / 32 * 4);
            var maskBytes = new byte[checked(stride * size)];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    if (bytes[(y * size + x) * 4 + 3] == 0)
                        maskBytes[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            mask = CreateBitmap(size, size, 1, 1, maskBytes);
            if (mask == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
            var result = CreateIconIndirect(ref info);
            if (result == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        finally
        {
            if (mask != IntPtr.Zero) DeleteObject(mask);
            DeleteObject(color);
        }
    }

    private static IntPtr CreateBitmapSection(IntPtr dc, int size, out IntPtr pixels)
    {
        var info = new BitmapInfo { Header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = size, Height = -size,
            Planes = 1, BitCount = 32, SizeImage = checked((uint)(size * size * 4))
        } };
        var bitmap = CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint XHotspot, YHotspot; public IntPtr Mask, Color;
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}

internal static class TrayIconPixels
{
    internal static byte[] WithBadge(byte[] source, int size, TrayIconVisualState state, int frame)
    {
        if (size is < 16 or > 64 || source.Length != size * size * 4) throw new ArgumentException("Invalid tray bitmap dimensions.");
        var result = (byte[])source.Clone();
        if (state == TrayIconVisualState.Disconnected) return result;
        var background = state switch
        {
            TrayIconVisualState.Idle => (R: 37, G: 128, B: 68),
            TrayIconVisualState.Paused => (R: 249, G: 203, B: 82),
            TrayIconVisualState.Attention => (R: 196, G: 43, B: 64),
            TrayIconVisualState.Offline => (R: 92, G: 100, B: 111),
            _ => (R: 93, G: 216, B: 246)
        };
        var darkGlyph = state is TrayIconVisualState.Paused or TrayIconVisualState.Checking or TrayIconVisualState.Transferring;
        var foreground = darkGlyph ? (R: 15, G: 45, B: 57) : (R: 255, G: 255, B: 255);
        var angle = (frame % TrayIconPresentation.FrameCount) * Math.PI * 2 / TrayIconPresentation.FrameCount;
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        const int samples = 4;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var inside = 0; var glyph = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var dx = ((x + (sx + .5) / samples) / size - .755) / .235;
                        var dy = ((y + (sy + .5) / samples) / size - .755) / .235;
                        if (dx * dx + dy * dy > 1) continue;
                        inside++;
                        if (IsGlyph(dx, dy, state, cosine, sine)) glyph++;
                    }
                if (inside == 0) continue;
                var offset = (y * size + x) * 4;
                var alpha = inside / (double)(samples * samples);
                var ratio = glyph / (double)inside;
                Blend(result, offset, 0, background.B + (foreground.B - background.B) * ratio, alpha);
                Blend(result, offset, 1, background.G + (foreground.G - background.G) * ratio, alpha);
                Blend(result, offset, 2, background.R + (foreground.R - background.R) * ratio, alpha);
                result[offset + 3] = (byte)Math.Round(255 * alpha + result[offset + 3] * (1 - alpha));
            }
        return result;
    }

    private static void Blend(byte[] bytes, int offset, int channel, double color, double alpha) =>
        bytes[offset + channel] = (byte)Math.Clamp(Math.Round(color * alpha + bytes[offset + channel] * (1 - alpha)), 0, 255);

    private static bool IsGlyph(double x, double y, TrayIconVisualState state, double cosine, double sine) => state switch
    {
        TrayIconVisualState.Idle => Line(x, y, -.52, .02, -.13, .4, .14) || Line(x, y, -.13, .4, .55, -.38, .14),
        TrayIconVisualState.Paused => Math.Abs(y) < .52 && (Math.Abs(x - .28) < .13 || Math.Abs(x + .28) < .13),
        TrayIconVisualState.Attention => (Math.Abs(x) < .13 && y is > -.59 and < .17) || x * x + (y - .49) * (y - .49) < .025,
        TrayIconVisualState.Offline => Line(x, y, -.5, .5, .5, -.5, .15),
        _ => SyncGlyph(x * cosine + y * sine, -x * sine + y * cosine)
    };

    private static bool SyncGlyph(double x, double y)
    {
        var radius = x * x + y * y;
        // Opposite arcs and their arrowheads keep the icon legible at 16 px,
        // while twelve cached orientations provide a steady, restrained motion.
        return (radius is > .21 and < .49 && (y < -.08 || y > .08)) ||
            (x is > .28 and < .68 && y is > -.3 and < .13 && y > -x + .3) ||
            (x is > -.68 and < -.28 && y is > -.13 and < .3 && y < -x - .3);
    }

    private static bool Line(double x, double y, double x1, double y1, double x2, double y2, double radius)
    {
        var dx = x2 - x1; var dy = y2 - y1;
        var t = Math.Clamp(((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy), 0, 1);
        var distanceX = x - (x1 + t * dx); var distanceY = y - (y1 + t * dy);
        return distanceX * distanceX + distanceY * distanceY < radius * radius;
    }
}

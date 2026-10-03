using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CloudBay.Windows;

/// <summary>Small Windows folder icons loaded from Shell definitions, without opening a backed-up folder.</summary>
public static class FolderIconProvider
{
    // ImageSource objects belong to their UI thread. Seven sizes and thirteen folder identities bound this cache.
    [ThreadStatic]
    private static Dictionary<(Guid Folder, int Pixels), ImageSource?>? _cache;

    /// <summary>Call on the UI thread. Pixels is the physical image size; null permits a Fluent font icon fallback.</summary>
    public static ImageSource? GetIcon(string? folderName, int pixels = 48)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        return KnownFolderBackup.FolderIds.TryGetValue(folderName, out var id) ? Get(id, pixels) : null;
    }

    /// <summary>The Windows stock folder icon for a user-selected backup folder.</summary>
    public static ImageSource? GetCustomFolderIcon(int pixels = 48) => Get(Guid.Empty, pixels);

    private static ImageSource? Get(Guid id, int requestedPixels)
    {
        if (DispatcherQueue.GetForCurrentThread() is null) return null;
        var pixels = requestedPixels switch
        {
            <= 16 => 16,
            <= 24 => 24,
            <= 32 => 32,
            <= 48 => 48,
            <= 64 => 64,
            <= 96 => 96,
            _ => 128
        };
        var cache = _cache ??= [];
        if (cache.TryGetValue((id, pixels), out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var data = ShellFolderIconReader.Read(id == Guid.Empty ? null : id, pixels);
            if (data is not null)
            {
                var bitmap = new WriteableBitmap(pixels, pixels);
                using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(data);
                bitmap.Invalidate();
                image = bitmap;
            }
        }
        catch (Exception error) when (error is COMException or Win32Exception or IOException or ArgumentException or
            UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // A missing Shell resource must not prevent folder backup controls from being used.
        }
        cache[(id, pixels)] = image;
        return image;
    }
}

/// <summary>Produces owned BGRA pixels; no COM, icon, bitmap, or device-context handle escapes Read.</summary>
internal static class ShellFolderIconReader
{
    public static byte[]? Read(Guid? folderId, int pixels)
    {
        if (pixels is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(pixels));
        var location = folderId is { } id ? GetKnownFolderIcon(id) : GetStockFolderIcon();
        if (location is not { } resource) return null;
        IntPtr icon = IntPtr.Zero;
        try
        {
            // SHDefExtractIcon interprets a negative index as a resource ID and honors the requested pixel size.
            var result = SHDefExtractIcon(resource.Path, resource.Index, 0, out icon, IntPtr.Zero, (uint)pixels);
            if (result != 0 || icon == IntPtr.Zero) return null;
            return RenderIcon(icon, pixels);
        }
        finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
    }

    private static (string Path, int Index)? GetKnownFolderIcon(Guid id)
    {
        const int changedApartment = unchecked((int)0x80010106);
        var initialized = CoInitializeEx(IntPtr.Zero, 2);
        if (initialized < 0 && initialized != changedApartment) Marshal.ThrowExceptionForHR(initialized);
        IntPtr manager = IntPtr.Zero, folder = IntPtr.Zero;
        var definition = new KnownFolderDefinition();
        try
        {
            var clsid = new Guid("4DF0C730-DF9D-4AE3-9153-AA6B82E9795A");
            var iid = new Guid("8BE2D872-86AA-4D47-B776-32CCA40C7018");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out manager));
            var getFolder = GetMethod<GetFolder>(manager, 6);
            Marshal.ThrowExceptionForHR(getFolder(manager, ref id, out folder));
            var getDefinition = GetMethod<GetFolderDefinition>(folder, 11);
            Marshal.ThrowExceptionForHR(getDefinition(folder, out definition));
            var resource = Marshal.PtrToStringUni(definition.Icon);
            if (string.IsNullOrWhiteSpace(resource)) return null;
            resource = Environment.ExpandEnvironmentVariables(resource.TrimStart('@'));
            if (resource.Length >= 260) return null;
            var path = new StringBuilder(resource, 260);
            var index = PathParseIconLocation(path);
            return LocalWindowsResource(path.ToString(), index);
        }
        finally
        {
            // FreeKnownFolderDefinitionFields is an inline SDK helper; release each of its eight allocations.
            Marshal.FreeCoTaskMem(definition.Name);
            Marshal.FreeCoTaskMem(definition.Description);
            Marshal.FreeCoTaskMem(definition.RelativePath);
            Marshal.FreeCoTaskMem(definition.ParsingName);
            Marshal.FreeCoTaskMem(definition.Tooltip);
            Marshal.FreeCoTaskMem(definition.LocalizedName);
            Marshal.FreeCoTaskMem(definition.Icon);
            Marshal.FreeCoTaskMem(definition.Security);
            if (folder != IntPtr.Zero) Marshal.Release(folder);
            if (manager != IntPtr.Zero) Marshal.Release(manager);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static (string Path, int Index)? GetStockFolderIcon()
    {
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>(), Path = "" };
        try
        {
            // SIID_FOLDER + SHGSI_ICONLOCATION retrieves a Windows resource location without a user-folder lookup.
            if (SHGetStockIconInfo(3, 0, ref info) != 0) return null;
            return LocalWindowsResource(info.Path, info.IconIndex);
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }

    private static (string Path, int Index)? LocalWindowsResource(string path, int index)
    {
        path = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) return null;
        var full = Path.GetFullPath(path);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        // Never read an icon placed in a redirected/cloud folder or on a network share.
        return full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? (full, index) : null;
    }

    private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static byte[] RenderIcon(IntPtr icon, int pixels)
    {
        var context = CreateCompatibleDC(IntPtr.Zero);
        if (context == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            var bytes = new byte[checked(pixels * pixels * 4)];
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = pixels, Height = -pixels,
                    Planes = 1, BitCount = 32, ImageSize = (uint)bytes.Length
                }
            };
            bitmap = CreateDIBSection(context, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(context, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            if (!DrawIconEx(context, 0, 0, icon, pixels, pixels, 0, IntPtr.Zero, 3))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            GdiFlush();
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            var hasAlpha = false;
            for (var i = 3; i < bytes.Length; i += 4) hasAlpha |= bytes[i] != 0;
            if (!hasAlpha)
            {
                // Older Shell resources use an AND mask rather than alpha. Preserve transparent corners in either format.
                var mask = new byte[bytes.Length];
                Marshal.Copy(mask, 0, bits, mask.Length);
                if (!DrawIconEx(context, 0, 0, icon, pixels, pixels, 0, IntPtr.Zero, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                GdiFlush();
                Marshal.Copy(bits, mask, 0, mask.Length);
                for (var i = 0; i < bytes.Length; i += 4)
                {
                    bytes[i + 3] = mask[i] == 0 && mask[i + 1] == 0 && mask[i + 2] == 0 ? (byte)255 : (byte)0;
                    if (bytes[i + 3] == 0) bytes[i] = bytes[i + 1] = bytes[i + 2] = 0;
                }
            }
            return bytes;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(context, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KnownFolderDefinition
    {
        public uint Category;
        public IntPtr Name, Description;
        public Guid Parent;
        public IntPtr RelativePath, ParsingName, Tooltip, LocalizedName, Icon, Security;
        public uint Attributes, DefinitionFlags;
        public Guid FolderType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SystemImageIndex, IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Color;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFolder(IntPtr manager, ref Guid id, out IntPtr folder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFolderDefinition(IntPtr folder, out KnownFolderDefinition definition);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("shlwapi.dll", EntryPoint = "PathParseIconLocationW", CharSet = CharSet.Unicode)]
    private static extern int PathParseIconLocation(StringBuilder path);
    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string path, int index, uint flags, out IntPtr large, IntPtr small, uint sizes);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr context, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr context);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr context, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr context, IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr context);
    [DllImport("gdi32.dll", EntryPoint = "GdiFlush")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
}

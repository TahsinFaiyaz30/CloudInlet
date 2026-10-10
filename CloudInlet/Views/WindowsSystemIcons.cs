using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CloudInlet.Windows;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

/// <summary>Windows-owned artwork for objects with a documented Shell stock icon.</summary>
internal static class WindowsSystemIcons
{
    // These are SHSTOCKICONID values, not version-dependent resource offsets.
    // Do not use SIID_SETTINGS (legacy Control Panel) or SIID_SHIELD (UAC only)
    // for app settings and general security. Those keep their Fluent fallback.
    private static readonly IReadOnlyDictionary<string, uint> StockIds = new Dictionary<string, uint>
    {
        ["folder"] = 3,    // SIID_FOLDER
        ["storage"] = 8,   // SIID_DRIVEFIXED
        ["lock"] = 47,     // SIID_LOCK
        ["music"] = 71,    // SIID_AUDIOFILES
        ["image"] = 72,    // SIID_IMAGEFILES
        ["video"] = 73,    // SIID_VIDEOFILES
        ["warning"] = 78,  // SIID_WARNING
        ["info"] = 79,     // SIID_INFO
        ["desktop"] = 94   // SIID_DESKTOPPC
    };

    [ThreadStatic]
    private static Dictionary<(string Kind, int Pixels), Uri?>? _cache;

    /// <summary>
    /// Returns the installed Windows artwork, including its original colors.
    /// The caller supplies its normal Fluent or contrast-aware fallback on failure.
    /// </summary>
    internal static bool TryCreateSource(string kind, double size, out IconSource source)
    {
        source = null!;
        if (!StockIds.TryGetValue(kind, out var stockId)) return false;
        // Allow 300% display scale while keeping a small, bounded cache.
        var pixels = size <= 24 ? 96 : 128;
        var cache = _cache ??= [];
        var key = (kind, pixels);
        if (!cache.TryGetValue(key, out var uri))
        {
            try { uri = Load(stockId, kind, pixels); }
            catch (Exception error) when (error is COMException or Win32Exception or IOException or
                ArgumentException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                // Artwork must never prevent a control or a page from loading.
                uri = null;
            }
            cache[key] = uri;
        }
        if (uri is null) return false;
        // ImageIconSource does not render reliably in IconSourceElement in WinUI
        // 1.8; BitmapIconSource preserves the color image on that same surface.
        source = new BitmapIconSource { UriSource = uri, ShowAsMonochrome = false };
        return true;
    }

    private static Uri? Load(uint stockId, string kind, int pixels)
    {
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>(), Path = "" };
        // SHGSI_ICONLOCATION = 0: Shell resolves its own resource. No file
        // association, user folder, synced path, or network share is queried.
        if (SHGetStockIconInfo(stockId, 0, ref info) != 0) return null;
        var path = Path.GetFullPath(info.Path);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        if (!path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var version = $"{path}|{info.IconIndex}|{File.GetLastWriteTimeUtc(path).Ticks}|{pixels}|png-v1";
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..20];
        // Only rendered Windows artwork lives here. Credentials, backup state,
        // and cloud payloads never enter this disposable presentation cache.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudInlet", "Cache", "WindowsIcons", stamp);
        var destination = Path.Combine(directory, kind + ".png");
        if (!File.Exists(destination))
        {
            var bytes = ShellFolderIconReader.Read(new FolderAppearance.IconResource(path, info.IconIndex), pixels);
            if (bytes is null) return null;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"{kind}-{Guid.NewGuid():N}.tmp");
            try
            {
                WritePng(temporary, bytes, pixels);
                try { File.Move(temporary, destination, overwrite: false); }
                catch (IOException) when (File.Exists(destination)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new Uri(destination);
    }

    private static void WritePng(string path, byte[] bgra, int pixels)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, pixels);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], pixels);
        header[8] = 8; // Eight bits per channel.
        header[9] = 6; // RGBA.
        WriteChunk(file, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[pixels * 4 + 1]; // PNG filter byte zero: no filter.
            for (var y = 0; y < pixels; y++)
            {
                for (var x = 0; x < pixels; x++)
                {
                    var input = (y * pixels + x) * 4;
                    var output = 1 + x * 4;
                    var alpha = bgra[input + 3];
                    // DrawIconEx gives premultiplied BGRA; PNG stores straight RGBA.
                    row[output] = Straight(bgra[input + 2], alpha);
                    row[output + 1] = Straight(bgra[input + 1], alpha);
                    row[output + 2] = Straight(bgra[input], alpha);
                    row[output + 3] = alpha;
                }
                encoder.Write(row);
            }
        }
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static byte Straight(byte value, byte alpha) => alpha == 0 ? (byte)0 :
        (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var tag = Encoding.ASCII.GetBytes(type);
        stream.Write(tag);
        stream.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in tag) crc = CrcByte(crc, value);
        foreach (var value in data) crc = CrcByte(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        return crc;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SystemImageIndex, IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);
}

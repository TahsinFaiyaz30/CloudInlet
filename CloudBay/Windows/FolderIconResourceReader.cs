using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CloudBay.Windows;

/// <summary>Extracts local icon resources as data, without executing DLL/EXE initialization or Shell handlers.</summary>
internal static class FolderIconResourceReader
{
    internal static SafeIconHandle? Create(FolderAppearance.IconResource resource, int pixels)
    {
        if (pixels is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(pixels));
        // This no-follow, no-recall handle proves residency and prevents replacing or writing
        // the resource while the Windows resource loader opens the same local path.
        using var file = FolderAppearance.OpenResidentFile(resource.Path);
        if (Path.GetExtension(resource.Path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new FileStream(file, FileAccess.Read);
            if (stream.Length > 16 * 1024 * 1024) return null;
            var content = new byte[checked((int)stream.Length)];
            stream.ReadExactly(content);
            return CreateFromIco(content, pixels);
        }
        // DATAFILE_EXCLUSIVE | IMAGE_RESOURCE: no imports, entry points or initialization.
        // An absolute path avoids any DLL search, and the file handle above denies replacement.
        var module = LoadLibraryExW(Path.GetFullPath(resource.Path), IntPtr.Zero, 0x60);
        if (module == IntPtr.Zero) return null;
        try
        {
            IntPtr group;
            if (resource.Index < 0)
            {
                var id = -(long)resource.Index;
                if (id is < 1 or > ushort.MaxValue) return null;
                group = FindResourceW(module, new IntPtr(id), new IntPtr(14));
            }
            else
            {
                if (resource.Index > 4096) return null;
                group = IntPtr.Zero;
                var remaining = resource.Index;
                EnumResourceName callback = (owner, type, name, _) =>
                {
                    if (remaining-- != 0) return true;
                    group = FindResourceW(owner, name, type);
                    return false;
                };
                EnumResourceNamesW(module, new IntPtr(14), callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            var directory = ResourceBytes(module, group, 64 * 1024);
            // LookupIconIdFromDirectoryEx does not validate data. Parse and bound the group
            // ourselves so malformed desktop.ini resources cannot cause an out-of-bounds read.
            if (directory is null || directory.Length < 6 || U16(directory, 0) != 0 || U16(directory, 2) != 1) return null;
            var count = U16(directory, 4);
            if (count is < 1 or > 256 || directory.Length < 6 + count * 14) return null;
            var selected = Select(directory, count, 14, pixels);
            var iconId = U16(directory, 6 + selected * 14 + 12);
            var declaredLength = U32(directory, 6 + selected * 14 + 8);
            var image = ResourceBytes(module, FindResourceW(module, new IntPtr(iconId), new IntPtr(3)), 4 * 1024 * 1024);
            return image is null || image.Length < declaredLength ? null : CreateFromBytes(image, pixels);
        }
        finally { FreeLibrary(module); }
    }

    private static SafeIconHandle? CreateFromIco(byte[] data, int pixels)
    {
        if (data.Length < 6 || U16(data, 0) != 0 || U16(data, 2) != 1) return null;
        var count = U16(data, 4);
        if (count is < 1 or > 256 || data.Length < 6 + count * 16) return null;
        var selected = Select(data, count, 16, pixels);
        var entry = 6 + selected * 16;
        var length = U32(data, entry + 8); var offset = U32(data, entry + 12);
        if (length is < 1 or > 4 * 1024 * 1024 || offset < 6 + count * 16 || (ulong)offset + length > (ulong)data.Length) return null;
        return CreateFromBytes(data.AsSpan((int)offset, (int)length).ToArray(), pixels);
    }

    private static int Select(byte[] data, int count, int stride, int pixels)
    {
        var selected = 0; var best = int.MaxValue;
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + index * stride;
            var width = data[entry] == 0 ? 256 : data[entry];
            var height = data[entry + 1] == 0 ? 256 : data[entry + 1];
            // Prefer an exact image, then downscale a larger image before upscaling a smaller one.
            var score = (width >= pixels && height >= pixels ? 0 : 1024) + Math.Abs(width - pixels) + Math.Abs(height - pixels);
            if (score < best) { best = score; selected = index; }
        }
        return selected;
    }

    private static byte[]? ResourceBytes(IntPtr module, IntPtr resource, uint maximum)
    {
        if (resource == IntPtr.Zero) return null;
        var size = SizeofResource(module, resource);
        if (size == 0 || size > maximum) return null;
        var loaded = LoadResource(module, resource); var pointer = LockResource(loaded);
        if (pointer == IntPtr.Zero) return null;
        var bytes = new byte[checked((int)size)]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return bytes;
    }

    private static SafeIconHandle? CreateFromBytes(byte[] bytes, int pixels)
    {
        var icon = CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, pixels, pixels, 0);
        return icon == IntPtr.Zero ? null : new SafeIconHandle(icon);
    }
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    internal sealed class SafeIconHandle : SafeHandle
    {
        internal SafeIconHandle(IntPtr icon) : base(IntPtr.Zero, true) => SetHandle(icon);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool EnumResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResourceName callback, IntPtr context);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bytes, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}

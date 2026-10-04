using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.Win32.SafeHandles;

namespace CloudBay.Windows;

/// <summary>Preserves Shell folder customization independently of the backed-up file contents.</summary>
public static class FolderAppearance
{
    public sealed record IconResource(string Path, int Index);

    /// <summary>Completes local Shell appearance after a verified copy without reopening network metadata.</summary>
    public static Task PreserveAfterVerifiedCopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // VerifiedTreeCopy already retained desktop.ini, named streams and activation
        // attributes. A UNC original can legitimately be a redirected Windows folder;
        // cosmetic work must neither block its mapping restoration nor contact the share.
        if (IsNetworkPath(source) || IsNetworkPath(destination)) return Task.CompletedTask;
        return PreserveAsync(source, destination, cancellationToken);
    }

    public static Task PreserveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        source = ValidateDirectory(source);
        destination = ValidateDirectory(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        // VerifiedTreeCopy preserves customization attributes throughout the copied tree.
        // A startup repair only needs this selected folder's metadata; never rescan the user's
        // full Documents tree just to make its root icon visible again.
        cancellationToken.ThrowIfCancellationRequested();
        var customization = File.GetAttributes(source) & (FileAttributes.ReadOnly | FileAttributes.System);
        var originalIni = Path.Combine(source, "desktop.ini");
        var copiedIni = Path.Combine(destination, "desktop.ini");
        var preserveIni = File.Exists(originalIni) && File.Exists(copiedIni);
        if (preserveIni)
        {
            // Turning backup off copies from the native sync root. Its desktop.ini is a
            // Cloud Files placeholder even after the verified copy has hydrated its bytes.
            // Only its attributes are needed here; online-only metadata is safe too, and
            // must not be recalled merely to preserve the destination's Shell appearance.
            EnsureRegularFile(originalIni, allowCloudFile: true); EnsureRegularFile(copiedIni, allowCloudFile: true);
        }
        // Reject unsupported metadata before changing the destination's appearance.
        ApplyAttributes(destination, customization);
        if (preserveIni)
        {
            ApplyAttributes(copiedIni, FileAttributes.Hidden | FileAttributes.System |
                (File.GetAttributes(originalIni) & FileAttributes.ReadOnly));
            // The verified tree copy retained the full original INI, including custom names,
            // views and relative icon resources. Do not replace it with a generic template.
            ApplyAttributes(destination, FileAttributes.ReadOnly);
        }
        Notify(destination);
        return Task.CompletedTask;
    }

    /// <summary>Repairs only an owned backup whose current Windows mapping is still unchanged.</summary>
    public static async Task EnsureKnownFolderAsync(BackupFolder folder, CancellationToken cancellationToken = default)
    {
        if (!KnownFolderBackup.GetPath(folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(folder.DestinationPath)) return;
        await RefreshLocalBackupAppearanceAsync(folder.OriginalPath, folder.DestinationPath, GetKnownFolderIcon(folder.Name), cancellationToken);
    }

    internal static async Task RefreshLocalBackupAppearanceAsync(string original, string destination, IconResource? fallback,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The initial verified copy preserved the original customization. Startup only
        // repairs the owned local root and must not probe an unavailable former share.
        if (!IsNetworkPath(original) && Directory.Exists(original))
            await PreserveAsync(original, destination, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureIconAsync(destination, fallback, cancellationToken);
    }

    /// <summary>Reads resident local metadata only. It never recalls an online-only INI or icon.</summary>
    public static IconResource? GetIconResource(string path)
    {
        try
        {
            path = ValidateDirectory(path);
            var ini = Path.Combine(path, "desktop.ini");
            if (!File.Exists(ini)) return null;
            using var handle = OpenResidentFile(ini);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length > 256 * 1024) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var values = ReadShellValues(reader.ReadToEnd());
            string? location = null;
            var index = 0;
            if (values.TryGetValue("IconResource", out var resource))
            {
                var separator = resource.LastIndexOf(',');
                if (separator > 0 && int.TryParse(resource[(separator + 1)..].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed)) { location = resource[..separator]; index = parsed; }
                else location = resource;
            }
            else if (values.TryGetValue("IconFile", out var file))
            {
                location = file;
                if (values.TryGetValue("IconIndex", out var iconIndex)) int.TryParse(iconIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
            }
            if (string.IsNullOrWhiteSpace(location)) return null;
            location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"').TrimStart('@'));
            if (!Path.IsPathFullyQualified(location)) location = Path.GetFullPath(Path.Combine(path, location));
            if (location.StartsWith("\\\\", StringComparison.Ordinal) || !CanReadResidentFile(location)) return null;
            return new(Path.GetFullPath(location), index);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { return null; }
    }

    public static IconResource? GetKnownFolderIcon(string name)
    {
        if (!KnownFolderBackup.FolderIds.TryGetValue(name, out var id)) return null;
        const int changedApartment = unchecked((int)0x80010106);
        var initialized = CoInitializeEx(IntPtr.Zero, 2);
        if (initialized < 0 && initialized != changedApartment) Marshal.ThrowExceptionForHR(initialized);
        IntPtr manager = IntPtr.Zero, folder = IntPtr.Zero;
        var definition = new KnownFolderDefinition();
        try
        {
            var clsid = new Guid("4DF0C730-DF9D-4AE3-9153-AA6B82E9795A"); var iid = new Guid("8BE2D872-86AA-4D47-B776-32CCA40C7018");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out manager));
            var getFolder = Method<GetFolder>(manager, 6);
            Marshal.ThrowExceptionForHR(getFolder(manager, ref id, out folder));
            Marshal.ThrowExceptionForHR(Method<GetFolderDefinition>(folder, 11)(folder, out definition));
            var resource = Marshal.PtrToStringUni(definition.Icon);
            if (string.IsNullOrWhiteSpace(resource)) return null;
            var value = new StringBuilder(Environment.ExpandEnvironmentVariables(resource.TrimStart('@')), 32_768);
            var index = PathParseIconLocation(value);
            return new(value.ToString(), index);
        }
        finally
        {
            Marshal.FreeCoTaskMem(definition.Name); Marshal.FreeCoTaskMem(definition.Description);
            Marshal.FreeCoTaskMem(definition.RelativePath); Marshal.FreeCoTaskMem(definition.ParsingName);
            Marshal.FreeCoTaskMem(definition.Tooltip); Marshal.FreeCoTaskMem(definition.LocalizedName);
            Marshal.FreeCoTaskMem(definition.Icon); Marshal.FreeCoTaskMem(definition.Security);
            if (folder != IntPtr.Zero) Marshal.Release(folder);
            if (manager != IntPtr.Zero) Marshal.Release(manager);
            if (initialized >= 0) CoUninitialize();
        }
    }

    public static async Task EnsureIconAsync(string destination, IconResource? fallback, CancellationToken cancellationToken = default)
    {
        destination = ValidateDirectory(destination);
        var ini = Path.Combine(destination, "desktop.ini");
        if (File.Exists(ini))
        {
            // A custom icon or an online-only INI is authoritative. This repair adjusts only the
            // Shell activation attributes and never overwrites unknown cloud or user metadata.
            EnsureRegularFile(ini, allowCloudFile: true);
            ApplyAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            ApplyAttributes(destination, FileAttributes.ReadOnly);
            Notify(destination);
            return;
        }
        if (fallback is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectory(destination);
        await using (var stream = new FileStream(ini, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true))
        await using (var writer = new StreamWriter(stream, Encoding.Unicode))
        {
            await writer.WriteAsync($"[.ShellClassInfo]\r\nIconResource={fallback.Path},{fallback.Index.ToString(CultureInfo.InvariantCulture)}\r\n".AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        EnsureRegularFile(ini);
        ApplyAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        ApplyAttributes(destination, FileAttributes.ReadOnly);
        Notify(destination);
    }

    private static Dictionary<string, string> ReadShellValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shell = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { shell = line.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!shell || line.StartsWith(';') || line.StartsWith('#')) continue;
            var split = line.IndexOf('=');
            if (split > 0) values[line[..split].Trim()] = line[(split + 1)..].Trim();
        }
        return values;
    }

    private static string ValidateDirectory(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Folder appearance requires a local folder.");
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null) throw new IOException("Folder appearance cannot follow a linked directory.");
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudPath(current.FullName))
                throw new IOException("Folder appearance cannot follow a linked directory.");
        }
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        return path;
    }
    private static bool CanReadResidentFile(string path)
    {
        if (!File.Exists(path)) return false;
        using var handle = OpenResidentFile(path);
        return true;
    }
    private static void EnsureRegularFile(string path, bool allowCloudFile = false)
    {
        ValidateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Folder appearance cannot follow a linked metadata file.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 &&
            (!allowCloudFile || !IsCloudPath(path)))
            throw new IOException("Folder appearance cannot follow a linked metadata file.");
    }
    /// <summary>Holds an immutable, no-recall view of an ordinary or completely resident Cloud Files file.</summary>
    internal static SafeFileHandle OpenResidentFile(string path)
    {
        EnsureRegularFile(path, allowCloudFile: true);
        // GENERIC_READ itself can trigger CFAPI hydration, even with OPEN_NO_RECALL.
        // Prove residency using READ_ATTRIBUTES before requesting any data access.
        using var probe = CreateFileW(path, 0x80, 1, IntPtr.Zero, 3, 0x00300000, IntPtr.Zero);
        if (probe.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        EnsureResident(probe);
        // Deny content writes and replacement while the reader is alive. OPEN_REPARSE_POINT
        // and the held residency probe prevent an icon lookup from requesting missing contents.
        var handle = CreateFileW(path, 0x80000000u, 1, IntPtr.Zero, 3, 0x00300000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            EnsureResident(handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void EnsureResident(SafeFileHandle handle)
    {
            if (!GetFileInformationByHandleEx(handle, 9, out var tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((tag.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                var state = CloudFiles.CloudFilesNative.CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
                if (state == uint.MaxValue || (state & CloudFiles.CloudFilesNative.Placeholder) == 0)
                    throw new IOException("Folder appearance cannot read a linked metadata file.");
                if (!CloudFiles.CloudFilesNative.GetFileInformationByHandle(handle.DangerousGetHandle(), out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // Allocated-byte counts cannot prove residency for sparse files. Query the actual
                // on-disk range on this same handle; fragmented/partial results fail closed.
                if (info.Size != 0 && (CfGetPlaceholderRangeInfo(handle, 1, 0, info.Size, out var range, 16, out var returned) != 0 ||
                    returned != 16 || range.Offset != 0 || range.Length < info.Size))
                    throw new IOException("Folder appearance cannot recall online-only metadata.");
                // Do not let an icon lookup block on VALIDATION_REQUIRED user I/O. The
                // same held no-recall handle must prove its bytes have been acknowledged
                // or locally edited before any data access is requested.
                if (info.Size != 0 && !CloudFiles.CloudFilesNative.HasAvailableCloudRanges(handle, info.Size))
                    throw new IOException("Folder appearance cannot read metadata awaiting cloud validation.");
            }
            else if ((tag.Attributes & (0x1000u | 0x400000u)) != 0)
                throw new IOException("Folder appearance cannot recall offline metadata.");
    }
    private static void ApplyAttributes(string path, FileAttributes additional)
    {
        using var handle = CreateFileW(path, 0x180, 3, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var state = CloudFiles.CloudFilesNative.CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
        if ((tag.Attributes & (uint)FileAttributes.ReparsePoint) != 0 &&
            (state == uint.MaxValue || (state & (CloudFiles.CloudFilesNative.Placeholder | 2)) == 0))
            throw new IOException("Folder appearance cannot change a linked file or directory.");
        var info = new BasicInformation { Attributes = tag.Attributes | (uint)additional };
        if (!SetFileInformationByHandle(handle, 0, ref info, (uint)Marshal.SizeOf<BasicInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static bool IsCloudPath(string path)
    {
        var state = CloudFiles.CloudFilesNative.State(path);
        return state != uint.MaxValue && (state & (CloudFiles.CloudFilesNative.Placeholder | 2)) != 0;
    }
    private static bool IsNetworkPath(string path) => path.StartsWith("\\\\", StringComparison.Ordinal);
    private static void Notify(string path) => SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileRange { public long Offset, Length; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInformation
    { public long CreationTime, AccessTime, WriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct KnownFolderDefinition
    {
        public uint Category; public IntPtr Name, Description; public Guid Parent;
        public IntPtr RelativePath, ParsingName, Tooltip, LocalizedName, Icon, Security;
        public uint Attributes, Flags; public Guid FolderType;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFolder(IntPtr manager, ref Guid id, out IntPtr folder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFolderDefinition(IntPtr folder, out KnownFolderDefinition definition);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    private static SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template) =>
        NativeCreateFileW(WindowsFilePaths.ToExtendedPath(path), access, sharing, security, disposition, flags, template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag tag, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref BasicInformation info, uint size);
    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderRangeInfo(SafeFileHandle handle, uint infoClass, long offset, long length,
        out FileRange range, uint bufferLength, out uint returnedLength);
    [DllImport("shlwapi.dll", EntryPoint = "PathParseIconLocationW", CharSet = CharSet.Unicode)] private static extern int PathParseIconLocation(StringBuilder path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr second);
}

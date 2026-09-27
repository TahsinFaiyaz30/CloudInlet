using System.Runtime.InteropServices;

namespace CloudBay.Core.Folders;

internal static class NativeKnownFolders
{
    private const uint KfDontVerify = 0x00004000;
    private const uint KfDefaultPath = 0x00000400;
    private const uint KfNotParentRelative = 0x00000200;
    private const uint KfCreate = 0x00008000;
    private const uint KfInit = 0x00000800;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcneUpdateDir = 0x00001000;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    private static readonly Guid KnownFolderManagerClsid = new("4DF0C730-DF9D-4AE3-9153-AA6B82E9795A");

    internal static Guid FolderId(KnownFolderKind kind) => kind switch
    {
        KnownFolderKind.Desktop => new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        KnownFolderKind.Documents => new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        KnownFolderKind.Pictures => new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        KnownFolderKind.Videos => new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        KnownFolderKind.Music => new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        KnownFolderKind.Downloads => new("374DE290-123F-4565-9164-39C4925E467B"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string GetCurrentPath(KnownFolderKind kind)
    {
        using var com = new ComInitialization();
        return GetPath(FolderId(kind), KfDontVerify);
    }

    internal static string GetDefaultPath(KnownFolderKind kind)
    {
        using var com = new ComInitialization();
        return GetPath(FolderId(kind), KfDefaultPath | KfNotParentRelative | KfDontVerify);
    }

    internal static void SetPath(KnownFolderKind kind, string path)
    {
        using var com = new ComInitialization();
        Guid id = FolderId(kind);
        int hr = SHSetKnownFolderPath(ref id, 0, IntPtr.Zero, path);
        Marshal.ThrowExceptionForHR(hr);
    }

    internal static void InitializeFolder(KnownFolderKind kind)
    {
        using var com = new ComInitialization();
        _ = GetPath(FolderId(kind), KfCreate | KfInit);
    }

    internal static ShellFolderDefinition GetDefinition(KnownFolderKind kind)
    {
        using var com = new ComInitialization();
        object? managerObject = null;
        IKnownFolder? folder = null;
        try
        {
            Type managerType = Type.GetTypeFromCLSID(KnownFolderManagerClsid, throwOnError: true)!;
            managerObject = Activator.CreateInstance(managerType)
                ?? throw new InvalidOperationException("The Windows Known Folder Manager is unavailable.");
            var manager = (IKnownFolderManager)managerObject;
            Guid id = FolderId(kind);
            Marshal.ThrowExceptionForHR(manager.GetFolder(ref id, out folder));
            if (folder is null) throw new InvalidOperationException("Windows did not return the known folder definition.");
            Marshal.ThrowExceptionForHR(folder.GetFolderDefinition(out var definition));
            try
            {
                return new ShellFolderDefinition(
                    Marshal.PtrToStringUni(definition.LocalizedName),
                    Marshal.PtrToStringUni(definition.Tooltip),
                    Marshal.PtrToStringUni(definition.Icon),
                    definition.FolderType);
            }
            finally
            {
                definition.FreeStrings();
            }
        }
        finally
        {
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (managerObject is not null) Marshal.ReleaseComObject(managerObject);
        }
    }

    internal static void NotifyChanged(string oldPath, string newPath)
    {
        NotifyPath(oldPath);
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) NotifyPath(newPath);
        SHChangeNotify(ShcneAssocChanged, ShcnfFlush, IntPtr.Zero, IntPtr.Zero);
        // Some processes listen for the shell-folder setting broadcast rather than SHChangeNotify.
        _ = SendMessageTimeout(new IntPtr(0xffff), WmSettingChange, IntPtr.Zero,
            "User Shell Folders", SmtoAbortIfHung, 5_000, out _);
    }

    private static void NotifyPath(string path)
    {
        IntPtr pointer = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlush, pointer, IntPtr.Zero);
            SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlush, pointer, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string GetPath(Guid id, uint flags)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(ref id, flags, IntPtr.Zero, out pointer);
            Marshal.ThrowExceptionForHR(hr);
            return Marshal.PtrToStringUni(pointer)
                ?? throw new InvalidOperationException("Windows returned an empty known folder path.");
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }

    private sealed class ComInitialization : IDisposable
    {
        private readonly bool _uninitialize;

        public ComInitialization()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows Known Folder APIs require Windows.");
            int hr = CoInitializeEx(IntPtr.Zero, 0);
            if (hr == RpcEChangedMode) return; // This thread already has an STA COM apartment.
            Marshal.ThrowExceptionForHR(hr);
            _uninitialize = true; // Both S_OK and S_FALSE require a matching CoUninitialize.
        }

        public void Dispose()
        {
            if (_uninitialize) CoUninitialize();
        }
    }

    internal sealed record ShellFolderDefinition(
        string? LocalizedName,
        string? Tooltip,
        string? Icon,
        Guid FolderType);

    [StructLayout(LayoutKind.Sequential)]
    private struct KnownFolderDefinitionNative
    {
        public int Category;
        public IntPtr Name;
        public IntPtr Description;
        public Guid ParentId;
        public IntPtr RelativePath;
        public IntPtr ParsingName;
        public IntPtr Tooltip;
        public IntPtr LocalizedName;
        public IntPtr Icon;
        public IntPtr Security;
        public uint Attributes;
        public uint Flags;
        public Guid FolderType;

        public void FreeStrings()
        {
            Marshal.FreeCoTaskMem(Name);
            Marshal.FreeCoTaskMem(Description);
            Marshal.FreeCoTaskMem(RelativePath);
            Marshal.FreeCoTaskMem(ParsingName);
            Marshal.FreeCoTaskMem(Tooltip);
            Marshal.FreeCoTaskMem(LocalizedName);
            Marshal.FreeCoTaskMem(Icon);
            Marshal.FreeCoTaskMem(Security);
        }
    }

    [ComImport, Guid("8BE2D872-86AA-4D47-B776-32CCA40C7018"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKnownFolderManager
    {
        [PreserveSig] int FolderIdFromCsidl(int csidl, out Guid folderId);
        [PreserveSig] int FolderIdToCsidl(ref Guid folderId, out int csidl);
        [PreserveSig] int GetFolderIds(out IntPtr folderIds, ref uint count);
        [PreserveSig] int GetFolder(ref Guid folderId, [MarshalAs(UnmanagedType.Interface)] out IKnownFolder folder);
    }

    [ComImport, Guid("3AA7AF7E-9B36-420C-A8E3-F77D4674A488"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKnownFolder
    {
        [PreserveSig] int GetId(out Guid folderId);
        [PreserveSig] int GetCategory(out int category);
        [PreserveSig] int GetShellItem(uint flags, ref Guid interfaceId, out IntPtr shellItem);
        [PreserveSig] int GetPath(uint flags, out IntPtr path);
        [PreserveSig] int SetPath(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int GetIdList(uint flags, out IntPtr idList);
        [PreserveSig] int GetFolderType(out Guid folderType);
        [PreserveSig] int GetRedirectionCapabilities(out uint capabilities);
        [PreserveSig] int GetFolderDefinition(out KnownFolderDefinitionNative definition);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHSetKnownFolderPath(
        ref Guid folderId, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] string path);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam,
        [MarshalAs(UnmanagedType.LPWStr)] string lParam,
        uint flags, uint timeoutMilliseconds, out nuint result);
}

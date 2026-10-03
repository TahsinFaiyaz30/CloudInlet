using System.Runtime.InteropServices;

namespace CloudBay.Windows;

/// <summary>Uses the real Known Folder COM interface to honor policy and permission restrictions.</summary>
public static class KnownFolderPolicy
{
    public static string? GetRestriction(Guid id)
    {
        const int changedApartment = unchecked((int)0x80010106);
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0 && initialized != changedApartment) Marshal.ThrowExceptionForHR(initialized);
        IntPtr manager = IntPtr.Zero, folder = IntPtr.Zero;
        try
        {
            var clsid = new Guid("4DF0C730-DF9D-4AE3-9153-AA6B82E9795A");
            var iid = new Guid("8BE2D872-86AA-4D47-B776-32CCA40C7018");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out manager));
            var managerVtable = Marshal.ReadIntPtr(manager);
            var getFolder = Marshal.GetDelegateForFunctionPointer<GetFolder>(Marshal.ReadIntPtr(managerVtable, 6 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getFolder(manager, ref id, out folder));
            var folderVtable = Marshal.ReadIntPtr(folder);
            var getCapabilities = Marshal.GetDelegateForFunctionPointer<GetCapabilities>(Marshal.ReadIntPtr(folderVtable, 10 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getCapabilities(folder, out var capabilities));
            if ((capabilities & 0x300) != 0) return "Windows policy controls this folder's location.";
            if ((capabilities & 0x400) != 0) return "Windows permissions prevent this folder from being redirected.";
            if ((capabilities & 1) == 0) return "Windows does not permit redirection of this folder.";
            return null;
        }
        finally
        {
            if (folder != IntPtr.Zero) Marshal.Release(folder);
            if (manager != IntPtr.Zero) Marshal.Release(manager);
            if (initialized >= 0) CoUninitialize();
        }
    }
    public static void EnsureRedirectable(Guid id)
    { if (GetRestriction(id) is { } restriction) throw new IOException(restriction); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFolder(IntPtr manager, ref Guid id, out IntPtr folder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCapabilities(IntPtr folder, out uint capabilities);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
}

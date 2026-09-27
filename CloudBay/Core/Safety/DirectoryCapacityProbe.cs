using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudBay.Core.Safety;

/// <summary>Queries the volume that actually contains a directory, including mounted folders.</summary>
public static class DirectoryCapacityProbe
{
    public static bool TryGet(string directory, out ulong availableBytes, out ulong totalBytes,
        out string? reason)
    {
        availableBytes = 0;
        totalBytes = 0;
        if (!OperatingSystem.IsWindows())
        {
            reason = "Windows capacity APIs are unavailable.";
            return false;
        }
        if (HasOpaqueReparseAncestor(directory))
        {
            reason = "The mounted cloud provider does not expose its quota through this Windows directory.";
            return false;
        }

        string queryPath = Path.EndsInDirectorySeparator(directory)
            ? directory : directory + Path.DirectorySeparatorChar;
        if (GetDiskFreeSpaceEx(queryPath, out availableBytes, out totalBytes, out _))
        {
            reason = null;
            return true;
        }

        reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    public static string GetFileSystemOrEmpty(string directory)
    {
        if (!OperatingSystem.IsWindows() || HasOpaqueReparseAncestor(directory)) return string.Empty;
        var volumePath = new StringBuilder(1024);
        if (!GetVolumePathName(directory, volumePath, volumePath.Capacity)) return string.Empty;
        var fileSystem = new StringBuilder(64);
        return GetVolumeInformation(volumePath.ToString(), null, 0,
            out _, out _, out _, fileSystem, fileSystem.Capacity)
            ? fileSystem.ToString() : string.Empty;
    }

    private static bool HasOpaqueReparseAncestor(string directory)
    {
        DirectoryInfo? current = new(Path.GetFullPath(directory));
        while (current is not null)
        {
            try
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 &&
                    current.LinkTarget is null)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return true;
            }
            current = current.Parent;
        }
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory,
        out ulong freeBytesAvailableToCaller, out ulong totalBytes,
        out ulong totalFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer,
        int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}

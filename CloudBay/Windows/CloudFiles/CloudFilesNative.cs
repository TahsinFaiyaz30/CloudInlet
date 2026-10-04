using System.ComponentModel;
using System.Runtime.InteropServices;
using CloudBay.Core.Sync;
using Microsoft.Win32.SafeHandles;

namespace CloudBay.Windows.CloudFiles;

// Layouts match Windows SDK 10.0.26100.0 cfapi.h on x64. Keep the unions explicit:
// CF_OPERATION_PARAMETERS.ParamSize is the offset + active union member, not sizeof(union).
internal static class CloudFilesNative
{
    internal const uint Placeholder = 1, InSync = 8, Partial = 16, PartiallyOnDisk = 32;
    internal const uint UpdateVerifyInSync = 1, UpdateMarkInSync = 2, UpdateDehydrate = 4;
    internal const int CloudUnsuccessful = unchecked((int)0xC000CF12);
    internal const int CloudCancelled = unchecked((int)0xC000CF1B);
    internal const int CloudNetworkUnavailable = unchecked((int)0xC000CF11);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void Callback(in CallbackInfo info, in CallbackParameters parameters);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CallbackRegistration { internal uint Type; internal IntPtr Function; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CallbackInfo
    {
        internal uint StructSize;
        internal long ConnectionKey;
        internal IntPtr CallbackContext, VolumeGuidName, VolumeDosName;
        internal uint VolumeSerialNumber;
        internal long SyncRootFileId;
        internal IntPtr SyncRootIdentity;
        internal uint SyncRootIdentityLength;
        internal long FileId, FileSize;
        internal IntPtr FileIdentity;
        internal uint FileIdentityLength;
        internal IntPtr NormalizedPath;
        internal long TransferKey;
        internal byte PriorityHint;
        internal IntPtr CorrelationVector, ProcessInfo;
        internal long RequestKey;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct CallbackParameters
    {
        [FieldOffset(0)] internal uint ParamSize;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(16)] internal long RequiredOffset;
        [FieldOffset(24)] internal long RequiredLength;
        [FieldOffset(32)] internal long OptionalOffset;
        [FieldOffset(40)] internal long OptionalLength;
        [FieldOffset(48)] internal long LastDehydrationTime;
        [FieldOffset(56)] internal uint LastDehydrationReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OperationInfo
    {
        internal uint StructSize, Type;
        internal long ConnectionKey, TransferKey;
        internal IntPtr CorrelationVector, SyncStatus;
        internal long RequestKey;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct TransferParameters
    {
        [FieldOffset(0)] internal uint ParamSize;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(12)] internal int CompletionStatus;
        [FieldOffset(16)] internal IntPtr Buffer;
        [FieldOffset(24)] internal long Offset;
        [FieldOffset(32)] internal long Length;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct RestartParameters
    {
        [FieldOffset(0)] internal uint ParamSize;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(16)] internal IntPtr Metadata;
        [FieldOffset(24)] internal IntPtr Identity;
        [FieldOffset(32)] internal uint IdentityLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct RetrieveParameters
    {
        [FieldOffset(0)] internal uint ParamSize;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(16)] internal IntPtr Buffer;
        [FieldOffset(24)] internal long Offset;
        [FieldOffset(32)] internal long Length;
        [FieldOffset(40)] internal long ReturnedLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct AckParameters
    {
        [FieldOffset(0)] internal uint ParamSize;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(12)] internal int CompletionStatus;
        [FieldOffset(16)] internal long Offset;
        [FieldOffset(24)] internal long Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicInfo
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Metadata { internal BasicInfo BasicInfo; internal long FileSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CreateInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string RelativeFileName;
        internal Metadata FsMetadata;
        internal IntPtr FileIdentity;
        internal uint FileIdentityLength, Flags;
        internal int Result;
        internal long CreateUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StandardInfo
    {
        internal long OnDiskDataSize, ValidatedDataSize, ModifiedDataSize, PropertiesSize;
        internal uint PinState, InSyncState;
        internal long FileId, SyncRootFileId;
        internal uint FileIdentityLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AttributeTag { internal uint Attributes, Tag; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileRange { internal long Offset, Length; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleInfo
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        internal uint VolumeSerialNumber, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        internal readonly long Size => ((long)SizeHigh << 32) | SizeLow;
        internal readonly long Modified => ((long)(uint)WriteTime.dwHighDateTime << 32) | (uint)WriteTime.dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReadOverlapped
    {
        internal IntPtr Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal IntPtr EventHandle;
    }

    [DllImport("cldapi.dll", EntryPoint = "CfConnectSyncRoot", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeConnectSyncRoot(string path, [In] CallbackRegistration[] callbacks, IntPtr context, uint flags, out long connection);
    internal static int CfConnectSyncRoot(string path, CallbackRegistration[] callbacks, IntPtr context, uint flags, out long connection) =>
        NativeConnectSyncRoot(WindowsFilePaths.ToExtendedPath(path), callbacks, context, flags, out connection);
    [DllImport("cldapi.dll")] internal static extern int CfDisconnectSyncRoot(long connection);
    [DllImport("cldapi.dll")] internal static extern int CfExecute(in OperationInfo info, in TransferParameters parameters);
    [DllImport("cldapi.dll", EntryPoint = "CfExecute")]
    internal static extern int CfRestartHydration(in OperationInfo info, in RestartParameters parameters);
    [DllImport("cldapi.dll", EntryPoint = "CfExecute")]
    internal static extern int CfRetrieveData(in OperationInfo info, ref RetrieveParameters parameters);
    [DllImport("cldapi.dll", EntryPoint = "CfExecute")]
    internal static extern int CfAckData(in OperationInfo info, in AckParameters parameters);
    [DllImport("cldapi.dll")] internal static extern int CfReportProviderProgress(long connection, long transfer, long total, long completed);
    [DllImport("cldapi.dll", EntryPoint = "CfCreatePlaceholders", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeCreatePlaceholders(string baseDirectory, [In, Out] CreateInfo[] placeholders, uint count, uint flags, out uint processed);
    internal static int CfCreatePlaceholders(string baseDirectory, CreateInfo[] placeholders, uint count, uint flags, out uint processed) =>
        NativeCreatePlaceholders(WindowsFilePaths.ToExtendedPath(baseDirectory), placeholders, count, flags, out processed);
    [DllImport("cldapi.dll", EntryPoint = "CfOpenFileWithOplock", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeOpenFileWithOplock(string path, uint flags, out ProtectedHandle handle);
    internal static int CfOpenFileWithOplock(string path, uint flags, out ProtectedHandle handle) =>
        NativeOpenFileWithOplock(WindowsFilePaths.ToExtendedPath(path), flags, out handle);
    [DllImport("cldapi.dll")] internal static extern void CfCloseHandle(IntPtr handle);
    [DllImport("cldapi.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool CfReferenceProtectedHandle(ProtectedHandle handle);
    [DllImport("cldapi.dll")] internal static extern void CfReleaseProtectedHandle(ProtectedHandle handle);
    [DllImport("cldapi.dll")] internal static extern IntPtr CfGetWin32HandleFromProtectedHandle(ProtectedHandle handle);
    [DllImport("cldapi.dll")] internal static extern int CfConvertToPlaceholder(ProtectedHandle handle, byte[] identity, uint identityLength, uint flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfUpdatePlaceholder(ProtectedHandle handle, in Metadata metadata, byte[] identity, uint identityLength, IntPtr ranges, uint count, uint flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll", EntryPoint = "CfUpdatePlaceholder")]
    internal static extern int CfUpdateIdentity(ProtectedHandle handle, IntPtr metadata, byte[] identity, uint identityLength, IntPtr ranges, uint count, uint flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfSetInSyncState(ProtectedHandle handle, uint state, uint flags, IntPtr usn);
    [DllImport("cldapi.dll")] internal static extern int CfSetPinState(ProtectedHandle handle, uint state, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll", EntryPoint = "CfSetPinState")]
    internal static extern int CfSetPinState(SafeFileHandle handle, uint state, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfHydratePlaceholder(ProtectedHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll", EntryPoint = "CfHydratePlaceholder")]
    internal static extern int CfHydratePlaceholder(SafeFileHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfDehydratePlaceholder(ProtectedHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfRevertPlaceholder(ProtectedHandle handle, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfGetPlaceholderInfo(ProtectedHandle handle, uint infoClass, IntPtr buffer, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll", EntryPoint = "CfGetPlaceholderInfo")]
    internal static extern int CfGetPlaceholderInfo(SafeFileHandle handle, uint infoClass, IntPtr buffer, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll")]
    internal static extern int CfGetPlaceholderRangeInfo(SafeFileHandle handle, uint infoClass, long offset, long length,
        out FileRange range, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll")]
    internal static extern int CfGetPlaceholderRangeInfoForHydration(long connection, long transfer, long fileId,
        uint infoClass, long offset, long length, out FileRange range, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll", EntryPoint = "CfGetSyncRootInfoByPath", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeGetSyncRootInfoByPath(string path, uint infoClass, out long rootFileId, uint bufferLength, out uint returnedLength);
    internal static int CfGetSyncRootInfoByPath(string path, uint infoClass, out long rootFileId, uint bufferLength, out uint returnedLength) =>
        NativeGetSyncRootInfoByPath(WindowsFilePaths.ToExtendedPath(path), infoClass, out rootFileId, bufferLength, out returnedLength);
    [DllImport("cldapi.dll")] internal static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    // Apply the long-path namespace at the native boundary only. Containment checks,
    // persisted identities and visible paths continue to use their normal Windows paths.
    internal static SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template) =>
        NativeCreateFile(WindowsFilePaths.ToExtendedPath(path), access, share, security, disposition, flags, template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(IntPtr handle, IntPtr buffer, uint length, IntPtr bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    internal static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    internal static ProtectedHandle Open(string path, bool exclusive = false, bool writable = false)
    {
        Check(CfOpenFileWithOplock(path, (exclusive ? 1u : 0u) | (writable ? 2u : 0u), out var handle));
        return handle;
    }

    internal static uint State(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
    }

    internal static bool IsFullyResident(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var state = CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
        if (state == uint.MaxValue) return false;
        if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (info.Size == 0) return true;
        // Disconnect can report NO_STATES even though cldflt retains a Cloud Files range
        // map. Consult that map before treating a missing Placeholder flag as a regular
        // file; otherwise an interrupted prefix looks fully available after shutdown.
        var result = CfGetPlaceholderRangeInfo(handle, 1, 0, info.Size, out var range, 16, out var returned);
        if (result >= 0 || result == unchecked((int)0x800700EA))
            return result == 0 && returned == 16 && range.Offset == 0 && range.Length >= info.Size &&
                HasAvailableCloudRanges(handle, info.Size);
        return (state & Placeholder) == 0 && (tag.Attributes & 0x1000u) == 0;
    }

    internal static bool HasAvailableCloudRanges(SafeFileHandle handle, long size)
    {
        if (size < 0) return false;
        // ONDISK alone includes bytes still awaiting VALIDATION_REQUIRED acknowledgment.
        // Availability needs coverage by validated cloud data or authoritative local edits.
        // Query one range at a time and fail closed for extremely fragmented metadata rather
        // than allocate in proportion to a file's length or block directory scans indefinitely.
        long cursor = 0;
        for (var queries = 0; queries < 128 && cursor < size; queries++)
        {
            var available = AvailableRangeEnd(handle, 2, cursor, size);
            if (available == size) return true; // The common clean, validated file needs one query.
            available = Math.Max(available, AvailableRangeEnd(handle, 3, cursor, size));
            if (available <= cursor) return false;
            cursor = available;
        }
        return cursor >= size;
    }

    private static long AvailableRangeEnd(SafeFileHandle handle, uint kind, long cursor, long size)
    {
        var result = CfGetPlaceholderRangeInfo(handle, kind, cursor, size - cursor, out var range, 16, out var returned);
        if ((result < 0 && result != unchecked((int)0x800700EA)) || returned != 16 ||
            range.Offset < 0 || range.Offset > cursor || range.Length <= 0)
            return cursor;
        // Cap before adding, so malformed/oversized native ranges cannot overflow Int64.
        return range.Length >= size - range.Offset ? size : Math.Max(cursor, range.Offset + range.Length);
    }

    internal sealed class ProtectedHandle : SafeHandle
    {
        public ProtectedHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() { CfCloseHandle(handle); return true; }
    }
}

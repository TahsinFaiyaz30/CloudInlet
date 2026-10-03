using System.ComponentModel;
using System.Runtime.InteropServices;
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

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int CfConnectSyncRoot(string path, [In] CallbackRegistration[] callbacks, IntPtr context, uint flags, out long connection);
    [DllImport("cldapi.dll")] internal static extern int CfDisconnectSyncRoot(long connection);
    [DllImport("cldapi.dll")] internal static extern int CfExecute(in OperationInfo info, in TransferParameters parameters);
    [DllImport("cldapi.dll")] internal static extern int CfReportProviderProgress(long connection, long transfer, long total, long completed);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int CfCreatePlaceholders(string baseDirectory, [In, Out] CreateInfo[] placeholders, uint count, uint flags, out uint processed);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] internal static extern int CfOpenFileWithOplock(string path, uint flags, out ProtectedHandle handle);
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
    [DllImport("cldapi.dll")] internal static extern int CfHydratePlaceholder(ProtectedHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfDehydratePlaceholder(ProtectedHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfRevertPlaceholder(ProtectedHandle handle, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] internal static extern int CfGetPlaceholderInfo(ProtectedHandle handle, uint infoClass, IntPtr buffer, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll", EntryPoint = "CfGetPlaceholderInfo")]
    internal static extern int CfGetPlaceholderInfo(SafeFileHandle handle, uint infoClass, IntPtr buffer, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int CfGetSyncRootInfoByPath(string path, uint infoClass, out long rootFileId, uint bufferLength, out uint returnedLength);
    [DllImport("cldapi.dll")] internal static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
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

    internal sealed class ProtectedHandle : SafeHandle
    {
        public ProtectedHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() { CfCloseHandle(handle); return true; }
    }
}

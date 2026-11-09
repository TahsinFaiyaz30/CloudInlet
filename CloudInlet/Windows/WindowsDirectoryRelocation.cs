using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudInlet.Core.Migration;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Windows;

/// <summary>
/// One recoverable same-volume directory rename, not a complete application-state/backup migration.
/// The owning adapter MUST quiesce all users (including SQLite and update workers), validate the
/// directory's ownership, and journal/commit its coupled settings and Windows/cloud registrations.
/// Do not expose this operation directly for a registered sync root or an installed application.
/// Renaming the original directory object preserves its contents, streams and security descriptors;
/// it neither copies payloads nor removes conflicting destinations. The session owns completion policy.
/// </summary>
internal sealed class WindowsDirectoryRelocation : IMigrationOperation, IDisposable
{
    private sealed record Identity(ulong Volume, ulong Low, ulong High, long Created);
    private sealed record Receipt(int Schema, string Item, string Fingerprint, string Source, string Destination,
        Identity Root, Identity SourceParent, Identity DestinationParent);
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly byte[] Entropy = "CloudInlet.DirectoryRelocation.v1"u8.ToArray();
    private readonly Receipt _receipt;
    private readonly FileStream _lease;
    private readonly FileStream _record;
    private readonly Guards _recordGuards;
    private bool _disposed;
    private int _busy;
    public string ItemId => _receipt.Item;
    public string ReviewedPlanFingerprint => _receipt.Fingerprint;
    public bool CanRevert => !_disposed;

    private WindowsDirectoryRelocation(Receipt receipt, FileStream lease, FileStream record, Guards guards)
    { _receipt = receipt; _lease = lease; _record = record; _recordGuards = guards; }

    internal static WindowsDirectoryRelocation Create(MigrationPlan plan, string itemId, string receiptPath) => OpenCore(plan, itemId, receiptPath, create: true);
    internal static WindowsDirectoryRelocation Resume(MigrationPlan plan, string itemId, string receiptPath) => OpenCore(plan, itemId, receiptPath, create: false);

    private static WindowsDirectoryRelocation OpenCore(MigrationPlan plan, string itemId, string receiptPath, bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Directory migration requires Windows.");
        var reviewed = MigrationPlan.Create(plan.Items);
        if (reviewed.Fingerprint != plan.Fingerprint) throw new InvalidDataException("The migration review changed.");
        var item = reviewed.Items.Single(item => item.Id == itemId);
        if (item.BlockedReason is not null) throw new InvalidOperationException("The reviewed change is unavailable.");
        var source = LocalPath(item.CurrentLocation);
        var destination = LocalPath(item.ProposedLocation);
        var record = LocalPath(receiptPath);
        if (Inside(source, destination) || Inside(destination, source) || Inside(source, record) || Inside(destination, record))
            throw new IOException("Source, destination and recovery record must be separate locations.");
        var recordGuards = new Guards(Path.GetDirectoryName(record)!);
        FileStream? lease = null;
        FileStream? heldRecord = null;
        try
        {
            // Open the lease itself without following a link, then retain exclusive ownership.
            var leaseHandle = Open(record + ".lock", 0xC0000000, 0, directory: false, create: true)!;
            try { lease = new FileStream(leaseHandle, FileAccess.ReadWrite); }
            catch { leaseHandle.Dispose(); throw; }
            using var parents = new Guards(Path.GetDirectoryName(source)!, Path.GetDirectoryName(destination)!);
            Receipt receipt;
            if (create)
            {
                using var root = Open(source, 0x10080, 3, directory: true)!;
                using var conflict = Open(destination, 0x80, 3, directory: true, allowMissing: true);
                if (conflict is not null) throw new IOException("The destination already exists. No folders were merged or replaced.");
                var identity = ReadIdentity(root);
                var fromParent = parents.Identity(Path.GetDirectoryName(source)!);
                var toParent = parents.Identity(Path.GetDirectoryName(destination)!);
                if (identity.Volume != toParent.Volume) throw new IOException("Directory migration requires the same local volume.");
                receipt = new(1, itemId, plan.Fingerprint, source, destination, identity, fromParent, toParent);
                var plaintext = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
                byte[] encrypted;
                try { encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                // The final receipt is immutable and published only after its bytes reach disk.
                // A interrupted temporary file contains only protected metadata, never user payload.
                var temporary = record + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(encrypted); output.Flush(flushToDisk: true); }
                File.Move(temporary, record, overwrite: false);
            }
            else
            {
                using var recordHandle = Open(record, 0x80000000, 1, directory: false)!;
                using var input = new FileStream(recordHandle, FileAccess.Read);
                if (input.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("The directory recovery record is invalid.");
                var encrypted = new byte[(int)input.Length];
                input.ReadExactly(encrypted);
                var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                try { receipt = JsonSerializer.Deserialize<Receipt>(plaintext, JsonOptions) ?? throw new InvalidDataException("The directory recovery record is empty."); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                if (receipt.Schema != 1 || receipt.Item != itemId || receipt.Fingerprint != plan.Fingerprint ||
                    receipt.Source != source || receipt.Destination != destination || receipt.Root is null ||
                    receipt.SourceParent is null || receipt.DestinationParent is null)
                    throw new InvalidDataException("The recovery record does not match this reviewed directory move.");
            }
            var heldHandle = Open(record, 0x80000000, 1, directory: false)!;
            try { heldRecord = new FileStream(heldHandle, FileAccess.Read); }
            catch { heldHandle.Dispose(); throw; }
            // Keep the durable receipt unchanged until this adapter releases its lease.
            // Re-read under that handle so a replacement between publication/read and acquisition cannot be trusted.
            if (heldRecord.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("The directory recovery record is invalid.");
            var heldBytes = new byte[(int)heldRecord.Length];
            heldRecord.ReadExactly(heldBytes);
            var heldPlaintext = ProtectedData.Unprotect(heldBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                if (JsonSerializer.Deserialize<Receipt>(heldPlaintext, JsonOptions) != receipt)
                    throw new InvalidDataException("The directory recovery record changed while it was being opened.");
            }
            finally { CryptographicOperations.ZeroMemory(heldPlaintext); }
            var operation = new WindowsDirectoryRelocation(receipt, lease, heldRecord, recordGuards);
            operation.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            return operation;
        }
        catch { heldRecord?.Dispose(); lease?.Dispose(); recordGuards.Dispose(); throw; }
    }

    public Task CheckAsync(CancellationToken cancellationToken) => Execute(cancellationToken, () => WithLocation((_, _) => { }));

    public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken) =>
        Execute(cancellationToken, () => WithLocation((handle, atDestination) =>
        {
            if (!atDestination)
            {
                progress.Report(new(ItemId, "Moving folder", "Renaming the original directory; existing destination folders are never replaced."));
                cancellationToken.ThrowIfCancellationRequested();
                Rename(handle, _receipt.Destination);
            }
            progress.Report(new(ItemId, "Folder moved", "The original directory is at the reviewed destination. Coupled settings still require verification.", 1, 1, "folders"));
        }, needsRename: true));

    public Task VerifyAsync(CancellationToken cancellationToken) => Execute(cancellationToken, () => WithLocation((_, atDestination) =>
    { if (!atDestination) throw new IOException("The original directory is still at its previous location."); }));

    public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken) =>
        Execute(cancellationToken, () => WithLocation((handle, atDestination) =>
        {
            if (atDestination)
            {
                progress.Report(new(ItemId, "Restoring folder", "Restoring only this original directory; a conflicting previous location is never replaced."));
                cancellationToken.ThrowIfCancellationRequested();
                Rename(handle, _receipt.Source);
            }
            progress.Report(new(ItemId, "Folder restored", "The original directory is back at its previous location.", 1, 1, "folders"));
        }, needsRename: true));

    public Task VerifyRevertedAsync(CancellationToken cancellationToken) => Execute(cancellationToken, () => WithLocation((_, atDestination) =>
    { if (atDestination) throw new IOException("The original directory has not yet been restored."); }));

    private void WithLocation(Action<SafeFileHandle, bool> action, bool needsRename = false)
    {
        using var parents = new Guards(Path.GetDirectoryName(_receipt.Source)!, Path.GetDirectoryName(_receipt.Destination)!);
        if (parents.Identity(Path.GetDirectoryName(_receipt.Source)!) != _receipt.SourceParent ||
            parents.Identity(Path.GetDirectoryName(_receipt.Destination)!) != _receipt.DestinationParent)
            throw new IOException("A parent directory changed after review. The folders were retained for recovery.");
        // Completed routing needs identity verification, not delete permission on
        // the profile currently guarded by another selected migration adapter.
        var access = needsRename ? 0x10080u : 0x81u;
        using var source = Open(_receipt.Source, access, 3, directory: true, allowMissing: true);
        using var destination = Open(_receipt.Destination, access, 3, directory: true, allowMissing: true);
        if ((source is null) == (destination is null))
            throw new IOException("The original directory cannot be identified at exactly one reviewed location. No folders were replaced.");
        var handle = source ?? destination!;
        if (ReadIdentity(handle) != _receipt.Root)
            throw new IOException("A different directory occupies the reviewed location. Its contents were retained.");
        action(handle, source is null);
    }

    private Task Execute(CancellationToken token, Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A directory migration action is already running.");
        try { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Stop the directory migration before closing its recovery record.");
        try { _disposed = true; _record.Dispose(); _lease.Dispose(); _recordGuards.Dispose(); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static bool Inside(string root, string path) => string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':' || path.IndexOf(':', 2) >= 0 || path.IndexOf('"') >= 0)
            throw new ArgumentException("A fully qualified local directory path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) ||
            full.Split(Path.DirectorySeparatorChar).Skip(1).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("A volume root or ambiguous directory name cannot be migrated.");
        return full;
    }

    // Read/list-data access makes Windows enforce sharing; attribute-only handles do not pin a name.
    // All ancestors stay open without delete sharing while inspecting/renaming their child.
    // OPEN_REPARSE_POINT makes the final component inspectable without following a link.
    private sealed class Guards : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
        internal Guards(params string[] directories)
        {
            try
            {
                foreach (var directory in directories)
                {
                    var ancestors = new Stack<string>();
                    for (var path = directory; path is not null; path = Path.GetDirectoryName(path)) ancestors.Push(path);
                    while (ancestors.TryPop(out var path))
                        if (!_handles.ContainsKey(path)) _handles.Add(path, Open(path, 0x81, 3, directory: true)!);
                }
            }
            catch { Dispose(); throw; }
        }
        internal Identity Identity(string directory) => ReadIdentity(_handles[directory]);
        public void Dispose() { foreach (var handle in _handles.Values.Reverse()) handle.Dispose(); _handles.Clear(); }
    }

    private static SafeFileHandle? Open(string path, uint access, uint share, bool directory, bool create = false, bool allowMissing = false)
    {
        var handle = CreateFile("\\\\?\\" + path, access, share, IntPtr.Zero, create ? 4u : 3u, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (allowMissing && error is 2 or 3) return null;
            throw NativeError(error);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw NativeError(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
                throw new IOException("Directory migration cannot follow a link or use a file in place of a folder.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static Identity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var basic) ||
            !GetFileInformationByHandleEx(handle, 18, out var info, (uint)Marshal.SizeOf<FileIdentity>()))
            throw NativeError(Marshal.GetLastWin32Error());
        return new(info.Volume, info.Low, info.High, ((long)basic.Creation.dwHighDateTime << 32) | (uint)basic.Creation.dwLowDateTime);
    }

    private static void Rename(SafeFileHandle handle, string destination)
    {
        var name = Encoding.Unicode.GetBytes("\\\\?\\" + destination);
        var offset = Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.Length)).ToInt32() + sizeof(uint);
        var size = checked(offset + name.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new RenameHeader { Replace = 0, Root = IntPtr.Zero, Length = (uint)name.Length }, buffer, false);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length);
            Marshal.WriteInt16(buffer, offset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) throw NativeError(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IOException NativeError(int error) => error is 32 or 33
        ? new MigrationTransientException("Another process is using this folder. Waiting for it to release the folder.")
        : new IOException("Windows could not safely access or rename the folder: " + new Win32Exception(error).Message, new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)] private struct RenameHeader { public uint Replace; public IntPtr Root; public uint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BasicInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr information, uint size);
}

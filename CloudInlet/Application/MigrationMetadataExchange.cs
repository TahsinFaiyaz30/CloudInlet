using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Application;

/// <summary>A recoverable compare-and-exchange for small migration metadata. The
/// original object is held against writes/replacement, renamed by handle to an
/// owned backup, then the flushed replacement is published without overwrite.
/// Callers must hold the state/recovery ancestor guards and stop normal writers.</summary>
internal static class MigrationMetadataExchange
{
    private sealed record Identity(uint Volume, uint High, uint Low);
    private sealed record Intent(int Schema, string Id, string Path, Identity Before, Identity After,
        string BeforeHash, string AfterHash);
    private static readonly byte[] Entropy = "CloudInlet.MigrationMetadataExchange.v1"u8.ToArray();

    internal static void Replace(string path, byte[] expected, byte[] replacement, string records,
        Action<string>? boundary = null)
    {
        path = Path.GetFullPath(path); records = Path.GetFullPath(records);
        ClientMigrationReview.EnsureUnlinked(path); ClientMigrationReview.EnsureUnlinked(records);
        Directory.CreateDirectory(records);
        using var guards = new DirectoryGuards(Path.GetDirectoryName(path)!, records);
        using var original = Open(path);
        if (Hash(original) != Digest(expected)) throw new IOException("A saved record changed before migration could commit it. Its contents were retained.");
        if (expected.AsSpan().SequenceEqual(replacement)) return;
        var id = Guid.NewGuid().ToString("N");
        var temporary = Temporary(path, id);
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(replacement); output.Flush(true); }
        // Preserve explicit access permissions rather than inheriting a different ACL.
        new FileInfo(temporary).SetAccessControl(new FileInfo(path).GetAccessControl());
        using var prepared = Open(temporary);
        if (Hash(prepared) != Digest(replacement)) throw new IOException("The prepared metadata changed before its recovery record was saved.");
        var intent = new Intent(1, id, path, ReadIdentity(original), ReadIdentity(prepared), Digest(expected), Digest(replacement));
        var journal = Path.Combine(records, id + ".pending.dpapi");
        Publish(journal, intent);
        boundary?.Invoke("prepared");
        Rename(original, Backup(path, id));
        boundary?.Invoke("original-retained");
        Rename(prepared, path);
        boundary?.Invoke("replacement-published");
        Complete(journal, intent);
    }

    internal static void Recover(string stateDirectory, string records)
    {
        ClientMigrationReview.EnsureUnlinked(records);
        if (!Directory.Exists(records)) return;
        using var recordGuards = new DirectoryGuards(records);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory)) + Path.DirectorySeparatorChar;
        foreach (var journal in Directory.EnumerateFiles(records, "*.pending.dpapi").Order(StringComparer.Ordinal))
        {
            ClientMigrationReview.EnsureUnlinked(journal);
            using var input = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 65536) throw new InvalidDataException("A metadata recovery record has an invalid size.");
            var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            Intent intent;
            try { intent = JsonSerializer.Deserialize<Intent>(plain) ?? throw new InvalidDataException("The metadata recovery record is empty."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (intent.Schema != 1 || !Guid.TryParseExact(intent.Id, "N", out _) ||
                Path.GetFileName(journal) != intent.Id + ".pending.dpapi" ||
                !Path.IsPathFullyQualified(intent.Path) || !Path.GetFullPath(intent.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                intent.Before is null || intent.After is null || !IsDigest(intent.BeforeHash) || !IsDigest(intent.AfterHash))
                throw new InvalidDataException("A metadata recovery record does not belong to this application profile.");
            var backup = Backup(intent.Path, intent.Id); var temporary = Temporary(intent.Path, intent.Id);
            using var dataGuards = new DirectoryGuards(Path.GetDirectoryName(intent.Path)!);
            foreach (var path in new[] { intent.Path, backup, temporary }) ClientMigrationReview.EnsureUnlinked(path);
            // Nothing else may replace these objects while their identities are checked.
            using var previous = File.Exists(backup) ? Open(backup) : Open(intent.Path);
            Require(previous, intent.Before, intent.BeforeHash);
            if (!File.Exists(backup)) Rename(previous, backup);
            if (File.Exists(intent.Path))
            {
                using var current = Open(intent.Path);
                Require(current, intent.After, intent.AfterHash);
            }
            else
            {
                using var prepared = Open(temporary);
                Require(prepared, intent.After, intent.AfterHash);
                Rename(prepared, intent.Path);
            }
            // Close the read lock before archiving its exact protected intent.
            input.Dispose();
            Complete(journal, intent);
        }
    }

    private static void Complete(string journal, Intent intent)
    {
        // Both metadata versions remain in the same directory for recovery. Neither
        // a later migration item nor cleanup removes/replaces this verified original.
        File.Move(journal, Path.Combine(Path.GetDirectoryName(journal)!, intent.Id + ".complete.dpapi"));
    }
    private static string Backup(string path, string id) => path + ".migration-" + id + ".bak";
    private static string Temporary(string path, string id) => path + ".migration-" + id + ".tmp";
    private static bool IsDigest(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Hash(FileStream file)
    {
        if (file.Length > 64 * 1024 * 1024) throw new IOException("The saved migration record exceeds its supported metadata size.");
        file.Position = 0;
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static void Require(FileStream file, Identity identity, string hash)
    {
        if (ReadIdentity(file) != identity || Hash(file) != hash)
            throw new IOException("A metadata file changed outside migration. Its current contents and the saved original were retained for recovery.");
    }
    private static void Publish(string path, Intent intent)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(intent);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(protectedBytes); output.Flush(true); }
        File.Move(temporary, path);
    }
    private static FileStream Open(string path)
    {
        ClientMigrationReview.EnsureUnlinked(path);
        // READ_DATA | READ_ATTRIBUTES | DELETE, share READ only. Rename this exact
        // opened object; pathname rechecks followed by ReplaceFile are not CAS.
        var handle = CreateFileW(NativePath(path), 0x10081, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("The metadata file is busy or cannot be opened safely.", new Win32Exception(error)); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x410) != 0) throw new IOException("Metadata cannot be a directory or linked file.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }
    private sealed class DirectoryGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal DirectoryGuards(params string[] paths)
        {
            try
            {
                foreach (var path in paths)
                    for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                    {
                        ClientMigrationReview.EnsureUnlinked(current);
                        var handle = CreateFileW(NativePath(current), 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
                        _handles.Add(handle);
                    }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in _handles) handle.Dispose(); }
    }
    private static Identity ReadIdentity(FileStream file)
    {
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // NTFS name tunneling may restore the prior path's creation timestamp
        // when publishing the replacement. The file ID remains the same object.
        return new(info.Volume, info.High, info.Low);
    }
    private static string NativePath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    private static void Rename(FileStream file, string destination)
    {
        var name = Encoding.Unicode.GetBytes(@"\\?\" + destination);
        var offset = Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.Length)).ToInt32() + sizeof(uint);
        var size = checked(offset + name.Length + sizeof(char)); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new RenameHeader { Replace = 0, Root = IntPtr.Zero, Length = (uint)name.Length }, buffer, false);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length); Marshal.WriteInt16(buffer, offset + name.Length, 0);
            if (!SetFileInformationByHandle(file.SafeFileHandle, 3, buffer, (uint)size))
                throw new IOException("Windows could not commit the saved record without replacing another file. The original is retained for recovery.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RenameHeader { public uint Replace; public IntPtr Root; public uint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, High, Low;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, uint size);
}

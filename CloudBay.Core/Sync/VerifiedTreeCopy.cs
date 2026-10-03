using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CloudBay.Core.Sync;

/// <summary>Verified, restartable folder copy which preserves colliding destination content.</summary>
public static class VerifiedTreeCopy
{
    public static async Task CopyAsync(string source, string destination, CancellationToken ct = default)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Backup folders must be separate, with neither containing the other.");
        ValidateDirectoryPath(source);
        ValidateDirectoryPath(destination);
        var snapshot = Snapshot(source, ct);
        // Inspect all affected destination paths before the first write. In particular, an existing
        // junction below the destination must not redirect a verified copy into unrelated data.
        foreach (var directory in snapshot.Directories)
        {
            ct.ThrowIfCancellationRequested();
            ValidateDirectoryPath(Path.Combine(destination, directory));
        }
        foreach (var relative in snapshot.Files.Keys)
        {
            ct.ThrowIfCancellationRequested();
            ValidateFilePath(Path.Combine(destination, relative));
        }
        CreateDirectory(destination);
        foreach (var directory in snapshot.Directories)
            CreateDirectory(Path.Combine(destination, directory));
        foreach (var (relative, fingerprint) in snapshot.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(source, relative);
            var target = Path.Combine(destination, relative);
            CreateDirectory(Path.GetDirectoryName(target)!);
            ValidateFilePath(path);
            ValidateFilePath(target);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (Fingerprint.Read(path) != fingerprint) throw new IOException("A source file changed during backup. Close apps using this folder and try again.");
            var sourceHash = await SHA256.HashDataAsync(input, ct);
            input.Position = 0;
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".cloudbay-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                ValidateFilePath(temporary);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true))
                {
                    await input.CopyToAsync(output, ct);
                    await output.FlushAsync(ct); output.Flush(true); output.Position = 0;
                    var copiedHash = await SHA256.HashDataAsync(output, ct);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash)) throw new IOException("Backup checksum verification failed.");
                }
                ValidateFilePath(temporary);
                File.SetLastWriteTimeUtc(temporary, fingerprint.ModifiedUtc);
                ValidateFilePath(target);
                if (File.Exists(target))
                {
                    byte[] destinationHash;
                    await using (var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                        destinationHash = await SHA256.HashDataAsync(existing, ct);
                    if (CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                    {
                        PreserveAttributes(target, fingerprint.Attributes);
                        continue;
                    }
                    var preserved = Path.Combine(Path.GetDirectoryName(target)!,
                        $"{Path.GetFileNameWithoutExtension(target)} (backup conflict {Guid.NewGuid().ToString("N")[..8]}){Path.GetExtension(target)}");
                    // Atomic move preserves even a late edit to the destination, then installs the verified source.
                    ValidateFilePath(target);
                    ValidateFilePath(preserved);
                    File.Move(target, preserved);
                }
                ValidateFilePath(temporary);
                ValidateFilePath(target);
                File.Move(temporary, target, overwrite: false);
                PreserveAttributes(target, fingerprint.Attributes);
            }
            finally
            {
                if (File.Exists(temporary)) { ValidateFilePath(temporary); File.Delete(temporary); }
            }
        }
        // Refuse Windows redirection if any file appeared, disappeared, or changed during the copy.
        var final = Snapshot(source, ct);
        if (!snapshot.Directories.SetEquals(final.Directories) || snapshot.Files.Count != final.Files.Count ||
            snapshot.Files.Any(pair => !final.Files.TryGetValue(pair.Key, out var value) || value != pair.Value) ||
            snapshot.DirectoryAttributes.Any(pair => !final.DirectoryAttributes.TryGetValue(pair.Key, out var value) || value != pair.Value) ||
            snapshot.CompatibilityJunctions.Count != final.CompatibilityJunctions.Count ||
            snapshot.CompatibilityJunctions.Any(pair => !final.CompatibilityJunctions.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new IOException("The source folder changed during backup. Original files were retained; close apps using this folder and try again.");
        foreach (var (relative, attributes) in snapshot.DirectoryAttributes)
            PreserveAttributes(Path.Combine(destination, relative), attributes);
    }

    public static string GetFingerprint(string path, CancellationToken ct = default)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var snapshot = Snapshot(path, ct);
        var text = string.Join('\n', snapshot.DirectoryAttributes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"D:{p.Key}:{(uint)p.Value}")
            .Concat(snapshot.Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"F:{p.Key}:{p.Value.Size}:{p.Value.ModifiedUtc.Ticks}:{(uint)p.Value.Attributes}"))
            .Concat(snapshot.CompatibilityJunctions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"J:{p.Key}:{p.Value}")));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static TreeSnapshot Snapshot(string source, CancellationToken ct)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryAttributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        var compatibilityJunctions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            ValidateDirectoryPath(current);
            directoryAttributes[Path.GetRelativePath(source, current) == "." ? "" : Path.GetRelativePath(source, current)] = AppearanceAttributes(current);
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                ct.ThrowIfCancellationRequested();
                // Windows installs protected compatibility junctions (including localized names)
                // inside Documents. Inspect the link itself, never its target, before traversal.
                // Ordinary junctions and symbolic links remain an error, as do linked roots,
                // ancestors and destination paths. Cloud Files reparse directories are retained.
                var compatibilityFingerprint = ReadCompatibilityJunction(directory);
                if (compatibilityFingerprint is not null)
                {
                    compatibilityJunctions.Add(Path.GetRelativePath(source, directory), compatibilityFingerprint);
                    continue;
                }
                ValidateDirectoryPath(directory);
                directories.Add(Path.GetRelativePath(source, directory)); pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                ValidateFilePath(file);
                files.Add(Path.GetRelativePath(source, file), Fingerprint.Read(file));
            }
        }
        return new(files, directories, directoryAttributes, compatibilityJunctions);
    }

    private static string? ReadCompatibilityJunction(string path)
    {
        const FileAttributes required = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System;
        if (!OperatingSystem.IsWindows() || (File.GetAttributes(path) & required) != required) return null;
        return ReadWindowsCompatibilityJunction(path);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsCompatibilityJunction(string path)
    {
        // READ_CONTROL | FILE_READ_ATTRIBUTES; OPEN_EXISTING; BACKUP_SEMANTICS |
        // OPEN_REPARSE_POINT. No directory-data access or target hydration is requested.
        // Omitting delete sharing pins the link while all of its metadata is inspected.
        using var handle = CreateFileW(path, 0x00020080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var information, 8))
            throw LinkInspectionError(Marshal.GetLastWin32Error());
        const uint required = (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System);
        const uint mountPointTag = 0xA0000003;
        if ((information.Attributes & required) != required || information.Tag != mountPointTag) return null;

        // Microsoft identifies compatibility junctions by these attributes AND a DACL denying
        // Everyone directory read/list access. Hidden/system flags alone are not an exception.
        if (GetKernelObjectSecurity(handle, 4, null, 0, out var descriptorLength) ||
            Marshal.GetLastWin32Error() != 122 || descriptorLength == 0 || descriptorLength > 1024 * 1024)
            throw new IOException("Windows could not inspect a protected folder link safely.");
        var descriptor = new byte[descriptorLength];
        if (!GetKernelObjectSecurity(handle, 4, descriptor, descriptorLength, out _))
            throw LinkInspectionError(Marshal.GetLastWin32Error());
        var acl = new RawSecurityDescriptor(descriptor, 0).DiscretionaryAcl;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var deniesEnumeration = acl is not null && acl.OfType<CommonAce>().Any(ace =>
            ace.AceQualifier == AceQualifier.AccessDenied && !ace.IsCallback &&
            (ace.AceFlags & AceFlags.InheritOnly) == 0 && (ace.AccessMask & 1) != 0 &&
            ace.SecurityIdentifier.Equals(everyone));
        if (!deniesEnumeration) return null;

        // Retain the junction's exact target and ACL in the source snapshot. A changed or
        // replaced skipped link makes the final validation fail rather than silently pass.
        var reparseData = new byte[16 * 1024];
        if (!DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, reparseData, (uint)reparseData.Length, out var length, IntPtr.Zero))
            throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (length < 8 || length > reparseData.Length || BitConverter.ToUInt32(reparseData) != mountPointTag)
            throw new IOException("A protected folder link changed during backup. Try again.");
        return $"{information.Attributes:X8}:{Convert.ToHexString(SHA256.HashData(descriptor))}:" +
            Convert.ToHexString(SHA256.HashData(reparseData.AsSpan(0, (int)length)));
    }

    private static IOException LinkInspectionError(int error) =>
        new("Windows could not inspect a protected folder link safely.", new Win32Exception(error));

    private static FileAttributes AppearanceAttributes(string path) => File.GetAttributes(path) &
        (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

    private static void PreserveAttributes(string path, FileAttributes appearance)
    {
        if (appearance == 0) return;
        ValidateFilePath(path);
        if (!OperatingSystem.IsWindows()) { File.SetAttributes(path, File.GetAttributes(path) | appearance); return; }
        // Set metadata on the opened object, never on a symbolic-link target substituted at the
        // pathname. Omitting delete sharing retains the destination while the update completes.
        using var handle = CreateFileW(path, 0x180, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var information, 8)) throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (information.Tag is 0xA0000003 or 0xA000000C) throw new IOException("Backup cannot write through a linked destination.");
        var metadata = new BasicInformation { Attributes = information.Attributes | (uint)appearance };
        if (!SetFileInformationByHandle(handle, 0, ref metadata, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LinkInspectionError(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation { public long Created, Accessed, Modified, Changed; public uint Attributes; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref BasicInformation info, uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, IntPtr input, uint inputLength,
        byte[] output, uint outputLength, out uint returned, IntPtr overlapped);

    private static void ValidateDirectoryPath(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            // Cloud Files directories can be reparse points without being links. Retain them;
            // reject actual junctions/symbolic links, including ancestors of a not-yet-created path.
            if (directory.LinkTarget is not null) throw new IOException("Backup cannot follow linked directories.");
    }

    private static void ValidateFilePath(string path)
    {
        ValidateDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Backup cannot follow file links.");
    }

    private static void CreateDirectory(string path)
    {
        ValidateDirectoryPath(path);
        Directory.CreateDirectory(path);
        ValidateDirectoryPath(path);
    }
    private sealed record TreeSnapshot(Dictionary<string, Fingerprint> Files, HashSet<string> Directories,
        Dictionary<string, FileAttributes> DirectoryAttributes,
        Dictionary<string, string> CompatibilityJunctions);
    private sealed record Fingerprint(long Size, DateTime ModifiedUtc, FileAttributes Attributes)
    { public static Fingerprint Read(string path) { var file = new FileInfo(path); return new(file.Length, file.LastWriteTimeUtc, AppearanceAttributes(path)); } }
}

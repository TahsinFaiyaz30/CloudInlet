using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Core.Sync;

/// <summary>Verified, restartable folder copy which preserves colliding destination content.</summary>
public static partial class VerifiedTreeCopy
{
    /// <summary>Creates a reviewed destination without reading or importing its former source.</summary>
    public static void EnsureDestinationDirectory(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CreateDirectory(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }

    public static async Task CopyAsync(string source, string destination, CancellationToken ct = default, IProgress<string>? progress = null)
    { await CopyVerifiedAsync(source, destination, ct, progress); }

    /// <summary>Returns the actual source snapshot whose bytes passed final copy verification.</summary>
    public static async Task<string> CopyVerifiedAsync(string source, string destination, CancellationToken ct = default, IProgress<string>? progress = null)
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
        var copiedHashes = new Dictionary<string, FileHashes>(StringComparer.OrdinalIgnoreCase);
        var directoryHashes = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
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
            {
            var path = Path.Combine(source, relative);
            var target = Path.Combine(destination, relative);
            CreateDirectory(Path.GetDirectoryName(target)!);
            ValidateFilePath(path);
            ValidateFilePath(target);
            using var sourceGuard = OpenMetadataGuard(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (!Fingerprint.Read(path).SameContentMetadata(fingerprint)) throw SourceChanged();
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
                    var namedHashes = await CopyNamedStreamsAsync(path, temporary, fingerprint.Streams, ct);
                    copiedHashes.Add(relative, new(sourceHash, namedHashes));
                }
                ValidateFilePath(temporary);
                File.SetLastWriteTimeUtc(temporary, fingerprint.ModifiedUtc);
                ValidateFilePath(target);
                if (File.Exists(target))
                {
                    bool identical;
                    using var targetGuard = OpenMetadataGuard(target);
                    await using (var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                        identical = await MatchesHashesAsync(target, existing, copiedHashes[relative], fingerprint.Streams, ct);
                    if (identical)
                    {
                        PreserveAttributes(target, fingerprint.Attributes);
                        sourceGuard.Dispose(); await input.DisposeAsync(); targetGuard.Dispose();
                        progress?.Report(relative);
                        continue;
                    }
                    var preserved = Path.Combine(Path.GetDirectoryName(target)!,
                        PathRules.ConflictFileName(Path.GetFileName(target), $" (backup conflict {Guid.NewGuid().ToString("N")[..8]})"));
                    // Atomic move preserves even a late edit to the destination, then installs the verified source.
                    ValidateFilePath(target);
                    ValidateFilePath(preserved);
                    targetGuard.Dispose(); // Release our own no-delete guard before the atomic preservation.
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
            progress?.Report(relative);
        }
        // A directory can have named NTFS data streams too. Do not replace an existing
        // destination stream whose bytes differ: its folder metadata must remain intact.
        foreach (var (relative, metadata) in snapshot.DirectoryMetadata)
        {
            var path = Path.Combine(source, relative);
            var target = Path.Combine(destination, relative);
            using var sourceGuard = OpenMetadataGuard(path);
            using var targetGuard = OpenMetadataGuard(target);
            directoryHashes.Add(relative, await CopyNamedStreamsAsync(path, target, metadata.Streams, ct, preserveExisting: true));
        }
        // Refuse Windows redirection if any file appeared, disappeared, or changed during the copy.
        var final = Snapshot(source, ct);
        if (!snapshot.Directories.SetEquals(final.Directories) || snapshot.Files.Count != final.Files.Count ||
            snapshot.DirectoryAttributes.Any(pair => !final.DirectoryAttributes.TryGetValue(pair.Key, out var value) || value != pair.Value) ||
            snapshot.CompatibilityJunctions.Count != final.CompatibilityJunctions.Count ||
            snapshot.CompatibilityJunctions.Any(pair => !final.CompatibilityJunctions.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw SourceChanged();
        foreach (var (relative, initial) in snapshot.Files)
        {
            if (!final.Files.TryGetValue(relative, out var current) || !initial.SameContentMetadata(current)) throw SourceChanged();
            if (initial.SameSnapshot(current)) continue;
            // Hydrating another Windows cloud provider may change cache metadata. Rehash
            // only these candidates, so that identical hydrated bytes remain importable
            // while a same-size edit with a restored modification time cannot pass.
            var path = Path.Combine(source, relative);
            using var guard = OpenMetadataGuard(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (!await MatchesHashesAsync(path, input, copiedHashes[relative], initial.Streams, ct) ||
                !Fingerprint.Read(path).SameSnapshot(current)) throw SourceChanged();
        }
        foreach (var (relative, initial) in snapshot.DirectoryMetadata)
        {
            if (!final.DirectoryMetadata.TryGetValue(relative, out var current) || !initial.SameContentMetadata(current)) throw SourceChanged();
            if (initial.ChangeTime == current.ChangeTime || initial.Streams.Count == 0) continue;
            var path = Path.Combine(source, relative);
            using var guard = OpenMetadataGuard(path);
            if (!await MatchesNamedHashesAsync(path, directoryHashes[relative], initial.Streams, ct) ||
                !DirectoryFingerprint.Read(path).SameSnapshot(current)) throw SourceChanged();
        }
        foreach (var (relative, attributes) in snapshot.DirectoryAttributes)
            PreserveAttributes(Path.Combine(destination, relative), attributes);
        return FingerprintSnapshot(final);
    }

    /// <summary>Rejects saves after the verified snapshot, before a caller changes a Windows folder mapping.</summary>
    public static void EnsureUnchanged(string source, string verifiedFingerprint, CancellationToken ct = default)
    {
        if (verifiedFingerprint is not { Length: 64 } || !verifiedFingerprint.All(Uri.IsHexDigit) ||
            !verifiedFingerprint.Equals(GetFingerprint(source, ct), StringComparison.Ordinal))
            throw SourceChanged();
    }

    public static string GetFingerprint(string path, CancellationToken ct = default)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return FingerprintSnapshot(Snapshot(path, ct));
    }

    /// <summary>Inspects source metadata without reading or hydrating file contents.</summary>
    public static TreeCopyInspection Inspect(string source, CancellationToken ct = default)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var snapshot = Snapshot(source, ct);
        var bytes = checked(snapshot.Files.Values.Sum(file => checked(file.Size + file.Streams.Sum(stream => stream.Size))) +
            snapshot.DirectoryMetadata.Values.Sum(directory => directory.Streams.Sum(stream => stream.Size)));
        return new(FingerprintSnapshot(snapshot), snapshot.Files.Count, bytes,
            snapshot.Files.Values.Any(file => file.RequiresHydration));
    }

    private static string FingerprintSnapshot(TreeSnapshot snapshot)
    {
        var text = string.Join('\n', snapshot.DirectoryAttributes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"D:{p.Key}:{(uint)p.Value}")
            .Concat(snapshot.Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => System.Text.Json.JsonSerializer.Serialize(new { Type = "F", Name = p.Key, Metadata = p.Value })))
            .Concat(snapshot.DirectoryMetadata.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => System.Text.Json.JsonSerializer.Serialize(new { Type = "S", Name = p.Key, Metadata = p.Value })))
            .Concat(snapshot.CompatibilityJunctions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"J:{p.Key}:{p.Value}")));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static IOException SourceChanged() => new("The source folder changed during backup. Original files were retained; close apps using this folder and try again.");

    private static async Task<IReadOnlyDictionary<string, byte[]>> CopyNamedStreamsAsync(string source, string target,
        IReadOnlyList<NamedStream> streams, CancellationToken ct, bool preserveExisting = false)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!streams.SequenceEqual(ReadNamedStreams(source))) throw SourceChanged();
        var existing = preserveExisting ? ReadNamedStreams(target).ToDictionary(stream => stream.Name, StringComparer.Ordinal) : null;
        foreach (var stream in streams)
        {
            ct.ThrowIfCancellationRequested();
            await using var input = OpenNamedStream(source, stream, FileMode.Open, FileAccess.Read);
            if (input.Length != stream.Size) throw SourceChanged();
            var hash = await SHA256.HashDataAsync(input, ct);
            hashes.Add(stream.Name, hash);
            input.Position = 0;
            if (existing?.ContainsKey(stream.Name) == true)
            {
                await using var retained = OpenNamedStream(target, stream, FileMode.Open, FileAccess.Read);
                var retainedHash = await SHA256.HashDataAsync(retained, ct);
                if (retained.Length != stream.Size || !CryptographicOperations.FixedTimeEquals(hash, retainedHash))
                    throw new IOException("Existing destination folder metadata differs from the source. Neither metadata stream was overwritten; original files were retained.");
                continue;
            }
            await using var output = OpenNamedStream(target, stream, FileMode.CreateNew, FileAccess.ReadWrite);
            await input.CopyToAsync(output, 64 * 1024, ct);
            await output.FlushAsync(ct); output.Flush(true); output.Position = 0;
            var outputHash = await SHA256.HashDataAsync(output, ct);
            if (output.Length != stream.Size || !CryptographicOperations.FixedTimeEquals(hash, outputHash))
                throw new IOException("Backup metadata stream checksum verification failed.");
        }
        return hashes;
    }

    private static async Task<bool> MatchesHashesAsync(string path, Stream unnamed, FileHashes expected,
        IReadOnlyList<NamedStream> streams, CancellationToken ct)
    {
        var unnamedHash = await SHA256.HashDataAsync(unnamed, ct);
        if (!CryptographicOperations.FixedTimeEquals(expected.Unnamed, unnamedHash)) return false;
        return await MatchesNamedHashesAsync(path, expected.Named, streams, ct);
    }

    private static async Task<bool> MatchesNamedHashesAsync(string path, IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyList<NamedStream> streams, CancellationToken ct)
    {
        if (!streams.SequenceEqual(ReadNamedStreams(path))) return false;
        foreach (var stream in streams)
        {
            await using var input = OpenNamedStream(path, stream, FileMode.Open, FileAccess.Read);
            if (input.Length != stream.Size || !expected.TryGetValue(stream.Name, out var hash)) return false;
            var inputHash = await SHA256.HashDataAsync(input, ct);
            if (!CryptographicOperations.FixedTimeEquals(hash, inputHash)) return false;
        }
        return true;
    }

    private static FileStream OpenNamedStream(string path, NamedStream stream, FileMode mode, FileAccess access)
    {
        ValidateStreamName(stream.Name);
        // The caller holds a no-delete metadata guard on the base file/directory; the
        // named stream cannot be redirected through a substituted file or symbolic link.
        return new FileStream(WindowsFilePaths.ToExtendedPath(path) + stream.Name, mode, access,
            access == FileAccess.Read ? FileShare.Read : FileShare.None, 64 * 1024, true);
    }

    private static IReadOnlyList<NamedStream> ReadNamedStreams(string path)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<NamedStream>();
        var result = new List<NamedStream>();
        var search = FindFirstStreamW(WindowsFilePaths.ToExtendedPath(path), 0, out var stream, 0);
        if (search == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 38 or 87) return result; // Empty directory or filesystem without named streams.
            throw new IOException("Windows could not inspect backup metadata streams.", new Win32Exception(error));
        }
        try
        {
            do
            {
                if (stream.Name == "::$DATA") continue;
                ValidateStreamName(stream.Name);
                if (stream.Size < 0 || result.Count >= 1024)
                    throw new IOException("A file contains unsupported metadata stream information. Original files were retained.");
                result.Add(new(stream.Name, stream.Size));
            } while (FindNextStreamW(search, out stream));
            var error = Marshal.GetLastWin32Error();
            if (error != 38) throw new IOException("Windows could not finish inspecting backup metadata streams.", new Win32Exception(error));
        }
        finally { FindClose(search); }
        return result.OrderBy(stream => stream.Name, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateStreamName(string name)
    {
        if (name is null || !name.StartsWith(':') || !name.EndsWith(":$DATA", StringComparison.Ordinal) || name.Length is < 8 or > 262)
            throw new IOException("A file contains an unsupported metadata stream name. Original files were retained.");
        var component = name[1..^6];
        if (component.Length is < 1 or > 255 || component.IndexOfAny(['\\', '/', ':', '\0']) >= 0 || component.Any(char.IsControl))
            throw new IOException("A file contains an unsafe metadata stream name. Original files were retained.");
    }

    private static SafeFileHandle OpenMetadataGuard(string path)
    {
        ValidateFilePath(path);
        if (!OperatingSystem.IsWindows()) return new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        // Open the object itself without data access or recall. No delete sharing pins it
        // while its unnamed/named streams are opened and copied through this pathname.
        var handle = CreateFileW(WindowsFilePaths.ToExtendedPath(path), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw LinkInspectionError(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag information, 8)) throw LinkInspectionError(Marshal.GetLastWin32Error());
            if (information.Tag is 0xA0000003 or 0xA000000C) throw new IOException("Backup cannot follow a linked source or destination.");
            ValidateCopyAttributes((FileAttributes)information.Attributes);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static NativeMetadata ReadNativeMetadata(string path)
    {
        if (!OperatingSystem.IsWindows()) return new(0, 0, 0, 0, (uint)File.GetAttributes(path));
        using var handle = OpenMetadataGuard(path);
        if (!GetFileInformationByHandleEx(handle, 0, out BasicInformation basic, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 18, out FileIdentification identity, (uint)Marshal.SizeOf<FileIdentification>()))
        {
            var error = Marshal.GetLastWin32Error();
            // Some mounted providers expose the older file identity API but not FILE_ID_INFO.
            // Both inspect the same no-follow, no-delete handle; never substitute path metadata.
            if (error is not (50 or 87) || !GetFileInformationByHandle(handle, out var legacy))
                throw new IOException("The source provider cannot expose a stable file identity for verified copying. Download this folder into an ordinary local folder and review that copy; source files were retained.",
                    new Win32Exception(error));
            identity = new() { Volume = legacy.Volume, Low = (ulong)legacy.IndexHigh << 32 | legacy.IndexLow, High = 0 };
        }
        return new(identity.Volume, identity.Low, identity.High, basic.Changed, basic.Attributes);
    }

    /// <summary>Rejects source protection which a native cloud copy cannot preserve safely.</summary>
    public static void ValidateCopyAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Encrypted) != 0)
            throw new IOException("Windows EFS-encrypted files and folders cannot be copied into native Files On-Demand safely. Their encrypted originals were retained; choose an unencrypted personal folder.");
    }

    private static TreeSnapshot Snapshot(string source, CancellationToken ct)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryAttributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        var directoryMetadata = new Dictionary<string, DirectoryFingerprint>(StringComparer.OrdinalIgnoreCase);
        var compatibilityJunctions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            ValidateDirectoryPath(current);
            var directoryRelative = Path.GetRelativePath(source, current) == "." ? "" : Path.GetRelativePath(source, current);
            var metadata = DirectoryFingerprint.Read(current);
            directoryAttributes[directoryRelative] = metadata.Attributes;
            directoryMetadata.Add(directoryRelative, metadata);
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
        return new(files, directories, directoryAttributes, compatibilityJunctions, directoryMetadata);
    }

    private static string? ReadCompatibilityJunction(string path)
    {
        const FileAttributes required = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System;
        if (!OperatingSystem.IsWindows() || (File.GetAttributes(path) & required) != required) return null;
        return ReadWindowsCompatibilityJunction(path);
    }

    /// <summary>Recognizes Windows' protected compatibility aliases without following their targets.</summary>
    public static bool IsProtectedCompatibilityJunction(string path) => ReadCompatibilityJunction(path) is not null;

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsCompatibilityJunction(string path)
    {
        // READ_CONTROL | FILE_READ_ATTRIBUTES; OPEN_EXISTING; BACKUP_SEMANTICS |
        // OPEN_REPARSE_POINT. No directory-data access or target hydration is requested.
        // Omitting delete sharing pins the link while all of its metadata is inspected.
        using var handle = CreateFileW(WindowsFilePaths.ToExtendedPath(path), 0x00020080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag information, 8))
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
        using var handle = CreateFileW(WindowsFilePaths.ToExtendedPath(path), 0x180, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag information, 8)) throw LinkInspectionError(Marshal.GetLastWin32Error());
        if (information.Tag is 0xA0000003 or 0xA000000C) throw new IOException("Backup cannot write through a linked destination.");
        var metadata = new BasicInformation { Attributes = information.Attributes | (uint)appearance };
        if (!SetFileInformationByHandle(handle, 0, ref metadata, (uint)Marshal.SizeOf<BasicInformation>()))
            throw LinkInspectionError(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation { public long Created, Accessed, Modified, Changed; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentification { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyFileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamInformation
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out BasicInformation info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentification info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LegacyFileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string path, uint infoLevel, out StreamInformation information, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr search, out StreamInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr search);
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
        {
            if (directory.LinkTarget is not null) throw new IOException("Backup cannot follow linked directories.");
            if (directory.Exists) ValidateCopyAttributes(directory.Attributes);
        }
    }

    private static void ValidateFilePath(string path)
    {
        ValidateDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Backup cannot follow file links.");
        if (File.Exists(path)) ValidateCopyAttributes(File.GetAttributes(path));
    }

    private static void CreateDirectory(string path)
    {
        ValidateDirectoryPath(path);
        Directory.CreateDirectory(path);
        ValidateDirectoryPath(path);
    }
    private sealed record TreeSnapshot(Dictionary<string, Fingerprint> Files, HashSet<string> Directories,
        Dictionary<string, FileAttributes> DirectoryAttributes,
        Dictionary<string, string> CompatibilityJunctions, Dictionary<string, DirectoryFingerprint> DirectoryMetadata);
    private sealed record NamedStream(string Name, long Size);
    private sealed record FileHashes(byte[] Unnamed, IReadOnlyDictionary<string, byte[]> Named);
    private sealed record NativeMetadata(ulong Volume, ulong Low, ulong High, long ChangeTime, uint AllAttributes)
    {
        public bool SameIdentity(NativeMetadata other) => Volume == other.Volume && Low == other.Low && High == other.High;
    }
    private sealed record Fingerprint(long Size, DateTime ModifiedUtc, FileAttributes Attributes,
        NativeMetadata Native, IReadOnlyList<NamedStream> Streams, bool RequiresHydration)
    {
        public bool SameContentMetadata(Fingerprint other) => Size == other.Size && ModifiedUtc == other.ModifiedUtc &&
            Attributes == other.Attributes && Native.SameIdentity(other.Native) && Streams.SequenceEqual(other.Streams);
        public bool SameSnapshot(Fingerprint other) => SameContentMetadata(other) && Native.ChangeTime == other.Native.ChangeTime;
        public static Fingerprint Read(string path)
        {
            using var guard = OpenMetadataGuard(path);
            var file = new FileInfo(path); var native = ReadNativeMetadata(path);
            return new(file.Length, file.LastWriteTimeUtc, AppearanceAttributes(path), native, ReadNamedStreams(path),
                (native.AllAttributes & ((uint)FileAttributes.Offline | 0x00400000 | 0x00040000)) != 0);
        }
    }
    private sealed record DirectoryFingerprint(FileAttributes Attributes, NativeMetadata Native, IReadOnlyList<NamedStream> Streams)
    {
        public long ChangeTime => Native.ChangeTime;
        public bool SameContentMetadata(DirectoryFingerprint other) => Attributes == other.Attributes &&
            Native.SameIdentity(other.Native) && Streams.SequenceEqual(other.Streams);
        public bool SameSnapshot(DirectoryFingerprint other) => SameContentMetadata(other) && ChangeTime == other.ChangeTime;
        public static DirectoryFingerprint Read(string path)
        {
            using var guard = OpenMetadataGuard(path);
            return new(AppearanceAttributes(path), ReadNativeMetadata(path), ReadNamedStreams(path));
        }
    }
}

public sealed record TreeCopyInspection(string Fingerprint, long FileCount, long TotalBytes, bool HasOnlineOnlyFiles);

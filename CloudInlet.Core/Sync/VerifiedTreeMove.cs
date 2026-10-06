using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Core.Sync;

/// <summary>Removes only exact, verified originals after the caller commits its Windows mapping.</summary>
public static class VerifiedTreeMove
{
    public static Task<BackupTransferOutcome> RemoveCopiedSourcesAsync(string source, string destination,
        string verifiedFingerprint, long expectedFileCount, CancellationToken ct = default) =>
        VerifiedTreeCopy.RemoveCopiedSourcesAsync(source, destination, verifiedFingerprint, expectedFileCount, ct);
}

public static partial class VerifiedTreeCopy
{
    internal static async Task<BackupTransferOutcome> RemoveCopiedSourcesAsync(string source, string destination,
        string verifiedFingerprint, long expectedFileCount, CancellationToken ct)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        TreeSnapshot snapshot;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new IOException("Verified source removal requires Windows file handles.");
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Source and destination must be separate folders.");
            snapshot = Snapshot(source, ct);
            if (!FingerprintSnapshot(snapshot).Equals(verifiedFingerprint, StringComparison.Ordinal)) throw SourceChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        { return Retained(0, Math.Max(0, expectedFileCount)); }

        long removed = 0, retained = 0;
        foreach (var (relative, expected) in snapshot.Files)
        {
            if (ct.IsCancellationRequested)
                return Retained(removed, retained + snapshot.Files.Count - removed - retained);
            try
            {
                await RemoveExactCopiedFileAsync(Path.Combine(source, relative), Path.Combine(destination, relative), expected, ct);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            { retained++; }
        }
        // Keep directories and Windows compatibility aliases. Their metadata can contain
        // independent data streams, and new files can arrive while source cleanup runs.
        // Never recurse through or delete these remaining containers during a move.
        return retained == 0 ? new(removed, 0, null) : Retained(removed, retained);
    }

    private static BackupTransferOutcome Retained(long removed, long retained) => new(removed, retained,
        "The Windows folder location was changed and verified copies were kept. " +
        (retained <= 0 ? "Original files were retained" : retained == 1 ? "One original file was retained" : $"{retained:N0} original files were retained") +
        " because files changed, were in use, could not be removed, or the move was interrupted. Review the original folder before removing anything.");

    private static async Task RemoveExactCopiedFileAsync(string source, string destination, Fingerprint expected, CancellationToken ct)
    {
        var guards = new List<SafeFileHandle>();
        var streamGuards = new List<FileStream>();
        try
        {
            PinDirectoryAncestors(source, guards);
            PinDirectoryAncestors(destination, guards);
            ValidateFilePath(destination);
            guards.Add(OpenMetadataGuard(destination));
            // DELETE access is requested up front; there is no pathname-based delete after
            // hashing. No write/delete sharing pins this exact source object until removal.
            using var removal = CreateFileW(WindowsFilePaths.ToExtendedPath(source), 0x80010080, 1,
                IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (removal.IsInvalid) throw RemovalError(Marshal.GetLastWin32Error());
            var before = ReadRemovalMetadata(removal);
            if (!expected.Native.SameIdentity(before.Native) || before.Native.ChangeTime != expected.Native.ChangeTime ||
                before.ModifiedUtc != expected.ModifiedUtc || before.Appearance != expected.Attributes)
                throw SourceChanged();
            using var input = new FileStream(removal, FileAccess.Read, 128 * 1024, isAsync: false);
            using var target = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            if (input.Length != expected.Size || target.Length != expected.Size) throw SourceChanged();
            var originalHash = await SHA256.HashDataAsync(input, ct);
            var copiedHash = await SHA256.HashDataAsync(target, ct);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, copiedHash))
                throw new IOException("The verified destination differs from the original; the original file was retained.");
            if (!expected.Streams.SequenceEqual(ReadNamedStreams(source)) || !expected.Streams.SequenceEqual(ReadNamedStreams(destination)))
                throw SourceChanged();
            foreach (var stream in expected.Streams)
            {
                ct.ThrowIfCancellationRequested();
                ValidateStreamName(stream.Name);
                var originalStream = new FileStream(WindowsFilePaths.ToExtendedPath(source) + stream.Name,
                    FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, true);
                streamGuards.Add(originalStream);
                var copiedStream = new FileStream(WindowsFilePaths.ToExtendedPath(destination) + stream.Name,
                    FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                streamGuards.Add(copiedStream);
                var originalStreamHash = await SHA256.HashDataAsync(originalStream, ct);
                var copiedStreamHash = await SHA256.HashDataAsync(copiedStream, ct);
                if (originalStream.Length != stream.Size || copiedStream.Length != stream.Size ||
                    !CryptographicOperations.FixedTimeEquals(originalStreamHash, copiedStreamHash))
                    throw new IOException("The copied file metadata differs; the original file was retained.");
            }
            var after = ReadRemovalMetadata(removal);
            if (after != before) throw SourceChanged();
            ct.ThrowIfCancellationRequested();
            // FILE_DISPOSITION_DELETE | FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE.
            // Driver rejection retains the original. No ACL or protection is weakened.
            var disposition = new RemovalDisposition { Flags = 0x11 };
            if (!SetRemovalDisposition(removal, 21, ref disposition, 4))
                throw RemovalError(Marshal.GetLastWin32Error());
        }
        finally
        {
            foreach (var stream in streamGuards) stream.Dispose();
            foreach (var guard in guards) guard.Dispose();
        }
    }

    private static void PinDirectoryAncestors(string file, List<SafeFileHandle> guards)
    {
        var directories = new Stack<string>();
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file))!); directory is not null; directory = directory.Parent)
            directories.Push(directory.FullName);
        foreach (var directory in directories)
        {
            ValidateDirectoryPath(directory);
            // A volume root cannot be renamed or substituted as a directory entry.
            // Its volume identity is checked on the held source file itself.
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) is null) continue;
            guards.Add(OpenMetadataGuard(directory));
        }
    }

    private static RemovalMetadata ReadRemovalMetadata(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, 8)) throw RemovalError(Marshal.GetLastWin32Error());
        if (tag.Tag is 0xA0000003 or 0xA000000C || (tag.Attributes & (uint)FileAttributes.Directory) != 0)
            throw new IOException("A source file was replaced by a link or directory; it was retained.");
        ValidateCopyAttributes((FileAttributes)tag.Attributes);
        if (!GetFileInformationByHandleEx(handle, 0, out BasicInformation basic, (uint)Marshal.SizeOf<BasicInformation>()))
            throw RemovalError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 18, out FileIdentification identity, (uint)Marshal.SizeOf<FileIdentification>()))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (50 or 87) || !GetFileInformationByHandle(handle, out var legacy)) throw RemovalError(error);
            identity = new() { Volume = legacy.Volume, Low = (ulong)legacy.IndexHigh << 32 | legacy.IndexLow };
        }
        return new(new(identity.Volume, identity.Low, identity.High, basic.Changed, basic.Attributes),
            DateTime.FromFileTimeUtc(basic.Modified), (FileAttributes)basic.Attributes &
            (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
    }

    private static IOException RemovalError(int code) => new("Windows retained an original file after its verified copy.", new Win32Exception(code));
    private sealed record RemovalMetadata(NativeMetadata Native, DateTime ModifiedUtc, FileAttributes Appearance);
    [StructLayout(LayoutKind.Sequential)] private struct RemovalDisposition { public uint Flags; }
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRemovalDisposition(SafeFileHandle handle, int informationClass, ref RemovalDisposition disposition, uint size);
}

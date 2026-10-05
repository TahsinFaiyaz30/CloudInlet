using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CloudBay.Core.Sync;

/// <summary>Disconnect cleanup removes only copies backed by a verified, still available cloud version.</summary>
public static class VerifiedCloudCopyCleanup
{
    public static bool HasNamedStreams(string path) => VerifiedTreeCopy.CloudCopyHasNamedStreams(path);
    internal static IDisposable GuardAncestors(string path) => VerifiedTreeCopy.CloudCopyGuardAncestors(path);
    public static Task<BackupTransferOutcome> RemoveAsync(string root, IReadOnlyDictionary<string, SyncEntry> manifest,
        Func<string, PlaceholderFileState> state,
        Func<string, CloudObject, CancellationToken, Task<bool>> removeOnline,
        Func<CloudObject, CancellationToken, Task> verifyCloud,
        CancellationToken cancellationToken = default) =>
        VerifiedTreeCopy.RemoveVerifiedCloudCopiesAsync(root, manifest, state, removeOnline, verifyCloud, cancellationToken);
}

public static partial class VerifiedTreeCopy
{
    internal static bool CloudCopyHasNamedStreams(string path) => ReadNamedStreams(path).Count > 0;
    internal static IDisposable CloudCopyGuardAncestors(string path)
    {
        var guards = new List<SafeFileHandle>();
        try { PinDirectoryAncestors(path, guards); return new CloudCopyGuards(guards); }
        catch { foreach (var guard in guards) guard.Dispose(); throw; }
    }
    private sealed class CloudCopyGuards(IReadOnlyList<SafeFileHandle> guards) : IDisposable
    { public void Dispose() { foreach (var guard in guards) guard.Dispose(); } }
    internal static async Task<BackupTransferOutcome> RemoveVerifiedCloudCopiesAsync(string root,
        IReadOnlyDictionary<string, SyncEntry> manifest, Func<string, PlaceholderFileState> state,
        Func<string, CloudObject, CancellationToken, Task<bool>> removeOnline,
        Func<CloudObject, CancellationToken, Task> verifyCloud, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Verified local cleanup requires Windows file handles.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ValidateDirectoryPath(root);
        long removed = 0, retained = 0;
        foreach (var (relative, entry) in manifest)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.RelativePath != relative || entry.NativeMarkPending || entry.Remote.Action != "upload" ||
                    entry.Remote.Size != entry.LocalSize || entry.Remote.Sha1 is not { Length: 40 } checksum || !checksum.All(Uri.IsHexDigit))
                { retained++; continue; }
                var path = PathRules.FullPath(root, relative);
                if (!File.Exists(path)) continue;
                ValidateFilePath(path);
                // Cloud transports back up the unnamed content stream; independent Windows
                // alternate streams must not disappear with a superficially identical copy.
                if (ReadNamedStreams(path).Count > 0) { retained++; continue; }
                var fileState = state(path);
                if (fileState.HasLocalChanges) { retained++; continue; }
                await verifyCloud(entry.Remote, ct).ConfigureAwait(false);
                if (fileState.IsPlaceholder && !fileState.IsHydrated)
                {
                    var onlineGuards = new List<SafeFileHandle>();
                    try
                    {
                        PinDirectoryAncestors(path, onlineGuards);
                        if (await removeOnline(path, entry.Remote, ct).ConfigureAwait(false)) removed++;
                        else retained++;
                    }
                    finally { foreach (var guard in onlineGuards) guard.Dispose(); }
                    continue;
                }
                var guards = new List<SafeFileHandle>();
                try
                {
                    PinDirectoryAncestors(path, guards);
                    using var removal = CreateFileW(WindowsFilePaths.ToExtendedPath(path), 0x80010080, 1,
                        IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (removal.IsInvalid) throw RemovalError(Marshal.GetLastWin32Error());
                    var before = ReadRemovalMetadata(removal);
                    // Never cause hydration while carrying out the remove-local choice.
                    if ((before.Native.AllAttributes & ((uint)FileAttributes.Offline | 0x00400000 | 0x00040000)) != 0 ||
                        before.ModifiedUtc != entry.LocalWriteUtc.UtcDateTime)
                    { retained++; continue; }
                    using var source = new FileStream(removal, FileAccess.Read, 128 * 1024, isAsync: false);
                    if (source.Length != entry.LocalSize || !checksum.Equals(Convert.ToHexString(await SHA1.HashDataAsync(source, ct).ConfigureAwait(false)), StringComparison.OrdinalIgnoreCase) ||
                        ReadNamedStreams(path).Count > 0 || ReadRemovalMetadata(removal) != before)
                    { retained++; continue; }
                    ct.ThrowIfCancellationRequested();
                    var disposition = new RemovalDisposition { Flags = 0x11 };
                    if (!SetRemovalDisposition(removal, 21, ref disposition, 4)) throw RemovalError(Marshal.GetLastWin32Error());
                    removed++;
                }
                finally { foreach (var guard in guards) guard.Dispose(); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { retained++; }
        }
        // Deliberately retain folders, user-only files, aliases, and every path outside the
        // reviewed manifest. No recursive directory deletion is used here.
        return new(removed, retained, retained > 0 ? $"{retained:N0} changed, unverified, or unavailable local copies were kept. Other personal files and folders were retained." : null);
    }
}

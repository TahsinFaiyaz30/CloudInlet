using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Packaging;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Windows;

/// <summary>Stages only the packaged runtime inventory outside the installation being replaced.</summary>
internal static class InstallerProgressHost
{
    private sealed record Payload(int SchemaVersion, IReadOnlyList<PayloadFile> Files);
    private sealed record PayloadFile(string Path, long Size, string Sha256);
    private sealed record StagedPayload(string Id, string Directory, Payload Payload);
    private static readonly byte[] ReceiptEntropy = "CloudInlet.InstallerProgressRuntime.v1"u8.ToArray();
    private const string ReceiptName = ".runtime.dpapi";
    private const string LeaseName = ".observer.lock";

    internal static string RuntimeRoot(string flavor)
    {
        if (flavor is not ("Release" or "Debug")) throw new IOException("The progress runtime build flavor is invalid.");
        // This must be outside Client: the restarted app can review migration of
        // its state folder while the completed installer window remains open.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudInlet", "Migration", flavor, "InstallerProgress");
    }

    internal static string Stage(string installation, string cache, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("The update observer identity is invalid.");
        installation = Path.GetFullPath(installation);
        var host = Path.Combine(Path.GetFullPath(cache), "host");
        UpdateCacheLocation.EnsureNormalPath(host, true);
        Directory.CreateDirectory(host);
        using var ancestors = new AncestorGuards(installation, host);
        var inventoryPath = Path.Combine(installation, "installer-progress-payload.json");
        UpdateCacheLocation.EnsureNormalPath(inventoryPath, false);
        using var inventory = new FileStream(inventoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (inventory.Length is < 1 or > 4 * 1024 * 1024) throw new IOException("The packaged progress-window inventory is invalid.");
        var payload = JsonSerializer.Deserialize<Payload>(inventory) ?? throw new IOException("The packaged progress-window inventory is empty.");
        if (payload.SchemaVersion != 1 || payload.Files is null || payload.Files.Count is < 1 or > 10000 ||
            payload.Files.Any(file => file is null || file.Size < 0 || file.Size > 1024L * 1024 * 1024 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)) ||
            payload.Files.Sum(file => file.Size) > 2L * 1024 * 1024 * 1024 ||
            payload.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Files.Count ||
            !payload.Files.Any(file => file.Path == "CloudInlet.exe"))
            throw new IOException("The packaged progress-window inventory is unsupported.");
        var output = Path.Combine(host, "progress-" + id);
        UpdateCacheLocation.EnsureNormalPath(output, true);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("This update already has a staged progress window.");
        // Validate every relative name before creating the new owned directory.
        foreach (var file in payload.Files) { Resolve(installation, file.Path); Resolve(output, file.Path); }
        Directory.CreateDirectory(output);
        using var outputGuard = new AncestorGuards(output);
        using var lease = AcquireRuntimeLease(output);
        var receipt = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new StagedPayload(id, output, payload)),
            ReceiptEntropy, DataProtectionScope.CurrentUser);
        using (var owner = new FileStream(Path.Combine(output, ReceiptName), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { owner.Write(receipt); owner.Flush(true); }
        foreach (var file in payload.Files)
        {
            var source = Resolve(installation, file.Path); var destination = Resolve(output, file.Path);
            using var sourceParents = new AncestorGuards(Path.GetDirectoryName(source)!);
            UpdateCacheLocation.EnsureNormalPath(source, false);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Size) throw new IOException("An installed runtime file changed. Repair CloudInlet before migrating its installation.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var destinationParents = new AncestorGuards(Path.GetDirectoryName(destination)!);
            UpdateCacheLocation.EnsureNormalPath(destination, false);
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
            input.CopyTo(target); target.Flush(true); target.Position = 0;
            if (!Convert.ToHexString(SHA256.HashData(target)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The progress-window runtime failed verification. The installed app was retained.");
        }
        return Path.Combine(output, "CloudInlet.exe");
    }

    internal static FileStream AcquireRuntimeLease(string directory)
    {
        var lease = Path.Combine(directory, LeaseName);
        UpdateCacheLocation.EnsureNormalPath(lease, false);
        return new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    }

    internal static FileStream AcquireObserverLease(string directory)
    {
        var lease = Path.Combine(directory, LeaseName);
        UpdateCacheLocation.EnsureNormalPath(lease, false);
        // Multiple verified windows may overlap during a handoff. Read leases
        // permit that overlap while denying the writer lease required for cleanup.
        return new FileStream(lease, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    // Delete only a protected, inactive inventory of byte-verified runtime files.
    // Unknown/changed files, linked paths and active windows are always retained.
    internal static void Prune(string cache, bool includeRecent = false)
    {
        var host = Path.Combine(Path.GetFullPath(cache), "host");
        UpdateCacheLocation.EnsureNormalPath(host, true);
        if (!Directory.Exists(host)) return;
        using var hostGuard = new AncestorGuards(host);
        foreach (var directory in Directory.EnumerateDirectories(host, "progress-*"))
        {
            var id = Path.GetFileName(directory)[9..];
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try { PruneOne(directory, id, includeRecent); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            { /* Keep unverifiable or in-use runtime files for a later safe cleanup. */ }
        }
    }

    private static void PruneOne(string directory, string id, bool includeRecent)
    {
        // An interrupted EXE relocation may need this window after sign-in, even
        // when no observer process currently holds its runtime lease.
        foreach (var flavor in new[] { "Debug", "Release" })
            if (directory.StartsWith(Path.Combine(RuntimeRoot(flavor), "host") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                ExeMigrationReceipt.ReferencesObserver(directory, flavor)) return;
        UpdateCacheLocation.EnsureNormalPath(directory, true);
        var receiptPath = Path.Combine(directory, ReceiptName);
        UpdateCacheLocation.EnsureNormalPath(receiptPath, false);
        if (!File.Exists(receiptPath) || !includeRecent && File.GetLastWriteTimeUtc(receiptPath) > DateTime.UtcNow.AddDays(-1)) return;
        using (var rootGuard = new AncestorGuards(directory))
        using (var lease = AcquireRuntimeLease(directory))
        using (var receipt = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (receipt.Length is < 1 or > 4 * 1024 * 1024) return;
            var bytes = new byte[checked((int)receipt.Length)]; receipt.ReadExactly(bytes);
            var plain = ProtectedData.Unprotect(bytes, ReceiptEntropy, DataProtectionScope.CurrentUser);
            StagedPayload? staged;
            try { staged = JsonSerializer.Deserialize<StagedPayload>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (staged is null || staged.Id != id || !staged.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                staged.Payload?.SchemaVersion != 1 || staged.Payload.Files is null || staged.Payload.Files.Count is < 1 or > 10000) return;
            var expected = staged.Payload.Files.ToDictionary(file => Resolve(directory, file.Path), StringComparer.OrdinalIgnoreCase);
            // Enumerate one directory level at a time, rejecting links before descending.
            var pending = new Queue<string>(); pending.Enqueue(directory);
            var actual = new List<string>();
            var directoryGuards = new List<AncestorGuards>();
            try
            {
                while (pending.TryDequeue(out var current))
                {
                    directoryGuards.Add(new AncestorGuards(current));
                    UpdateCacheLocation.EnsureNormalPath(current, true);
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                    {
                        UpdateCacheLocation.EnsureNormalPath(child, Directory.Exists(child));
                        if (Directory.Exists(child)) pending.Enqueue(child); else actual.Add(child);
                    }
                }
                if (actual.Any(path => path != receiptPath && path != Path.Combine(directory, LeaseName) && !expected.ContainsKey(path))) return;
                var handles = new List<FileStream>();
                var parents = new List<AncestorGuards>();
                try
                {
                    foreach (var path in actual.Where(expected.ContainsKey))
                    {
                        parents.Add(new AncestorGuards(Path.GetDirectoryName(path)!));
                        // DELETE access without delete sharing pins this exact file object;
                        // deletion below acts on the verified handle, never a reopened path.
                        var handle = CreateFileW(@"\\?\" + path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A progress runtime file is still in use."); }
                        var file = new FileStream(handle, FileAccess.Read); handles.Add(file);
                        var entry = expected[path];
                        if (file.Length != entry.Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return;
                    }
                    foreach (var file in handles)
                    {
                        var disposition = new byte[] { 1 };
                        if (!SetFileInformationByHandle(file.SafeFileHandle, 4, disposition, 1))
                            throw new IOException("A progress runtime file could not be removed safely.");
                    }
                }
                finally { foreach (var file in handles) file.Dispose(); foreach (var parent in parents) parent.Dispose(); }
            }
            finally { foreach (var guard in directoryGuards) guard.Dispose(); }
        }
        // Retain the small protected ownership receipt and empty directory tree.
        // They support recovery/audit without any recursive directory deletion.
    }

    internal static async Task LaunchAsync(string executable, string journal, string id, string requestHash,
        Process worker, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var value in new[] { "--installer-progress=" + journal, "--progress-id=" + id, "--request-hash=" + requestHash,
            "--worker-id=" + worker.Id, "--worker-start=" + worker.StartTime.ToUniversalTime().Ticks }) start.ArgumentList.Add(value);
        using var observer = Process.Start(start) ?? throw new IOException("CloudInlet could not open installation progress.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                if (InstallerProgressJournal.IsObserverReady(journal, id, requestHash)) return;
                if (observer.HasExited) throw new IOException("The installation progress window closed before it was ready. Your current app remains open.");
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("The installation progress window did not become ready. Your current app remains open."); }
    }

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(part => part is "" or "." or "..") ||
            relative.Equals("distribution.json", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase) || relative.Equals(LeaseName, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The packaged progress inventory contains an invalid relative path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The progress runtime path escaped its owned directory.");
        return path;
    }

    private sealed class AncestorGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _held = [];
        internal AncestorGuards(params string[] paths)
        {
            try
            {
                foreach (var path in paths)
                    for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                    {
                        UpdateCacheLocation.EnsureNormalPath(current, true);
                        var handle = CreateFileW(@"\\?\" + current, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The installer progress runtime directory is busy or unavailable."); }
                        _held.Add(handle);
                        UpdateCacheLocation.EnsureNormalPath(current, true);
                    }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in _held) handle.Dispose(); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, byte[] information, uint size);
}

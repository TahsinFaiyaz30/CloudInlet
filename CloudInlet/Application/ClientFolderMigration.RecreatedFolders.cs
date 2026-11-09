using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Application;

internal sealed partial class ClientFolderMigration
{
    private sealed record RecreatedEntry(string RelativePath, bool Directory, FileAttributes Attributes, string? Sha256);
    private sealed record RecreatedReceipt(int Schema, string Fingerprint, string ItemId, string Original,
        DirectoryIdentity OriginalIdentity, string Source, string Preserved, DirectoryIdentity Identity,
        RecreatedEntry[] Entries);

    // Windows can recreate a still-mapped Known Folder after an interrupted root
    // rename. Keep the complete directory object in a visible sibling; never
    // merge or delete it, and never classify ordinary user files as disposable.
    private string PreservedFolder(string source) => Path.Combine(Path.GetDirectoryName(source)!,
        "CloudInlet recovered folder " + ReceiptKey(_plan, ItemId)[..16] + (Same(source, _receipt.Source) ? "-previous" : "-new"));
    private string RecreatedRecord(string source) => _directoryReceipt +
        (Same(source, _receipt.Source) ? ".recreated-previous.dpapi" : ".recreated-new.dpapi");

    private string LocateOriginalFolder(CancellationToken token)
    {
        CheckLinks(_receipt.Source); CheckLinks(_receipt.Destination);
        var sourceExists = Directory.Exists(_receipt.Source);
        var destinationExists = Directory.Exists(_receipt.Destination);
        var sourceOriginal = sourceExists && ReadIdentity(_receipt.Source) == _receipt.Identity;
        var destinationOriginal = destinationExists && ReadIdentity(_receipt.Destination) == _receipt.Identity;
        if (sourceOriginal == destinationOriginal)
            throw new IOException("The original folder cannot be identified at exactly one reviewed location. Both locations were retained.");
        var original = sourceOriginal ? _receipt.Source : _receipt.Destination;
        var other = sourceOriginal ? _receipt.Destination : _receipt.Source;
        foreach (var location in new[] { _receipt.Source, _receipt.Destination })
        {
            var record = ReadRecreatedReceipt(location);
            if (record is null) continue;
            if (Directory.Exists(record.Preserved)) VerifyPreservedFolder(record, token);
            else if (File.Exists(record.Preserved) || !Directory.Exists(record.Source) || ReadIdentity(record.Source) != record.Identity)
                throw new IOException("The separately preserved Windows folder needs inspection. Its recovery record was retained: " + record.Preserved);
        }
        if (File.Exists(other)) throw new IOException("A file occupies the other reviewed location. It was retained: " + other);
        if (!Directory.Exists(other)) return original;
        // Ready receipts have made no changes; an unrelated pre-existing target
        // remains a conflict. A native move receipt is mandatory evidence here.
        if (_receipt.Schema != 2 || _receipt.Phase != "Started" || !File.Exists(_directoryReceipt) || !Mappings().Any())
            throw new IOException("Both reviewed folders exist. No folder was moved or overwritten.");
        if (_runtime.IsRegistered(other, Account, Same(other, _receipt.Source)
                ? SyncRootMigrationIdentity.IsCanonical(_receipt.Settings, _receipt.Source) : true))
            throw new IOException("A recreated folder has a cloud registration. Its files and registration were retained: " + other);
        var previous = ReadRecreatedReceipt(other);
        if (previous is not null)
        {
            if (Directory.Exists(previous.Preserved) || ReadIdentity(other) != previous.Identity)
                throw new IOException("Another folder appeared after the previous conflict was preserved. Inspect both locations before retrying: " + other + " and " + previous.Preserved);
            RequireRecreatedEntries(previous.Entries, InspectRecreatedFolder(other, token));
        }
        else
        {
            CheckLinks(PreservedFolder(other));
            if (Directory.Exists(PreservedFolder(other)) || File.Exists(PreservedFolder(other)))
                throw new IOException("The recovery folder already exists without this migration's receipt. It was retained: " + PreservedFolder(other));
            _ = InspectRecreatedFolder(other, token);
        }
        return original;
    }

    private async Task PreserveRecreatedFolderAsync(IProgress<MigrationProgress> progress, CancellationToken token)
    {
        var original = LocateOriginalFolder(token);
        var other = Same(original, _receipt.Source) ? _receipt.Destination : _receipt.Source;
        if (!Directory.Exists(other)) return;
        var recordPath = RecreatedRecord(other);
        var receipt = ReadRecreatedReceipt(other);
        if (receipt is null)
        {
            receipt = new(1, _plan.Fingerprint, ItemId, original, _receipt.Identity,
                other, PreservedFolder(other), ReadIdentity(other), InspectRecreatedFolder(other, token));
            CheckLinks(recordPath);
            PublishProtected(recordPath, JsonSerializer.SerializeToUtf8Bytes(receipt));
        }
        if (ReadIdentity(other) != receipt.Identity) throw new IOException("The recreated Windows folder changed before preservation.");
        RequireRecreatedEntries(receipt.Entries, InspectRecreatedFolder(other, token));
        Report(progress, "Preserving recreated Windows folder", "Keeping Windows folder metadata at " + receipt.Preserved + ". No files will be merged or deleted.");
        token.ThrowIfCancellationRequested();
        var preservationPlan = MigrationPlan.Create([new(ItemId, MigrationArea.BackupFolder,
            "Preserve recreated Windows folder", other, receipt.Preserved, "Retain the separate directory before continuing " + _plan.Fingerprint)]);
        var moveRecord = recordPath + ".rename";
        using (var move = File.Exists(moveRecord)
                   ? WindowsDirectoryRelocation.Resume(preservationPlan, ItemId, moveRecord)
                   : WindowsDirectoryRelocation.Create(preservationPlan, ItemId, moveRecord))
        {
            try { await move.ApplyAsync(progress, token); }
            catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 5 })
            {
                // Windows can report ACCESS_DENIED for a directory rename when
                // a descendant file or directory is open, including handles
                // that share DELETE. Never change permissions to proceed.
                var blocked = FindRecreatedSharingConflict(receipt, token);
                if (blocked is not null)
                    throw new MigrationTransientException("Windows is still using " + blocked +
                        ". Close applications using that folder. Its contents remain at " + receipt.Source +
                        "; recovery will retry preservation at " + receipt.Preserved + ".");
                throw new IOException("Windows denied preservation of the recreated folder (error 5): " + receipt.Source +
                    ". Close applications using it and check its permissions before retrying. The recovery destination is " +
                    receipt.Preserved + ". All contents and recovery records were retained.", error);
            }
            await move.VerifyAsync(token);
        }
        // A concurrent writer may add content during the rename. The entire
        // directory is retained, but unexpected content must block completion.
        VerifyPreservedFolder(receipt, token);
        Report(progress, "Recreated Windows folder preserved", receipt.Preserved + " remains available. Continuing only the selected migration.");
    }

    private string? FindRecreatedSharingConflict(RecreatedReceipt receipt, CancellationToken token)
    {
        CheckLinks(receipt.Source);
        if (ReadIdentity(receipt.Source) != receipt.Identity)
            throw new IOException("The recreated Windows folder changed while diagnosing the denied rename. Both locations were retained.");
        // Probe only the bounded, previously verified metadata inventory. An
        // ancestor rename can fail even if a descendant handle shares DELETE.
        // The second, exclusive READ probe detects that case. Release each
        // probe before the next one so our own handle cannot create contention.
        // Access rights here never delete, read payloads or modify permissions.
        foreach (var entry in receipt.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(receipt.Source, entry.RelativePath);
            var guards = new List<SafeFileHandle>();
            try
            {
                ClientRegistrationMigration.PinAncestors(Path.GetDirectoryName(path)!, guards);
                CheckLinks(path);
                foreach (var probe in new[] { (Access: 0x10080u, Share: 7u), (Access: 0x81u, Share: 0u) })
                {
                    using var handle = CreateFileW(@"\\?\" + path, probe.Access, probe.Share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        var code = Marshal.GetLastWin32Error();
                        if (code is 32 or 33) return path;
                        // Access denied or an unavailable entry does not establish
                        // a sharing conflict. Keep the original failure visible.
                        return null;
                    }
                    if (!GetFileInformationByHandle(handle, out var information) ||
                        (information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 ||
                        ((information.Attributes & (uint)FileAttributes.Directory) != 0) != entry.Directory)
                        throw new IOException("A recreated Windows folder entry changed during lock diagnosis. It was retained: " + path);
                }
            }
            finally { foreach (var guard in guards) guard.Dispose(); }
        }
        return null;
    }

    private RecreatedReceipt? ReadRecreatedReceipt(string source)
    {
        var path = RecreatedRecord(source); CheckLinks(path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 256 * 1024) throw new InvalidDataException("Invalid recreated-folder recovery record.");
        var encrypted = new byte[checked((int)input.Length)]; input.ReadExactly(encrypted);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        RecreatedReceipt value;
        try { value = JsonSerializer.Deserialize<RecreatedReceipt>(plain) ?? throw new InvalidDataException("Missing recreated-folder recovery record."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var expectedOriginal = Same(source, _receipt.Source) ? _receipt.Destination : _receipt.Source;
        if (value.Schema != 1 || value.Fingerprint != _plan.Fingerprint || value.ItemId != ItemId ||
            !Same(value.Original, expectedOriginal) || value.OriginalIdentity != _receipt.Identity ||
            !Same(value.Source, source) || !Same(value.Preserved, PreservedFolder(source)) || value.Identity is null ||
            value.Identity == _receipt.Identity || value.Entries is null || value.Entries.Length is < 1 or > 128)
            throw new InvalidDataException("The recreated-folder receipt does not match this migration.");
        return value;
    }

    private void VerifyPreservedFolder(RecreatedReceipt receipt, CancellationToken token)
    {
        CheckLinks(receipt.Preserved);
        if (ReadIdentity(receipt.Preserved) != receipt.Identity)
            throw new IOException("The preserved Windows folder was replaced. Inspect it before retrying: " + receipt.Preserved);
        RequireRecreatedEntries(receipt.Entries, InspectRecreatedFolder(receipt.Preserved, token));
    }

    private static void RequireRecreatedEntries(RecreatedEntry[] expected, RecreatedEntry[] actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new IOException("The recreated Windows folder changed after inspection. Its entire contents were retained; review the recovery location before retrying.");
    }

    private RecreatedEntry[] InspectRecreatedFolder(string root, CancellationToken token)
    {
        CheckLinks(root);
        var mapped = Mappings().Select(folder => Path.GetRelativePath(_receipt.Source, folder.DestinationPath))
            .Select(path => path == "." ? "" : path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<RecreatedEntry>();
        var pending = new Stack<string>(); pending.Push(root);
        var guards = new List<SafeFileHandle>();
        long total = 0;
        try
        {
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= 128) throw new IOException("The recreated Windows folder exceeds the metadata-only recovery limit. It was retained.");
            CheckLinks(path);
            var relative = Path.GetRelativePath(root, path); if (relative == ".") relative = "";
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 || HasRecreatedNamedStreams(path))
                throw new IOException("A recreated Windows folder contains links, online-only content or named streams. It was retained: " + path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                // Pin each inspected directory against substitution while its
                // children are read. Never follow a reparse-point file handle.
                ClientRegistrationMigration.PinAncestors(path, guards);
                if (relative.Length > 0 && !mapped.Any(name => name.Equals(relative, StringComparison.OrdinalIgnoreCase) || name.StartsWith(relative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("An unexpected directory appeared at the previous location. It was retained: " + path);
                entries.Add(new(relative, true, attributes, null));
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if (entries.Count + pending.Count >= 128) throw new IOException("The recreated folder inventory is too large. It was retained.");
                    pending.Push(child);
                }
            }
            else
            {
                var parent = Path.GetDirectoryName(relative) ?? "";
                const FileAttributes metadata = FileAttributes.Hidden | FileAttributes.System;
                if (!mapped.Contains(parent) || !Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || (attributes & metadata) != metadata)
                    throw new IOException("New user content exists at the previous folder location. Move or review it before retrying; no contents were merged or deleted: " + path);
                using var handle = CreateFileW(@"\\?\" + path, 0x80000000, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Offline)) != 0 ||
                    (information.Attributes & (uint)metadata) != (uint)metadata)
                    throw new IOException("A recreated metadata file changed during inspection. It was retained.");
                using var input = new FileStream(handle, FileAccess.Read);
                total += input.Length;
                if (input.Length > 64 * 1024 || total > 128 * 1024) throw new IOException("The recreated folder contains more than Windows metadata. It was retained.");
                entries.Add(new(relative, false, attributes, Convert.ToHexString(SHA256.HashData(input))));
            }
        }
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
        }
        finally { foreach (var guard in guards) guard.Dispose(); }
    }

    private static bool HasRecreatedNamedStreams(string path)
    {
        var search = FindFirstStreamW(@"\\?\" + path, 0, out var stream, 0);
        if (search == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return false; // No streams on an empty ordinary directory.
            throw new IOException("Windows could not inspect recreated-folder streams.", new Win32Exception(error));
        }
        try
        {
            do { if (stream.Name != "::$DATA") return true; } while (FindNextStreamW(search, out stream));
            var error = Marshal.GetLastWin32Error();
            if (error != 38) throw new IOException("Windows could not finish inspecting recreated-folder streams.", new Win32Exception(error));
            return false;
        }
        finally { FindClose(search); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RecreatedStream { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string name, int level, out RecreatedStream data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr handle, out RecreatedStream data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}

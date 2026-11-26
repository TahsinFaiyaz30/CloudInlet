using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.Transfers;
using CloudInlet.Packaging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Application;

// The application mutex is held before any profile is selected. Nothing is moved or merged.
internal static class SavedProfileLocation
{
    private const string MarkerName = ".cloudinlet-profile-v1";
    private static readonly byte[] Marker = "CloudInlet profile v1\n"u8.ToArray();
    private sealed record PriorRoute(int Schema, string Home, string Flavor, string Location, JsonElement? Plan, string? ItemId, string? Receipt);
    private sealed record PriorReview(int SchemaVersion, string Id, string[] SelectedIds);
    private sealed record DirectoryIdentity(ulong Volume, ulong Low, ulong High, long Created);
    private sealed record PriorReceipt(int Schema, string Item, string Fingerprint, string Source, string Destination,
        DirectoryIdentity Root, DirectoryIdentity SourceParent, DirectoryIdentity DestinationParent);

    internal static string Resolve(string localAppData, string flavor, Action? checkUpgrade = null)
    {
        var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
        PreviousInstallation.RequireFinished(home, flavor);
        var relative = flavor == "Debug" ? @"Debug\Client" : "Client";
        var canonical = Path.Combine(home, "CloudInlet", relative);
        var previous = Path.Combine(home, "CloudBay", relative);
        var records = Path.Combine(home, "CloudInlet", "Migration", flavor, "State");
        var routePath = Path.Combine(records, "state-route.dpapi");
        UpdateCacheLocation.EnsureNormalPath(routePath, false);
        UpdateCacheLocation.EnsureNormalPath(previous, true);
        UpdateCacheLocation.EnsureNormalPath(canonical, true);
        using var canonicalGuard = Directory.Exists(canonical) ? PinDirectory(canonical) : null;
        using var previousGuard = Directory.Exists(previous) ? PinDirectory(previous) : null;
        var canonicalUsed = canonicalGuard is not null && !IsEmptyDirectoryTree(canonical);
        var previousUsed = previousGuard is not null && !IsEmptyDirectoryTree(previous);
        if (canonicalUsed && previousUsed)
            throw new IOException($"Two populated profile folders need review in CloudInlet 1.2.2. Both were preserved.\n{canonical}\n{previous}");
        if (File.Exists(routePath))
        {
            var route = ReadProtected<PriorRoute>(routePath, "CloudInlet.ClientStateRoute.v1");
            if (route.Schema != 1 || !string.Equals(route.Home, home, StringComparison.OrdinalIgnoreCase) || route.Flavor != flavor)
                throw new InvalidDataException("The saved profile location belongs to a different installation.");
            if (route.Location == "Fresh" && route.Plan is null && !previousUsed && (!canonicalUsed || HasMarker(canonical)))
                return Register(canonical, checkUpgrade);
            var directory = route.Location switch
            {
                "Canonical" when canonicalGuard is not null && !previousUsed => canonical,
                "Legacy" when previousGuard is not null && !canonicalUsed => previous,
                _ => throw new IOException(PreviousInstallation.FinishMessage)
            };
            if (route.Plan is { } plan)
            {
                if (route.ItemId != "application-state" || !Path.IsPathFullyQualified(route.Receipt ?? "") ||
                    !Path.GetFullPath(route.Receipt!).StartsWith(records + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The saved profile receipt does not identify this installation.");
                var receipt = ReadProtected<PriorReceipt>(route.Receipt!, "CloudInlet.DirectoryRelocation.v1");
                if (receipt.Schema != 1 || receipt.Item != route.ItemId || receipt.Fingerprint != plan.GetProperty("Fingerprint").GetString() ||
                    !string.Equals(receipt.Source, previous, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(receipt.Destination, canonical, StringComparison.OrdinalIgnoreCase) ||
                    DirectoryId(directory) != receipt.Root || DirectoryId(Path.GetDirectoryName(previous)!) != receipt.SourceParent ||
                    DirectoryId(Path.GetDirectoryName(canonical)!) != receipt.DestinationParent)
                    throw new IOException("The saved profile folder differs from the one verified by CloudInlet 1.2.2. Both locations were preserved.");
            }
            return Register(directory, checkUpgrade);
        }
        RequireFinishedReviews(records);
        var selected = previousUsed ? previous : canonical;
        if (HasMarker(selected)) return selected;
        if (previousUsed || canonicalUsed)
        {
            if (CloudInlet.Windows.WindowsUpdateInstaller.IsInstallationInProgress(Path.Combine(selected, "Updates")))
                throw new IOException("An update is using the saved profile. Reopen CloudInlet after the update finishes.");
            var settings = Path.Combine(selected, "settings.json");
            if (!File.Exists(settings)) throw new IOException($"The existing folder has no identifiable CloudInlet settings. Open 1.2.2 to review it.\n{selected}");
            using var document = JsonDocument.Parse(ReadSmall(settings));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("RootPath", out var savedRoot) || savedRoot.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("CloudInlet could not identify the existing profile settings.");
            ValidateRetainedProfile(selected);
        }
        return Register(selected, canonicalUsed || previousUsed ? checkUpgrade : null);
    }

    internal static IDisposable PinDirectory(string directory) => new PathGuards(directory, false);

    private static void RequireFinishedReviews(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "state-use.lock", "last-offered.txt" };
        foreach (var file in Directory.EnumerateFiles(directory, "finished-*.dpapi"))
        {
            var id = Path.GetFileNameWithoutExtension(file)["finished-".Length..];
            var review = ReadProtected<PriorReview>(file, "CloudInlet.MigrationConsent.v1");
            if (!Guid.TryParseExact(id, "N", out _) || review.SchemaVersion != 1 || review.Id != id ||
                review.SelectedIds is not { Length: > 0 } || review.SelectedIds.Contains("application-state", StringComparer.Ordinal))
                throw new IOException(PreviousInstallation.FinishMessage);
            known.Add(Path.GetFileName(file));
            known.Add("session-" + id + ".json");
            known.Add("receipts-" + id);
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckPath(entry);
            if (!known.Contains(Path.GetFileName(entry))) throw new IOException(PreviousInstallation.FinishMessage);
        }
    }

    private static bool HasMarker(string directory)
    {
        var path = Path.Combine(directory, MarkerName);
        UpdateCacheLocation.EnsureNormalPath(path, false);
        if (!File.Exists(path)) return false;
        using var stream = OpenFile(path, FileAccess.Read, 1);
        Span<byte> bytes = stackalloc byte[Marker.Length];
        if (stream.Length != Marker.Length) throw new InvalidDataException("The CloudInlet profile marker is incomplete.");
        stream.ReadExactly(bytes);
        if (!bytes.SequenceEqual(Marker)) throw new InvalidDataException("The CloudInlet profile marker could not be verified.");
        return true;
    }

    private static string Register(string directory, Action? checkUpgrade)
    {
        if (HasMarker(directory)) return directory;
        checkUpgrade?.Invoke();
        using var guard = new PathGuards(directory, true);
        if (HasMarker(directory)) return directory;
        var path = Path.Combine(directory, MarkerName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = OpenFile(temporary, FileAccess.ReadWrite, 0, createNew: true)) { file.Write(Marker); file.Flush(true); }
        File.Move(temporary, path);
        return directory;
    }

    private static T ReadProtected<T>(string path, string purpose)
    {
        var decrypted = ProtectedData.Unprotect(ReadSmall(path), Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(decrypted) ?? throw new InvalidDataException("The saved profile record is empty."); }
        finally { CryptographicOperations.ZeroMemory(decrypted); }
    }

    private static DirectoryIdentity DirectoryId(string path)
    {
        using var handle = OpenHandle(path, 0x81, 3, 3, directory: true);
        if (!GetFileInformationByHandle(handle, out var basic) || !GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new IOException("Windows could not verify the profile folder identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new(id.Volume, id.Low, id.High, ((long)basic.Creation.dwHighDateTime << 32) | (uint)basic.Creation.dwLowDateTime);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity info, uint size);

    private static bool IsEmptyDirectoryTree(string directory)
    {
        var pending = new Stack<string>(); pending.Push(directory);
        var visited = 0;
        while (pending.TryPop(out var folder))
        {
            // Do not walk an arbitrary large tree while deciding whether startup is fresh.
            if (++visited > 128) return false;
            using var guard = new PathGuards(folder, create: false);
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                CheckPath(entry);
                if (folder == directory && IsUncommittedMarker(Path.GetFileName(entry))) continue;
                if ((File.GetAttributes(entry) & FileAttributes.Directory) == 0) return false;
                pending.Push(entry);
            }
        }
        return true;
    }

    private static bool IsUncommittedMarker(string name) => name.Length == MarkerName.Length + 37 && name.StartsWith(MarkerName + ".", StringComparison.Ordinal) &&
        name.EndsWith(".tmp", StringComparison.Ordinal) && Guid.TryParseExact(name[(MarkerName.Length + 1)..^4], "N", out _);

    private static void ValidateRetainedProfile(string directory)
    {
        CheckPath(directory);
        if (!Directory.Exists(directory)) throw new IOException("The saved application profile is missing.");
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var folder))
        {
            CheckPath(folder);
            foreach (var child in Directory.EnumerateFileSystemEntries(folder))
            {
                CheckPath(child);
                if (Directory.Exists(child)) pending.Push(child);
                else if (!string.Equals(Path.GetRelativePath(directory, child), @"Updates\update-install.lock", StringComparison.OrdinalIgnoreCase)) files.Add(child);
            }
        }
        files.Sort(StringComparer.Ordinal);
        // SHM is SQLite's reconstructible WAL index, not committed application
        // data. Read-only SQLite may rebuild it after a crash. Only recognize an
        // index paired with an existing database and WAL; arbitrary files remain
        // part of the exact inventory.
        var sharedIndexes = files.Where(file => Path.GetExtension(file).Equals(".sqlite", StringComparison.OrdinalIgnoreCase) &&
            files.Contains(file + "-wal", StringComparer.OrdinalIgnoreCase)).Select(file => file + "-shm").ToHashSet(StringComparer.OrdinalIgnoreCase);
        files.RemoveAll(sharedIndexes.Contains);
        var held = new List<FileStream>();
        var indexHandles = new List<FileStream>();
        var indexParents = new List<PathGuards>();
        try
        {
            // Keep every existing database, WAL and saved record fixed against writes and replacement during the integrity check.
            foreach (var file in files) held.Add(OpenFile(file, FileAccess.Read, 1));
            foreach (var index in sharedIndexes)
            {
                indexParents.Add(new PathGuards(Path.GetDirectoryName(index)!, create: false));
                // OPEN_REPARSE_POINT validates the exact cache object, and no
                // delete sharing prevents replacement while SQLite rebuilds it.
                indexHandles.Add(OpenFile(index, FileAccess.ReadWrite, 3, create: true));
            }
            ValidateSettings(directory); ValidateVault(directory, "credentials.dpapi", "CloudBay.B2.v1"u8.ToArray(), oneDrive: false);
            ValidateVault(directory, "onedrive.dpapi", "CloudBay.OneDrive.v1"u8.ToArray(), oneDrive: true);
            foreach (var file in files.Where(file => Path.GetExtension(file).Equals(".sqlite", StringComparison.OrdinalIgnoreCase))) ValidateDatabase(file);
        }
        finally
        {
            foreach (var stream in held) stream.Dispose();
            foreach (var stream in indexHandles) stream.Dispose();
            foreach (var guard in indexParents) guard.Dispose();
        }
    }

    private static void ValidateSettings(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return;
        var settings = JsonSerializer.Deserialize<AppSettings>(ReadSmall(path));
        if (settings is null || settings.SchemaVersion != 1 || settings.RootPath is null || settings.Backups is null ||
            settings.CustomBackups is null || settings.Exclusions is null || settings.Prefix is null)
            throw new InvalidDataException("The saved settings cannot be verified. Their original bytes were preserved.");
    }

    private static void ValidateVault(string directory, string file, byte[] entropy, bool oneDrive)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) return;
        var plaintext = ProtectedData.Unprotect(ReadSmall(path), entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (oneDrive)
            {
                var accounts = JsonSerializer.Deserialize<List<ClientStorage.OneDriveConnection>>(plaintext);
                if (accounts is null || accounts.Any(account => account is null || account.Tokens is null || string.IsNullOrWhiteSpace(account.Id)))
                    throw new InvalidDataException("A saved OneDrive account cannot be verified.");
            }
            else
            {
                var credentials = JsonSerializer.Deserialize<B2Credentials>(plaintext);
                if (credentials is null || string.IsNullOrWhiteSpace(credentials.KeyId) || string.IsNullOrWhiteSpace(credentials.ApplicationKey))
                    throw new InvalidDataException("The saved B2 account cannot be verified.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void ValidateDatabase(string path)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open();
        using var mode = database.CreateCommand(); mode.CommandText = "PRAGMA query_only=ON"; mode.ExecuteNonQuery();
        using var check = database.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        using (var rows = check.ExecuteReader())
            if (!rows.Read() || rows.GetString(0) != "ok" || rows.Read()) throw new InvalidDataException("A saved database failed its integrity check. The original files were preserved.");
        using var table = database.CreateCommand(); table.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='transfer_items'";
        if (Convert.ToInt64(table.ExecuteScalar()) == 0) return;
        using var checkpoints = database.CreateCommand(); checkpoints.CommandText = "SELECT checkpoint FROM transfer_items WHERE checkpoint IS NOT NULL";
        using var reader = checkpoints.ExecuteReader();
        while (reader.Read())
        {
            var plaintext = ProtectedData.Unprotect((byte[])reader[0], "CloudBay.TransferCheckpoint.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            try
            {
                var checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(plaintext);
                if (checkpoint is null || checkpoint.AcknowledgedBytes < 0 || string.IsNullOrWhiteSpace(checkpoint.Provider))
                    throw new InvalidDataException("A saved transfer checkpoint cannot be verified.");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    private static byte[] ReadSmall(string path)
    {
        using var input = OpenFile(path, FileAccess.Read, 5);
        if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("A saved settings or account file exceeds its supported size.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }


    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("A saved profile cannot follow links or junctions."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class PathGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal PathGuards(string directory, bool create)
        {
            var parts = new Stack<string>();
            for (string? path = directory; path is not null; path = Path.GetDirectoryName(path)) parts.Push(path);
            try
            {
                while (parts.TryPop(out var path))
                {
                    CheckPath(path);
                    if (create && !Directory.Exists(path)) Directory.CreateDirectory(path);
                    _handles.Add(OpenHandle(path, 0x81, 3, 3, directory: true));
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in _handles.AsEnumerable().Reverse()) handle.Dispose(); _handles.Clear(); }
    }
    private static FileStream OpenFile(string path, FileAccess access, uint share, bool create = false, bool createNew = false)
    {
        CheckPath(path);
        var handle = OpenHandle(path, access == FileAccess.Read ? 0x80000000u : 0xC0000000u, share, createNew ? 1u : create ? 4u : 3u, directory: false);
        try { return new FileStream(handle, access); } catch { handle.Dispose(); throw; }
    }
    private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint creation, bool directory)
    {
        var handle = CreateFile("\\\\?\\" + path, access, share, IntPtr.Zero, creation, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is 32 or 33) throw new IOException("Another application is using the saved profile. Wait for it to finish and reopen CloudInlet.");
            throw new IOException("The saved profile could not be safely opened: " + new Win32Exception(error).Message, new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
        { handle.Dispose(); throw new IOException("Profile paths must be ordinary files and directories without links."); }
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BasicInfo info);
}

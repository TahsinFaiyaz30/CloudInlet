using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Transfers;
using CloudInlet.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Application;

/// <summary>
/// Routes a profile before ClientController opens any databases or starts background work.
/// The caller also holds the published application mutex, including while showing migration UI.
/// A pending protected intent never permits a controller to initialize an empty replacement profile.
/// </summary>
internal sealed class ClientStateMigration : IMigrationOperation, IDisposable
{
    internal sealed record Route(int Schema, string Home, string Flavor, string Location,
        MigrationPlan? Plan = null, string? ItemId = null, string? Journal = null,
        string? Receipt = null, string? Inventory = null);
    private static readonly byte[] Entropy = "CloudInlet.ClientStateRoute.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly StateUseLease _lease;
    private readonly WindowsDirectoryRelocation _move;
    private readonly Route _intent;
    private bool _disposed;
    public string ItemId => _intent.ItemId!;
    public string ReviewedPlanFingerprint => _intent.Plan!.Fingerprint;
    // A reverted route can still need the outer session's final Reverted save after a crash.
    public bool CanRevert => !_disposed && _lease.ReadRoute()?.Location is "Pending" or "Legacy";

    internal sealed record StateResolution(string? DataDirectory, bool RequiresRecovery,
        MigrationPlan? Plan = null, string? SessionJournalPath = null);

    internal sealed class StateUseLease : IDisposable
    {
        private readonly PathGuards _guards;
        private readonly FileStream _file;
        private bool _disposed;
        internal ClientStateMigration? ActiveOperation { get; set; }
        internal string Home { get; }
        internal string Flavor { get; }
        internal string RoutePath => Path.Combine(RecoveryDirectory, "state-route.dpapi");
        internal string LegacyDirectory { get; }
        internal string CanonicalDirectory { get; }
        internal string RecoveryDirectory { get; }
        internal StateUseLease(string home, string flavor)
        {
            Home = NormalPath(home); Flavor = flavor;
            if (flavor is not ("Release" or "Debug")) throw new ArgumentException("Unknown application build flavor.", nameof(flavor));
            LegacyDirectory = Path.Combine(Home, "CloudBay", flavor == "Debug" ? @"Debug\Client" : "Client");
            CanonicalDirectory = Path.Combine(Home, "CloudInlet", flavor == "Debug" ? @"Debug\Client" : "Client");
            RecoveryDirectory = Path.Combine(Home, "CloudInlet", "Migration", flavor, "State");
            _guards = new PathGuards(RecoveryDirectory, create: true);
            try { _file = OpenFile(Path.Combine(RecoveryDirectory, "state-use.lock"), FileAccess.ReadWrite, 0, create: true); }
            catch { _guards.Dispose(); throw; }
        }
        internal void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
        internal Route? ReadRoute()
        {
            EnsureOpen();
            if (!File.Exists(RoutePath)) { CheckPath(RoutePath); return null; }
            using var stream = OpenFile(RoutePath, FileAccess.Read, 1);
            if (stream.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("The saved state location cannot be verified. Both profiles were preserved.");
            var encrypted = new byte[(int)stream.Length]; stream.ReadExactly(encrypted);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var route = JsonSerializer.Deserialize<Route>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException("The saved state location is empty.");
                if (route.Schema != 1 || route.Home != Home || route.Flavor != Flavor ||
                    route.Location is not ("Legacy" or "Canonical" or "Fresh" or "Pending"))
                    throw new InvalidDataException("The saved state location does not belong to this profile.");
                if (route.Plan is not null)
                {
                    ValidatePlan(this, route.Plan, route.ItemId!);
                    if (route.Receipt is null || route.Journal is null || !Within(RecoveryDirectory, route.Receipt) ||
                        !Within(RecoveryDirectory, route.Journal) || route.Inventory is not { Length: 64 } ||
                        route.Inventory.Any(c => !Uri.IsHexDigit(c)))
                        throw new InvalidDataException("The saved migration recovery information is incomplete.");
                }
                else if (route.Location == "Pending" || route.ItemId is not null || route.Journal is not null ||
                         route.Receipt is not null || route.Inventory is not null)
                    throw new InvalidDataException("The saved migration review is missing.");
                return route;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        internal void WriteRoute(Route route)
        {
            EnsureOpen(); CheckPath(RoutePath);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(route, JsonOptions);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            var temporary = RoutePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = OpenFile(temporary, FileAccess.ReadWrite, 0, createNew: true))
            { output.Write(encrypted); output.Flush(true); }
            File.Move(temporary, RoutePath, overwrite: true);
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _file.Dispose(); _guards.Dispose(); }
    }

    internal static StateUseLease AcquireStateUseLease(string localAppData, string flavor) => new(localAppData, flavor);

    internal static bool CanAcknowledgeUnstarted(StateUseLease lease, MigrationPlan plan, string sessionJournalPath)
    {
        lease.EnsureOpen();
        var item = plan.Items.Single(item => item.Area == MigrationArea.ApplicationState);
        ValidatePlan(lease, plan, item.Id);
        var journal = NormalPath(sessionJournalPath);
        if (!Within(lease.RecoveryDirectory, journal) || !File.Exists(journal)) return false;
        ValidateExistingJournal(journal, plan, item.Id);
        var route = lease.ReadRoute();
        CheckPath(lease.LegacyDirectory); CheckPath(lease.CanonicalDirectory);
        // Create durably writes its intent before returning an adapter capable of moving any
        // data. If that intent never existed, acknowledging Revert cannot undo any mutation.
        return lease.ActiveOperation is null && route?.Location is null or "Legacy" && route?.Journal != journal &&
            Directory.Exists(lease.LegacyDirectory) && !Directory.Exists(lease.CanonicalDirectory) && !File.Exists(lease.CanonicalDirectory);
    }

    internal static Task AcknowledgeUnstartedAsync(StateUseLease lease, MigrationPlan plan, string sessionJournalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanAcknowledgeUnstarted(lease, plan, sessionJournalPath))
            throw new IOException("The state change has a recovery intent and must be restored through that saved operation.");
        return Task.CompletedTask;
    }

    internal static StateResolution Resolve(StateUseLease lease)
    {
        lease.EnsureOpen();
        var route = lease.ReadRoute();
        CheckPath(lease.LegacyDirectory); CheckPath(lease.CanonicalDirectory);
        if (File.Exists(lease.LegacyDirectory) || File.Exists(lease.CanonicalDirectory))
            throw new IOException("A file occupies an application profile directory. It was preserved; no replacement profile was created.");
        if (route?.Location == "Pending") return new(null, true, route.Plan, route.Journal);
        var legacy = Directory.Exists(lease.LegacyDirectory);
        var canonical = Directory.Exists(lease.CanonicalDirectory);
        if (route is null)
        {
            if (canonical) throw new IOException("An unrecognized CloudInlet profile already exists. No profiles were merged or replaced.");
            if (legacy) return new(lease.LegacyDirectory, false);
            // The durable claim is written before creating an empty directory. A power loss between
            // these two steps can only resume this fresh initialization, never select another profile.
            route = new(1, lease.Home, lease.Flavor, "Fresh"); lease.WriteRoute(route);
        }
        if (route.Location == "Fresh")
        {
            if (legacy || canonical && Directory.EnumerateFileSystemEntries(lease.CanonicalDirectory).Any())
                throw new IOException("The new profile changed before initialization finished. Its contents were preserved.");
            using var created = new PathGuards(lease.CanonicalDirectory, create: true);
            lease.WriteRoute(route with { Location = "Canonical" });
            return new(lease.CanonicalDirectory, false);
        }
        if (route.Location == "Canonical" && canonical && !legacy)
        {
            VerifyRouteObject(route, atDestination: true);
            return new(lease.CanonicalDirectory, false, route.Plan, route.Journal);
        }
        if (route.Location == "Legacy" && legacy && !canonical)
        {
            VerifyRouteObject(route, atDestination: false);
            return new(lease.LegacyDirectory, false, route.Plan, route.Journal);
        }
        throw new IOException("The saved profile cannot be identified at exactly one owned location. Recovery is required; no empty profile was created.");

        void VerifyRouteObject(Route value, bool atDestination)
        {
            if (value.Plan is null) return;
            if (lease.ActiveOperation is { } active && active._intent.Receipt == value.Receipt)
            {
                (atDestination ? active._move.VerifyAsync(default) : active._move.VerifyRevertedAsync(default)).GetAwaiter().GetResult();
                return;
            }
            using var move = WindowsDirectoryRelocation.Resume(value.Plan, value.ItemId!, value.Receipt!);
            (atDestination ? move.VerifyAsync(default) : move.VerifyRevertedAsync(default)).GetAwaiter().GetResult();
        }
    }

    internal static ClientStateMigration Create(StateUseLease lease, MigrationPlan plan, string itemId, string sessionJournalPath)
    {
        lease.EnsureOpen(); ValidatePlan(lease, plan, itemId);
        if (lease.ActiveOperation is not null) throw new InvalidOperationException("A state migration adapter is already open.");
        if (Resolve(lease).DataDirectory != lease.LegacyDirectory) throw new InvalidOperationException("Only an existing legacy profile can be migrated.");
        var journal = NormalPath(sessionJournalPath);
        if (!Within(lease.RecoveryDirectory, journal))
            throw new IOException("Use a session journal inside this profile's recovery directory.");
        if (File.Exists(journal)) ValidateExistingJournal(journal, plan, itemId);
        using var worker = AcquireUpdateGuard(lease.LegacyDirectory);
        var inventory = InspectState(lease.LegacyDirectory, null, itemId, default);
        using var parents = new PathGuards(Path.GetDirectoryName(lease.CanonicalDirectory)!, create: true);
        var receipt = Path.Combine(lease.RecoveryDirectory, "move-" + Guid.NewGuid().ToString("N") + ".dpapi");
        var move = WindowsDirectoryRelocation.Create(plan, itemId, receipt);
        var intent = new Route(1, lease.Home, lease.Flavor, "Pending", plan, itemId, journal, receipt, inventory);
        try { lease.WriteRoute(intent); return new(lease, move, intent); }
        catch { move.Dispose(); throw; }
    }

    internal static ClientStateMigration Resume(StateUseLease lease)
    {
        lease.EnsureOpen();
        if (lease.ActiveOperation is not null) throw new InvalidOperationException("A state migration adapter is already open.");
        var intent = lease.ReadRoute();
        if (intent is not { Plan: not null } || intent.Location is not ("Pending" or "Canonical" or "Legacy"))
            throw new InvalidOperationException("This profile has no saved migration.");
        return new(lease, WindowsDirectoryRelocation.Resume(intent.Plan, intent.ItemId!, intent.Receipt!), intent);
    }

    private ClientStateMigration(StateUseLease lease, WindowsDirectoryRelocation move, Route intent)
    { _lease = lease; _move = move; _intent = intent; lease.ActiveOperation = this; }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var route = EnsureRoute(); cancellationToken.ThrowIfCancellationRequested();
        if (route.Location == "Canonical") { await _move.VerifyAsync(cancellationToken).ConfigureAwait(false); return; }
        if (route.Location == "Legacy") throw new InvalidOperationException("This state move was reverted. Review a new migration to move it again.");
        await _move.CheckAsync(cancellationToken).ConfigureAwait(false);
        using var worker = AcquireUpdateGuard(CurrentDirectory());
        MatchInventory(InspectState(CurrentDirectory(), null, ItemId, cancellationToken));
    }

    public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        var route = EnsureRoute();
        if (route.Location == "Canonical") { await _move.VerifyAsync(cancellationToken).ConfigureAwait(false); return; }
        if (route.Location == "Legacy") throw new InvalidOperationException("This state move was reverted. Review a new migration to move it again.");
        using var worker = AcquireUpdateGuard(CurrentDirectory());
        progress.Report(new(ItemId, "Checking saved data", "Checking settings, encrypted accounts, databases and recovery records before moving the original folder."));
        MatchInventory(InspectState(CurrentDirectory(), progress, ItemId, cancellationToken));
        // Windows does not rename a directory while any descendant remains open, including
        // this guard. Release it immediately before the native rename. A worker acquiring it
        // first makes the rename fail without moving anything; a worker arriving after rename
        // cannot open the old cache. The caller retains the app mutex throughout this window.
        worker?.Dispose();
        try { await _move.ApplyAsync(progress, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { using var checkWorker = AcquireUpdateGuard(CurrentDirectory()); throw; }
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var route = EnsureRoute();
        if (route.Location == "Legacy") throw new InvalidOperationException("The state move was reverted and cannot be marked migrated.");
        await _move.VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (route.Location == "Canonical") return; // Route committed before outer session's completion save.
        using var worker = AcquireUpdateGuard(_lease.CanonicalDirectory);
        MatchInventory(InspectState(_lease.CanonicalDirectory, null, ItemId, cancellationToken));
        // No caller can construct ClientController until both verification and this durable route commit succeed.
        _lease.WriteRoute(_intent with { Location = "Canonical" });
    }

    public async Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        var route = EnsureRoute();
        if (route.Location == "Canonical") throw new InvalidOperationException("Completed application-state migration cannot be reverted.");
        if (route.Location == "Legacy") { await _move.VerifyRevertedAsync(cancellationToken).ConfigureAwait(false); return; }
        using var worker = AcquireUpdateGuard(CurrentDirectory());
        worker?.Dispose();
        try { await _move.RevertAsync(progress, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { using var checkWorker = AcquireUpdateGuard(CurrentDirectory()); throw; }
    }

    public async Task VerifyRevertedAsync(CancellationToken cancellationToken)
    {
        var route = EnsureRoute();
        if (route.Location == "Canonical") throw new InvalidOperationException("Completed application-state migration cannot be reverted.");
        await _move.VerifyRevertedAsync(cancellationToken).ConfigureAwait(false);
        if (route.Location == "Legacy") return;
        // A failed content validation must not prevent returning the original bytes to their original
        // location. Normal startup reports the existing content error without inventing replacement data.
        _lease.WriteRoute(_intent with { Location = "Legacy" });
    }

    private string CurrentDirectory() => Directory.Exists(_lease.LegacyDirectory) ? _lease.LegacyDirectory : _lease.CanonicalDirectory;
    private Route EnsureRoute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.EnsureOpen();
        var route = _lease.ReadRoute();
        if (route is null || route.Plan?.Fingerprint != _intent.Plan?.Fingerprint ||
            route.ItemId != _intent.ItemId || route.Journal != _intent.Journal || route.Receipt != _intent.Receipt || route.Inventory != _intent.Inventory)
            throw new IOException("The state migration route changed. Reopen recovery before continuing.");
        return route;
    }
    private void MatchInventory(string inventory)
    {
        if (inventory != _intent.Inventory) throw new IOException("Saved data changed after review. It was preserved; revert this incomplete change and review it again.");
    }
    public void Dispose() { if (_disposed) return; _move.Dispose(); _disposed = true; _lease.ActiveOperation = null; }

    private static void ValidateExistingJournal(string journal, MigrationPlan plan, string itemId)
    {
        var bytes = ReadSmall(journal);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("SchemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("Plan", out var savedPlan) ||
            JsonSerializer.Deserialize<MigrationPlan>(savedPlan, JsonOptions) is not { } reviewed ||
            MigrationPlan.Create(reviewed.Items).Fingerprint != plan.Fingerprint || reviewed.Fingerprint != plan.Fingerprint ||
            !root.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array ||
            !items.EnumerateArray().Any(item => item.TryGetProperty("Id", out var id) && id.GetString() == itemId))
            throw new InvalidDataException("The existing session journal does not match this reviewed state migration.");
    }

    private static void ValidatePlan(StateUseLease lease, MigrationPlan plan, string id)
    {
        if (MigrationPlan.Create(plan.Items).Fingerprint != plan.Fingerprint) throw new InvalidDataException("The migration review changed.");
        var item = plan.Items.Single(item => item.Id == id);
        if (item.Area != MigrationArea.ApplicationState || item.BlockedReason is not null || item.Dependencies?.Count > 0 ||
            !string.Equals(NormalPath(item.CurrentLocation), lease.LegacyDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalPath(item.ProposedLocation), lease.CanonicalDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed application-state change does not match this profile.");
    }

    private static FileStream? AcquireUpdateGuard(string directory)
    {
        var updates = Path.Combine(directory, "Updates"); CheckPath(updates);
        if (!Directory.Exists(updates)) return null;
        // Delete sharing permits the original parent directory to move. Another worker still cannot
        // open this file with its exclusive sharing contract until migration releases this handle.
        return OpenFile(Path.Combine(updates, "update-install.lock"), FileAccess.ReadWrite, 4, create: true);
    }

    private static string InspectState(string directory, IProgress<MigrationProgress>? progress, string itemId, CancellationToken ct)
    {
        CheckPath(directory);
        if (!Directory.Exists(directory)) throw new IOException("The saved application profile is missing.");
        var files = new List<string>(); var folders = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested(); CheckPath(folder);
            foreach (var child in Directory.EnumerateFileSystemEntries(folder))
            {
                CheckPath(child);
                if (Directory.Exists(child)) { folders.Add(child); pending.Push(child); }
                else if (!string.Equals(Path.GetRelativePath(directory, child), @"Updates\update-install.lock", StringComparison.OrdinalIgnoreCase)) files.Add(child);
            }
        }
        files.Sort(StringComparer.Ordinal); folders.Sort(StringComparer.Ordinal);
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
            // Keep every existing database, WAL and saved record fixed against writes and replacement for
            // the entire inspection; all handles close before the directory moves.
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
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var folder in folders) digest.AppendData(Encoding.UTF8.GetBytes("D\0" + Path.GetRelativePath(directory, folder) + "\0"));
            long total = held.Sum(stream => stream.Length), completed = 0;
            var buffer = new byte[128 * 1024];
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                digest.AppendData(Encoding.UTF8.GetBytes("F\0" + Path.GetRelativePath(directory, files[i]) + "\0" + held[i].Length + "\0"));
                int read;
                while ((read = held[i].Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested(); digest.AppendData(buffer.AsSpan(0, read)); completed += read;
                    progress?.Report(new(itemId, "Verifying saved data", Path.GetRelativePath(directory, files[i]), completed, total, "bytes"));
                }
            }
            return Convert.ToHexString(digest.GetHashAndReset());
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

    private static bool Within(string parent, string path) => NormalPath(path).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string NormalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("A fully qualified local profile path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full) || full.Split('\\').Skip(1).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("An ambiguous profile path cannot be used.");
        return full;
    }
    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile migration cannot follow links or junctions."); }
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
            if (error is 32 or 33) throw new MigrationTransientException("Another application or update worker is using the saved data. Waiting for it to finish.");
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

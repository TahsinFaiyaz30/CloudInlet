using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Application;

/// <summary>Durable forward recovery of the local half of a verified cloud migration.
/// The caller holds the state-use lease, pauses other clients by explicit review, and does
/// not create ClientController until this operation completes. Original cloud history stays intact.</summary>
internal sealed class ClientCloudPrefixBinding : ICloudPrefixMigrationBinding, IDisposable
{
    private sealed record Root(string Path, ClientFolderMigration.DirectoryIdentity Identity, string Prefix,
        string Destination, string StateName, bool Canonical, bool Registered, ClientFolderMigration.DirectoryIdentity? ManifestIdentity);
    private sealed record Metadata(string RelativePath, byte[] Original, bool Affected);
    private sealed record Receipt(int Schema, string Fingerprint, string ItemId, string State, AppSettings Settings,
        IReadOnlyList<Root> Roots, IReadOnlyList<Metadata> Metadata, string MaintenanceReceipt, string? MappingHash = null);
    private sealed record ImportReceipt(string SourceVersionId, CloudObject Copied);
    private readonly MigrationItem _item;
    private readonly CloudPrefixKeyMap _map;
    private readonly string _record;
    private readonly FileStream _lease;
    private readonly List<SafeFileHandle> _guards;
    private readonly IClientFolderMigrationRuntime _runtime;
    private readonly Action<string>? _progress;
    private Receipt _receipt;
    private bool _disposed;
    private static readonly byte[] Entropy = "CloudInlet.CloudBinding.v1"u8.ToArray();
    public string MaintenanceReceipt => _receipt.MaintenanceReceipt;
    private string Exchanges => _record + ".metadata";
    private string SettingsPath => Path.Combine(_receipt.State, "settings.json");
    private ClientCloudPrefixBinding(MigrationItem item, Receipt receipt, string record, FileStream lease,
        List<SafeFileHandle> guards, IClientFolderMigrationRuntime runtime, Action<string>? progress)
    { _item = item; _receipt = receipt; _record = record; _lease = lease; _guards = guards; _runtime = runtime; _progress = progress;
        _map = new(item.CurrentLocation, item.ProposedLocation, item.CloudMappings); }

    internal static ClientCloudPrefixBinding Open(MigrationPlan plan, string itemId, string state, string receiptDirectory,
        string maintenanceReceipt, IClientFolderMigrationRuntime? runtime = null, Action<string>? progress = null)
    {
        if (MigrationPlan.Create(plan.Items).Fingerprint != plan.Fingerprint || maintenanceReceipt.Length != 64 || !maintenanceReceipt.All(Uri.IsHexDigit))
            throw new InvalidDataException("The cloud maintenance review is invalid.");
        var item = plan.Items.Single(value => value.Id == itemId);
        if (item.Area != MigrationArea.CloudPrefix || item.BlockedReason is not null) throw new InvalidDataException("This cloud item is unavailable.");
        var map = new CloudPrefixKeyMap(item.CurrentLocation, item.ProposedLocation, item.CloudMappings);
        state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state)); receiptDirectory = Path.GetFullPath(receiptDirectory);
        ClientMigrationReview.EnsureUnlinked(state); ClientMigrationReview.EnsureUnlinked(receiptDirectory); Directory.CreateDirectory(receiptDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Fingerprint + "|" + itemId)));
        var record = Path.Combine(receiptDirectory, key + ".cloud-binding.dpapi");
        ClientMigrationReview.EnsureUnlinked(record); ClientMigrationReview.EnsureUnlinked(record + ".lock");
        var lease = new FileStream(record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var guards = new List<SafeFileHandle>();
        try
        {
            ClientRegistrationMigration.PinAncestors(state, guards); ClientRegistrationMigration.PinAncestors(receiptDirectory, guards);
            MigrationMetadataExchange.Recover(state, record + ".metadata"); runtime ??= new ClientFolderMigration.WindowsRuntime();
            Receipt receipt;
            if (File.Exists(record))
            {
                using var file = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length is < 1 or > 192 * 1024 * 1024) throw new InvalidDataException("The cloud binding receipt is invalid or too large.");
                var data = new byte[(int)file.Length]; file.ReadExactly(data); var plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                try { receipt = JsonSerializer.Deserialize<Receipt>(plain) ?? throw new InvalidDataException("Empty cloud binding receipt."); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (receipt.Schema != 1 || receipt.Fingerprint != plan.Fingerprint || receipt.ItemId != itemId || receipt.State != state ||
                    receipt.MaintenanceReceipt != maintenanceReceipt || receipt.Settings is null || receipt.Roots is null || receipt.Metadata is null)
                    throw new InvalidDataException("The cloud binding receipt does not match this maintenance review.");
                PathRules.ValidateSettings(receipt.Settings);
            }
            else
            {
                var settings = new ClientStorage(state).LoadSettings(); PathRules.ValidateSettings(settings); ClientFolderMigration.CheckPending(state);
                var configured = settings.CustomBackups.Select(value => (Path: value.SourcePath, value.Prefix)).Append((Path: settings.RootPath, settings.Prefix)).ToArray();
                if (!configured.Any(value => value.Prefix == item.CurrentLocation)) throw new IOException("The reviewed cloud prefix no longer belongs to a configured root.");
                // Only the exact saved generated namespaces may receive nested rename rules.
                foreach (var rule in map.Rules)
                    if (!settings.CustomBackups.Any(value => value.Prefix == rule.SourcePrefix && value.Prefix == settings.Prefix + ".cloudbay-backups/" + value.Name + "/" &&
                        rule.DestinationPrefix == item.ProposedLocation + ".cloudinlet-backups/" + value.Name + "/"))
                        throw new InvalidDataException("A nested cloud mapping does not match a saved generated backup.");
                var roots = configured.Where(value => value.Prefix.StartsWith(item.CurrentLocation, StringComparison.Ordinal)).Select(value =>
                {
                    var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Path));
                    var destination = value.Prefix == item.CurrentLocation ? item.ProposedLocation : map.Map(value.Prefix + "__binding__")[..^11];
                    var canonical = SyncRootMigrationIdentity.IsCanonical(settings, path);
                    var stateName = SyncRootMigrationIdentity.StateName(settings, path, value.Prefix);
                    var registered = runtime.IsRegistered(path, settings.AccountId + ":" + settings.BucketId + ":" + value.Prefix, canonical);
                    var manifest = Path.Combine(state, "State", stateName + ".sqlite"); ClientMigrationReview.EnsureUnlinked(manifest);
                    if (Directory.Exists(manifest) || registered && !File.Exists(manifest))
                        throw new IOException("The registered cloud folder has no readable sync baseline. Restore its baseline before migration.");
                    return new Root(path, ClientFolderMigration.ReadIdentity(path), value.Prefix, destination, stateName, canonical,
                        registered, File.Exists(manifest) ? ClientFolderMigration.ReadIdentity(manifest) : null);
                }).ToArray();
                if (roots.Length == 0 || roots.Select(value => value.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
                    throw new InvalidDataException("Cloud migration requires distinct configured local roots.");
                receipt = new(1, plan.Fingerprint, itemId, state, settings, roots, CaptureHistory(state, settings, item.CurrentLocation), maintenanceReceipt);
            }
            foreach (var root in receipt.Roots)
            {
                if (root.StateName != SyncRootMigrationIdentity.StateName(receipt.Settings, root.Path, root.Prefix) ||
                    Prefix(receipt.Settings, root.Path) != root.Prefix || root.Destination != (root.Prefix == item.CurrentLocation ? item.ProposedLocation : map.Map(root.Prefix + "__binding__")[..^11]))
                    throw new InvalidDataException("The protected cloud root does not match its saved settings.");
                ClientRegistrationMigration.PinAncestors(Path.GetDirectoryName(root.Path)!, guards); guards.Add(ClientRegistrationMigration.Pin(root.Path));
                if (ClientFolderMigration.ReadIdentity(root.Path) != root.Identity) throw new IOException("A local cloud folder was replaced. Its current contents were retained.");
                var manifest = Path.Combine(state, "State", root.StateName + ".sqlite");
                ClientMigrationReview.EnsureUnlinked(manifest);
                if (root.ManifestIdentity is not null)
                {
                    ClientRegistrationMigration.PinAncestors(Path.GetDirectoryName(manifest)!, guards);
                    guards.Add(ClientRegistrationMigration.Pin(manifest));
                    if (ClientFolderMigration.ReadIdentity(manifest) != root.ManifestIdentity) throw new IOException("The sync baseline was replaced. Its contents were retained.");
                }
                else if (File.Exists(manifest) || Directory.Exists(manifest) || File.Exists(manifest + "-wal") || File.Exists(manifest + "-shm"))
                    throw new IOException("An unexpected sync baseline appeared during migration. It was retained.");
            }
            if (!File.Exists(record)) Publish(record, receipt, false);
            return new(item, receipt, record, lease, guards, runtime, progress);
        }
        catch { lease.Dispose(); foreach (var guard in guards) guard.Dispose(); throw; }
    }

    public Task VerifyMaintenanceAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        MigrationMetadataExchange.Recover(_receipt.State, Exchanges); ClientFolderMigration.CheckPending(_receipt.State);
        foreach (var root in _receipt.Roots)
        {
            if (ClientFolderMigration.ReadIdentity(root.Path) != root.Identity) throw new IOException("A local cloud folder changed during maintenance.");
            var manifest = Path.Combine(_receipt.State, "State", root.StateName + ".sqlite"); ClientMigrationReview.EnsureUnlinked(manifest);
            if (root.ManifestIdentity is null ? File.Exists(manifest) || Directory.Exists(manifest) || File.Exists(manifest + "-wal") || File.Exists(manifest + "-shm") :
                !File.Exists(manifest) || ClientFolderMigration.ReadIdentity(manifest) != root.ManifestIdentity)
                throw new IOException("The saved sync baseline changed identity during maintenance.");
            if (!root.Registered && Registration(root) is not null) throw new IOException("An unreviewed Windows registration appeared during maintenance.");
        }
        var history = HistoryPaths(_receipt.State).Select(path => Path.GetRelativePath(_receipt.State, path)).Order(StringComparer.OrdinalIgnoreCase);
        if (!history.SequenceEqual(_receipt.Metadata.Select(value => value.RelativePath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Import history files were added or removed during maintenance. Their contents were retained.");
        ValidateSettings(new ClientStorage(_receipt.State).LoadSettings());
        return Task.CompletedTask;
    }
    public async Task VerifyOriginalAsync(CancellationToken token)
    {
        await VerifyMaintenanceAsync(token);
        if (_receipt.MappingHash is not null) throw new IOException("Cloud references are already switching. Continue their saved recovery.");
        var settings = new ClientStorage(_receipt.State).LoadSettings();
        foreach (var root in _receipt.Roots)
        {
            if (Prefix(settings, root.Path) != root.Prefix || !Bindings(settings, root.Path).SequenceEqual(Bindings(_receipt.Settings, root.Path)))
                throw new IOException("The cloud root settings changed after review.");
            if (root.Registered && Registration(root) != false) throw new IOException("The original Windows cloud registration changed.");
        }
        foreach (var metadata in _receipt.Metadata)
            if (!File.ReadAllBytes(MetadataPath(metadata)).AsSpan().SequenceEqual(metadata.Original)) throw new IOException("Import history changed during maintenance.");
    }
    public async Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
    {
        await VerifyMaintenanceAsync(token);
        ValidateMappings(mappings);
        // Prove every local reference can be converted before unregistering any native root.
        ValidateReferences(mappings);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mappings.OrderBy(value => value.Original.FileId, StringComparer.Ordinal))));
        if (_receipt.MappingHash is null)
        {
            await VerifyOriginalAsync(token); var next = _receipt with { MappingHash = digest }; Publish(_record, next, true); _receipt = next;
        }
        else if (_receipt.MappingHash != digest) throw new IOException("The verified cloud mappings changed. Recovery was retained.");
        foreach (var root in _receipt.Roots.Where(value => value.Registered))
            if (Registration(root) == false)
            {
                _progress?.Invoke("Preserving cloud files in " + root.Path);
                await _runtime.PrepareAndUnregisterAsync(root.Path, Account(root.Prefix), root.Canonical, new ClientStorage(_receipt.State).LoadCredentials(),
                    new ProgressForward(_progress), token);
            }
        foreach (var root in _receipt.Roots)
        {
            token.ThrowIfCancellationRequested(); _progress?.Invoke("Updating verified cloud references for " + root.Path);
            var path = Path.Combine(_receipt.State, "State", root.StateName + ".sqlite"); ClientMigrationReview.EnsureUnlinked(path);
            if (File.Exists(path)) new SyncManifest(path).MigrateCloudPrefixReferences(_item.CurrentLocation, _item.ProposedLocation, mappings, _item.CloudMappings);
        }
        foreach (var metadata in _receipt.Metadata)
        {
            token.ThrowIfCancellationRequested(); var path = MetadataPath(metadata); var target = TransformHistory(metadata, mappings);
            var current = File.ReadAllBytes(path);
            if (current.AsSpan().SequenceEqual(target)) continue;
            if (!current.AsSpan().SequenceEqual(metadata.Original)) throw new IOException("Import history changed outside this migration. It was retained.");
            MigrationMetadataExchange.Replace(path, current, target, Exchanges);
        }
        SaveSettings();
        foreach (var root in _receipt.Roots.Where(value => value.Registered))
        {
            if (Registration(root) == true) continue;
            _progress?.Invoke("Restoring Files On-Demand for " + root.Path);
            await _runtime.RegisterAsync(root.Path, Account(root.Destination), true, Path.Combine(_receipt.State, "State", root.StateName + ".sqlite"), token);
        }
        await VerifySwitchedAsync(mappings, token);
    }
    public async Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token)
    {
        await VerifyMaintenanceAsync(token);
        ValidateMappings(mappings);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mappings.OrderBy(value => value.Original.FileId, StringComparer.Ordinal))));
        if (_receipt.MappingHash != digest) throw new IOException("The switched cloud mapping receipt is missing or different.");
        var settings = new ClientStorage(_receipt.State).LoadSettings();
        foreach (var root in _receipt.Roots)
        {
            if (Prefix(settings, root.Path) != root.Destination || !Bindings(settings, root.Path).SequenceEqual([NewBinding(root)]))
                throw new IOException("The cloud prefix and sync baseline still need recovery.");
            if (root.Registered && Registration(root) != true) throw new IOException("The new Windows cloud registration still needs recovery.");
            var path = Path.Combine(_receipt.State, "State", root.StateName + ".sqlite");
            if (File.Exists(path))
            {
                var manifest = new SyncManifest(path);
                foreach (var remote in manifest.ReadAll().Values.Select(value => value.Remote).Concat(manifest.ReadDirectories().Values.Select(value => value.Remote)))
                    if (!mappings.Any(value => value.Migrated.FileId == remote.FileId && value.Migrated.Key == remote.Key && value.Migrated.Size == remote.Size &&
                        value.Migrated.Action == remote.Action && string.Equals(value.Migrated.Sha1, remote.Sha1, StringComparison.OrdinalIgnoreCase))) throw new IOException("A sync baseline still refers to an unverified cloud version.");
            }
        }
        foreach (var metadata in _receipt.Metadata)
            if (!File.ReadAllBytes(MetadataPath(metadata)).AsSpan().SequenceEqual(TransformHistory(metadata, mappings))) throw new IOException("Import history still needs migration recovery.");
    }
    private string Account(string prefix) => _receipt.Settings.AccountId + ":" + _receipt.Settings.BucketId + ":" + prefix;
    private bool? Registration(Root root)
    {
        try { if (_runtime.IsRegistered(root.Path, Account(root.Destination), true)) return true; }
        catch (IOException) { if (_runtime.IsRegistered(root.Path, Account(root.Prefix), root.Canonical)) return false; throw; }
        return _runtime.IsRegistered(root.Path, Account(root.Prefix), root.Canonical) ? false : null;
    }
    private static string Prefix(AppSettings settings, string root) => Same(settings.RootPath, root) ? settings.Prefix :
        settings.CustomBackups.Single(value => Same(value.SourcePath, root)).Prefix;
    private static SyncRootBinding[] Bindings(AppSettings settings, string root) => settings.RootBindings.Where(value => Same(value.RootPath, root)).ToArray();
    private SyncRootBinding NewBinding(Root root) => new(root.Path, root.StateName, true, _receipt.Settings.AccountId, _receipt.Settings.BucketId, root.Destination);
    private void ValidateSettings(AppSettings settings)
    {
        PathRules.ValidateSettings(settings);
        if (settings.AccountId != _receipt.Settings.AccountId || settings.BucketId != _receipt.Settings.BucketId || settings.KeyId != _receipt.Settings.KeyId || settings.RootPath != _receipt.Settings.RootPath ||
            settings.CustomBackups.Count != _receipt.Settings.CustomBackups.Count || settings.CustomBackups.Any(value => !_receipt.Settings.CustomBackups.Any(old => old.Name == value.Name && old.SourcePath == value.SourcePath)))
            throw new IOException("The account or configured folders changed during cloud maintenance.");
        foreach (var root in _receipt.Roots)
            if (Prefix(settings, root.Path) != root.Prefix && Prefix(settings, root.Path) != root.Destination ||
                !Bindings(settings, root.Path).SequenceEqual(Bindings(_receipt.Settings, root.Path)) && !Bindings(settings, root.Path).SequenceEqual([NewBinding(root)]))
                throw new IOException("The saved cloud root identity changed outside migration.");
        if (!_receipt.Roots.Any(root => Same(root.Path, settings.RootPath)) && settings.Prefix != _receipt.Settings.Prefix ||
            settings.CustomBackups.Where(value => !_receipt.Roots.Any(root => Same(root.Path, value.SourcePath))).Any(value =>
                _receipt.Settings.CustomBackups.Single(old => Same(old.SourcePath, value.SourcePath)).Prefix != value.Prefix))
            throw new IOException("An unrelated cloud prefix changed during maintenance.");
    }
    private void SaveSettings()
    {
        ClientMigrationReview.EnsureUnlinked(SettingsPath); var before = File.ReadAllBytes(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(before) ?? throw new InvalidDataException("Empty settings."); ValidateSettings(settings);
        var main = _receipt.Roots.SingleOrDefault(value => Same(value.Path, settings.RootPath));
        var updated = settings with { Prefix = main?.Destination ?? settings.Prefix,
            CustomBackups = settings.CustomBackups.Select(value => _receipt.Roots.SingleOrDefault(root => Same(root.Path, value.SourcePath)) is { } root ? value with { Prefix = root.Destination } : value).ToList(),
            RootBindings = settings.RootBindings.Where(value => !_receipt.Roots.Any(root => Same(root.Path, value.RootPath))).Concat(_receipt.Roots.Select(NewBinding)).ToList() };
        PathRules.ValidateSettings(updated);
        var after = JsonSerializer.SerializeToUtf8Bytes(updated, new JsonSerializerOptions { WriteIndented = true });
        if (!before.AsSpan().SequenceEqual(after)) MigrationMetadataExchange.Replace(SettingsPath, before, after, Exchanges);
    }
    private static IReadOnlyList<Metadata> CaptureHistory(string state, AppSettings settings, string prefix)
    {
        var result = new List<Metadata>(); long size = 0;
        foreach (var kind in new[] { "Imports", "CloudImports" })
        {
            var directory = Path.Combine(state, kind); ClientMigrationReview.EnsureUnlinked(directory); if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                ClientMigrationReview.EnsureUnlinked(path); var bytes = ReadBounded(path, kind == "Imports" ? 1024 * 1024 : 64 * 1024 * 1024, ref size);
                if (size > 64 * 1024 * 1024) throw new IOException("Import history exceeds the cloud migration metadata limit.");
                string identity;
                if (kind == "Imports")
                { var record = JsonSerializer.Deserialize<FolderImportRecord>(bytes) ?? throw new InvalidDataException("Unreadable import history.");
                    if (record.State != "Completed" || !Path.GetFileNameWithoutExtension(path).Equals(record.Id, StringComparison.OrdinalIgnoreCase) || new ImportJournal(state).Read(recordId: record.Id).SingleOrDefault() is null) throw new IOException("Finish interrupted import history before migrating cloud prefixes."); identity = record.DestinationIdentity; }
                else
                { var record = JsonSerializer.Deserialize<CloudImportRecord>(bytes) ?? throw new InvalidDataException("Unreadable cloud import history.");
                    var history = new CloudImportJournal(state); var validated = history.Read(recordId: record.Id).SingleOrDefault();
                    if (record.State != "Completed" || !Path.GetFileNameWithoutExtension(path).Equals(record.Id, StringComparison.OrdinalIgnoreCase) || validated is null || history.SkippedRecords != 0) throw new IOException("Resolve interrupted or damaged cloud import history before migration."); identity = record.DestinationIdentity; }
                var parts = identity.Split('|');
                var affected = parts.Length == 4 && parts[0] == settings.AccountId && parts[1] == settings.BucketId && parts[2].StartsWith(prefix, StringComparison.Ordinal);
                result.Add(new(Path.GetRelativePath(state, path), bytes, affected));
                if (kind == "CloudImports")
                {
                    var receipts = Path.Combine(directory, Path.GetFileNameWithoutExtension(path)); ClientMigrationReview.EnsureUnlinked(receipts);
                    if (!Directory.Exists(receipts)) continue;
                    foreach (var child in Directory.EnumerateFiles(receipts, "*.json"))
                    { ClientMigrationReview.EnsureUnlinked(child); var data = ReadBounded(child, 64 * 1024, ref size);
                        if (size > 64 * 1024 * 1024) throw new IOException("Cloud import receipts exceed the migration metadata limit."); result.Add(new(Path.GetRelativePath(state, child), data, affected)); }
                }
            }
        }
        if (!HistoryPaths(state).Select(path => Path.GetRelativePath(state, path)).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            result.Select(value => value.RelativePath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Unrecognized import recovery files need review before cloud migration.");
        return result;
    }
    private byte[] TransformHistory(Metadata metadata, IReadOnlyList<CloudPrefixVersionMapping> mappings)
    {
        if (!metadata.Affected) return metadata.Original;
        string Identity(string identity)
        { var parts = identity.Split('|'); parts[2] = parts[2] == _item.CurrentLocation ? _item.ProposedLocation : _map.Map(parts[2] + "__binding__")[..^11]; return string.Join('|', parts); }
        CloudObject Remote(CloudObject old)
        {
            var match = mappings.SingleOrDefault(value => value.Original.FileId == old.FileId && value.Original.Key == old.Key && value.Original.Size == old.Size && value.Original.Action == old.Action &&
                string.Equals(value.Original.Sha1, old.Sha1, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("An import receipt has no verified cloud migration mapping.");
            return old with { FileId = match.Migrated.FileId, Key = match.Migrated.Key };
        }
        if (metadata.RelativePath.StartsWith("Imports" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { var record = JsonSerializer.Deserialize<FolderImportRecord>(metadata.Original)!; return JsonSerializer.SerializeToUtf8Bytes(record with { DestinationIdentity = Identity(record.DestinationIdentity) }); }
        if (metadata.RelativePath.Split(Path.DirectorySeparatorChar).Length == 2)
        { var record = JsonSerializer.Deserialize<CloudImportRecord>(metadata.Original)!;
            return JsonSerializer.SerializeToUtf8Bytes(record with { DestinationIdentity = Identity(record.DestinationIdentity), Completed = record.Completed.ToDictionary(value => value.Key, value => Remote(value.Value), StringComparer.Ordinal) }); }
        var receipt = JsonSerializer.Deserialize<ImportReceipt>(metadata.Original) ?? throw new InvalidDataException("Unreadable import receipt.");
        return JsonSerializer.SerializeToUtf8Bytes(receipt with { Copied = Remote(receipt.Copied) });
    }
    private string MetadataPath(Metadata metadata)
    {
        var path = Path.GetFullPath(Path.Combine(_receipt.State, metadata.RelativePath));
        if (!path.StartsWith(_receipt.State + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Import history escaped application data.");
        ClientMigrationReview.EnsureUnlinked(path); return path;
    }
    private static byte[] ReadBounded(string path, int limit, ref long total)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > limit || file.Length > 64L * 1024 * 1024 - total)
            throw new IOException("Import history exceeds the supported migration metadata limit.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); total += bytes.Length; return bytes;
    }
    private static IEnumerable<string> HistoryPaths(string state)
    {
        IEnumerable<string> Walk(string directory)
        {
            ClientMigrationReview.EnsureUnlinked(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                ClientMigrationReview.EnsureUnlinked(child);
                if (Directory.Exists(child)) { foreach (var file in Walk(child)) yield return file; }
                else if (Path.GetExtension(child).Equals(".json", StringComparison.OrdinalIgnoreCase)) yield return child;
            }
        }
        foreach (var kind in new[] { "Imports", "CloudImports" })
        { var directory = Path.Combine(state, kind); ClientMigrationReview.EnsureUnlinked(directory); if (Directory.Exists(directory)) foreach (var path in Walk(directory)) yield return path; }
    }
    private void ValidateMappings(IReadOnlyList<CloudPrefixVersionMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var original = new HashSet<string>(StringComparer.Ordinal); var migrated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
            if (mapping is not { Original: { } before, Migrated: { } after } || string.IsNullOrWhiteSpace(before.FileId) || string.IsNullOrWhiteSpace(after.FileId) ||
                before.FileId == after.FileId || after.Key != _map.Map(before.Key) || before.Size < 0 || before.Size != after.Size ||
                before.Action is not ("upload" or "hide") || before.Action != after.Action || !string.Equals(before.Sha1, after.Sha1, StringComparison.OrdinalIgnoreCase) ||
                !original.Add(before.FileId) || !migrated.Add(after.FileId)) throw new InvalidDataException("The cloud version mapping does not preserve verified source identity.");
        if (original.Overlaps(migrated)) throw new InvalidDataException("Cloud migration cannot reuse original version identities.");
        _map.ValidateDistinctKeys(mappings.Select(value => value.Original.Key));
    }
    private void ValidateReferences(IReadOnlyList<CloudPrefixVersionMapping> mappings)
    {
        foreach (var root in _receipt.Roots.Where(value => value.ManifestIdentity is not null))
        {
            var manifest = new SyncManifest(Path.Combine(_receipt.State, "State", root.StateName + ".sqlite"));
            foreach (var remote in manifest.ReadAll().Values.Select(value => value.Remote).Concat(manifest.ReadDirectories().Values.Select(value => value.Remote)))
                if (!mappings.Any(mapping => new[] { mapping.Original, mapping.Migrated }.Any(value =>
                    value.FileId == remote.FileId && value.Key == remote.Key && value.Size == remote.Size && value.Action == remote.Action && string.Equals(value.Sha1, remote.Sha1, StringComparison.OrdinalIgnoreCase))))
                    throw new IOException("A local sync baseline has no verified cloud mapping. Its contents were retained.");
        }
        foreach (var metadata in _receipt.Metadata)
        {
            var target = TransformHistory(metadata, mappings); var current = File.ReadAllBytes(MetadataPath(metadata));
            if (!current.AsSpan().SequenceEqual(metadata.Original) && !current.AsSpan().SequenceEqual(target))
                throw new IOException("Import history changed outside this migration. Its contents were retained.");
        }
    }
    private static void Publish(string path, Receipt receipt, bool overwrite)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(receipt); byte[] data;
        try { data = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); } finally { CryptographicOperations.ZeroMemory(plain); }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); } File.Move(temp, path, overwrite);
    }
    private static bool Same(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private sealed class ProgressForward(Action<string>? callback) : IProgress<string> { public void Report(string value) => callback?.Invoke(value); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lease.Dispose(); foreach (var guard in _guards) guard.Dispose(); }
}

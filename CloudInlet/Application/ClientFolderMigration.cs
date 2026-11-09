using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;
using CloudInlet.Windows.CloudFiles;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Windows.Storage.Provider;

namespace CloudInlet.Application;

internal interface IClientFolderMigrationRuntime
{
    bool IsRegistered(string root, string account, bool canonical);
    Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials,
        IProgress<string> progress, CancellationToken cancellationToken);
    Task RegisterAsync(string root, string account, bool canonical, string manifestPath, CancellationToken cancellationToken);
    string GetKnownFolder(string name);
    void ExchangeKnownFolder(string name, string expected, string replacement);
}

/// <summary>Run before the controller exists. The protected recovery record precedes placeholder
/// preparation. Recovery identifies the original directory object; no payload is copied or deleted.</summary>
internal sealed partial class ClientFolderMigration : IMigrationOperation, IDisposable
{
    internal sealed record DirectoryIdentity(uint Volume, uint High, uint Low, long Created);
    private sealed record MetadataChange(string RelativePath, byte[] Before, byte[] After);
    private sealed record Receipt(int Schema, string Fingerprint, string ItemId, string StateDirectory,
        string Source, string Destination, DirectoryIdentity Identity, AppSettings Settings, string Prefix,
        string? BackupName, bool Registered, IReadOnlyList<MetadataChange> Metadata, string? Phase = null);
    private static readonly byte[] Entropy = "CloudInlet.FolderMigration.v1"u8.ToArray();
    private readonly MigrationPlan _plan;
    private Receipt _receipt;
    private readonly string _directoryReceipt;
    private readonly IClientFolderMigrationRuntime _runtime;
    private readonly FileStream _lease;
    private readonly List<SafeFileHandle> _guards;
    private WindowsDirectoryRelocation? _move;
    private bool _disposed;
    private string State => _receipt.StateDirectory;
    private string MetadataRecords => _directoryReceipt + ".metadata";
    private string Account => _receipt.Settings.AccountId + ":" + _receipt.Settings.BucketId + ":" + _receipt.Prefix;
    public string ItemId => _receipt.ItemId;
    public string ReviewedPlanFingerprint => _receipt.Fingerprint;
    public bool CanRevert => !_disposed;
    private ClientFolderMigration(MigrationPlan plan, Receipt receipt, string directoryReceipt,
        IClientFolderMigrationRuntime runtime, FileStream lease, List<SafeFileHandle> guards)
    { _plan = plan; _receipt = receipt; _directoryReceipt = directoryReceipt; _runtime = runtime; _lease = lease; _guards = guards; }

    internal static ClientFolderMigration Create(MigrationPlan plan, string itemId, string stateDirectory,
        string receiptDirectory, IClientFolderMigrationRuntime? runtime = null) => Open(plan, itemId, stateDirectory, receiptDirectory, runtime, true);
    internal static ClientFolderMigration Resume(MigrationPlan plan, string itemId, string stateDirectory,
        string receiptDirectory, IClientFolderMigrationRuntime? runtime = null) => Open(plan, itemId, stateDirectory, receiptDirectory, runtime, false);

    internal static bool HasRecovery(MigrationPlan plan, string itemId, string receiptDirectory)
    {
        var path = ReceiptPath(plan, itemId, receiptDirectory); CheckLinks(path);
        return File.Exists(path);
    }
    internal static bool CanAcknowledgeUnstarted(MigrationPlan plan, string itemId, string receiptDirectory)
    {
        var path = ReceiptPath(plan, itemId, receiptDirectory); CheckLinks(path);
        var item = plan.Items.Single(value => value.Id == itemId);
        CheckLinks(item.ProposedLocation);
        // No adapter mutates folders, mappings or metadata before its protected
        // receipt is published. A creation failure can safely be dismissed here.
        return !File.Exists(path) && Directory.Exists(item.CurrentLocation) &&
            !Directory.Exists(item.ProposedLocation) && !File.Exists(item.ProposedLocation);
    }
    internal static Task AcknowledgeUnstartedAsync(MigrationPlan plan, string itemId, string receiptDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = ReceiptPath(plan, itemId, receiptDirectory); CheckLinks(path + ".lock");
        Directory.CreateDirectory(receiptDirectory);
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!CanAcknowledgeUnstarted(plan, itemId, receiptDirectory))
            throw new IOException("This folder needs recovery from its saved migration record before it can be reverted.");
        return Task.CompletedTask;
    }
    private static string ReceiptKey(MigrationPlan plan, string itemId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Fingerprint + "|" + itemId)));
    private static string ReceiptPath(MigrationPlan plan, string itemId, string directory) =>
        Path.Combine(Full(directory), ReceiptKey(plan, itemId) + ".folder.dpapi");

    private static ClientFolderMigration Open(MigrationPlan plan, string itemId, string stateDirectory,
        string receiptDirectory, IClientFolderMigrationRuntime? runtime, bool create)
    {
        runtime ??= new WindowsRuntime();
        if (MigrationPlan.Create(plan.Items).Fingerprint != plan.Fingerprint) throw new InvalidDataException("The folder migration review changed.");
        var item = plan.Items.Single(value => value.Id == itemId);
        if (item.BlockedReason is not null || item.Area is not (MigrationArea.SyncFolder or MigrationArea.BackupFolder))
            throw new InvalidOperationException("This is not an available personal-folder migration.");
        var source = Full(item.CurrentLocation); var destination = Full(item.ProposedLocation);
        var oldLeaf = BuildInfo.Flavor == "Debug" ? "CloudBay Debug" : "CloudBay";
        var newLeaf = BuildInfo.Flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet";
        if (!Path.GetFileName(source).Equals(oldLeaf, StringComparison.OrdinalIgnoreCase) ||
            !Same(destination, Path.Combine(Path.GetDirectoryName(source)!, newLeaf)))
            throw new InvalidDataException("Only the reviewed CloudBay folder can be renamed to its CloudInlet sibling.");
        var state = Full(stateDirectory); var records = Full(receiptDirectory);
        if (Within(source, state) || Within(destination, state) || Within(source, records) || Within(destination, records))
            throw new IOException("Recovery records and application data must be outside the personal folder.");
        CheckLinks(state); CheckLinks(records); Directory.CreateDirectory(records);
        var key = ReceiptKey(plan, itemId);
        var recordPath = Path.Combine(records, key + ".folder.dpapi");
        CheckLinks(recordPath); CheckLinks(recordPath + ".lock");
        var lease = new FileStream(recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var guards = new List<SafeFileHandle>();
        try
        {
            // Keep every ancestor fixed while reviewing/mutating paths. Read/list access without
            // delete sharing is required: attribute-only handles do not prevent parent renames.
            foreach (var guarded in new[] { state, records, Path.GetDirectoryName(source)! }.Distinct(StringComparer.OrdinalIgnoreCase))
                for (var current = guarded; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    CheckLinks(current);
                    var handle = CreateFileW(@"\\?\" + current, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    guards.Add(handle);
                }
            MigrationMetadataExchange.Recover(state, Path.Combine(records, key + ".directory.dpapi.metadata"));
            Receipt receipt;
            if (create)
            {
                var settings = new ClientStorage(state).LoadSettings(); PathRules.ValidateSettings(settings);
                string prefix; string? backupName;
                if (item.Area == MigrationArea.SyncFolder && Same(settings.RootPath, source))
                { prefix = settings.Prefix; backupName = null; }
                else if (item.Area == MigrationArea.BackupFolder && settings.CustomBackups.SingleOrDefault(value => Same(value.SourcePath, source)) is { } backup)
                { prefix = backup.Prefix; backupName = backup.Name; }
                else throw new IOException("The saved folder changed after review. Review migration again.");
                foreach (var other in settings.CustomBackups.Select(value => value.SourcePath).Append(settings.RootPath))
                    if (!Same(other, source) && (Within(source, other) || Within(other, source) || Within(destination, other) || Within(other, destination)))
                        throw new IOException("This folder overlaps another configured backup. Separate the backup locations before migrating; unchecked folders were retained.");
                if (!Directory.Exists(source))
                    throw new IOException("The reviewed source folder is missing. No replacement folder was created.");
                CheckPending(state);
                var account = settings.AccountId + ":" + settings.BucketId + ":" + prefix;
                var registered = runtime.IsRegistered(source, account, SyncRootMigrationIdentity.IsCanonical(settings, source));
                if (!registered) CheckLinks(source);
                foreach (var folder in settings.Backups.Where(folder => Within(source, folder.DestinationPath)))
                    if (!Same(runtime.GetKnownFolder(folder.Name), folder.DestinationPath))
                        throw new IOException("The Windows " + folder.Name + " mapping changed. Review that backup before migrating.");
                receipt = new(2, plan.Fingerprint, itemId, state, source, destination, ReadIdentity(source), settings,
                    prefix, backupName, registered, CaptureHistory(state, settings, source, destination), "Ready");
                PublishProtected(recordPath, JsonSerializer.SerializeToUtf8Bytes(receipt));
            }
            else
            {
                var bytes = File.ReadAllBytes(recordPath);
                if (bytes.Length > 192 * 1024 * 1024) throw new InvalidDataException("The folder recovery record exceeds its supported metadata size.");
                var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                try { receipt = JsonSerializer.Deserialize<Receipt>(plain) ?? throw new InvalidDataException("The folder recovery record is empty."); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (receipt.Schema is not (1 or 2) || receipt.Schema == 2 && receipt.Phase is not ("Ready" or "Started") ||
                    receipt.Fingerprint != plan.Fingerprint || receipt.ItemId != itemId ||
                    !Same(receipt.StateDirectory, state) || !Same(receipt.Source, source) || !Same(receipt.Destination, destination) ||
                    receipt.Metadata is null || receipt.Settings is null)
                    throw new InvalidDataException("The folder recovery record does not match this review.");
                PathRules.ValidateSettings(receipt.Settings);
            }
            // Return the receipt-backed operation even if live preflight will fail.
            // The visible session runs CheckAsync; a never-started item can then be
            // declined without deleting an unrelated destination that appeared meanwhile.
            return new ClientFolderMigration(plan, receipt, Path.Combine(records, key + ".directory.dpapi"), runtime, lease, guards);
        }
        catch { foreach (var guard in guards) guard.Dispose(); lease.Dispose(); throw; }
    }

    public Task CheckAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
        MigrationMetadataExchange.Recover(State, MetadataRecords);
        var original = LocateOriginalFolder(cancellationToken);
        if (!_receipt.Registered && _runtime.IsRegistered(original,
            Account, Same(original, _receipt.Source) ? SyncRootMigrationIdentity.IsCanonical(_receipt.Settings, _receipt.Source) : true))
            throw new IOException("A Windows cloud registration appeared after review. It was retained; review this folder again.");
        CheckPending(State); ValidateSettingsOwnership(new ClientStorage(State).LoadSettings());
        foreach (var change in _receipt.Metadata) RequireEither(MetadataPath(change), change.Before, change.After);
        foreach (var folder in Mappings())
        {
            var current = _runtime.GetKnownFolder(folder.Name);
            if (!Same(current, folder.DestinationPath) && !Same(current, Rebase(folder.DestinationPath)))
                throw new IOException("The Windows " + folder.Name + " mapping changed outside this migration. Its new mapping was retained.");
        }
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        await PreserveRecreatedFolderAsync(progress, cancellationToken);
        if (_receipt.Schema == 2 && _receipt.Phase == "Ready")
        {
            var started = _receipt with { Phase = "Started" };
            // Persist the transition before any hydration, unregistration, directory
            // move or settings mutation. Missing/tampered receipts never mean Ready.
            PublishProtected(ReceiptPath(_plan, ItemId, Path.GetDirectoryName(_directoryReceipt)!),
                JsonSerializer.SerializeToUtf8Bytes(started), overwrite: true);
            _receipt = started;
        }
        if (Directory.Exists(_receipt.Source))
        {
            Report(progress, "Preserving cloud files", "Downloading and verifying online-only files before changing the Windows registration.");
            if (_receipt.Registered) await _runtime.PrepareAndUnregisterAsync(_receipt.Source, Account,
                SyncRootMigrationIdentity.IsCanonical(_receipt.Settings, _receipt.Source), new ClientStorage(State).LoadCredentials(),
                new InlineProgress<string>(detail => Report(progress, "Preserving cloud files", detail)), cancellationToken);
            if (ReadIdentity(_receipt.Source) != _receipt.Identity) throw new IOException("The source folder changed during Windows preparation.");
            _move ??= File.Exists(_directoryReceipt) ? WindowsDirectoryRelocation.Resume(_plan, ItemId, _directoryReceipt)
                : WindowsDirectoryRelocation.Create(_plan, ItemId, _directoryReceipt);
            await _move.ApplyAsync(progress, cancellationToken);
        }
        Report(progress, "Updating folder references", "Updating saved settings and completed history belonging to this folder.");
        ApplyMetadata(false);
        foreach (var folder in Mappings())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, "Updating Windows folder", folder.Name + ": " + Rebase(folder.DestinationPath));
            _runtime.ExchangeKnownFolder(folder.Name, folder.DestinationPath, Rebase(folder.DestinationPath));
        }
        SaveSettings(false);
        if (_receipt.Registered)
        {
            Report(progress, "Restoring Files On-Demand", "Registering CloudInlet with the preserved sync baseline.");
            await _runtime.RegisterAsync(_receipt.Destination, Account, true, ManifestPath(), cancellationToken);
        }
        await VerifyAsync(cancellationToken);
        Report(progress, "Folder migration verified", "The original directory, settings, history and Windows mappings are verified.", 1, 1);
    }

    public Task VerifyAsync(CancellationToken cancellationToken) => Verify(false, cancellationToken);
    public async Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        if (_receipt.Schema == 2 && _receipt.Phase == "Ready")
        {
            await VerifyRevertedAsync(cancellationToken);
            Report(progress, "Previous folder retained", "This item stopped before making changes. The original folder is verified; other locations were left untouched.", 1, 1);
            return;
        }
        await CheckAsync(cancellationToken);
        await PreserveRecreatedFolderAsync(progress, cancellationToken);
        if (Directory.Exists(_receipt.Destination))
        {
            Report(progress, "Restoring this folder", "Preserving cloud files before restoring only this incomplete item.");
            if (_receipt.Registered) await _runtime.PrepareAndUnregisterAsync(_receipt.Destination, Account, true, new ClientStorage(State).LoadCredentials(),
                new InlineProgress<string>(detail => Report(progress, "Restoring this folder", detail)), cancellationToken);
            _move ??= WindowsDirectoryRelocation.Resume(_plan, ItemId, _directoryReceipt);
            await _move.RevertAsync(progress, cancellationToken);
        }
        ApplyMetadata(true);
        foreach (var folder in Mappings()) _runtime.ExchangeKnownFolder(folder.Name, Rebase(folder.DestinationPath), folder.DestinationPath);
        SaveSettings(true);
        if (_receipt.Registered) await _runtime.RegisterAsync(_receipt.Source, Account,
            SyncRootMigrationIdentity.IsCanonical(_receipt.Settings, _receipt.Source), ManifestPath(), cancellationToken);
        await VerifyRevertedAsync(cancellationToken);
        Report(progress, "Previous folder restored", "This item's previous location is verified. Other migration items were retained.", 1, 1);
    }
    public Task VerifyRevertedAsync(CancellationToken cancellationToken) => Verify(true, cancellationToken);
    private Task Verify(bool reverted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = reverted ? _receipt.Source : _receipt.Destination;
        var absent = reverted ? _receipt.Destination : _receipt.Source;
        var unchangedPreflight = reverted && _receipt.Schema == 2 && _receipt.Phase == "Ready";
        if ((!unchangedPreflight && Directory.Exists(absent)) || !Directory.Exists(expected) || ReadIdentity(expected) != _receipt.Identity)
            throw new IOException("The original folder is not at its verified expected location.");
        var settings = new ClientStorage(State).LoadSettings(); ValidateSettingsOwnership(settings);
        var root = _receipt.BackupName is null ? settings.RootPath : settings.CustomBackups.Single(value => value.Name == _receipt.BackupName).SourcePath;
        if (!Same(root, expected)) throw new IOException("The saved folder location has not yet been committed.");
        foreach (var folder in Mappings())
            if (!Same(_runtime.GetKnownFolder(folder.Name), reverted ? folder.DestinationPath : Rebase(folder.DestinationPath)))
                throw new IOException("The Windows " + folder.Name + " mapping still needs recovery.");
        foreach (var change in _receipt.Metadata)
            if (!File.ReadAllBytes(MetadataPath(change)).AsSpan().SequenceEqual(reverted ? change.Before : change.After))
                throw new IOException("A completed import history entry still needs recovery.");
        if (_receipt.Registered && !_runtime.IsRegistered(expected, Account,
            !reverted || SyncRootMigrationIdentity.IsCanonical(_receipt.Settings, _receipt.Source)))
            throw new IOException("The Windows Files On-Demand registration still needs recovery.");
        return Task.CompletedTask;
    }

    private IEnumerable<BackupFolder> Mappings() => _receipt.Settings.Backups.Where(folder => Within(_receipt.Source, folder.DestinationPath));
    private string Rebase(string path) => Same(path, _receipt.Source) ? _receipt.Destination : Path.Combine(_receipt.Destination, Path.GetRelativePath(_receipt.Source, path));
    private string ManifestPath() => Path.Combine(State, "State", SyncRootMigrationIdentity.StateName(_receipt.Settings, _receipt.Source, _receipt.Prefix) + ".sqlite");
    private void ValidateSettingsOwnership(AppSettings current)
    {
        PathRules.ValidateSettings(current);
        if (current.AccountId != _receipt.Settings.AccountId || current.BucketId != _receipt.Settings.BucketId || current.Prefix != _receipt.Settings.Prefix)
            throw new IOException("The account or cloud namespace changed. Restore this migration before changing accounts.");
        var actual = _receipt.BackupName is null ? current.RootPath : current.CustomBackups.SingleOrDefault(value => value.Name == _receipt.BackupName)?.SourcePath;
        if (actual is null || !Same(actual, _receipt.Source) && !Same(actual, _receipt.Destination))
            throw new IOException("The saved personal folder changed outside this migration. Its new setting was retained.");
        if (_receipt.BackupName is not null && current.CustomBackups.Single(value => value.Name == _receipt.BackupName).Prefix != _receipt.Prefix)
            throw new IOException("This custom backup's cloud namespace changed. Review its settings before retrying.");
        foreach (var expected in Mappings())
        {
            var actualFolder = current.Backups.SingleOrDefault(folder => folder.Name == expected.Name);
            if (actualFolder is null || actualFolder.OriginalPath != expected.OriginalPath ||
                !Same(actualFolder.DestinationPath, expected.DestinationPath) && !Same(actualFolder.DestinationPath, Rebase(expected.DestinationPath)))
                throw new IOException("The saved Windows folder backup changed outside this migration.");
        }
        if (current.Backups.Count(folder => Within(_receipt.Source, folder.DestinationPath) || Within(_receipt.Destination, folder.DestinationPath)) != Mappings().Count())
            throw new IOException("The set of Windows folders in this backup changed after review. Review those mappings before retrying.");
        var affectedBindings = current.RootBindings.Where(binding => Same(binding.RootPath, _receipt.Source) || Same(binding.RootPath, _receipt.Destination)).ToArray();
        var originalBindings = _receipt.Settings.RootBindings.Where(binding => Same(binding.RootPath, _receipt.Source)).ToArray();
        var migratedBinding = new SyncRootBinding(_receipt.Destination,
            SyncRootMigrationIdentity.StateName(_receipt.Settings, _receipt.Source, _receipt.Prefix), true,
            _receipt.Settings.AccountId, _receipt.Settings.BucketId, _receipt.Prefix);
        if (!affectedBindings.SequenceEqual(originalBindings) && !affectedBindings.SequenceEqual([migratedBinding]))
            throw new IOException("The saved sync baseline changed outside this migration. Its current identity was retained.");
    }
    private void SaveSettings(bool reverted)
    {
        var path = Path.Combine(State, "settings.json"); CheckLinks(path);
        var before = File.ReadAllBytes(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(before) ?? throw new InvalidDataException("Saved settings are empty.");
        ValidateSettingsOwnership(settings);
        var from = reverted ? _receipt.Destination : _receipt.Source;
        var to = reverted ? _receipt.Source : _receipt.Destination;
        string Map(string value) => Same(value, from) ? to : Within(from, value) ? Path.Combine(to, Path.GetRelativePath(from, value)) : value;
        var bindings = settings.RootBindings.Where(binding => !Same(binding.RootPath, _receipt.Source) && !Same(binding.RootPath, _receipt.Destination)).ToList();
        if (reverted) bindings.AddRange(_receipt.Settings.RootBindings.Where(binding => Same(binding.RootPath, _receipt.Source)));
        else bindings.Add(new(_receipt.Destination, SyncRootMigrationIdentity.StateName(_receipt.Settings, _receipt.Source, _receipt.Prefix), true, _receipt.Settings.AccountId, _receipt.Settings.BucketId, _receipt.Prefix));
        var updated = settings with
        {
            RootPath = _receipt.BackupName is null ? to : settings.RootPath,
            CustomBackups = settings.CustomBackups.Select(folder => folder.Name == _receipt.BackupName ? folder with { SourcePath = to } : folder).ToList(),
            Backups = settings.Backups.Select(folder => folder with { DestinationPath = Map(folder.DestinationPath) }).ToList(),
            SelectedExclusions = settings.SelectedExclusions.Select(value => value with { RootPath = Map(value.RootPath) }).ToList(),
            GuidedExclusions = settings.GuidedExclusions.Select(value => value.RootPath is null ? value : value with { RootPath = Map(value.RootPath) }).ToList(),
            RootBindings = bindings
        };
        PathRules.ValidateSettings(updated);
        MigrationMetadataExchange.Replace(path, before, JsonSerializer.SerializeToUtf8Bytes(updated, new JsonSerializerOptions { WriteIndented = true }), MetadataRecords);
    }
    private void ApplyMetadata(bool reverted)
    {
        foreach (var change in _receipt.Metadata)
        {
            var before = reverted ? change.After : change.Before; var after = reverted ? change.Before : change.After;
            var path = MetadataPath(change); RequireEither(path, before, after);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(after)) MigrationMetadataExchange.Replace(path, before, after, MetadataRecords);
        }
    }
    private string MetadataPath(MetadataChange change)
    {
        var path = Full(Path.Combine(State, change.RelativePath));
        if (!Within(State, path) || Same(path, State)) throw new InvalidDataException("A migration metadata path escaped application data.");
        CheckLinks(path); return path;
    }
    private static IReadOnlyList<MetadataChange> CaptureHistory(string state, AppSettings settings, string source, string destination)
    {
        var changes = new List<MetadataChange>(); long bytes = 0;
        var oldIdentity = settings.AccountId + "|" + settings.BucketId + "|" + settings.Prefix + "|" + settings.RootPath.ToUpperInvariant();
        var newIdentity = settings.AccountId + "|" + settings.BucketId + "|" + settings.Prefix + "|" + destination.ToUpperInvariant();
        foreach (var kind in new[] { "Imports", "CloudImports" })
        {
            var directory = Path.Combine(state, kind); if (!Directory.Exists(directory)) continue;
            CheckLinks(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                CheckLinks(path);
                if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("An import history entry exceeds its supported size.");
                var before = File.ReadAllBytes(path); bytes = checked(bytes + before.Length);
                if (bytes > 64 * 1024 * 1024)
                    throw new IOException("Import history exceeds this migration's metadata limit. Original files were retained for review.");
                byte[]? after = null;
                if (kind == "Imports")
                {
                    var record = JsonSerializer.Deserialize<FolderImportRecord>(before) ?? throw new InvalidDataException("An import history record is unreadable.");
                    if (!Guid.TryParseExact(record.Id, "N", out _) || !Path.GetFileNameWithoutExtension(path).Equals(record.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An import history filename does not match its saved identity.");
                    if (record.State != "Completed") throw new IOException("Finish or resolve the interrupted folder import in import history before migrating a folder.");
                    if (Same(source, settings.RootPath) && record.DestinationIdentity == oldIdentity)
                    {
                        var validated = new ImportJournal(state).Read(oldIdentity, recordId: record.Id).SingleOrDefault()
                            ?? throw new InvalidDataException("A completed import record could not be verified. Its original file was retained.");
                        after = JsonSerializer.SerializeToUtf8Bytes(validated with { DestinationIdentity = newIdentity,
                            Plan = validated.Plan with { DestinationPath = Path.Combine(destination, Path.GetRelativePath(source, validated.Plan.DestinationPath)) } });
                    }
                }
                else
                {
                    var record = JsonSerializer.Deserialize<CloudImportRecord>(before) ?? throw new InvalidDataException("A cloud import history record is unreadable.");
                    if (!Guid.TryParseExact(record.Id, "N", out _) || !Path.GetFileNameWithoutExtension(path).Equals(record.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A cloud import filename does not match its saved identity.");
                    if (record.State != "Completed") throw new IOException("Finish or resolve the interrupted cloud import in import history before migrating a folder.");
                    if (Same(source, settings.RootPath) && record.DestinationIdentity == oldIdentity)
                    {
                        var validated = new CloudImportJournal(state).Read(oldIdentity, recordId: record.Id).SingleOrDefault()
                            ?? throw new InvalidDataException("A completed cloud import record could not be verified. Its original file was retained.");
                        // File IDs and source deletion receipts remain byte-for-byte unchanged.
                        after = JsonSerializer.SerializeToUtf8Bytes(record with { DestinationIdentity = newIdentity,
                            Plan = record.Plan with { DestinationPath = Path.Combine(destination, Path.GetRelativePath(source, validated.Plan.DestinationPath)) } });
                    }
                }
                if (after is not null) changes.Add(new(Path.GetRelativePath(state, path), before, after));
            }
        }
        var stopped = Path.Combine(state, "cloud-backup-stopped.json");
        if (File.Exists(stopped))
        {
            CheckLinks(stopped);
            var before = File.ReadAllBytes(stopped);
            var roots = JsonSerializer.Deserialize<List<ClientStorage.CloudBackupRelinquishedRoot>>(before)
                ?? throw new InvalidDataException("Stopped backup ownership could not be read. Its record was retained.");
            if (roots.Any(root => root is null || !Path.IsPathFullyQualified(root.RootPath) ||
                string.IsNullOrWhiteSpace(root.Name) || root.RelativePath != PathRules.ValidateRelative(root.RelativePath)))
                throw new InvalidDataException("Stopped backup ownership contains an invalid folder reference.");
            var updated = roots.Select(root => Same(root.RootPath, source) ? root with { RootPath = destination } : root).ToArray();
            if (!roots.SequenceEqual(updated)) changes.Add(new("cloud-backup-stopped.json", before, JsonSerializer.SerializeToUtf8Bytes(updated)));
        }
        return changes;
    }
    internal static void CheckPending(string state)
    {
        foreach (var name in new[] { "backup-pending.json", "disconnect-pending.json", "cloud-backup-stop.json" })
            if (File.Exists(Path.Combine(state, name))) throw new IOException("Finish the pending backup or account operation before migrating folders. Its recovery record was retained.");
        // Upload checkpoints encode exact local paths and identities. Never rewrite an active upload.
        var uploads = Path.Combine(state, "Uploads");
        if (Directory.Exists(uploads))
        {
            foreach (var path in EnumerateGuarded(uploads))
                if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Finish resumable uploads before migrating folders. Reconnect and let pending upload cleanup complete, then retry.");
        }
        var jobs = Path.Combine(state, "CloudTransfers", "jobs.sqlite");
        if (File.Exists(jobs))
        {
            CheckLinks(jobs);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = jobs, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM transfer_jobs WHERE state <> 5";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new IOException("Finish or explicitly resolve pending cloud transfer jobs before migrating folders. Paused and cancelled recovery records were retained.");
        }
    }
    private static IEnumerable<string> EnumerateGuarded(string directory)
    {
        CheckLinks(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckLinks(path);
            if (Directory.Exists(path)) { foreach (var child in EnumerateGuarded(path)) yield return child; }
            else yield return path;
        }
    }
    private static void RequireEither(string path, byte[] before, byte[] after)
    {
        var actual = File.ReadAllBytes(path);
        if (!actual.AsSpan().SequenceEqual(before) && !actual.AsSpan().SequenceEqual(after))
            throw new IOException("A saved record changed outside this migration. Its current contents were retained: " + Path.GetFileName(path));
    }
    private static void PublishProtected(string path, byte[] plain, bool overwrite = false)
    {
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(protectedBytes); output.Flush(true); }
        File.Move(temporary, path, overwrite);
    }
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string first, string second) => Full(first).Equals(Full(second), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string root, string path) => Same(root, path) || Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void CheckLinks(string path)
        => ClientMigrationReview.EnsureUnlinked(path);
    private void Report(IProgress<MigrationProgress> progress, string operation, string detail, long complete = 0, long? total = null) =>
        progress.Report(new(ItemId, operation, detail, complete, total, "folders"));
    public void Dispose() { if (_disposed) return; _disposed = true; _move?.Dispose(); _lease.Dispose(); foreach (var guard in _guards) guard.Dispose(); }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    internal static DirectoryIdentity ReadIdentity(string path)
    {
        using var handle = CreateFileW(@"\\?\" + path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow,
            ((long)information.CreationTime.dwHighDateTime << 32) | (uint)information.CreationTime.dwLowDateTime);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    internal sealed class WindowsRuntime(Func<ICloudStore>? cloudFactory = null) : IClientFolderMigrationRuntime
    {
        public bool IsRegistered(string root, string account, bool canonical)
        {
            var expected = WindowsPlaceholderService.GetRegistrationId(account, canonical);
            foreach (var entry in StorageProviderSyncRootManager.GetCurrentSyncRoots())
            {
                var path = entry.Path.Path;
                if (Same(path, root) && WindowsPlaceholderService.HasRegistrationIdentity(entry, account, canonical)) return true;
                if (entry.Id == expected || Within(root, path) || Within(path, root))
                    throw new IOException("A different Windows cloud-provider registration owns this folder. Its registration was retained.");
            }
            return false;
        }
        public async Task PrepareAndUnregisterAsync(string root, string account, bool canonical, B2Credentials? credentials,
            IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (!IsRegistered(root, account, canonical)) return;
            // Fully local folders do not need network access. Connect only if Windows requests
            // online-only content, and serialize first authorization across hydration callbacks.
            using var cloud = cloudFactory?.Invoke() ?? new B2CloudStore();
            using var authorization = new SemaphoreSlim(1, 1);
            var authorized = false;
            await using (var placeholders = new WindowsPlaceholderService())
            {
                await placeholders.ConnectAsync(root, account,
                    async (file, offset, length, destination, token) =>
                    {
                        await authorization.WaitAsync(token);
                        try
                        {
                            if (!authorized)
                            {
                                if (credentials is null) throw new IOException("Reconnect the B2 account before migrating online-only files. No registration was removed.");
                                var connected = await cloud.ConnectAsync(credentials, token);
                                if (!account.StartsWith(connected.AccountId + ":", StringComparison.Ordinal))
                                    throw new IOException("These credentials belong to a different B2 account. The original provider was retained.");
                                authorized = true;
                            }
                        }
                        finally { authorization.Release(); }
                        await cloud.DownloadAsync(file, destination, offset, length,
                            new InlineProgress<TransferProgress>(value => progress.Report("Downloading " + file.Key + ": " + value.Bytes + " / " + value.TotalBytes + " bytes")), token);
                    },
                    cancellationToken, null, canonical);
                await placeholders.PrepareForUnregisterAsync(cancellationToken, progress);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRegistered(root, account, canonical)) throw new IOException("The Windows registration changed while cloud files were being preserved.");
            StorageProviderSyncRootManager.Unregister(WindowsPlaceholderService.GetRegistrationId(account, canonical));
            if (IsRegistered(root, account, canonical)) throw new IOException("Windows did not remove the previous registration; the folder was retained.");
        }
        public async Task RegisterAsync(string root, string account, bool canonical, string manifestPath, CancellationToken cancellationToken)
        {
            if (IsRegistered(root, account, canonical)) return;
            var manifest = new SyncManifest(manifestPath);
            await using var placeholders = new WindowsPlaceholderService();
            await placeholders.ConnectAsync(root, account,
                (_, _, _, _, _) => Task.FromException(new IOException("CloudInlet is finishing folder migration. Reopen the app after completion to download files.")),
                cancellationToken, manifest.PrepareForNewRegistration, canonical);
        }
        public string GetKnownFolder(string name) => KnownFolderBackup.GetPath(name);
        public void ExchangeKnownFolder(string name, string expected, string replacement) => KnownFolderBackup.CompareExchangePath(name, expected, replacement);
    }
}

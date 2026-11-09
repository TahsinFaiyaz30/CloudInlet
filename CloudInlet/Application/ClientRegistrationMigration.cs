using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;
using CloudInlet.Windows.CloudFiles;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Application;

/// <summary>Changes only an owned native registration. Run with the application-state
/// lease held and before constructing a controller. The original folder and baseline stay put.</summary>
internal sealed class ClientRegistrationMigration : IMigrationOperation, IDisposable
{
    private sealed record Receipt(int Schema, string Fingerprint, string ItemId, string State, string Root,
        ClientFolderMigration.DirectoryIdentity Identity, string AccountId, string BucketId, string Prefix,
        string StateName, SyncRootBinding[] OriginalBindings, bool Started);
    private static readonly byte[] Entropy = "CloudInlet.RegistrationMigration.v1"u8.ToArray();
    private Receipt _receipt;
    private readonly string _record;
    private readonly IClientFolderMigrationRuntime _runtime;
    private readonly FileStream _lease;
    private readonly List<SafeFileHandle> _guards;
    private bool _disposed;
    public string ItemId => _receipt.ItemId;
    public string ReviewedPlanFingerprint => _receipt.Fingerprint;
    public bool CanRevert => !_disposed;
    private string Account => _receipt.AccountId + ":" + _receipt.BucketId + ":" + _receipt.Prefix;
    private string Exchanges => _record + ".metadata";
    private string SettingsPath => Path.Combine(_receipt.State, "settings.json");
    private SyncRootBinding CanonicalBinding => new(_receipt.Root, _receipt.StateName, true,
        _receipt.AccountId, _receipt.BucketId, _receipt.Prefix);
    private ClientRegistrationMigration(Receipt receipt, string record, IClientFolderMigrationRuntime runtime,
        FileStream lease, List<SafeFileHandle> guards)
    { _receipt = receipt; _record = record; _runtime = runtime; _lease = lease; _guards = guards; }

    internal static bool IsRegistrationItem(MigrationItem item) => item.Area == MigrationArea.WindowsIntegration &&
        item.Id.StartsWith("sync-registration-", StringComparison.Ordinal);
    private static string Record(MigrationPlan plan, string itemId, string receipts) => Path.Combine(Path.GetFullPath(receipts),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Fingerprint + "|" + itemId))) + ".registration.dpapi");
    internal static bool HasRecovery(MigrationPlan plan, string itemId, string receipts)
    { var record = Record(plan, itemId, receipts); ClientMigrationReview.EnsureUnlinked(record); return File.Exists(record); }
    internal static bool CanAcknowledgeUnstarted(MigrationPlan plan, string itemId, string state, string receipts)
    {
        if (HasRecovery(plan, itemId, receipts)) return false;
        var item = plan.Items.Single(value => value.Id == itemId);
        var settings = new ClientStorage(state).LoadSettings(); PathRules.ValidateSettings(settings);
        var roots = settings.CustomBackups.Select(value => (Root: value.SourcePath, value.Prefix)).Append((Root: settings.RootPath, settings.Prefix));
        var matches = roots.Where(value => WindowsPlaceholderService.GetRegistrationId(settings.AccountId + ":" + settings.BucketId + ":" + value.Prefix) == item.CurrentLocation).ToArray();
        return matches.Length == 1 && !SyncRootMigrationIdentity.IsCanonical(settings, matches[0].Root) &&
            new ClientFolderMigration.WindowsRuntime().IsRegistered(matches[0].Root, settings.AccountId + ":" + settings.BucketId + ":" + matches[0].Prefix, false);
    }
    internal static Task AcknowledgeUnstartedAsync(MigrationPlan plan, string itemId, string state, string receipts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var record = Record(plan, itemId, receipts); ClientMigrationReview.EnsureUnlinked(record + ".lock"); Directory.CreateDirectory(receipts);
        using var lease = new FileStream(record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!CanAcknowledgeUnstarted(plan, itemId, state, receipts)) throw new IOException("Restore this registration's recovery record before continuing.");
        return Task.CompletedTask;
    }

    internal static ClientRegistrationMigration Open(MigrationPlan plan, string itemId, string state, string receipts,
        IClientFolderMigrationRuntime? runtime = null)
    {
        if (MigrationPlan.Create(plan.Items).Fingerprint != plan.Fingerprint) throw new InvalidDataException("The registration review changed.");
        var item = plan.Items.Single(value => value.Id == itemId);
        if (!IsRegistrationItem(item) || item.BlockedReason is not null) throw new InvalidDataException("This registration is not available for migration.");
        state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state));
        receipts = Path.TrimEndingDirectorySeparator(Path.GetFullPath(receipts));
        ClientMigrationReview.EnsureUnlinked(state); ClientMigrationReview.EnsureUnlinked(receipts); Directory.CreateDirectory(receipts);
        var record = Record(plan, itemId, receipts); ClientMigrationReview.EnsureUnlinked(record); ClientMigrationReview.EnsureUnlinked(record + ".lock");
        var lease = new FileStream(record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var guards = new List<SafeFileHandle>();
        try
        {
            PinAncestors(state, guards); PinAncestors(receipts, guards);
            MigrationMetadataExchange.Recover(state, record + ".metadata");
            runtime ??= new ClientFolderMigration.WindowsRuntime();
            Receipt receipt;
            if (File.Exists(record))
            {
                using var input = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid registration recovery record size.");
                var data = new byte[(int)input.Length]; input.ReadExactly(data);
                var plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                try { receipt = JsonSerializer.Deserialize<Receipt>(plain) ?? throw new InvalidDataException("Empty registration recovery record."); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (receipt.Schema != 1 || receipt.Fingerprint != plan.Fingerprint || receipt.ItemId != itemId || !Same(receipt.State, state) ||
                    receipt.OriginalBindings is null || receipt.OriginalBindings.Length > 1 || receipt.StateName.Length != 24 || !receipt.StateName.All(Uri.IsHexDigit))
                    throw new InvalidDataException("The registration recovery record does not match this review.");
            }
            else
            {
                var settings = new ClientStorage(state).LoadSettings(); PathRules.ValidateSettings(settings);
                var candidates = settings.CustomBackups.Select(value => (Root: value.SourcePath, value.Prefix))
                    .Append((Root: settings.RootPath, settings.Prefix))
                    .Where(value => WindowsPlaceholderService.GetRegistrationId(settings.AccountId + ":" + settings.BucketId + ":" + value.Prefix) == item.CurrentLocation).ToArray();
                if (candidates.Length != 1) throw new IOException("This registration no longer identifies exactly one saved folder. Review its settings again.");
                var (root, prefix) = candidates[0]; root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var account = settings.AccountId + ":" + settings.BucketId + ":" + prefix;
                if (SyncRootMigrationIdentity.IsCanonical(settings, root) || !runtime.IsRegistered(root, account, false))
                    throw new IOException("The reviewed CloudBay registration changed. Its current registration was retained.");
                ClientFolderMigration.CheckPending(state);
                receipt = new(1, plan.Fingerprint, itemId, state, root, ClientFolderMigration.ReadIdentity(root), settings.AccountId,
                    settings.BucketId, prefix, SyncRootMigrationIdentity.StateName(settings, root, prefix),
                    settings.RootBindings.Where(value => Same(value.RootPath, root)).ToArray(), false);
            }
            var identity = receipt.AccountId + ":" + receipt.BucketId + ":" + receipt.Prefix;
            if (WindowsPlaceholderService.GetRegistrationId(identity) != item.CurrentLocation ||
                WindowsPlaceholderService.GetRegistrationId(identity, true) != item.ProposedLocation)
                throw new InvalidDataException("The reviewed native identities do not match the saved account.");
            PinAncestors(Path.GetDirectoryName(receipt.Root)!, guards);
            guards.Add(Pin(receipt.Root)); // Pin the root object, including a legitimate cloud reparse point.
            if (ClientFolderMigration.ReadIdentity(receipt.Root) != receipt.Identity) throw new IOException("The original sync folder was replaced. Its new contents were retained.");
            if (!File.Exists(record)) Publish(record, receipt, false);
            return new(receipt, record, runtime, lease, guards);
        }
        catch { foreach (var guard in guards) guard.Dispose(); lease.Dispose(); throw; }
    }

    public Task CheckAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        MigrationMetadataExchange.Recover(_receipt.State, Exchanges);
        if (ClientFolderMigration.ReadIdentity(_receipt.Root) != _receipt.Identity) throw new IOException("The original sync folder changed.");
        ClientFolderMigration.CheckPending(_receipt.State); ValidateSettings(new ClientStorage(_receipt.State).LoadSettings());
        var registration = Registration();
        if (!_receipt.Started && registration != false) throw new IOException("The original CloudBay registration changed before migration started.");
        return Task.CompletedTask;
    }
    public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken token)
    {
        await CheckAsync(token); MarkStarted();
        if (Registration() == false)
        {
            Report(progress, "Preserving cloud files", "Downloading and verifying online-only files before changing the Windows registration.");
            await _runtime.PrepareAndUnregisterAsync(_receipt.Root, Account, false, new ClientStorage(_receipt.State).LoadCredentials(),
                new InlineProgress(value => Report(progress, "Preserving cloud files", value)), token);
        }
        Report(progress, "Updating Windows registration", "Keeping the same folder and sync history while registering CloudInlet.");
        SaveBinding(false);
        await _runtime.RegisterAsync(_receipt.Root, Account, true, Path.Combine(_receipt.State, "State", _receipt.StateName + ".sqlite"), token);
        await VerifyAsync(token);
        Report(progress, "Windows registration verified", "CloudInlet now owns the existing folder. Its contents and sync baseline were retained.", 1);
    }
    public Task VerifyAsync(CancellationToken token) => Verify(false, token);
    public async Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken token)
    {
        await CheckAsync(token);
        if (_receipt.Started)
        {
            if (Registration() == true)
            {
                Report(progress, "Restoring Windows registration", "Preserving local files before restoring this incomplete item's registration.");
                await _runtime.PrepareAndUnregisterAsync(_receipt.Root, Account, true, new ClientStorage(_receipt.State).LoadCredentials(),
                    new InlineProgress(value => Report(progress, "Preserving cloud files", value)), token);
            }
            SaveBinding(true);
            await _runtime.RegisterAsync(_receipt.Root, Account, false, Path.Combine(_receipt.State, "State", _receipt.StateName + ".sqlite"), token);
        }
        await VerifyRevertedAsync(token);
        Report(progress, "Previous registration restored", "Only this incomplete registration was restored. Completed migration items were retained.", 1);
    }
    public Task VerifyRevertedAsync(CancellationToken token) => Verify(true, token);
    private async Task Verify(bool reverted, CancellationToken token)
    {
        await CheckAsync(token);
        var settings = new ClientStorage(_receipt.State).LoadSettings();
        if (Registration() != !reverted || !Bindings(settings).SequenceEqual(reverted ? _receipt.OriginalBindings : [CanonicalBinding]))
            throw new IOException("The Windows registration and its saved sync identity still need recovery.");
    }
    private bool? Registration()
    {
        // IsRegistered rejects overlapping or foreign registrations. Try both exact
        // owned identities, but never interpret an access/ownership error as absence.
        try { if (_runtime.IsRegistered(_receipt.Root, Account, true)) return true; }
        catch (IOException) { if (_runtime.IsRegistered(_receipt.Root, Account, false)) return false; throw; }
        return _runtime.IsRegistered(_receipt.Root, Account, false) ? false : null;
    }
    private SyncRootBinding[] Bindings(AppSettings settings) => settings.RootBindings.Where(value => Same(value.RootPath, _receipt.Root)).ToArray();
    private void ValidateSettings(AppSettings settings)
    {
        PathRules.ValidateSettings(settings);
        var roots = settings.CustomBackups.Select(value => (Root: value.SourcePath, value.Prefix)).Append((Root: settings.RootPath, settings.Prefix));
        if (settings.AccountId != _receipt.AccountId || settings.BucketId != _receipt.BucketId ||
            roots.Count(value => Same(value.Root, _receipt.Root) && value.Prefix == _receipt.Prefix) != 1 ||
            !Bindings(settings).SequenceEqual(_receipt.OriginalBindings) && !Bindings(settings).SequenceEqual([CanonicalBinding]))
            throw new IOException("The folder, account or sync identity changed outside migration. Its current settings were retained.");
    }
    private void SaveBinding(bool reverted)
    {
        ClientMigrationReview.EnsureUnlinked(SettingsPath);
        var before = File.ReadAllBytes(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(before) ?? throw new InvalidDataException("Empty settings.");
        ValidateSettings(settings);
        var desired = reverted ? _receipt.OriginalBindings : [CanonicalBinding];
        if (Bindings(settings).SequenceEqual(desired)) return;
        var updated = settings with { RootBindings = settings.RootBindings.Where(value => !Same(value.RootPath, _receipt.Root)).Concat(desired).ToList() };
        MigrationMetadataExchange.Replace(SettingsPath, before, JsonSerializer.SerializeToUtf8Bytes(updated, new JsonSerializerOptions { WriteIndented = true }), Exchanges);
    }
    private void MarkStarted()
    { if (_receipt.Started) return; var next = _receipt with { Started = true }; Publish(_record, next, true); _receipt = next; }
    private static void Publish(string path, Receipt receipt, bool overwrite)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(receipt); byte[] data;
        try { data = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); }
        File.Move(temp, path, overwrite);
    }
    private static bool Same(string first, string second) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    internal static void PinAncestors(string path, List<SafeFileHandle> guards)
    { for (var current = path; current is not null; current = Path.GetDirectoryName(current)) { ClientMigrationReview.EnsureUnlinked(current); guards.Add(Pin(current)); } }
    internal static SafeFileHandle Pin(string path)
    {
        var handle = CreateFileW(@"\\?\" + path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); } return handle;
    }
    private void Report(IProgress<MigrationProgress> progress, string operation, string detail, long done = 0) => progress.Report(new(ItemId, operation, detail, done, done == 1 ? 1 : null, "registrations"));
    public void Dispose() { if (_disposed) return; _disposed = true; _lease.Dispose(); foreach (var guard in _guards) guard.Dispose(); }
    private sealed class InlineProgress(Action<string> callback) : IProgress<string> { public void Report(string value) => callback(value); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

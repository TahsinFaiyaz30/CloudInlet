using System.Net;
using System.Security.Cryptography;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;

namespace CloudInlet.Application;

/// <summary>Desktop ownership, credentials, protected receipts and native bindings for one
/// reviewed cloud migration. Open only before ClientController while holding the state-use lease.</summary>
internal sealed class ClientCloudPrefixMigration : IMigrationOperation, IKeepOriginalMigrationOperation, IDisposable
{
    private readonly MigrationPlan _plan;
    private readonly MigrationItem _item;
    private readonly string _state;
    private readonly ClientCloudPrefixBinding _binding;
    private readonly AnchoredJournal _journal;
    private readonly ProtectedB2CloudPrefixCopyJournal _copyJournal;
    private readonly Func<B2Credentials, CancellationToken, Task<ICloudPrefixMigrationStore>> _connect;
    private readonly B2CloudStore? _cloud;
    private CloudPrefixMigration? _operation;
    private IProgress<MigrationProgress>? _progress;
    private bool _disposed;
    public string ItemId => _item.Id;
    public string ReviewedPlanFingerprint => _plan.Fingerprint;
    public bool CanRevert
    {
        get
        {
            if (_disposed) return false;
            try { return _operation?.CanRevert ?? (_journal.Read()?.Phase is not
                (CloudPrefixMigrationPhase.Switching or CloudPrefixMigrationPhase.Switched or CloudPrefixMigrationPhase.Reverted or CloudPrefixMigrationPhase.OriginalKept)); }
            catch { return false; }
        }
    }
    public bool CanKeepOriginal
    {
        get
        {
            if (_disposed) return false;
            try { return _operation?.CanKeepOriginal ?? (_journal.Read()?.Phase is null or
                CloudPrefixMigrationPhase.Copying or CloudPrefixMigrationPhase.Reverting or CloudPrefixMigrationPhase.OriginalKept); }
            catch { return false; }
        }
    }
    private static string BasePath(MigrationPlan plan, string id, string receipts) => Path.Combine(Path.GetFullPath(receipts),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Fingerprint + "|" + id))));
    internal static bool HasRecovery(MigrationPlan plan, string id, string receipts)
    {
        var path = BasePath(plan, id, receipts); ClientMigrationReview.EnsureUnlinked(receipts);
        return new[] { ".cloud-binding.dpapi", ".cloud-binding.dpapi.metadata", ".cloud-history.dpapi", ".cloud-history.dpapi.started", ".cloud-copies.dpapi" }
            .Any(suffix => File.Exists(path + suffix) || Directory.Exists(path + suffix)) ||
            Directory.Exists(receipts) && Directory.EnumerateFileSystemEntries(receipts, Path.GetFileName(path) + "*.log").Any();
    }

    internal ClientCloudPrefixMigration(ClientMigrationReview.Consent consent, string id, string state, string receipts,
        IClientFolderMigrationRuntime? runtime = null,
        Func<B2Credentials, CancellationToken, Task<ICloudPrefixMigrationStore>>? connect = null)
    {
        if (!consent.CloudMaintenanceAcknowledged || !consent.SelectedIds.Contains(id, StringComparer.Ordinal) ||
            !Guid.TryParseExact(consent.Id, "N", out _))
            throw new InvalidDataException("The selected cloud folder needs its protected maintenance confirmation.");
        _plan = consent.Plan; _item = _plan.Items.Single(item => item.Id == id); _state = state;
        var acknowledgement = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "CloudInlet.CloudMaintenance.v1|" + consent.Id + "|" + _plan.Fingerprint + "|" + id)));
        _binding = ClientCloudPrefixBinding.Open(_plan, id, state, receipts, acknowledgement, runtime,
            detail => _progress?.Report(new(id, "Updating local cloud references", detail)));
        ProtectedCloudPrefixMigrationJournal? journal = null;
        ProtectedB2CloudPrefixCopyJournal? copies = null;
        try
        {
            var path = BasePath(_plan, id, receipts);
            byte[] Protect(byte[] bytes, string kind) => ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(kind), DataProtectionScope.CurrentUser);
            byte[] Unprotect(byte[] bytes, string kind) => ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes(kind), DataProtectionScope.CurrentUser);
            journal = new(path + ".cloud-history.dpapi", data => Protect(data, "CloudInlet.CloudHistory.v1"), data => Unprotect(data, "CloudInlet.CloudHistory.v1"));
            copies = new(path + ".cloud-copies.dpapi", data => Protect(data, "CloudInlet.CloudCopies.v1"), data => Unprotect(data, "CloudInlet.CloudCopies.v1"));
            _journal = new(journal, path + ".cloud-history.dpapi.started", acknowledgement); _copyJournal = copies;
            // Transport mutations are impossible before the outer history intent is durable.
            // Losing that outer record must never authorize an unstarted-item acknowledgement.
            if (_journal.Read() is null && copies.Read() is { } copy && (copy.Intents.Count != 0 || copy.HideReceipts?.Count > 0))
                throw new InvalidDataException("The cloud history recovery record is missing. Restore it before retrying; existing cloud versions were retained.");
            if (connect is not null) _connect = connect;
            else
            {
                _cloud = new B2CloudStore();
                _connect = async (credentials, token) =>
                {
                    var settings = new ClientStorage(_state).LoadSettings();
                    var account = await _cloud.ConnectAsync(credentials, token).ConfigureAwait(false);
                    if (account.AccountId != settings.AccountId) throw new IOException("The connected cloud account differs from the reviewed account.");
                    return _cloud.CreatePrefixMigrationStore(settings.BucketId, _item.CurrentLocation, _item.ProposedLocation, _copyJournal, _item.CloudMappings);
                };
            }
        }
        catch { copies?.Dispose(); journal?.Dispose(); _binding.Dispose(); throw; }
    }
    internal static bool CanAcknowledgeUnstarted(MigrationPlan plan, string id, string state, string receipts)
    {
        ClientMigrationReview.EnsureUnlinked(receipts); ClientMigrationReview.EnsureUnlinked(state);
        if (HasRecovery(plan, id, receipts)) return false;
        var item = plan.Items.Single(value => value.Id == id);
        var settings = new ClientStorage(state).LoadSettings();
        CloudInlet.Core.Sync.PathRules.ValidateSettings(settings);
        return item.Area == MigrationArea.CloudPrefix && item.BlockedReason is null &&
            settings.CustomBackups.Select(value => value.Prefix).Append(settings.Prefix).Contains(item.CurrentLocation, StringComparer.Ordinal);
    }
    internal static Task AcknowledgeUnstartedAsync(MigrationPlan plan, string id, string state, string receipts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanAcknowledgeUnstarted(plan, id, state, receipts)) throw new IOException("This cloud change has saved recovery work. Recover its recorded step before continuing.");
        return Task.CompletedTask;
    }
    private async Task<CloudPrefixMigration> OperationAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_operation is not null) return _operation;
        await _binding.VerifyMaintenanceAsync(token).ConfigureAwait(false);
        var credentials = new ClientStorage(_state).LoadCredentials() ?? throw new IOException("Reconnect this B2 account before migrating its cloud folders. No cloud objects were changed.");
        var store = await _connect(credentials, token).ConfigureAwait(false);
        var settings = new ClientStorage(_state).LoadSettings();
        if (store.AccountId != settings.AccountId || store.BucketId != settings.BucketId)
            throw new IOException("The migration connection does not match the reviewed account and bucket.");
        return _operation = new(_plan, ItemId, _item.CurrentLocation, _item.ProposedLocation, store, _binding, _journal);
    }
    public Task CheckAsync(CancellationToken token) => RunAsync(async () =>
        await (await OperationAsync(token).ConfigureAwait(false)).CheckAsync(token).ConfigureAwait(false), token);
    public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken token)
    {
        _progress = progress;
        try { await RunAsync(async () => await (await OperationAsync(token).ConfigureAwait(false)).ApplyAsync(progress, token).ConfigureAwait(false), token).ConfigureAwait(false); }
        finally { _progress = null; }
    }
    public Task VerifyAsync(CancellationToken token) => RunAsync(async () =>
        await (await OperationAsync(token).ConfigureAwait(false)).VerifyAsync(token).ConfigureAwait(false), token);
    public async Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken token)
    {
        // A denied capability or offline preflight can be dismissed without requiring network.
        if (_journal.Read() is null) { await _binding.VerifyOriginalAsync(token).ConfigureAwait(false); return; }
        await RunAsync(async () => await (await OperationAsync(token).ConfigureAwait(false)).RevertAsync(progress, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }
    public async Task VerifyRevertedAsync(CancellationToken token)
    {
        if (_journal.Read() is null) { await _binding.VerifyOriginalAsync(token).ConfigureAwait(false); return; }
        await RunAsync(async () => await (await OperationAsync(token).ConfigureAwait(false)).VerifyRevertedAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
    }
    private async Task<CloudPrefixMigration> OriginalRecoveryAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_operation is not null) return _operation;
        await _binding.VerifyMaintenanceAsync(token).ConfigureAwait(false);
        await _binding.VerifyOriginalAsync(token).ConfigureAwait(false);
        var settings = new ClientStorage(_state).LoadSettings();
        // This exit remains available offline or with revoked provider permissions. No cloud request
        // is needed to verify that local references have not switched, and none is permitted here.
        return _operation = new(_plan, ItemId, _item.CurrentLocation, _item.ProposedLocation,
            new RetainedOriginalStore(settings.AccountId, settings.BucketId), _binding, _journal);
    }
    public async Task KeepOriginalAsync(IProgress<MigrationProgress> progress, CancellationToken token) =>
        await (await OriginalRecoveryAsync(token).ConfigureAwait(false)).KeepOriginalAsync(progress, token).ConfigureAwait(false);
    public async Task VerifyOriginalKeptAsync(CancellationToken token) =>
        await (await OriginalRecoveryAsync(token).ConfigureAwait(false)).VerifyOriginalKeptAsync(token).ConfigureAwait(false);

    private sealed class RetainedOriginalStore(string accountId, string bucketId) : ICloudPrefixMigrationStore
    {
        public string AccountId => accountId;
        public string BucketId => bucketId;
        private static Exception Unavailable() => new InvalidOperationException("Keeping the original location cannot access or change cloud objects.");
        public Task ValidateAccessAsync(string source, string destination, CancellationToken token) => throw Unavailable();
        public Task<IReadOnlyList<CloudPrefixVersion>> ListAsync(string prefix, CancellationToken token) => throw Unavailable();
        public Task<CloudPrefixVersion> CopyAsync(CloudPrefixVersion source, string destination, string operation, CancellationToken token) => throw Unavailable();
        public Task<CloudPrefixVersion> HideOnceAsync(string destination, CancellationToken token) => throw Unavailable();
        public Task DeleteExactAsync(CloudPrefixVersion version, CancellationToken token) => throw Unavailable();
    }
    private static async Task RunAsync(Func<Task> action, CancellationToken token)
    {
        try { await action().ConfigureAwait(false); }
        catch (B2RequestException error) when (error.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)error.StatusCode >= 500)
        { throw new MigrationTransientException(error.Message); }
        catch (HttpRequestException) when (!token.IsCancellationRequested)
        { throw new MigrationTransientException("The cloud connection was interrupted. Verified receipts were retained for a safe retry."); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new MigrationTransientException("A required migration file is temporarily locked. Close the application using it."); }
    }
    private sealed class AnchoredJournal(ProtectedCloudPrefixMigrationJournal journal, string marker, string acknowledgement) : ICloudPrefixMigrationJournal, IDisposable
    {
        private static readonly byte[] Entropy = "CloudInlet.CloudHistoryAnchor.v1"u8.ToArray();
        public CloudPrefixMigrationCheckpoint? Read()
        {
            var record = journal.Read();
            if (ValidateAnchor())
            {
                if (record is null) throw new InvalidDataException("The cloud history recovery record is missing. Restore it before continuing; existing cloud objects were retained.");
            }
            else if (record is not null) PublishAnchor();
            return record;
        }
        public void Write(CloudPrefixMigrationCheckpoint checkpoint)
        {
            journal.Write(checkpoint);
            if (!ValidateAnchor()) PublishAnchor();
        }
        private bool ValidateAnchor()
        {
            ClientMigrationReview.EnsureUnlinked(marker);
            if (Directory.Exists(marker)) throw new InvalidDataException("The cloud history anchor was replaced by a directory.");
            if (!File.Exists(marker)) return false;
            using var file = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > 4096) throw new InvalidDataException("The cloud history anchor is invalid.");
            var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            try { if (Encoding.UTF8.GetString(plain) != acknowledgement) throw new InvalidDataException("The cloud history anchor belongs to a different review."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return true;
        }
        private void PublishAnchor()
        {
            ClientMigrationReview.EnsureUnlinked(marker);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(acknowledgement), Entropy, DataProtectionScope.CurrentUser);
            // This receipt must be durable before the engine can issue its first mutation.
            using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            file.Write(bytes); file.Flush(true);
        }
        public void Dispose() => journal.Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _cloud?.Dispose(); _copyJournal.Dispose(); _journal.Dispose(); _binding.Dispose();
    }
}

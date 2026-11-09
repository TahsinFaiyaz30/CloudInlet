using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudInlet.Core.Migration;

/// <summary>The complete immutable-version metadata used for migration, not a current-file listing.</summary>
public sealed record CloudPrefixVersion(string FileId, string Key, string Action, long Size, string? Sha1,
    long UploadedAtMillis, string ContentType, IReadOnlyDictionary<string, string> Metadata,
    bool ProtectionReadable = true, string? RetentionMode = null, long? RetainUntilMillis = null,
    string? LegalHold = null, string? EncryptionMode = null, string? EncryptionAlgorithm = null,
    string? ReplicationStatus = null)
{
    private readonly IReadOnlyDictionary<string, string> _metadata = FreezeMetadata(Metadata);
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = FreezeMetadata(value);
    }
    private static IReadOnlyDictionary<string, string> FreezeMetadata(IReadOnlyDictionary<string, string> value) =>
        value is null ? null! : value is ImmutableDictionary<string, string> immutable && immutable.KeyComparer == StringComparer.Ordinal
            ? immutable : value.ToImmutableDictionary(StringComparer.Ordinal);
}

/// <summary>
/// Provider contract: listings include every version in newest-first order per key; copy uses a durable,
/// unique operation receipt. Hide is sent exactly once and returns its immutable ID. Delete accepts only
/// an exact immutable version. None of these methods may stage payload on disk.
/// </summary>
public interface ICloudPrefixMigrationStore
{
    string AccountId { get; }
    string BucketId { get; }
    Task ValidateAccessAsync(string sourcePrefix, string destinationPrefix, CancellationToken token);
    Task<IReadOnlyList<CloudPrefixVersion>> ListAsync(string prefix, CancellationToken token);
    Task<CloudPrefixVersion> CopyAsync(CloudPrefixVersion source, string destinationKey, string operationId, CancellationToken token);
    Task<CloudPrefixVersion> HideOnceAsync(string destinationKey, CancellationToken token);
    Task DeleteExactAsync(CloudPrefixVersion version, CancellationToken token);
}

/// <summary>Optional recovery of a hide response that the transport durably acknowledged before
/// the outer journal was interrupted. A listing alone must never establish hide ownership.</summary>
public interface ICloudPrefixMigrationHideRecovery
{
    Task<CloudPrefixVersion?> RecoverAcknowledgedHideAsync(string destinationKey,
        IReadOnlyCollection<string> acknowledgedVersionIds, CancellationToken token);
}

/// <summary>Reconciles a pending copy for an explicitly requested revert. May cancel only an
/// authenticated, immutable unfinished destination ID; never creates a replacement version.</summary>
public interface ICloudPrefixMigrationCopyRecovery
{
    Task<CloudPrefixVersion?> ReconcileCopyForRevertAsync(CloudPrefixVersion source, string destinationKey,
        string operationId, CancellationToken token);
}

public sealed record CloudPrefixVersionMapping(CloudPrefixVersion Original, CloudPrefixVersion Migrated);

/// <summary>
/// The application must quiesce all local users of this account before constructing an operation.
/// SwitchAsync durably and idempotently changes settings and every dependent manifest/native reference
/// together, retaining old/new version IDs and original history timestamps. A prefix-only settings edit
/// is not an implementation of this contract. Original versions are deliberately retained in B2.
/// </summary>
public interface ICloudPrefixMigrationBinding
{
    /// <summary>Durable acknowledgement identifying the maintenance review, bound to this account and both prefixes.</summary>
    string MaintenanceReceipt { get; }
    /// <summary>Verify the acknowledgement and local writer leases remain valid. This is not a B2-wide lock.</summary>
    Task VerifyMaintenanceAsync(CancellationToken token);
    Task VerifyOriginalAsync(CancellationToken token);
    Task SwitchAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token);
    Task VerifySwitchedAsync(IReadOnlyList<CloudPrefixVersionMapping> mappings, CancellationToken token);
}

public interface ICloudPrefixMigrationJournal
{
    CloudPrefixMigrationCheckpoint? Read();
    void Write(CloudPrefixMigrationCheckpoint checkpoint);
}

public enum CloudPrefixMigrationPhase { Copying, Switching, Switched, Reverting, Reverted, OriginalKept }
public sealed record CloudPrefixMigrationEntry(CloudPrefixVersion Source, string DestinationKey, string OperationId,
    bool RequestPending = false, CloudPrefixVersion? Destination = null, bool Removed = false);
public sealed record CloudPrefixMigrationCheckpoint(int Schema, string PlanFingerprint, string ItemId, string AccountId,
    string BucketId, string SourcePrefix, string DestinationPrefix, string MaintenanceReceipt, CloudPrefixMigrationPhase Phase,
    IReadOnlyList<CloudPrefixMigrationEntry> Entries)
{
    private readonly IReadOnlyList<CloudPrefixMigrationEntry> _entries = MigrationSnapshotList<CloudPrefixMigrationEntry>.Freeze(Entries);
    public IReadOnlyList<CloudPrefixMigrationEntry> Entries { get => _entries; init => _entries = MigrationSnapshotList<CloudPrefixMigrationEntry>.Freeze(value); }
}

/// <summary>
/// Recoverable version-history migration. It never deletes source versions. A successful migration means
/// the application has switched to verified new versions and retains the originals for recovery; deleting
/// the old namespace requires a separate retention-aware user decision. Unexpected external writes stop
/// the operation rather than merging histories or overwriting another client's work.
/// </summary>
public sealed class CloudPrefixMigration : IMigrationOperation, IKeepOriginalMigrationOperation
{
    public const string ReceiptMetadataKey = "cloudinlet_migration_id";
    public const string SourceMetadataKey = "cloudinlet_migration_source_id";
    private readonly ICloudPrefixMigrationStore _store;
    private readonly ICloudPrefixMigrationBinding _binding;
    private readonly ICloudPrefixMigrationJournal _journal;
    private readonly string _sourcePrefix, _destinationPrefix;
    private readonly CloudPrefixKeyMap _keyMap;
    private CloudPrefixMigrationCheckpoint? _checkpoint;
    private Exception? _journalFailure;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int VerificationBatchSize = 128;
    public string ItemId { get; }
    public string ReviewedPlanFingerprint { get; }
    public bool CanRevert => _journalFailure is null && _checkpoint?.Phase is not
        (CloudPrefixMigrationPhase.Switching or CloudPrefixMigrationPhase.Switched or CloudPrefixMigrationPhase.Reverted or CloudPrefixMigrationPhase.OriginalKept);
    public bool CanKeepOriginal => _journalFailure is null && (_checkpoint is null || _checkpoint.Phase is
        CloudPrefixMigrationPhase.Copying or CloudPrefixMigrationPhase.Reverting or CloudPrefixMigrationPhase.OriginalKept);
    public IReadOnlyList<CloudPrefixVersionMapping> VersionMappings => _checkpoint is null ? [] : Mappings();

    public CloudPrefixMigration(MigrationPlan plan, string itemId, string sourcePrefix, string destinationPrefix,
        ICloudPrefixMigrationStore store, ICloudPrefixMigrationBinding binding, ICloudPrefixMigrationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var item = plan.Items.Single(value => value.Id == itemId);
        if (item.Area != MigrationArea.CloudPrefix || item.BlockedReason is not null ||
            MigrationPlan.Create(plan.Items).Fingerprint != plan.Fingerprint)
            throw new ArgumentException("An executable, reviewed cloud-prefix item is required.", nameof(plan));
        ValidatePrefix(sourcePrefix); ValidatePrefix(destinationPrefix);
        if (item.CurrentLocation != sourcePrefix || item.ProposedLocation != destinationPrefix)
            throw new ArgumentException("The cloud folders do not match the user's reviewed migration item.");
        if (sourcePrefix.StartsWith(destinationPrefix, StringComparison.Ordinal) ||
            destinationPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new ArgumentException("Migration prefixes must be separate, non-overlapping folders.");
        ItemId = itemId; ReviewedPlanFingerprint = plan.Fingerprint;
        _sourcePrefix = sourcePrefix; _destinationPrefix = destinationPrefix;
        _keyMap = new(sourcePrefix, destinationPrefix, item.CloudMappings);
        _store = store; _binding = binding; _journal = journal;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(journal);
        if (string.IsNullOrWhiteSpace(store.AccountId) || string.IsNullOrWhiteSpace(store.BucketId))
            throw new ArgumentException("The reviewed cloud account and bucket identities are required.", nameof(store));
        if (binding.MaintenanceReceipt is not { Length: 64 } || !binding.MaintenanceReceipt.All(Uri.IsHexDigit))
            throw new ArgumentException("A durable maintenance acknowledgement and held local writer leases are required.", nameof(binding));
        _checkpoint = journal.Read();
        if (_checkpoint is not null) ValidateCheckpoint(_checkpoint);
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CheckCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CheckCoreAsync(CancellationToken token)
    {
        EnsureJournalHealthy();
        if (_checkpoint?.Phase == CloudPrefixMigrationPhase.OriginalKept)
            throw Conflict("This cloud change was stopped with the original location kept. Review a new migration; retained copies must not be adopted or overwritten.");
        await _binding.VerifyMaintenanceAsync(token).ConfigureAwait(false);
        await _store.ValidateAccessAsync(_sourcePrefix, _destinationPrefix, token).ConfigureAwait(false);
        if (_checkpoint?.Phase is CloudPrefixMigrationPhase.Switching or CloudPrefixMigrationPhase.Switched)
        {
            await VerifySourcesAsync(token).ConfigureAwait(false);
            await VerifyDestinationsAsync(token).ConfigureAwait(false);
            return;
        }
        if (_checkpoint?.Phase is CloudPrefixMigrationPhase.Reverting or CloudPrefixMigrationPhase.Reverted)
            throw new InvalidOperationException("This migration was reverted or is reverting. Resume recovery, or review a new migration.");
        await _binding.VerifyOriginalAsync(token).ConfigureAwait(false);
        if (_checkpoint is null)
        {
            var source = ValidateListing(await _store.ListAsync(_sourcePrefix, token).ConfigureAwait(false), _sourcePrefix);
            if (source.Any(version => version.Metadata.ContainsKey(ReceiptMetadataKey) || version.Metadata.ContainsKey(SourceMetadataKey)))
                throw Conflict("Source history already contains cloud-migration receipts. A preservation-aware migration is required; no existing metadata was replaced.");
            var target = ValidateListing(await _store.ListAsync(_destinationPrefix, token).ConfigureAwait(false), _destinationPrefix);
            if (target.Count != 0) throw Conflict("The proposed cloud folder already contains versions. Choose an empty destination; no files were replaced.");
            _keyMap.ValidateDistinctKeys(source.Select(value => value.Key));
            // Freeze original metadata and service ordering. Same-millisecond versions must retain
            // provider ordering; sorting by opaque file ID would invent history.
            var entries = source.GroupBy(value => value.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .SelectMany(group => group.Reverse()).Select(value => new CloudPrefixMigrationEntry(value,
                    _keyMap.Map(value.Key), OperationId(value.FileId))).ToArray();
            Persist(new(1, ReviewedPlanFingerprint, ItemId, _store.AccountId, _store.BucketId,
                _sourcePrefix, _destinationPrefix, _binding.MaintenanceReceipt, CloudPrefixMigrationPhase.Copying, entries));
        }
        await VerifySourcesAsync(token).ConfigureAwait(false);
        await VerifyDestinationsAsync(token).ConfigureAwait(false);
    }

    public async Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_checkpoint!.Phase == CloudPrefixMigrationPhase.Switched) return;
            if (_checkpoint.Phase == CloudPrefixMigrationPhase.Copying)
            {
                for (var index = 0; index < _checkpoint.Entries.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = _checkpoint.Entries[index];
                    if (entry.Destination is not null) continue;
                    // B2 has no account-wide writer lock. Recheck complete inventories at bounded
                    // intervals, on every resume and immediately before activation. The transport
                    // additionally verifies the exact immutable source and policy before each copy.
                    if (index % VerificationBatchSize == 0)
                    {
                        await VerifySourcesAsync(cancellationToken).ConfigureAwait(false);
                        await VerifyDestinationsAsync(cancellationToken).ConfigureAwait(false);
                        entry = _checkpoint.Entries[index];
                    }
                    if (entry.RequestPending && entry.Source.Action == "hide") throw UnknownHide();
                    progress.Report(new(ItemId, "Copying cloud history", entry.Source.Key, index, _checkpoint.Entries.Count, "versions"));
                    // The intent precedes the request. A lost hide acknowledgment cannot be claimed
                    // from listing alone because another client can produce an identical hide marker.
                    Update(index, entry with { RequestPending = true });
                    CloudPrefixVersion copied;
                    try
                    {
                        copied = entry.Source.Action == "hide"
                            ? await _store.HideOnceAsync(entry.DestinationKey, cancellationToken).ConfigureAwait(false)
                            : await _store.CopyAsync(entry.Source, entry.DestinationKey, entry.OperationId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CloudMigrationRequestRejectedException)
                    {
                        Update(index, entry with { RequestPending = false });
                        throw;
                    }
                    ValidateCopy(entry, copied);
                    Update(index, entry with { Destination = Freeze(copied), RequestPending = false });
                    progress.Report(new(ItemId, "Verified cloud version", entry.DestinationKey, index + 1, _checkpoint.Entries.Count, "versions"));
                }
                await VerifySourcesAsync(cancellationToken).ConfigureAwait(false);
                await VerifyDestinationsAsync(cancellationToken).ConfigureAwait(false);
                await _binding.VerifyOriginalAsync(cancellationToken).ConfigureAwait(false);
                Persist(_checkpoint with { Phase = CloudPrefixMigrationPhase.Switching });
            }
            progress.Report(new(ItemId, "Updating cloud references", "Saving verified version mappings and switching the selected cloud folder."));
            await _binding.SwitchAsync(Mappings(), cancellationToken).ConfigureAwait(false);
            await _binding.VerifySwitchedAsync(Mappings(), cancellationToken).ConfigureAwait(false);
            Persist(_checkpoint with { Phase = CloudPrefixMigrationPhase.Switched });
            progress.Report(new(ItemId, "Cloud folder migrated", "The verified destination is active. Original cloud versions remain available for recovery.",
                _checkpoint.Entries.Count, _checkpoint.Entries.Count, "versions"));
        }
        finally { _gate.Release(); }
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureJournalHealthy();
            if (_checkpoint?.Phase != CloudPrefixMigrationPhase.Switched) throw Conflict("Cloud folder references have not finished switching.");
            await _store.ValidateAccessAsync(_sourcePrefix, _destinationPrefix, cancellationToken).ConfigureAwait(false);
            await VerifySourcesAsync(cancellationToken).ConfigureAwait(false);
            await VerifyDestinationsAsync(cancellationToken).ConfigureAwait(false);
            await _binding.VerifySwitchedAsync(Mappings(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureJournalHealthy();
            if (_checkpoint?.Phase == CloudPrefixMigrationPhase.Reverted) return;
            if (!CanRevert) throw Conflict("References may already use the new cloud folder. Retry their recovery before making further changes.");
            await _store.ValidateAccessAsync(_sourcePrefix, _destinationPrefix, cancellationToken).ConfigureAwait(false);
            await _binding.VerifyOriginalAsync(cancellationToken).ConfigureAwait(false);
            if (_checkpoint is null) return;
            await VerifySourcesAsync(cancellationToken).ConfigureAwait(false);
            await VerifyDestinationsAsync(cancellationToken, allowMissingOwned: _checkpoint.Phase == CloudPrefixMigrationPhase.Reverting).ConfigureAwait(false);
            Persist(_checkpoint with { Phase = CloudPrefixMigrationPhase.Reverting });
            for (var index = 0; index < _checkpoint.Entries.Count; index++)
            {
                var entry = _checkpoint.Entries[index];
                if (!entry.RequestPending) continue;
                if (entry.Source.Action != "upload" || _store is not ICloudPrefixMigrationCopyRecovery recovery)
                    throw Conflict("A cloud request has an unconfirmed outcome. Recover its receipt before reverting; all original versions remain intact.");
                progress.Report(new(ItemId, "Recovering unfinished cloud copy", entry.DestinationKey));
                var completed = await recovery.ReconcileCopyForRevertAsync(entry.Source, entry.DestinationKey,
                    entry.OperationId, cancellationToken).ConfigureAwait(false);
                if (completed is not null) ValidateCopy(entry, completed);
                Update(index, entry with { RequestPending = false, Destination = completed is null ? null : Freeze(completed) });
            }
            for (var index = _checkpoint.Entries.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = _checkpoint.Entries[index];
                if (entry.Destination is null || entry.Removed) continue;
                if (index % VerificationBatchSize == 0)
                {
                    await VerifySourcesAsync(cancellationToken).ConfigureAwait(false);
                    await VerifyDestinationsAsync(cancellationToken, allowMissingOwned: true).ConfigureAwait(false);
                }
                progress.Report(new(ItemId, "Reverting copied version", entry.DestinationKey));
                await _store.DeleteExactAsync(entry.Destination, cancellationToken).ConfigureAwait(false);
                Update(index, entry with { Removed = true });
            }
            await VerifyDestinationsAsync(cancellationToken, allowMissingOwned: true).ConfigureAwait(false);
            Persist(_checkpoint with { Phase = CloudPrefixMigrationPhase.Reverted });
        }
        finally { _gate.Release(); }
    }

    public async Task VerifyRevertedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureJournalHealthy();
            await _binding.VerifyOriginalAsync(cancellationToken).ConfigureAwait(false);
            if (_checkpoint is null) return;
            if (_checkpoint.Phase != CloudPrefixMigrationPhase.Reverted) throw Conflict("The cloud migration has not finished reverting.");
            await _store.ValidateAccessAsync(_sourcePrefix, _destinationPrefix, cancellationToken).ConfigureAwait(false);
            await VerifySourcesAsync(cancellationToken).ConfigureAwait(false);
            var target = await _store.ListAsync(_destinationPrefix, cancellationToken).ConfigureAwait(false);
            if (target.Count != 0) throw Conflict("The destination is not empty after recovery. Its remaining versions were retained.");
        }
        finally { _gate.Release(); }
    }

    public async Task KeepOriginalAsync(IProgress<MigrationProgress> progress, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureJournalHealthy();
            if (!CanKeepOriginal) throw Conflict("Cloud references may already be switching. Complete their recovery before continuing.");
            // Do not consult mutable remote history: another writer or an unacknowledged hide is
            // exactly why the user may need this exit. Neither remote ownership nor absence is inferred.
            await _binding.VerifyMaintenanceAsync(token).ConfigureAwait(false);
            await _binding.VerifyOriginalAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_checkpoint?.Phase != CloudPrefixMigrationPhase.OriginalKept)
                Persist((_checkpoint ?? new(1, ReviewedPlanFingerprint, ItemId, _store.AccountId, _store.BucketId,
                    _sourcePrefix, _destinationPrefix, _binding.MaintenanceReceipt, CloudPrefixMigrationPhase.Copying, []))
                    with { Phase = CloudPrefixMigrationPhase.OriginalKept });
            progress.Report(new(ItemId, "Original cloud location kept",
                "The original local references were verified. Partial cloud copies and all recovery records remain retained; no cloud objects were changed."));
        }
        finally { _gate.Release(); }
    }

    public async Task VerifyOriginalKeptAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureJournalHealthy();
            if (_checkpoint?.Phase != CloudPrefixMigrationPhase.OriginalKept)
                throw Conflict("The authenticated record does not confirm keeping the original cloud location.");
            await _binding.VerifyMaintenanceAsync(token).ConfigureAwait(false);
            await _binding.VerifyOriginalAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task VerifySourcesAsync(CancellationToken token)
    {
        await _binding.VerifyMaintenanceAsync(token).ConfigureAwait(false);
        var current = ValidateListing(await _store.ListAsync(_sourcePrefix, token).ConfigureAwait(false), _sourcePrefix);
        var expected = _checkpoint!.Entries.Select(entry => entry.Source).ToArray();
        var currentById = current.ToDictionary(value => value.FileId, StringComparer.Ordinal);
        if (current.Count != expected.Length || expected.Any(value => !currentById.TryGetValue(value.FileId, out var actual) || !SameVersion(value, actual)))
            throw Conflict("Cloud source versions or their protection changed during migration. Pause other clients and review the changed history; originals were not deleted.");
        var expectedByKey = expected.GroupBy(value => value.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Reverse().Select(value => value.FileId), StringComparer.Ordinal);
        foreach (var group in current.GroupBy(value => value.Key, StringComparer.Ordinal))
            if (!group.Select(value => value.FileId).SequenceEqual(expectedByKey[group.Key]))
                throw Conflict("The cloud provider returned a different version-history order. No further version was created.");
    }

    private async Task VerifyDestinationsAsync(CancellationToken token, bool allowMissingOwned = false)
    {
        var actual = ValidateListing(await _store.ListAsync(_destinationPrefix, token).ConfigureAwait(false), _destinationPrefix);
        if (_store is ICloudPrefixMigrationHideRecovery recovery)
        {
            for (var index = 0; index < _checkpoint!.Entries.Count; index++)
            {
                var pending = _checkpoint.Entries[index];
                if (!pending.RequestPending || pending.Source.Action != "hide") continue;
                var claimed = _checkpoint.Entries.Where(value => value.Destination is not null).Select(value => value.Destination!.FileId).ToArray();
                var receipt = await recovery.RecoverAcknowledgedHideAsync(pending.DestinationKey, claimed, token).ConfigureAwait(false);
                if (receipt is null) continue;
                ValidateCopy(pending, receipt);
                if (!actual.Any(value => SameVersion(value, receipt))) throw Conflict("An acknowledged hide marker changed or disappeared. Its history was retained for recovery.");
                Update(index, pending with { Destination = Freeze(receipt), RequestPending = false });
            }
        }
        var expected = _checkpoint!.Entries.Where(entry => entry.Destination is not null && !entry.Removed).ToArray();
        var expectedById = expected.ToDictionary(entry => entry.Destination!.FileId, StringComparer.Ordinal);
        var actualById = actual.ToDictionary(value => value.FileId, StringComparer.Ordinal);
        var pendingByOperation = _checkpoint.Entries.Where(value => value.RequestPending && value.Source.Action == "upload")
            .ToDictionary(value => value.OperationId, StringComparer.Ordinal);
        var pendingReceipts = new HashSet<string>(StringComparer.Ordinal);
        // Pending copy requests are reconciled by their stable operation receipt inside CopyAsync.
        // They are not automatically adopted from a listing, and any unknown marker still stops us.
        foreach (var version in actual)
        {
            if (expectedById.TryGetValue(version.FileId, out var entry))
            {
                if (!SameVersion(entry.Destination!, version)) throw Conflict("A copied version's metadata or protection changed. Nothing was overwritten.");
                continue;
            }
            if (!version.Metadata.TryGetValue(ReceiptMetadataKey, out var receipt) || !pendingByOperation.TryGetValue(receipt, out var pending) ||
                pending.DestinationKey != version.Key || !version.Metadata.TryGetValue(SourceMetadataKey, out var sourceId) || sourceId != pending.Source.FileId)
                throw Conflict("Another version appeared in the destination. Migration stopped without replacing or deleting it.");
            if (!pendingReceipts.Add(pending.OperationId))
                throw Conflict("Multiple destination versions claim the same pending migration receipt. No duplicate history was activated or deleted.");
            ValidateCopy(pending, version);
        }
        if (!allowMissingOwned && expected.Any(entry => !actualById.ContainsKey(entry.Destination!.FileId)))
            throw Conflict("A verified destination version is missing. No application references were switched.");
        var expectedByKey = expected.GroupBy(entry => entry.DestinationKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Where(entry => actualById.ContainsKey(entry.Destination!.FileId))
                .Reverse().Select(entry => entry.Destination!.FileId), StringComparer.Ordinal);
        foreach (var group in actual.GroupBy(value => value.Key, StringComparer.Ordinal))
        {
            var owned = group.Where(value => expectedById.ContainsKey(value.FileId)).Select(value => value.FileId);
            var ordered = expectedByKey.GetValueOrDefault(group.Key) ?? [];
            if (!owned.SequenceEqual(ordered)) throw Conflict("Destination version order changed. History cannot be safely activated.");
        }
    }

    private void ValidateCheckpoint(CloudPrefixMigrationCheckpoint checkpoint)
    {
        if (checkpoint.Schema != 1 || checkpoint.PlanFingerprint != ReviewedPlanFingerprint || checkpoint.ItemId != ItemId ||
            checkpoint.AccountId != _store.AccountId || checkpoint.BucketId != _store.BucketId ||
            checkpoint.SourcePrefix != _sourcePrefix || checkpoint.DestinationPrefix != _destinationPrefix ||
            checkpoint.MaintenanceReceipt != _binding.MaintenanceReceipt ||
            !Enum.IsDefined(checkpoint.Phase) || checkpoint.Entries is null)
            throw Conflict("The cloud recovery record does not match the reviewed account, bucket and folders.");
        ValidateListing(checkpoint.Entries.Select(entry => entry.Source).ToArray(), _sourcePrefix);
        var previousIncomplete = false;
        foreach (var entry in checkpoint.Entries)
        {
            if (entry.DestinationKey != _keyMap.Map(entry.Source.Key) || entry.OperationId != OperationId(entry.Source.FileId) ||
                entry.RequestPending && entry.Destination is not null || entry.Removed && entry.Destination is null ||
                entry.Removed && checkpoint.Phase is not (CloudPrefixMigrationPhase.Reverting or CloudPrefixMigrationPhase.Reverted or CloudPrefixMigrationPhase.OriginalKept) ||
                previousIncomplete && entry.Destination is not null)
                throw Conflict("The cloud recovery record contains inconsistent version receipts.");
            if (entry.Destination is null) previousIncomplete = true;
            else ValidateCopy(entry, entry.Destination);
        }
        if (checkpoint.Phase is CloudPrefixMigrationPhase.Switching or CloudPrefixMigrationPhase.Switched && previousIncomplete)
            throw Conflict("The cloud recovery record claims activation without complete version receipts.");
    }

    private static IReadOnlyList<CloudPrefixVersion> ValidateListing(IReadOnlyList<CloudPrefixVersion> versions, string prefix)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CloudPrefixVersion>();
        foreach (var version in versions)
        {
            if (version is null || string.IsNullOrWhiteSpace(version.FileId) || !ids.Add(version.FileId) ||
                !version.Key.StartsWith(prefix, StringComparison.Ordinal) || version.Key.Length <= prefix.Length ||
                version.Action is not ("upload" or "hide") || version.Size < 0 || version.UploadedAtMillis < 0 ||
                version.Metadata is null || string.IsNullOrWhiteSpace(version.ContentType))
                throw Conflict("The cloud inventory is incomplete or contains unsupported versions. No migration may start.");
            if (version.Action == "upload" && (version.Sha1 is not { Length: 40 } || !version.Sha1.All(Uri.IsHexDigit)))
                throw Conflict("A historical version has no whole-file checksum. It must be verified before migration.");
            if (!version.ProtectionReadable || version.RetentionMode is not null || version.RetainUntilMillis is not null ||
                version.LegalHold is not (null or "off") ||
                !((version.EncryptionMode is null && version.EncryptionAlgorithm is null) ||
                  (version.EncryptionMode == "SSE-B2" && version.EncryptionAlgorithm == "AES256")) || version.ReplicationStatus is not null)
                throw Conflict("This history uses retention, encryption, replication, or unreadable protection settings. It needs a preservation-aware provider migration; no protection was weakened.");
            if (version.Action == "hide" && (version.Size != 0 || version.Metadata.Count != 0))
                throw Conflict("A hide marker has unexpected content or metadata.");
            result.Add(Freeze(version));
        }
        return result;
    }

    private static void ValidateCopy(CloudPrefixMigrationEntry entry, CloudPrefixVersion copy)
    {
        var source = entry.Source;
        if (copy.Metadata is null || copy.Action == "upload" &&
            (!copy.Metadata.TryGetValue(ReceiptMetadataKey, out var receipt) || receipt != entry.OperationId ||
             !copy.Metadata.TryGetValue(SourceMetadataKey, out var sourceId) || sourceId != source.FileId))
            throw Conflict("The cloud copy does not carry this migration's exact source and operation receipt.");
        if (string.IsNullOrWhiteSpace(copy.FileId) || copy.FileId == source.FileId || copy.Key != entry.DestinationKey ||
            copy.Action != source.Action || copy.Size != source.Size || !string.Equals(copy.Sha1, source.Sha1, StringComparison.OrdinalIgnoreCase) ||
            copy.ContentType != source.ContentType || !copy.ProtectionReadable || copy.RetentionMode != source.RetentionMode ||
            copy.RetainUntilMillis != source.RetainUntilMillis || copy.LegalHold != source.LegalHold ||
            copy.EncryptionMode != source.EncryptionMode || copy.EncryptionAlgorithm != source.EncryptionAlgorithm || copy.ReplicationStatus != source.ReplicationStatus ||
            source.Metadata.Any(pair => !copy.Metadata.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw Conflict("The copied cloud version does not preserve the reviewed content, metadata and protection.");
    }

    private static bool SameVersion(CloudPrefixVersion left, CloudPrefixVersion right) =>
        left.FileId == right.FileId && left.Key == right.Key && left.Action == right.Action && left.Size == right.Size &&
        string.Equals(left.Sha1, right.Sha1, StringComparison.OrdinalIgnoreCase) && left.UploadedAtMillis == right.UploadedAtMillis &&
        left.ContentType == right.ContentType && left.ProtectionReadable == right.ProtectionReadable &&
        left.RetentionMode == right.RetentionMode && left.RetainUntilMillis == right.RetainUntilMillis && left.LegalHold == right.LegalHold &&
        left.EncryptionMode == right.EncryptionMode && left.EncryptionAlgorithm == right.EncryptionAlgorithm && left.ReplicationStatus == right.ReplicationStatus &&
        left.Metadata.Count == right.Metadata.Count && left.Metadata.All(pair => right.Metadata.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static CloudPrefixVersion Freeze(CloudPrefixVersion value) => value with
        { Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value.Metadata, StringComparer.Ordinal)) };
    private IReadOnlyList<CloudPrefixVersionMapping> Mappings() => _checkpoint!.Entries.Where(entry => entry.Destination is not null && !entry.Removed)
        .Select(entry => new CloudPrefixVersionMapping(entry.Source, entry.Destination!)).ToArray();
    private string OperationId(string sourceId) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new[] { "CloudInlet.CloudPrefixMigration.v1", ReviewedPlanFingerprint, ItemId, _store.AccountId, _store.BucketId, _sourcePrefix, _destinationPrefix, sourceId }))).ToLowerInvariant();
    private void Update(int index, CloudPrefixMigrationEntry entry)
    { Persist(_checkpoint! with { Entries = MigrationSnapshotList<CloudPrefixMigrationEntry>.Set(_checkpoint.Entries, index, entry) }); }
    private void Persist(CloudPrefixMigrationCheckpoint checkpoint)
    {
        EnsureJournalHealthy();
        try { _journal.Write(checkpoint); _checkpoint = checkpoint; }
        catch (Exception error) { _journalFailure = error; throw new MigrationJournalException("Cloud migration stopped because its recovery record could not be saved.", error); }
    }
    private void EnsureJournalHealthy()
    { if (_journalFailure is not null) throw new MigrationJournalException("Reopen the saved cloud migration before making further changes.", _journalFailure); }
    private static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.EndsWith('/') || prefix.StartsWith('/') || prefix.Contains('\\') ||
            prefix.Any(char.IsControl) || prefix.Split('/').SkipLast(1).Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("A nonempty, normalized cloud folder prefix ending in '/' is required.");
    }
    private static InvalidDataException Conflict(string message) => new(message);
    private static InvalidDataException UnknownHide() => Conflict("The hide-marker request has an unconfirmed outcome. B2 provides no ownership token for hide markers, so CloudInlet will not repeat or delete it automatically. Original history is intact; inspect the saved receipt before continuing.");
}

/// <summary>Only a provider response proving the creating request was rejected may clear a pending intent.</summary>
public sealed class CloudMigrationRequestRejectedException(string message, Exception? inner = null) : IOException(message, inner);

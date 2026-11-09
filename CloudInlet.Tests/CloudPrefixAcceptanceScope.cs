using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;

namespace CloudInlet.Tests;

/// <summary>Test-only ownership boundary. A listing can verify ownership, never establish it.</summary>
internal sealed class CloudPrefixAcceptanceScope
{
    internal static readonly string[] RequiredCapabilities = ["listBuckets", "listFiles", "readFiles", "writeFiles", "deleteFiles",
        "readBucketEncryption", "readBucketRetentions", "readFileRetentions", "readFileLegalHolds", "readBucketReplications"];
    internal sealed record OwnedVersion(string Id, string Key);
    private readonly Dictionary<string, OwnedVersion> _owned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private string? _accountId;
    internal string Bucket { get; }
    internal string Root { get; }
    internal string Source => Root + "CloudBay/";
    internal string Destination => Root + "CloudInlet/";
    internal bool WritesEnabled { get; private set; }
    internal bool CleanupEnabled { get; private set; }
    internal bool RevertEnabled { get; private set; }
    internal IReadOnlyCollection<OwnedVersion> Owned => _owned.Values;
    internal IReadOnlyCollection<OwnedVersion> RemainingOwned => _owned.Values.Where(value => !_deleted.Contains(value.Id)).ToArray();
    internal IReadOnlyCollection<string> DeletedIds => _deleted;

    internal CloudPrefixAcceptanceScope(string bucket, string? allowedPrefix, Guid run)
    {
        if (string.IsNullOrWhiteSpace(bucket) || run == Guid.Empty) throw new InvalidDataException("A restricted bucket and fresh run identity are required.");
        allowedPrefix ??= "";
        if (allowedPrefix.Length != 0 && (!allowedPrefix.EndsWith('/') || allowedPrefix.Contains('\\') ||
            allowedPrefix.Split('/').Any(p => p is "." or "..") || allowedPrefix.Any(char.IsControl)))
            throw new InvalidDataException("The restricted prefix cannot contain ambiguous segments.");
        Bucket = bucket; Root = allowedPrefix + "CloudInletMigrationAcceptance/" + run.ToString("N") + "/";
    }

    internal static string[] MissingCapabilities(CloudAccount account) =>
        RequiredCapabilities.Where(value => !account.Capabilities.Contains(value, StringComparer.Ordinal)).ToArray();

    internal void EnableWrites(CloudAccount account, B2CloudPrefixMigrationInventory inventory)
    {
        if (WritesEnabled || account.AllowedBucketId != Bucket || MissingCapabilities(account).Length != 0 ||
            inventory.AccountId != account.AccountId || inventory.BucketId != Bucket || inventory.SourcePrefix != Source ||
            inventory.DestinationPrefix != Destination || inventory.SourceVersions.Count != 0 || inventory.DestinationVersions.Count != 0 ||
            inventory.UnfinishedUploads.Count != 0)
            throw new InvalidDataException("Acceptance needs a complete restricted-key preflight and two empty fresh namespaces.");
        RequirePolicy(inventory);
        _accountId = account.AccountId;
        WritesEnabled = true;
    }

    internal void RequirePolicy(B2CloudPrefixMigrationInventory inventory)
    {
        if (_accountId is not null && inventory.AccountId != _accountId || inventory.BucketId != Bucket || inventory.SourcePrefix != Source || inventory.DestinationPrefix != Destination ||
            inventory.UnfinishedUploads.Count != 0 || inventory.Blockers.Any(blocker => blocker.Code is not
                ("bucket-encryption-preservation-required" or "version-encryption-preservation-required" or "destination-not-empty")) ||
            !((inventory.DefaultEncryptionMode is null && inventory.DefaultEncryptionAlgorithm is null) ||
                (inventory.DefaultEncryptionMode == "SSE-B2" && inventory.DefaultEncryptionAlgorithm == "AES256")))
            throw new InvalidDataException("The fixture policy cannot safely preserve and remove its own test history.");
        foreach (var version in inventory.SourceVersions.Concat(inventory.DestinationVersions))
            if (!version.ProtectionReadable || version.RetentionMode is not null || version.RetainUntilMillis is not null ||
                version.LegalHold is not (null or "off") || version.ReplicationStatus is not null)
                throw new InvalidDataException("Protected fixture versions must be retained for explicit recovery.");
    }

    // The caller authenticates these acknowledgments against its protected production journals.
    // Provider listings only verify that exact evidence; they can never create ownership.
    internal void RestoreForRecovery(CloudAccount account, B2CloudPrefixMigrationInventory inventory,
        IReadOnlyCollection<OwnedVersion> acknowledged)
    {
        if (WritesEnabled || CleanupEnabled || RevertEnabled || _owned.Count != 0 || acknowledged.Count == 0 ||
            account.AllowedBucketId != Bucket || MissingCapabilities(account).Length != 0 || inventory.AccountId != account.AccountId)
            throw new InvalidDataException("Recovery needs untouched scope state and complete restricted-account evidence.");
        RequirePolicy(inventory);
        var receipts = new Dictionary<string, OwnedVersion>(StringComparer.Ordinal);
        foreach (var receipt in acknowledged)
        {
            RequireKey(receipt.Key);
            if (string.IsNullOrWhiteSpace(receipt.Id) || !receipts.TryAdd(receipt.Id, receipt))
                throw new InvalidDataException("Recovery acknowledgments must have unique immutable identities.");
        }
        var actual = inventory.SourceVersions.Concat(inventory.DestinationVersions).ToArray();
        if (actual.Length != receipts.Count || actual.Select(value => value.FileId).Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Any(value => !receipts.TryGetValue(value.FileId, out var owned) || owned.Key != value.Key))
            throw new InvalidDataException("Recovery inventory does not match the complete acknowledged evidence.");
        foreach (var receipt in receipts.Values) _owned.Add(receipt.Id, receipt);
        _accountId = account.AccountId;
        WritesEnabled = true;
    }

    // The unfinished IDs must come from authenticated provider journals, never discovery.
    internal void RestoreForMultipartRecovery(CloudAccount account, B2CloudPrefixMigrationInventory inventory,
        IReadOnlyCollection<OwnedVersion> acknowledged, IReadOnlyCollection<OwnedVersion> acknowledgedUnfinished)
    {
        if (acknowledgedUnfinished.Count == 0 ||
            acknowledgedUnfinished.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != acknowledgedUnfinished.Count ||
            inventory.UnfinishedUploads.Count != acknowledgedUnfinished.Count ||
            inventory.SourceVersions.Any(value => value.Action == "start") ||
            inventory.UnfinishedUploads.Any(value => value.Action != "start" ||
                !value.Key.StartsWith(Destination, StringComparison.Ordinal) ||
                acknowledgedUnfinished.Count(receipt => receipt.Id == value.FileId && receipt.Key == value.Key) != 1) ||
            inventory.UnfinishedUploads.Select(value => value.FileId).Distinct(StringComparer.Ordinal).Count() != inventory.UnfinishedUploads.Count ||
            inventory.DestinationVersions.Where(value => value.Action == "start").Any(value =>
                !inventory.UnfinishedUploads.Any(pending => pending.FileId == value.FileId && pending.Key == value.Key)))
            throw new InvalidDataException("Multipart recovery requires exact authenticated unfinished destination identities.");
        RestoreForRecovery(account, inventory with
        {
            DestinationVersions = inventory.DestinationVersions.Where(value => value.Action != "start").Concat(inventory.UnfinishedUploads).ToArray(),
            UnfinishedUploads = [],
            Blockers = inventory.Blockers.Where(value => value.Code != "unfinished-uploads").ToArray()
        }, acknowledged);
    }

    internal void RequireKey(string key)
    {
        if (!(key.StartsWith(Source, StringComparison.Ordinal) && key.Length > Source.Length ||
            key.StartsWith(Destination, StringComparison.Ordinal) && key.Length > Destination.Length) ||
            key.Contains('\\') || key.Any(char.IsControl) || key.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("The request is outside this exact acceptance namespace.");
    }

    internal void RequireWrite(string key)
    {
        RequireKey(key);
        if (!WritesEnabled || CleanupEnabled || RevertEnabled) throw new InvalidDataException("Fixture creation is not enabled.");
    }

    internal void Acknowledge(string id, string key)
    {
        RequireWrite(key);
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("A creation acknowledgment needs its immutable ID.");
        if (_owned.TryGetValue(id, out var existing) && existing.Key != key)
            throw new InvalidDataException("A provider acknowledgment reused an immutable identity.");
        _owned[id] = new(id, key);
    }

    internal OwnedVersion RequireOwned(string id)
    {
        if (!_owned.TryGetValue(id, out var value)) throw new InvalidDataException("The version has no creation acknowledgment from this run.");
        RequireKey(value.Key); return value;
    }

    internal void EnableCleanup(B2CloudPrefixMigrationInventory inventory)
    {
        if (!WritesEnabled) throw new InvalidDataException("No fixture was created.");
        RequireRemainingInventory(inventory);
        CleanupEnabled = true;
    }

    internal void EnableRevert(B2CloudPrefixMigrationInventory inventory)
    {
        if (!WritesEnabled || CleanupEnabled || RevertEnabled) throw new InvalidDataException("Destination recovery requires an active, reviewed fixture.");
        RequireRemainingInventory(inventory);
        if (inventory.DestinationVersions.Count == 0) throw new InvalidDataException("There are no acknowledged destination versions to recover.");
        RevertEnabled = true;
    }

    internal void RequireRemainingInventory(B2CloudPrefixMigrationInventory inventory)
    {
        RequirePolicy(inventory);
        var actual = inventory.SourceVersions.Concat(inventory.DestinationVersions).ToArray();
        if (actual.Length != _owned.Count - _deleted.Count || actual.Select(v => v.FileId).Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Any(v => _deleted.Contains(v.FileId) || !_owned.TryGetValue(v.FileId, out var owned) || owned.Key != v.Key))
            throw new InvalidDataException("The fixture inventory contains missing or unacknowledged versions. Nothing may be deleted.");
    }

    internal void RequireDelete(string id, string key)
    {
        if ((!CleanupEnabled && !(RevertEnabled && key.StartsWith(Destination, StringComparison.Ordinal))) ||
            _deleted.Contains(id) || RequireOwned(id).Key != key)
            throw new InvalidDataException("Deletion requires an exact remaining acknowledged identity; recovery can delete only destination versions.");
    }

    internal void AcknowledgeDeletion(string id, string key)
    {
        RequireDelete(id, key);
        _deleted.Add(id);
    }
}

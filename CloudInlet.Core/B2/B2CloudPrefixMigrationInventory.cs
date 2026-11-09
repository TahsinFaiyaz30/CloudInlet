using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CloudInlet.Core.Migration;

namespace CloudInlet.Core.B2;

/// <summary>A read-only preflight snapshot, never authorization or a provider-wide writer lock.</summary>
public sealed record B2CloudPrefixMigrationInventory(string AccountId, string BucketId, long BucketRevision,
    string SourcePrefix, string DestinationPrefix, string? DefaultEncryptionMode, string? DefaultEncryptionAlgorithm,
    IReadOnlyList<CloudPrefixVersion> SourceVersions, IReadOnlyList<CloudPrefixVersion> DestinationVersions,
    IReadOnlyList<CloudPrefixVersion> UnfinishedUploads, IReadOnlyList<B2CloudMigrationBlocker> Blockers);

public sealed record B2CloudMigrationBlocker(string Code, string Description);

public sealed partial class B2CloudStore
{
    /// <summary>
    /// Reads every version and unfinished upload under both reviewed prefixes and the applicable bucket
    /// policy. It performs no cloud mutations, downloads or local writes. Execution still needs durable
    /// copy recovery, protection-preserving requests and an atomic application-reference binding.
    /// </summary>
    public async Task<B2CloudPrefixMigrationInventory> InspectPrefixMigrationAsync(string bucketId, string sourcePrefix,
        string destinationPrefix, CancellationToken cancellationToken = default)
    {
        MigrationFolderPrefix(sourcePrefix); MigrationFolderPrefix(destinationPrefix);
        if (sourcePrefix.StartsWith(destinationPrefix, StringComparison.Ordinal) || destinationPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new ArgumentException("Migration folders must be separate, non-overlapping prefixes.");
        ValidateAccess(bucketId, sourcePrefix, "listFiles"); ValidateAccess(bucketId, destinationPrefix, "listFiles");
        ValidateCapability("listBuckets");
        var account = Current.Account.AccountId;
        var budget = new MigrationInventoryBudget();
        var blockers = new Dictionary<string, B2CloudMigrationBlocker>(StringComparer.Ordinal);
        void Block(string code, string description) => blockers.TryAdd(code, new(code, description));
        using var initial = await MigrationBucketAsync(account, bucketId, cancellationToken).ConfigureAwait(false);
        var bucket = initial.RootElement.GetProperty("buckets")[0];
        var revision = MigrationLong(bucket, "revision");
        var encryption = MigrationProtectedValue(bucket, "defaultServerSideEncryption", out var encryptionReadable);
        string? mode = null, algorithm = null;
        if (!encryptionReadable) Block("bucket-encryption-unreadable", "The application key cannot read the bucket's effective encryption policy.");
        else
        {
            MigrationObject(encryption);
            mode = MigrationNullableString(encryption, "mode"); algorithm = MigrationNullableString(encryption, "algorithm");
            // B2's effective default may be SSE-B2/AES256 even without a stored configuration.
            // Record it faithfully; a filtered null must never be interpreted as unencrypted.
            if (mode is not null || algorithm is not null)
                Block("bucket-encryption-preservation-required", "The effective bucket encryption must be preserved explicitly by the migration transport.");
        }
        var protection = MigrationProtectedValue(bucket, "fileLockConfiguration", out var protectionReadable);
        if (!protectionReadable) Block("bucket-retention-unreadable", "The application key cannot read the bucket retention policy.");
        else
        {
            MigrationObject(protection);
            if (!protection.TryGetProperty("isFileLockEnabled", out var locked) || locked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw MigrationInventoryInvalid();
            if (!protection.TryGetProperty("defaultRetention", out var retention)) throw MigrationInventoryInvalid();
            MigrationObject(retention);
            if (MigrationNullableString(retention, "mode") is not null || MigrationRequired(retention, "period").ValueKind != JsonValueKind.Null)
                Block("bucket-default-retention", "New copies would inherit bucket retention. A retention-aware migration is required.");
        }
        var lifecycle = MigrationRequired(bucket, "lifecycleRules");
        if (lifecycle.ValueKind != JsonValueKind.Array) throw MigrationInventoryInvalid();
        foreach (var rule in lifecycle.EnumerateArray())
        {
            MigrationObject(rule);
            var prefix = MigrationString(rule, "fileNamePrefix", allowEmpty: true);
            if (MigrationPrefixesOverlap(prefix, sourcePrefix) || MigrationPrefixesOverlap(prefix, destinationPrefix))
                Block("lifecycle-policy", "Lifecycle rules overlap a selected cloud folder and could change its retained history.");
        }
        if (!bucket.TryGetProperty("replicationConfiguration", out var replication))
            Block("bucket-replication-unreadable", "The response does not establish the bucket's replication policy.");
        else
        {
            if (replication.ValueKind == JsonValueKind.Object && replication.TryGetProperty("isClientAuthorizedToRead", out _))
            {
                replication = MigrationProtectedValue(bucket, "replicationConfiguration", out var readable);
                if (!readable) Block("bucket-replication-unreadable", "The application key cannot read the bucket's replication policy.");
            }
            if (replication.ValueKind != JsonValueKind.Null)
            {
                MigrationObject(replication);
                if (!Current.Account.Capabilities.Contains("readBucketReplications", StringComparer.Ordinal))
                    Block("bucket-replication-unreadable", "The application key does not establish permission to read the complete replication policy.");
                if (!replication.TryGetProperty("asReplicationSource", out var asSource) || !replication.TryGetProperty("asReplicationDestination", out var asDestination))
                    Block("bucket-replication-unreadable", "The response does not establish both directions of the bucket's replication policy.");
                else if (asSource.ValueKind != JsonValueKind.Null || asDestination.ValueKind != JsonValueKind.Null)
                    Block("replication-policy", "Bucket replication needs an explicit preservation-aware migration.");
            }
        }

        var sources = await MigrationVersionsAsync(account, bucketId, sourcePrefix, budget, cancellationToken).ConfigureAwait(false);
        var destinations = await MigrationVersionsAsync(account, bucketId, destinationPrefix, budget, cancellationToken).ConfigureAwait(false);
        var unfinishedSource = await MigrationUnfinishedAsync(account, bucketId, sourcePrefix, budget, cancellationToken).ConfigureAwait(false);
        var unfinishedTarget = await MigrationUnfinishedAsync(account, bucketId, destinationPrefix, budget, cancellationToken).ConfigureAwait(false);
        if (destinations.Count != 0) Block("destination-not-empty", "The proposed cloud folder already has retained versions; migration must not merge or replace them.");
        if (unfinishedSource.Count != 0 || unfinishedTarget.Count != 0 || sources.Any(v => v.Action == "start") || destinations.Any(v => v.Action == "start"))
            Block("unfinished-uploads", "An unfinished large upload exists in a selected folder. Finish or explicitly recover it before migration.");
        foreach (var version in sources.Concat(destinations).Concat(unfinishedSource).Concat(unfinishedTarget))
        {
            if (!version.ProtectionReadable) Block("version-protection-unreadable", "The application key cannot read all historical version protection settings.");
            if (version.RetentionMode is not null || version.RetainUntilMillis is not null || version.LegalHold is not (null or "off"))
                Block("protected-versions", "Historical versions use retention or legal hold and need protection-aware copying.");
            if (version.EncryptionMode is not null || version.EncryptionAlgorithm is not null)
                Block("version-encryption-preservation-required", "Historical encryption settings must be preserved explicitly when copying each version.");
            if (version.ReplicationStatus is not null) Block("replicated-versions", "Historical versions have replication state that must be preserved.");
            if (version.Action == "upload" && !IsSha1(version.Sha1)) Block("missing-whole-file-checksum", "A historical version lacks a verified whole-file checksum.");
            if (version.Key == sourcePrefix || version.Key == destinationPrefix) Block("root-marker", "The selected prefix has its own root marker, which needs explicit mapping during migration.");
        }
        if (sources.Any(version => version.Metadata.ContainsKey(CloudPrefixMigration.ReceiptMetadataKey) || version.Metadata.ContainsKey(CloudPrefixMigration.SourceMetadataKey)))
            Block("existing-migration-receipts", "Source history already has migration receipts that must remain intact.");
        using var final = await MigrationBucketAsync(account, bucketId, cancellationToken).ConfigureAwait(false);
        if (MigrationLong(final.RootElement.GetProperty("buckets")[0], "revision") != revision)
            throw new InvalidDataException("The bucket policy changed during migration review. Refresh the inventory before proceeding.");
        MigrationAccount(account);
        return new(account, bucketId, revision, sourcePrefix, destinationPrefix, mode, algorithm,
            sources, destinations, Array.AsReadOnly(unfinishedSource.Concat(unfinishedTarget).ToArray()), Array.AsReadOnly(blockers.Values.ToArray()));
    }

    private async Task<JsonDocument> MigrationBucketAsync(string account, string bucket, CancellationToken token)
    {
        MigrationAccount(account); ValidateCapability("listBuckets");
        var document = await ApiAsync("b2_list_buckets", new { accountId = account, bucketId = bucket }, token).ConfigureAwait(false);
        try
        {
            MigrationObject(document.RootElement);
            var values = MigrationRequired(document.RootElement, "buckets");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 1 ||
                MigrationString(values[0], "accountId") != account || MigrationString(values[0], "bucketId") != bucket)
                throw MigrationInventoryInvalid();
            MigrationAccount(account);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private async Task<IReadOnlyList<CloudPrefixVersion>> MigrationVersionsAsync(string account, string bucket, string prefix,
        MigrationInventoryBudget budget, CancellationToken token)
    {
        var result = new List<CloudPrefixVersion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<(string, string)>();
        var closedKeys = new HashSet<string>(StringComparer.Ordinal);
        string? nextName = null, nextId = null, previousKey = null;
        long? previousTimestamp = null;
        do
        {
            MigrationAccount(account); ValidateAccess(bucket, prefix, "listFiles");
            var limit = ListPageSize;
            using var json = await ApiAsync("b2_list_file_versions", new
                { bucketId = bucket, prefix, startFileName = nextName, startFileId = nextId, maxFileCount = limit }, token).ConfigureAwait(false);
            var root = json.RootElement; budget.Include(root);
            var files = MigrationRequired(root, "files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > limit) throw MigrationInventoryInvalid();
            var name = MigrationNullableString(root, "nextFileName"); var id = MigrationNullableString(root, "nextFileId");
            if ((name is null) != (id is null) || name is not null &&
                (name.Length == 0 || id!.Length == 0 || !name.StartsWith(prefix, StringComparison.Ordinal) || files.GetArrayLength() == 0 || !cursors.Add((name, id))))
                throw MigrationInventoryInvalid();
            foreach (var value in files.EnumerateArray())
            {
                var version = MigrationVersion(value, account, bucket, prefix);
                if (!ids.Add(version.FileId)) throw MigrationInventoryInvalid();
                if (version.Key != previousKey)
                {
                    if (!closedKeys.Add(version.Key)) throw MigrationInventoryInvalid();
                    previousKey = version.Key; previousTimestamp = null;
                }
                if (previousTimestamp is { } timestamp && version.UploadedAtMillis > timestamp) throw MigrationInventoryInvalid();
                previousTimestamp = version.UploadedAtMillis;
                result.Add(version); budget.AddVersion();
            }
            nextName = name; nextId = id;
        } while (nextName is not null);
        return result.AsReadOnly();
    }

    private async Task<IReadOnlyList<CloudPrefixVersion>> MigrationUnfinishedAsync(string account, string bucket, string prefix,
        MigrationInventoryBudget budget, CancellationToken token)
    {
        var result = new List<CloudPrefixVersion>(); var ids = new HashSet<string>(StringComparer.Ordinal); var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        do
        {
            MigrationAccount(account); ValidateAccess(bucket, prefix, "listFiles");
            // The documented unfinished-upload page limit is smaller than the version-list limit.
            var limit = Math.Min(ListPageSize, 100);
            using var json = await ApiAsync("b2_list_unfinished_large_files", new
                { bucketId = bucket, namePrefix = prefix, startFileId = next, maxFileCount = limit }, token).ConfigureAwait(false);
            var root = json.RootElement; budget.Include(root);
            var files = MigrationRequired(root, "files"); var cursor = MigrationNullableString(root, "nextFileId");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > limit || cursor is not null &&
                (cursor.Length == 0 || files.GetArrayLength() == 0 || !cursors.Add(cursor))) throw MigrationInventoryInvalid();
            foreach (var value in files.EnumerateArray())
            {
                var version = MigrationVersion(value, account, bucket, prefix);
                if (version.Action != "start" || !ids.Add(version.FileId)) throw MigrationInventoryInvalid();
                result.Add(version); budget.AddVersion();
            }
            next = cursor;
        } while (next is not null);
        return result.AsReadOnly();
    }

    private static CloudPrefixVersion MigrationVersion(JsonElement value, string account, string bucket, string prefix,
        bool copyAcknowledgment = false)
    {
        MigrationObject(value);
        if (MigrationString(value, "accountId") != account || MigrationString(value, "bucketId") != bucket) throw MigrationInventoryInvalid();
        var key = MigrationString(value, "fileName"); var action = MigrationString(value, "action");
        if (copyAcknowledgment)
        {
            // b2_copy_file identifies its acknowledgment as "copy", while the same
            // immutable version is an "upload" in history and b2_get_file_info.
            // Only that endpoint may translate the action; all other fields still
            // pass the shared parser and exact receipt/independent-info checks.
            if (action is not ("copy" or "upload")) throw MigrationInventoryInvalid();
            action = "upload";
        }
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || action is not ("upload" or "hide" or "start")) throw MigrationInventoryInvalid();
        var metadata = MigrationRequired(value, "fileInfo"); MigrationObject(metadata);
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in metadata.EnumerateObject())
            if (property.Value.ValueKind != JsonValueKind.String || !info.TryAdd(property.Name, property.Value.GetString()!)) throw MigrationInventoryInvalid();
        var length = MigrationLong(value, "contentLength"); var timestamp = MigrationLong(value, "uploadTimestamp");
        var sha1 = MigrationNullableString(value, "contentSha1");
        if (action == "upload" && sha1 == "none") sha1 = info.GetValueOrDefault("large_file_sha1");
        var type = MigrationString(value, "contentType");
        var readable = true; string? retentionMode = null, legalHold = null, encryptionMode = null, encryptionAlgorithm = null; long? retainUntil = null;
        if (action == "hide")
        {
            // B2 may use null or SHA-1(empty) for a marker's absent payload. Keep
            // listings and acknowledged receipts comparable across both forms.
            if (string.Equals(sha1, "da39a3ee5e6b4b0d3255bfef95601890afd80709", StringComparison.OrdinalIgnoreCase)) sha1 = null;
            if (length != 0 || info.Count != 0 || sha1 is not null || type != "application/x-bz-hide-marker") throw MigrationInventoryInvalid();
            // B2 omits protection/encryption on hide markers. They do not contain payload.
            if (value.TryGetProperty("fileRetention", out _) || value.TryGetProperty("legalHold", out _) || value.TryGetProperty("serverSideEncryption", out _))
                throw MigrationInventoryInvalid();
        }
        else
        {
            if (action == "start" && length != 0) throw MigrationInventoryInvalid();
            var retention = MigrationProtectedValue(value, "fileRetention", out var retentionReadable);
            var hold = MigrationProtectedValue(value, "legalHold", out var holdReadable);
            readable = retentionReadable && holdReadable;
            if (retentionReadable)
            {
                MigrationObject(retention); retentionMode = MigrationNullableString(retention, "mode");
                var until = MigrationRequired(retention, "retainUntilTimestamp");
                if (until.ValueKind != JsonValueKind.Null) retainUntil = MigrationLong(retention, "retainUntilTimestamp");
            }
            if (holdReadable)
            {
                if (hold.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw MigrationInventoryInvalid();
                legalHold = hold.ValueKind == JsonValueKind.Null ? null : hold.GetString();
            }
            if (value.TryGetProperty("serverSideEncryption", out var encryption))
            {
                MigrationObject(encryption); encryptionMode = MigrationNullableString(encryption, "mode"); encryptionAlgorithm = MigrationNullableString(encryption, "algorithm");
            }
            else readable = false;
        }
        var replication = value.TryGetProperty("replicationStatus", out _) ? MigrationNullableString(value, "replicationStatus") : null;
        return new(MigrationString(value, "fileId"), key, action, length, sha1, timestamp, type,
            new ReadOnlyDictionary<string, string>(info), readable, retentionMode, retainUntil, legalHold, encryptionMode, encryptionAlgorithm, replication);
    }

    private static JsonElement MigrationProtectedValue(JsonElement parent, string name, out bool readable)
    {
        readable = false;
        if (!parent.TryGetProperty(name, out var property)) return default;
        MigrationObject(property);
        var authorized = MigrationRequired(property, "isClientAuthorizedToRead"); var value = MigrationRequired(property, "value");
        if (authorized.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw MigrationInventoryInvalid();
        readable = authorized.ValueKind == JsonValueKind.True;
        if (!readable && value.ValueKind != JsonValueKind.Null) throw MigrationInventoryInvalid();
        return value;
    }
    private void MigrationAccount(string account)
    { if (Current.Account.AccountId != account) throw new InvalidDataException("The connected account changed during migration review."); }
    private static void MigrationFolderPrefix(string prefix)
    {
        ValidateKey(prefix);
        if (!prefix.EndsWith('/') || prefix.StartsWith('/') || prefix.Contains('\\') || prefix.Split('/').SkipLast(1).Any(v => v is "" or "." or ".."))
            throw new ArgumentException("A normalized, nonempty cloud folder ending in '/' is required.");
    }
    private static bool MigrationPrefixesOverlap(string left, string right) => left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal);
    private static void MigrationObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw MigrationInventoryInvalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name)) throw MigrationInventoryInvalid();
    }
    private static JsonElement MigrationRequired(JsonElement value, string name)
    { MigrationObject(value); return value.TryGetProperty(name, out var property) ? property : throw MigrationInventoryInvalid(); }
    private static string? MigrationNullableString(JsonElement value, string name)
    {
        var property = MigrationRequired(value, name);
        return property.ValueKind == JsonValueKind.Null ? null : property.ValueKind == JsonValueKind.String ? property.GetString() : throw MigrationInventoryInvalid();
    }
    private static string MigrationString(JsonElement value, string name, bool allowEmpty = false)
    {
        var result = MigrationNullableString(value, name);
        return result is not null && (allowEmpty || !string.IsNullOrWhiteSpace(result)) ? result : throw MigrationInventoryInvalid();
    }
    private static long MigrationLong(JsonElement value, string name)
    {
        if (!HasNonnegativeInteger(value, name)) throw MigrationInventoryInvalid();
        return LongValue(value, name);
    }
    private static InvalidDataException MigrationInventoryInvalid() => new("Backblaze returned incomplete, conflicting or unsupported migration metadata. Migration stopped because the response could not be verified.");
    private sealed class MigrationInventoryBudget
    {
        private long _bytes; private int _versions;
        public void Include(JsonElement value)
        { _bytes += Encoding.UTF8.GetByteCount(value.GetRawText()); if (_bytes > 64 * 1024 * 1024) throw new InvalidDataException("Cloud migration inventory exceeds the supported metadata limit."); }
        public void AddVersion()
        { if (++_versions > 100_000) throw new InvalidDataException("Cloud migration inventory exceeds the supported version count."); }
    }
}

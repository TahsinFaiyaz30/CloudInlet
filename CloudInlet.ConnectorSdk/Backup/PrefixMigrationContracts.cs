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

public sealed class CloudMigrationRequestRejectedException(string message, Exception? inner = null) : IOException(message, inner);

public static class PrefixReceiptKeys
{
    public const string ReceiptMetadataKey = "cloudinlet_migration_id";
    public const string SourceMetadataKey = "cloudinlet_migration_source_id";
}

public sealed class MigrationJournalException(string message, Exception inner) : IOException(message, inner);

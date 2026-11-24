using CloudInlet.Core.Migration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudInlet.Core.B2;

/// <summary>Authenticated, exclusively leased metadata journal; no file payload is persisted.</summary>
public interface IB2CloudPrefixCopyJournal
{
    B2CloudPrefixCopyCheckpoint? Read();
    void Write(B2CloudPrefixCopyCheckpoint checkpoint);
}

public enum B2CloudPrefixCopyPhase { Ready, Creating, CopyingParts, Finishing, Completed, Canceling, Canceled }
public sealed record B2CloudPrefixCopyIntent(string OperationId, CloudPrefixVersion Source, string DestinationKey,
    B2CloudPrefixCopyPhase Phase = B2CloudPrefixCopyPhase.Ready, string? LargeFileId = null,
    long PartSize = 0, IReadOnlyList<string?>? PartHashes = null, CloudPrefixVersion? Completed = null)
{
    private readonly IReadOnlyList<string?>? _partHashes = PartHashes is null ? null : MigrationSnapshotList<string?>.Freeze(PartHashes);
    public IReadOnlyList<string?>? PartHashes { get => _partHashes; init => _partHashes = value is null ? null : MigrationSnapshotList<string?>.Freeze(value); }
}
public sealed record B2CloudPrefixCopyCheckpoint(int Schema, string AccountId, string BucketId,
    string SourcePrefix, string DestinationPrefix, long BucketRevision, IReadOnlyList<B2CloudPrefixCopyIntent> Intents,
    IReadOnlyList<CloudPrefixVersion>? HideReceipts = null, IReadOnlyList<CloudPrefixRule>? Rules = null)
{
    private readonly IReadOnlyList<B2CloudPrefixCopyIntent> _intents = MigrationSnapshotList<B2CloudPrefixCopyIntent>.Freeze(Intents);
    private readonly IReadOnlyList<CloudPrefixVersion>? _hideReceipts = HideReceipts is null ? null : MigrationSnapshotList<CloudPrefixVersion>.Freeze(HideReceipts);
    private readonly IReadOnlyList<CloudPrefixRule>? _rules = Rules is null ? null : MigrationSnapshotList<CloudPrefixRule>.Freeze(Rules);
    public IReadOnlyList<B2CloudPrefixCopyIntent> Intents { get => _intents; init => _intents = MigrationSnapshotList<B2CloudPrefixCopyIntent>.Freeze(value); }
    public IReadOnlyList<CloudPrefixVersion>? HideReceipts { get => _hideReceipts; init => _hideReceipts = value is null ? null : MigrationSnapshotList<CloudPrefixVersion>.Freeze(value); }
    public IReadOnlyList<CloudPrefixRule>? Rules { get => _rules; init => _rules = value is null ? null : MigrationSnapshotList<CloudPrefixRule>.Freeze(value); }
}

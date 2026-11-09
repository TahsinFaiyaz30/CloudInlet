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

/// <summary>Authenticated metadata deltas. The host supplies current-user protection and holds
/// native ancestor-directory guards throughout this object's exclusive lease.</summary>
public sealed class ProtectedB2CloudPrefixCopyJournal : IB2CloudPrefixCopyJournal, IDisposable
{
    private sealed record PartChange(int Index, string? Hash);
    private sealed record Change(int Index, B2CloudPrefixCopyIntent Intent, IReadOnlyList<PartChange>? Parts = null);
    private sealed record Delta(B2CloudPrefixCopyCheckpoint? Snapshot, IReadOnlyList<Change>? Changes = null,
        IReadOnlyList<CloudPrefixVersion>? HideReceipts = null);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<B2CloudPrefixCopyPhase>(allowIntegerValues: false) }
    };
    private readonly ProtectedMigrationLog<B2CloudPrefixCopyCheckpoint, Delta> _log;
    public ProtectedB2CloudPrefixCopyJournal(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect) =>
        _log = new(path, "B2CloudPrefixCopy", protect, unprotect, Options, Difference, Restore);
    public B2CloudPrefixCopyCheckpoint? Read() => _log.Read();
    public void Write(B2CloudPrefixCopyCheckpoint checkpoint) => _log.Write(checkpoint);
    public void Dispose() => _log.Dispose();

    private static Delta Difference(B2CloudPrefixCopyCheckpoint? previous, B2CloudPrefixCopyCheckpoint next)
    {
        if (previous is null) return new(next);
        if (previous with { Intents = next.Intents, HideReceipts = next.HideReceipts, Rules = next.Rules } != next ||
            !(previous.Rules ?? []).SequenceEqual(next.Rules ?? []) || previous.Intents.Count > next.Intents.Count ||
            (previous.HideReceipts?.Count ?? 0) > (next.HideReceipts?.Count ?? 0))
            throw new InvalidDataException("The cloud provider recovery identity cannot change or lose receipts.");
        var changes = new List<Change>();
        foreach (var index in MigrationSnapshotList<B2CloudPrefixCopyIntent>.ChangedIndices(previous.Intents, next.Intents))
            if (index >= previous.Intents.Count || !ReferenceEquals(previous.Intents[index], next.Intents[index]) && previous.Intents[index] != next.Intents[index])
            {
                var intent = next.Intents[index];
                if (index < previous.Intents.Count && previous.Intents[index].PartHashes is { } before &&
                    intent.PartHashes is { } after && before.Count == after.Count)
                {
                    // Ten thousand parts must not rewrite all ten thousand hashes
                    // for each acknowledgement or exhaust the recovery log limit.
                    var parts = MigrationSnapshotList<string?>.ChangedIndices(before, after)
                        .Where(part => before[part] != after[part]).Select(part => new PartChange(part, after[part])).ToArray();
                    changes.Add(new(index, intent with { PartHashes = null }, parts));
                }
                else changes.Add(new(index, intent));
            }
        foreach (var index in MigrationSnapshotList<CloudPrefixVersion>.ChangedIndices(previous.HideReceipts ?? [], next.HideReceipts ?? []).Where(index => index < (previous.HideReceipts?.Count ?? 0)))
            if (!ReferenceEquals(previous.HideReceipts![index], next.HideReceipts![index]) && previous.HideReceipts[index] != next.HideReceipts[index])
                throw new InvalidDataException("An acknowledged hide receipt cannot change in place.");
        var oldHideCount = previous.HideReceipts?.Count ?? 0;
        return new(null, changes, Enumerable.Range(oldHideCount, (next.HideReceipts?.Count ?? 0) - oldHideCount)
            .Select(index => next.HideReceipts![index]).ToArray());
    }
    private static B2CloudPrefixCopyCheckpoint Restore(IEnumerable<Delta> deltas)
    {
        B2CloudPrefixCopyCheckpoint? state = null; List<B2CloudPrefixCopyIntent>? intents = null; List<CloudPrefixVersion>? hides = null;
        foreach (var delta in deltas)
        {
            if (delta.Snapshot is { } snapshot)
            {
                if (state is not null || delta.Changes is not null || delta.HideReceipts is not null || snapshot.Intents is null)
                    throw new InvalidDataException("The provider recovery log has an unexpected snapshot.");
                state = snapshot; intents = snapshot.Intents.ToList(); hides = (snapshot.HideReceipts ?? []).ToList();
            }
            else
            {
                if (state is null || delta.Changes is null || delta.HideReceipts is null)
                    throw new InvalidDataException("The provider recovery delta is incomplete.");
                var seen = new HashSet<int>();
                foreach (var change in delta.Changes)
                {
                    if (change.Index < 0 || change.Index > intents!.Count || change.Intent is null || !seen.Add(change.Index))
                        throw new InvalidDataException("The provider recovery delta changes an invalid intent index.");
                    var intent = change.Intent;
                    if (change.Parts is { } parts)
                    {
                        if (change.Index >= intents.Count || intent.PartHashes is not null || intents[change.Index].PartHashes is not { } prior)
                            throw new InvalidDataException("The provider recovery part delta has no original inventory.");
                        var hashes = prior.ToArray(); var seenParts = new HashSet<int>();
                        foreach (var part in parts)
                        {
                            if (part.Index < 0 || part.Index >= hashes.Length || !seenParts.Add(part.Index))
                                throw new InvalidDataException("The provider recovery part delta has an invalid index.");
                            hashes[part.Index] = part.Hash;
                        }
                        intent = intent with { PartHashes = hashes };
                    }
                    if (change.Index == intents.Count) intents.Add(intent); else intents[change.Index] = intent;
                }
                hides!.AddRange(delta.HideReceipts);
            }
        }
        return state is null ? throw new InvalidDataException("The provider recovery log has no snapshot.") :
            state with { Intents = intents!.ToArray(), HideReceipts = hides!.ToArray() };
    }
}

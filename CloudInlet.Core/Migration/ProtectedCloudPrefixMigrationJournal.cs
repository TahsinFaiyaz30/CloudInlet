using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudInlet.Core.Migration;

/// <summary>Authenticated metadata deltas. The host supplies current-user protection and holds
/// native ancestor-directory guards throughout this object's exclusive lease.</summary>
public sealed class ProtectedCloudPrefixMigrationJournal : ICloudPrefixMigrationJournal, IDisposable
{
    private sealed record Change(int Index, CloudPrefixMigrationEntry Entry);
    private sealed record Delta(CloudPrefixMigrationCheckpoint? Snapshot, CloudPrefixMigrationPhase? Phase = null,
        IReadOnlyList<Change>? Changes = null);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<CloudPrefixMigrationPhase>(allowIntegerValues: false) }
    };
    private readonly ProtectedMigrationLog<CloudPrefixMigrationCheckpoint, Delta> _log;
    public ProtectedCloudPrefixMigrationJournal(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect) =>
        _log = new(path, "CloudPrefixMigration", protect, unprotect, Options, Difference, Restore);
    public CloudPrefixMigrationCheckpoint? Read() => _log.Read();
    public void Write(CloudPrefixMigrationCheckpoint checkpoint) => _log.Write(checkpoint);
    public void Dispose() => _log.Dispose();

    private static Delta Difference(CloudPrefixMigrationCheckpoint? previous, CloudPrefixMigrationCheckpoint next)
    {
        if (previous is null) return new(next);
        if (previous with { Phase = next.Phase, Entries = next.Entries } != next || previous.Entries.Count != next.Entries.Count)
            throw new InvalidDataException("The cloud recovery identity or source inventory cannot change in place.");
        var changes = new List<Change>();
        foreach (var index in MigrationSnapshotList<CloudPrefixMigrationEntry>.ChangedIndices(previous.Entries, next.Entries))
            if (!ReferenceEquals(previous.Entries[index], next.Entries[index]) && previous.Entries[index] != next.Entries[index])
                changes.Add(new(index, next.Entries[index]));
        return new(null, next.Phase, changes);
    }
    private static CloudPrefixMigrationCheckpoint Restore(IEnumerable<Delta> deltas)
    {
        CloudPrefixMigrationCheckpoint? state = null; List<CloudPrefixMigrationEntry>? entries = null;
        foreach (var delta in deltas)
        {
            if (delta.Snapshot is { } snapshot)
            {
                if (state is not null || delta.Phase is not null || delta.Changes is not null || snapshot.Entries is null)
                    throw new InvalidDataException("The cloud recovery log has an unexpected snapshot.");
                state = snapshot; entries = snapshot.Entries.ToList();
            }
            else
            {
                if (state is null || delta.Phase is null || !Enum.IsDefined(delta.Phase.Value) || delta.Changes is null)
                    throw new InvalidDataException("The cloud recovery delta is incomplete.");
                var seen = new HashSet<int>();
                foreach (var change in delta.Changes)
                {
                    if (change.Index < 0 || change.Index >= entries!.Count || change.Entry is null || !seen.Add(change.Index))
                        throw new InvalidDataException("The cloud recovery delta changes an invalid version index.");
                    entries[change.Index] = change.Entry;
                }
                state = state with { Phase = delta.Phase.Value };
            }
        }
        return state is null ? throw new InvalidDataException("The cloud recovery log has no snapshot.") : state with { Entries = entries!.ToArray() };
    }
}

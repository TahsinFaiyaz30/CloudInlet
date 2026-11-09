using System.Collections;
using System.Collections.Immutable;

namespace CloudInlet.Core.Migration;

/// <summary>
/// Immutable recovery metadata with a single-edit lineage hint. The hint identifies a predecessor
/// without retaining it; it is used only to avoid scanning an unchanged inventory during journaling.
/// Untrusted or freshly deserialized lists always take the full comparison path.
/// </summary>
internal sealed class MigrationSnapshotList<T> : IReadOnlyList<T>
{
    private readonly ImmutableList<T> _items;
    private readonly object _revision = new();
    private readonly object? _previousRevision;
    private readonly int _changedIndex;
    private MigrationSnapshotList(ImmutableList<T> items, object? previousRevision = null, int changedIndex = -1)
    { _items = items; _previousRevision = previousRevision; _changedIndex = changedIndex; }
    public static IReadOnlyList<T> Freeze(IReadOnlyList<T> values) => values is null ? null! :
        values is MigrationSnapshotList<T> ? values : new MigrationSnapshotList<T>(values.ToImmutableList());
    public static IReadOnlyList<T> Set(IReadOnlyList<T> values, int index, T value)
    {
        var prior = (MigrationSnapshotList<T>)Freeze(values);
        return new MigrationSnapshotList<T>(prior._items.SetItem(index, value), prior._revision, index);
    }
    public static IReadOnlyList<T> Append(IReadOnlyList<T> values, T value)
    {
        var prior = (MigrationSnapshotList<T>)Freeze(values);
        return new MigrationSnapshotList<T>(prior._items.Add(value), prior._revision, prior.Count);
    }
    public static IEnumerable<int> ChangedIndices(IReadOnlyList<T> previous, IReadOnlyList<T> next)
    {
        if (ReferenceEquals(previous, next)) return [];
        if (previous is MigrationSnapshotList<T> prior && next is MigrationSnapshotList<T> current &&
            ReferenceEquals(prior._revision, current._previousRevision)) return [current._changedIndex];
        return Enumerable.Range(0, next.Count);
    }
    public int Count => _items.Count;
    public T this[int index] => _items[index];
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

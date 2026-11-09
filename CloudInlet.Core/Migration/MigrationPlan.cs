using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudInlet.Core.Migration;

public enum MigrationArea { Installation, ApplicationState, WindowsIntegration, SyncFolder, BackupFolder, CloudPrefix }

public sealed record MigrationItem(string Id, MigrationArea Area, string Title, string CurrentLocation,
    string ProposedLocation, string Description, string? BlockedReason = null,
    IReadOnlyList<string>? Dependencies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CloudPrefixRule>? CloudMappings = null);

/// <summary>A snapshot for review only. Its fingerprint is not permission to execute a later migration.</summary>
public sealed record MigrationPlan(string Fingerprint, IReadOnlyList<MigrationItem> Items)
{
    public static MigrationPlan Create(IEnumerable<MigrationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var byId = new Dictionary<string, MigrationItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireText(item.Id, nameof(item.Id));
            if (item.Id != item.Id.Trim() || item.Id.Any(char.IsControl))
                throw new ArgumentException("A migration item identity cannot contain outer whitespace or control characters.", nameof(items));
            if (!Enum.IsDefined(item.Area)) throw new ArgumentException("Unknown migration area.", nameof(items));
            RequireText(item.Title, nameof(item.Title));
            RequireText(item.CurrentLocation, nameof(item.CurrentLocation));
            RequireText(item.ProposedLocation, nameof(item.ProposedLocation));
            RequireText(item.Description, nameof(item.Description));
            if (item.BlockedReason is not null) RequireText(item.BlockedReason, nameof(item.BlockedReason));
            var dependencies = (item.Dependencies ?? []).ToArray();
            foreach (var dependency in dependencies) RequireText(dependency, nameof(item.Dependencies));
            if (dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
                throw new ArgumentException("A migration item cannot repeat a dependency.", nameof(items));
            Array.Sort(dependencies, StringComparer.Ordinal);
            IReadOnlyList<CloudPrefixRule>? mappings = null;
            if (item.CloudMappings is not null)
            {
                if (item.Area != MigrationArea.CloudPrefix) throw new ArgumentException("Cloud mappings require a cloud migration item.");
                mappings = new CloudPrefixKeyMap(item.CurrentLocation, item.ProposedLocation, item.CloudMappings).Rules;
            }
            var snapshot = item with { Dependencies = Array.AsReadOnly(dependencies), CloudMappings = mappings };
            if (!byId.TryAdd(item.Id, snapshot))
                throw new ArgumentException("Migration item identities must be unique.", nameof(items));
        }

        // Validate the graph iteratively; a long dependency chain must not overflow the stack.
        var remaining = byId.ToDictionary(pair => pair.Key, pair => pair.Value.Dependencies!.Count, StringComparer.Ordinal);
        var dependents = byId.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var item in byId.Values)
            foreach (var dependency in item.Dependencies!)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                    throw new ArgumentException("A migration dependency does not identify an item in this plan.", nameof(items));
                list.Add(item.Id);
            }
        var ready = new Queue<string>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
                if (--remaining[dependent] == 0) ready.Enqueue(dependent);
        }
        if (visited != byId.Count) throw new ArgumentException("Migration dependencies cannot contain a cycle.", nameof(items));

        var ordered = byId.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        // JSON supplies unambiguous field boundaries. Sort both collections so display order
        // cannot invalidate an otherwise identical review; every material field is included.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Format = "CloudInlet.MigrationReview.v1", Items = ordered });
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(fingerprint, Array.AsReadOnly(ordered));
    }

    private static void RequireText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A migration " + field + " is required.", field);
    }
}

/// <summary>Dependency-aware review choices. Selecting an item never changes files, registry or update eligibility.</summary>
public sealed class MigrationSelection
{
    private readonly Dictionary<string, MigrationItem> _items;
    private readonly Dictionary<string, List<string>> _dependents;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    public MigrationSelection(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var snapshot = MigrationPlan.Create(plan.Items);
        if (!string.Equals(snapshot.Fingerprint, plan.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The migration review no longer matches its fingerprint. Create a fresh plan.", nameof(plan));
        _items = snapshot.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        _dependents = _items.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var item in _items.Values)
            foreach (var dependency in item.Dependencies!) _dependents[dependency].Add(item.Id);
    }

    // Return an immutable snapshot: callers cannot bypass dependency checks by casting this set.
    public IReadOnlySet<string> SelectedIds => _selected.ToFrozenSet(StringComparer.Ordinal);
    public bool CanContinue => _selected.Count > 0;

    public void Select(string id)
    {
        RequireItem(id);
        var closure = DependencyClosure(id);
        var blocked = closure.Select(candidate => _items[candidate]).FirstOrDefault(item => item.BlockedReason is not null);
        if (blocked is not null) throw new InvalidOperationException(blocked.Title + ": " + blocked.BlockedReason);
        _selected.UnionWith(closure);
    }

    public void Deselect(string id)
    {
        RequireItem(id);
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(id);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            _selected.Remove(current);
            foreach (var dependent in _dependents[current]) pending.Enqueue(dependent);
        }
    }

    public void SelectAll()
    {
        // Blocked choices and their dependents stay unchecked; independent work remains selectable.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(_items.Values.Where(item => item.BlockedReason is not null).Select(item => item.Id));
        while (pending.TryDequeue(out var id))
        {
            if (!blocked.Add(id)) continue;
            foreach (var dependent in _dependents[id]) pending.Enqueue(dependent);
        }
        _selected.UnionWith(_items.Keys.Where(id => !blocked.Contains(id)));
    }

    public void DeselectAll() => _selected.Clear();

    private HashSet<string> DependencyClosure(string id)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(id);
        while (pending.TryPop(out var current))
        {
            if (!closure.Add(current)) continue;
            foreach (var dependency in _items[current].Dependencies!) pending.Push(dependency);
        }
        return closure;
    }

    private void RequireItem(string id)
    {
        if (id is null || !_items.ContainsKey(id))
            throw new ArgumentException("The migration item is not part of this review.", nameof(id));
    }
}

namespace CloudInlet.Core.Connectors;

/// <summary>Source identifies the subscribed catalog or a manual import. Mirrors belong to that signed catalog.</summary>
public sealed record ConnectorOffer(ConnectorManifest Manifest, string Hash, string Source, string[] Locations);
public sealed record InstalledConnector(ConnectorOffer Current, bool Enabled = true, bool Automatic = true, ConnectorOffer? Previous = null);
public sealed record ConnectorOwner(string Fingerprint, string Publisher);
public sealed record ConnectorSource(string Location, string PublicKey);
public sealed record ConnectorRegistry
{
    public Dictionary<string, InstalledConnector> Installed { get; init; } = [];
    public Dictionary<string, ConnectorOwner> Owners { get; init; } = [];
    public List<ConnectorSource> Sources { get; init; } = [];
    public bool Automatic { get; init; }
}

public sealed record ConnectorPlan(IReadOnlyList<ConnectorOffer> Ordered, IReadOnlyList<string> Notices);

public static class ConnectorResolver
{
    private sealed record Need(string Id, string? Key = null, VersionRange? Range = null);
    public static ConnectorPlan Plan(IEnumerable<string> requested, ConnectorRegistry state,
        IEnumerable<ConnectorOffer> offers, string? officialKey, bool update = false, IReadOnlyDictionary<string, string>? requestedHashes = null)
    {
        var roots = requested.Distinct().ToArray();
        if (roots.Length == 0) return new([], []);
        var catalog = offers.Concat(state.Installed.Values.Select(i => i.Current)).ToLookup(o => o.Manifest.Id);
        var selected = new Dictionary<string, ConnectorOffer>();
        var errors = new HashSet<string>();
        var attempts = 0;
        bool Solve(List<Need> pending)
        {
            if (++attempts > 32768) throw new InvalidDataException("The connector catalog has too many conflicting version combinations.");
            if (selected.Count > 128) throw new InvalidDataException("Connector dependency graph exceeds 128 packages.");
            if (pending.Count == 0)
            {
                try { ValidateGraph(Effective(state, selected)); return true; }
                catch (InvalidDataException e) { errors.Add(e.Message); return false; }
            }
            var need = pending[0];
            var rest = pending.Skip(1).ToList();
            bool Fits(ConnectorOffer offer) => (requestedHashes is null || !requestedHashes.TryGetValue(need.Id, out var expected) || expected == offer.Hash) &&
                (need.Key is null || offer.Manifest.Fingerprint == need.Key) &&
                (need.Range is null || need.Range.Contains(offer.Manifest.Version));
            if (selected.TryGetValue(need.Id, out var chosen))
            {
                if (Fits(chosen)) return Solve(rest);
                errors.Add($"Conflicting publisher or version requirements for {need.Id}.");
                return false;
            }
            state.Installed.TryGetValue(need.Id, out var installed);
            var candidates = catalog[need.Id].Where(Fits).Where(o => Permitted(o, state, officialKey))
                .Where(o => installed is null || o.Source == installed.Current.Source &&
                    ConnectorFormat.VersionOf(o.Manifest.Version) >= ConnectorFormat.VersionOf(installed.Current.Manifest.Version) &&
                    (o.Manifest.Version != installed.Current.Manifest.Version || o.Hash == installed.Current.Hash))
                .DistinctBy(o => (o.Source, o.Hash)).ToList();
            if (candidates.Select(o => o.Manifest.Fingerprint).Distinct().Count() > 1 ||
                candidates.GroupBy(o => o.Manifest.Version).Any(g => g.Select(o => o.Hash).Distinct().Count() > 1))
            { errors.Add($"Conflicting connector identities or package bytes for {need.Id}. Select a single trusted source."); return false; }
            foreach (var offer in candidates.OrderByDescending(o => !update && o.Hash == installed?.Current.Hash)
                .ThenByDescending(o => ConnectorFormat.VersionOf(o.Manifest.Version)))
            {
                if (offer.Manifest.CompatibilityIssue is { } issue) { errors.Add(need.Id + " · " + issue); continue; }
                selected.Add(need.Id, offer);
                var next = offer.Manifest.Dependencies.Where(d => !d.Optional).Select(d => new Need(d.Id, d.PublisherKey, d.Versions)).Concat(rest).ToList();
                if (Solve(next)) return true;
                selected.Remove(need.Id);
            }
            errors.Add($"Missing a compatible trusted package for {need.Id}" +
                (need.Range is null ? "." : $" in [{need.Range.Min}, {need.Range.Max})."));
            return false;
        }
        if (!Solve(roots.Select(id => new Need(id)).ToList())) throw new InvalidDataException(string.Join(" ", errors.Take(8)));
        var effective = Effective(state, selected);
        var ordered = Order(roots, effective).Where(o => selected.ContainsKey(o.Manifest.Id)).ToArray();
        var notices = ordered.SelectMany(o => o.Manifest.Dependencies.Where(d => d.Optional && !Available(d, effective))
            .Select(d => $"{o.Manifest.Name} will run without optional component {d.Id}.")).Distinct().ToList();
        notices.AddRange(ordered.Where(o => state.Installed.TryGetValue(o.Manifest.Id, out var i) && !i.Enabled)
            .Select(o => $"Enable {o.Manifest.Name}."));
        return new(ordered, notices);
    }
    internal static bool Permitted(ConnectorOffer o, ConnectorRegistry state, string? officialKey) =>
        (o.Manifest.Id is not ("b2" or "onedrive") || o.Manifest.Fingerprint == officialKey) &&
        (!state.Owners.TryGetValue(o.Manifest.Id, out var owner) ||
            owner.Fingerprint == o.Manifest.Fingerprint && owner.Publisher == o.Manifest.Publisher);

    private static Dictionary<string, InstalledConnector> Effective(ConnectorRegistry state, Dictionary<string, ConnectorOffer> changes)
    {
        var result = new Dictionary<string, InstalledConnector>(state.Installed);
        foreach (var pair in changes) result[pair.Key] = new(pair.Value);
        return result;
    }
    internal static bool Available(ConnectorDependency d, IReadOnlyDictionary<string, InstalledConnector> installed) =>
        installed.TryGetValue(d.Id, out var i) && i.Enabled && i.Current.Manifest.Fingerprint == d.PublisherKey &&
        d.Versions.Contains(i.Current.Manifest.Version) && i.Current.Manifest.CompatibilityIssue is null;

    internal static void ValidateGraph(IReadOnlyDictionary<string, InstalledConnector> installed) =>
        Order(installed.Where(p => p.Value.Enabled).Select(p => p.Key), installed);

    internal static IReadOnlyList<ConnectorOffer> Order(IEnumerable<string> roots, IReadOnlyDictionary<string, InstalledConnector> installed)
    {
        var ordered = new List<ConnectorOffer>();
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException($"Circular connector dependency involving {id}.");
            if (!installed.TryGetValue(id, out var item) || !item.Enabled)
                throw new InvalidDataException($"Install or enable required connector {id}.");
            var manifest = item.Current.Manifest;
            if (manifest.CompatibilityIssue is { } issue) throw new InvalidDataException($"{id} · {issue}");
            foreach (var dep in manifest.Dependencies)
            {
                if (Available(dep, installed)) Visit(dep.Id);
                else if (!dep.Optional) throw new InvalidDataException($"{id} requires enabled {dep.Id} in [{dep.Versions.Min}, {dep.Versions.Max}) from publisher {dep.PublisherKey}.");
            }
            visiting.Remove(id); done.Add(id); ordered.Add(item.Current);
        }
        foreach (var id in roots) Visit(id);
        return ordered;
    }
}

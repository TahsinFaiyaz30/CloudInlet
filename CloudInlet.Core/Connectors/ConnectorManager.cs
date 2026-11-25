using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Connectors;

namespace CloudInlet.Core.Connectors;

public sealed class ConnectorManager
{
    public const string Local = "Local import";
    private readonly string _root;
    private readonly object _gate = new();
    private readonly ConnectorSource? _official;
    private readonly Dictionary<string, (string Hash, ConnectorLoadContext Context, object Entry)> _loaded = [];
    private readonly Dictionary<string, ConnectorOffer[]> _catalogs = [];
    private readonly Dictionary<string, string> _loadErrors = [];
    private ConnectorRegistry _state;
    public string? OfficialFingerprint => _official is null ? null : ConnectorFormat.Fingerprint(_official.PublicKey);
    public ConnectorRegistry Snapshot { get { lock (_gate) return Clone(_state); } }
    public ConnectorOffer[] Offers { get { lock (_gate) return _catalogs.Values.SelectMany(x => x).ToArray(); } }
    public ConnectorSource[] Sources { get { lock (_gate) return _state.Sources.Concat(_official is null ? [] : new[] { _official }).DistinctBy(s => s.Location).ToArray(); } }
    public event Action<string>? Progress;
    public ConnectorManager(string directory, ConnectorSource? official = null)
    {
        _root = Path.GetFullPath(directory);
        _official = official;
        _state = ReadRegistry();
        if (Directory.Exists(_root))
        {
            // Another process may be staging a change. Only collect abandoned files while holding its writer lock.
            FileStream lease;
            try { lease = LockDirectory(); }
            catch (IOException) { return; }
            using (lease) { _state = ReadRegistry(); CleanUnused(); }
        }
    }
    private ConnectorRegistry ReadRegistry()
    {
        var path = Path.Combine(_root, "registry.json");
        var state = File.Exists(path) ? ConnectorFormat.Parse<ConnectorRegistry>(File.ReadAllBytes(path)) : new();
        foreach (var (id, package) in state.Installed)
        {
            package.Current.Manifest.Validate();
            if (id != package.Current.Manifest.Id || !ConnectorFormat.Digest(package.Current.Hash) || !state.Owners.ContainsKey(id))
                throw new InvalidDataException("Connector registry needs recovery.");
        }
        return state;
    }
    private static ConnectorRegistry Clone(ConnectorRegistry state) => state with
        { Installed = new(state.Installed), Owners = new(state.Owners), Sources = [.. state.Sources] };

    private FileStream LockDirectory()
    {
        Directory.CreateDirectory(_root);
        return new FileStream(Path.Combine(_root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void Commit(ConnectorRegistry next)
    {
        var path = Path.Combine(_root, "registry.json");
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(f, next, ConnectorFormat.Json); f.Flush(true); }
            File.Move(tmp, path, true);
            lock (_gate) _state = next;
        }
        finally { File.Delete(tmp); }
    }
    private string BytesPath(ConnectorOffer o) =>
        ConnectorFormat.Digest(o.Hash) ? Path.Combine(_root, "packages", o.Hash + ".cipack") : throw new InvalidDataException("Invalid package digest.");
    public async Task SubscribeAsync(ConnectorSource reviewed, CancellationToken ct = default)
    {
        var result = await ConnectorCatalogReader.ReadAsync(reviewed.Location, reviewed.PublicKey, ct).ConfigureAwait(false);
        lock (_gate)
        {
            using var lease = LockDirectory();
            var next = ReadRegistry();
            var old = next.Sources.Append(_official).FirstOrDefault(s => s?.Location == result.Source.Location);
            if (old is not null && old.PublicKey != result.Source.PublicKey) throw new InvalidDataException("This source already has a different publisher key.");
            if (old is null) next.Sources.Add(result.Source);
            Commit(next); _catalogs[result.Source.Location] = result.Offers;
        }
    }
    public void RemoveSource(string location)
    {
        lock (_gate)
        {
            if (_official?.Location == location) throw new InvalidOperationException("The app distribution owns this publisher catalog.");
            using var lease = LockDirectory();
            var next = ReadRegistry();
            next.Sources.RemoveAll(s => s.Location == location);
            Commit(next);
            _catalogs.Remove(location);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var source in Sources)
        {
            try
            {
                var (_, offers) = await ConnectorCatalogReader.ReadAsync(source.Location, source.PublicKey, ct).ConfigureAwait(false);
                lock (_gate) if (Sources.Contains(source)) _catalogs[source.Location] = offers;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { lock (_gate) _catalogs.Remove(source.Location); failures.Add(source.Location + " · " + e.Message); }
        }
        if (failures.Count == 0) return;
        var message = string.Join(Environment.NewLine, failures);
        if (Offers.Length == 0) throw new IOException(message);
        Progress?.Invoke(message);
    }
    public static ConnectorOffer InspectLocal(string file)
    {
        var pkg = ConnectorPackage.Inspect(file);
        return new(pkg.Manifest, pkg.Hash, Local, [new Uri(Path.GetFullPath(file)).AbsoluteUri]);
    }
    public ConnectorPlan Plan(IEnumerable<string> ids, bool update = false, IEnumerable<ConnectorOffer>? imports = null)
    {
        var local = imports?.ToArray() ?? [];
        if (local.GroupBy(o => o.Manifest.Id).Any(g => g.Count() > 1))
            throw new InvalidDataException("Choose one local package per connector ID.");
        lock (_gate)
        {
            foreach (var o in local)
                if (_state.Installed.TryGetValue(o.Manifest.Id, out var installed) && installed.Current.Source != o.Source)
                    throw new InvalidDataException($"{o.Manifest.Name} receives updates from {installed.Current.Source}. Local import cannot replace that subscription.");
            return ConnectorResolver.Plan(ids, _state, local.Concat(Offers), OfficialFingerprint, update,
                local.ToDictionary(o => o.Manifest.Id, o => o.Hash));
        }
    }
    public bool NeedsRestart(string id)
    {
        lock (_gate) return _loaded.TryGetValue(id, out var loaded) && (!_state.Installed.TryGetValue(id, out var i) || i.Current.Hash != loaded.Hash);
    }
    public string? Issue(string id)
    {
        lock (_gate)
        {
            try
            {
                var graph = ConnectorResolver.Order([id], _state.Installed);
                foreach (var o in graph)
                {
                    if (!ConnectorResolver.Permitted(o, _state, OfficialFingerprint))
                        return $"The publisher for {o.Manifest.Id} does not match this installation.";
                    if (_loadErrors.TryGetValue(o.Hash, out var error)) return error;
                    if (!File.Exists(BytesPath(o))) return $"Reinstall required connector {o.Manifest.Name}.";
                }
                return null;
            }
            catch (InvalidDataException e) { return e.Message; }
        }
    }
    public IProviderConnector Provider(string id)
    {
        lock (_gate)
        {
            if (Issue(id) is { } issue) throw new ConnectorUnavailableException(issue);
            // A running account keeps the complete old dependency graph until the process restarts.
            if (_loaded.TryGetValue(id, out var active)) return active.Entry as IProviderConnector ?? throw new InvalidDataException("A component cannot provide transfer accounts.");
            var graph = ConnectorResolver.Order([id], _state.Installed);
            if (graph.Any(o => NeedsRestart(o.Manifest.Id)))
                throw new ConnectorUnavailableException("Restart CloudInlet to activate the updated connector dependencies.");
            foreach (var o in graph)
            {
                var m = o.Manifest;
                if (_loaded.ContainsKey(m.Id)) continue;
                var path = BytesPath(o);
                try
                {
                    VerifyOffer(o, path);
                    var deps = m.Dependencies.Where(d => ConnectorResolver.Available(d, _state.Installed)).Select(d => _loaded[d.Id].Context).ToArray();
                    if (deps.SelectMany(d => d.Exports).Concat(m.Files.Keys.Where(ConnectorFormat.AssemblyFile)).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                        throw new InvalidDataException("Connector dependencies export conflicting assembly names.");
                    var context = new ConnectorLoadContext(path, m, deps);
                    var entry = context.Activate();
                    if (m.Kind == "component" ? entry is not IConnectorComponent : entry is not IProviderConnector p || p.Id != m.Id || p.Capabilities != m.Capabilities)
                        throw new InvalidDataException("The entry point disagrees with the signed connector contract.");
                    if (m.Capabilities.HasFlag(ConnectorCapabilities.AccountSetup) && entry is not IConnectorAccountSetup)
                        throw new InvalidDataException("Connector advertises interactive account setup without the SDK interface.");
                    _loaded.Add(m.Id, (o.Hash, context, entry));
                }
                catch (Exception e)
                {
                    _loadErrors[o.Hash] = $"{m.Name} could not load. Repair this version or roll back. {e.GetBaseException().Message}";
                    throw new ConnectorUnavailableException(_loadErrors[o.Hash]);
                }
            }
            return _loaded[id].Entry as IProviderConnector ?? throw new InvalidDataException("A component cannot provide transfer accounts.");
        }
    }
    private static void VerifyOffer(ConnectorOffer o, string path)
    {
        var p = ConnectorPackage.Inspect(path);
        if (p.Hash != o.Hash || JsonSerializer.Serialize(p.Manifest, ConnectorFormat.Json) != JsonSerializer.Serialize(o.Manifest, ConnectorFormat.Json))
            throw new InvalidDataException("Downloaded connector differs from the reviewed signed package.");
    }

    public async Task InstallAsync(ConnectorPlan plan, CancellationToken ct = default, bool automatic = false,
        IReadOnlyDictionary<string, string>? storeFiles = null)
    {
        using var lease = LockDirectory();
        var state = ReadRegistry();
        lock (_gate) _state = state;
        var selected = plan.Ordered;
        var next = Clone(state);
        foreach (var o in selected)
        {
            o.Manifest.Validate();
            var id = o.Manifest.Id;
            if (!ConnectorFormat.Digest(o.Hash) || o.Locations.Length == 0)
                throw new InvalidDataException("Installation requires an authenticated package and a reviewed source.");
            if (o.Source == Local)
            {
                if (o.Locations.Any(location => !ConnectorCatalogReader.Address(location).IsFile))
                    throw new InvalidDataException("Local import requires an explicitly selected local package.");
            }
            else if (state.Installed.GetValueOrDefault(id)?.Current.Hash != o.Hash || !File.Exists(BytesPath(o)))
            {
                lock (_gate)
                {
                    if (!Sources.Any(s => s.Location == o.Source && s.PublicKey == o.Manifest.PublicKey) ||
                        !_catalogs.TryGetValue(o.Source, out var catalog) ||
                        !catalog.Any(entry => entry.Hash == o.Hash && entry.Store == o.Store && o.Locations.All(entry.Locations.Contains)))
                        throw new InvalidDataException("Refresh this trusted catalog and review its signed download locations.");
                }
            }
            state.Installed.TryGetValue(id, out var old);
            if (!ConnectorResolver.Permitted(o, state, OfficialFingerprint) || old is not null &&
                (old.Current.Source != o.Source || old.Current.Manifest.Fingerprint != o.Manifest.Fingerprint ||
                 ConnectorFormat.VersionOf(old.Current.Manifest.Version) > ConnectorFormat.VersionOf(o.Manifest.Version) ||
                 old.Current.Manifest.Version == o.Manifest.Version && old.Current.Hash != o.Hash))
                throw new InvalidDataException($"The installation plan for {id} is stale or conflicts with its publisher, version or source.");
            if (automatic && (!state.Automatic || old is null || !old.Automatic || !old.Enabled || o.Source == Local))
                throw new InvalidOperationException("This plan requires manual connector review.");
            next.Installed[id] = new(o, true, old?.Automatic ?? true, old?.Current.Hash == o.Hash ? old.Previous : old?.Current);
            next.Owners[id] = new(o.Manifest.Fingerprint, o.Manifest.Publisher);
        }
        ConnectorResolver.ValidateGraph(next.Installed);
        try
        {
            foreach (var o in selected)
            {
                ct.ThrowIfCancellationRequested();
                var path = BytesPath(o);
                if (File.Exists(path))
                {
                    try { VerifyOffer(o, path); continue; }
                    catch (Exception e) when (e is IOException or InvalidDataException or BadImageFormatException or CryptographicException)
                    { Progress?.Invoke($"Repairing {o.Manifest.Name}"); }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".partial");
                try
                {
                    // The UI reviews the selected publisher and mirror before downloading.
                    var address = ConnectorCatalogReader.Address(o.Locations.First());
                    if (storeFiles is not null && storeFiles.TryGetValue(o.Hash, out var storeFile))
                    {
                        if (o.Store is null || o.Source == Local || !Path.IsPathFullyQualified(storeFile))
                            throw new InvalidDataException("Unexpected Microsoft Store connector package.");
                        address = new Uri(Path.GetFullPath(storeFile));
                    }
                    Progress?.Invoke($"Installing {o.Manifest.Name} from {(address.IsFile ? "a reviewed local package" : address.Host)}");
                    using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { await ConnectorCatalogReader.CopyAsync(address, f, ConnectorPackage.MaxPackageBytes, ct).ConfigureAwait(false); f.Flush(true); }
                    VerifyOffer(o, temp);
                    Progress?.Invoke($"Installing {o.Manifest.Name} {o.Manifest.Version}");
                    File.Move(temp, path, true);
                }
                finally { File.Delete(temp); }
            }
            ct.ThrowIfCancellationRequested();
            Commit(next);
            lock (_gate) foreach (var o in selected) _loadErrors.Remove(o.Hash);
            Progress?.Invoke(selected.Any(o => NeedsRestart(o.Manifest.Id)) ? "Installed · restart CloudInlet to activate the update." : "Connectors ready.");
        }
        finally { CleanUnused(); }
    }

    public static void CheckRemoval(ConnectorRegistry state, string id, bool uninstall, Func<string, string?>? inUse)
    {
        if (inUse?.Invoke(id) is { } reason) throw new InvalidOperationException(reason);
        var dependent = state.Installed.Values.FirstOrDefault(i => (uninstall || i.Enabled) &&
            i.Current.Manifest.Dependencies.Any(d => !d.Optional && d.Id == id));
        if (dependent is not null) throw new InvalidOperationException($"{dependent.Current.Manifest.Name} requires this connector.");
    }

    public void Change(string id, string action, Func<string, string?>? inUse = null)
    {
        lock (_gate)
        {
            using var lease = LockDirectory();
            var state = ReadRegistry();
            var current = state.Installed.GetValueOrDefault(id) ?? throw new InvalidOperationException("Connector is not installed.");
            if (action is "disable" or "uninstall")
            {
                CheckRemoval(state, id, action == "uninstall", inUse);
            }
            switch (action)
            {
                case "uninstall": state.Installed.Remove(id); break;
                case "enable": case "disable": state.Installed[id] = current with { Enabled = action == "enable" }; break;
                case "automatic": state.Installed[id] = current with { Automatic = !current.Automatic }; break;
                case "rollback" when current.Previous is { } prior:
                    VerifyOffer(prior, BytesPath(prior));
                    state.Installed[id] = current with { Current = prior, Previous = current.Current }; break;
                default: throw new InvalidOperationException("This connector action is unavailable.");
            }
            ConnectorResolver.ValidateGraph(state.Installed);
            Commit(state);
            CleanUnused();
        }
    }
    public void SetAutomatic(bool enabled)
    {
        lock (_gate) { using var lease = LockDirectory(); Commit(ReadRegistry() with { Automatic = enabled }); }
    }
    private void CleanUnused()
    {
        lock (_gate)
        {
            // Active contexts pin an open package handle until process exit.
            var keep = _state.Installed.Values.SelectMany(i => new[] { i.Current.Hash, i.Previous?.Hash })
                .ToHashSet();
            var folder = Path.Combine(_root, "packages");
            if (Directory.Exists(folder)) foreach (var path in Directory.EnumerateFiles(folder, "*.cipack"))
            {
                var hash = Path.GetFileNameWithoutExtension(path);
                if (ConnectorFormat.Digest(hash) && !keep.Contains(hash)) TryDelete(path);
            }
            foreach (var path in Directory.EnumerateFiles(_root, "registry.json.*.tmp"))
                if (Guid.TryParseExact(Path.GetFileName(path)["registry.json.".Length..^4], "N", out _)) TryDelete(path);
            foreach (var path in Directory.EnumerateFiles(_root, "*.partial"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) TryDelete(path);
        }
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class ConnectorUnavailableException(string message) : IOException(message);

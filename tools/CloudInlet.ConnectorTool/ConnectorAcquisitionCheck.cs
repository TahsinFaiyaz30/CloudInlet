using CloudInlet.Core.Connectors;

namespace CloudInlet.Validation;

internal static class ConnectorAcquisitionCheck
{
    internal static async Task RunAsync(ConnectorManager manager, string store, CancellationToken ct)
    {
        var archives = Path.Combine(store, "packages");
        int Count() => Directory.Exists(archives) ? Directory.GetFiles(archives, "*.cipack").Length : 0;
        if (manager.Snapshot.Installed.Count != 0 || Count() != 0 || manager.Sources.Length != 1)
            throw new InvalidOperationException("Select a fresh isolated connector profile with a single pinned catalog.");

        await manager.RefreshAsync(ct);
        if (Count() != 0) throw new InvalidDataException("Discovery downloaded a provider.");
        foreach (var id in new[] { "b2", "onedrive" })
        {
            var plan = manager.Plan([id]);
            if (plan.Ordered.Count != 1 || plan.Ordered[0].Manifest.Id != id)
                throw new InvalidDataException("Unexpected first-party installation plan.");
            await manager.InstallAsync(plan, ct);
            if (Count() != 1 || manager.Snapshot.Installed.Count != 1 || manager.Provider(id).Id != id)
                throw new InvalidDataException("Expected only the selected, verified provider in writable storage.");
            manager.Change(id, "disable");
            if (manager.Issue(id) is null) throw new InvalidDataException("Disabled provider is available.");
            manager.Change(id, "enable");
            if (manager.Issue(id) is not null) throw new InvalidDataException("Enabled provider is unavailable.");
            manager.Change(id, "uninstall");
            if (Count() != 0 || manager.Snapshot.Installed.Count != 0)
                throw new InvalidDataException("Removal left an installed archive.");
        }
    }
}

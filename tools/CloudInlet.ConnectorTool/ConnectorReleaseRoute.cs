using System.Text.RegularExpressions;
using CloudInlet.Core.Connectors;

internal sealed record OfficialProvider(string Name, string Repository, string Folder, string Assembly, string EntryPoint,
    bool OriginalCentralRelease = false)
{
    private static readonly Lazy<Dictionary<string, OfficialProvider>> Providers = new(() =>
        ConnectorFormat.Parse<Dictionary<string, OfficialProvider>>(File.ReadAllBytes(
            Environment.GetEnvironmentVariable("CLOUDINLET_CONNECTOR_PROVIDERS") ?? Path.Combine(AppContext.BaseDirectory, "official-providers.json"))));
    internal static OfficialProvider Get(string id)
    {
        if (!ConnectorFormat.ValidId(id) || !Providers.Value.TryGetValue(id, out var p) || p is null ||
            string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.EntryPoint) || !ConnectorFormat.AssemblyFile(p.Assembly) ||
            !Regex.IsMatch(p.Repository, @"\ACloudInlet-Connector-[A-Za-z0-9][A-Za-z0-9.-]*\z") ||
            !Regex.IsMatch(p.Folder, @"\A[A-Za-z0-9][A-Za-z0-9.-]*\z"))
            throw new InvalidDataException("Unknown or invalid provider in the official distribution registry.");
        return p;
    }
}

internal sealed record ConnectorReleaseRoute(string ProviderRepository, string Repository, string Tag,
    string Title, string SourceFolder, string Filename, string? OriginalDownload)
{
    public bool Historical => OriginalDownload is not null;
    public string Release => $"https://github.com/{Repository}/releases/tag/{Tag}";
    public string Download => $"https://github.com/{Repository}/releases/download/{Tag}/{Filename}";
    internal string[] Locations => OriginalDownload is { } original ? [Download, original] : [Download];

    internal static ConnectorReleaseRoute For(string id, string version)
    {
        var registration = OfficialProvider.Get(id);
        if (ConnectorFormat.VersionOf(version) < new Version(1, 0, 0))
            throw new InvalidDataException("Official connector history begins at 1.0.0.");
        var provider = $"TahsinFaiyaz30/{registration.Repository}";
        var filename = $"{id}-{version}.cipack";
        var original = registration.OriginalCentralRelease && version == "1.0.0"
            ? $"https://github.com/{OfficialRelease.Central}/releases/download/{id}-v{version}/{filename}"
            : null;
        return new(provider, provider, $"v{version}", $"{registration.Name} Connector {version}", registration.Folder, filename, original);
    }

    internal static async Task WriteDirectoryAsync(string pin, string catalog, string directory, string guideRevision, string output)
    {
        if (!Regex.IsMatch(guideRevision, @"\A[0-9a-f]{40}\z")) throw new InvalidDataException("Supply the reviewed documentation commit.");
        var offers = await OfficialRelease.ReadCatalogAsync(pin, catalog);
        const string source = "https://github.com/TahsinFaiyaz30/CloudInlet";
        var docs = $"{source}/blob/{guideRevision}/docs";
        var central = $"https://github.com/{OfficialRelease.Central}";
        var lines = new List<string>();
        var ids = directory == "central" ? offers.Select(o => o.Manifest.Id).Distinct().Order().ToArray() : new[] { directory };
        var table = new List<string>
        {
            "| Connector | Version | Platform | Connector SDK | Package | Release notes |",
            "| --- | --- | --- | --- | --- | --- |"
        };
        foreach (var id in ids)
        {
            var history = offers.Where(o => o.Manifest.Id == id).OrderByDescending(o => ConnectorFormat.VersionOf(o.Manifest.Version)).ToArray();
            if (history.Length == 0) throw new InvalidDataException($"The signed directory has no entry for {id}.");
            if (OfficialProvider.Get(id).OriginalCentralRelease && !history.Any(o => o.Manifest.Version == "1.0.0"))
                throw new InvalidDataException($"The original {id} release is missing.");
            var latest = history[0].Manifest;
            var route = For(id, latest.Version);
            if (directory != "central")
            {
                lines.AddRange([$"# {latest.Name} Connector for CloudInlet", "",
                    $"This repository distributes the **{latest.Name}** provider connector. Its current version is **{latest.Version}**. The SDK and provider implementation live in CloudInlet. [Source for {latest.Version}]({source}/tree/{latest.SourceRevision}/connectors/{route.SourceFolder})",
                    "", "## Release history", "",
                    $"Each version has its own signed download and release notes. [GitHub releases](https://github.com/{route.ProviderRepository}/releases)", ""]);
            }
            foreach (var offer in directory == "central" ? history.Take(1) : history)
            {
                var m = offer.Manifest;
                var r = For(id, m.Version);
                var platform = string.Join(", ", m.Platforms.Select(p => p == "windows" ? "Windows" : p)) + " " + string.Join(", ", m.Architectures);
                var notes = offer.Locations[0] == r.OriginalDownload
                    ? $"{central}/releases/tag/{id}-v{m.Version}" : r.Release;
                table.Add($"| [{m.Name}](https://github.com/{r.ProviderRepository}) | {m.Version} | {platform} | ≥ {m.Sdk.Min}, < {m.Sdk.Max} | [{r.Filename}]({offer.Locations[0]}) | [Release notes]({notes}) |");
            }
        }
        if (directory == "central")
            lines.AddRange(["# CloudInlet connector directory", "",
                "The signed catalog lists each official connector independently. Select a connector to see its complete release history. The original 1.0.0 downloads remain available here. [Historical releases](" + central + "/releases)", ""]);
        lines.AddRange(table);
        lines.AddRange(["", "## Install and update", "",
            "In CloudInlet 1.2.5 or later, open **Connectors**, select the provider, review the publisher and download, and choose **Install**. Open **Settings → Updates and about** to update an installed connector. [Connector guide](" + docs + "/CONNECTORS.md)", "",
            $"CloudInlet verifies the signed catalog and package. The official publisher fingerprint is `{offers[0].Manifest.Fingerprint}`. [Central directory]({central}) · [Signed catalog]({OfficialRelease.Discovery}) · [Connector development guide]({docs}/CONNECTORS.md)"]);
        if (directory == "central")
            lines.AddRange(["", "## Maintainers", "",
                "Register providers in `providers.json`. Map an approved Microsoft Store download to its exact connector SHA-256 in `store-packages.json`. The protected workflow signs location changes in the catalog. Compatible connectors and download locations can change independently of CloudInlet.", "",
                $"Dispatch **Publish a signed connector** with one provider and its reviewed source commit. The protected central workflow signs the package, verifies its public download and updates this directory from the complete signed catalog. [Release procedure]({docs}/CONNECTOR_RELEASES.md) · [Host package measurements]({docs}/CONNECTOR_VERIFICATION.md)"]);
        lines.AddRange(["", "<!-- Generated from the verified official catalog by CloudInlet.ConnectorTool release-readme. -->"]);
        File.WriteAllText(output, string.Join('\n', lines) + "\n");
    }
}

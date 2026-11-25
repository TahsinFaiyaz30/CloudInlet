using System.Text.RegularExpressions;
using CloudInlet.Core.Connectors;

internal sealed record ConnectorReleaseRoute(string ProviderRepository, string Repository, string Tag,
    string Title, string SourceFolder, string Filename, bool Historical)
{
    public string Release => $"https://github.com/{Repository}/releases/tag/{Tag}";
    public string Download => $"https://github.com/{Repository}/releases/download/{Tag}/{Filename}";

    internal static ConnectorReleaseRoute For(string id, string version)
    {
        var (suffix, display, folder) = id switch
        {
            "b2" => ("B2", "Backblaze B2", "BackblazeB2"),
            "onedrive" => ("OneDrive", "OneDrive", "OneDrive"),
            _ => throw new InvalidDataException("Unknown official connector ID.")
        };
        if (ConnectorFormat.VersionOf(version) < new Version(1, 0, 0))
            throw new InvalidDataException("Official connector history begins at 1.0.0.");
        var provider = $"TahsinFaiyaz30/CloudInlet-Connector-{suffix}";
        var historical = version == "1.0.0";
        return new(provider, historical ? OfficialRelease.Central : provider, historical ? $"{id}-v{version}" : $"v{version}",
            $"{display} Connector {version}", folder, $"{id}-{version}.cipack", historical);
    }

    internal static async Task WriteDirectoryAsync(string pin, string catalog, string directory, string guideRevision, string output)
    {
        if (!Regex.IsMatch(guideRevision, @"\A[0-9a-f]{40}\z")) throw new InvalidDataException("Supply the reviewed documentation commit.");
        var offers = await OfficialRelease.ReadCatalogAsync(pin, catalog);
        const string source = "https://github.com/TahsinFaiyaz30/CloudInlet";
        var docs = $"{source}/blob/{guideRevision}/docs";
        var central = $"https://github.com/{OfficialRelease.Central}";
        var lines = new List<string>();
        var ids = directory == "central" ? new[] { "b2", "onedrive" } : new[] { directory };
        var table = new List<string>
        {
            "| Connector | Version | Platform | Connector SDK | Package | Release notes |",
            "| --- | --- | --- | --- | --- | --- |"
        };
        foreach (var id in ids)
        {
            var history = offers.Where(o => o.Manifest.Id == id).OrderByDescending(o => ConnectorFormat.VersionOf(o.Manifest.Version)).ToArray();
            if (history.Length == 0) throw new InvalidDataException($"The signed directory has no entry for {id}.");
            if (!history.Any(o => o.Manifest.Version == "1.0.0")) throw new InvalidDataException($"The original {id} release is missing.");
            var latest = history[0].Manifest;
            var route = For(id, latest.Version);
            if (directory != "central")
            {
                lines.AddRange([$"# {latest.Name} Connector for CloudInlet", "",
                    $"This repository distributes the **{latest.Name}** provider connector. Its current version is **{latest.Version}**. The SDK and provider implementation live in CloudInlet. [Source for {latest.Version}]({source}/tree/{latest.SourceRevision}/connectors/{route.SourceFolder})",
                    "", "## Release history", "",
                    $"Each row links to the original signed download. Version 1.0.0 is hosted in the historical distribution repository. Subsequent versions have their own tags here. [GitHub releases](https://github.com/{route.ProviderRepository}/releases)", ""]);
            }
            foreach (var offer in directory == "central" ? history.Take(1) : history)
            {
                var m = offer.Manifest;
                var r = For(id, m.Version);
                var platform = string.Join(", ", m.Platforms.Select(p => p == "windows" ? "Windows" : p)) + " " + string.Join(", ", m.Architectures);
                table.Add($"| [{m.Name}](https://github.com/{r.ProviderRepository}) | {m.Version} | {platform} | ≥ {m.Sdk.Min}, < {m.Sdk.Max} | [{r.Filename}]({offer.Locations[0]}) | [Release notes]({r.Release}) |");
            }
        }
        if (directory == "central")
            lines.AddRange(["# CloudInlet connector directory", "",
                "The signed catalog lists each official connector independently. Select a connector to see its complete release history. The original 1.0.0 downloads remain available here. [Historical releases](" + central + "/releases)", ""]);
        lines.AddRange(table);
        lines.AddRange(["", "## Install and update", "",
            "In a connector-enabled CloudInlet build, open **Connectors**, select the provider, review the publisher and download, and choose **Install**. Open **Settings → Updates and about** to update an installed connector. The published CloudInlet 1.2.1 app predates the Connectors page. [Run the connector-enabled host](" + docs + "/CONNECTORS.md#try-the-published-connectors)", "",
            $"CloudInlet verifies the signed catalog and package. The official publisher fingerprint is `{offers[0].Manifest.Fingerprint}`. [Central directory]({central}) · [Signed catalog]({OfficialRelease.Discovery}) · [Connector development guide]({docs}/CONNECTORS.md)"]);
        if (directory == "central")
            lines.AddRange(["", "## Maintainers", "",
                $"Dispatch **Publish a signed connector** with one provider and its reviewed source commit. The protected central workflow signs the package, verifies its public download and updates this directory from the complete signed catalog. [Release procedure]({docs}/CONNECTOR_RELEASES.md) · [Host package measurements]({docs}/CONNECTOR_VERIFICATION.md)"]);
        lines.AddRange(["", "<!-- Generated from the verified official catalog by CloudInlet.ConnectorTool release-readme. -->"]);
        File.WriteAllText(output, string.Join('\n', lines) + "\n");
    }
}

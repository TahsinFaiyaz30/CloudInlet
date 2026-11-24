using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CloudInlet.Core.Connectors;

internal static class CorePayload
{
    internal static void Verify(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "CloudInlet.ConnectorSdk.dll"))) throw new InvalidDataException("Missing host SDK.");
        var pin = ConnectorFormat.Parse<ConnectorSource>(File.ReadAllBytes(Path.Combine(directory, "connector-source.json")));
        _ = ConnectorFormat.Fingerprint(pin.PublicKey);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (file.Split(Path.DirectorySeparatorChar).Contains("StoreConnectors", StringComparer.OrdinalIgnoreCase) ||
                name.EndsWith(".cipack", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Connector payload or private key in base app: {name}.");
            using var input = File.OpenRead(file);
            if (input.Length < 4) continue;
            var first = input.ReadByte(); var second = input.ReadByte(); input.Position = 0;
            if (first == 'P' && second == 'K')
            {
                using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.GetEntry("manifest.sig") is not null || zip.Entries.Any(e => e.Name.EndsWith(".cipack", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Embedded connector package in {name}.");
            }
            if (first != 'M' || second != 'Z') continue;
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) continue;
            var assembly = md.GetString(md.GetAssemblyDefinition().Name);
            bool implements = md.TypeDefinitions.SelectMany(h => md.GetTypeDefinition(h).GetInterfaceImplementations())
                .Select(h => md.GetInterfaceImplementation(h).Interface).Where(h => h.Kind == HandleKind.TypeReference)
                .Select(h => md.GetTypeReference((TypeReferenceHandle)h)).Any(t => md.GetString(t.Namespace) == "CloudInlet.Connectors" &&
                    md.GetString(t.Name) is "IProviderConnector" or "IConnectorComponent" or "IOneDriveConnector");
            if (assembly.StartsWith("CloudInlet.Connectors.", StringComparison.OrdinalIgnoreCase) || assembly == "ExampleConnector" || implements)
                throw new InvalidDataException($"Provider implementation in base app: {name}.");
            if (!assembly.StartsWith("CloudInlet", StringComparison.OrdinalIgnoreCase)) continue;
            var resources = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            if (resources.Size > 0)
            {
                var bytes = pe.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).AsSpan();
                if (bytes.IndexOf("manifest.sig"u8) >= 0 || bytes.IndexOf("BSJB"u8) >= 0)
                    throw new InvalidDataException($"Embedded connector archive or managed payload in {name}.");
            }
        }
    }
}

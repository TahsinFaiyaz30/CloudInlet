using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudInlet.Core.Connectors;

internal static class OfficialRelease
{
    internal const string Central = "TahsinFaiyaz30/CloudInlet-Connectors";
    internal const string Discovery = "https://raw.githubusercontent.com/" + Central + "/main/catalog.json";
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ConnectorFormat.Json);

    private static ConnectorSource Pin(string file)
    {
        var pin = ConnectorFormat.Parse<ConnectorSource>(File.ReadAllBytes(file));
        if (pin.Location != Discovery) throw new InvalidDataException("Unexpected official catalog location.");
        _ = ConnectorFormat.Fingerprint(pin.PublicKey);
        return pin;
    }

    private static ECDsa SigningKey(string publicKey)
    {
        var secret = Environment.GetEnvironmentVariable("CLOUDINLET_CONNECTOR_SIGNING_KEY") ?? throw new InvalidOperationException("Protected connector signing credential is missing.");
        var bytes = Convert.FromBase64String(secret);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var consumed);
            if (consumed != bytes.Length || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != publicKey)
                throw new InvalidDataException("Signing credential does not match the independently pinned official publisher.");
            return key;
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void CheckOfficial(ConnectorManifest m, string key)
    {
        var expected = m.Id switch
        {
            "b2" => ("CloudInlet.Connectors.BackblazeB2.dll", "CloudInlet.Core.B2.B2Connector"),
            "onedrive" => ("CloudInlet.Connectors.OneDrive.dll", "CloudInlet.Core.OneDrive.OneDriveConnector"),
            _ => throw new InvalidDataException("Only the two approved first-party connectors can be published.")
        };
        if (m.Kind != "provider" || m.Publisher != "CloudInlet" || m.PublicKey != key || m.SourceRevision is null ||
            m.Assembly != expected.Item1 || m.EntryPoint != expected.Item2 || m.CompatibilityIssue is not null)
            throw new InvalidDataException("Wrong official connector identity, contract, source revision or publisher.");
    }

    internal static void Package(bool sign, string id, string revision, string input, string pinFile, string package)
    {
        var pin = Pin(pinFile);
        var template = ConnectorFormat.Parse<ConnectorManifest>(File.ReadAllBytes(Path.Combine(input, "connector.json")));
        if (template.Id != id || template.PublicKey != "" || template.Files.Any(f => !ConnectorFormat.PayloadFile(f.Key) || f.Value != ""))
            throw new InvalidDataException("Invalid official build input.");
        var inventory = new Dictionary<string, byte[]>();
        foreach (var name in template.Files.Keys)
        {
            var file = new FileInfo(Path.Combine(input, name));
            if (file.Length is <= 0 or > 32 * 1024 * 1024 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Invalid build file.");
            inventory.Add(name, File.ReadAllBytes(file.FullName));
        }
        var manifest = template with
        {
            PublicKey = pin.PublicKey, SourceRevision = revision,
            Documentation = $"https://github.com/TahsinFaiyaz30/CloudInlet/blob/{revision}/docs/CONNECTORS.md",
            Files = inventory.ToDictionary(f => f.Key, f => Convert.ToHexString(SHA256.HashData(f.Value)))
        };
        manifest.Validate();
        CheckOfficial(manifest, pin.PublicKey);
        var encoded = Encode(manifest);
        if (sign)
        {
            using var key = SigningKey(pin.PublicKey);
            using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
            void Add(string name, byte[] value)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(value);
            }
            Add("manifest.json", encoded);
            Add("manifest.sig", key.SignData(encoded, HashAlgorithmName.SHA256));
            foreach (var pair in inventory) Add(pair.Key, pair.Value);
        }
        var inspected = ConnectorPackage.Inspect(package);
        if (!Encode(inspected.Manifest).AsSpan().SequenceEqual(encoded))
            throw new InvalidDataException("The release differs from the exact source build, publisher or signed provenance.");
        Console.WriteLine($"{id} {manifest.Version} · {revision} · SHA-256 {inspected.Hash}");
    }

    internal static async Task<ConnectorOffer[]> ReadCatalogAsync(string pinFile, string catalog)
    {
        var pin = Pin(pinFile);
        var (_, offers) = await ConnectorCatalogReader.ReadAsync(new Uri(Path.GetFullPath(catalog)).AbsoluteUri, pin.PublicKey, default);
        foreach (var offer in offers)
        {
            var m = offer.Manifest;
            var route = ConnectorReleaseRoute.For(m.Id, m.Version);
            if (m.Publisher != "CloudInlet" || m.Kind != "provider" || m.SourceRevision is null ||
                !(offer.Locations.SequenceEqual(route.Locations) ||
                  (route.OriginalDownload is { } original && offer.Locations is [var only] && only == original)))
                throw new InvalidDataException("Invalid publisher, provenance or immutable location in the official catalog.");
        }
        if (offers.GroupBy(o => (o.Manifest.Id, o.Manifest.Version)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Duplicate connector versions in the official catalog.");
        return offers;
    }

    internal static async Task VerifyListedAsync(string pinFile, string catalog, string id, string version, string revision, string package)
    {
        var offers = await ReadCatalogAsync(pinFile, catalog);
        var offer = offers.Single(o => o.Manifest.Id == id && o.Manifest.Version == version);
        var inspected = ConnectorPackage.Inspect(package);
        if (offer.Manifest.SourceRevision != revision || offer.Hash != inspected.Hash ||
            !Encode(offer.Manifest).AsSpan().SequenceEqual(Encode(inspected.Manifest)))
            throw new InvalidDataException("The downloaded package or requested source revision differs from the signed catalog.");
        Console.WriteLine($"{id} {version} · {revision} · SHA-256 {offer.Hash}");
    }

    internal static async Task MergeCatalogAsync(string pinFile, string previous, string package, string output)
    {
        var pin = Pin(pinFile);
        var verified = ConnectorPackage.Inspect(package);
        CheckOfficial(verified.Manifest, pin.PublicKey);
        var entries = new List<JsonElement>();
        var offers = new List<ConnectorOffer>();
        if (previous != "-")
        {
            var old = await ReadCatalogAsync(pinFile, previous);
            var envelope = ConnectorFormat.Parse<SignedConnectorCatalog>(File.ReadAllBytes(previous));
            var historical = Convert.FromBase64String(envelope.Payload);
            ConnectorFormat.Verify(pin.PublicKey, historical, Convert.FromBase64String(envelope.Signature));
            using var document = JsonDocument.Parse(historical);
            var rawEntries = document.RootElement.GetProperty("packages").EnumerateArray().ToArray();
            foreach (var (o, raw) in old.Zip(rawEntries))
            {
                // Historical SDKs may be incompatible with today's signing host. Keep their authenticated entries.
                offers.Add(o);
                entries.Add(raw.Clone());
            }
        }
        var route = ConnectorReleaseRoute.For(verified.Manifest.Id, verified.Manifest.Version);
        var same = offers.SingleOrDefault(e => e.Manifest.Id == verified.Manifest.Id && e.Manifest.Version == verified.Manifest.Version);
        if (same is not null)
        {
            if (same.Hash != verified.Hash || !Encode(same.Manifest).AsSpan().SequenceEqual(Encode(verified.Manifest)))
                throw new InvalidDataException("This version already identifies different bytes or metadata.");
            if (same.Locations.SequenceEqual(route.Locations))
            {
                File.Copy(previous, output);
                return;
            }
            // Only the two original downloads can reach here. Preserve all authenticated metadata.
            var index = offers.IndexOf(same);
            var original = JsonNode.Parse(entries[index].GetRawText())!.AsObject();
            original["locations"] = JsonSerializer.SerializeToNode(route.Locations);
            entries[index] = JsonSerializer.SerializeToElement(original);
        }
        else
        {
            var entry = new CatalogEntry(verified.Manifest, verified.Hash, route.Locations);
            offers.Add(new(entry.Manifest, entry.Hash, Discovery, entry.Locations));
            ConnectorResolver.Plan([entry.Manifest.Id], new(), offers, ConnectorFormat.Fingerprint(pin.PublicKey),
                requestedHashes: new Dictionary<string, string> { [entry.Manifest.Id] = entry.Hash });
            entries.Add(JsonSerializer.SerializeToElement(entry, ConnectorFormat.Json));
        }
        var payload = Encode(new { schema = 1, packages = entries });
        using var key = SigningKey(pin.PublicKey);
        File.WriteAllBytes(output, Encode(new SignedConnectorCatalog(pin.PublicKey, Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256)))));
        _ = await ConnectorCatalogReader.ReadAsync(new Uri(Path.GetFullPath(output)).AbsoluteUri, pin.PublicKey, default);
    }
}

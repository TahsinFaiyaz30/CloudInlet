using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core.Connectors;

static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ConnectorFormat.Json);
static ECDsa Key(string path)
{
    var key = ECDsa.Create();
    var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), "CloudInlet.ConnectorPublisher.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    try { key.ImportPkcs8PrivateKey(bytes, out _); return key; }
    finally { CryptographicOperations.ZeroMemory(bytes); }
}

try
{
    switch (args)
    {
        case ["test-acquisition", var bootstrap, var profile]:
        {
            if (Path.Exists(profile)) throw new InvalidOperationException("Choose a new test directory.");
            var source = ConnectorFormat.Parse<ConnectorSource>(File.ReadAllBytes(bootstrap));
            await CloudInlet.Validation.ConnectorAcquisitionCheck.RunAsync(new ConnectorManager(profile, source), profile, default);
            Console.WriteLine("Verified discovery, selective download, provider loading, disable, enable and removal.");
            break;
        }
        case ["assert-core", var directory]:
            CorePayload.Verify(directory);
            break;
        case ["release-pack" or "release-verify", var id, var revision, var input, var pin, var package]:
            OfficialRelease.Package(args[0] == "release-pack", id, revision, input, pin, package);
            break;
        case ["release-route", var id, var version]:
            Console.WriteLine(JsonSerializer.Serialize(ConnectorReleaseRoute.For(id, version), ConnectorFormat.Json));
            break;
        case ["release-readme", var pin, var catalog, var directory, var revision, var output]:
            await ConnectorReleaseRoute.WriteDirectoryAsync(pin, catalog, directory, revision, output);
            break;
        case ["release-check-entry", var pin, var catalog, var id, var version, var revision, var package]:
            await OfficialRelease.VerifyListedAsync(pin, catalog, id, version, revision, package);
            break;
        case ["release-catalog", var pin, var previous, var pack, var output]:
            await OfficialRelease.MergeCatalogAsync(pin, previous, pack, output);
            break;
        case ["keygen", var path]:
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = key.ExportPkcs8PrivateKey();
            try { file.Write(ProtectedData.Protect(bytes, "CloudInlet.ConnectorPublisher.v1"u8.ToArray(), DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            Console.WriteLine("Publisher fingerprint " + ConnectorFormat.Fingerprint(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
            break;
        }
        case ["pack", var template, var build, var keyPath, var output]:
        {
            using var key = Key(keyPath);
            var m = ConnectorFormat.Parse<ConnectorManifest>(File.ReadAllBytes(template));
            if (m.Files.Keys.Any(n => !ConnectorFormat.PayloadFile(n))) throw new InvalidDataException("Invalid assembly filename.");
            var files = m.Files.Keys.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(build, n)));
            m = m with { PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                Files = files.ToDictionary(f => f.Key, f => Convert.ToHexString(SHA256.HashData(f.Value))) };
            m.Validate();
            var bytes = Encode(m);
            var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    void Add(string name, byte[] data) { using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); entry.Write(data); }
                    Add("manifest.json", bytes); Add("manifest.sig", key.SignData(bytes, HashAlgorithmName.SHA256));
                    foreach (var file in files) Add(file.Key, file.Value);
                }
                ConnectorPackage.Inspect(temp);
                File.Move(temp, output, false);
            }
            finally { File.Delete(temp); }
            Console.WriteLine(output);
            break;
        }
        case ["validate", var package]:
        {
            var p = ConnectorPackage.Inspect(package);
            Console.WriteLine($"{p.Manifest.Name} {p.Manifest.Version}\nPublisher {p.Manifest.Publisher} · {p.Manifest.Fingerprint}\nSHA-256 {p.Hash}\n{p.Manifest.CompatibilityIssue ?? "Compatible with this host"}");
            if (p.Manifest.CompatibilityIssue is not null) return 1;
            break;
        }
        case ["catalog", var keyPath, var output, .. var packages] when packages.Length > 0:
        {
            using var key = Key(keyPath);
            var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var mirrors = new Dictionary<string, string[]>();
            if (packages is ["--locations", var map, .. var files] && files.Length > 0)
            {
                mirrors = ConnectorFormat.Parse<Dictionary<string, string[]>>(File.ReadAllBytes(map));
                packages = files;
            }
            var entries = packages.Select(path =>
            {
                var pkg = ConnectorPackage.Inspect(path);
                if (pkg.Manifest.PublicKey != pub) throw new InvalidDataException("Catalog publisher must match the package publisher.");
                return new CatalogEntry(pkg.Manifest, pkg.Hash, mirrors.GetValueOrDefault(pkg.Manifest.Id) ?? [Uri.EscapeDataString(Path.GetFileName(path))]);
            }).ToArray();
            var bytes = Encode(new ConnectorCatalog(1, entries));
            File.WriteAllBytes(output, Encode(new SignedConnectorCatalog(pub, Convert.ToBase64String(bytes), Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256)))));
            break;
        }
        case ["bootstrap", var catalog, var address, var output]:
        {
            var (source, _) = await ConnectorCatalogReader.ReadAsync(new Uri(Path.GetFullPath(catalog)).AbsoluteUri, null, default);
            File.WriteAllBytes(output, Encode(source with { Location = new Uri(address).AbsoluteUri }));
            break;
        }
        default:
            Console.Error.WriteLine("keygen KEY | pack MANIFEST BUILD_DIR KEY OUTPUT | validate PACKAGE | catalog KEY OUTPUT [--locations MAP] PACKAGE... | bootstrap SIGNED_CATALOG CATALOG_URI OUTPUT | assert-core APP | release-pack/release-verify ID COMMIT INPUT PIN PACKAGE | release-catalog PIN PRIOR_OR_DASH PACKAGE OUTPUT | release-route ID VERSION | release-readme PIN CATALOG central/ID DOCS_COMMIT OUTPUT | release-check-entry PIN CATALOG ID VERSION COMMIT PACKAGE");
            return 2;
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace CloudInlet.Core.Connectors;

public sealed record ConnectorPackage(ConnectorManifest Manifest, string Hash)
{
    public const int MaxPackageBytes = 64 * 1024 * 1024;
    internal const int MaxAssemblyBytes = 32 * 1024 * 1024;
    public static ConnectorPackage Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxPackageBytes) throw new InvalidDataException("Connector package exceeds 64 MiB.");
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 3 or > 130 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException("Incomplete connector package or duplicate entries.");
        var bytes = Read(zip.GetEntry("manifest.json"), 256 * 1024);
        var manifest = ConnectorFormat.Parse<ConnectorManifest>(bytes);
        manifest.Validate();
        ConnectorFormat.Verify(manifest.PublicKey, bytes, Read(zip.GetEntry("manifest.sig"), 64));
        if (zip.Entries.Count != manifest.Files.Count + 2 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
            throw new InvalidDataException("Unexpected connector package inventory or expanded size.");
        foreach (var file in manifest.Files)
        {
            var data = VerifiedFile(zip, file.Key, file.Value);
            if (!ConnectorFormat.AssemblyFile(file.Key)) continue;
            using var pe = new PEReader(new MemoryStream(data, writable: false));
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor || !cor.Flags.HasFlag(CorFlags.ILOnly) ||
                !pe.GetMetadataReader().IsAssembly)
                throw new InvalidDataException($"Expected a managed IL assembly in {file.Key}.");
            var md = pe.GetMetadataReader();
            var name = md.GetString(md.GetAssemblyDefinition().Name);
            if (md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) is "CloudInlet" or "CloudInlet.Core"))
                throw new InvalidDataException("Connector assemblies may reference the public SDK and declared dependencies.");
            if (file.Key == manifest.Assembly && !md.TypeDefinitions.Select(md.GetTypeDefinition).Any(t =>
                (t.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public && !t.Attributes.HasFlag(TypeAttributes.Abstract) &&
                (md.GetString(t.Namespace) + "." + md.GetString(t.Name)).TrimStart('.') == manifest.EntryPoint))
                throw new InvalidDataException("The manifest entry point must be a public concrete type in its entry assembly.");
            if (file.Key != name + ".dll" || ConnectorLoadContext.SharedAssembly(name))
                throw new InvalidDataException($"A connector cannot supply or rename the host or framework assembly {name}.");
        }
        return new(manifest, hash);
    }
    internal static byte[] VerifiedFile(ZipArchive zip, string name, string digest)
    {
        var bytes = Read(zip.GetEntry(name), MaxAssemblyBytes);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != digest)
            throw new InvalidDataException($"The SHA-256 digest for {name} differs from the signed manifest.");
        return bytes;
    }
    internal static byte[] Read(ZipArchiveEntry? entry, int limit)
    {
        if (entry is null || entry.Length <= 0 || entry.Length > limit || entry.Name != entry.FullName ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
            throw new InvalidDataException("Unsafe or missing connector package entry.");
        using var stream = entry.Open();
        var result = new byte[checked((int)entry.Length)];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Connector entry exceeds its declared length.");
        return result;
    }
}

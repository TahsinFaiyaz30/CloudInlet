using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using CloudInlet.Connectors;

namespace CloudInlet.Core.Connectors;

/// <summary>Managed assembly resolution for one package and its declared dependencies. Runs as the desktop user.</summary>
internal sealed class ConnectorLoadContext(string package, ConnectorManifest manifest, IReadOnlyList<ConnectorLoadContext> dependencies)
    : AssemblyLoadContext(manifest.Id, isCollectible: false)
{
    // Pin the immutable file handle across cleanup by other processes. Lazy dependency reads refer to the same bytes.
    private readonly ZipArchive _package = new(new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), ZipArchiveMode.Read);

    internal static bool SharedAssembly(string name) => name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" ||
        name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CloudInlet", StringComparison.OrdinalIgnoreCase) || name.Equals("CloudInlet.Core", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(typeof(IProviderConnector).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);
    private Assembly FromPackage(string file)
    {
        byte[] bytes;
        lock (_package) bytes = ConnectorPackage.VerifiedFile(_package, file, manifest.Files[file]);
        using var stream = new MemoryStream(bytes, writable: false);
        return LoadFromStream(stream);
    }
    public object Activate() => Activator.CreateInstance(LoadFromAssemblyName(new(Path.GetFileNameWithoutExtension(manifest.Assembly)))
        .GetType(manifest.EntryPoint, throwOnError: true)!) ?? throw new InvalidDataException("Connector entry point could not be constructed.");

    internal IEnumerable<string> Exports => manifest.Files.Keys.Where(ConnectorFormat.AssemblyFile);

    protected override Assembly? Load(AssemblyName name)
    {
        var simple = name.Name ?? throw new FileLoadException("Missing assembly identity.");
        if (simple == typeof(IProviderConnector).Assembly.GetName().Name) return typeof(IProviderConnector).Assembly;
        if (simple is "CloudInlet.Core" or "CloudInlet") throw new FileLoadException("Connectors must use the public SDK.");
        if (SharedAssembly(simple)) return Default.LoadFromAssemblyName(name);
        var file = simple + ".dll";
        if (manifest.Files.ContainsKey(file)) return FromPackage(file);
        var dependency = dependencies.SingleOrDefault(d => d.Exports.Contains(file, StringComparer.OrdinalIgnoreCase));
        return dependency?.LoadFromAssemblyName(name) ?? throw new FileNotFoundException($"Undeclared connector dependency {simple}.");
    }
}

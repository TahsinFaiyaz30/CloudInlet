using System.Xml;

namespace CloudInlet.Windows;

/// <summary>Only packages that deliberately share both state locations and routing receipts may relocate them.</summary>
internal static class StoreStateRoutingPolicy
{
    internal static readonly string[] RequiredDirectories =
    [
        @"$(KnownFolder:LocalAppData)\CloudBay\Client",
        @"$(KnownFolder:LocalAppData)\CloudInlet\Client",
        @"$(KnownFolder:LocalAppData)\CloudInlet\Migration\Release"
    ];

    internal static bool SupportsRouting(string manifestPath)
    {
        using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = new XmlDocument { XmlResolver = null }; document.Load(reader);
        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("p", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        namespaces.AddNamespace("v", "http://schemas.microsoft.com/appx/manifest/virtualization/windows10");
        namespaces.AddNamespace("d6", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6");
        var paths = document.SelectNodes("/p:Package/p:Properties/v:FileSystemWriteVirtualization/v:ExcludedDirectories/v:ExcludedDirectory", namespaces)!
            .Cast<XmlNode>().Select(node => node.InnerText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Older Windows uses the declared desktop6 fallback. Require it as well so
        // the very same package cannot silently select a virtualized route there.
        return RequiredDirectories.All(paths.Contains) && document.SelectSingleNode(
            "/p:Package/p:Properties/d6:FileSystemWriteVirtualization", namespaces)?.InnerText == "disabled";
    }
}

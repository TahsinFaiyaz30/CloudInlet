using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudInlet.Core.Connectors;

public sealed record ConnectorStorePackage(string ProductId, string PackageFamilyName)
{
    public void Validate()
    {
        if (ProductId is null || !Regex.IsMatch(ProductId, @"\A[A-Z0-9]{12}\z") ||
            PackageFamilyName is null || !Regex.IsMatch(PackageFamilyName, @"\A[A-Za-z0-9.-]{3,50}_[a-z0-9]{13}\z"))
            throw new InvalidDataException("Invalid Microsoft Store product ID or package family name.");
    }
}
public sealed record CatalogEntry(ConnectorManifest Manifest, string Hash, string[] Locations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConnectorStorePackage? Store = null);
public sealed record ConnectorCatalog(int Schema, CatalogEntry[] Packages);
public sealed record SignedConnectorCatalog(string PublicKey, string Payload, string Signature);

public static class ConnectorCatalogReader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<(ConnectorSource Source, ConnectorOffer[] Offers)> ReadAsync(string location, string? pinnedKey, CancellationToken ct)
    {
        var uri = Address(location);
        using var buffer = new MemoryStream();
        await CopyAsync(uri, buffer, 4 * 1024 * 1024, ct).ConfigureAwait(false);
        var envelope = ConnectorFormat.Parse<SignedConnectorCatalog>(buffer.ToArray());
        if (pinnedKey is not null && pinnedKey != envelope.PublicKey) throw new InvalidDataException("The catalog publisher key has changed.");
        var data = Convert.FromBase64String(envelope.Payload);
        ConnectorFormat.Verify(envelope.PublicKey, data, Convert.FromBase64String(envelope.Signature));
        var catalog = ConnectorFormat.Parse<ConnectorCatalog>(data);
        if (catalog.Schema != 1 || catalog.Packages is not { Length: <= 512 }) throw new InvalidDataException("Unsupported connector catalog.");
        var packages = new List<ConnectorOffer>();
        foreach (var entry in catalog.Packages)
        {
            entry.Manifest.Validate();
            if (entry.Manifest.PublicKey != envelope.PublicKey || !ConnectorFormat.Digest(entry.Hash) || entry.Locations is not { Length: > 0 and <= 8 })
                throw new InvalidDataException("The catalog must authenticate every package, mirror and publisher.");
            var locations = entry.Locations.Select(raw =>
            {
                var endpoint = new Uri(uri, raw);
                if (endpoint.IsFile && (!uri.IsFile || Path.GetDirectoryName(endpoint.LocalPath) != Path.GetDirectoryName(uri.LocalPath)) ||
                    endpoint.Scheme is not ("file" or "https") || endpoint.IsUnc || endpoint.UserInfo.Length != 0)
                    throw new InvalidDataException("Unpermitted connector download location.");
                return endpoint.AbsoluteUri;
            }).ToArray();
            entry.Store?.Validate();
            packages.Add(new(entry.Manifest, entry.Hash, uri.AbsoluteUri, locations, entry.Store));
        }
        return (new(uri.AbsoluteUri, envelope.PublicKey), packages.ToArray());
    }
    internal static Uri Address(string location)
    {
        var uri = new Uri(location, UriKind.Absolute);
        if (uri.Scheme is not ("https" or "file") || uri.UserInfo.Length > 0 || uri.IsUnc)
            throw new InvalidDataException("Select a local file or an HTTPS connector source.");
        return uri;
    }
    internal static async Task CopyAsync(Uri address, Stream destination, long limit, CancellationToken ct)
    {
        async Task Copy(Stream input)
        {
            var buffer = new byte[65536];
            int count;
            long total = 0;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if ((total += count) > limit) throw new InvalidDataException("Connector download exceeds its size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
        }
        if (address.IsFile) { using var input = File.OpenRead(address.LocalPath); await Copy(input); return; }
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                address = new Uri(address, next);
                if (address.Scheme != "https" || address.UserInfo.Length > 0) throw new InvalidDataException("Invalid connector download redirect.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Connector download exceeds its size limit.");
            using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await Copy(input); return;
        }
        throw new IOException("Too many connector download redirects.");
    }
}

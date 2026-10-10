using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudInlet.Core.Updates;

public static class UpdateManifestRules
{
    public const string Repository = "TahsinFaiyaz30/CloudInlet";
    // CloudBay clients upgrade through the immutable 1.1.2 bridge. Subsequent
    // CloudInlet releases trust only the canonical schema-2 feed and assets.
    public static readonly Uri FeedUri = new($"https://github.com/{Repository}/releases/latest/download/updates-v2.json");
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new NamedEnumConverter<UpdateBuildFlavor>(), new NamedEnumConverter<UpdateInstallerKind>() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static UpdateCandidate? Select(UpdateManifest manifest, InstalledUpdateIdentity identity)
    {
        ValidateIdentity(identity);
        if (manifest.SchemaVersion != 2 || manifest.Repository != Repository ||
            !UpdateVersion.TryParse(manifest.Version, out var version) || manifest.Tag != "v" + manifest.Version ||
            manifest.Assets is null || manifest.Assets.Count is < 1 or > 32)
            throw new InvalidDataException("The release update manifest is invalid.");
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            ValidateAsset(asset, manifest.Version);
            if (!variants.Add($"{asset.BuildFlavor}|{asset.InstallerKind}|{asset.Architecture}") || !names.Add(asset.FileName))
                throw new InvalidDataException("The release manifest contains duplicate update variants.");
        }
        UpdateVersion.TryParse(identity.Version, out var installed);
        if (version <= installed || identity.InstallerKind is UpdateInstallerKind.Store)
            return null;
        var selected = manifest.Assets.SingleOrDefault(asset => asset.BuildFlavor == identity.BuildFlavor &&
            asset.InstallerKind == identity.InstallerKind && asset.Architecture == identity.Architecture);
        if (selected is null) throw new InvalidDataException("This release does not contain an update for the installed package type.");
        return new UpdateCandidate(manifest.Version, manifest.Tag, selected);
    }

    public static void ValidateIdentity(InstalledUpdateIdentity identity)
    {
        if (!UpdateVersion.TryParse(identity.Version, out _) || !Enum.IsDefined(identity.BuildFlavor) ||
            !Enum.IsDefined(identity.InstallerKind) || identity.Architecture is not ("x64" or "arm64"))
            throw new InvalidDataException("The installed update identity is invalid.");
    }

    public static void ValidateCandidate(UpdateCandidate candidate, InstalledUpdateIdentity identity)
    {
        if (candidate?.Asset?.FileName is null) throw new InvalidDataException("The cached update identity is invalid.");
        var selected = Select(new UpdateManifest(2, Repository,
            candidate.Version, candidate.Tag, [candidate.Asset]), identity);
        if (selected is null) throw new InvalidDataException("The cached update is not newer than this installation.");
    }

    public static Uri DownloadUri(UpdateCandidate candidate)
    {
        if (candidate is null || !UpdateVersion.TryParse(candidate.Version, out _) || candidate.Tag != "v" + candidate.Version)
            throw new InvalidDataException("The release update identity is invalid.");
        ValidateAsset(candidate.Asset, candidate.Version);
        return new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(candidate.Tag)}/{Uri.EscapeDataString(candidate.Asset.FileName)}");
    }

    private static void ValidateAsset(UpdateAsset asset, string version)
    {
        if (asset is null || !Enum.IsDefined(asset.BuildFlavor) || !Enum.IsDefined(asset.InstallerKind) ||
            asset.Architecture is not ("x64" or "arm64") || asset.Size is <= 0 or > 1_073_741_824L ||
            asset.Sha256 is not { Length: 64 } || !asset.Sha256.All(Uri.IsHexDigit) ||
            asset.FileName is null || !Regex.IsMatch(asset.FileName, @"^CloudInlet-[A-Za-z0-9][A-Za-z0-9._-]{0,180}\.(exe|msi|zip|msix|msixupload)$",
                RegexOptions.CultureInvariant) || asset.FileName.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("A release installer entry is invalid.");
        var suffix = asset.InstallerKind switch
        {
            UpdateInstallerKind.Exe => "setup.exe", UpdateInstallerKind.Msi => "setup.msi",
            UpdateInstallerKind.Portable => "portable.zip", _ => ""
        };
        var expectedName = $"CloudInlet-{version}-win-{asset.Architecture}-{asset.BuildFlavor.ToString().ToLowerInvariant()}-{suffix}";
        if (suffix.Length == 0 || asset.FileName != expectedName)
            throw new InvalidDataException("The release installer name does not match its version and installed variant.");
    }

    private sealed class NamedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();
                foreach (var value in Enum.GetValues<T>())
                    if (string.Equals(name, value.ToString(), StringComparison.OrdinalIgnoreCase)) return value;
            }
            // JsonStringEnumConverter also accepts numeric strings such as "0". An immutable
            // release variant must explicitly name its flavor and package type.
            throw new JsonException("A release variant must use a recognized enum name.");
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("A release variant is undefined."));
    }
}

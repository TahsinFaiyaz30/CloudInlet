using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CloudInlet.Connectors;

namespace CloudInlet.Core.Connectors;

public sealed record VersionRange(string Min, string Max)
{
    public bool Contains(string version) => ConnectorFormat.VersionOf(version) >= ConnectorFormat.VersionOf(Min) &&
        ConnectorFormat.VersionOf(version) < ConnectorFormat.VersionOf(Max);
    internal void Validate()
    {
        if (ConnectorFormat.VersionOf(Min) >= ConnectorFormat.VersionOf(Max))
            throw new InvalidDataException("Expected an inclusive minimum and a larger exclusive maximum version.");
    }
}

public sealed record ConnectorDependency(string Id, string PublisherKey, VersionRange Versions, bool Optional = false);
public sealed record ConnectorManifest(int Schema, string Id, string Name, string Publisher, string PublicKey,
    string Version, VersionRange Sdk, string[] Platforms, string[] Architectures, string Assembly, string EntryPoint,
    [property: JsonConverter(typeof(CapabilityNames))] ConnectorCapabilities Capabilities, ConnectorDependency[] Dependencies, string License, string Documentation,
    string ReleaseNotes, Dictionary<string, string> Files, string Kind = "provider")
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRevision { get; init; }
    [JsonIgnore] public string Fingerprint => ConnectorFormat.Fingerprint(PublicKey);
    [JsonIgnore] public string? CompatibilityIssue => !Sdk.Contains(ConnectorFormat.SdkVersion)
        ? $"Requires connector SDK [{Sdk.Min}, {Sdk.Max}). This app provides {ConnectorFormat.SdkVersion}."
        : Capabilities.HasFlag(CapabilityNames.Unknown) ? "This connector declares capabilities this app does not recognize. Update CloudInlet or choose an earlier compatible version."
        : !Platforms.Contains("windows") || !Architectures.Contains("x64") || !OperatingSystem.IsWindows() ||
          System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64
            ? "This connector requires another operating system or processor architecture." : null;

    public void Validate()
    {
        if (Schema != 1 || !ConnectorFormat.ValidId(Id) || Id == "local" || string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(Publisher) || string.IsNullOrWhiteSpace(EntryPoint) || EntryPoint.Length > 512 ||
            string.IsNullOrWhiteSpace(License) || string.IsNullOrWhiteSpace(Documentation) || ReleaseNotes is null ||
            Kind is not ("provider" or "component") || Platforms is not { Length: > 0 } || Architectures is not { Length: > 0 } ||
            Files is not { Count: > 0 and <= 128 } || Dependencies is null || Dependencies.Length > 32 || Sdk is null)
            throw new InvalidDataException("Incomplete or unsupported connector manifest.");
        if (SourceRevision is not null && !Regex.IsMatch(SourceRevision, @"\A[0-9a-f]{40}\z"))
            throw new InvalidDataException("Invalid connector source revision.");
        ConnectorFormat.VersionOf(Version);
        Sdk.Validate();
        _ = Fingerprint;
        if (!ConnectorFormat.AssemblyFile(Assembly) || !Files.ContainsKey(Assembly) ||
            Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count ||
            Files.Any(f => !ConnectorFormat.PayloadFile(f.Key) || !ConnectorFormat.Digest(f.Value)))
            throw new InvalidDataException("Connector payload names or SHA-256 hashes are invalid.");
        if (Dependencies.Any(d => d is null || !ConnectorFormat.ValidId(d.Id) || !ConnectorFormat.Digest(d.PublisherKey) || d.Versions is null) ||
            Dependencies.Select(d => d.Id).Distinct().Count() != Dependencies.Length)
            throw new InvalidDataException("Dependencies require unique IDs, exact publisher fingerprints and bounded versions.");
        foreach (var dependency in Dependencies) dependency.Versions.Validate();
    }
}

/// <summary>An older host can show a future package alongside the compatible entries in the same signed catalog.</summary>
public sealed class CapabilityNames : JsonConverter<ConnectorCapabilities>
{
    internal const ConnectorCapabilities Unknown = (ConnectorCapabilities)int.MinValue;
    private static readonly Dictionary<string, ConnectorCapabilities> Names =
        Enum.GetValues<ConnectorCapabilities>().ToDictionary(flag => flag.ToString(), StringComparer.Ordinal);

    public override ConnectorCapabilities Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Connector capabilities must be a list of names.");
        var flags = ConnectorCapabilities.None;
        foreach (var name in reader.GetString()!.Split(',', StringSplitOptions.TrimEntries))
            flags |= Names.GetValueOrDefault(name, Unknown);
        return flags;
    }

    public override void Write(Utf8JsonWriter writer, ConnectorCapabilities flags, JsonSerializerOptions options) =>
        writer.WriteStringValue(flags.HasFlag(Unknown) ? "Unrecognized" : flags.ToString());
}

public static class ConnectorFormat
{
    public const string SdkVersion = "1.0.0";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, Converters = { new JsonStringEnumConverter() }
    };
    public static bool ValidId(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\z") && value.Length <= 96 &&
        !Regex.IsMatch(value, @"\A(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|\z)");
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) && value == value.ToUpperInvariant();
    public static bool PayloadFile(string? value) => AssemblyFile(value) || value is "LICENSE.txt" or "README.md";
    public static bool AssemblyFile(string? value) => value is { Length: <= 160 } && Regex.IsMatch(value, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\.dll\z") &&
        !Regex.IsMatch(value, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static Version VersionOf(string value)
    {
        if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != value)
            throw new InvalidDataException("A connector version must have three canonical numeric components.");
        return version;
    }
    public static string Fingerprint(string key)
    {
        using var signer = ImportKey(key);
        return Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
    }
    internal static ECDsa ImportKey(string key)
    {
        var bytes = Convert.FromBase64String(key);
        var signer = ECDsa.Create();
        try
        {
            signer.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || signer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) != key)
                throw new InvalidDataException("Expected a canonical ECDSA P-256 publisher key.");
            return signer;
        }
        catch { signer.Dispose(); throw; }
    }
    public static void Verify(string key, byte[] payload, byte[] signature)
    {
        using var signer = ImportKey(key);
        if (!signer.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            throw new InvalidDataException("The connector publisher signature is invalid.");
    }
    public static T Parse<T>(byte[] bytes) where T : class
    {
        using var doc = JsonDocument.Parse(bytes);
        CheckNames(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing connector metadata.");
    }
    private static void CheckNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckNames(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in value.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate connector metadata field.");
            CheckNames(field.Value);
        }
    }
}

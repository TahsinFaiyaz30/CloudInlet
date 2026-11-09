using CloudInlet.Core.Sync;

namespace CloudInlet.Core.Migration;

public sealed record CloudPrefixRule(string SourcePrefix, string DestinationPrefix);

/// <summary>Immutable reviewed folder mapping. More specific generated backup folders take
/// precedence over the root mapping; every destination remains in the reviewed namespace.</summary>
public sealed class CloudPrefixKeyMap
{
    private readonly string _source, _destination;
    public IReadOnlyList<CloudPrefixRule> Rules { get; }
    public CloudPrefixKeyMap(string source, string destination, IEnumerable<CloudPrefixRule>? rules = null)
    {
        Validate(source); Validate(destination);
        if (source.StartsWith(destination, StringComparison.Ordinal) || destination.StartsWith(source, StringComparison.Ordinal))
            throw new ArgumentException("Cloud migration namespaces must be separate.");
        _source = source; _destination = destination;
        var values = (rules ?? []).OrderByDescending(value => value.SourcePrefix.Length).ThenBy(value => value.SourcePrefix, StringComparer.Ordinal).ToArray();
        foreach (var rule in values)
        {
            Validate(rule.SourcePrefix); Validate(rule.DestinationPrefix);
            if (rule.SourcePrefix == source || !rule.SourcePrefix.StartsWith(source, StringComparison.Ordinal) ||
                rule.DestinationPrefix == destination || !rule.DestinationPrefix.StartsWith(destination, StringComparison.Ordinal))
                throw new ArgumentException("Nested cloud mappings must stay within the reviewed root folders.");
        }
        if (values.Select(value => value.SourcePrefix).Distinct(StringComparer.Ordinal).Count() != values.Length ||
            values.Select(value => value.DestinationPrefix).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Cloud migration mappings cannot repeat a source or destination folder.");
        Rules = Array.AsReadOnly(values);
    }
    public string Map(string sourceKey)
    {
        if (!sourceKey.StartsWith(_source, StringComparison.Ordinal) || sourceKey.Length <= _source.Length)
            throw new InvalidDataException("The cloud version is outside the reviewed source folder.");
        var rule = Rules.FirstOrDefault(value => sourceKey.StartsWith(value.SourcePrefix, StringComparison.Ordinal));
        return rule is null ? _destination + sourceKey[_source.Length..] : rule.DestinationPrefix + sourceKey[rule.SourcePrefix.Length..];
    }
    public void ValidateDistinctKeys(IEnumerable<string> keys)
    {
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in keys.Distinct(StringComparer.Ordinal))
        {
            var target = Map(source);
            if (destinations.TryGetValue(target, out var previous) && previous != source)
                throw new InvalidDataException("Two original cloud names would use the same migrated name. No histories were merged or replaced.");
            destinations[target] = source;
        }
    }
    private static void Validate(string value)
    {
        if (string.IsNullOrEmpty(value) || value != PathRules.NormalizePrefix(value) || !value.EndsWith('/') ||
            value.StartsWith('/') || value.Contains('\\') || value.Split('/').SkipLast(1).Any(part => part is "" or "." or ".."))
            throw new ArgumentException("A normalized nonempty cloud folder is required.");
    }
}

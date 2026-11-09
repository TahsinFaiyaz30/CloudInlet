using System.Security.Cryptography;
using System.Text;

namespace CloudInlet.Core.Sync;

public static class SyncRootMigrationIdentity
{
    public static string StateName(AppSettings settings, string root, string prefix)
    {
        var binding = Binding(settings, root);
        return (binding is not null && binding.AccountId == settings.AccountId && binding.BucketId == settings.BucketId && binding.Prefix == prefix ? binding.StateName : null) ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            settings.AccountId + "|" + settings.BucketId + "|" + prefix + "|" + root.ToUpperInvariant())))[..24];
    }
    public static bool IsCanonical(AppSettings settings, string root) => Binding(settings, root)?.CanonicalRegistration ?? false;
    public static void Validate(AppSettings settings)
    {
        if (settings.RootBindings is null || settings.RootBindings.Any(item => item is null ||
            !Path.IsPathFullyQualified(item.RootPath) || item.AccountId is null || item.BucketId is null || item.Prefix is null || item.Prefix != PathRules.NormalizePrefix(item.Prefix) || item.StateName is not { Length: 24 } || !item.StateName.All(Uri.IsHexDigit)) ||
            settings.RootBindings.Select(item => Path.GetFullPath(item.RootPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.RootBindings.Count)
            throw new InvalidDataException("The saved sync-folder migration identities are invalid. Restore their recovery record before continuing.");
    }
    private static SyncRootBinding? Binding(AppSettings settings, string root)
    {
        Validate(settings);
        return settings.RootBindings.SingleOrDefault(item => Path.GetFullPath(item.RootPath).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }
}

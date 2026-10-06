namespace CloudInlet.Core.OneDrive;

/// <summary>Public registration and authority used when connecting a new OneDrive account.</summary>
public sealed record OneDriveSignInOptions
{
    internal OneDriveSignInOptions(string clientId, string tenant)
    {
        ClientId = clientId;
        Tenant = tenant;
    }

    public string ClientId { get; }
    public string Tenant { get; }
}

/// <summary>CloudInlet supplies its public application registration; users only choose their Microsoft account.</summary>
public static class OneDriveSignInConfiguration
{
    // A public-client application ID is public configuration, not a credential.
    public const string DefaultClientId = "3f7bdd18-1b8e-44a4-8600-77c20f466005";
    public const string DefaultTenant = "common";
    public const string YxrczTenantId = "e99dfb85-288d-46a1-91b0-7d5d1fdac950";
    public const string ClientIdEnvironmentVariable = "CLOUDINLET_ONEDRIVE_CLIENT_ID";
    public const string TenantEnvironmentVariable = "CLOUDINLET_ONEDRIVE_TENANT_ID";
    public const string LegacyClientIdEnvironmentVariable = "CLOUDBAY_ONEDRIVE_CLIENT_ID";
    public const string LegacyTenantEnvironmentVariable = "CLOUDBAY_ONEDRIVE_TENANT_ID";

    public static OneDriveSignInOptions FromEnvironment(bool useYxrczTenant = false) => FromEnvironment(
        Environment.GetEnvironmentVariable, useYxrczTenant);

    internal static OneDriveSignInOptions FromEnvironment(Func<string, string?> read, bool useYxrczTenant = false)
    {
        string? Override(string current, string legacy)
        {
            var value = read(current);
            return string.IsNullOrWhiteSpace(value) ? read(legacy) : value;
        }
        return Resolve(Override(ClientIdEnvironmentVariable, LegacyClientIdEnvironmentVariable),
            Override(TenantEnvironmentVariable, LegacyTenantEnvironmentVariable), useYxrczTenant);
    }

    /// <summary>Blank overrides use built-in defaults. An explicit authority overrides the optional yxrcz mode.</summary>
    public static OneDriveSignInOptions Resolve(string? clientIdOverride = null, string? tenantOverride = null,
        bool useYxrczTenant = false)
    {
        var clientId = string.IsNullOrWhiteSpace(clientIdOverride) ? DefaultClientId : clientIdOverride.Trim();
        if (!Guid.TryParse(clientId, out var applicationId) || applicationId == Guid.Empty)
            throw new ArgumentException($"{ClientIdEnvironmentVariable} must contain a valid Microsoft application (client) ID.", nameof(clientIdOverride));

        var tenant = string.IsNullOrWhiteSpace(tenantOverride)
            ? useYxrczTenant ? YxrczTenantId : DefaultTenant
            : tenantOverride.Trim();
        if (tenant.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.')))
            throw new ArgumentException($"{TenantEnvironmentVariable} must contain common, consumers, organizations, or a Microsoft tenant ID/domain.", nameof(tenantOverride));

        return new(applicationId.ToString("D"), tenant);
    }
}

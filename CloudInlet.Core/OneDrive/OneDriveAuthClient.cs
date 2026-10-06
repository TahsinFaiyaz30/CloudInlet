using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudInlet.Core.OneDrive;

/// <summary>Delegated public-client authentication. The host persists refreshed credentials in its secure credential store.</summary>
public sealed class OneDriveAuthClient
{
    public const string DefaultScopes = "offline_access https://graph.microsoft.com/Files.ReadWrite https://graph.microsoft.com/User.Read";
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    { PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 32 });
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _authority;
    private readonly Func<OneDriveTokenSet, CancellationToken, Task>? _persist;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private OneDriveTokenSet? _tokens;

    public OneDriveAuthClient(string clientId, string tenant = "common", OneDriveTokenSet? tokens = null,
        Func<OneDriveTokenSet, CancellationToken, Task>? persist = null, HttpClient? http = null)
    {
        if (!Guid.TryParse(clientId, out _)) throw new ArgumentException("Enter the Microsoft Entra application (client) ID for CloudInlet.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(tenant) || tenant.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.')))
            throw new ArgumentException("The Microsoft tenant must be common, consumers, organizations, or a tenant ID/domain.", nameof(tenant));
        _clientId = clientId;
        _authority = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/";
        _tokens = tokens;
        _persist = persist;
        _http = http ?? SharedHttp;
    }

    public async Task<OneDriveDeviceCode> BeginDeviceSignInAsync(CancellationToken cancellationToken = default)
    {
        using var result = await FormAsync("devicecode", new() { ["client_id"] = _clientId, ["scope"] = DefaultScopes }, cancellationToken);
        var value = result.RootElement;
        return new(value.GetProperty("user_code").GetString()!, new(value.GetProperty("verification_uri").GetString()!),
            Text(value, "message") ?? "Open the Microsoft sign-in page and enter the code.", value.GetProperty("device_code").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(value.GetProperty("expires_in").GetInt32()),
            value.TryGetProperty("interval", out var interval) ? Math.Max(1, interval.GetInt32()) : 5);
    }

    public async Task<OneDriveTokenSet> CompleteDeviceSignInAsync(OneDriveDeviceCode code, CancellationToken cancellationToken = default)
    {
        var interval = code.IntervalSeconds;
        while (DateTimeOffset.UtcNow < code.ExpiresUtc)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            try
            {
                using var result = await FormAsync("token", new() { ["client_id"] = _clientId,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = code.DeviceCode }, cancellationToken);
                return await SaveAsync(ParseTokens(result.RootElement, null), cancellationToken).ConfigureAwait(false);
            }
            catch (OneDriveApiException error) when (error.Code == "authorization_pending") { }
            catch (OneDriveApiException error) when (error.Code == "slow_down") { interval = Math.Min(60, interval + 5); }
        }
        throw new OneDriveSignInRequiredException("Microsoft sign-in expired. Start sign-in again.");
    }

    public OneDriveAuthorizationRequest CreateAuthorizationRequest(Uri redirectUri)
    {
        if (!redirectUri.IsAbsoluteUri || redirectUri.Scheme != "http" || !redirectUri.IsLoopback)
            throw new ArgumentException("Public-client browser sign-in requires an HTTP loopback redirect URI.", nameof(redirectUri));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var query = new Dictionary<string, string> { ["client_id"] = _clientId, ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri, ["response_mode"] = "query", ["scope"] = DefaultScopes,
            ["state"] = state, ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["prompt"] = "select_account" };
        return new(new(_authority + "authorize?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))),
            redirectUri, state, verifier);
    }

    public async Task<OneDriveTokenSet> RedeemAuthorizationCodeAsync(OneDriveAuthorizationRequest request, string code, string state,
        CancellationToken cancellationToken = default)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.State), Encoding.UTF8.GetBytes(state)))
            throw new InvalidDataException("Microsoft sign-in state did not match this request.");
        using var result = await FormAsync("token", new() { ["client_id"] = _clientId, ["grant_type"] = "authorization_code",
            ["code"] = code, ["redirect_uri"] = request.RedirectUri.AbsoluteUri, ["code_verifier"] = request.CodeVerifier,
            ["scope"] = DefaultScopes }, cancellationToken);
        return await SaveAsync(ParseTokens(result.RootElement, null), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_tokens is { } usable && usable.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return usable.AccessToken;
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is { } cached && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return cached.AccessToken;
            var previous = _tokens ?? throw new OneDriveSignInRequiredException("Connect the OneDrive account again to continue this transfer.");
            if (string.IsNullOrWhiteSpace(previous.RefreshToken)) throw new OneDriveSignInRequiredException("Connect the OneDrive account again to continue this transfer.");
            try
            {
                using var result = await FormAsync("token", new() { ["client_id"] = _clientId, ["grant_type"] = "refresh_token",
                    ["refresh_token"] = previous.RefreshToken, ["scope"] = DefaultScopes }, cancellationToken);
                return (await SaveAsync(ParseTokens(result.RootElement, previous), cancellationToken).ConfigureAwait(false)).AccessToken;
            }
            catch (OneDriveApiException error) when (error.Code is "invalid_grant" or "interaction_required" or "consent_required")
            { throw new OneDriveSignInRequiredException("Microsoft requires this OneDrive account to sign in again. Saved transfer progress is retained."); }
        }
        finally { _refresh.Release(); }
    }

    /// <summary>Invalidate only the token rejected by the service; another worker's refreshed token remains valid.</summary>
    public void InvalidateAccessToken(string rejectedToken)
    {
        var current = _tokens;
        if (current?.AccessToken == rejectedToken)
            Interlocked.CompareExchange(ref _tokens, current with { ExpiresUtc = DateTimeOffset.MinValue }, current);
    }

    private async Task<OneDriveTokenSet> SaveAsync(OneDriveTokenSet tokens, CancellationToken cancellationToken)
    {
        if (_persist is not null) await _persist(tokens, cancellationToken).ConfigureAwait(false);
        _tokens = tokens;
        return tokens;
    }

    private async Task<JsonDocument> FormAsync(string action, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(values);
        using var response = await _http.PostAsync(_authority + action, content, cancellationToken).ConfigureAwait(false);
        var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (response.IsSuccessStatusCode) return result;
        using (result)
        {
            var error = Text(result.RootElement, "error") ?? "authentication_failed";
            // Error descriptions can contain request parameters or account details. Keep stored diagnostics free of tokens.
            var codes = result.RootElement.TryGetProperty("error_codes", out var savedCodes) && savedCodes.ValueKind == JsonValueKind.Array
                ? savedCodes.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)).Select(value => value.GetInt32()).ToArray()
                : Array.Empty<int>();
            var detail = codes.Contains(7000218)
                ? " Enable Allow public client flows in the app registration's Authentication settings, save, and sign in again."
                : "";
            var diagnostic = codes.Length == 0 ? error : error + "; " + string.Join(", ", codes.Take(8).Select(value => "AADSTS" + value));
            throw new OneDriveApiException((int)response.StatusCode, error, $"Microsoft sign-in failed ({diagnostic})." + detail);
        }
    }

    private static OneDriveTokenSet ParseTokens(JsonElement value, OneDriveTokenSet? previous) => new(
        value.GetProperty("access_token").GetString()!, Text(value, "refresh_token") ?? previous?.RefreshToken ?? "",
        DateTimeOffset.UtcNow.AddSeconds(value.GetProperty("expires_in").GetInt32()), Text(value, "scope") ?? previous?.Scope ?? DefaultScopes);
    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

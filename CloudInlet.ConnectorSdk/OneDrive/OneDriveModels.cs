namespace CloudInlet.Core.OneDrive;

public sealed record OneDriveTokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc, string Scope);
public sealed record OneDriveDeviceCode(string UserCode, Uri VerificationUri, string Message, string DeviceCode,
    DateTimeOffset ExpiresUtc, int IntervalSeconds);
public sealed record OneDriveAuthorizationRequest(Uri AuthorizationUri, Uri RedirectUri, string State, string CodeVerifier);
public sealed record OneDriveDrive(string Id, string Name, string Owner);
public sealed record OneDriveItem(string Id, string Name, long Size, string ETag, string? CTag, bool IsFolder,
    DateTimeOffset ModifiedUtc, string? Sha1 = null, string? Sha256 = null, string? DownloadUrl = null);
public sealed record OneDrivePage(IReadOnlyList<OneDriveItem> Items, string? NextLink);
public sealed record OneDriveUploadSession(string UploadUrl, DateTimeOffset ExpiresUtc, long NextOffset);

public sealed class OneDriveApiException(int statusCode, string code, string message) : IOException(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; init; }
}

public sealed class OneDriveSignInRequiredException(string message) : InvalidOperationException(message);

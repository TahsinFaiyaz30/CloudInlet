using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudBay.Core.B2;
using CloudBay.Core.Transfers;

namespace CloudBay.Core.OneDrive;

/// <summary>Graph metadata and streaming transport. No payload operation opens a local file or buffers an entire response.</summary>
public sealed class OneDriveClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 32,
        ConnectTimeout = TimeSpan.FromSeconds(30)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;
    private readonly OneDriveAuthClient _auth;
    private readonly BandwidthLimiter _uploadLimit;
    private readonly BandwidthLimiter _downloadLimit;
    private readonly ConcurrencyGate _uploads = new();
    private readonly ConcurrencyGate _downloads = new();
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(2);
    internal const string ItemFields = "id,name,size,eTag,cTag,folder,file,lastModifiedDateTime,@microsoft.graph.downloadUrl";
    private const string GraphRoot = "https://graph.microsoft.com/v1.0/";

    public OneDriveClient(OneDriveAuthClient auth, HttpClient? http = null, TransferBandwidthBudget? bandwidthBudget = null)
    {
        _auth = auth; _http = http ?? SharedHttp;
        bandwidthBudget ??= new TransferBandwidthBudget();
        _uploadLimit = bandwidthBudget.Uploads;
        _downloadLimit = bandwidthBudget.Downloads;
    }

    /// <summary>Zero bytes/second means unlimited. Limits apply across all simultaneous account transfers.</summary>
    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond, int connections) =>
        Configure(uploadBytesPerSecond, downloadBytesPerSecond, connections, connections);

    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond, int uploadConnections, int downloadConnections)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uploadBytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(downloadBytesPerSecond);
        if (uploadConnections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(uploadConnections));
        if (downloadConnections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(downloadConnections));
        _uploads.Configure(uploadConnections);
        _downloads.Configure(downloadConnections);
        _uploadLimit.Configure(uploadBytesPerSecond);
        _downloadLimit.Configure(downloadBytesPerSecond);
    }

    public async Task<IReadOnlyList<OneDriveDrive>> ListDrivesAsync(CancellationToken cancellationToken = default)
    {
        // /me/drive identifies the user's actual primary OneDrive. Personal
        // accounts can also expose unusable ObjectHandle entries in /me/drives.
        using var primaryResult = await GetJsonAsync("me/drive?$select=id,name,owner", cancellationToken);
        var primary = ParseDrive(primaryResult.RootElement);
        var drives = new List<OneDriveDrive> { primary };
        var includedIds = new HashSet<string>(StringComparer.Ordinal) { primary.Id };
        var checkedIds = new HashSet<string>(includedIds, StringComparer.Ordinal);
        string? url = "me/drives?$select=id,name,owner";
        while (url is not null)
        {
            using var result = await GetJsonAsync(url, cancellationToken);
            foreach (var item in result.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                if (!checkedIds.Add(id)) continue;
                try
                {
                    // Validate additional drives once per enumeration, through
                    // the shared authenticated connection, before offering them.
                    using var additionalResult = await GetJsonAsync($"drives/{Segment(id)}?$select=id,name,owner", cancellationToken);
                    var additional = ParseDrive(additionalResult.RootElement);
                    if (includedIds.Add(additional.Id)) drives.Add(additional);
                }
                catch (OneDriveApiException error) when (error.StatusCode is 403 or 404 ||
                    error.StatusCode == 400 && error.Code.Equals("invalidRequest", StringComparison.OrdinalIgnoreCase))
                {
                    // An inaccessible, removed or invalid additional entry must
                    // not prevent access to the verified primary OneDrive.
                }
            }
            url = OneDriveAuthClient.Text(result.RootElement, "@odata.nextLink");
        }
        return drives;
    }

    private static OneDriveDrive ParseDrive(JsonElement item)
    {
        var owner = item.TryGetProperty("owner", out var value) && value.TryGetProperty("user", out var user)
            ? OneDriveAuthClient.Text(user, "displayName") ?? "Microsoft account" : "Microsoft account";
        return new(item.GetProperty("id").GetString()!, OneDriveAuthClient.Text(item, "name") ?? "OneDrive", owner);
    }

    public Task<OneDriveItem> GetRootAsync(string driveId, CancellationToken cancellationToken = default) =>
        GetItemAtAsync($"drives/{Segment(driveId)}/root?$select={ItemFields}", cancellationToken);

    // Personal OneDrive can omit the preauthenticated download URL when $select is
    // present, even if it explicitly includes that annotation. Fresh transfer
    // metadata must request the full item so this remains one metadata round trip.
    public Task<OneDriveItem> GetItemAsync(string driveId, string itemId, CancellationToken cancellationToken = default) =>
        GetItemAtAsync($"drives/{Segment(driveId)}/items/{Segment(itemId)}", cancellationToken);

    public async Task<OneDriveItem?> GetByPathAsync(string driveId, string folderId, string relativePath, CancellationToken cancellationToken = default)
    {
        try { return await GetItemAtAsync(ItemPath(driveId, folderId, relativePath) + $"?$select={ItemFields}", cancellationToken); }
        catch (OneDriveApiException error) when (error.StatusCode == 404) { return null; }
    }

    public async Task<OneDrivePage> ListChildrenPageAsync(string driveId, string folderId, string? nextLink = null,
        CancellationToken cancellationToken = default)
    {
        var url = nextLink ?? $"drives/{Segment(driveId)}/items/{Segment(folderId)}/children?$select={ItemFields}&$top=200";
        if (nextLink is not null)
        {
            var expectedPath = Uri.UnescapeDataString(GraphUri($"drives/{Segment(driveId)}/items/{Segment(folderId)}/children").AbsolutePath);
            if (!Uri.UnescapeDataString(GraphUri(nextLink).AbsolutePath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The saved OneDrive folder page belongs to another drive or folder.");
        }
        using var result = await GetJsonAsync(url, cancellationToken);
        return new(result.RootElement.GetProperty("value").EnumerateArray().Select(ParseItem).ToArray(),
            OneDriveAuthClient.Text(result.RootElement, "@odata.nextLink"));
    }

    public async Task<Stream> OpenReadAsync(string driveId, OneDriveItem saved, long offset, long length, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || length < 0 || offset > saved.Size || length > saved.Size - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length == 0) return Stream.Null;
        // Graph's primary stream is mutable. Saved-version metadata checks bracket cryptographic verification.
        var current = await GetItemAsync(driveId, saved.Id, cancellationToken);
        return await OpenValidatedReadAsync(driveId, saved, current, offset, length, cancellationToken).ConfigureAwait(false);
    }

    // The source adapter may carry its immediately preceding validation into this
    // read. Later validation and all destination/source-delete checks remain fresh.
    internal async Task<Stream> OpenValidatedReadAsync(string driveId, OneDriveItem saved, OneDriveItem current,
        long offset, long length, CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0 || offset > saved.Size || length > saved.Size - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        EnsureUnchanged(saved, current);
        if (length == 0) return Stream.Null;
        var download = current.DownloadUrl;
        if (download is null) throw new InvalidDataException("Microsoft did not provide a OneDrive download URL for this file.");
        await _downloads.EnterAsync(cancellationToken).ConfigureAwait(false);
        var admitted = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var response = await SendDownloadAsync(SecureContentUri(download), new(offset, checked(offset + length - 1)), cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
                {
                    response.Dispose();
                    current = await GetItemAsync(driveId, saved.Id, cancellationToken);
                    EnsureUnchanged(saved, current);
                    download = current.DownloadUrl ?? throw new InvalidDataException("The OneDrive download URL expired.");
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                { response.Dispose(); throw new TransferSourceChangedException("The OneDrive source changed during its transfer."); }
                if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (range?.From != offset || range.To != offset + length - 1 || range.Length != saved.Size)
                    { response.Dispose(); throw new InvalidDataException("OneDrive returned an unexpected byte range."); }
                }
                else if (offset != 0 || length != saved.Size || response.Content.Headers.ContentLength != saved.Size)
                { response.Dispose(); throw new InvalidDataException("OneDrive ignored a partial range request. The unfinished file must be retried."); }
                try
                {
                    var stream = new OwnedRangeStream(await response.Content.ReadAsStreamAsync(cancellationToken), response, length, _downloadLimit, _downloads);
                    admitted = false;
                    return stream;
                }
                catch { response.Dispose(); throw; }
            }
            throw new IOException("The OneDrive download URL could not be refreshed.");
        }
        finally { if (admitted) _downloads.Exit(); }
    }

    public async Task ValidateAsync(string driveId, OneDriveItem saved, CancellationToken cancellationToken = default) =>
        EnsureUnchanged(saved, await GetItemAsync(driveId, saved.Id, cancellationToken));

    public async Task DeleteUnchangedAsync(string driveId, OneDriveItem saved, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(driveId, saved, cancellationToken);
        using var result = await SendGraphAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, GraphUri($"drives/{Segment(driveId)}/items/{Segment(saved.Id)}"));
            request.Headers.TryAddWithoutValidation("If-Match", saved.ETag);
            return request;
        }, cancellationToken);
        if (result.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new TransferSourceChangedException("The OneDrive source changed; its verified destination is retained and its source was not deleted.");
        if (!result.IsSuccessStatusCode) await ThrowApiAsync(result, cancellationToken);
    }

    public async Task<OneDriveUploadSession> CreateUploadSessionAsync(string driveId, string parentId, string name,
        string conflictBehavior, string? replaceETag, CancellationToken cancellationToken = default)
    {
        // The name is already authoritative in the URL. Personal OneDrive can
        // reject the otherwise redundant item.name, so keep this body minimal.
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["item"] = new Dictionary<string, object>
            { ["@microsoft.graph.conflictBehavior"] = conflictBehavior } });
        using var response = await SendGraphAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, GraphUri(ItemPath(driveId, parentId, name) + "/createUploadSession"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (replaceETag is not null) request.Headers.TryAddWithoutValidation("If-Match", replaceETag);
            return request;
        }, cancellationToken, retryTransient: false);
        if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
        using var json = await ParseAsync(response, cancellationToken);
        return ParseSession(json.RootElement, null);
    }

    public async Task<OneDriveUploadSession?> GetUploadSessionAsync(string uploadUrl, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SecureContentUri(uploadUrl));
            using var response = await SendContentAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
            if (attempt < 4 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
            { await Task.Delay(RetryDelay(response, attempt), cancellationToken); continue; }
            if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
            using var json = await ParseAsync(response, cancellationToken);
            return ParseSession(json.RootElement, uploadUrl);
        }
    }

    /// <summary>Explicit provider-session cleanup. Normal pause/cancel retains the session for recovery.</summary>
    public async Task CancelUploadSessionAsync(string uploadUrl, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, SecureContentUri(uploadUrl));
        using var response = await SendContentAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return;
        if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
    }

    public async Task<(OneDriveItem? Item, OneDriveUploadSession? Session)> UploadFragmentAsync(string uploadUrl,
        ReadOnlyMemory<byte> bytes, long offset, long totalLength, CancellationToken cancellationToken = default,
        IProgress<TransferProgress>? progress = null)
    {
        await _uploads.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, SecureContentUri(uploadUrl));
            request.Content = new ProgressMemoryContent(bytes, _uploadLimit, sent => progress?.Report(new(offset + sent, totalLength)));
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Content.Headers.ContentRange = new(offset, offset + bytes.Length - 1, totalLength);
            // Preauthenticated upload URLs must never receive Microsoft bearer credentials.
            using var response = await SendContentAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
            using var json = await ParseAsync(response, cancellationToken);
            return response.StatusCode == HttpStatusCode.Accepted ? (null, ParseSession(json.RootElement, uploadUrl)) : (ParseItem(json.RootElement), null);
        }
        finally { _uploads.Exit(); }
    }

    public Task<OneDriveItem> PutEmptyAsync(string driveId, string folderId, string name, string? existingETag,
        CancellationToken cancellationToken = default) =>
        PutSmallAsync(driveId, folderId, name, ReadOnlyMemory<byte>.Empty, existingETag is null ? "fail" : "replace", existingETag, cancellationToken);

    public async Task<OneDriveItem> PutSmallAsync(string driveId, string folderId, string name, ReadOnlyMemory<byte> bytes,
        string conflictBehavior, string? existingETag, CancellationToken cancellationToken = default,
        IProgress<TransferProgress>? progress = null)
    {
        await _uploads.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await SendGraphAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, GraphUri(ItemPath(driveId, folderId, name) +
                    "/content?@microsoft.graph.conflictBehavior=" + Uri.EscapeDataString(conflictBehavior)));
                request.Content = new ProgressMemoryContent(bytes, _uploadLimit, sent => progress?.Report(new(sent, bytes.Length)));
                request.Content.Headers.ContentType = new("application/octet-stream");
                if (existingETag is not null) request.Headers.TryAddWithoutValidation("If-Match", existingETag);
                else request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                return request;
            }, cancellationToken, retryTransient: false);
            if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
            using var json = await ParseAsync(response, cancellationToken);
            return ParseItem(json.RootElement);
        }
        finally { _uploads.Exit(); }
    }

    public async Task<OneDriveItem> EnsureFolderAsync(string driveId, string parentId, string name, CancellationToken cancellationToken = default)
    {
        var existing = await GetByPathAsync(driveId, parentId, name, cancellationToken);
        if (existing is not null)
        {
            if (!existing.IsFolder) throw new TransferConflictException($"A destination file blocks the OneDrive folder '{name}'.");
            return existing;
        }
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" });
        using var response = await SendGraphAsync(() => new HttpRequestMessage(HttpMethod.Post,
            GraphUri($"drives/{Segment(driveId)}/items/{Segment(parentId)}/children"))
            { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken, retryTransient: false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            existing = await GetByPathAsync(driveId, parentId, name, cancellationToken);
            if (existing?.IsFolder == true) return existing;
        }
        if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
        using var json = await ParseAsync(response, cancellationToken);
        return ParseItem(json.RootElement);
    }

    private async Task<OneDriveItem> GetItemAtAsync(string url, CancellationToken cancellationToken)
    { using var json = await GetJsonAsync(url, cancellationToken); return ParseItem(json.RootElement); }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendGraphAsync(() => new(HttpMethod.Get, GraphUri(url)), cancellationToken);
        if (!response.IsSuccessStatusCode) await ThrowApiAsync(response, cancellationToken);
        return await ParseAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendGraphAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken, bool retryTransient = true)
    {
        var reauthenticated = false;
        for (var attempt = 0; ; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(cancellationToken);
            using var request = create();
            request.Headers.Authorization = new("Bearer", token);
            var response = await SendContentAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !reauthenticated)
            { response.Dispose(); _auth.InvalidateAccessToken(token); reauthenticated = true; continue; }
            if (retryTransient && attempt < 5 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
            {
                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    private async Task<HttpResponseMessage> SendContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        // Cap a stalled request rather than total transfer duration, so intentional
        // bandwidth caps and very large healthy uploads do not expire mid-file.
        using var inactivity = new TransferInactivity(cancellationToken, InactivityTimeout);
        if (request.Content is ProgressMemoryContent content) content.Inactivity = inactivity;
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, inactivity.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("The connection to OneDrive stopped responding. Saved transfer progress is retained."); }
        finally { if (request.Content is ProgressMemoryContent uploaded) uploaded.Inactivity = null; }
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(Uri url, RangeHeaderValue range, CancellationToken cancellationToken)
    {
        var redirects = 0;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = range;
            var response = await SendContentAsync(request, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || ++redirects > 5) throw new IOException("OneDrive returned too many download redirects.");
                url = SecureContentUri(new Uri(url, location).AbsoluteUri);
                continue;
            }
            if (attempt < 5 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
            {
                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            return response;
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter?.Date is { } date) return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * (1 << attempt)) + Random.Shared.Next(100, 400));
    }

    private static async Task ThrowApiAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var owned = response;
        var code = "request_failed";
        try
        {
            using var json = await ParseAsync(response, cancellationToken);
            if (json.RootElement.TryGetProperty("error", out var error)) code = OneDriveAuthClient.Text(error, "code") ?? code;
        }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        if (status == 412) throw new TransferSourceChangedException("The OneDrive file changed since this transfer was planned.");
        if (status == 409) throw new TransferConflictException("OneDrive reports a destination name conflict.");
        throw new OneDriveApiException(status, code, $"OneDrive request failed (HTTP {status}, {code}). Saved transfer progress is retained.")
            { RetryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } retryAt
                ? TimeSpan.FromTicks(Math.Max(0, (retryAt - DateTimeOffset.UtcNow).Ticks)) : null) };
    }

    private static Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ParseContentAsync(response.Content, cancellationToken);
    private static async Task<JsonDocument> ParseContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InactivityTimeout);
        try { return await JsonDocument.ParseAsync(await content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("OneDrive metadata stopped responding. Saved transfer progress is retained."); }
    }
    internal static OneDriveItem ParseItem(JsonElement item)
    {
        var hashes = item.TryGetProperty("file", out var file) && file.TryGetProperty("hashes", out var value) ? value : default;
        var modified = OneDriveAuthClient.Text(item, "lastModifiedDateTime");
        return new(item.GetProperty("id").GetString()!, OneDriveAuthClient.Text(item, "name") ?? "",
            item.TryGetProperty("size", out var size) ? size.GetInt64() : 0, OneDriveAuthClient.Text(item, "eTag") ?? "",
            OneDriveAuthClient.Text(item, "cTag"), item.TryGetProperty("folder", out _),
            DateTimeOffset.TryParse(modified, out var date) ? date : DateTimeOffset.UnixEpoch,
            hashes.ValueKind == JsonValueKind.Object ? OneDriveAuthClient.Text(hashes, "sha1Hash") : null,
            hashes.ValueKind == JsonValueKind.Object ? OneDriveAuthClient.Text(hashes, "sha256Hash") : null,
            OneDriveAuthClient.Text(item, "@microsoft.graph.downloadUrl"));
    }

    private static OneDriveUploadSession ParseSession(JsonElement value, string? uploadUrl)
    {
        var offset = 0L;
        if (value.TryGetProperty("nextExpectedRanges", out var ranges))
        {
            var starts = ranges.EnumerateArray().Select(range => long.Parse(range.GetString()!.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (starts.Length > 0) offset = starts.Min();
        }
        return new(OneDriveAuthClient.Text(value, "uploadUrl") ?? uploadUrl ?? throw new InvalidDataException("OneDrive omitted its upload session URL."),
            DateTimeOffset.Parse(value.GetProperty("expirationDateTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture), offset);
    }

    internal static void EnsureUnchanged(OneDriveItem saved, OneDriveItem current)
    {
        if (saved.Id != current.Id || saved.Size != current.Size || string.IsNullOrEmpty(saved.ETag) || saved.ETag != current.ETag || current.IsFolder)
            throw new TransferSourceChangedException("The OneDrive source changed since this transfer was discovered. Its source is retained.");
    }

    private static Uri GraphUri(string path)
    {
        var url = new Uri(new Uri(GraphRoot), path);
        if (url.Scheme != "https" || !url.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) || !url.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
            throw new InvalidDataException("OneDrive returned a pagination URL outside Microsoft Graph.");
        return url;
    }
    private static Uri SecureContentUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("OneDrive returned an invalid secure content URL.");
        return uri;
    }
    internal static string Segment(string value) => Uri.EscapeDataString(value);
    internal static string ItemPath(string driveId, string folderId, string path) =>
        $"drives/{Segment(driveId)}/items/{Segment(folderId)}:/{string.Join('/', path.Replace('\\', '/').Split('/').Select(Segment))}:";

    private sealed class ProgressMemoryContent(ReadOnlyMemory<byte> bytes, BandwidthLimiter limiter, Action<long> report) : HttpContent
    {
        public TransferInactivity? Inactivity { get; set; }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = Math.Min(64 * 1024, bytes.Length - offset);
                Inactivity?.Suspend();
                try { await limiter.WaitAsync(count, cancellationToken).ConfigureAwait(false); }
                finally { Inactivity?.Reset(); }
                await stream.WriteAsync(bytes.Slice(offset, count), cancellationToken).ConfigureAwait(false);
                Inactivity?.Reset();
                offset += count;
                report(offset);
            }
        }
    }

    private sealed class OwnedRangeStream(Stream source, HttpResponseMessage response, long length,
        BandwidthLimiter limiter, ConcurrencyGate downloads) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;
        private int _disposed;
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_remaining == 0 || buffer.Length == 0) return 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(InactivityTimeout);
            int read;
            try { read = await source.ReadAsync(buffer[..(int)Math.Min(64 * 1024, Math.Min(buffer.Length, _remaining))], timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new HttpRequestException("The download from OneDrive stopped responding. Saved transfer progress is retained."); }
            if (read == 0) throw new EndOfStreamException("The OneDrive response ended before its declared byte range.");
            await limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            { try { source.Dispose(); } finally { response.Dispose(); downloads.Exit(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            { try { await source.DisposeAsync(); } finally { response.Dispose(); downloads.Exit(); } }
            GC.SuppressFinalize(this);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

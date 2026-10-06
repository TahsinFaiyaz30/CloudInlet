using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudInlet.Core.Updates;

internal sealed record UpdateFeedResult(UpdateManifest? Manifest, string? ETag, bool NotModified, bool NotPublished);
internal sealed class UpdateNetworkException(string message, DateTimeOffset? retryUtc = null) : IOException(message)
{
    public DateTimeOffset? RetryUtc { get; } = retryUtc;
}

internal sealed class GitHubUpdateTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public GitHubUpdateTransport(HttpMessageHandler? handler, TimeProvider clock)
    {
        _clock = clock;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = Timeout,
            MaxConnectionsPerServer = 2
        }, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudInlet-Updater/1.0");
    }

    public async Task<UpdateFeedResult> FetchAsync(string? etag, CancellationToken token)
    {
        using var response = await SendAsync(UpdateManifestRules.FeedUri, etag, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new(null, etag, true, false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(null, null, false, true);
        CheckResponse(response);
        if (response.Content.Headers.ContentLength > 256 * 1024) throw new InvalidDataException("The release update manifest is too large.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await ReadAsync(input, buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > 256 * 1024) throw new InvalidDataException("The release update manifest is too large.");
            output.Write(buffer, 0, count);
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(output.ToArray(), UpdateManifestRules.JsonOptions)
                ?? throw new InvalidDataException("The release update manifest is empty.");
            return new(manifest, response.Headers.ETag?.ToString(), false, false);
        }
        catch (JsonException) { throw new InvalidDataException("The release update manifest cannot be read."); }
    }

    public async Task DownloadAsync(UpdateCandidate candidate, Stream destination, Action<long> progress, CancellationToken token)
    {
        using var response = await SendAsync(UpdateManifestRules.DownloadUri(candidate), null, token).ConfigureAwait(false);
        CheckResponse(response);
        if (response.Content.Headers.ContentLength is long length && length != candidate.Asset.Size)
            throw new InvalidDataException("The installer size does not match the release manifest.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var count = await ReadAsync(input, buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (total + count > candidate.Asset.Size) throw new InvalidDataException("The installer exceeds the release manifest size.");
            digest.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            total += count; progress(total);
        }
        if (total != candidate.Asset.Size || !CryptographicOperations.FixedTimeEquals(
            digest.GetHashAndReset(), Convert.FromHexString(candidate.Asset.Sha256)))
            throw new InvalidDataException("The downloaded installer failed its size or SHA-256 integrity check.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? etag, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            ValidateUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            using var timeout = new CancellationTokenSource(Timeout, _clock);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new UpdateNetworkException("The update server did not respond in time."); }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new UpdateNetworkException("The update server returned an invalid redirect.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new UpdateNetworkException("The update server returned too many redirects.");
    }

    private async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(Timeout, _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new UpdateNetworkException("The update download stopped responding."); }
    }

    private void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var now = _clock.GetUtcNow();
        DateTimeOffset? retry = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            retry = delta >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delta;
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
            response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch))
        {
            try { var date = DateTimeOffset.FromUnixTimeSeconds(epoch); if (retry is null || date > retry) retry = date; }
            catch (ArgumentOutOfRangeException) { }
        }
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            throw new UpdateNetworkException("GitHub temporarily limited update checks. CloudInlet will wait before trying again.",
                retry is null || retry < now.AddMinutes(1) ? now.AddMinutes(1) : retry);
        throw new UpdateNetworkException($"The update server returned HTTP {(int)response.StatusCode}.", retry);
    }

    private static void ValidateUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("An update redirect left the trusted HTTPS release servers.");
        if (uri.Host == "github.com")
        {
            if (uri.Query.Length != 0 || !IsReleasePath(uri.AbsolutePath))
                throw new InvalidDataException("An update URL does not belong to this repository's releases.");
            return;
        }
        if (uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" && uri.AbsolutePath.Length > 1)
            return; // GitHub's signed release-asset CDN, reached only by a trusted release redirect.
        throw new InvalidDataException("An update redirect left the trusted release servers.");
    }

    private static bool IsReleasePath(string path)
    {
        foreach (var feed in new[] { UpdateManifestRules.FeedUri, UpdateManifestRules.LegacyFeedUri })
        {
            if (path == feed.AbsolutePath) return true;
            var prefix = feed.AbsolutePath[..feed.AbsolutePath.IndexOf("/releases/", StringComparison.Ordinal)] + "/releases/download/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var legacy = feed == UpdateManifestRules.LegacyFeedUri;
            var pattern = legacy
                ? @"^v\d{1,5}\.\d{1,5}\.\d{1,5}/(updates-v1\.json|CloudBay-[A-Za-z0-9._-]+\.(exe|msi|zip))$"
                : @"^v\d{1,5}\.\d{1,5}\.\d{1,5}/(updates-v2\.json|CloudInlet-[A-Za-z0-9._-]+\.(exe|msi|zip))$";
            if (Regex.IsMatch(path[prefix.Length..], pattern, RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}

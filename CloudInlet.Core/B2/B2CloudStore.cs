using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Core.B2;

/// <summary>
/// B2 Native API v4 transport. One HTTP connection pool serves the lifetime of the store.
/// An upload URL is borrowed exclusively by one transfer and reused for subsequent small files.
/// </summary>
public sealed partial class B2CloudStore : ICloudStore
{
    private const int Attempts = 5;
    private const long MultipartThreshold = 64 * 1024 * 1024;
    private const long MaxPartSize = 5_000_000_000;
    private const long MaxLargeFileSize = 10_000_000_000_000;
    private readonly HttpClient _http;
    private readonly TimeSpan _metadataTimeout;
    private readonly TimeSpan _transferInactivityTimeout;
    private readonly SemaphoreSlim _authorizeLock = new(1, 1);
    private readonly ConcurrencyGate _uploads = new();
    private readonly ConcurrencyGate _uploadRequests = new();
    private readonly BandwidthLimiter _uploadLimit;
    private readonly BandwidthLimiter _downloadLimit;
    private readonly TransferBandwidthBudget _requestBudget;
    private readonly ConcurrentDictionary<string, ConcurrentBag<UploadSession>> _uploadSessions = new(StringComparer.Ordinal);
    private B2Credentials? _credentials;
    private Authorization? _authorization;
    private int _connections = 4;
    private int _listPageSize = 1_000;
    private bool _disposed;

    /// <summary>Safe operational notes, for example older large files without whole-file checksum metadata.</summary>
    public event EventHandler<string>? Diagnostic;

    /// <summary>Metadata page size, bounded to one B2 listing transaction per page.</summary>
    public int ListPageSize
    {
        get => Volatile.Read(ref _listPageSize);
        set
        {
            if (value is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _listPageSize, value);
        }
    }

    public B2CloudStore(HttpMessageHandler? handler = null, TimeSpan? metadataTimeout = null,
        TimeSpan? transferInactivityTimeout = null, TransferBandwidthBudget? bandwidthBudget = null)
    {
        bandwidthBudget ??= new TransferBandwidthBudget();
        _requestBudget = bandwidthBudget;
        _uploadLimit = bandwidthBudget.Uploads;
        _downloadLimit = bandwidthBudget.Downloads;
        _metadataTimeout = ValidateTimeout(metadataTimeout ?? TimeSpan.FromSeconds(60), nameof(metadataTimeout));
        _transferInactivityTimeout = ValidateTimeout(transferInactivityTimeout ?? TimeSpan.FromSeconds(90), nameof(transferInactivityTimeout));
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            PooledConnectionLifetime = TimeSpan.FromHours(1),
            MaxConnectionsPerServer = 32,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _http.DefaultRequestHeaders.ExpectContinue = false;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudInlet/2.0");
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    /// <summary>Zero bytes/second means unlimited. Limits are shared across all simultaneous transfers.</summary>
    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond, int connections) =>
        Configure(uploadBytesPerSecond, downloadBytesPerSecond, connections, connections);

    /// <summary>Set final upload and download request bounds without a temporary symmetric limit.</summary>
    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond, int uploadConnections, int downloadConnections)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(uploadBytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(downloadBytesPerSecond);
        if (uploadConnections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(uploadConnections));
        if (downloadConnections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(downloadConnections));
        _requestBudget.ConfigureRequestLimits(uploadConnections, downloadConnections);
        Volatile.Write(ref _connections, uploadConnections);
        _uploads.Configure(uploadConnections);
        _uploadRequests.Configure(uploadConnections);
        ConfigureDownloads(downloadConnections);
        _uploadLimit.Configure(uploadBytesPerSecond);
        _downloadLimit.Configure(downloadBytesPerSecond);
    }

    public async Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.KeyId) || string.IsNullOrWhiteSpace(credentials.ApplicationKey))
            throw new ArgumentException("An application key ID and application key are required.");
        await _authorizeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var auth = await AuthorizeAsync(credentials, cancellationToken).ConfigureAwait(false);
            _credentials = credentials;
            Volatile.Write(ref _authorization, auth);
            _uploadSessions.Clear();
            return auth.Account;
        }
        finally { _authorizeLock.Release(); }
    }

    public async Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        var auth = Current;
        if (!auth.Account.Capabilities.Contains("listBuckets", StringComparer.Ordinal))
        {
            if (auth.AllowedBuckets is { Count: > 0 } buckets && buckets.All(b => !string.IsNullOrEmpty(b.Name)))
                return buckets;
            throw new UnauthorizedAccessException("This application key must permit listing buckets or include its restricted bucket name.");
        }
        var body = new Dictionary<string, object?> { ["accountId"] = auth.Account.AccountId };
        if (auth.AllowedBuckets is { Count: 1 }) body["bucketId"] = auth.AllowedBuckets[0].Id;
        using var json = await ApiAsync("b2_list_buckets", body, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("buckets").EnumerateArray()
            .Select(b => new CloudBucket(RequiredString(b, "bucketId"), RequiredString(b, "bucketName")))
            .Where(b => auth.AllowedBuckets is null || auth.AllowedBuckets.Any(a => a.Id == b.Id)).ToArray();
    }

    // Historical callers retain hide markers; reconciliation uses the complete current snapshot below.
    public async IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateAccess(bucketId, prefix, "listFiles");
        string? lastKey = null;
        await foreach (var item in ListVersionsAsync(bucketId, prefix, cancellationToken).ConfigureAwait(false))
        {
            if (item.Action is not ("upload" or "hide")) continue;
            if (item.Key == lastKey) continue;
            lastKey = item.Key;
            yield return item;
        }
    }

    /// <summary>Lists current uploads without scanning every retained version. Hidden names are absent from the complete snapshot.</summary>
    public async IAsyncEnumerable<CloudObject> ListCurrentAsync(string bucketId, string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateAccess(bucketId, prefix, "listFiles");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? nextName = null;
        do
        {
            var pageSize = ListPageSize;
            // No delimiter: include nested objects and our zero-byte, trailing-slash directory markers.
            using var json = await ApiAsync("b2_list_file_names", new
            { bucketId, prefix, startFileName = nextName, maxFileCount = pageSize }, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > pageSize ||
                !root.TryGetProperty("nextFileName", out var cursor) ||
                cursor.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("Backblaze returned an incomplete current-file listing.");

            var name = cursor.ValueKind == JsonValueKind.Null ? null : cursor.GetString();
            if (name is not null && (name.Length == 0 || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                items.GetArrayLength() == 0 || !cursors.Add(name)))
                throw new InvalidDataException("Backblaze returned an invalid or repeated current-file listing cursor.");

            // Validate the entire page before exposing it, including the terminal cursor. The sync engine
            // also waits for every page before performing any reconciliation or inferring absence.
            var page = new List<CloudObject>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || OptionalString(item, "action") != "upload" ||
                    !HasNonnegativeInteger(item, "contentLength") || !HasNonnegativeInteger(item, "uploadTimestamp") ||
                    item.TryGetProperty("fileInfo", out var info) && info.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Backblaze returned invalid current-file metadata.");
                var file = ParseObject(item);
                if (file.FileId.Length == 0 || file.Key.Length == 0 || !file.Key.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Backblaze returned a file outside the requested prefix or without its identity.");
                if (!names.Add(file.Key))
                    throw new InvalidDataException("Backblaze returned a duplicate name in the current-file listing.");
                page.Add(file);
            }
            foreach (var file in page) yield return file;
            nextName = name;
        } while (nextName is not null);
    }

    private static bool HasNonnegativeInteger(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) && number >= 0,
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var number) && number >= 0,
            _ => false
        };
    }

    public async Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1,
        DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateAccess(bucketId, key, "writeFiles");
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!source.CanRead) throw new ArgumentException("The upload source must be readable.", nameof(source));
        if (!IsSha1(sha1)) throw new ArgumentException("A 40 digit SHA1 checksum is required.", nameof(sha1));
        await _uploads.EnterAsync(cancellationToken).ConfigureAwait(false);
        MemoryStream? staged = null;
        try
        {
            if (!source.CanSeek)
            {
                // Compatibility streams have no replay capability. Bound their RAM replay buffer;
                // cloud adapters use independent version-checked range reads instead of staging.
                if (length > 4 * 1024 * 1024)
                    throw new ArgumentException("Non-seekable uploads larger than 4 MiB require a replayable transfer source. CloudInlet never stages upload payloads on disk.", nameof(source));
                staged = new MemoryStream(checked((int)length));
                await CopyExactlyAsync(source, staged, length, cancellationToken).ConfigureAwait(false);
                staged.Position = 0;
                source = staged;
            }
            if (source.Length - source.Position < length)
                throw new EndOfStreamException("The upload source is shorter than its declared length.");
            var start = source.Position;
            return length >= MultipartThreshold
                ? await UploadLargeAsync(bucketId, key, source, start, length, sha1, modifiedUtc, progress, cancellationToken,
                    durableSource: staged is null).ConfigureAwait(false)
                : await UploadSmallAsync(bucketId, key, source, start, length, sha1, modifiedUtc, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (staged is not null) await staged.DisposeAsync().ConfigureAwait(false);
            _uploads.Exit();
        }
    }

    private async Task<CloudObject> UploadSmallAsync(string bucketId, string key, Stream source, long start, long length,
        string sha1, DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        var accountId = Current.Account.AccountId;
        var sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
            { bucketId, key, sha1.ToLowerInvariant(), length.ToString(CultureInfo.InvariantCulture),
              modifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), start.ToString(CultureInfo.InvariantCulture) })))).ToLowerInvariant();
        var operationId = sourceId;
        var memoryId = accountId + "|" + operationId;
        using var operationLock = await LockOperationAsync(memoryId, token).ConfigureAwait(false);
        var journal = _transferIntents;
        var intentPath = journal?.PathFor(accountId, operationId);
        // Healthy files need no disk lock or intent flush; only a previously uncertain
        // operation uses its cross-process checkpoint while querying the immutable receipt.
        await using var intentLock = journal is not null && File.Exists(intentPath)
            ? await journal.LockAsync(intentPath!, token).ConfigureAwait(false) : null;
        var expected = new B2TransferIntentJournal.Entry(1, "upload", accountId, bucketId, key, operationId, sourceId,
            length, sha1.ToLowerInvariant(), modifiedUtc.ToUnixTimeMilliseconds());
        var intent = journal?.Read(intentPath!) ?? (_volatileIntents.TryGetValue(memoryId, out var memory) ? memory : null) ?? expected;
        if (!SameIntent(intent, expected)) throw new InvalidDataException("The retained upload intent does not match this source.");
        void Persist(bool pending, bool durable = false)
        {
            intent = intent with { RequestPending = pending };
            _volatileIntents[memoryId] = intent;
            if (durable && journal is not null) journal.Write(intentPath!, intent);
        }
        void Forget() { _volatileIntents.TryRemove(memoryId, out _); journal?.Remove(intentPath!); }
        CloudObject Complete(CloudObject file, bool baseline)
        {
            source.Position = checked(start + length); Forget();
            progress?.Report(new(length, length) { IsBaseline = baseline });
            return file;
        }
        if (intent.RequestPending)
        {
            var receipt = await FindOperationReceiptAsync(intent, token).ConfigureAwait(false);
            if (receipt is not null) return Complete(receipt, baseline: true);
            throw UnknownOutcome("upload");
        }
        await _uploadRequests.EnterAsync(token).ConfigureAwait(false);
        using var sourceLock = new SemaphoreSlim(1, 1);
        var pool = _uploadSessions.GetOrAdd(bucketId, _ => new());
        UploadSession? session = null;
        var reusable = false;
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (session is null)
                {
                    while (pool.TryTake(out var cached))
                        if (cached.ExpiresUtc > DateTimeOffset.UtcNow) { session = cached; break; }
                    session ??= await GetUploadSessionAsync(bucketId, null, token).ConfigureAwait(false);
                }
                long bytes = 0;
                progress?.Report(new(0, length) { IsBaseline = true });
                using var request = UploadRequest(session, source, sourceLock, start, length, sha1,
                    n => { var sent = Interlocked.Add(ref bytes, n); progress?.Report(new(sent, length)); }, token);
                request.Headers.TryAddWithoutValidation("X-Bz-File-Name", EncodeName(key));
                request.Headers.TryAddWithoutValidation("X-Bz-Info-src_last_modified_millis", modifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation("X-Bz-Info-cloudbay_upload_id", operationId);
                request.Headers.TryAddWithoutValidation("X-Bz-Info-cloudbay_source_id", sourceId);
                // Healthy files keep a memory-only intent. Persist uncertainty only if an
                // attempt exits without confirmation; successful pooled uploads need no fsync.
                Persist(pending: true);
                HttpStatusCode? rejectedStatus = null;
                HttpStatusCode? observedStatus = null;
                var acknowledgmentRejected = false;
                try
                {
                    using var response = await SendAsync(request, token).ConfigureAwait(false);
                    observedStatus = response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = await ReadDocumentAsync(response, token).ConfigureAwait(false);
                        if (json.RootElement.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("Backblaze returned incomplete upload acknowledgment metadata.");
                        var file = ParseObject(json.RootElement);
                        // ParseObject also normalizes legitimate restore/copy responses.
                        // A direct upload must acknowledge its own operation explicitly.
                        if (RequiredString(json.RootElement, "action") != "upload" || file.Key != key || file.Size != length ||
                            !sha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase) ||
                            OptionalString(json.RootElement, "bucketId") is { } returnedBucket && returnedBucket != bucketId)
                        {
                            acknowledgmentRejected = true;
                            throw new InvalidDataException("Backblaze returned an upload checksum or length that does not match the source.");
                        }
                        reusable = true;
                        return Complete(file, baseline: false);
                    }
                    // An explicit authorization/input rejection proves the body was not
                    // committed. A fully sent 5xx/408 response can still be ambiguous.
                    if (IsDefiniteTransferUploadRejection(response.StatusCode))
                    {
                        rejectedStatus = response.StatusCode;
                        // The rejection is authoritative even if reading its body
                        // fails or the caller cancels before error parsing finishes.
                        Persist(pending: false); Forget();
                    }
                    var error = await ReadErrorAsync(response, token).ConfigureAwait(false);
                    response.Dispose(); // The parsed response no longer owns an active payload request.
                    // A failing upload endpoint is retired; never share it with the next file.
                    session = null;
                    if (rejectedStatus is null && bytes >= length)
                    {
                        Persist(pending: true, durable: true);
                        var receipt = await FindOperationReceiptAsync(intent, token).ConfigureAwait(false);
                        if (receipt is not null) return Complete(receipt, baseline: false);
                        throw UnknownOutcome("upload", error);
                    }
                    Persist(pending: false);
                    if (!IsUploadRetry(response.StatusCode, error.Code) || attempt == Attempts - 1) { Forget(); throw error; }
                    await BackoffAsync(response, attempt, token).ConfigureAwait(false);
                }
                catch (InvalidDataException) when (acknowledgmentRejected)
                {
                    // A readable acknowledgment for different content/identity is
                    // an integrity failure. Retain its intent and stop for review.
                    Persist(pending: true, durable: true);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // A small file is committed only after its complete declared body.
                    // Pausing an incomplete request should allow that file to start again;
                    // a fully sent body still needs its receipt before any replay.
                    if (rejectedStatus is not null || bytes < length) { Persist(pending: false); Forget(); }
                    else Persist(pending: true, durable: true);
                    throw;
                }
                catch (Exception ex) when (ex is not UnknownTransferOutcomeException &&
                    (ex is IOException and not B2RequestException || ex is HttpRequestException or InvalidDataException or JsonException ||
                     ex is InvalidOperationException && observedStatus is not null))
                {
                    session = null;
                    if (rejectedStatus is { } status)
                    {
                        Forget();
                        // Only the known status survives a failed error body;
                        // provider text and transport exceptions are not retained.
                        var rejection = new B2RequestException(status, "request_failed");
                        if (!IsUploadRetry(status, rejection.Code) || attempt == Attempts - 1) throw rejection;
                        await BackoffAsync(null, attempt, token).ConfigureAwait(false);
                        continue;
                    }
                    if (bytes >= length)
                    {
                        Persist(pending: true, durable: true);
                        var receipt = await FindOperationReceiptAsync(intent, token).ConfigureAwait(false);
                        if (receipt is not null) return Complete(receipt, baseline: false);
                        throw UnknownOutcome("upload");
                    }
                    Persist(pending: false);
                    if (attempt == Attempts - 1) { Forget(); throw; }
                    await BackoffAsync(null, attempt, token).ConfigureAwait(false);
                }
            }
            throw new IOException("The upload could not complete after bounded retries.");
        }
        finally
        {
            // Caller cancellation can arrive after the body or response was transmitted.
            // Retain only unresolved attempts; never add a disk write to successful files.
            try
            {
                if (_volatileIntents.TryGetValue(memoryId, out var unresolved) && unresolved.RequestPending && journal is not null)
                    journal.Write(intentPath!, unresolved);
            }
            finally
            {
                if (reusable && session is not null) pool.Add(session);
                _uploadRequests.Exit();
            }
        }
    }

    public async Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default)
    {
        ValidateAccess(bucketId, key, "writeFiles");
        ValidateKey(key);
        using var _ = await ApiAsync("b2_hide_file", new { bucketId, fileName = key }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default)
    {
        ValidateAccess(bucketId, key, "listFiles");
        var versions = new List<CloudObject>();
        await foreach (var item in ListVersionsAsync(bucketId, key, cancellationToken).ConfigureAwait(false))
        {
            if (!item.Key.Equals(key, StringComparison.Ordinal)) break;
            if (item.Action is "upload" or "hide") versions.Add(item);
        }
        return versions;
    }

    public async Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ValidateAccess(bucketId, version.Key, "writeFiles");
        ValidateCapability("readFiles");
        if (version.Action != "upload") throw new ArgumentException("A hide marker cannot be restored as file content.", nameof(version));
        if (version.Size < 0 || version.Size > MaxLargeFileSize) throw new ArgumentOutOfRangeException(nameof(version), "This version exceeds B2's large-file limit.");
        if (version.Size < MaxPartSize)
        {
            using var json = await ApiAsync("b2_copy_file", new
            {
                sourceFileId = version.FileId, fileName = version.Key, destinationBucketId = bucketId, metadataDirective = "COPY"
            }, cancellationToken, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
            return ParseObject(json.RootElement);
        }
        // Large restores stay in the service: copy parts without downloading the entire file locally.
        var partSize = Math.Min(Math.Max(Math.Max(Current.RecommendedPartSize, (version.Size + 9_999) / 10_000), Current.MinimumPartSize), version.Size / 2);
        if (partSize > MaxPartSize) throw new ArgumentOutOfRangeException(nameof(version));
        var partCount = checked((int)((version.Size + partSize - 1) / partSize));
        string? fileId = null;
        var completed = false;
        var finishUncertain = false;
        try
        {
            using (var start = await ApiAsync("b2_start_large_file", new
            {
                bucketId, fileName = version.Key, contentType = "b2/x-auto",
                fileInfo = IsSha1(version.Sha1)
                    ? new Dictionary<string, string> { ["large_file_sha1"] = version.Sha1!, ["src_last_modified_millis"] = version.ModifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) }
                    : new Dictionary<string, string> { ["src_last_modified_millis"] = version.ModifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) }
            }, cancellationToken, retryNetwork: false, retryTransient: false).ConfigureAwait(false)) fileId = RequiredString(start.RootElement, "fileId");
            var hashes = new string[partCount];
            await Parallel.ForEachAsync(Enumerable.Range(0, partCount), new ParallelOptions
            { MaxDegreeOfParallelism = Volatile.Read(ref _connections), CancellationToken = cancellationToken }, async (i, ct) =>
            {
                var from = i * partSize;
                var to = Math.Min(version.Size, from + partSize) - 1;
                using var part = await ApiAsync("b2_copy_part", new
                { sourceFileId = version.FileId, largeFileId = fileId, partNumber = i + 1, range = $"bytes={from}-{to}" }, ct).ConfigureAwait(false);
                hashes[i] = RequiredString(part.RootElement, "contentSha1");
                if (!IsSha1(hashes[i])) throw new InvalidDataException("Backblaze returned an invalid copied part checksum.");
            }).ConfigureAwait(false);
            CloudObject restored;
            try { restored = await FinishAsync(fileId, hashes, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException ||
                error is IOException and not B2RequestException || error is B2RequestException request && IsTransient(request.StatusCode))
            {
                finishUncertain = true;
                ReportDiagnostic("The restored B2 version could not be confirmed after finishing. Its file ID was retained in B2; no cancellation was sent for a possibly completed version.");
                throw;
            }
            if (restored.Size != version.Size) throw new IOException("The restored file size does not match the selected version.");
            completed = true;
            return restored;
        }
        finally
        {
            if (fileId is not null && !completed && !finishUncertain)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { using var _ = await ApiAsync("b2_cancel_large_file", new { fileId }, cleanup.Token).ConfigureAwait(false); }
                catch (B2RequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                catch { ReportDiagnostic("An unfinished multipart restore could not be cleaned up. Check B2 unfinished uploads when the connection is restored."); }
            }
        }
    }

    private async IAsyncEnumerable<CloudObject> ListVersionsAsync(string bucketId, string prefix,
        [EnumeratorCancellation] CancellationToken token)
    {
        string? nextName = null, nextId = null;
        do
        {
            using var json = await ApiAsync("b2_list_file_versions", new
            { bucketId, prefix, startFileName = nextName, startFileId = nextId, maxFileCount = ListPageSize }, token).ConfigureAwait(false);
            foreach (var item in json.RootElement.GetProperty("files").EnumerateArray())
            {
                var file = ParseObject(item);
                if (!file.Key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Backblaze returned a file outside the requested prefix.");
                yield return file;
            }
            var name = OptionalString(json.RootElement, "nextFileName");
            var id = OptionalString(json.RootElement, "nextFileId");
            if (name is not null && name == nextName && id == nextId) throw new InvalidDataException("Backblaze returned a repeated listing cursor.");
            nextName = name; nextId = id;
        } while (nextName is not null);
    }

    private async Task<Authorization> AuthorizeAsync(B2Credentials credentials, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_metadataTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.backblazeb2.com/b2api/v4/b2_authorize_account");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.KeyId}:{credentials.ApplicationKey}")));
            try
            {
                using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadErrorAsync(response, timeout.Token).ConfigureAwait(false);
                    if (!IsTransient(response.StatusCode) || attempt == Attempts - 1) throw error;
                    await BackoffAsync(response, attempt, token).ConfigureAwait(false);
                    continue;
                }
                using var json = await ReadDocumentAsync(response, timeout.Token).ConfigureAwait(false);
                var root = json.RootElement;
                var storage = root.GetProperty("apiInfo").GetProperty("storageApi");
                var allowed = storage.GetProperty("allowed");
                var capabilities = allowed.GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()!).ToArray();
                IReadOnlyList<CloudBucket>? buckets = null;
                if (allowed.TryGetProperty("buckets", out var list) && list.ValueKind == JsonValueKind.Array)
                    buckets = list.EnumerateArray().Select(b => new CloudBucket(RequiredString(b, "id"), OptionalString(b, "name") ?? "")).ToArray();
                var account = new CloudAccount(RequiredString(root, "accountId"), SecureUrl(RequiredString(storage, "apiUrl")),
                    SecureUrl(RequiredString(storage, "downloadUrl")), capabilities,
                    buckets is { Count: 1 } ? buckets[0].Id : null, OptionalString(allowed, "namePrefix"));
                return new(account, RequiredString(root, "authorizationToken"), buckets,
                    Math.Max(5_000_000, LongValue(storage, "absoluteMinimumPartSize")),
                    Math.Max(5_000_000, LongValue(storage, "recommendedPartSize")));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < Attempts - 1)
            { await BackoffAsync(null, attempt, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new HttpRequestException("Backblaze B2 authorization timed out. Changes will retry automatically."); }
            catch (Exception ex) when ((ex is IOException and not B2RequestException || ex is HttpRequestException) && attempt < Attempts - 1)
            { await BackoffAsync(null, attempt, token).ConfigureAwait(false); }
        }
    }

    private async Task ReauthorizeAsync(Authorization rejected, CancellationToken token)
    {
        await _authorizeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // If another worker already refreshed this token, use that result instead of stampeding.
            if (ReferenceEquals(Volatile.Read(ref _authorization), rejected))
            {
                var refreshed = await AuthorizeAsync(_credentials ?? throw new InvalidOperationException("Connect first."), token).ConfigureAwait(false);
                Volatile.Write(ref _authorization, refreshed);
                _uploadSessions.Clear();
            }
        }
        finally { _authorizeLock.Release(); }
    }

    private async Task<JsonDocument> ApiAsync(string operation, object body, CancellationToken token,
        bool retryNetwork = true, bool retryTransient = true)
    {
        // Serialize once; rebuilding HttpRequestMessage and content is essential for safe replay.
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        for (var attempt = 0; ; attempt++)
        {
            var auth = Current;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_metadataTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{auth.Account.ApiUrl}/b2api/v4/{operation}");
            request.Headers.TryAddWithoutValidation("Authorization", auth.Token);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            try
            {
                using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return await ReadDocumentAsync(response, timeout.Token).ConfigureAwait(false);
                var error = await ReadErrorAsync(response, timeout.Token).ConfigureAwait(false);
                if (IsExpired(error) && attempt < Attempts - 1)
                { await ReauthorizeAsync(auth, token).ConfigureAwait(false); continue; }
                if (!IsTransient(response.StatusCode) || !retryTransient || attempt == Attempts - 1) throw error;
                await BackoffAsync(response, attempt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && retryNetwork && attempt < Attempts - 1)
            { await BackoffAsync(null, attempt, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new HttpRequestException("Backblaze B2 metadata timed out. Changes will retry automatically."); }
            catch (Exception ex) when ((ex is IOException and not B2RequestException || ex is HttpRequestException) && retryNetwork && attempt < Attempts - 1)
            { await BackoffAsync(null, attempt, token).ConfigureAwait(false); }
        }
    }

    private async Task<UploadSession> GetUploadSessionAsync(string bucketId, string? fileId, CancellationToken token)
    {
        using var json = await ApiAsync(fileId is null ? "b2_get_upload_url" : "b2_get_upload_part_url",
            fileId is null ? new { bucketId } : (object)new { fileId }, token).ConfigureAwait(false);
        return new(SecureUrl(RequiredString(json.RootElement, "uploadUrl")), RequiredString(json.RootElement, "authorizationToken"), DateTimeOffset.UtcNow.AddHours(23));
    }

    private async Task<CloudObject> FinishAsync(string fileId, string[] hashes, CancellationToken token)
    {
        try
        {
            using var finish = await ApiAsync("b2_finish_large_file", new { fileId, partSha1Array = hashes }, token).ConfigureAwait(false);
            return ParseObject(finish.RootElement);
        }
        catch (B2RequestException original) when (original.StatusCode == HttpStatusCode.BadRequest)
        {
            // A lost successful finish response makes its retry return 400. B2 documents checking the
            // known file ID rather than creating another upload/version or canceling completed content.
            CloudObject file;
            try
            {
                using var info = await ApiAsync("b2_get_file_info", new { fileId }, token).ConfigureAwait(false);
                file = ParseObject(info.RootElement);
            }
            catch (B2RequestException) { throw original; }
            if (file.Action != "upload") throw;
            return file;
        }
    }

    private HttpRequestMessage UploadRequest(UploadSession session, Stream source, SemaphoreSlim sourceLock, long start,
        long length, string sha1, Action<int>? progress, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, session.Url);
        request.Headers.ExpectContinue = false;
        request.Headers.TryAddWithoutValidation("Authorization", session.Token);
        request.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", sha1.ToLowerInvariant());
        request.Content = new SegmentContent(source, sourceLock, start, length, sha1, _uploadLimit, progress, token);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("b2/x-auto");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, bool payloadDownload = false)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        TransferInactivity? inactivity = null;
        long? sentAt = null;
        IDisposable? payloadLease = null;
        try
        {
            // Cloud authorization/range opening and the first block must be ready
            // before sending B2 headers, rather than leaving its server waiting.
            if (request.Content is ReplayableContent prepared) await prepared.PrepareAsync(token).ConfigureAwait(false);
            // Source preparation can wait for a shared download slot. Never hold
            // upload admission while waiting for that source or its first block.
            if (request.Content is SegmentContent or ReplayableContent)
                payloadLease = await _requestBudget.EnterUploadAsync(token).ConfigureAwait(false);
            else if (payloadDownload)
                payloadLease = await _requestBudget.EnterDownloadAsync(token).ConfigureAwait(false);
            inactivity = new TransferInactivity(token,
                request.Content is SegmentContent or ReplayableContent ? _transferInactivityTimeout : _metadataTimeout);
            if (request.Content is SegmentContent segment) segment.Inactivity = inactivity;
            if (request.Content is ReplayableContent replayable) replayable.Inactivity = inactivity;
            sentAt = Stopwatch.GetTimestamp();
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, inactivity.Token).ConfigureAwait(false);
            if (payloadLease is not null)
            {
                TransferBandwidthBudget.AttachResponseLease(response, payloadLease);
                payloadLease = null;
            }
            return response;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw SafeTransportException(error, "The connection to Backblaze B2 timed out. Changes will retry automatically."); }
        catch (HttpRequestException) when (request.Content is ReplayableContent { SourceError: TransferSourceChangedException })
        { throw ((ReplayableContent)request.Content).SourceError!; }
        catch (HttpRequestException) when (request.Content is ReplayableContent { SourceError: InvalidDataException })
        { throw ((ReplayableContent)request.Content).SourceError!; }
        catch (HttpRequestException error)
        { throw SafeTransportException(error, "The connection to Backblaze B2 failed. Check your network connection."); }
        finally
        {
            payloadLease?.Dispose();
            if (request.Content is SegmentContent uploaded) uploaded.Inactivity = null;
            if (request.Content is ReplayableContent replayed)
            {
                if (sentAt is { } started) replayed.SendMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                replayed.Inactivity = null;
                await replayed.StopPreparationAsync().ConfigureAwait(false);
            }
            inactivity?.Dispose();
        }
    }

    private const string TransportFailureKey = "CloudBay.B2.TransportFailure";
    private sealed record TransportFailure(HttpRequestError RequestError, string InnerTypes, string? SocketError,
        string HResult, string? InnerHResult);

    private static HttpRequestException SafeTransportException(Exception original, string message)
    {
        var classification = ClassifyTransportFailure(original);
        // Preserve the framework's bounded error category while removing messages,
        // URLs, credentials and arbitrary inner exceptions from public diagnostics.
        var safe = new HttpRequestException(classification.RequestError, message, inner: null,
            statusCode: (original as HttpRequestException)?.StatusCode);
        safe.Data[TransportFailureKey] = classification;
        return safe;
    }

    private static TransportFailure ClassifyTransportFailure(Exception error)
    {
        if (error.Data[TransportFailureKey] is TransportFailure saved) return saved;
        var category = error is HttpRequestException request && Enum.IsDefined(request.HttpRequestError)
            ? request.HttpRequestError : HttpRequestError.Unknown;
        var innerTypes = new List<string>(4);
        string? socketError = null, innerHResult = null;
        for (var current = error; current is not null && innerTypes.Count < 4; current = current.InnerException)
        {
            if (current is SocketException socket)
                socketError ??= Enum.IsDefined(socket.SocketErrorCode) ? socket.SocketErrorCode.ToString() : "Unknown";
            if (current == error) continue;
            innerTypes.Add(current switch
            {
                SocketException => "SocketException",
                HttpRequestException => "HttpRequestException",
                AuthenticationException => "AuthenticationException",
                InvalidDataException => "InvalidDataException",
                IOException => "IOException",
                OperationCanceledException => "OperationCanceledException",
                TimeoutException => "TimeoutException",
                ObjectDisposedException => "ObjectDisposedException",
                _ => "Other"
            });
            innerHResult = unchecked((uint)current.HResult).ToString("x8", CultureInfo.InvariantCulture);
        }
        return new(category, string.Join(">", innerTypes), socketError,
            unchecked((uint)error.HResult).ToString("x8", CultureInfo.InvariantCulture), innerHResult);
    }

    private async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_metadataTimeout);
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > 16 * 1024 * 1024) throw new IOException("The B2 API response exceeded the metadata size limit.");
                output.Write(buffer, 0, count);
            }
            try { return JsonDocument.Parse(output.ToArray()); }
            catch (JsonException) { throw new InvalidDataException("Backblaze returned an invalid API response."); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("Backblaze B2 metadata timed out. Changes will retry automatically."); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<B2RequestException> ReadErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Drain the entire response for HTTP connection reuse, while retaining only a bounded prefix.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_metadataTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var sample = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                var keep = (int)Math.Min(count, 8 * 1024 - sample.Length);
                if (keep > 0) sample.Write(buffer, 0, keep);
            }
            var code = "request_failed";
            try
            {
                using var json = JsonDocument.Parse(sample.ToArray());
                var candidate = json.RootElement.ValueKind == JsonValueKind.Object ? OptionalString(json.RootElement, "code") : null;
                // No server text (including an unknown code) may accidentally reveal an echoed credential.
                if (candidate is "bad_auth_token" or "expired_auth_token" or "unauthorized" or "unsupported" or
                    "bad_request" or "bad_bucket_id" or "invalid_bucket_id" or "not_found" or "file_not_present" or
                    "service_unavailable" or "too_many_requests" or "storage_cap_exceeded" or "transaction_cap_exceeded" or
                    "download_cap_exceeded" or "access_denied" or "range_not_satisfiable" or "sha1_mismatch" or
                    "bad_sha1" or "bad_part_number" or "out_of_range" or "already_hidden" or "source_too_large" or
                    "auth_token_limit" or "upload_token_used_concurrently" or "request_timeout" or "cap_exceeded" or "length_required" or
                    "unsupported_media_type" or "method_not_allowed") code = candidate;
            }
            catch (JsonException) { }
            return new(response.StatusCode, code);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("The Backblaze B2 error response timed out. Changes will retry automatically."); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async ValueTask<int> ReadTransferAsync(Stream source, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_transferInactivityTimeout);
        try { return await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("The download from Backblaze B2 stopped responding. Changes will retry automatically."); }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name, "A positive, finite timeout is required.");
        return timeout;
    }

    private static async Task BackoffAsync(HttpResponseMessage? response, int attempt, CancellationToken token)
    {
        var retry = response?.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble() * .5);
        // No transfer can be parked indefinitely by an untrusted Retry-After header.
        if (delay > TimeSpan.FromMinutes(2)) delay = TimeSpan.FromMinutes(2);
        await Task.Delay(delay, token).ConfigureAwait(false);
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static bool IsUploadRetry(HttpStatusCode status, string code) => IsTransient(status) || status == HttpStatusCode.Unauthorized || code is "auth_token_limit" or "upload_token_used_concurrently";
    private static bool IsExpired(B2RequestException error) => error.StatusCode == HttpStatusCode.Unauthorized && error.Code is "bad_auth_token" or "expired_auth_token";

    private static void ValidateDownloadResponse(HttpResponseMessage response, CloudObject file, long offset, long count, bool full)
    {
        if (response.Content.Headers.ContentLength != count) throw new InvalidDataException("The download Content-Length does not match the requested range.");
        if (full)
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("A full-file download returned an unexpected HTTP status.");
        }
        else
        {
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                range.From != offset || range.To != offset + count - 1 || range.Length != file.Size)
                throw new InvalidDataException("Backblaze did not honor the exact requested byte range.");
        }
        var id = Header(response, "X-Bz-File-Id");
        if (id is not null && id != file.FileId) throw new InvalidDataException("Backblaze returned a different file version.");
        var remoteSha = Header(response, "X-Bz-Content-Sha1");
        if (!IsSha1(remoteSha)) remoteSha = Header(response, "X-Bz-Info-large_file_sha1");
        if (IsSha1(file.Sha1) && IsSha1(remoteSha) && !file.Sha1!.Equals(remoteSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The remote checksum changed for the selected file version.");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private Authorization Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Volatile.Read(ref _authorization) ?? throw new InvalidOperationException("Connect to Backblaze B2 first.");
        }
    }

    private void ValidateCapability(string capability)
    {
        if (!Current.Account.Capabilities.Contains(capability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException($"The application key needs the {capability} capability.");
    }

    private void ValidateAccess(string bucketId, string key, string capability)
    {
        ValidateCapability(capability);
        if (string.IsNullOrWhiteSpace(bucketId)) throw new ArgumentException("A bucket ID is required.", nameof(bucketId));
        if (Current.AllowedBuckets is { } buckets && !buckets.Any(b => b.Id == bucketId))
            throw new UnauthorizedAccessException("This application key cannot access the selected bucket.");
        ValidatePrefix(key);
    }

    private void ValidatePrefix(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var prefix = Current.Account.AllowedNamePrefix;
        if (prefix is not null && !key.StartsWith(prefix, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The selected path is outside this application key's allowed prefix.");
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) > 1024 || key.Any(c => c < 32 || c == 127))
            throw new ArgumentException("B2 file names must contain 1 to 1024 UTF-8 bytes and no control characters.", nameof(key));
    }

    private static string EncodeName(string key) => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    private static bool IsSha1(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static string SecureUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Backblaze returned an invalid secure service endpoint.");
        return value.TrimEnd('/');
    }

    private static CloudObject ParseObject(JsonElement item)
    {
        var hash = OptionalString(item, "contentSha1");
        if (!IsSha1(hash) && item.TryGetProperty("fileInfo", out var info)) hash = OptionalString(info, "large_file_sha1");
        long modified = LongValue(item, "uploadTimestamp");
        if (item.TryGetProperty("fileInfo", out var metadata) &&
            long.TryParse(OptionalString(metadata, "src_last_modified_millis"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis)) modified = millis;
        DateTimeOffset timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(modified); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Backblaze returned an invalid file timestamp."); }
        var action = OptionalString(item, "action") ?? "upload";
        // Copy acknowledgments use "copy" on the live Native API; the resulting version is uploaded content.
        if (action == "copy") action = "upload";
        return new(RequiredString(item, "fileId"), RequiredString(item, "fileName"), LongValue(item, "contentLength"),
            IsSha1(hash) ? hash!.ToLowerInvariant() : null, timestamp, action);
    }

    private static string RequiredString(JsonElement item, string name) => OptionalString(item, name)
        ?? throw new InvalidDataException("Backblaze returned incomplete API metadata.");
    private static string? OptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long LongValue(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return 0;
    }

    private static async Task<string> HashSegmentAsync(Stream source, SemaphoreSlim sourceLock, long start, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long position = 0;
        try
        {
            while (position < length)
            {
                int count;
                await sourceLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    source.Position = checked(start + position);
                    count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - position)), token).ConfigureAwait(false);
                }
                finally { sourceLock.Release(); }
                if (count == 0) throw new EndOfStreamException("The upload source ended before its declared length.");
                hash.AppendData(buffer, 0, count);
                position += count;
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (length > 0)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The upload source ended before its declared length.");
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                length -= count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void ReportDiagnostic(string message)
    {
        if (Diagnostic is not { } handlers) return;
        foreach (EventHandler<string> handler in handlers.GetInvocationList())
        {
            try { handler(this, message); }
            catch { /* An observer must not change the outcome of a transfer or its cleanup. */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _credentials = null;
        Volatile.Write(ref _authorization, null);
        _uploadSessions.Clear();
        _http.Dispose();
    }

    private sealed record UploadSession(string Url, string Token, DateTimeOffset ExpiresUtc);
    private sealed record Authorization(CloudAccount Account, string Token, IReadOnlyList<CloudBucket>? AllowedBuckets,
        long MinimumPartSize, long RecommendedPartSize);
}

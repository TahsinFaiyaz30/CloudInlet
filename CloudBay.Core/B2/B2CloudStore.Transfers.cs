using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core.Transfers;

namespace CloudBay.Core.B2;

public sealed partial class B2CloudStore
{
    internal string TransferAccountId => Current.Account.AccountId;

    /// <summary>Explicitly remove a caller-owned unfinished upload after its workers have stopped.</summary>
    public async Task CancelTransferUploadAsync(string bucketId, TransferCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (checkpoint.Provider != "b2" || checkpoint.Data?.GetValueOrDefault("kind") != "large") return;
        var key = checkpoint.Data.GetValueOrDefault("key") ?? throw new InvalidDataException("The B2 upload checkpoint has no destination path.");
        var operation = checkpoint.Data.GetValueOrDefault("operation_id") ?? throw new InvalidDataException("The B2 upload checkpoint has no operation identity.");
        var source = checkpoint.Data.GetValueOrDefault("source_id") ?? throw new InvalidDataException("The B2 upload checkpoint has no source identity.");
        ValidateAccess(bucketId, key, "writeFiles"); ValidateAccess(bucketId, key, "listFiles");
        var found = await FindTransferStartAsync(bucketId, key, operation, source, cancellationToken).ConfigureAwait(false);
        if (found is null) return; // A completed destination is never cancelled or deleted.
        if (checkpoint.SessionId.Length > 0 && found != checkpoint.SessionId)
            throw new InvalidDataException("The unfinished upload does not match the caller's checkpoint.");
        await CancelLargeAsync(found, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(string bucketId, string prefix,
        CancellationToken cancellationToken = default)
    {
        var folders = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ListTransferPageAsync(bucketId, prefix, cursor, foldersOnly: true, cancellationToken).ConfigureAwait(false);
            folders.AddRange(page.Files.Where(f => f.Action == "folder" || f.Key.EndsWith('/')).Select(f => f.Key));
            cursor = page.Next;
        } while (cursor is not null);
        return folders.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal async Task<(IReadOnlyList<CloudObject> Files, string? Next)> ListTransferPageAsync(string bucketId,
        string prefix, string? cursor, bool foldersOnly, CancellationToken token)
    {
        ValidateAccess(bucketId, prefix, "listFiles");
        if (cursor is not null && !cursor.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The saved B2 discovery cursor is outside the selected folder.");
        var body = new Dictionary<string, object?>
        { ["bucketId"] = bucketId, ["prefix"] = prefix, ["startFileName"] = cursor, ["maxFileCount"] = ListPageSize };
        if (foldersOnly) body["delimiter"] = "/";
        using var json = await ApiAsync("b2_list_file_names", body, token).ConfigureAwait(false);
        var root = json.RootElement;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() > ListPageSize || !root.TryGetProperty("nextFileName", out var next) ||
            next.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("Backblaze returned an incomplete transfer discovery page.");
        var result = new List<CloudObject>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in files.EnumerateArray())
        {
            var key = RequiredString(item, "fileName");
            var action = RequiredString(item, "action");
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !names.Add(key) ||
                action is not ("upload" or "folder") || action == "folder" && !foldersOnly)
                throw new InvalidDataException("Backblaze returned an invalid transfer discovery entry.");
            if (action == "folder") result.Add(new("", key, 0, null, DateTimeOffset.UnixEpoch, "folder"));
            else
            {
                if (!HasNonnegativeInteger(item, "contentLength") || !HasNonnegativeInteger(item, "uploadTimestamp"))
                    throw new InvalidDataException("Backblaze returned an invalid transfer file length or timestamp.");
                result.Add(ParseObject(item));
            }
        }
        var continuation = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
        if (continuation is not null && (continuation.Length == 0 || continuation == cursor ||
            !continuation.StartsWith(prefix, StringComparison.Ordinal) || files.GetArrayLength() == 0))
            throw new InvalidDataException("Backblaze returned an invalid transfer discovery cursor.");
        return (result, continuation);
    }

    internal async Task<CloudObject?> FindTransferCurrentAsync(string bucketId, string key, CancellationToken token)
    {
        ValidateAccess(bucketId, key, "listFiles");
        using var json = await ApiAsync("b2_list_file_names", new { bucketId, prefix = key, startFileName = key, maxFileCount = 1 }, token).ConfigureAwait(false);
        var files = json.RootElement.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 1)
            throw new InvalidDataException("Backblaze returned an invalid destination lookup.");
        return files.EnumerateArray().Select(ParseObject).FirstOrDefault(f => f.Key == key && f.Action == "upload");
    }

    internal async Task ValidateTransferSourceAsync(string bucketId, CloudObject expected, CancellationToken token)
    {
        ValidateAccess(bucketId, expected.Key, "readFiles");
        using var json = await ApiAsync("b2_get_file_info", new { fileId = expected.FileId }, token).ConfigureAwait(false);
        var actual = ParseObject(json.RootElement);
        if (RequiredString(json.RootElement, "bucketId") != bucketId || actual.FileId != expected.FileId ||
            actual.Key != expected.Key || actual.Action != "upload" || actual.Size != expected.Size ||
            actual.ModifiedUtc != expected.ModifiedUtc || !string.Equals(actual.Sha1, expected.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new TransferSourceChangedException("The selected B2 source version changed or is no longer available.");
    }

    internal async Task DeleteTransferSourceAsync(string bucketId, CloudObject expected, CancellationToken token)
    {
        ValidateAccess(bucketId, expected.Key, "deleteFiles");
        await ValidateTransferSourceAsync(bucketId, expected, token).ConfigureAwait(false);
        var current = await FindTransferCurrentAsync(bucketId, expected.Key, token).ConfigureAwait(false);
        if (current?.FileId != expected.FileId)
            throw new TransferSourceChangedException("A newer B2 source version exists. The copied version was retained.");
        using var json = await ApiAsync("b2_delete_file_version", new { fileName = expected.Key, fileId = expected.FileId },
            token, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
        if (RequiredString(json.RootElement, "fileId") != expected.FileId || RequiredString(json.RootElement, "fileName") != expected.Key)
            throw new InvalidDataException("Backblaze did not acknowledge deletion of the exact selected version.");
    }

    internal async Task<bool> IsTransferSourceDeletedAsync(string bucketId, CloudObject expected, CancellationToken token)
    {
        ValidateAccess(bucketId, expected.Key, "readFiles");
        try
        {
            using var json = await ApiAsync("b2_get_file_info", new { fileId = expected.FileId }, token).ConfigureAwait(false);
            return false;
        }
        catch (B2RequestException error) when (error.StatusCode == HttpStatusCode.NotFound || error.Code == "file_not_present")
        { return true; }
    }

    internal async Task<Stream> OpenTransferReadAsync(CloudObject file, long offset, long length, CancellationToken token)
    {
        ValidateDownloadArguments(file, offset, length);
        if (length == 0) return new MemoryStream([], writable: false);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var auth = Current;
            await _downloads.EnterAsync(token).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            var admitted = true;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{auth.Account.DownloadUrl}/b2api/v4/b2_download_file_by_id?fileId={Uri.EscapeDataString(file.FileId)}");
                request.Headers.TryAddWithoutValidation("Authorization", auth.Token);
                var full = offset == 0 && length == file.Size;
                if (!full) request.Headers.Range = new RangeHeaderValue(offset, checked(offset + length - 1));
                response = await SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadErrorAsync(response, token).ConfigureAwait(false);
                    if (IsExpired(error) && attempt < Attempts - 1)
                    { await ReauthorizeAsync(auth, token).ConfigureAwait(false); continue; }
                    if (!IsTransient(response.StatusCode) || attempt == Attempts - 1) throw error;
                    await BackoffAsync(response, attempt, token).ConfigureAwait(false);
                    continue;
                }
                ValidateDownloadResponse(response, file, offset, length, full);
                if (Header(response, "X-Bz-File-Id") != file.FileId)
                    throw new InvalidDataException("Backblaze returned a different source version.");
                var advertisedHash = Header(response, "X-Bz-Content-Sha1");
                if (!IsSha1(advertisedHash)) advertisedHash = Header(response, "X-Bz-Info-large_file_sha1");
                if (IsSha1(file.Sha1) && IsSha1(advertisedHash) && !file.Sha1!.Equals(advertisedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backblaze advertised different content for the selected source version.");
                var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var owned = new TransferReadStream(this, response, stream, length);
                response = null; admitted = false;
                return owned;
            }
            finally { response?.Dispose(); if (admitted) _downloads.Exit(); }
        }
        throw new HttpRequestException("The B2 source range could not be opened after bounded retries.");
    }

    internal static string TransferSourceId(ITransferSourceFile source) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { source.Entry.Id, source.Entry.Version,
            source.Entry.Size, source.Entry.ModifiedUtc })))).ToLowerInvariant();

    internal async Task<TransferReceipt?> FindTransferReceiptAsync(string bucketId, string key, string operationId,
        ITransferSourceFile source, CancellationToken token)
    {
        ValidateAccess(bucketId, key, "listFiles");
        var sourceId = TransferSourceId(source);
        string? nextName = null, nextId = null;
        TransferReceipt? found = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var json = await ApiAsync("b2_list_file_versions", new
            { bucketId, prefix = key, startFileName = nextName, startFileId = nextId, maxFileCount = ListPageSize }, token).ConfigureAwait(false);
            var files = json.RootElement.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Backblaze returned an incomplete receipt listing.");
            foreach (var item in files.EnumerateArray())
            {
                if (RequiredString(item, "fileName") != key) break;
                if (OptionalString(item, "action") != "upload") continue;
                if (!item.TryGetProperty("fileInfo", out var info) || OptionalString(info, "cloudbay_upload_id") != operationId) continue;
                var file = ParseObject(item);
                if (OptionalString(info, "cloudbay_source_id") != sourceId || file.Size != source.Entry.Size || file.Action != "upload" ||
                    file.ModifiedUtc.ToUnixTimeMilliseconds() != source.Entry.ModifiedUtc.ToUnixTimeMilliseconds() ||
                    OptionalString(item, "bucketId") is { } returnedBucket && returnedBucket != bucketId ||
                    source.Entry.Sha1 is { } expected && !expected.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The B2 receipt does not match the saved source identity and content.");
                if (found is not null && found.Id != file.FileId)
                    throw new InvalidDataException("Multiple B2 versions share this operation. No additional destination file was created.");
                found = TransferReceiptFor(file, operationId, sourceId);
            }
            nextName = OptionalString(json.RootElement, "nextFileName");
            nextId = OptionalString(json.RootElement, "nextFileId");
            if (nextName is not null && (nextName != key || nextId is null)) break;
            if (nextName is not null && !cursors.Add(nextName + "|" + nextId))
                throw new InvalidDataException("Backblaze returned a repeated receipt cursor.");
        } while (nextName is not null);
        return found;
    }

    internal static TransferReceipt TransferReceiptFor(CloudObject file, string operationId, string sourceId) =>
        new(file.FileId, file.Key, file.FileId, file.Size, file.Sha1, operationId,
            new Dictionary<string, string> { ["key"] = file.Key, ["source_id"] = sourceId,
                ["modified_millis"] = file.ModifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) });

    private static TransferReceipt BindTransferContentHash(TransferReceipt receipt, string? uploadedHash)
    {
        if (!IsSha1(uploadedHash) || !uploadedHash!.Equals(receipt.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reconciled B2 receipt has different content from the transmitted source.");
        var data = receipt.Data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(receipt.Data);
        data["upload_sha1"] = uploadedHash;
        return receipt with { Data = data };
    }

    internal async Task<TransferReceipt> UploadTransferAsync(string bucketId, string key, TransferUploadRequest upload,
        ITransferSourceFile source, TransferCheckpoint? saved, Func<TransferCheckpoint, CancellationToken, Task> save,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        ValidateAccess(bucketId, key, "writeFiles"); ValidateAccess(bucketId, key, "listFiles"); ValidateKey(key);
        if (source.Entry.Size is < 0 or > MaxLargeFileSize) throw new ArgumentOutOfRangeException(nameof(source));
        if (upload.OperationId is not { Length: 64 } || !upload.OperationId.All(Uri.IsHexDigit))
            throw new ArgumentException("A stable SHA256 operation identity is required.", nameof(upload));
        var sourceId = TransferSourceId(source);
        if (saved is not null && (saved.Provider != "b2" || saved.Data is null ||
            saved.Data.GetValueOrDefault("operation_id") != upload.OperationId || saved.Data.GetValueOrDefault("source_id") != sourceId ||
            saved.Data.GetValueOrDefault("key") != key))
            throw new InvalidDataException("The saved B2 checkpoint belongs to another source or destination.");
        using var operation = await LockOperationAsync(Current.Account.AccountId + "|transfer|" + upload.OperationId, token).ConfigureAwait(false);
        await source.ValidateAsync(token).ConfigureAwait(false);
        await _uploads.EnterAsync(token).ConfigureAwait(false);
        try
        {
            return source.Entry.Size < MultipartThreshold
                ? await UploadTransferSmallAsync(bucketId, key, upload.OperationId, sourceId, source, saved, save, progress, token).ConfigureAwait(false)
                : await UploadTransferLargeAsync(bucketId, key, upload.OperationId, sourceId, source, saved, save, progress, upload.ConflictPolicy, token).ConfigureAwait(false);
        }
        finally { _uploads.Exit(); }
    }

    private static Dictionary<string, string> TransferData(string key, string operationId, string sourceId, string kind) =>
        new(StringComparer.Ordinal) { ["key"] = key, ["operation_id"] = operationId, ["source_id"] = sourceId, ["kind"] = kind };

    private static HttpRequestMessage TransferUploadRequest(UploadSession session, ReplayableContent content, long length)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, session.Url);
        request.Headers.ExpectContinue = false;
        request.Headers.TryAddWithoutValidation("Authorization", session.Token);
        request.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", "hex_digits_at_end");
        request.Content = content;
        request.Content.Headers.ContentLength = checked(length + 40);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("b2/x-auto");
        return request;
    }

    private async Task<TransferReceipt> UploadTransferSmallAsync(string bucketId, string key, string operationId,
        string sourceId, ITransferSourceFile source, TransferCheckpoint? saved,
        Func<TransferCheckpoint, CancellationToken, Task> save, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        var data = TransferData(key, operationId, sourceId, "small");
        if (saved?.Data?.GetValueOrDefault("pending") == "true")
        {
            var recovered = await FindTransferReceiptAsync(bucketId, key, operationId, source, token).ConfigureAwait(false);
            if (recovered is not null) { progress?.Report(new(source.Entry.Size, source.Entry.Size) { IsBaseline = true }); return recovered; }
            throw UnknownOutcome("cloud upload");
        }
        var pool = _uploadSessions.GetOrAdd(bucketId, _ => new());
        UploadSession? session = null;
        var reusable = false;
        await _uploadRequests.EnterAsync(token).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                long bytes = 0;
                progress?.Report(new(0, source.Entry.Size) { IsBaseline = true });
                await using var content = new ReplayableContent(source, 0, source.Entry.Size, _uploadLimit,
                    count => progress?.Report(new(Interlocked.Add(ref bytes, count), source.Entry.Size)), token);
                content.BeginPreparation(token);
                while (session is null && pool.TryTake(out var cached))
                    if (cached.ExpiresUtc > DateTimeOffset.UtcNow) session = cached;
                session ??= await GetUploadSessionAsync(bucketId, null, token).ConfigureAwait(false);
                using var request = TransferUploadRequest(session, content, source.Entry.Size);
                request.Headers.TryAddWithoutValidation("X-Bz-File-Name", EncodeName(key));
                request.Headers.TryAddWithoutValidation("X-Bz-Info-src_last_modified_millis", source.Entry.ModifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation("X-Bz-Info-cloudbay_upload_id", operationId);
                request.Headers.TryAddWithoutValidation("X-Bz-Info-cloudbay_source_id", sourceId);
                data["pending"] = "true";
                await save(new("b2", operationId, 0, new Dictionary<string, string>(data)), token).ConfigureAwait(false);
                HttpStatusCode? observedStatus = null;
                Version? observedVersion = null;
                HttpStatusCode? rejectedStatus = null;
                try
                {
                    using var response = await SendAsync(request, token).ConfigureAwait(false);
                    observedStatus = response.StatusCode;
                    observedVersion = response.Version;
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = await ReadDocumentAsync(response, token).ConfigureAwait(false);
                        if (json.RootElement.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("Backblaze returned incomplete upload acknowledgment metadata.");
                        var file = ParseObject(json.RootElement);
                        if (file.Action != "upload" || file.Key != key || file.Size != source.Entry.Size || !IsSha1(content.Sha1) ||
                            !content.Sha1!.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase) || RequiredString(json.RootElement, "bucketId") != bucketId)
                            throw new InvalidDataException("The B2 cloud upload acknowledgment failed content verification.");
                        reusable = true;
                        return BindTransferContentHash(TransferReceiptFor(file, operationId, sourceId), content.Sha1);
                    }
                    // Receiving a definite rejection is sufficient to release the
                    // creation intent. A truncated error body must not turn an
                    // documented upload rejection into an uncertain upload.
                    // Redirects and undocumented statuses are not such receipts.
                    if (IsDefiniteTransferUploadRejection(response.StatusCode))
                    {
                        rejectedStatus = response.StatusCode;
                        data["pending"] = "false";
                        await save(new("b2", operationId, 0, new Dictionary<string, string>(data)), CancellationToken.None).ConfigureAwait(false);
                    }
                    var error = await ReadErrorAsync(response, token).ConfigureAwait(false);
                    session = null;
                    if (rejectedStatus is null && content.TrailerStarted)
                    {
                        await RecordUncertaintyAsync("provider_response", response.StatusCode, error.Code, content: content,
                            responseVersion: response.Version).ConfigureAwait(false);
                        var receipt = await FindTransferReceiptAsync(bucketId, key, operationId, source, token).ConfigureAwait(false);
                        if (receipt is not null) return BindTransferContentHash(receipt, content.Sha1);
                        throw UnknownOutcome("cloud upload", error);
                    }
                    data["pending"] = "false";
                    await save(new("b2", operationId, 0, new Dictionary<string, string>(data)), token).ConfigureAwait(false);
                    if (!IsUploadRetry(response.StatusCode, error.Code) || attempt == Attempts - 1) throw error;
                    await BackoffAsync(response, attempt, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is TransferSourceChangedException or InvalidDataException && !content.TrailerStarted)
                {
                    if (content.SendMilliseconds is null) reusable = true;
                    data["pending"] = "false";
                    await save(new("b2", operationId, 0, new Dictionary<string, string>(data)), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception error) when (error is OperationCanceledException or HttpRequestException or InvalidDataException or JsonException ||
                    error is InvalidOperationException && observedStatus is not null ||
                    error is IOException and not B2RequestException and not UnknownTransferOutcomeException and not TransferSourceChangedException)
                {
                    if (content.SendMilliseconds is null) reusable = true;
                    else session = null;
                    if (rejectedStatus is { } status)
                    {
                        if (error is OperationCanceledException) throw;
                        // The status is trustworthy even when its body cannot be
                        // read. Do not log provider text or the transport exception.
                        var rejection = new B2RequestException(status, "request_failed");
                        if (!IsUploadRetry(status, rejection.Code) || attempt == Attempts - 1) throw rejection;
                        await BackoffAsync(null, attempt, token).ConfigureAwait(false);
                        continue;
                    }
                    if (!content.TrailerStarted)
                    {
                        data["pending"] = "false";
                        // Persist a proven incomplete body even during pause/cancel.
                        await save(new("b2", operationId, 0, new Dictionary<string, string>(data)), CancellationToken.None).ConfigureAwait(false);
                        if (error is OperationCanceledException || attempt == Attempts - 1) throw;
                        await BackoffAsync(null, attempt, token).ConfigureAwait(false);
                        continue;
                    }
                    await RecordUncertaintyAsync(error is OperationCanceledException ? "cancelled_after_body" :
                        observedStatus is { } responseStatus && (int)responseStatus is >= 200 and < 300 ? "acknowledgment_unreadable" :
                        observedStatus is not null ? "provider_error_body_unreadable" : "transport_after_body", observedStatus,
                        transportError: error, content: content, responseVersion: observedVersion).ConfigureAwait(false);
                    if (error is OperationCanceledException) throw;
                    var receipt = await FindTransferReceiptAsync(bucketId, key, operationId, source, token).ConfigureAwait(false);
                    if (receipt is not null) return BindTransferContentHash(receipt, content.Sha1);
                    // The checkpoint retains safe classification fields. A raw
                    // transport/parser exception must not leak through diagnostics.
                    throw UnknownOutcome("cloud upload");
                }
            }
            throw new IOException("The B2 cloud upload failed after bounded retries.");
        }
        finally { if (reusable && session is not null) pool.Add(session); _uploadRequests.Exit(); }

        Task RecordUncertaintyAsync(string category, HttpStatusCode? status, string? safeCode = null, Exception? transportError = null,
            ReplayableContent? content = null, Version? responseVersion = null)
        {
            data["failure_category"] = category;
            if (status is { } value) data["response_status"] = ((int)value).ToString(CultureInfo.InvariantCulture);
            else data.Remove("response_status");
            if (safeCode is not null) data["response_code"] = safeCode;
            else data.Remove("response_code");
            if (transportError is HttpRequestException)
            {
                var failure = ClassifyTransportFailure(transportError);
                data["transport_error"] = failure.RequestError.ToString();
                data["transport_inner_types"] = failure.InnerTypes;
                data["transport_hresult"] = failure.HResult;
                if (failure.SocketError is { } socket) data["transport_socket_error"] = socket;
                if (failure.InnerHResult is { } innerHResult) data["transport_inner_hresult"] = innerHResult;
            }
            if (content is not null)
            {
                if (content.SourceOpenMilliseconds is { } opened) data["source_open_ms"] = opened.ToString(CultureInfo.InvariantCulture);
                if (content.FirstBlockMilliseconds is { } first) data["source_first_block_ms"] = first.ToString(CultureInfo.InvariantCulture);
                if (content.SourceReadMilliseconds is { } read) data["source_read_ms"] = read.ToString(CultureInfo.InvariantCulture);
                if (content.SendMilliseconds is { } send) data["request_send_ms"] = send.ToString(CultureInfo.InvariantCulture);
                data["prepared_bytes"] = content.PreparedBytes.ToString(CultureInfo.InvariantCulture);
                data["sent_bytes"] = content.SentBytes.ToString(CultureInfo.InvariantCulture);
                data["request_version"] = "2.0";
                data["request_version_policy"] = "RequestVersionOrLower";
            }
            if (responseVersion is { Major: 1 or 2 or 3, Minor: 0 or 1 }) data["response_version"] = responseVersion.ToString(2);
            // Only bounded classifications and the allowlisted B2 error code are
            // durable. Exception messages can contain URLs or credentials.
            return save(new("b2", operationId, 0, new Dictionary<string, string>(data)), CancellationToken.None);
        }
    }

    private static bool IsDefiniteTransferUploadRejection(HttpStatusCode status) => status is
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.LengthRequired or HttpStatusCode.UnsupportedMediaType or
        HttpStatusCode.TooManyRequests;

    private async Task<TransferReceipt> UploadTransferLargeAsync(string bucketId, string key, string operationId,
        string sourceId, ITransferSourceFile source, TransferCheckpoint? saved,
        Func<TransferCheckpoint, CancellationToken, Task> save, IProgress<TransferProgress>? progress,
        TransferConflictPolicy conflictPolicy, CancellationToken token)
    {
        var data = TransferData(key, operationId, sourceId, "large");
        var partSize = saved?.Data is { } old && long.TryParse(old.GetValueOrDefault("part_size"), CultureInfo.InvariantCulture, out var size)
            ? size : MultipartPartSize(Current, source.Entry.Size);
        if (partSize < Current.MinimumPartSize || partSize > MaxPartSize || (source.Entry.Size + partSize - 1) / partSize is < 2 or > 10_000)
            throw new InvalidDataException("The saved B2 multipart layout is invalid.");
        data["part_size"] = partSize.ToString(CultureInfo.InvariantCulture);
        var hashes = new string?[checked((int)((source.Entry.Size + partSize - 1) / partSize))];
        var acknowledged = new Dictionary<int, string>();
        if (saved?.Data?.GetValueOrDefault("parts") is { } serialized)
        {
            try { acknowledged = JsonSerializer.Deserialize<Dictionary<int, string>>(serialized) ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidDataException("The saved B2 part receipts are invalid."); }
            if (acknowledged.Any(p => p.Key < 1 || p.Key > hashes.Length || !IsSha1(p.Value)))
                throw new InvalidDataException("The saved B2 part receipts do not match this file.");
        }
        string? fileId = saved?.SessionId is { Length: > 0 } previous ? previous : null;
        if (fileId is null && saved is not null)
            fileId = await FindTransferStartAsync(bucketId, key, operationId, sourceId, token).ConfigureAwait(false);
        if (fileId is null)
        {
            if (saved?.Data?.GetValueOrDefault("pending") == "true") throw UnknownOutcome("multipart start");
            data["pending"] = "true";
            await save(new("b2", "", 0, new Dictionary<string, string>(data)), token).ConfigureAwait(false);
            var metadata = new Dictionary<string, string>
            {
                ["cloudbay_upload_id"] = operationId, ["cloudbay_source_id"] = sourceId,
                ["src_last_modified_millis"] = source.Entry.ModifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            };
            if (IsSha1(source.Entry.Sha1)) metadata["large_file_sha1"] = source.Entry.Sha1!.ToLowerInvariant();
            try
            {
                using var started = await ApiAsync("b2_start_large_file", new
                { bucketId, fileName = key, contentType = "b2/x-auto", fileInfo = metadata }, token,
                    retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                fileId = RequiredString(started.RootElement, "fileId");
            }
            catch (B2RequestException error) when (!IsTransient(error.StatusCode))
            {
                data["pending"] = "false";
                await save(new("b2", "", 0, new Dictionary<string, string>(data)), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        long confirmed = 0;
        if (saved?.SessionId is { Length: > 0 } || saved is not null)
        {
            Dictionary<int, string> parts;
            try { parts = await ReadTransferPartsAsync(fileId, hashes.Length, partSize, source.Entry.Size, token).ConfigureAwait(false); }
            catch (B2RequestException error) when (error.StatusCode == HttpStatusCode.NotFound || error.Code == "file_not_present")
            {
                // An externally cancelled unfinished file has no reusable session. Confirm
                // that its immutable ID is absent, then restart only this unfinished file.
                try
                {
                    using var existing = await ApiAsync("b2_get_file_info", new { fileId }, token).ConfigureAwait(false);
                    throw new InvalidDataException("A completed B2 file exists without a matching operation receipt.");
                }
                catch (B2RequestException missing) when (missing.StatusCode == HttpStatusCode.NotFound || missing.Code == "file_not_present")
                {
                    var recoveredReceipt = await FindTransferReceiptAsync(bucketId, key, operationId, source, token).ConfigureAwait(false);
                    if (recoveredReceipt is not null) return recoveredReceipt;
                    return await UploadTransferLargeAsync(bucketId, key, operationId, sourceId, source, null, save, progress, conflictPolicy, token).ConfigureAwait(false);
                }
            }
            foreach (var part in parts)
            {
                if (acknowledged.TryGetValue(part.Key, out var expected))
                {
                    if (!expected.Equals(part.Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A B2 acknowledged part changed after its checkpoint.");
                }
                else
                {
                    // Only an acknowledgment lost at interruption needs a source range reread.
                    await using var input = await source.OpenReadAsync((part.Key - 1) * partSize,
                        Math.Min(partSize, source.Entry.Size - (part.Key - 1) * partSize), token).ConfigureAwait(false);
                    var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, token).ConfigureAwait(false));
                    if (!actual.Equals(part.Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An uncertain B2 part does not match its immutable source.");
                }
                hashes[part.Key - 1] = part.Value;
                acknowledged[part.Key] = part.Value;
                confirmed += Math.Min(partSize, source.Entry.Size - (part.Key - 1) * partSize);
            }
        }
        data["pending"] = "false";
        data["parts"] = JsonSerializer.Serialize(acknowledged);
        await save(new("b2", fileId, confirmed, new Dictionary<string, string>(data)), token).ConfigureAwait(false);
        progress?.Report(new(confirmed, source.Entry.Size) { IsBaseline = true });
        var gate = new object();
        using var saveGate = new SemaphoreSlim(1, 1);
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(token);
        long displayed = confirmed;
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, Math.Min(hashes.Length, Volatile.Read(ref _connections))).Select(async _ =>
        {
            UploadSession? session = null;
            await _uploadRequests.EnterAsync(workers.Token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= hashes.Length) return;
                    if (hashes[index] is not null) continue;
                    var offset = index * partSize;
                    var length = Math.Min(partSize, source.Entry.Size - offset);
                    for (var attempt = 0; attempt < Attempts; attempt++)
                    {
                        long sent = 0;
                        await using var content = new ReplayableContent(source, offset, length, _uploadLimit, count =>
                        { lock (gate) { sent += count; displayed += count; progress?.Report(new(Math.Min(displayed, source.Entry.Size), source.Entry.Size)); } }, workers.Token);
                        content.BeginPreparation(workers.Token);
                        session ??= await GetUploadSessionAsync(bucketId, fileId, workers.Token).ConfigureAwait(false);
                        using var request = TransferUploadRequest(session, content, length);
                        request.Headers.TryAddWithoutValidation("X-Bz-Part-Number", (index + 1).ToString(CultureInfo.InvariantCulture));
                        try
                        {
                            using var response = await SendAsync(request, workers.Token).ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                            {
                                var error = await ReadErrorAsync(response, workers.Token).ConfigureAwait(false);
                                session = null;
                                if (!IsUploadRetry(response.StatusCode, error.Code) || attempt == Attempts - 1) throw error;
                                RollBack(); await BackoffAsync(response, attempt, workers.Token).ConfigureAwait(false); continue;
                            }
                            using var json = await ReadDocumentAsync(response, workers.Token).ConfigureAwait(false);
                            var hash = ((ReplayableContent)request.Content!).Sha1;
                            if (!IsSha1(hash) || RequiredString(json.RootElement, "fileId") != fileId ||
                                LongValue(json.RootElement, "partNumber") != index + 1 || LongValue(json.RootElement, "contentLength") != length ||
                                !hash!.Equals(RequiredString(json.RootElement, "contentSha1"), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Backblaze acknowledged a multipart range with different content.");
                            await saveGate.WaitAsync(workers.Token).ConfigureAwait(false);
                            try
                            {
                                hashes[index] = hash;
                                acknowledged[index + 1] = hash;
                                confirmed += length;
                                data["parts"] = JsonSerializer.Serialize(acknowledged);
                                await save(new("b2", fileId, confirmed, new Dictionary<string, string>(data)), workers.Token).ConfigureAwait(false);
                            }
                            finally { saveGate.Release(); }
                            break;
                        }
                        catch (Exception error) when ((error is HttpRequestException || error is IOException and not B2RequestException and not TransferSourceChangedException) && attempt < Attempts - 1)
                        { if (content.SendMilliseconds is not null) session = null; RollBack(); await BackoffAsync(null, attempt, workers.Token).ConfigureAwait(false); }

                        void RollBack() { lock (gate) { displayed -= sent; progress?.Report(new(Math.Max(0, displayed), source.Entry.Size) { IsBaseline = true }); } }
                    }
                }
            }
            catch { await workers.CancelAsync().ConfigureAwait(false); throw; }
            finally { _uploadRequests.Exit(); }
        })).ConfigureAwait(false);
        await source.ValidateAsync(token).ConfigureAwait(false);
        if (conflictPolicy is TransferConflictPolicy.Fail or TransferConflictPolicy.Skip or TransferConflictPolicy.Rename &&
            await FindTransferCurrentAsync(bucketId, key, token).ConfigureAwait(false) is not null)
        {
            var receipt = await FindTransferReceiptAsync(bucketId, key, operationId, source, token).ConfigureAwait(false);
            if (receipt is not null) { progress?.Report(new(source.Entry.Size, source.Entry.Size) { IsBaseline = true }); return receipt; }
            // Native B2 name creation has no atomic If-None-Match primitive. This
            // final guard stops observed late conflicts before committing parts.
            throw new TransferConflictException("The selected B2 destination became occupied before multipart completion. The unfinished upload and source are retained.");
        }
        var completed = await FinishAsync(fileId, hashes.Select(h => h ?? throw new InvalidDataException("An upload part is missing.")).ToArray(), token).ConfigureAwait(false);
        if (completed.Key != key || completed.Size != source.Entry.Size || completed.FileId != fileId || completed.Action != "upload")
            throw new InvalidDataException("Backblaze returned an invalid completed multipart receipt.");
        progress?.Report(new(source.Entry.Size, source.Entry.Size));
        return TransferReceiptFor(completed, operationId, sourceId);
    }

    private async Task<string?> FindTransferStartAsync(string bucketId, string key, string operationId, string sourceId, CancellationToken token)
    {
        string? cursor = null, found = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var json = await ApiAsync("b2_list_unfinished_large_files", new { bucketId, namePrefix = key, startFileId = cursor, maxFileCount = 100 }, token).ConfigureAwait(false);
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                if (OptionalString(file, "fileName") != key || !file.TryGetProperty("fileInfo", out var info) ||
                    OptionalString(info, "cloudbay_upload_id") != operationId) continue;
                if (OptionalString(info, "cloudbay_source_id") != sourceId || RequiredString(file, "bucketId") != bucketId)
                    throw new InvalidDataException("The unfinished B2 upload belongs to another source.");
                var id = RequiredString(file, "fileId");
                if (found is not null && found != id) throw new InvalidDataException("Multiple unfinished B2 uploads share this operation.");
                found = id;
            }
            cursor = OptionalString(json.RootElement, "nextFileId");
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidDataException("Backblaze returned a repeated unfinished-upload cursor.");
        } while (cursor is not null);
        return found;
    }

    private async Task<Dictionary<int, string>> ReadTransferPartsAsync(string fileId, int count, long partSize, long size, CancellationToken token)
    {
        var result = new Dictionary<int, string>();
        var next = 1;
        do
        {
            using var json = await ApiAsync("b2_list_parts", new { fileId, startPartNumber = next, maxPartCount = 1000 }, token).ConfigureAwait(false);
            foreach (var part in json.RootElement.GetProperty("parts").EnumerateArray())
            {
                var number = checked((int)LongValue(part, "partNumber"));
                var hash = RequiredString(part, "contentSha1");
                if (number < next || number > count || !IsSha1(hash) || RequiredString(part, "fileId") != fileId ||
                    LongValue(part, "contentLength") != Math.Min(partSize, size - (number - 1) * partSize) || !result.TryAdd(number, hash))
                    throw new InvalidDataException("The retained B2 part receipt is invalid.");
            }
            var cursor = json.RootElement.GetProperty("nextPartNumber");
            if (cursor.ValueKind == JsonValueKind.Null) return result;
            if (!cursor.TryGetInt32(out var continuation) || continuation <= next || continuation > count)
                throw new InvalidDataException("Backblaze returned an invalid part-list continuation.");
            next = continuation;
        } while (true);
    }

    internal async Task<string> VerifyTransferReceiptAsync(string bucketId, TransferReceipt receipt, ITransferSourceFile source, CancellationToken token)
    {
        var key = receipt.Data?.GetValueOrDefault("key") ?? receipt.RelativePath;
        ValidateAccess(bucketId, key, "readFiles");
        using var json = await ApiAsync("b2_get_file_info", new { fileId = receipt.Id }, token).ConfigureAwait(false);
        var actual = ParseObject(json.RootElement);
        if (actual.FileId != receipt.Id || actual.Key != key || actual.Size != source.Entry.Size || actual.Action != "upload" ||
            RequiredString(json.RootElement, "bucketId") != bucketId || !json.RootElement.TryGetProperty("fileInfo", out var info) ||
            OptionalString(info, "cloudbay_upload_id") != receipt.OperationId || OptionalString(info, "cloudbay_source_id") != TransferSourceId(source))
            throw new InvalidDataException("The B2 destination receipt no longer matches this operation.");
        await source.ValidateAsync(token).ConfigureAwait(false);
        if (source.Entry.Size < MultipartThreshold && IsSha1(actual.Sha1) && IsSha1(receipt.Sha1))
        {
            if (!actual.Sha1!.Equals(receipt.Sha1, StringComparison.OrdinalIgnoreCase) ||
                source.Entry.Sha1 is { } expected && !actual.Sha1.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The B2 destination failed provider SHA1 verification.");
            var uploadedHash = source.Entry.Sha1 ?? receipt.Data?.GetValueOrDefault("upload_sha1");
            if (!IsSha1(uploadedHash))
            {
                // A crash can lose the locally computed checksum of a small source without
                // provider hashes. Recheck that source once, never replay its destination upload.
                await using var original = await source.OpenReadAsync(0, source.Entry.Size, token).ConfigureAwait(false);
                uploadedHash = Convert.ToHexString(await SHA1.HashDataAsync(original, token).ConfigureAwait(false));
            }
            if (!actual.Sha1!.Equals(uploadedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The B2 destination checksum differs from the selected source content.");
            return uploadedHash!.ToLowerInvariant();
        }
        // B2 whole-file SHA1 for multipart is client metadata, not a provider-computed hash.
        // Read back through bounded RAM before Move; absent source hashes also require one
        // immutable source pass. These provider limitations are included in elapsed throughput.
        byte[] destinationHash;
        await using (var destination = await OpenTransferReadAsync(actual, 0, actual.Size, token).ConfigureAwait(false))
            destinationHash = await SHA1.HashDataAsync(destination, token).ConfigureAwait(false);
        // Release this store's download slot before opening another source stream. A
        // same-account B2 source without a whole-file hash must work with one connection;
        // retaining a completed destination stream would otherwise deadlock that read.
        string expectedHash;
        if (IsSha1(source.Entry.Sha1)) expectedHash = source.Entry.Sha1!;
        else
        {
            await using var original = await source.OpenReadAsync(0, source.Entry.Size, token).ConfigureAwait(false);
            expectedHash = Convert.ToHexString(await SHA1.HashDataAsync(original, token).ConfigureAwait(false));
        }
        if (!Convert.ToHexString(destinationHash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The B2 destination failed streaming content verification.");
        await source.ValidateAsync(token).ConfigureAwait(false);
        return expectedHash.ToLowerInvariant();
    }

    private sealed class TransferReadStream(B2CloudStore owner, HttpResponseMessage response, Stream stream, long length) : Stream
    {
        private long _read;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_read == length || buffer.Length == 0) return 0;
            var count = await owner.ReadDownloadNetworkAsync(stream, buffer[..(int)Math.Min(buffer.Length, length - _read)], token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The B2 cloud range ended before its acknowledged length.");
            await owner._downloadLimit.WaitAsync(count, token).ConfigureAwait(false);
            _read += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; response.Dispose(); owner._downloads.Exit(); }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

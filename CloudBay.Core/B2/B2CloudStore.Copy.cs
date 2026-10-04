using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CloudBay.Core.B2;

public sealed partial class B2CloudStore
{
    private B2TransferIntentJournal? _transferIntents;
    private readonly ConcurrentDictionary<string, B2TransferIntentJournal.Entry> _volatileIntents = new(StringComparer.Ordinal);
    private readonly object _intentGate = new();
    private readonly Dictionary<string, IntentGate> _intentLocks = new(StringComparer.Ordinal);

    /// <summary>Read-only validation for an already completed import; never creates a version.</summary>
    public async Task VerifyCopyAsync(string destinationBucketId, string destinationKey, CloudObject source,
        string operationId, CloudObject existing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(existing);
        ValidateKey(destinationKey); ValidateAccess(destinationBucketId, destinationKey, "readFiles");
        if (!IsSha1(source.Sha1) || existing.Key != destinationKey || existing.Size != source.Size ||
            !source.Sha1!.Equals(existing.Sha1, StringComparison.OrdinalIgnoreCase) ||
            operationId is not { Length: 64 } || !operationId.All(Uri.IsHexDigit))
            throw new InvalidDataException("The saved copy receipt does not match the selected source and destination.");
        var expected = new B2TransferIntentJournal.Entry(1, "copy", Current.Account.AccountId, destinationBucketId,
            destinationKey, operationId.ToLowerInvariant(), source.FileId, source.Size, source.Sha1!, source.ModifiedUtc.ToUnixTimeMilliseconds());
        await VerifyOperationReceiptAsync(expected, existing, cancellationToken).ConfigureAwait(false);
    }
    public async Task<CloudObject> CopyToAsync(string destinationBucketId, string destinationKey, CloudObject source,
        string operationId, CancellationToken cancellationToken = default)
    {
        await _uploads.EnterAsync(cancellationToken).ConfigureAwait(false);
        try { return await CopyToCoreAsync(destinationBucketId, destinationKey, source, operationId, cancellationToken).ConfigureAwait(false); }
        finally { _uploads.Exit(); }
    }

    private async Task<CloudObject> CopyToCoreAsync(string destinationBucketId, string destinationKey, CloudObject source,
        string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateKey(destinationKey); ValidateAccess(destinationBucketId, destinationKey, "writeFiles");
        ValidateAccess(destinationBucketId, destinationKey, "listFiles"); ValidateCapability("readFiles");
        ValidateKey(source.Key); ValidatePrefix(source.Key);
        if (operationId is not { Length: 64 } || !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("A stable SHA256 import operation ID is required.", nameof(operationId));
        if (source.Action != "upload" || string.IsNullOrWhiteSpace(source.FileId) || !IsSha1(source.Sha1))
            throw new ArgumentException("Copy requires an immutable content version with its whole-file SHA1.", nameof(source));
        if (source.Size is < 0 or > MaxLargeFileSize) throw new ArgumentOutOfRangeException(nameof(source));
        operationId = operationId.ToLowerInvariant();
        var accountId = Current.Account.AccountId;
        var memoryId = accountId + "|" + operationId;
        using var operationLock = await LockOperationAsync(memoryId, cancellationToken).ConfigureAwait(false);
        var journal = _transferIntents;
        var path = journal?.PathFor(accountId, operationId);
        await using var held = journal is null ? null : await journal.LockAsync(path!, cancellationToken).ConfigureAwait(false);
        var expected = new B2TransferIntentJournal.Entry(1, "copy", accountId, destinationBucketId, destinationKey,
            operationId, source.FileId, source.Size, source.Sha1!.ToLowerInvariant(), source.ModifiedUtc.ToUnixTimeMilliseconds());
        var entry = journal?.Read(path!) ?? (_volatileIntents.TryGetValue(memoryId, out var memory) ? memory : null);
        if (entry is not null && !SameIntent(entry, expected))
            throw new InvalidDataException("This import operation ID belongs to a different source or destination. The earlier intent was retained.");
        entry ??= expected;

        // File IDs carry no trusted bucket field. Re-read the immutable source before the
        // first copy so a stale preview or restricted key cannot copy a different object.
        using var sourceInfo = await ApiAsync("b2_get_file_info", new { fileId = source.FileId }, cancellationToken).ConfigureAwait(false);
        var actualSource = ParseObject(sourceInfo.RootElement);
        var sourceBucket = RequiredString(sourceInfo.RootElement, "bucketId");
        ValidateAccess(sourceBucket, actualSource.Key, "readFiles");
        ValidateAcknowledgment(sourceInfo.RootElement, sourceBucket, source.Key, source.Size, source.Sha1!, source.FileId);
        if (actualSource.ModifiedUtc.ToUnixTimeMilliseconds() != source.ModifiedUtc.ToUnixTimeMilliseconds())
            throw new InvalidDataException("The source timestamp does not match the immutable version selected for import.");
        if (OptionalString(sourceInfo.RootElement, "accountId") is { } sourceAccount && sourceAccount != accountId)
            throw new UnauthorizedAccessException("Direct B2 import requires source and destination in the same account.");
        if (sourceBucket == destinationBucketId && source.Key == destinationKey)
            throw new ArgumentException("An import destination cannot be the source cloud path.", nameof(destinationKey));
        var metadata = CopyMetadata(sourceInfo.RootElement, entry);
        void Persist(B2TransferIntentJournal.Entry value)
        { entry = value; _volatileIntents[memoryId] = value; if (journal is not null) journal.Write(path!, value); }
        void Forget() { _volatileIntents.TryRemove(memoryId, out _); journal?.Remove(path!); }

        var existing = await FindOperationReceiptAsync(entry, cancellationToken).ConfigureAwait(false);
        if (existing is not null) { Forget(); return existing; }
        if (source.Size < MaxPartSize)
        {
            if (entry.RequestPending) throw UnknownOutcome("copy");
            Persist(entry with { RequestPending = true });
            var creatingAcknowledged = false;
            try
            {
                await _uploadRequests.EnterAsync(cancellationToken).ConfigureAwait(false);
                JsonDocument copied;
                try { copied = await ApiAsync("b2_copy_file", new
                    {
                        sourceFileId = source.FileId, fileName = destinationKey, destinationBucketId,
                        metadataDirective = "REPLACE", contentType = OptionalString(sourceInfo.RootElement, "contentType") ?? "b2/x-auto",
                        fileInfo = metadata
                    }, cancellationToken, retryNetwork: false, retryTransient: false).ConfigureAwait(false); }
                finally { _uploadRequests.Exit(); }
                creatingAcknowledged = true;
                using var ownedCopyResponse = copied;
                ValidateAcknowledgment(copied.RootElement, destinationBucketId, destinationKey, source.Size, source.Sha1!);
                var result = ParseObject(copied.RootElement);
                await VerifyOperationReceiptAsync(entry, result, cancellationToken).ConfigureAwait(false);
                Forget(); return result;
            }
            catch (B2RequestException error) when (!IsTransient(error.StatusCode))
            {
                // A rejected metadata read after creation says nothing about whether the
                // creating POST committed. Keep its intent pending until verified.
                if (!creatingAcknowledged) Persist(entry with { RequestPending = false });
                throw;
            }
            catch (Exception error) when (error is not OperationCanceledException && IsAmbiguousOutcome(error))
            {
                var recovered = await FindOperationReceiptAsync(entry, cancellationToken).ConfigureAwait(false);
                if (recovered is not null) { Forget(); return recovered; }
                throw UnknownOutcome("copy", error);
            }
        }

        var partSize = entry.PartSize == 0 ? MultipartPartSize(Current, source.Size) : entry.PartSize;
        Persist(entry with { PartSize = partSize });
        if (entry.FileId is null && entry.RequestPending)
        {
            var recoveredId = await FindOwnedCopyStartAsync(entry, cancellationToken).ConfigureAwait(false);
            if (recoveredId is null) throw UnknownOutcome("multipart copy start");
            Persist(entry with { FileId = recoveredId, RequestPending = false });
        }
        if (entry.FileId is null)
        {
            Persist(entry with { RequestPending = true });
            try
            {
                using var start = await ApiAsync("b2_start_large_file", new
                { bucketId = destinationBucketId, fileName = destinationKey, contentType = "b2/x-auto", fileInfo = metadata },
                    cancellationToken, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                if (RequiredString(start.RootElement, "bucketId") != destinationBucketId || RequiredString(start.RootElement, "fileName") != destinationKey)
                    throw new InvalidDataException("Backblaze returned a different multipart copy destination.");
                Persist(entry with { FileId = RequiredString(start.RootElement, "fileId"), RequestPending = false });
            }
            catch (B2RequestException error) when (!IsTransient(error.StatusCode))
            { Persist(entry with { RequestPending = false }); throw; }
            catch (Exception error) when (error is not OperationCanceledException && IsAmbiguousOutcome(error))
            {
                var recoveredId = await FindOwnedCopyStartAsync(entry, cancellationToken).ConfigureAwait(false);
                if (recoveredId is null) throw UnknownOutcome("multipart copy start", error);
                Persist(entry with { FileId = recoveredId, RequestPending = false });
            }
        }
        var hashes = await ReadCopyPartsAsync(entry, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(Enumerable.Range(0, hashes.Length).Where(i => hashes[i] is null),
            new ParallelOptions { MaxDegreeOfParallelism = Volatile.Read(ref _connections), CancellationToken = cancellationToken }, async (i, token) =>
            {
                await _uploadRequests.EnterAsync(token).ConfigureAwait(false);
                try
                {
                    var from = i * partSize; var count = Math.Min(partSize, source.Size - from);
                    using var copied = await ApiAsync("b2_copy_part", new
                    { sourceFileId = source.FileId, largeFileId = entry.FileId, partNumber = i + 1, range = $"bytes={from}-{from + count - 1}" }, token).ConfigureAwait(false);
                    if (RequiredString(copied.RootElement, "fileId") != entry.FileId || LongValue(copied.RootElement, "partNumber") != i + 1 ||
                        LongValue(copied.RootElement, "contentLength") != count || !IsSha1(OptionalString(copied.RootElement, "contentSha1")))
                        throw new InvalidDataException("Backblaze returned a mismatched copied part acknowledgment.");
                    hashes[i] = RequiredString(copied.RootElement, "contentSha1");
                }
                finally { _uploadRequests.Exit(); }
            }).ConfigureAwait(false);
        var finished = await FinishAsync(entry.FileId!, hashes.Select(h => h!).ToArray(), cancellationToken).ConfigureAwait(false);
        if (finished.FileId != entry.FileId || finished.Key != destinationKey || finished.Size != source.Size ||
            !source.Sha1!.Equals(finished.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The finished cloud copy does not match its immutable source.");
        await VerifyOperationReceiptAsync(entry, finished, cancellationToken).ConfigureAwait(false);
        Forget(); return finished;
    }

    private static bool SameIntent(B2TransferIntentJournal.Entry a, B2TransferIntentJournal.Entry b) =>
        a.Kind == b.Kind && a.AccountId == b.AccountId && a.BucketId == b.BucketId && a.Key == b.Key &&
        a.OperationId == b.OperationId && a.SourceId == b.SourceId && a.Length == b.Length &&
        a.Sha1.Equals(b.Sha1, StringComparison.OrdinalIgnoreCase) && a.ModifiedMillis == b.ModifiedMillis;

    private static Dictionary<string, string> CopyMetadata(JsonElement source, B2TransferIntentJournal.Entry entry)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.TryGetProperty("fileInfo", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String) result[property.Name] = property.Value.GetString()!;
        result["cloudbay_import_id"] = entry.OperationId;
        result["cloudbay_source_id"] = entry.SourceId;
        result["src_last_modified_millis"] = entry.ModifiedMillis.ToString(CultureInfo.InvariantCulture);
        result["large_file_sha1"] = entry.Sha1;
        return result;
    }

    private static void ValidateAcknowledgment(JsonElement json, string bucket, string key, long length, string sha1, string? expectedId = null)
    {
        var file = ParseObject(json);
        if (file.Action != "upload" || file.Key != key || file.Size != length || !sha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase) ||
            RequiredString(json, "bucketId") != bucket || expectedId is not null && file.FileId != expectedId)
            throw new InvalidDataException("Backblaze returned a version, path, length, or checksum that does not match the reviewed transfer.");
    }

    private async Task<CloudObject?> FindOperationReceiptAsync(B2TransferIntentJournal.Entry entry, CancellationToken token)
    {
        ValidateAccess(entry.BucketId, entry.Key, "listFiles");
        string? nextName = null, nextId = null;
        CloudObject? receipt = null;
        var cursors = new HashSet<(string Name, string Id)>();
        do
        {
            using var json = await ApiAsync("b2_list_file_versions", new
            { bucketId = entry.BucketId, prefix = entry.Key, startFileName = nextName, startFileId = nextId, maxFileCount = ListPageSize }, token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
                !json.RootElement.TryGetProperty("nextFileName", out var nameField) || nameField.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                !json.RootElement.TryGetProperty("nextFileId", out var idField) || idField.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("Backblaze returned an incomplete operation receipt listing.");
            foreach (var file in files.EnumerateArray())
            {
                if (OptionalString(file, "fileName") != entry.Key) continue;
                if (!MatchesReceipt(file, entry)) continue;
                ValidateAcknowledgment(file, entry.BucketId, entry.Key, entry.Length, entry.Sha1);
                var found = ParseObject(file);
                if (receipt is not null && receipt.FileId != found.FileId)
                    throw new InvalidDataException("Backblaze contains more than one version for this transfer receipt. No additional version was created.");
                await VerifyOperationReceiptAsync(entry, found, token).ConfigureAwait(false);
                receipt = found;
            }
            var name = OptionalString(json.RootElement, "nextFileName"); var id = OptionalString(json.RootElement, "nextFileId");
            if ((name is null) != (id is null) || name is not null && (!cursors.Add((name, id!)) || !name.StartsWith(entry.Key, StringComparison.Ordinal)))
                throw new InvalidDataException("Backblaze returned an invalid receipt listing cursor.");
            nextName = name; nextId = id;
        } while (nextName is not null && nextName == entry.Key);
        return receipt;
    }

    private async Task VerifyOperationReceiptAsync(B2TransferIntentJournal.Entry entry, CloudObject expected, CancellationToken token)
    {
        ValidateAccess(entry.BucketId, entry.Key, "readFiles");
        using var info = await ApiAsync("b2_get_file_info", new { fileId = expected.FileId }, token).ConfigureAwait(false);
        ValidateAcknowledgment(info.RootElement, entry.BucketId, entry.Key, entry.Length, entry.Sha1, expected.FileId);
        if (!MatchesReceipt(info.RootElement, entry) ||
            ParseObject(info.RootElement).ModifiedUtc.ToUnixTimeMilliseconds() != entry.ModifiedMillis ||
            OptionalString(info.RootElement, "accountId") is { } account && account != entry.AccountId)
            throw new InvalidDataException("The cloud transfer receipt belongs to another operation, source version, or timestamp. No new version was created.");
    }

    private static bool MatchesReceipt(JsonElement file, B2TransferIntentJournal.Entry entry) =>
        file.TryGetProperty("fileInfo", out var info) && info.ValueKind == JsonValueKind.Object &&
        OptionalString(info, entry.Kind == "copy" ? "cloudbay_import_id" : "cloudbay_upload_id") == entry.OperationId &&
        OptionalString(info, "cloudbay_source_id") == entry.SourceId;

    private async Task<string?> FindOwnedCopyStartAsync(B2TransferIntentJournal.Entry entry, CancellationToken token)
    {
        string? next = null; string? found = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var json = await ApiAsync("b2_list_unfinished_large_files", new
            { bucketId = entry.BucketId, namePrefix = entry.Key, startFileId = next, maxFileCount = ListPageSize }, token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
                !json.RootElement.TryGetProperty("nextFileId", out var cursorField) || cursorField.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("Backblaze returned an incomplete copy checkpoint listing.");
            foreach (var file in files.EnumerateArray())
            {
                if (OptionalString(file, "fileName") != entry.Key || !MatchesReceipt(file, entry)) continue;
                if (RequiredString(file, "bucketId") != entry.BucketId) throw new InvalidDataException("The unfinished copy bucket does not match its intent.");
                var metadata = file.GetProperty("fileInfo");
                if (OptionalString(metadata, "large_file_sha1") != entry.Sha1 ||
                    OptionalString(metadata, "src_last_modified_millis") != entry.ModifiedMillis.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("The unfinished copy metadata does not match its durable source intent.");
                var id = RequiredString(file, "fileId");
                if (found is not null && found != id) throw new InvalidDataException("Multiple unfinished files share one copy intent; no additional copy was started.");
                found = id;
            }
            var cursor = OptionalString(json.RootElement, "nextFileId");
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidDataException("Backblaze returned a repeated unfinished-copy cursor.");
            next = cursor;
        } while (next is not null);
        return found;
    }

    private async Task<string?[]> ReadCopyPartsAsync(B2TransferIntentJournal.Entry entry, CancellationToken token)
    {
        var hashes = new string?[checked((int)((entry.Length + entry.PartSize - 1) / entry.PartSize))];
        int? next = 1;
        do
        {
            using var json = await ApiAsync("b2_list_parts", new { fileId = entry.FileId, startPartNumber = next, maxPartCount = 1_000 }, token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array ||
                parts.GetArrayLength() > 1_000 || !json.RootElement.TryGetProperty("nextPartNumber", out var cursor) ||
                cursor.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                throw new InvalidDataException("Backblaze returned an incomplete copied-part listing.");
            foreach (var part in parts.EnumerateArray())
            {
                var number = checked((int)LongValue(part, "partNumber"));
                if (number < 1 || number > hashes.Length || hashes[number - 1] is not null ||
                    RequiredString(part, "fileId") != entry.FileId ||
                    LongValue(part, "contentLength") != Math.Min(entry.PartSize, entry.Length - (number - 1) * entry.PartSize) ||
                    !IsSha1(OptionalString(part, "contentSha1"))) throw new InvalidDataException("A copied part does not match its durable source layout.");
                hashes[number - 1] = RequiredString(part, "contentSha1");
            }
            var previous = next;
            next = cursor.ValueKind == JsonValueKind.Number && cursor.TryGetInt32(out var continuation) ? continuation : null;
            if (cursor.ValueKind == JsonValueKind.Number && next is null || next is not null &&
                (next <= previous || next > hashes.Length || parts.GetArrayLength() == 0)) throw new InvalidDataException("Backblaze returned an invalid copied-part cursor.");
        } while (next is not null);
        return hashes;
    }

    private static bool IsAmbiguousOutcome(Exception error) => error is HttpRequestException or IOException or JsonException ||
        error is B2RequestException request && IsTransient(request.StatusCode);
    private static IOException UnknownOutcome(string operation, Exception? inner = null) => new UnknownTransferOutcomeException(operation, inner);
    private sealed class UnknownTransferOutcomeException(string operation, Exception? inner) : IOException(
        $"The B2 {operation} outcome could not be confirmed. Its transfer intent was retained; CloudBay will check the receipt before creating another version.", inner);

    private async Task<IDisposable> LockOperationAsync(string identity, CancellationToken token)
    {
        IntentGate gate;
        lock (_intentGate)
        {
            if (!_intentLocks.TryGetValue(identity, out gate!)) _intentLocks.Add(identity, gate = new());
            gate.Users++;
        }
        try { await gate.Semaphore.WaitAsync(token).ConfigureAwait(false); }
        catch { ReleaseIntentGate(identity, gate, acquired: false); throw; }
        return new IntentLease(() => ReleaseIntentGate(identity, gate, acquired: true));
    }
    private void ReleaseIntentGate(string identity, IntentGate gate, bool acquired)
    {
        if (acquired) gate.Semaphore.Release();
        lock (_intentGate)
            if (--gate.Users == 0) { _intentLocks.Remove(identity); gate.Semaphore.Dispose(); }
    }
    private sealed class IntentGate { public int Users; public SemaphoreSlim Semaphore { get; } = new(1, 1); }
    private sealed class IntentLease(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }
}

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CloudBay.Core.B2;

public sealed partial class B2CloudStore
{
    private B2UploadJournal? _uploadJournal;
    private sealed record PreparedUpload(string AccountId, string BucketId, string Key, long Offset, long Length,
        long ModifiedTicks, long PartSize, string Sha1, string[] PartHashes);
    private readonly ConditionalWeakTable<Stream, PreparedUpload> _preparedUploads = new();
    private readonly object _preparedGate = new();

    /// <summary>
    /// Uses the shared hashing budget; the sync engine holds a writer-denying file handle.
    /// Calculate whole-file and multipart checksums together, avoiding a second disk pass.
    /// The server independently checks every transmitted part against these hashes.
    /// </summary>
    public async Task<string> PrepareUploadChecksumAsync(string bucketId, string key, Stream source, long length,
        DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ValidateAccess(bucketId, key, "writeFiles");
        ValidateKey(key);
        var start = source.CanSeek ? source.Position : 0;
        lock (_preparedGate) _preparedUploads.Remove(source);
        await TransferResources.Hashing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                if (source is not FileStream file || source.CanWrite || !source.CanSeek || length < MultipartThreshold)
                    return Convert.ToHexString(await SHA1.HashDataAsync(source, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (source.Length - start < length)
                    throw new EndOfStreamException("The upload source is shorter than its declared length.");
                var auth = Current;
                var partSize = MultipartPartSize(auth, length);
                var journal = _uploadJournal;
                var entry = journal?.Read(journal.PathFor(auth.Account.AccountId, bucketId, key, Path.GetFullPath(file.Name), start));
                if (entry is not null && entry.Length == length && entry.SourceOffset == start && entry.ModifiedTicks == modifiedUtc.UtcTicks)
                    partSize = entry.PartSize;
                using var sourceLock = new SemaphoreSlim(1, 1);
                var prepared = await PreparePartsAsync(source, sourceLock, start, length, partSize, cancellationToken).ConfigureAwait(false);
                lock (_preparedGate)
                    _preparedUploads.Add(source, new(auth.Account.AccountId, bucketId, key, start, length, modifiedUtc.UtcTicks,
                        partSize, prepared.Sha1, prepared.Hashes));
                return prepared.Sha1;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (source.CanSeek) source.Position = start; }
            finally { TransferResources.Hashing.Release(); }
        }
    }

    private static long MultipartPartSize(Authorization auth, long length)
    {
        if (length > MaxLargeFileSize) throw new ArgumentOutOfRangeException(nameof(length), "This file exceeds B2's 10 TB large-file limit.");
        var size = Math.Min(Math.Max(Math.Max(auth.MinimumPartSize, auth.RecommendedPartSize), (length + 9_999) / 10_000), length / 2);
        if (size > MaxPartSize) throw new ArgumentOutOfRangeException(nameof(length), "This file exceeds the B2 multipart size limit.");
        return size;
    }

    /// <summary>Enable durable multipart checkpoints in the client's private local state directory.</summary>
    public void ConfigureResumableUploads(string journalDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _uploadJournal = new B2UploadJournal(journalDirectory);
        _transferIntents = new B2TransferIntentJournal(Path.Combine(journalDirectory, "operations"));
    }

    /// <summary>
    /// Cancel this account's managed unfinished uploads before intentionally removing its credentials.
    /// The caller must stop and await sync workers first. A failed cancellation preserves its checkpoint.
    /// </summary>
    public async Task CancelPendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        var journal = _uploadJournal;
        if (journal is null) return;
        var accountId = Current.Account.AccountId;
        foreach (var path in journal.Paths())
        {
            await using var held = await journal.LockAsync(path, cancellationToken).ConfigureAwait(false);
            var entry = journal.Read(path);
            if (entry is null || entry.AccountId != accountId) continue;
            ValidateAccess(entry.BucketId, entry.Key, "writeFiles");
            await CancelCheckpointAsync(journal, path, entry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Bound the lifetime of checkpoints whose source was removed or inactive for seven days.</summary>
    public async Task CleanupAbandonedUploadsAsync(CancellationToken cancellationToken = default)
    {
        var journal = _uploadJournal;
        if (journal is null) return;
        var accountId = Current.Account.AccountId;
        foreach (var path in journal.Paths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var held = journal.TryLock(path);
            if (held is null) continue;
            var entry = journal.Read(path);
            if (entry is null || entry.AccountId != accountId) continue;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            if (File.Exists(entry.SourcePath) &&
                (entry.CreatedUtc >= cutoff || journal.LastActivityUtc(path) >= cutoff)) continue;
            ValidateAccess(entry.BucketId, entry.Key, "writeFiles");
            await CancelCheckpointAsync(journal, path, entry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Independently re-read the immutable completed version's size and checksum metadata.
    /// Multipart byte integrity additionally depends on the server-validated per-part hashes;
    /// B2 stores the whole-file SHA1 as client metadata for large files.
    /// </summary>
    public async Task VerifyUploadAsync(CloudObject file, string bucketId, CancellationToken cancellationToken = default)
    {
        ValidateAccess(bucketId, file.Key, "readFiles");
        using var info = await ApiAsync("b2_get_file_info", new { fileId = file.FileId }, cancellationToken).ConfigureAwait(false);
        var remote = ParseObject(info.RootElement);
        if (remote.Action != "upload" || remote.FileId != file.FileId || remote.Key != file.Key || remote.Size != file.Size ||
            !IsSha1(file.Sha1) || !file.Sha1!.Equals(remote.Sha1, StringComparison.OrdinalIgnoreCase) ||
            RequiredString(info.RootElement, "bucketId") != bucketId ||
            OptionalString(info.RootElement, "accountId") is { } returnedAccount && returnedAccount != Current.Account.AccountId)
            throw new InvalidDataException("The uploaded B2 version failed its independent size and checksum verification.");
    }

    private async Task<CloudObject> UploadLargeAsync(string bucketId, string key, Stream source, long start, long length,
        string sha1, DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress, CancellationToken token, bool durableSource)
    {
        var auth = Current;
        var partSize = MultipartPartSize(auth, length);
        var journal = durableSource && source is FileStream ? _uploadJournal : null;
        var sourcePath = source is FileStream fileSource ? Path.GetFullPath(fileSource.Name) : "";
        var path = journal?.PathFor(auth.Account.AccountId, bucketId, key, sourcePath, start);
        await using var checkpointLock = journal is not null
            ? await journal.LockAsync(path!, token).ConfigureAwait(false) : null;
        B2UploadJournal.Entry? entry = journal?.Read(path!);
        var existingCheckpoint = entry is not null;
        string? fileId = null;
        var finished = false;
        var preserve = false;
        using var sourceLock = new SemaphoreSlim(1, 1);
        using var workersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        long transferred = 0;
        var progressGate = new object();
        try
        {
            // Preserve the original part layout when authorization's recommended size changes.
            if (entry is not null && entry.Length == length && entry.SourceOffset == start &&
                entry.ModifiedTicks == modifiedUtc.UtcTicks && sha1.Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase))
                partSize = entry.PartSize;
            PreparedUpload? prepared;
            lock (_preparedGate)
            { _preparedUploads.TryGetValue(source, out prepared); _preparedUploads.Remove(source); }
            string[] hashes;
            if (source is FileStream && !source.CanWrite && prepared is not null &&
                prepared.AccountId == auth.Account.AccountId && prepared.BucketId == bucketId && prepared.Key == key &&
                prepared.Offset == start && prepared.Length == length && prepared.ModifiedTicks == modifiedUtc.UtcTicks &&
                prepared.PartSize == partSize && sha1.Equals(prepared.Sha1, StringComparison.OrdinalIgnoreCase))
                hashes = prepared.PartHashes;
            else
            {
                await TransferResources.Hashing.WaitAsync(token).ConfigureAwait(false);
                try { hashes = await HashPartsAsync(source, sourceLock, start, length, partSize, sha1, token).ConfigureAwait(false); }
                finally { TransferResources.Hashing.Release(); }
            }
            if (entry is not null && (entry.Length != length || entry.SourceOffset != start ||
                entry.ModifiedTicks != modifiedUtc.UtcTicks || !sha1.Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase) ||
                !entry.PartHashes.SequenceEqual(hashes, StringComparer.OrdinalIgnoreCase)))
            {
                await CancelCheckpointAsync(journal!, path!, entry, token).ConfigureAwait(false);
                entry = null;
                existingCheckpoint = false;
            }
            if (journal is not null && entry is null)
            {
                entry = new(1, auth.Account.AccountId, bucketId, key, sourcePath, start, length, modifiedUtc.UtcTicks,
                    partSize, sha1.ToLowerInvariant(), hashes, Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow);
                journal.Write(path!, entry); // Persist intent before any request that creates charged remote state.
            }
            var completed = new bool[hashes.Length];
            if (entry is not null)
            {
                fileId = entry.FileId ?? (existingCheckpoint ? await FindCheckpointUploadAsync(entry, token).ConfigureAwait(false) : null);
                if (fileId is not null)
                {
                    // Expiry measures inactivity, so a resumed upload does not inherit
                    // the original attempt's age and get discarded on the next restart.
                    entry = entry with { FileId = fileId, CreatedUtc = DateTimeOffset.UtcNow };
                    journal!.Write(path!, entry);
                    var recovered = await ResumePartsAsync(entry, completed, token).ConfigureAwait(false);
                    if (recovered is not null)
                    {
                        finished = true;
                        journal.Remove(path!);
                        source.Position = checked(start + length);
                        progress?.Report(new(length, length) { IsBaseline = true });
                        return recovered;
                    }
                }
            }
            if (fileId is null)
            {
                var metadata = new Dictionary<string, string>
                {
                    ["src_last_modified_millis"] = modifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    ["large_file_sha1"] = sha1.ToLowerInvariant()
                };
                if (entry is not null) metadata["cloudbay_upload_id"] = entry.UploadId;
                using var json = await ApiAsync("b2_start_large_file", new
                { bucketId, fileName = key, contentType = "b2/x-auto", fileInfo = metadata }, token,
                    retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                fileId = RequiredString(json.RootElement, "fileId");
                if (entry is not null)
                {
                    entry = entry with { FileId = fileId };
                    journal!.Write(path!, entry);
                }
            }

            for (var i = 0; i < completed.Length; i++)
                if (completed[i]) transferred += Math.Min(partSize, length - i * partSize);
            progress?.Report(new(transferred, length) { IsBaseline = true });
            var nextPart = -1;
            var workers = Enumerable.Range(0, Math.Min(hashes.Length, Volatile.Read(ref _connections))).Select(async _ =>
            {
                UploadSession? session = null;
                var entered = false;
                try
                {
                    await _uploadRequests.EnterAsync(workersCts.Token).ConfigureAwait(false);
                    entered = true;
                    while (true)
                    {
                        var index = Interlocked.Increment(ref nextPart);
                        if (index >= hashes.Length) return;
                        if (completed[index]) continue;
                        var partStart = checked(start + index * partSize);
                        var partLength = Math.Min(partSize, length - index * partSize);
                        for (var attempt = 0; ; attempt++)
                        {
                            session ??= await GetUploadSessionAsync(bucketId, fileId, workersCts.Token).ConfigureAwait(false);
                            long sentThisAttempt = 0;
                            using var request = UploadRequest(session, source, sourceLock, partStart, partLength, hashes[index], n =>
                            {
                                lock (progressGate)
                                {
                                    sentThisAttempt += n;
                                    transferred += n;
                                    progress?.Report(new(Math.Min(transferred, length), length));
                                }
                            }, workersCts.Token);
                            request.Headers.TryAddWithoutValidation("X-Bz-Part-Number", (index + 1).ToString(CultureInfo.InvariantCulture));
                            try
                            {
                                using var response = await SendAsync(request, workersCts.Token).ConfigureAwait(false);
                                if (response.IsSuccessStatusCode)
                                {
                                    using var json = await ReadDocumentAsync(response, workersCts.Token).ConfigureAwait(false);
                                    if (RequiredString(json.RootElement, "fileId") != fileId ||
                                        !RequiredString(json.RootElement, "contentSha1").Equals(hashes[index], StringComparison.OrdinalIgnoreCase) ||
                                        LongValue(json.RootElement, "contentLength") != partLength || LongValue(json.RootElement, "partNumber") != index + 1)
                                        throw new InvalidDataException("Backblaze returned an invalid multipart acknowledgment.");
                                    completed[index] = true;
                                    break;
                                }
                                var error = await ReadErrorAsync(response, workersCts.Token).ConfigureAwait(false);
                                session = null;
                                if (!IsUploadRetry(response.StatusCode, error.Code) || attempt == Attempts - 1) throw error;
                                RollBackAttempt(sentThisAttempt);
                                await BackoffAsync(response, attempt, workersCts.Token).ConfigureAwait(false);
                            }
                            catch (Exception error) when ((error is IOException and not B2RequestException || error is HttpRequestException) && attempt < Attempts - 1)
                            {
                                RollBackAttempt(sentThisAttempt);
                                session = null;
                                await BackoffAsync(null, attempt, workersCts.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch { await workersCts.CancelAsync().ConfigureAwait(false); throw; }
                finally { if (entered) _uploadRequests.Exit(); }
            }).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            var file = await FinishAsync(fileId, hashes, token).ConfigureAwait(false);
            ValidateCompletedCheckpoint(file, key, length, sha1, fileId);
            finished = true;
            if (journal is not null) journal.Remove(path!);
            source.Position = checked(start + length);
            progress?.Report(new(length, length));
            return file;
        }
        catch (Exception error)
        {
            preserve = journal is not null && entry is not null && error is not (InvalidDataException or EndOfStreamException) &&
                (token.IsCancellationRequested || error is HttpRequestException || error is IOException and not B2RequestException ||
                 error is B2RequestException request && IsTransient(request.StatusCode));
            throw;
        }
        finally
        {
            if (!finished && !preserve && (fileId is not null || entry is not null))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    if (journal is not null && entry is not null)
                        await CancelCheckpointAsync(journal, path!, entry with { FileId = fileId ?? entry.FileId }, cleanup.Token).ConfigureAwait(false);
                    else if (fileId is not null) await CancelLargeAsync(fileId, cleanup.Token).ConfigureAwait(false);
                }
                catch { ReportDiagnostic("An unfinished upload could not be removed. Its checkpoint was retained; CloudBay will retry cleanup when the connection returns."); }
            }
        }

        void RollBackAttempt(long sent)
        {
            lock (progressGate)
            {
                transferred -= sent;
                // Retransmitted bytes are real traffic, but discarded bytes
                // are no longer this file's completed progress. Publish that
                // counter reset without treating it as new network payload.
                progress?.Report(new(Math.Clamp(transferred, 0, length), length) { IsBaseline = true });
            }
        }
    }

    // One sequential disk pass builds the whole-file and immutable per-part checksums together.
    private static async Task<string[]> HashPartsAsync(Stream source, SemaphoreSlim sourceLock, long start, long length,
        long partSize, string expectedSha1, CancellationToken token)
    {
        var prepared = await PreparePartsAsync(source, sourceLock, start, length, partSize, token).ConfigureAwait(false);
        if (!prepared.Sha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The multipart upload source does not match its declared SHA1 checksum.");
        return prepared.Hashes;
    }

    private static async Task<(string Sha1, string[] Hashes)> PreparePartsAsync(Stream source, SemaphoreSlim sourceLock,
        long start, long length, long partSize, CancellationToken token)
    {
        var hashes = new string[checked((int)((length + partSize - 1) / partSize))];
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var part = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long offset = 0, inPart = 0;
        var index = 0;
        try
        {
            while (offset < length)
            {
                int count;
                await sourceLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    source.Position = checked(start + offset);
                    count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Math.Min(length - offset, partSize - inPart))), token).ConfigureAwait(false);
                }
                finally { sourceLock.Release(); }
                if (count == 0) throw new EndOfStreamException("The upload source ended before its declared length.");
                whole.AppendData(buffer, 0, count); part.AppendData(buffer, 0, count);
                offset += count; inPart += count;
                if (inPart == partSize || offset == length)
                { hashes[index++] = Convert.ToHexString(part.GetHashAndReset()).ToLowerInvariant(); inPart = 0; }
            }
            return (Convert.ToHexString(whole.GetHashAndReset()).ToLowerInvariant(), hashes);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<CloudObject?> ResumePartsAsync(B2UploadJournal.Entry entry, bool[] completed, CancellationToken token)
    {
        var next = 1;
        var last = 0;
        try
        {
            do
            {
                using var json = await ApiAsync("b2_list_parts", new { fileId = entry.FileId, startPartNumber = next, maxPartCount = 1_000 }, token).ConfigureAwait(false);
                var root = json.RootElement;
                if (!root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() > 1_000 ||
                    !root.TryGetProperty("nextPartNumber", out var cursor) || cursor.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                    throw new InvalidDataException("Backblaze returned an incomplete upload checkpoint listing.");
                foreach (var part in parts.EnumerateArray())
                {
                    var number = LongValue(part, "partNumber");
                    if (number < next || number <= last || number > completed.Length || RequiredString(part, "fileId") != entry.FileId ||
                        LongValue(part, "contentLength") != Math.Min(entry.PartSize, entry.Length - (number - 1) * entry.PartSize) ||
                        !RequiredString(part, "contentSha1").Equals(entry.PartHashes[number - 1], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A stored B2 upload part does not match the verified local file.");
                    completed[number - 1] = true; last = (int)number;
                }
                if (cursor.ValueKind == JsonValueKind.Null) break;
                if (!cursor.TryGetInt32(out var continuation) || continuation <= last || continuation <= next || continuation > completed.Length || parts.GetArrayLength() == 0)
                    throw new InvalidDataException("Backblaze returned an invalid upload checkpoint cursor.");
                next = continuation;
            } while (true);
            return null;
        }
        catch (B2RequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            // The finish may have succeeded immediately before the process was interrupted.
            using var info = await ApiAsync("b2_get_file_info", new { fileId = entry.FileId }, token).ConfigureAwait(false);
            var file = ParseObject(info.RootElement);
            ValidateCompletedCheckpoint(file, entry.Key, entry.Length, entry.Sha1, entry.FileId!);
            return file;
        }
    }

    private async Task<string?> FindCheckpointUploadAsync(B2UploadJournal.Entry entry, CancellationToken token)
    {
        string? next = null, found = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var json = await ApiAsync("b2_list_unfinished_large_files", new
            { bucketId = entry.BucketId, namePrefix = entry.Key, startFileId = next, maxFileCount = 100 }, token).ConfigureAwait(false);
            var root = json.RootElement;
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 100 ||
                !root.TryGetProperty("nextFileId", out var cursor) || cursor.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("Backblaze returned an incomplete unfinished-upload listing.");
            foreach (var file in files.EnumerateArray())
            {
                if (OptionalString(file, "fileName") != entry.Key || !file.TryGetProperty("fileInfo", out var info) ||
                    OptionalString(info, "cloudbay_upload_id") != entry.UploadId) continue;
                if (OptionalString(file, "action") != "start" || OptionalString(file, "bucketId") != entry.BucketId ||
                    !entry.Sha1.Equals(OptionalString(info, "large_file_sha1"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backblaze returned a mismatched unfinished upload identity.");
                var id = RequiredString(file, "fileId");
                if (found is not null && found != id) await CancelLargeAsync(id, token).ConfigureAwait(false);
                else found = id;
            }
            next = cursor.ValueKind == JsonValueKind.Null ? null : cursor.GetString();
            if (next is not null && (next.Length == 0 || files.GetArrayLength() == 0 || !cursors.Add(next)))
                throw new InvalidDataException("Backblaze returned an invalid unfinished-upload cursor.");
        } while (next is not null);
        return found;
    }

    private async Task CancelCheckpointAsync(B2UploadJournal journal, string path, B2UploadJournal.Entry entry, CancellationToken token)
    {
        var fileId = entry.FileId ?? await FindCheckpointUploadAsync(entry, token).ConfigureAwait(false);
        if (fileId is not null) await CancelLargeAsync(fileId, token).ConfigureAwait(false);
        journal.Remove(path);
    }

    private async Task CancelLargeAsync(string fileId, CancellationToken token)
    {
        try { using var _ = await ApiAsync("b2_cancel_large_file", new { fileId }, token).ConfigureAwait(false); }
        catch (B2RequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        catch (B2RequestException error) when (error.StatusCode == HttpStatusCode.BadRequest)
        {
            using var info = await ApiAsync("b2_get_file_info", new { fileId }, token).ConfigureAwait(false);
            if (ParseObject(info.RootElement).Action != "upload") throw;
        }
    }

    private static void ValidateCompletedCheckpoint(CloudObject file, string key, long length, string sha1, string fileId)
    {
        if (file.Action != "upload" || file.Key != key || file.FileId != fileId || file.Size != length ||
            !sha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backblaze returned a completed multipart file with unexpected metadata.");
    }
}

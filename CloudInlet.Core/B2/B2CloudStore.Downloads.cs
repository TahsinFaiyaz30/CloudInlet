using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CloudInlet.Core.B2;

public sealed partial class B2CloudStore
{
    // Buffers stay 64 KiB regardless of file/chunk size. Very large files grow their chunks
    // so a durable checkpoint journal cannot grow beyond 10,000 entries.
    public const long DownloadChunkSize = 8 * 1024 * 1024;
    private readonly ConcurrencyGate _downloads = new();
    private int _downloadConnections = 4;

    public void ConfigureDownloads(int connections)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (connections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(connections));
        Volatile.Write(ref _downloadConnections, connections);
        _downloads.Configure(connections);
        _requestBudget.ConfigureDownloadRequests(connections);
    }

    public static long GetDownloadChunkSize(long fileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        var minimum = fileSize / 10_000 + (fileSize % 10_000 == 0 ? 0 : 1);
        const long alignment = 64 * 1024;
        return Math.Max(DownloadChunkSize, checked((minimum + alignment - 1) / alignment * alignment));
    }

    public async Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The download destination must be writable.", nameof(destination));
        var count = ValidateDownloadArguments(file, offset, length);
        if (count == 0 && file.Size != 0) { progress?.Report(new(0, 0)); return; }
        var full = offset == 0 && count == file.Size;
        using var hash = full ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        var expectedSha1 = await DownloadRangeAsync(file, offset, count,
            async (data, token) =>
            {
                // Advance the resume offset and hash only after the destination accepts the bytes.
                // This includes CFAPI's non-seekable hydration stream: delivered bytes are not replayed.
                await destination.WriteAsync(data, token).ConfigureAwait(false);
                hash?.AppendData(data.Span);
            }, progress, cancellationToken).ConfigureAwait(false);
        if (full) VerifyDownloadHash(hash!.GetHashAndReset(), expectedSha1);
    }

    /// <summary>
    /// Downloads an immutable version into caller-owned staging. Completed chunks are locally
    /// rechecked before reuse; newly completed chunks are flushed before the journal callback.
    /// Native hydration uses DownloadAsync and never allocates a second copy of a placeholder.
    /// </summary>
    public async Task DownloadFileAsync(CloudObject file, FileStream destination,
        IReadOnlyList<DownloadChunk> completedChunks,
        Func<DownloadChunk, CancellationToken, Task> checkpoint,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(completedChunks);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Download staging must be a readable, writable, seekable file.", nameof(destination));
        ValidateDownloadArguments(file, 0, null);
        var chunkSize = GetDownloadChunkSize(file.Size);
        var chunkCount = checked((int)(file.Size / chunkSize + (file.Size % chunkSize == 0 ? 0 : 1)));
        var complete = new HashSet<int>();
        var offsets = new HashSet<long>();
        long downloaded = 0;
        foreach (var chunk in completedChunks)
        {
            if (chunk.Offset < 0 || chunk.Offset >= file.Size || chunk.Offset % chunkSize != 0 ||
                chunk.Length != Math.Min(chunkSize, file.Size - chunk.Offset) || !IsSha1(chunk.Sha1) ||
                !offsets.Add(chunk.Offset))
                throw new ArgumentException("A saved download chunk is outside this file's chunk layout.", nameof(completedChunks));
            if (destination.Length < chunk.Offset + chunk.Length) continue;
            var actual = await HashDownloadSegmentAsync(destination, chunk.Offset, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(chunk.Sha1, StringComparison.OrdinalIgnoreCase)) continue;
            complete.Add(checked((int)(chunk.Offset / chunkSize)));
            downloaded += chunk.Length;
        }
        // RandomAccess writes extend the file as chunks arrive. Do not reserve or zero-fill
        // the entire remote file before its first byte has been downloaded.
        if (destination.Length > file.Size) destination.SetLength(file.Size);
        progress?.Report(new(downloaded, file.Size) { IsBaseline = true });
        if (file.Size == 0)
        {
            var expected = await DownloadRangeAsync(file, 0, 0,
                (_, _) => ValueTask.CompletedTask, null, cancellationToken).ConfigureAwait(false);
            VerifyDownloadHash(SHA1.HashData([]), expected);
            destination.Flush(flushToDisk: true);
            return;
        }

        if (chunkCount == 1)
        {
            // A final one-chunk checkpoint would be immediately deleted after installation.
            // Keep tiny files out of the worker/journal pipeline while retaining one durable
            // data flush and independent verification of the bytes actually written to disk.
            string? expected = IsSha1(file.Sha1) ? file.Sha1 : null;
            if (complete.Count == 0)
            {
                destination.SetLength(0);
                long written = 0;
                expected = await DownloadRangeAsync(file, 0, file.Size, async (data, token) =>
                {
                    await RandomAccess.WriteAsync(destination.SafeFileHandle, data, written, token).ConfigureAwait(false);
                    written += data.Length;
                }, progress, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            destination.Flush(flushToDisk: true);
            await VerifyStagedDownloadAsync(destination, file.Size, expected, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var progressGate = new object();
        string? wholeSha1 = IsSha1(file.Sha1) ? file.Sha1 : null;
        using var saveGate = new SemaphoreSlim(1, 1);
        var pending = Enumerable.Range(0, chunkCount).Where(index => !complete.Contains(index));
        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = Volatile.Read(ref _downloadConnections),
            CancellationToken = cancellationToken
        }, async (index, workerToken) =>
        {
            var chunkOffset = index * chunkSize;
            var count = Math.Min(chunkSize, file.Size - chunkOffset);
            long written = 0, lastProgress = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var expected = await DownloadRangeAsync(file, chunkOffset, count,
                async (data, ct) =>
                {
                    await RandomAccess.WriteAsync(destination.SafeFileHandle, data, chunkOffset + written, ct).ConfigureAwait(false);
                    hash.AppendData(data.Span);
                    written += data.Length;
                }, progress is null ? null : new DownloadProgress(value =>
                {
                    lock (progressGate)
                    {
                        downloaded += value.Bytes - lastProgress;
                        lastProgress = value.Bytes;
                        progress.Report(new(downloaded, file.Size));
                    }
                }), workerToken).ConfigureAwait(false);
            lock (progressGate)
            {
                if (IsSha1(expected))
                {
                    if (wholeSha1 is not null && !wholeSha1.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The remote checksum changed for the selected file version.");
                    wholeSha1 = expected;
                }
            }
            var saved = new DownloadChunk(chunkOffset, count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            await saveGate.WaitAsync(workerToken).ConfigureAwait(false);
            try
            {
                // Serialize journal commits and make bytes durable before recording completion.
                destination.Flush(flushToDisk: true);
                await checkpoint(saved, workerToken).ConfigureAwait(false);
            }
            finally { saveGate.Release(); }
        }).ConfigureAwait(false);

        await VerifyStagedDownloadAsync(destination, file.Size, wholeSha1, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyStagedDownloadAsync(FileStream destination, long size, string? wholeSha1,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        // Range responses advertise the whole-file SHA1, never the range SHA1. Check the assembled
        // file once, with pooled memory and the shared disk/hash budget, before it can be installed.
        if (IsSha1(wholeSha1))
        {
            var actual = await HashDownloadSegmentAsync(destination, 0, size, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(wholeSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file failed SHA1 verification.");
        }
        else ReportDiagnostic("This B2 file has no whole-file SHA1 metadata. Its download uses authenticated HTTPS and exact byte-length verification.");
        destination.Position = size;
        progress?.Report(new(size, size));
    }

    private long ValidateDownloadArguments(CloudObject file, long offset, long? length)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateCapability("readFiles");
        ValidatePrefix(file.Key);
        if (string.IsNullOrWhiteSpace(file.FileId)) throw new ArgumentException("An immutable file version ID is required.", nameof(file));
        if (file.Action != "upload") throw new ArgumentException("Only uploaded file versions can be downloaded.", nameof(file));
        if (file.Size < 0 || offset < 0 || offset > file.Size || length is < 0 || length > file.Size - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "The requested range is outside this file.");
        return length ?? file.Size - offset;
    }

    private async Task<string?> DownloadRangeAsync(CloudObject file, long offset, long count,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        long copied = 0;
        string? expectedSha1 = IsSha1(file.Sha1) ? file.Sha1 : null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var auth = Current;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{auth.Account.DownloadUrl}/b2api/v4/b2_download_file_by_id?fileId={Uri.EscapeDataString(file.FileId)}");
            var remaining = count - copied;
            var start = checked(offset + copied);
            var full = start == 0 && remaining == file.Size;
            request.Headers.TryAddWithoutValidation("Authorization", auth.Token);
            if (!full) request.Headers.Range = new RangeHeaderValue(start, checked(start + remaining - 1));
            HttpResponseMessage? response = null;
            var reauthorize = false;
            var responseValidated = false;
            await _downloads.EnterAsync(token).ConfigureAwait(false);
            try
            {
                if (attempt == 0) progress?.Report(new(0, count) { IsBaseline = true });
                response = await SendAsync(request, token, payloadDownload: true).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadErrorAsync(response, token).ConfigureAwait(false);
                    if (IsExpired(error) && attempt < Attempts - 1) reauthorize = true;
                    else if (!IsTransient(response.StatusCode) || attempt == Attempts - 1) throw error;
                }
                else
                {
                    ValidateDownloadResponse(response, file, start, remaining, full);
                    responseValidated = true;
                    if (Header(response, "X-Bz-File-Id") != file.FileId)
                        throw new InvalidDataException("Backblaze returned no matching immutable file version.");
                    var responseSha1 = Header(response, "X-Bz-Content-Sha1");
                    if (!IsSha1(responseSha1)) responseSha1 = Header(response, "X-Bz-Info-large_file_sha1");
                    if (IsSha1(responseSha1))
                    {
                        if (expectedSha1 is not null && !expectedSha1.Equals(responseSha1, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The remote checksum changed for the selected file version.");
                        expectedSha1 = responseSha1;
                    }
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        while (copied < count)
                        {
                            var read = await ReadDownloadNetworkAsync(input,
                                buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - copied)), token).ConfigureAwait(false);
                            if (read == 0) throw new HttpRequestException("The download ended before its declared length.");
                            await _downloadLimit.WaitAsync(read, token).ConfigureAwait(false);
                            await write(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            copied += read;
                            progress?.Report(new(copied, count));
                        }
                        // HTTP enforces Content-Length on real responses; also reject malformed
                        // transports that supply additional bytes beyond the validated range.
                        if (await ReadDownloadNetworkAsync(input, buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                            throw new InvalidDataException("The download exceeded its declared length.");
                        return expectedSha1;
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
            }
            catch (HttpRequestException) when (attempt < Attempts - 1 && (!responseValidated || copied < count)) { }
            finally
            {
                _downloads.Exit();
                response?.Dispose();
            }
            if (reauthorize) await ReauthorizeAsync(auth, token).ConfigureAwait(false);
            else await BackoffAsync(response, attempt, token).ConfigureAwait(false);
        }
        throw new HttpRequestException("The download could not complete after bounded retries.");
    }

    private async ValueTask<int> ReadDownloadNetworkAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    {
        try { return await ReadTransferAsync(input, buffer, token).ConfigureAwait(false); }
        catch (IOException)
        { throw new HttpRequestException("The download connection was interrupted."); }
    }

    private void VerifyDownloadHash(byte[] actual, string? expected)
    {
        if (IsSha1(expected))
        {
            if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file failed SHA1 verification.");
        }
        else ReportDiagnostic("This B2 file has no whole-file SHA1 metadata. Its download uses authenticated HTTPS and exact byte-length verification.");
    }

    private static async Task<string> HashDownloadSegmentAsync(FileStream file, long offset, long count, CancellationToken token)
    {
        await TransferResources.Hashing.WaitAsync(token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long readTotal = 0;
            while (readTotal < count)
            {
                var read = await RandomAccess.ReadAsync(file.SafeFileHandle,
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - readTotal)), offset + readTotal, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The staged download is shorter than its recorded chunk.");
                hash.AppendData(buffer, 0, read);
                readTotal += read;
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); TransferResources.Hashing.Release(); }
    }

    private sealed class DownloadProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}

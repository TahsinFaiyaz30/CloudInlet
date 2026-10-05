namespace CloudBay.Core.Transfers;

/// <summary>
/// Source and destination primitives for transfers managed by the Windows sync engine.
/// Native orchestration continues to own placeholders, staging and atomic installation;
/// these adapters preserve the provider's connection pools and durable upload/download protocols.
/// </summary>
public static class NativeTransferAdapters
{
    /// <summary>
    /// Prepare a writer-denying local read handle. Pass this exact handle to UploadPreparedAsync:
    /// B2 attaches its multipart checksum cache to the original FileStream identity.
    /// The caller owns the handle and keeps it open through preparation and upload.
    /// </summary>
    public static async Task<string> PrepareUploadChecksumAsync(ICloudStore store, string bucketId, string key,
        FileStream source, long length, DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateSource(source, length);
        var offset = source.Position;
        try
        {
            var checksum = await store.PrepareUploadChecksumAsync(bucketId, key, source, length, modifiedUtc,
                cancellationToken).ConfigureAwait(false);
            if (!IsSha1(checksum)) throw new InvalidDataException("The provider returned an invalid local source checksum.");
            return checksum;
        }
        finally { source.Position = offset; }
    }

    /// <summary>
    /// Upload the prepared original handle without wrapping it or staging another copy.
    /// An acknowledgment is accepted only for the expected destination and content.
    /// Independent provider verification must complete before native local marking.
    /// </summary>
    public static async Task<CloudObject> UploadPreparedAsync(ICloudStore store, string bucketId, string key,
        FileStream source, long length, string sha1, DateTimeOffset modifiedUtc,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateSource(source, length);
        if (!IsSha1(sha1)) throw new InvalidDataException("The prepared local source checksum is invalid.");
        var uploaded = await store.UploadAsync(bucketId, key, source, length, sha1, modifiedUtc, progress,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded.FileId) || uploaded.Action != "upload" || uploaded.Key != key ||
            uploaded.Size != length || !sha1.Equals(uploaded.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The uploaded file identity, size, or checksum does not match the locked local source.");
        return uploaded;
    }

    /// <summary>Share the verification budget with local and cloud relay destination adapters.</summary>
    public static async Task VerifyUploadAsync(ICloudStore store, CloudObject file, string bucketId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(file);
        await TransferResources.Verification.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await store.VerifyUploadAsync(file, bucketId, cancellationToken).ConfigureAwait(false); }
        finally { TransferResources.Verification.Release(); }
    }

    /// <summary>
    /// Explicit cloud-to-local destination: preserve the original staging handle and range receipts.
    /// The provider revalidates saved ranges and flushes new ranges before calling checkpoint.
    /// This adapter is never used for a cloud destination.
    /// </summary>
    public static async Task DownloadFileAsync(ICloudStore store, CloudObject file, FileStream destination,
        IReadOnlyList<DownloadChunk> completedChunks, Func<DownloadChunk, CancellationToken, Task> checkpoint,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(completedChunks);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Download staging must be a readable, writable, seekable file.", nameof(destination));
        if (file.Size < 0) throw new InvalidDataException("The cloud source has an invalid length.");
        await store.DownloadFileAsync(file, destination, completedChunks, checkpoint, progress,
            cancellationToken).ConfigureAwait(false);
        if (destination.Length != file.Size)
            throw new InvalidDataException("The downloaded file length does not match its cloud version.");
    }

    private static void ValidateSource(FileStream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!source.CanRead || source.CanWrite || !source.CanSeek)
            throw new ArgumentException("A prepared local upload requires the original read-only, seekable file handle.", nameof(source));
        if (source.Position > source.Length || length != source.Length - source.Position)
            throw new InvalidDataException("The local upload length does not match the locked source range.");
    }

    private static bool IsSha1(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
}

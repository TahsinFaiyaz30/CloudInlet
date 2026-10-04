namespace CloudBay.Core;

public interface ICloudStore : IDisposable
{
    Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix, CancellationToken cancellationToken = default);
    /// <summary>Enumerates a complete snapshot of current objects. Failed or cancelled enumeration must not be used to infer deletions.</summary>
    IAsyncEnumerable<CloudObject> ListCurrentAsync(string bucketId, string prefix, CancellationToken cancellationToken = default) =>
        ListAsync(bucketId, prefix, cancellationToken);
    Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1,
        DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Providers can compute multipart and whole-file hashes together while the caller holds a stable read handle.</summary>
    async Task<string> PrepareUploadChecksumAsync(string bucketId, string key, Stream source, long length,
        DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default)
    {
        await TransferResources.Hashing.WaitAsync(cancellationToken);
        var position = source.Position;
        try
        {
            return await Task.Run(async () => Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(source, cancellationToken))
                .ToLowerInvariant(), cancellationToken);
        }
        finally { source.Position = position; TransferResources.Hashing.Release(); }
    }
    /// <summary>Independently confirms an upload's immutable version metadata before local data is marked clean.</summary>
    Task VerifyUploadAsync(CloudObject file, string bucketId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Downloads an immutable version into private staging; checkpoints are committed only after a durable flush.</summary>
    async Task DownloadFileAsync(CloudObject file, FileStream destination, IReadOnlyList<DownloadChunk> completedChunks,
        Func<DownloadChunk, CancellationToken, Task> checkpoint, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        destination.Position = 0;
        destination.SetLength(0);
        await DownloadAsync(file, destination, progress: progress, cancellationToken: cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(true);
        if (destination.Length != file.Size) throw new InvalidDataException("The downloaded file length does not match its cloud version.");
        if (file.Sha1 is { Length: 40 } sha1 && sha1.All(Uri.IsHexDigit))
        {
            await TransferResources.Hashing.WaitAsync(cancellationToken);
            try
            {
                destination.Position = 0;
                var hash = await System.Security.Cryptography.SHA1.HashDataAsync(destination, cancellationToken);
                if (!Convert.ToHexString(hash).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded file failed its cloud checksum verification.");
            }
            finally { TransferResources.Hashing.Release(); }
        }
    }
    Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default);
    Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default);
    /// <summary>Copies an immutable source into a reviewed destination, reconciling a stable operation receipt after interruption.</summary>
    Task<CloudObject> CopyToAsync(string destinationBucketId, string destinationKey, CloudObject source,
        string operationId, CancellationToken cancellationToken = default) =>
        Task.FromException<CloudObject>(new NotSupportedException("Direct cloud import is unavailable for this provider."));
    /// <summary>Read-only validation of a completed import receipt and its immutable source identity.</summary>
    Task VerifyCopyAsync(string destinationBucketId, string destinationKey, CloudObject source,
        string operationId, CloudObject existing, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Direct cloud import receipt verification is unavailable for this provider."));
}

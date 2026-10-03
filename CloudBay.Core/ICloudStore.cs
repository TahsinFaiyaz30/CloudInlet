namespace CloudBay.Core;

public interface ICloudStore : IDisposable
{
    Task<CloudAccount> ConnectAsync(B2Credentials credentials, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudBucket>> ListBucketsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<CloudObject> ListAsync(string bucketId, string prefix, CancellationToken cancellationToken = default);
    Task<CloudObject> UploadAsync(string bucketId, string key, Stream source, long length, string sha1,
        DateTimeOffset modifiedUtc, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadAsync(CloudObject file, Stream destination, long offset = 0, long? length = null,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task HideAsync(string bucketId, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudObject>> VersionsAsync(string bucketId, string key, CancellationToken cancellationToken = default);
    Task<CloudObject> RestoreAsync(string bucketId, CloudObject version, CancellationToken cancellationToken = default);
}

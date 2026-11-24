namespace CloudInlet.Core;

public sealed record B2Credentials(string KeyId, string ApplicationKey);
public sealed record CloudAccount(string AccountId, string ApiUrl, string DownloadUrl,
    IReadOnlyList<string> Capabilities, string? AllowedBucketId = null, string? AllowedNamePrefix = null);
public sealed record CloudBucket(string Id, string Name);
public sealed record CloudObject(string FileId, string Key, long Size, string? Sha1,
    DateTimeOffset ModifiedUtc, string Action = "upload");
public sealed record TransferProgress(long Bytes, long TotalBytes)
{
    /// <summary>Bytes already confirmed before this request; they are not new network traffic.</summary>
    public bool IsBaseline { get; init; }
}
public sealed record DownloadChunk(long Offset, long Length, string Sha1);

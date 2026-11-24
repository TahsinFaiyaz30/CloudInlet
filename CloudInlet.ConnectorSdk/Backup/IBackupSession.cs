using CloudInlet.Core;
using CloudInlet.Core.B2;
using CloudInlet.Core.Migration;

namespace CloudInlet.Connectors;

/// <summary>Optional contract for the published versioned B2 backup and migration workflows.</summary>
public interface IBackupSession : ICloudStore, IConnectorSession
{
    event EventHandler<string>? Diagnostic;
    Task<(CloudAccount Account, CloudBucket Bucket)> ConnectBackupAsync(B2Credentials credentials, string bucketName,
        string prefix, CancellationToken cancellationToken);
    void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond, int uploadConnections, int downloadConnections);
    void ConfigureResumableUploads(string journalDirectory);
    Task CleanupAbandonedUploadsAsync(CancellationToken cancellationToken = default);
    ICloudPrefixMigrationStore CreatePrefixMigrationStore(string bucketId, string sourcePrefix,
        string destinationPrefix, IB2CloudPrefixCopyJournal journal, IReadOnlyList<CloudPrefixRule>? rules = null);
}

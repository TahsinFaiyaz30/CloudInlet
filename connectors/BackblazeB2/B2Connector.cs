using CloudInlet.Connectors;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Core.B2;

public sealed class B2Connector : IProviderConnector
{
    public string Id => "b2";
    public ConnectorCapabilities Capabilities => ConnectorCapabilities.Browse | ConnectorCapabilities.Read |
        ConnectorCapabilities.Write | ConnectorCapabilities.DeleteExactVersion | ConnectorCapabilities.Verify |
        ConnectorCapabilities.ResumableUpload | ConnectorCapabilities.ObjectKeys | ConnectorCapabilities.Checksums |
        ConnectorCapabilities.ServerCopy | ConnectorCapabilities.FolderBackup;
    public IConnectorSession Open(ConnectorContext context) => new B2CloudStore(context.CreateHttpHandler?.Invoke(), bandwidthBudget: context.Bandwidth);
}

public sealed partial class B2CloudStore : IBackupSession
{
    public async Task<(CloudAccount Account, CloudBucket Bucket)> ConnectBackupAsync(B2Credentials credentials, string bucketName,
        string prefix, CancellationToken ct)
    {
        var account = await ConnectAsync(credentials, ct);
        var required = new[] { "listFiles", "readFiles", "writeFiles" };
        if (required.Any(capability => !account.Capabilities.Contains(capability)))
            throw new IOException("This application key needs listFiles, readFiles, and writeFiles access to the selected B2 bucket.");
        var bucket = (await ListBucketsAsync(ct)).SingleOrDefault(b => b.Name == bucketName)
            ?? throw new IOException("The application key cannot access that bucket. Check its bucket restriction and List All Bucket Names permission.");
        if (account.AllowedNamePrefix is { Length: > 0 } allowed && !prefix.StartsWith(allowed, StringComparison.Ordinal))
            throw new IOException("The selected cloud folder is outside the application key's permitted file prefix.");
        return (account, bucket);
    }
    public async Task<IReadOnlyList<TransferLocation>> GetRootsAsync(string accountId, CancellationToken token) =>
        (await ListBucketsAsync(token)).Select(b => new TransferLocation("b2", accountId, b.Id, "", "", b.Name)).ToArray();
    public ITransferEndpoint OpenEndpoint(TransferLocation location) => new B2TransferEndpoint(this, location);
}

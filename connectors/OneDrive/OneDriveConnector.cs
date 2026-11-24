using System.Text.Json;
using CloudInlet.Connectors;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Core.OneDrive;

public sealed class OneDriveConnector : IOneDriveConnector
{
    public string Id => "onedrive";
    public ConnectorCapabilities Capabilities => ConnectorCapabilities.Browse | ConnectorCapabilities.Read |
        ConnectorCapabilities.Write | ConnectorCapabilities.DeleteExactVersion | ConnectorCapabilities.Verify |
        ConnectorCapabilities.ResumableUpload | ConnectorCapabilities.HierarchicalFolders | ConnectorCapabilities.Checksums;
    public Task<OneDriveDeviceCode> BeginSignInAsync(string clientId, string tenant, CancellationToken token) =>
        new OneDriveAuthClient(clientId, tenant).BeginDeviceSignInAsync(token);
    public Task<OneDriveTokenSet> CompleteSignInAsync(string clientId, string tenant, OneDriveDeviceCode code, CancellationToken token) =>
        new OneDriveAuthClient(clientId, tenant).CompleteDeviceSignInAsync(code, token);
    public IConnectorSession Open(ConnectorContext context)
    {
        var tokens = JsonSerializer.Deserialize<OneDriveTokenSet>(context.Credential ?? "") ??
            throw new OneDriveSignInRequiredException("Reconnect the selected OneDrive account.");
        var http = context.CreateHttpHandler is { } create ? new HttpClient(create()) : null;
        var auth = new OneDriveAuthClient(context.Options!["clientId"], context.Options["tenant"], tokens,
            context.SaveCredential is null ? null : (next, ct) => context.SaveCredential(JsonSerializer.Serialize(next), ct), http);
        return new OneDriveClient(auth, http, bandwidthBudget: context.Bandwidth);
    }
}

public sealed partial class OneDriveClient : IOneDriveSession
{
    public async Task<IReadOnlyList<TransferLocation>> GetRootsAsync(string accountId, CancellationToken token)
    {
        var result = new List<TransferLocation>();
        foreach (var drive in await ListDrivesAsync(token))
        {
            var root = await GetRootAsync(drive.Id, token);
            result.Add(new("onedrive", accountId, drive.Id, root.Id, "", drive.Name));
        }
        return result;
    }
    public ITransferEndpoint OpenEndpoint(TransferLocation location) => new OneDriveTransferEndpoint(this, location);
}

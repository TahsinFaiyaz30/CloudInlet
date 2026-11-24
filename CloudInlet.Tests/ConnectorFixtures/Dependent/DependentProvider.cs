using CloudInlet.Connectors;
using CloudInlet.Core.Transfers;

namespace Fixture.Storage;

public sealed class DependentProvider : IProviderConnector, IConnectorSession, IConnectorAccountSetup
{
    public string Id => "fixture.dependent";
    public ConnectorCapabilities Capabilities => ConnectorCapabilities.Browse | ConnectorCapabilities.AccountSetup;
    public IReadOnlyList<ConnectorField> Fields => [new("key", "Credential", true)];
    public Task<ConnectorAccount> ConnectAsync(IReadOnlyDictionary<string, string> values, CancellationToken token) =>
        Task.FromResult(new ConnectorAccount("test-account", "Fixture account", values["key"]));
    public IConnectorSession Open(ConnectorContext context) => this;
    public Task<IReadOnlyList<TransferLocation>> GetRootsAsync(string accountId, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<TransferLocation>>([new(Id, accountId, "container", "root", "", new StorageComponent().RootName())]);
    public ITransferEndpoint OpenEndpoint(TransferLocation location) => throw new NotSupportedException();
}

using CloudInlet.Core;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Connectors;

[Flags]
public enum ConnectorCapabilities
{
    None = 0, Browse = 1, Read = 2, Write = 4, DeleteExactVersion = 8, Verify = 16,
    ResumableUpload = 32, ServerCopy = 64, FolderBackup = 128, ObjectKeys = 256,
    HierarchicalFolders = 512, Checksums = 1024, AccountSetup = 2048
}

/// <summary>One account's protected credential and settings. The host owns persistence and aggregate limits.</summary>
public sealed record ConnectorContext(string AccountId, TransferBandwidthBudget Bandwidth, string? Credential = null,
    Func<string, CancellationToken, Task>? SaveCredential = null, IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Optional host-supplied transport for isolated tests, proxies or diagnostics.</summary>
    public Func<HttpMessageHandler>? CreateHttpHandler { get; init; }
}

public interface IProviderConnector
{
    string Id { get; }
    ConnectorCapabilities Capabilities { get; }
    IConnectorSession Open(ConnectorContext context);
}

public interface IConnectorSession
{
    Task<IReadOnlyList<TransferLocation>> GetRootsAsync(string accountId, CancellationToken cancellationToken);
    ITransferEndpoint OpenEndpoint(TransferLocation location);
}

/// <summary>A component exports a contract agreed with its dependent. Components have no accounts or transfer endpoints.</summary>
public interface IConnectorComponent { }

public sealed record ConnectorField(string Id, string Label, bool Secret = false);
public sealed record ConnectorAccount(string Id, string Name, string Credential);

/// <summary>Optional interactive account setup. The host renders the fields and protects the resulting opaque credential.</summary>
public interface IConnectorAccountSetup
{
    IReadOnlyList<ConnectorField> Fields { get; }
    Task<ConnectorAccount> ConnectAsync(IReadOnlyDictionary<string, string> values, CancellationToken token);
}

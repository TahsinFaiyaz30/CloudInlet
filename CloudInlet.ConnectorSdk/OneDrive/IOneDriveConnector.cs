using CloudInlet.Core.OneDrive;

namespace CloudInlet.Connectors;

public interface IOneDriveConnector : IProviderConnector
{
    Task<OneDriveDeviceCode> BeginSignInAsync(string clientId, string tenant, CancellationToken token);
    Task<OneDriveTokenSet> CompleteSignInAsync(string clientId, string tenant, OneDriveDeviceCode code, CancellationToken token);
}

public interface IOneDriveSession : IConnectorSession
{
    void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond, int uploads, int downloads);
    Task<IReadOnlyList<OneDriveDrive>> ListDrivesAsync(CancellationToken cancellationToken = default);
    Task<OneDriveItem> GetRootAsync(string driveId, CancellationToken cancellationToken = default);
}

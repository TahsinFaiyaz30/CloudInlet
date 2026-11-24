using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CloudInlet.Core;
using CloudInlet.Core.OneDrive;
using CloudInlet.Connectors;
using System.Text.Json;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Application;

public sealed partial class ClientController
{
    private readonly ConcurrentDictionary<string, IOneDriveSession> _oneDriveClients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _oneDriveVaultGate = new(1, 1);
    public IReadOnlyList<ClientStorage.OneDriveConnection> OneDriveAccounts => _storage.LoadOneDriveConnections();

    private IOneDriveConnector OneDriveConnector => (IOneDriveConnector)Connectors.Provider("onedrive");

    public Task<OneDriveDeviceCode> BeginOneDriveSignInAsync(string clientId, string tenant = "common", CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(clientId.Trim(), out _)) throw new ArgumentException("Enter the Microsoft application (client) ID for CloudInlet's delegated OneDrive connection.");
        return OneDriveConnector.BeginSignInAsync(clientId.Trim(), tenant.Trim(), cancellationToken);
    }

    public async Task<ClientStorage.OneDriveConnection> CompleteOneDriveSignInAsync(string clientId, string tenant,
        OneDriveDeviceCode code, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var tokens = await OneDriveConnector.CompleteSignInAsync(clientId.Trim(), tenant.Trim(), code, operation.Token);
        var client = OpenOneDrive("", clientId.Trim(), tenant.Trim(), tokens);
        ConfigureOneDriveTransport(client, Settings);
        var drives = await client.ListDrivesAsync(operation.Token);
        var drive = drives.FirstOrDefault() ?? throw new IOException("This Microsoft account has no accessible OneDrive. Set up OneDrive and sign in again.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId.Trim() + "|" + drive.Id)))[..24];
        var profile = new ClientStorage.OneDriveConnection(id, string.IsNullOrWhiteSpace(drive.Owner) ? drive.Name : drive.Owner,
            clientId.Trim(), tenant.Trim(), tokens);
        await _oneDriveVaultGate.WaitAsync(operation.Token);
        try
        {
            var accounts = _storage.LoadOneDriveConnections().Where(item => item.Id != id).Append(profile).ToArray();
            _storage.SaveOneDriveConnections(accounts);
            _oneDriveClients.TryRemove(id, out _);
        }
        finally { _oneDriveVaultGate.Release(); }
        AddActivity(new(DateTimeOffset.UtcNow, Core.ActivityKind.Information, "", "Connected OneDrive · " + profile.Name));
        NotifyChanged();
        return profile;
    }

    private IOneDriveSession GetOneDriveClient(string accountId)
    {
        if (Connectors.Issue("onedrive") is { } issue) throw new Core.Connectors.ConnectorUnavailableException(issue);
        return _oneDriveClients.GetOrAdd(accountId, id =>
        {
            var account = _storage.LoadOneDriveConnections().SingleOrDefault(item => item.Id == id)
                ?? throw new IOException("Reconnect the original OneDrive account to continue this transfer.");
            var client = OpenOneDrive(id, account.ClientId, account.Tenant, account.Tokens,
                async (tokens, ct) =>
                {
                    await _oneDriveVaultGate.WaitAsync(ct);
                    try
                    {
                        var accounts = _storage.LoadOneDriveConnections().Select(item => item.Id == id ? item with { Tokens = tokens } : item).ToArray();
                        if (!accounts.Any(item => item.Id == id)) throw new IOException("This OneDrive account was removed while refreshing its session.");
                        _storage.SaveOneDriveConnections(accounts);
                    }
                    finally { _oneDriveVaultGate.Release(); }
                });
            ConfigureOneDriveTransport(client, Settings);
            return client;
        });
    }

    private IOneDriveSession OpenOneDrive(string id, string clientId, string tenant, OneDriveTokenSet tokens,
        Func<OneDriveTokenSet, CancellationToken, Task>? save = null) => (IOneDriveSession)OneDriveConnector.Open(
        new(id, _transferBandwidth, JsonSerializer.Serialize(tokens), save is null ? null :
            (value, ct) => save(JsonSerializer.Deserialize<OneDriveTokenSet>(value)!, ct),
            new Dictionary<string, string> { ["clientId"] = clientId, ["tenant"] = tenant }));

    private static void ConfigureOneDriveTransport(IOneDriveSession client, AppSettings settings)
    {
        var limits = TransferLimits.For(settings);
        client.Configure(settings.UploadBytesPerSecond, settings.DownloadBytesPerSecond, limits.Uploads, limits.Downloads);
    }

    public Task<IReadOnlyList<OneDriveDrive>> GetOneDriveDrivesAsync(string accountId, CancellationToken cancellationToken = default) =>
        GetOneDriveClient(accountId).ListDrivesAsync(cancellationToken);

    public async Task<TransferLocation> GetOneDriveRootAsync(string accountId, string driveId, CancellationToken cancellationToken = default)
    {
        var root = await GetOneDriveClient(accountId).GetRootAsync(driveId, cancellationToken);
        var account = OneDriveAccounts.Single(item => item.Id == accountId);
        return new("onedrive", accountId, driveId, root.Id, "", "OneDrive · " + account.Name);
    }

    public TransferLocation GetB2TransferRoot(string bucketId, string bucketName, string prefix = "") =>
        new("b2", Settings.AccountId, bucketId, "", Core.Sync.PathRules.NormalizePrefix(prefix), "Backblaze B2 · " + bucketName);

    public TransferLocation GetBackupTransferLocation(string name)
    {
        var folder = Settings.Backups.Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return GetB2TransferRoot(Settings.BucketId, Settings.BucketName,
            Settings.Prefix + Path.GetRelativePath(Settings.RootPath, folder.DestinationPath).Replace('\\', '/') + "/");
    }
}

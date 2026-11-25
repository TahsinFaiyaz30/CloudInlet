using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Connectors;
using CloudInlet.Core;
using CloudInlet.Core.Connectors;
using CloudInlet.Core.Transfers;
using CloudInlet.Windows;

namespace CloudInlet.Application;

public sealed partial class ClientController
{
    public ConnectorManager Connectors { get; }

    internal static string ProfileConnectorDirectory => UpdateInstallation.IsPackaged
            ? Path.Combine(global::Windows.Storage.ApplicationData.Current.LocalFolder.Path, "ProviderPacks", BuildInfo.Flavor)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudInlet", "ProviderPacks", BuildInfo.Flavor);
    internal static ConnectorManager ProfileConnectorManager => ProfileConnectors.Value;
    private static readonly Lazy<ConnectorManager> ProfileConnectors = new(() => CreateManager(ProfileConnectorDirectory));

    internal static ConnectorManager OpenConnectors(string state)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var subpath = BuildInfo.Flavor == "Debug" ? Path.Combine("Debug", "Client") : "Client";
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state));
        if (new[] { "CloudBay", "CloudInlet" }.Any(product =>
            path.Equals(Path.Combine(local, product, subpath), StringComparison.OrdinalIgnoreCase))) return ProfileConnectors.Value;
        return CreateManager(Path.Combine(path, "Connectors"));
    }

    private static ConnectorManager CreateManager(string folder)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "connector-source.json");
        return new(folder, File.Exists(source) ? ConnectorFormat.Parse<ConnectorSource>(File.ReadAllBytes(source)) : null);
    }

    internal static IBackupSession OpenBackup(ConnectorManager connectors, TransferBandwidthBudget? bandwidth = null) =>
        connectors.Provider("b2").Open(new("", bandwidth ?? new())) as IBackupSession ??
        throw new InvalidDataException("The installed Backblaze connector lacks the folder backup contract.");

    public string[] RequiredConnectors(params string[] additional)
    {
        var ids = new HashSet<string>(additional);
        if (Settings.IsConfigured || File.Exists(Path.Combine(_storage.DirectoryPath, "credentials.dpapi"))) ids.Add("b2");
        try { if (OneDriveAccounts.Count > 0) ids.Add("onedrive"); }
        catch (Exception e) when (e is CryptographicException or IOException or JsonException)
        { ids.Add("onedrive"); }
        var custom = Path.Combine(_storage.DirectoryPath, "ConnectorAccounts");
        if (Directory.Exists(custom))
            foreach (var file in Directory.EnumerateFiles(custom, "*.dpapi")) ids.Add(Path.GetFileNameWithoutExtension(file));
        foreach (var job in CloudTransferJobs.Where(j => j.State is not (TransferJobState.Completed or TransferJobState.Cancelled)))
        { ids.Add(job.Plan.Source.Provider); ids.Add(job.Plan.Destination.Provider); }
        if (_storage.LoadCloudBackupStopIntent() is { } stop)
        { ids.Add(stop.Plan.Source.Provider); ids.Add(stop.Plan.Destination.Provider); }
        return ids.Where(id => id != "local" && Connectors.Issue(id) is not null).Order().ToArray();
    }

    public string? ConnectorInUse(string id)
    {
        if (id == "b2" && (_operations.CurrentCount == 0 || _cloudTransferProviderGate.CurrentCount == 0))
            return "Wait for the current connection or folder operation to finish.";
        if (id == "b2" && (_cloud is not null || _placeholders is not null || Settings.IsConfigured && HasRegisteredBackupRoot() ||
            Settings.Backups.Count > 0 || Settings.CustomBackups.Count > 0))
            return "This connector owns a configured Windows sync root. Review the folder backup and disconnect the root before removing or disabling its connector.";
        if (_cloudTransferRuns.Keys.Any(key => CloudTransferJobs.Any(j => j.Plan.Id == key &&
            (j.Plan.Source.Provider == id || j.Plan.Destination.Provider == id))))
            return "Pause the transfers using this connector before changing it.";
        return null;
    }
    private bool HasRegisteredBackupRoot()
    {
        if (Settings.RootBindings.Count > 0) return true;
        var identity = Settings.AccountId + ":" + Settings.BucketId + ":" + Settings.Prefix;
        var registration = Windows.CloudFiles.WindowsPlaceholderService.GetRegistrationId(identity,
            Core.Sync.SyncRootMigrationIdentity.IsCanonical(Settings, Settings.RootPath));
        return global::Windows.Storage.Provider.StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(root =>
            root.Id == registration || Path.GetFullPath(root.Path.Path).Equals(Path.GetFullPath(Settings.RootPath), StringComparison.OrdinalIgnoreCase));
    }
    public void ChangeConnector(string id, string action)
    {
        Connectors.Change(id, action, ConnectorInUse);
        NotifyChanged();
    }
    private void CheckTransferConnectors(TransferLocation source, TransferLocation destination, TransferOperation operation)
    {
        var missing = new[] { source.Provider, destination.Provider }.Distinct().Where(id => id != "local" && Connectors.Issue(id) is not null).ToArray();
        if (missing.Length > 0) throw new ConnectorUnavailableException("Install required connectors: " + string.Join(", ", missing) + ".");
        void Require(string id, ConnectorCapabilities flags)
        {
            if (id != "local" && !Connectors.Provider(id).Capabilities.HasFlag(flags))
                throw new InvalidOperationException($"{id} does not support the requested operation ({flags}).");
        }
        Require(source.Provider, ConnectorCapabilities.Read | (operation == TransferOperation.Move ? ConnectorCapabilities.DeleteExactVersion : 0));
        Require(destination.Provider, ConnectorCapabilities.Write | ConnectorCapabilities.Verify);
    }

    private DateTimeOffset _nextConnectorCheck;
    public string[] AvailableConnectorUpdates(bool automatic)
    {
        var state = Connectors.Snapshot;
        var ids = new List<string>();
        foreach (var (id, installed) in state.Installed)
        {
            if (!installed.Enabled || installed.Current.Source == ConnectorManager.Local || automatic && !installed.Automatic) continue;
            try
            {
                var plan = Connectors.Plan([id], update: true);
                if (plan.Ordered.Any(o => o.Manifest.Id == id && o.Hash != installed.Current.Hash) &&
                    (!automatic || plan.Ordered.All(o => state.Installed.TryGetValue(o.Manifest.Id, out var i) && i.Enabled && i.Automatic)))
                    ids.Add(id);
            }
            catch (InvalidDataException) { /* Individual incompatible versions remain visible for manual review. */ }
        }
        return ids.ToArray();
    }

    public async Task CheckAutomaticConnectorsAsync(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow < _nextConnectorCheck) return;
        var state = Connectors.Snapshot;
        if (!state.Automatic || !state.Installed.Values.Any(i => i.Enabled && i.Automatic && i.Current.Source != ConnectorManager.Local)) return;
        _nextConnectorCheck = DateTimeOffset.UtcNow.AddHours(6);
        try
        {
            await Connectors.RefreshAsync(token);
            var ids = AvailableConnectorUpdates(true);
            if (ids.Length > 0)
            {
                var plan = Connectors.Plan(ids, update: true);
                var delivery = await StoreConnectorDelivery.ReviewAsync(plan, 0, token);
                var files = await delivery.AcquireAsync(plan, false, token);
                await Connectors.InstallAsync(plan, token, automatic: true, storeFiles: files);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, "", "Connector update needs attention: " + e.Message)); }
    }

    private readonly object _connectorVaultGate = new();
    private string VaultPath(string id)
    {
        if (!ConnectorFormat.ValidId(id)) throw new InvalidDataException("Invalid connector account identity.");
        return Path.Combine(_storage.DirectoryPath, "ConnectorAccounts", id + ".dpapi");
    }
    private static byte[] AccountEntropy(string id) => Encoding.UTF8.GetBytes("CloudInlet.ConnectorAccounts.v1|" + id);
    private ConnectorAccount[] ReadAccounts(string id)
    {
        var path = VaultPath(id);
        if (!File.Exists(path)) return [];
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), AccountEntropy(id), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<ConnectorAccount[]>(bytes) ?? throw new InvalidDataException("Connector accounts could not be read."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void SaveAccount(string id, ConnectorAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Id) || account.Id.Length > 1024 || string.IsNullOrWhiteSpace(account.Name) || account.Credential is null)
            throw new InvalidDataException("The connector returned incomplete account information.");
        lock (_connectorVaultGate)
        {
            var accounts = ReadAccounts(id).Where(a => a.Id != account.Id).Append(account).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(accounts);
            try
            {
                var path = VaultPath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                ClientStorage.AtomicWrite(path, ProtectedData.Protect(bytes, AccountEntropy(id), DataProtectionScope.CurrentUser));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public IReadOnlyList<(string Id, string Name)> CustomAccounts(string id)
    {
        lock (_connectorVaultGate) return ReadAccounts(id).Select(a => (a.Id, a.Name)).ToArray();
    }
    public async Task ConnectCustomAccountAsync(string id, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        var setup = (IConnectorAccountSetup)Connectors.Provider(id);
        var account = await setup.ConnectAsync(values, token);
        token.ThrowIfCancellationRequested();
        SaveAccount(id, account);
        NotifyChanged();
    }
    private IConnectorSession OpenCustomSession(string id, string account)
    {
        var connector = Connectors.Provider(id);
        ConnectorAccount? saved;
        lock (_connectorVaultGate) saved = ReadAccounts(id).SingleOrDefault(a => a.Id == account);
        if (saved is null && (connector.Capabilities.HasFlag(ConnectorCapabilities.AccountSetup) || account != "default"))
            throw new IOException("Reconnect the original account before resuming this transfer.");
        return connector.Open(new(account, _transferBandwidth, saved?.Credential, (value, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            SaveAccount(id, new(account, saved?.Name ?? account, value));
            return Task.CompletedTask;
        }));
    }
    public async Task<IReadOnlyList<TransferLocation>> CustomRootsAsync(string id, string account, CancellationToken ct)
    {
        var roots = await OpenCustomSession(id, account).GetRootsAsync(account, ct);
        foreach (var root in roots)
        {
            TransferValidation.ValidateLocation(root);
            if (root.Provider != id || root.AccountId != account) throw new InvalidDataException("The connector returned a root for another account.");
        }
        return roots;
    }
}

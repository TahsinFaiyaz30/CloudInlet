using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Core.Sync;
using CloudBay.Windows;
using CloudBay.Windows.CloudFiles;
using Microsoft.Win32;

namespace CloudBay.Application;

/// <summary>Only supplied fields change. Account identity and backup ownership are managed by their lifecycle operations.</summary>
public sealed record PreferenceUpdate
{
    public bool? StartAtSignIn { get; init; }
    public bool? FilesOnDemand { get; init; }
    public bool? PauseOnMetered { get; init; }
    public bool? PauseOnBatterySaver { get; init; }
    public int? UploadConcurrency { get; init; }
    public int? DownloadConcurrency { get; init; }
    public UploadMode? UploadMode { get; init; }
    public long? UploadBytesPerSecond { get; init; }
    public long? DownloadBytesPerSecond { get; init; }
    public int? PollSeconds { get; init; }
    public string? Theme { get; init; }
    public IReadOnlyList<string>? Exclusions { get; init; }
    public IReadOnlyList<SelectedExclusion>? SelectedExclusions { get; init; }
    public IReadOnlyList<GuidedExclusion>? GuidedExclusions { get; init; }
    public IReadOnlyList<string>? DisabledLegacyExclusions { get; init; }
}

public sealed partial class ClientController : IAsyncDisposable
{
    private readonly ClientStorage _storage;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Action<string> _unregisterSyncRoot;
    private readonly bool _manageStartup;
    private readonly CancellationTokenSource _lifetime = new();
    private B2CloudStore? _cloud;
    private WindowsPlaceholderService? _placeholders;
    private SyncEngine? _engine;
    private readonly Task _reconnectLoop;
    private readonly Task _transferPulse;
    private CancellationTokenSource? _transferMaintenanceCancellation;
    private Task? _transferMaintenance;
    private bool _disconnecting;
    private volatile bool _maintenance;
    private readonly ConcurrentDictionary<string, CustomRuntime> _customRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SyncSnapshot> _customSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TransferSnapshot> _hydrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransferSpeedMeter> _hydrationSpeeds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _hydrationFinalizations = new(StringComparer.Ordinal);
    private readonly TransferSpeedMeter _nativeDownloadSpeed = new();
    private long _nativeDownloadPayload;
    private SyncSnapshot _rootSnapshot = new(ClientState.NotConnected, "Connect a Backblaze B2 bucket to get started");
    private readonly object _changedGate = new();
    private readonly object _aggregateGate = new();
    private string? _settingsRecoveryError;
    private long _lastChange;
    private long _manualPauseUntilTicks;
    public AppSettings Settings { get; private set; }
    public SyncSnapshot Snapshot { get; private set; } = new(ClientState.NotConnected, "Connect a Backblaze B2 bucket to get started");
    public IReadOnlyList<ActivityEvent> Activity => _storage.Activity;
    public string DiagnosticsPath => _storage.DiagnosticsPath;
    public event EventHandler? Changed;

    public ClientController(ClientStorage? storage = null, Action<string>? unregisterSyncRoot = null, bool manageStartup = true)
    {
        _storage = storage ?? new();
        _unregisterSyncRoot = unregisterSyncRoot ?? global::Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister;
        _manageStartup = manageStartup;
        try { Settings = _storage.LoadSettings(); PathRules.ValidateSettings(Settings); RecoverBackupIntent(); }
        catch (Exception error)
        {
            _settingsRecoveryError = error.Message;
            Settings = new();
            Snapshot = new(ClientState.Attention, "Settings need recovery: " + error.Message);
            _storage.Log(new(DateTimeOffset.UtcNow, ActivityKind.Error, "", Snapshot.Message));
        }
        _reconnectLoop = Task.Run(ReconnectAsync);
        _transferPulse = Task.Run(RefreshTransferSpeedsAsync);
    }

    public async Task StartAsync()
    {
        if (!Settings.IsConfigured) return;
        await _operations.WaitAsync(_lifetime.Token);
        try
        {
            if (!Settings.IsConfigured || _disconnecting) return;
            if (_engine is not null)
            {
                foreach (var folder in Settings.CustomBackups.Where(folder => !_customRoots.ContainsKey(folder.Name)))
                {
                    try { await OpenCustomRootAsync(folder, _lifetime.Token); }
                    catch (Exception error) { _customSnapshots[folder.Name] = new(ClientState.Attention, error.Message); PublishAggregate(); }
                }
                return;
            }
            var credentials = _storage.LoadCredentials();
            if (credentials is null) throw new InvalidDataException("Enter your B2 application key to reconnect this account.");
            await OpenConnectionAsync(credentials, Settings, _lifetime.Token);
            ConfigureStartup(Settings.StartAtSignIn);
        }
        catch (Exception error) { await CloseConnectionAsync(); SetStatus(new(ClientState.Attention, error.Message)); }
        finally { _operations.Release(); }
    }

    private async Task ReconnectAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
                if (Settings.IsConfigured && !_disconnecting && (_engine is null || _customRoots.Count < Settings.CustomBackups.Count)) await StartAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void RecoverBackupIntent()
    {
        if (_storage.LoadBackupIntent() is not { } pending) return;
        var folder = pending.Folder;
        if (!PathRules.FullPath(Settings.RootPath, folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The interrupted backup does not match this sync folder.");
        var livePath = KnownFolderBackup.GetPath(folder.Name);
        var backups = Settings.Backups.Where(b => b.Name != folder.Name).ToList();
        if (livePath.Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase)) backups.Add(folder);
        else if (!livePath.Equals(folder.OriginalPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Another app changed an interrupted system folder backup. Original journal retained for recovery.");
        Settings = Settings with { Backups = backups };
        _storage.SaveSettings(Settings); _storage.ClearBackupIntent();
        _storage.Log(new(DateTimeOffset.UtcNow, ActivityKind.Backup, folder.Name, "Recovered the Windows folder mapping after an interrupted backup operation."));
    }

    public async Task ConnectAsync(string keyId, string applicationKey, string bucketName, string rootPath,
        string prefix, CancellationToken cancellationToken = default)
    {
        EnsureSettingsHealthy();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(applicationKey) || string.IsNullOrWhiteSpace(bucketName))
            throw new ArgumentException("Enter the B2 key ID, application key, and bucket name.");
        rootPath = Path.GetFullPath(rootPath);
        prefix = PathRules.NormalizePrefix(prefix);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var settings = Settings with { KeyId = keyId.Trim(), BucketName = bucketName.Trim(), RootPath = rootPath, Prefix = prefix };
            PathRules.ValidateSettings(settings);
            if ((Settings.Backups.Count > 0 || Settings.CustomBackups.Count > 0) && (!Settings.RootPath.Equals(settings.RootPath, StringComparison.OrdinalIgnoreCase) || Settings.BucketName != settings.BucketName || Settings.Prefix != settings.Prefix))
                throw new IOException("Stop system folder backups before changing the account, bucket, prefix, or sync folder.");
            await CloseConnectionAsync();
            var credentials = new B2Credentials(settings.KeyId, applicationKey);
            await OpenConnectionAsync(credentials, settings, cancellationToken);
            _storage.SaveCredentials(credentials);
            _storage.SaveSettings(Settings);
            ConfigureStartup(Settings.StartAtSignIn);
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, "", "Connected to Backblaze B2"));
        }
        catch (Exception error)
        {
            await CloseConnectionAsync();
            SetStatus(new(ClientState.Attention, error.Message));
            throw;
        }
        finally { _operations.Release(); }
    }

    private async Task OpenConnectionAsync(B2Credentials credentials, AppSettings settings, CancellationToken ct)
    {
        SetStatus(new(ClientState.Connecting, "Connecting to Backblaze B2"));
        var cloud = new B2CloudStore();
        WindowsPlaceholderService? placeholders = null;
        try
        {
            ConfigureTransport(cloud, settings);
            cloud.ConfigureResumableUploads(Path.Combine(_storage.DirectoryPath, "Uploads"));
            cloud.Diagnostic += (_, message) => AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, "", message));
            ValidatePersonalRoot(settings.RootPath);
            var account = await cloud.ConnectAsync(credentials, ct);
            var required = new[] { "listFiles", "readFiles", "writeFiles" };
            if (required.Any(capability => !account.Capabilities.Contains(capability)))
                throw new IOException("This application key needs listFiles, readFiles, and writeFiles access to the selected B2 bucket.");
            var bucket = (await cloud.ListBucketsAsync(ct)).SingleOrDefault(b => b.Name == settings.BucketName)
                ?? throw new IOException("The application key cannot access that bucket. Check its bucket restriction and List All Bucket Names permission.");
            if (account.AllowedNamePrefix is { Length: > 0 } allowed && !settings.Prefix.StartsWith(allowed, StringComparison.Ordinal))
                throw new IOException("The selected cloud folder is outside the application key's permitted file prefix.");
            settings = settings with { AccountId = account.AccountId, BucketId = bucket.Id };
            placeholders = new WindowsPlaceholderService();
            placeholders.ConfigureTransferLimits(TransferLimits.For(settings).Downloads);
            await placeholders.ConnectAsync(settings.RootPath, account.AccountId + ":" + bucket.Id + ":" + settings.Prefix,
                async (file, offset, length, destination, token) =>
                {
                    await HydrateTrackedAsync(cloud, file, settings.Prefix, "CloudBay", offset, length, destination, token);
                }, ct);
            // Each account/root/prefix has independent state, so reconnecting never inherits a different baseline.
            var identity = account.AccountId + "|" + bucket.Id + "|" + settings.Prefix + "|" + settings.RootPath.ToUpperInvariant();
            var stateName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            var manifest = new SyncManifest(Path.Combine(_storage.DirectoryPath, "State", stateName + ".sqlite"));
            _cloud = cloud; _placeholders = placeholders; Settings = settings;
            foreach (var folder in settings.Backups)
            {
                try { await Task.Run(() => FolderAppearance.EnsureKnownFolderAsync(folder, ct), ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                { AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, folder.Name, "Folder appearance could not be refreshed. Your backup data was retained.")); }
            }
            try { await cloud.CleanupAbandonedUploadsAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, "", "Pending upload cleanup will retry when B2 is available.")); }
            _engine = new SyncEngine(cloud, placeholders, manifest, settings,
                Path.Combine(_storage.DirectoryPath, "Recovery"), AddActivity, SetStatus,
                () => SystemIntegration.GetPauseReason(Settings), "CloudBay");
            foreach (var folder in settings.CustomBackups)
            {
                try { await OpenCustomRootAsync(folder, ct); }
                catch (Exception error)
                {
                    _customSnapshots[folder.Name] = new(ClientState.Attention, error.Message);
                    AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, folder.Name, "Custom folder backup could not start: " + error.Message));
                }
            }
            ApplyManualPause(_engine);
            _engine.Start();
            _transferMaintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _transferMaintenance = Task.Run(() => MaintainUploadCheckpointsAsync(cloud, _transferMaintenanceCancellation.Token));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (placeholders is not null) await placeholders.DisposeAsync();
            cloud.Dispose(); throw;
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureSettingsHealthy();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        PathRules.ValidateSettings(settings);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Settings.RootPath != settings.RootPath || Settings.Prefix != settings.Prefix ||
                Settings.BucketId != settings.BucketId || Settings.KeyId != settings.KeyId ||
                Settings.BucketName != settings.BucketName || Settings.AccountId != settings.AccountId)
                throw new IOException("Use account setup to change the connection or sync folder.");
            if (!Settings.Backups.SequenceEqual(settings.Backups) || !Settings.CustomBackups.SequenceEqual(settings.CustomBackups))
                throw new IOException("Folder backup changed while these settings were open. Reload settings and use folder backup controls to change those folders.");
            SavePreferenceUpdate(new()
            {
                StartAtSignIn = settings.StartAtSignIn, FilesOnDemand = settings.FilesOnDemand,
                PauseOnMetered = settings.PauseOnMetered, PauseOnBatterySaver = settings.PauseOnBatterySaver,
                UploadConcurrency = settings.UploadConcurrency, UploadBytesPerSecond = settings.UploadBytesPerSecond,
                DownloadConcurrency = settings.DownloadConcurrency, UploadMode = settings.UploadMode,
                DownloadBytesPerSecond = settings.DownloadBytesPerSecond, PollSeconds = settings.PollSeconds,
                Theme = settings.Theme, Exclusions = settings.Exclusions.ToArray(),
                DisabledLegacyExclusions = settings.DisabledLegacyExclusions.ToArray(),
                SelectedExclusions = settings.SelectedExclusions.ToArray(), GuidedExclusions = settings.GuidedExclusions.ToArray()
            });
        }
        finally { _operations.Release(); }
    }

    /// <summary>Atomically merges preference changes with the latest lifecycle state, without reconnecting native roots.</summary>
    public async Task<AppSettings> UpdatePreferencesAsync(PreferenceUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnsureSettingsHealthy();
        // The caller may edit its UI collection while this update waits behind a backup operation.
        update = update with { Exclusions = update.Exclusions?.ToArray(), DisabledLegacyExclusions = update.DisabledLegacyExclusions?.ToArray(),
            SelectedExclusions = update.SelectedExclusions?.ToArray(),
            GuidedExclusions = update.GuidedExclusions?.ToArray() };
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            return SavePreferenceUpdate(update);
        }
        finally { _operations.Release(); }
    }

    private AppSettings SavePreferenceUpdate(PreferenceUpdate update)
    {
        var previous = Settings;
        var settings = previous with
        {
            StartAtSignIn = update.StartAtSignIn ?? previous.StartAtSignIn,
            FilesOnDemand = update.FilesOnDemand ?? previous.FilesOnDemand,
            PauseOnMetered = update.PauseOnMetered ?? previous.PauseOnMetered,
            PauseOnBatterySaver = update.PauseOnBatterySaver ?? previous.PauseOnBatterySaver,
            UploadConcurrency = update.UploadConcurrency ?? previous.UploadConcurrency,
            DownloadConcurrency = update.DownloadConcurrency ?? previous.DownloadConcurrency,
            UploadMode = update.UploadMode ?? previous.UploadMode,
            UploadBytesPerSecond = update.UploadBytesPerSecond ?? previous.UploadBytesPerSecond,
            DownloadBytesPerSecond = update.DownloadBytesPerSecond ?? previous.DownloadBytesPerSecond,
            PollSeconds = update.PollSeconds ?? previous.PollSeconds,
            Theme = update.Theme ?? previous.Theme,
            Exclusions = update.Exclusions?.ToList() ?? previous.Exclusions,
            DisabledLegacyExclusions = update.DisabledLegacyExclusions?.ToList() ?? previous.DisabledLegacyExclusions,
            SelectedExclusions = update.SelectedExclusions?.ToList() ?? previous.SelectedExclusions,
            GuidedExclusions = update.GuidedExclusions?.ToList() ?? previous.GuidedExclusions
        };
        PathRules.ValidateSettings(settings);
        var transferChanged = previous.UploadConcurrency != settings.UploadConcurrency || previous.DownloadConcurrency != settings.DownloadConcurrency || previous.UploadMode != settings.UploadMode ||
            previous.UploadBytesPerSecond != settings.UploadBytesPerSecond || previous.DownloadBytesPerSecond != settings.DownloadBytesPerSecond;
        // Bandwidth is applied by the shared transport; changing it should not schedule another file scan.
        // Concurrency also changes the engine's parallel work scheduling.
        var syncChanged = previous.UploadConcurrency != settings.UploadConcurrency || previous.DownloadConcurrency != settings.DownloadConcurrency || previous.UploadMode != settings.UploadMode || previous.FilesOnDemand != settings.FilesOnDemand ||
            previous.PauseOnMetered != settings.PauseOnMetered || previous.PauseOnBatterySaver != settings.PauseOnBatterySaver ||
            previous.PollSeconds != settings.PollSeconds || !previous.Exclusions.SequenceEqual(settings.Exclusions) ||
            !previous.DisabledLegacyExclusions.SequenceEqual(settings.DisabledLegacyExclusions) ||
            !previous.SelectedExclusions.SequenceEqual(settings.SelectedExclusions) || !previous.GuidedExclusions.SequenceEqual(settings.GuidedExclusions);
        var startupChanged = previous.StartAtSignIn != settings.StartAtSignIn;
        if (!transferChanged && !syncChanged && !startupChanged && previous.Theme == settings.Theme) return previous;

        if (startupChanged && _manageStartup)
        {
            // Restore the actual registry value, including its type, if the durable settings write fails.
            // Recreating it from the old preference could overwrite a startup entry changed outside the app.
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var existed = key.GetValueNames().Contains("CloudBay", StringComparer.OrdinalIgnoreCase);
            var value = existed ? key.GetValue("CloudBay", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            var kind = existed ? key.GetValueKind("CloudBay") : RegistryValueKind.String;
            try
            {
                ConfigureStartup(settings.StartAtSignIn);
                _storage.SaveSettings(settings);
            }
            catch (Exception error)
            {
                try
                {
                    if (existed) key.SetValue("CloudBay", value!, kind);
                    else key.DeleteValue("CloudBay", throwOnMissingValue: false);
                }
                catch (Exception rollback)
                {
                    throw new IOException("Preferences could not be saved, and Windows startup could not be restored. Check the Windows startup setting.",
                        new AggregateException(error, rollback));
                }
                throw;
            }
        }
        else _storage.SaveSettings(settings);

        Settings = settings;
        if (transferChanged)
        {
            if (_cloud is { } cloud) ConfigureTransport(cloud, settings);
            var downloadWorkers = TransferLimits.For(settings).Downloads;
            _placeholders?.ConfigureTransferLimits(downloadWorkers);
            foreach (var runtime in _customRoots.Values) runtime.Placeholders.ConfigureTransferLimits(downloadWorkers);
        }
        if (syncChanged)
        {
            _engine?.Configure(settings);
            foreach (var runtime in _customRoots.Values) runtime.Engine.Configure(CustomSettings(runtime.Folder));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return settings;
    }

    public async Task SetBackupAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!Settings.IsConfigured || _engine is null) throw new IOException("Connect your B2 account before enabling system folder backup.");
            var current = Settings.Backups.SingleOrDefault(b => b.Name == name);
            if ((current is not null) == enabled) return;
            var systemSource = enabled ? KnownFolderBackup.GetPath(name) : current!.DestinationPath;
            if (Settings.CustomBackups.Any(folder => IsNested(folder.SourcePath, systemSource)))
                throw new IOException("A custom backup is inside this system folder. Stop that custom backup before changing the Windows folder location.");
            _maintenance = true;
            await _engine.QuiesceAsync(cancellationToken);
            var folders = Settings.Backups.ToList();
            if (enabled)
            {
                // Persist intent before Windows redirection so an interrupted operation can be recovered.
                var intent = new BackupFolder(name, KnownFolderBackup.GetPath(name), PathRules.FullPath(Settings.RootPath, name));
                _storage.SaveBackupIntent(intent, true);
                var folder = await Task.Run(() => KnownFolderBackup.EnableAsync(name, Settings.RootPath, cancellationToken), cancellationToken);
                folders.Add(folder);
                Settings = Settings with { Backups = folders };
                _storage.SaveSettings(Settings);
                _storage.ClearBackupIntent();
            }
            else
            {
                _storage.SaveBackupIntent(current!, false);
                await Task.Run(() => KnownFolderBackup.DisableAsync(current!, cancellationToken), cancellationToken);
                folders.Remove(current!);
                Settings = Settings with { Backups = folders };
                _storage.SaveSettings(Settings);
                _storage.ClearBackupIntent();
            }
            _engine.Configure(Settings);
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, name, enabled ? "System folder backup enabled" : "System folder restored to its local location. Cloud copy retained."));
        }
        finally { EndMaintenance(resume: true); _operations.Release(); }
    }

    private AppSettings CustomSettings(CustomBackupFolder folder) => Settings with
    { RootPath = folder.SourcePath, Prefix = folder.Prefix, Backups = [], CustomBackups = [] };
    private static bool IsNested(string path, string parent) => Path.GetFullPath(path).Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private CustomRuntime GetCustom(string name) => _customRoots.TryGetValue(name, out var runtime) ? runtime :
        throw new IOException("This custom backup is unavailable. Check its folder location and reconnect.");

    private async Task OpenCustomRootAsync(CustomBackupFolder folder, CancellationToken ct)
    {
        if (_cloud is null) throw new IOException("Connect your B2 account first.");
        if (!Directory.Exists(folder.SourcePath)) throw new DirectoryNotFoundException("The custom backup folder is unavailable. No deletions were sent to B2.");
        ValidatePersonalRoot(folder.SourcePath);
        var expectedPrefix = Settings.Prefix + ".cloudbay-backups/" + PathRules.ValidateRelative(folder.Name) + "/";
        if (!folder.Prefix.Equals(expectedPrefix, StringComparison.Ordinal)) throw new InvalidDataException("Custom backup settings contain an unexpected cloud prefix.");
        var placeholders = new WindowsPlaceholderService();
        placeholders.ConfigureTransferLimits(TransferLimits.For(Settings).Downloads);
        try
        {
            var cloud = _cloud;
            await placeholders.ConnectAsync(folder.SourcePath, Settings.AccountId + ":" + Settings.BucketId + ":" + folder.Prefix,
                async (file, offset, length, destination, token) =>
                {
                    await HydrateTrackedAsync(cloud, file, folder.Prefix, folder.Name, offset, length, destination, token);
                }, ct);
            var identity = Settings.AccountId + "|" + Settings.BucketId + "|" + folder.Prefix + "|" + folder.SourcePath.ToUpperInvariant();
            var stateName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            var manifest = new SyncManifest(Path.Combine(_storage.DirectoryPath, "State", stateName + ".sqlite"));
            var engine = new SyncEngine(cloud, placeholders, manifest, CustomSettings(folder),
                Path.Combine(_storage.DirectoryPath, "Recovery", folder.Name),
                value => AddActivity(value with { Path = folder.Name + "/" + value.Path }),
                value => { _customSnapshots[folder.Name] = value; PublishAggregate(); },
                () => SystemIntegration.GetPauseReason(Settings), folder.Name);
            if (!_customRoots.TryAdd(folder.Name, new(folder, placeholders, engine)))
                throw new IOException("A custom backup with this name is already active.");
            try { ApplyManualPause(engine); engine.Start(); }
            catch { _customRoots.TryRemove(folder.Name, out _); await engine.DisposeAsync(); throw; }
        }
        catch { await placeholders.DisposeAsync(); throw; }
    }

    public async Task AddCustomBackupAsync(string sourcePath, string name, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        name = PathRules.ValidateRelative(name.Trim());
        if (name.Contains('/') || name.Length > 64 || name.StartsWith(".cloudbay", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a backup name of at most 64 characters without path separators.");
        sourcePath = Path.GetFullPath(sourcePath);
        ValidatePersonalRoot(sourcePath);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            if (Settings.CustomBackups.Any(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("A backup with this name already exists.");
            var folder = new CustomBackupFolder(name, sourcePath, Settings.Prefix + ".cloudbay-backups/" + name + "/");
            // Save intent first: a crash after registration will reconnect the same source and prefix.
            var previous = Settings;
            Settings = Settings with { CustomBackups = [..Settings.CustomBackups, folder] };
            _storage.SaveSettings(Settings);
            try { await OpenCustomRootAsync(folder, cancellationToken); }
            catch { Settings = previous; _storage.SaveSettings(Settings); throw; }
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, name, "Custom folder backup enabled in its existing Windows location."));
        }
        finally { _operations.Release(); }
    }

    public async Task RemoveCustomBackupAsync(string name, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_customRoots.ContainsKey(name))
            {
                var folder = Settings.CustomBackups.SingleOrDefault(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException("This custom backup is no longer configured.");
                await OpenCustomRootAsync(folder, cancellationToken);
            }
            var root = GetCustom(name);
            _maintenance = true;
            await root.Engine.QuiesceAsync(cancellationToken);
            await root.Placeholders.PrepareForUnregisterAsync(cancellationToken);
            var registration = root.Placeholders.RegistrationId;
            await root.Engine.DisposeAsync(); await root.Placeholders.DisposeAsync();
            _customRoots.TryRemove(name, out _); _customSnapshots.TryRemove(name, out _);
            try { if (registration is not null) _unregisterSyncRoot(registration); }
            catch (Exception error)
            {
                _customSnapshots[name] = new(ClientState.Attention, "Stopping this backup needs a retry: " + error.Message);
                PublishAggregate();
                throw;
            }
            Settings = Settings with { CustomBackups = Settings.CustomBackups.Where(folder => !folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() };
            _storage.SaveSettings(Settings);
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Backup, name, "Custom backup stopped. All local files and B2 versions were retained."));
            PublishAggregate();
        }
        finally { EndMaintenance(resume: true); _operations.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken);
        _disconnecting = true;
        try
        {
            if (_cloud is null && Settings.IsConfigured)
            {
                var credentials = _storage.LoadCredentials() ?? throw new IOException("Enter your B2 application key to reconnect before disconnecting safely.");
                await OpenConnectionAsync(credentials, Settings, cancellationToken);
            }
            EnsureConnected();
            _maintenance = true;
            if (_engine is not null) { await _engine.QuiesceAsync(cancellationToken); }
            foreach (var root in _customRoots.Values) await root.Engine.QuiesceAsync(cancellationToken);
            SetStatus(Snapshot with { State = ClientState.Syncing, Message = "Downloading all files before disconnecting" });
            await _placeholders!.SetPinAsync(Settings.RootPath, PinMode.AlwaysAvailable, cancellationToken);
            foreach (var folder in Settings.Backups.ToArray())
            {
                _storage.SaveBackupIntent(folder, false);
                await Task.Run(() => KnownFolderBackup.DisableAsync(folder, cancellationToken), cancellationToken);
                Settings = Settings with { Backups = Settings.Backups.Where(b => b.Name != folder.Name).ToList() };
                _storage.SaveSettings(Settings); _storage.ClearBackupIntent();
            }
            var registration = _placeholders.RegistrationId;
            await _placeholders.PrepareForUnregisterAsync(cancellationToken);
            var customRegistrations = new List<string>();
            foreach (var root in _customRoots.Values)
            {
                await root.Placeholders.PrepareForUnregisterAsync(cancellationToken);
                if (root.Placeholders.RegistrationId is { } customRegistration) customRegistrations.Add(customRegistration);
            }
            await _cloud!.CancelPendingUploadsAsync(cancellationToken);
            await CloseConnectionAsync();
            if (registration is not null) _unregisterSyncRoot(registration);
            foreach (var customRegistration in customRegistrations) _unregisterSyncRoot(customRegistration);
            _storage.ClearCredentials();
            Settings = Settings with { KeyId = "", AccountId = "", BucketId = "", BucketName = "", Backups = [], CustomBackups = [] };
            _storage.SaveSettings(Settings);
            ConfigureStartup(false);
            SetStatus(new(ClientState.NotConnected, "Account disconnected. Local and B2 files were retained."));
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, "", "Disconnected the account after downloading cloud files and restoring system folders."));
        }
        catch (Exception error) { SetStatus(Snapshot with { State = ClientState.Attention, Message = "Disconnect stopped: " + error.Message }); throw; }
        finally { EndMaintenance(resume: false); _disconnecting = false; _operations.Release(); }
    }

    public void Pause(TimeSpan? duration = null)
    {
        Interlocked.Exchange(ref _manualPauseUntilTicks, duration.HasValue ? (DateTimeOffset.UtcNow + duration.Value).UtcTicks : long.MaxValue);
        _engine?.Pause(duration); foreach (var root in _customRoots.Values) root.Engine.Pause(duration);
    }
    public void Resume()
    {
        Interlocked.Exchange(ref _manualPauseUntilTicks, 0);
        if (_maintenance) return;
        _engine?.Resume(); foreach (var root in _customRoots.Values) root.Engine.Resume();
    }
    private bool ApplyManualPause(SyncEngine engine)
    {
        var until = Interlocked.Read(ref _manualPauseUntilTicks);
        var remaining = until - DateTimeOffset.UtcNow.UtcTicks;
        if (until == long.MaxValue) { engine.Pause(); return true; }
        if (remaining > 0) { engine.Pause(TimeSpan.FromTicks(remaining)); return true; }
        return false;
    }
    private void EndMaintenance(bool resume)
    {
        if (!_maintenance) return;
        _maintenance = false;
        foreach (var engine in _customRoots.Values.Select(root => root.Engine).Prepend(_engine).OfType<SyncEngine>())
            if (!ApplyManualPause(engine) && resume) engine.Resume();
    }
    public void ApprovePendingDeletions()
    {
        if (_rootSnapshot.Message.StartsWith("Review required:", StringComparison.Ordinal)) _engine?.ApproveDeletions();
        foreach (var root in _customRoots.Values)
            if (_customSnapshots.TryGetValue(root.Folder.Name, out var status) && status.Message.StartsWith("Review required:", StringComparison.Ordinal)) root.Engine.ApproveDeletions();
    }
    public Task SyncNowAsync() => Task.WhenAll(new[] { _engine?.SyncNowAsync(_lifetime.Token) ?? Task.CompletedTask }
        .Concat(_customRoots.Values.Select(root => root.Engine.SyncNowAsync(_lifetime.Token))));
    public void LaunchFolder(string? backupName = null) => SystemIntegration.OpenFolder(backupName is null ? Settings.RootPath : GetCustom(backupName).Folder.SourcePath);
    public Task<IReadOnlyList<CloudObject>> GetVersionsAsync(string relativePath, string? backupName = null)
    {
        EnsureConnected();
        PathRules.ValidateRelative(relativePath.Replace('\\', '/'));
        var prefix = backupName is null ? Settings.Prefix : GetCustom(backupName).Folder.Prefix;
        return _cloud!.VersionsAsync(Settings.BucketId, prefix + relativePath.Replace('\\', '/'), _lifetime.Token);
    }
    public async Task RestoreVersionAsync(CloudObject version)
    {
        EnsureConnected();
        PathRules.FromKey(version.Key, Settings.Prefix);
        await _cloud!.RestoreAsync(Settings.BucketId, version, _lifetime.Token);
        AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Restore, version.Key, "A previous B2 version was restored"));
        await SyncNowAsync();
    }
    public Task SetPinAsync(string relativePath, PinMode mode, string? backupName = null)
    {
        EnsureConnected(); var root = backupName is null ? null : GetCustom(backupName);
        return (root?.Placeholders ?? _placeholders!).SetPinAsync(PathRules.FullPath(root?.Folder.SourcePath ?? Settings.RootPath, relativePath.Replace('\\', '/')), mode, _lifetime.Token);
    }
    public Task FreeSpaceAsync(string relativePath, string? backupName = null)
    {
        EnsureConnected(); var root = backupName is null ? null : GetCustom(backupName);
        return (root?.Placeholders ?? _placeholders!).FreeSpaceAsync(PathRules.FullPath(root?.Folder.SourcePath ?? Settings.RootPath, relativePath.Replace('\\', '/')), _lifetime.Token);
    }
    private void EnsureConnected()
    { if (_cloud is null || _placeholders is null) throw new IOException("Connect your B2 account first."); }
    private void ConfigureStartup(bool enabled)
    {
        if (_manageStartup) SystemIntegration.ConfigureStartup(enabled);
    }

    private void EnsureSettingsHealthy()
    {
        if (_settingsRecoveryError is not null)
            throw new IOException("CloudBay preserved settings that could not be recovered. Restore settings.json from its .bak copy in " +
                _storage.DirectoryPath + " and restart before changing the account or Windows folder locations. " + _settingsRecoveryError);
    }
    private static void ValidatePersonalRoot(string sourcePath)
    {
        var protectedLocations = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) };
        if (protectedLocations.Any(path => path.Length > 0 && (IsNested(sourcePath, path) || IsNested(path, sourcePath))))
            throw new IOException("Choose a personal data folder. Windows, installed programs, and the live application profile require a system backup tool.");
    }
    private void SetStatus(SyncSnapshot value) { _rootSnapshot = value; PublishAggregate(); }
    private void PublishAggregate()
    {
        lock (_aggregateGate)
        {
        var hydrations = _hydrations.Values.Select(item => item with
        {
            BytesPerSecond = item.Phase == TransferPhase.Downloading && _hydrationSpeeds.TryGetValue(item.Id, out var speed)
                ? speed.BytesPerSecond : 0
        }).ToArray();
        var all = _customSnapshots.Select(pair => (Name: pair.Key, Snapshot: pair.Value)).Prepend((Name: "", Snapshot: _rootSnapshot)).ToArray();
        static int Rank(ClientState state) => state switch { ClientState.Attention => 6, ClientState.Offline => 5,
            ClientState.Connecting => 4, ClientState.Syncing => 3, ClientState.Paused => 2, _ => 1 };
        var selected = all.MaxBy(item => Rank(item.Snapshot.State));
        var message = selected.Snapshot.Message;
        var reviewed = all.Where(item => item.Snapshot.Message.StartsWith("Review required:", StringComparison.Ordinal)).ToArray();
        var pending = all.Sum(item => item.Snapshot.Pending);
        if (reviewed.Length > 0)
        {
            pending = reviewed.Sum(item => item.Snapshot.Pending);
            message = $"Review required: {pending} files disappeared across {reviewed.Length} sync folders. Deletion sync is paused.";
        }
        else if (selected.Name.Length > 0) message = selected.Name + ": " + message;
        var previous = Snapshot;
        Snapshot = selected.Snapshot with { Message = message, Pending = pending,
            FileCount = all.Sum(item => item.Snapshot.FileCount), CloudBytes = all.Sum(item => item.Snapshot.CloudBytes),
            LocalBytes = all.Sum(item => item.Snapshot.LocalBytes),
            TransferTotalBytes = all.Sum(item => item.Snapshot.TransferTotalBytes) + hydrations.Sum(item => item.TotalBytes),
            TransferredBytes = all.Sum(item => item.Snapshot.TransferredBytes) + hydrations.Sum(item => item.Bytes),
            UploadBytesPerSecond = all.Sum(item => item.Snapshot.UploadBytesPerSecond),
            DownloadBytesPerSecond = all.Sum(item => item.Snapshot.DownloadBytesPerSecond) +
                (hydrations.Any(item => item.Phase == TransferPhase.Downloading) ? _nativeDownloadSpeed.BytesPerSecond : 0),
            Transfers = hydrations.Concat(all.SelectMany(item => item.Snapshot.Transfers))
                .OrderBy(item => item.Phase is TransferPhase.Queued or TransferPhase.Paused or TransferPhase.Retrying ? 1 : 0).Take(256).ToArray(),
            ActiveTransfers = all.Sum(item => item.Snapshot.ActiveTransfers) + hydrations.Count(item => item.Phase != TransferPhase.Queued),
            QueuedTransfers = all.Sum(item => item.Snapshot.QueuedTransfers) + hydrations.Count(item => item.Phase == TransferPhase.Queued) };
        if (_hydrations.Count > 0 && Snapshot.State is not (ClientState.Attention or ClientState.Offline or ClientState.Paused))
            Snapshot = Snapshot with { State = ClientState.Syncing, Message = "Downloading your files" };
        NotifyChanged(previous.State != Snapshot.State || previous.Pending != Snapshot.Pending);
        }
    }
    private void NotifyChanged(bool force = true)
    {
        lock (_changedGate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!force && System.Diagnostics.Stopwatch.GetElapsedTime(_lastChange, now) < TimeSpan.FromMilliseconds(250)) return;
            _lastChange = now;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void AddActivity(ActivityEvent value) { if (_storage.Log(value)) NotifyChanged(); }
    private static void ConfigureTransport(B2CloudStore cloud, AppSettings settings)
    {
        var limits = TransferLimits.For(settings);
        cloud.Configure(settings.UploadBytesPerSecond, settings.DownloadBytesPerSecond, limits.Uploads);
        cloud.ConfigureDownloads(limits.Downloads);
    }

    private async Task HydrateTrackedAsync(B2CloudStore cloud, CloudObject file, string prefix, string rootName,
        long offset, long length, Stream destination, CancellationToken token)
    {
        var relative = file.Key.StartsWith(prefix, StringComparison.Ordinal) ? file.Key[prefix.Length..] : file.Key;
        var id = "hydrate|" + rootName + "|" + file.FileId + "|" + Guid.NewGuid().ToString("N");
        var speed = new TransferSpeedMeter();
        long previousBytes = 0;
        _hydrationSpeeds[id] = speed;
        _hydrations[id] = new(id, rootName, relative, ActivityKind.Download, TransferPhase.Queued, 0, length);
        PublishAggregate();
        var admitted = false;
        var awaitingValidation = destination is HydrationStream;
        if (destination is HydrationStream native)
        {
            // Monitor before transport starts: a failed transport checksum can trigger
            // a successful CFAPI restart. Only the service's final validation outcome
            // decides whether this attempt is completed, cancelled, or a real error.
            var completion = FinalizeNativeHydrationAsync(id, rootName, relative, length, native.ValidationCompletion);
            _hydrationFinalizations[id] = completion;
            _ = completion.ContinueWith(_ => _hydrationFinalizations.TryRemove(id, out var removed),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        try
        {
            await TransferResources.NativeHydration.WaitAsync(token);
            admitted = true;
            await cloud.DownloadAsync(file, destination, offset, length, new HydrationProgress(value =>
            {
                var previous = Interlocked.Exchange(ref previousBytes, value.Bytes);
                if (value.IsBaseline)
                {
                    speed.Reset(value.Bytes);
                    lock (_aggregateGate)
                        if (_nativeDownloadPayload == 0) _nativeDownloadSpeed.Reset(0);
                }
                else
                {
                    speed.Sample(value.Bytes);
                    lock (_aggregateGate)
                    {
                        _nativeDownloadPayload += Math.Max(0, value.Bytes - previous);
                        _nativeDownloadSpeed.Sample(_nativeDownloadPayload);
                    }
                }
                _hydrations[id] = new(id, rootName, relative, ActivityKind.Download, TransferPhase.Downloading,
                    value.Bytes, value.TotalBytes);
                PublishAggregate();
            }), token);
            _hydrations[id] = new(id, rootName, relative, ActivityKind.Download, TransferPhase.Verifying, length, length);
            PublishAggregate();
            // Return to CFAPI so it can persist and validate the assembled cache; the
            // independent monitor keeps its row visible through ACK without a deadlock.
            if (!awaitingValidation)
                AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Download, rootName + "/" + relative, "Downloaded on demand", length));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (!awaitingValidation)
                AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, rootName + "/" + relative, error.Message));
            throw;
        }
        finally
        {
            if (admitted) TransferResources.NativeHydration.Release();
            if (!awaitingValidation)
            {
                _hydrationSpeeds.TryRemove(id, out _);
                _hydrations.TryRemove(id, out _); PublishAggregate();
            }
        }
    }
    private async Task FinalizeNativeHydrationAsync(string id, string rootName, string relative, long length, Task validation)
    {
        try
        {
            await validation.ConfigureAwait(false);
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Download, rootName + "/" + relative, "Downloaded on demand", length));
        }
        catch (OperationCanceledException) { /* Interruption or a bounded native restart has its own next live request. */ }
        catch (Exception error)
        {
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Error, rootName + "/" + relative, error.Message));
        }
        finally
        {
            _hydrationSpeeds.TryRemove(id, out _);
            _hydrations.TryRemove(id, out _);
            PublishAggregate();
        }
    }
    private sealed class HydrationProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        private long _last;
        public void Report(TransferProgress value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (value.Bytes != 0 && value.Bytes != value.TotalBytes &&
                System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _last), now) < TimeSpan.FromMilliseconds(250)) return;
            Interlocked.Exchange(ref _last, now);
            report(value);
        }
    }
    private async Task MaintainUploadCheckpointsAsync(B2CloudStore cloud, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(6), token);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
                operation.CancelAfter(TimeSpan.FromMinutes(2));
                try { await cloud.CleanupAbandonedUploadsAsync(operation.Token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                { AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, "", "Pending upload cleanup will retry when B2 is available.")); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RefreshTransferSpeedsAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
                if (Snapshot.ActiveTransfers == 0) continue;
                _engine?.RefreshTransferStatus();
                foreach (var runtime in _customRoots.Values) runtime.Engine.RefreshTransferStatus();
                if (!_hydrations.IsEmpty) PublishAggregate();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task CloseConnectionAsync()
    {
        _transferMaintenanceCancellation?.Cancel();
        if (_transferMaintenance is not null) await _transferMaintenance;
        _transferMaintenance = null;
        _transferMaintenanceCancellation?.Dispose(); _transferMaintenanceCancellation = null;
        foreach (var runtime in _customRoots.Values)
        { await runtime.Engine.DisposeAsync(); await runtime.Placeholders.DisposeAsync(); }
        _customRoots.Clear(); _customSnapshots.Clear();
        if (_engine is not null) { await _engine.DisposeAsync(); _engine = null; }
        if (_placeholders is not null) { await _placeholders.DisposeAsync(); _placeholders = null; }
        await Task.WhenAll(_hydrationFinalizations.Values);
        _hydrationFinalizations.Clear();
        _cloud?.Dispose(); _cloud = null;
        _hydrations.Clear(); _hydrationSpeeds.Clear();
        Interlocked.Exchange(ref _nativeDownloadPayload, 0); _nativeDownloadSpeed.Reset(0);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _engine?.Pause();
        foreach (var root in _customRoots.Values) { root.Engine.Pause(); await root.Placeholders.DisconnectAsync(); }
        if (_placeholders is not null) await _placeholders.DisconnectAsync();
        await _reconnectLoop;
        await _transferPulse;
        await _operations.WaitAsync();
        try { await CloseConnectionAsync(); }
        finally { _operations.Release(); _operations.Dispose(); _lifetime.Dispose(); }
    }
    private sealed record CustomRuntime(CustomBackupFolder Folder, WindowsPlaceholderService Placeholders, SyncEngine Engine);
}

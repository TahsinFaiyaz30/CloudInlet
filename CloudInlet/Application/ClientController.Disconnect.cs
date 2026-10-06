using CloudInlet.Core;
using CloudInlet.Core.Sync;
using CloudInlet.Windows;

namespace CloudInlet.Application;

public sealed partial class ClientController
{
    private DisconnectMode? _pendingDisconnectMode;
    private void RecoverAccountDisconnectIntent()
    {
        if (_storage.LoadAccountDisconnectIntent() is not { } pending) return;
        if (Enum.IsDefined(pending.Mode) && pending.RootPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase) &&
            Settings.KeyId.Length == 0 && Settings.AccountId.Length == 0 && Settings.BucketId.Length == 0)
        {
            // Settings were durably cleared just before a process exit; finish vault cleanup only.
            _storage.ClearCredentials(); _storage.ClearAccountDisconnectIntent();
            return;
        }
        if (!Enum.IsDefined(pending.Mode) || pending.AccountId != Settings.AccountId || pending.BucketId != Settings.BucketId ||
            !pending.RootPath.Equals(Settings.RootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The interrupted disconnect does not match this account. Its original journal was preserved.");
        _pendingDisconnectMode = pending.Mode;
        _disconnecting = true;
    }

    public async Task<BackupTransferOutcome?> DisconnectAsync(DisconnectMode mode, CancellationToken cancellationToken = default)
    {
        EnsureSettingsHealthy();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = operation.Token;
        // Match the cloud backup stop workflow's gate order. A new stop must not
        // persist its account-bound intent while disconnect clears that account.
        await _cloudBackupStopGate.WaitAsync(cancellationToken);
        try
        {
            if (_storage.LoadCloudBackupStopIntent() is not null)
                throw new IOException("CloudInlet is recovering an interrupted folder backup stop. Keep this B2 account connected and let recovery finish before disconnecting; reconnect the same account if its connection needs attention.");
            await _operations.WaitAsync(cancellationToken);
        }
        catch { _cloudBackupStopGate.Release(); throw; }
        _disconnecting = true;
        try
        {
            if (!Settings.IsConfigured) throw new IOException("No B2 account is connected.");
            _storage.SaveAccountDisconnectIntent(new(mode, Settings.AccountId, Settings.BucketId, Settings.RootPath));
            _pendingDisconnectMode = mode;
            var cloudJobs = CloudTransferJobs.Where(job => (job.Plan.Source.Provider == "b2" || job.Plan.Destination.Provider == "b2") &&
                job.State is Core.Transfers.TransferJobState.Running or Core.Transfers.TransferJobState.Discovering).ToArray();
            await Task.WhenAll(cloudJobs.Select(job => PauseCloudTransferAsync(job.Plan.Id)));
            if (_cloud is null && mode != DisconnectMode.DisconnectOnly)
            {
                var credentials = _storage.LoadCredentials() ?? throw new IOException("Enter your B2 application key to verify cloud copies before this disconnect.");
                await OpenConnectionAsync(credentials, Settings, cancellationToken);
            }
            _maintenance = true;
            if (_engine is not null) await _engine.QuiesceAsync(cancellationToken);
            foreach (var root in _customRoots.Values) await root.Engine.QuiesceAsync(cancellationToken);
            SetStatus(new(ClientState.Syncing, mode switch
            {
                DisconnectMode.DownloadAndDisconnect => "Downloading files before disconnecting",
                DisconnectMode.RemoveLocalCopyAndDisconnect => "Verifying cloud copies before removing eligible local files",
                _ => "Disconnecting without downloading files"
            }));
            if (mode == DisconnectMode.DownloadAndDisconnect)
                await _placeholders!.SetPinAsync(Settings.RootPath, PinMode.AlwaysAvailable, cancellationToken);
            foreach (var folder in Settings.Backups.ToArray())
            {
                _storage.SaveBackupIntent(folder, false);
                await Task.Run(async () =>
                {
                    if (mode == DisconnectMode.DownloadAndDisconnect) await KnownFolderBackup.DisableAsync(folder, cancellationToken);
                    else await KnownFolderBackup.ApplyDisableReviewedAsync(KnownFolderBackup.PreviewDisable(folder, null, BackupTransferMode.None, cancellationToken), cancellationToken);
                }, cancellationToken);
                Settings = Settings with { Backups = Settings.Backups.Where(b => b.Name != folder.Name).ToList() };
                _storage.SaveSettings(Settings); _storage.ClearBackupIntent();
            }
            BackupTransferOutcome? cleanup = null;
            if (mode == DisconnectMode.RemoveLocalCopyAndDisconnect)
            {
                EnsureConnected();
                var results = new List<BackupTransferOutcome>
                {
                    await RemoveRootCopiesAsync(Settings.RootPath, Settings, _engine!, _placeholders!, cancellationToken)
                };
                foreach (var root in _customRoots.Values)
                    results.Add(await RemoveRootCopiesAsync(root.Folder.SourcePath, CustomSettings(root.Folder), root.Engine, root.Placeholders, cancellationToken));
                var retained = results.Sum(item => item.RetainedFileCount);
                cleanup = new(results.Sum(item => item.RemovedFileCount), retained,
                    retained > 0 ? $"{retained:N0} changed, unverified, or unavailable local copies were kept. Other personal files and folders were retained." : null);
            }
            var registrations = new List<string>();
            if (mode == DisconnectMode.DownloadAndDisconnect)
            {
                await _placeholders!.PrepareForUnregisterAsync(cancellationToken);
                if (_placeholders.RegistrationId is { } registration) registrations.Add(registration);
                foreach (var root in _customRoots.Values)
                {
                    await root.Placeholders.PrepareForUnregisterAsync(cancellationToken);
                    if (root.Placeholders.RegistrationId is { } customRegistration) registrations.Add(customRegistration);
                }
            }
            // Quiesced native uploads keep their acknowledged B2 parts and local journal.
            // Reconnecting this account can continue the unchanged unfinished file;
            // abandoned-source maintenance remains responsible for eventual cleanup.
            await CloseConnectionAsync();
            foreach (var registration in registrations) _unregisterSyncRoot(registration);
            await _cloudTransferProviderGate.WaitAsync(cancellationToken);
            try { _cloudTransferB2?.Dispose(); _cloudTransferB2 = null; }
            finally { _cloudTransferProviderGate.Release(); }
            Settings = Settings with { KeyId = "", AccountId = "", BucketId = "", BucketName = "", Backups = [], CustomBackups = [] };
            _storage.SaveSettings(Settings);
            _storage.ClearCredentials();
            await ConfigureStartupAsync(false);
            _storage.ClearAccountDisconnectIntent(); _pendingDisconnectMode = null;
            var message = mode switch
            {
                DisconnectMode.DownloadAndDisconnect => "Account disconnected. Downloaded local files and B2 files were retained.",
                DisconnectMode.RemoveLocalCopyAndDisconnect => $"Account disconnected. {cleanup!.RemovedFileCount:N0} verified local copies removed. B2 files and other personal files were retained.",
                _ => "Account disconnected without downloading. Local files and online-only placeholders were retained; reconnect to open online-only files."
            };
            SetStatus(new(ClientState.NotConnected, message));
            AddActivity(new(DateTimeOffset.UtcNow, ActivityKind.Information, "", message + (cleanup?.RetentionWarning is { } warning ? " " + warning : "")));
            return cleanup;
        }
        catch (Exception error) { SetStatus(new(ClientState.Attention, "Disconnect stopped: " + error.Message)); throw; }
        finally { EndMaintenance(resume: false); _disconnecting = false; _operations.Release(); _cloudBackupStopGate.Release(); }
    }

    private Task<BackupTransferOutcome> RemoveRootCopiesAsync(string path, AppSettings settings, SyncEngine engine,
        Windows.CloudFiles.WindowsPlaceholderService placeholders, CancellationToken ct) => VerifiedCloudCopyCleanup.RemoveAsync(path,
            engine.ReadVerifiedEntries().Where(pair => !PathRules.IsExcluded(pair.Key, settings))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            placeholders.GetFileState, placeholders.TryRemoveVerifiedOnlinePlaceholderAsync,
            (file, token) => _cloud!.VerifyUploadAsync(file, settings.BucketId, token), ct);
}

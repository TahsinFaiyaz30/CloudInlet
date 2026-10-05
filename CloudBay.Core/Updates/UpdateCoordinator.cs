using System.Security.Cryptography;
using System.Text.Json;

namespace CloudBay.Core.Updates;

/// <summary>Serializes checks, downloads and installs; never touches backup settings or cloud credentials.</summary>
public sealed class UpdateCoordinator : IAsyncDisposable
{
    private sealed record SavedState(int SchemaVersion, InstalledUpdateIdentity Identity, UpdateManifest? Manifest,
        string? ETag, UpdateCandidate? Pending, bool PendingReady, DateTimeOffset? LastCheckedUtc,
        DateTimeOffset? NextCheckUtc, DateTimeOffset? EarliestRequestUtc, int FailureCount,
        DateTimeOffset? LastInstallAttemptUtc = null);

    private readonly InstalledUpdateIdentity _identity;
    private readonly UpdateCache _cache;
    private readonly GitHubUpdateTransport _transport;
    private readonly TimeProvider _clock;
    private readonly IUpdateInstaller? _installer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private FileStream? _ownershipLock;
    private UpdateManifest? _manifest;
    private string? _etag;
    private UpdateCandidate? _candidate;
    private UpdateCandidate? _pending;
    private bool _ready;
    private bool _initialized;
    private volatile bool _disposed;
    private DateTimeOffset? _lastChecked;
    private DateTimeOffset? _nextCheck;
    private DateTimeOffset? _earliestRequest;
    private int _failureCount;
    private DateTimeOffset? _lastInstallAttempt;
    private Task? _scheduler;
    private UpdateSnapshot _snapshot = new(UpdateState.Idle, "Updates have not been checked yet.");
    private UpdatePreferences _preferences = new();

    public UpdateCoordinator(InstalledUpdateIdentity identity, string ownedCachePath, IUpdateInstaller? installer = null,
        HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        UpdateManifestRules.ValidateIdentity(identity);
        _identity = identity; _cache = new UpdateCache(ownedCachePath);
        _clock = timeProvider ?? TimeProvider.System; _installer = installer;
        _transport = new GitHubUpdateTransport(handler, _clock);
    }

    public InstalledUpdateIdentity Identity => _identity;
    public UpdateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public UpdatePreferences Preferences => Volatile.Read(ref _preferences);
    public event Action<UpdateSnapshot>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperation(cancellationToken);
        cancellationToken = operation.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _ownershipLock = _cache.AcquireLock();
            try
            {
                _preferences = (_cache.Read<UpdatePreferences>("preferences.json") ?? new()).Normalize();
                var saved = _cache.Read<SavedState>("state.json");
                if (saved is { SchemaVersion: 1 } && saved.Identity == _identity)
                {
                    if (saved.Manifest is not null) _candidate = UpdateManifestRules.Select(saved.Manifest, _identity);
                    if (saved.Pending is not null) UpdateManifestRules.ValidateCandidate(saved.Pending, _identity);
                    _manifest = saved.Manifest; _etag = saved.ETag;
                    _lastChecked = saved.LastCheckedUtc; _nextCheck = saved.NextCheckUtc;
                    _earliestRequest = saved.EarliestRequestUtc;
                    _failureCount = Math.Clamp(saved.FailureCount, 0, 8);
                    _lastInstallAttempt = saved.LastInstallAttemptUtc;
                    _pending = saved.Pending;
                    _ready = saved.PendingReady && _pending is not null &&
                        await _cache.VerifyAsync(_pending, cancellationToken).ConfigureAwait(false);
                }
                if (!_ready) _pending = null;
                _cache.RemovePackagesExcept(_ready && _pending is not null ? _cache.PackagePath(_pending) : null);
                _initialized = true;
                PublishRestingState();
                Persist();
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                // Corrupt metadata is not executable authority. Clear only this owned cache's
                // reserved installer files, then require a fresh manifest and download.
                _manifest = null; _etag = null; _candidate = null; _pending = null; _ready = false;
                _cache.RemovePackagesExcept(null); _initialized = true;
                Persist(); Publish(UpdateState.Error, "Cached update information was invalid. Check again to download a verified update.");
            }
        }
        catch { _ownershipLock?.Dispose(); _ownershipLock = null; throw; }
        finally { _gate.Release(); }
    }

    public async Task SavePreferencesAsync(UpdatePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        using var operation = CreateOperation(cancellationToken);
        cancellationToken = operation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = preferences.Normalize();
            _cache.Write("preferences.json", normalized); _preferences = normalized;
            if (_lastChecked is not null) _nextCheck = _lastChecked + TimeSpan.FromHours(normalized.CheckIntervalHours);
            Persist(); Publish(Snapshot.State, Snapshot.Message, Snapshot.DownloadedBytes);
        }
        finally { _gate.Release(); }
    }

    public async Task CheckAsync(bool manual = true, CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperation(cancellationToken);
        cancellationToken = operation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State == UpdateState.Installing) return;
            if (_identity.InstallerKind == UpdateInstallerKind.Store) { PublishRestingState(); return; }
            var now = _clock.GetUtcNow();
            if (_earliestRequest > now || !manual && (!Preferences.AutomaticChecks || _nextCheck > now))
            {
                if (manual) Publish(_ready && _pending == _candidate ? UpdateState.Ready :
                    _candidate is not null ? UpdateState.Available : UpdateState.Deferred,
                    "A recent check or server limit is still active. CloudBay will check again at the scheduled time.");
                return;
            }
            _earliestRequest = now.AddMinutes(1); Persist();
            Publish(UpdateState.Checking, "Checking GitHub Releases for your installed package type…");
            try
            {
                var result = await _transport.FetchAsync(_etag, cancellationToken).ConfigureAwait(false);
                var manifest = result.NotModified ? _manifest : result.Manifest;
                if (result.NotModified && manifest is null) throw new InvalidDataException("The update server returned no cached release manifest.");
                UpdateCandidate? candidate = null;
                if (manifest is not null)
                {
                    candidate = UpdateManifestRules.Select(manifest, _identity);
                    if (_manifest is not null && UpdateVersion.TryParse(_manifest.Version, out var previous) &&
                        UpdateVersion.TryParse(manifest.Version, out var next) && next < previous)
                        throw new InvalidDataException("The update feed returned an older release. The newer cached release was retained.");
                    if (_manifest is not null && _manifest.Version == manifest.Version && !SameReleaseAssets(_manifest, manifest))
                        throw new InvalidDataException("Published update files changed without a new release version. The verified release was retained.");
                }
                if (!result.NotPublished) { _manifest = manifest; _etag = result.ETag; _candidate = candidate; }
                _lastChecked = _clock.GetUtcNow(); _failureCount = 0;
                _nextCheck = _lastChecked + TimeSpan.FromHours(Preferences.CheckIntervalHours) + Jitter();
                Persist(); PublishRestingState(result.NotPublished ? "No update has been published yet." : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { PublishRestingState("Update check cancelled."); throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException)
            { RecordFailure(error); }
        }
        finally { _gate.Release(); }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperation(cancellationToken);
        cancellationToken = operation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State == UpdateState.Installing) return;
            var candidate = _candidate ?? throw new InvalidOperationException("Check for an available update first.");
            if (_identity.InstallerKind is UpdateInstallerKind.Store or UpdateInstallerKind.Portable)
                throw new InvalidOperationException("This installation must be updated through its original distribution method.");
            UpdateManifestRules.ValidateCandidate(candidate, _identity);
            if (_earliestRequest > _clock.GetUtcNow() && _failureCount > 0)
            { Publish(UpdateState.Deferred, "The update server requested a delay before another download."); return; }
            if (_ready && _pending == candidate && await _cache.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false))
            { PublishRestingState(); return; }
            // The newer candidate has been validated before removing an older ready installer.
            // Persist its removal before issuing any network download, so a crash cannot install it.
            _cache.RemovePackagesExcept(null); _pending = null; _ready = false; Persist();
            Publish(UpdateState.Downloading, $"Downloading CloudBay {candidate.Version}…");
            try
            {
                var lastProgress = _clock.GetTimestamp();
                using (var output = _cache.CreatePartial(candidate))
                {
                    await _transport.DownloadAsync(candidate, output, bytes =>
                    {
                        if (bytes == candidate.Asset.Size || _clock.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(250))
                        { lastProgress = _clock.GetTimestamp(); Publish(UpdateState.Downloading, $"Downloading CloudBay {candidate.Version}…", bytes); }
                    }, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _cache.Promote(candidate); _pending = candidate; _ready = true; _failureCount = 0;
                Persist(); PublishRestingState();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { PublishRestingState("Update download cancelled. No incomplete installer was retained."); throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException)
            { RecordFailure(error); }
            finally { _cache.DeletePartial(candidate); }
        }
        finally { _gate.Release(); }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperation(cancellationToken);
        cancellationToken = operation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State == UpdateState.Installing) return;
            if (_installer is null || _identity.InstallerKind is UpdateInstallerKind.Store or UpdateInstallerKind.Portable)
                throw new InvalidOperationException("Automatic installation is unavailable for this distribution.");
            if (!_ready || _pending is null || _pending != _candidate)
                throw new InvalidOperationException("Download the current available update before installing.");
            UpdateManifestRules.ValidateCandidate(_pending, _identity);
            if (!await _cache.VerifyAsync(_pending, cancellationToken).ConfigureAwait(false))
            {
                _cache.RemovePackagesExcept(null); _pending = null; _ready = false; Persist();
                Publish(UpdateState.Error, "The cached installer changed. Download a verified copy before installing."); return;
            }
            Publish(UpdateState.Installing, $"Installing CloudBay {_pending.Version}…");
            try
            {
                _lastInstallAttempt = _clock.GetUtcNow(); Persist();
                await _installer.InstallAsync(new VerifiedUpdatePackage(_cache.PackagePath(_pending), _identity, _pending), cancellationToken)
                    .ConfigureAwait(false);
                Publish(UpdateState.Installing, "The update installer has started. CloudBay will reopen when it finishes.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { PublishRestingState(); throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            { Publish(UpdateState.Error, "The update could not be installed. " + error.Message); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Starts one bounded background scheduler. Manual checks still honor server delays.</summary>
    public void Start()
    {
        ThrowIfDisposed();
        lock (_lifetime) { _scheduler ??= RunSchedulerAsync(_lifetime.Token); }
    }

    private async Task RunSchedulerAsync(CancellationToken token)
    {
        try
        {
            await InitializeAsync(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                if (Snapshot.State != UpdateState.Installing)
                {
                    if (Preferences.AutomaticChecks) await CheckAsync(manual: false, token).ConfigureAwait(false);
                    if (_identity.InstallerKind is UpdateInstallerKind.Exe or UpdateInstallerKind.Msi &&
                        Preferences.AutomaticallyDownload && Snapshot.State == UpdateState.Available)
                        await DownloadAsync(token).ConfigureAwait(false);
                    if (Preferences.AutomaticallyInstall && _installer is not null && Snapshot.State == UpdateState.Ready &&
                        (_lastInstallAttempt is null || _lastInstallAttempt < _clock.GetUtcNow().AddHours(-1)))
                        await InstallAsync(token).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), _clock, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        { Publish(UpdateState.Error, "Automatic updates stopped: " + error.Message); }
    }

    private void RecordFailure(Exception error)
    {
        _failureCount = Math.Min(8, _failureCount + 1);
        var retry = _clock.GetUtcNow() + TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, _failureCount - 1)));
        if (error is UpdateNetworkException { RetryUtc: { } requested } && requested > retry) retry = requested;
        _earliestRequest = retry; _nextCheck = retry; Persist();
        Publish(UpdateState.Error, error.Message);
    }
    private TimeSpan Jitter() => TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(0, 301));
    private void Persist() => _cache.Write("state.json", new SavedState(1, _identity, _manifest, _etag, _pending,
        _ready, _lastChecked, _nextCheck, _earliestRequest, _failureCount, _lastInstallAttempt));
    private void PublishRestingState(string? message = null)
    {
        var state = _identity.InstallerKind == UpdateInstallerKind.Store ? UpdateState.StoreManaged :
            _candidate is null ? (_lastChecked is null ? UpdateState.Idle : UpdateState.UpToDate) :
            _ready && _pending == _candidate ? UpdateState.Ready : UpdateState.Available;
        Publish(state, message ?? (state switch
        {
            UpdateState.StoreManaged => "Microsoft Store manages updates for this installation.",
            UpdateState.Ready => $"CloudBay {_candidate!.Version} is downloaded and ready to install.",
            UpdateState.Available when _identity.InstallerKind == UpdateInstallerKind.Portable =>
                $"CloudBay {_candidate!.Version} is available. Download the portable package from GitHub Releases.",
            UpdateState.Available => $"CloudBay {_candidate!.Version} is available for your installed package type.",
            UpdateState.UpToDate => "You're up to date.", _ => "Updates have not been checked yet."
        }));
    }
    private void Publish(UpdateState state, string message, long downloaded = 0)
    {
        var snapshot = new UpdateSnapshot(state, message, _candidate, downloaded, _candidate?.Asset.Size ?? 0,
            _lastChecked, Later(_nextCheck, _earliestRequest),
            _ready && _pending is not null && _pending == _candidate ? _cache.PackagePath(_pending) : null);
        Volatile.Write(ref _snapshot, snapshot);
        if (Changed is not { } handlers) return;
        foreach (Action<UpdateSnapshot> listener in handlers.GetInvocationList())
            try { listener(snapshot); } catch { /* Presentation callbacks cannot invalidate a verified download. */ }
    }
    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null || first > second ? first : second;
    private static bool SameReleaseAssets(UpdateManifest first, UpdateManifest second) =>
        first.Assets.Count == second.Assets.Count && first.Assets.All(asset => second.Assets.Any(other =>
            asset.BuildFlavor == other.BuildFlavor && asset.InstallerKind == other.InstallerKind &&
            asset.Architecture == other.Architecture && asset.FileName == other.FileName && asset.Size == other.Size &&
            string.Equals(asset.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase)));
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private CancellationTokenSource CreateOperation(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        if (_scheduler is not null) await _scheduler.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _ownershipLock?.Dispose(); _ownershipLock = null; _transport.Dispose(); }
        finally { _gate.Release(); _lifetime.Dispose(); }
    }
}

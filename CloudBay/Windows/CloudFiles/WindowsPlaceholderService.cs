using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CloudBay.Core;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using FileAttributes = System.IO.FileAttributes;
using static CloudBay.Windows.CloudFiles.CloudFilesNative;

namespace CloudBay.Windows.CloudFiles;

/// <summary>Native Cloud Files provider. The OS owns cache residency, pinning, and Storage Sense eviction.</summary>
public sealed class WindowsPlaceholderService : IPlaceholderService
{
    public static readonly Guid ProviderId = new("cb8ba37b-49da-4693-a2be-bf88b98c6225");
    private static readonly SemaphoreSlim ResidencyOperations = new(16);
    private int _downloadWorkers = Math.Clamp(Environment.ProcessorCount * 2, 4, 8);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _callbackGate = new();
    private readonly ConcurrentDictionary<long, HydrationWork> _requests = new();
    private readonly ConcurrentDictionary<long, int> _checksumRestarts = new();
    private readonly ConcurrentDictionary<long, long> _checksumGenerations = new();
    private readonly ConcurrentDictionary<long, string> _cacheVerified = new();
    private readonly object _restartGate = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _cacheVerification = new();
    private readonly SemaphoreSlim _residencyGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _pendingPinPaths = new(StringComparer.OrdinalIgnoreCase);
    // Only explicit non-default pin preferences need history. File IDs avoid retaining every path
    // string, and let an attribute notification distinguish a pin change from ordinary hydration.
    private readonly ConcurrentDictionary<long, uint> _knownPinStates = new();
    private readonly ConcurrentDictionary<long, byte> _pendingDehydration = new();
    private FileSystemWatcher? _pinWatcher;
    private FileSystemWatcher? _rootPinWatcher;
    private Channel<string>? _pinChanges;
    private Task? _pinWorker;
    private Timer? _pinRetryTimer;
    private int _pinRescanRequested;
    private int _pinRetryNeeded;
    private int _pinStartupPending;
    private readonly Callback _fetchCallback;
    private readonly Callback _cancelCallback;
    private readonly Callback _validateCallback;
    private readonly Callback _closeCallback;
    private readonly CallbackRegistration[] _callbacks;
    private CancellationTokenSource _lifetime = new();
    private HydrationHandler? _hydrate;
    private string? _root;
    private string? _registrationId;
    private long _connection;
    private long _nextRequest;
    private volatile bool _connected;
    private bool _disposed;

    public string? RegistrationId => _registrationId;

    public void ConfigureTransferLimits(int downloadWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(downloadWorkers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(downloadWorkers, 16);
        Volatile.Write(ref _downloadWorkers, downloadWorkers);
    }

    public WindowsPlaceholderService()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("CloudBay Cloud Files requires a 64-bit Windows process.");
        VerifyNativeLayouts();
        _fetchCallback = OnFetchData;
        _cancelCallback = OnCancelFetchData;
        _validateCallback = OnValidateData;
        _closeCallback = OnFileClosed;
        _callbacks =
        [
            new() { Type = 0, Function = Marshal.GetFunctionPointerForDelegate(_fetchCallback) },
            new() { Type = 1, Function = Marshal.GetFunctionPointerForDelegate(_validateCallback) },
            new() { Type = 2, Function = Marshal.GetFunctionPointerForDelegate(_cancelCallback) },
            new() { Type = 6, Function = Marshal.GetFunctionPointerForDelegate(_closeCallback) },
            new() { Type = uint.MaxValue, Function = IntPtr.Zero },
        ];
    }

    public async Task ConnectAsync(string rootPath, string accountIdentity, HydrationHandler hydrate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hydrate);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentity);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected) throw new InvalidOperationException("Disconnect the previous sync root before connecting another one.");
            if (!StorageProviderSyncRootManager.IsSupported()) throw new PlatformNotSupportedException("Windows Cloud Files is unavailable on this computer.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var wasRegistered = ValidateRoot(root, accountIdentity);
            Directory.CreateDirectory(root);
            ValidateAncestorLinks(root);
            if (!wasRegistered) RejectOrphanedPlaceholders(root, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetRegistrationId(accountIdentity);
            var info = new StorageProviderSyncRootInfo
            {
                Id = id,
                Path = await StorageFolder.GetFolderFromPathAsync(root),
                DisplayNameResource = "CloudBay – Backblaze B2",
                IconResource = Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico") + ",0",
                ProviderId = ProviderId,
                Version = "2.0.0",
                HydrationPolicy = StorageProviderHydrationPolicy.Full,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed |
                    StorageProviderHydrationPolicyModifier.ValidationRequired,
                PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                AllowPinning = true,
                ShowSiblingsAsGroup = false,
                Context = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(accountIdentity)),
            };
            // Register does both the Cloud Files registration and the Explorer navigation registration.
            // No manual namespace registry edits, and no second, conflicting CfRegisterSyncRoot call.
            var registeredNow = false;
            try
            {
                StorageProviderSyncRootManager.Register(info);
                registeredNow = true;
                _registrationId = id;
                var registered = StorageProviderSyncRootManager.GetSyncRootInformationForId(id);
                if (!string.Equals(Path.TrimEndingDirectorySeparator(registered.Path.Path), root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Windows registered a different sync folder for this account.");
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
                _hydrate = hydrate;
                _root = root;
                _checksumRestarts.Clear();
                _checksumGenerations.Clear();
                _cacheVerified.Clear();
                _cacheVerification.Clear();
                Check(CfConnectSyncRoot(root, _callbacks, IntPtr.Zero, 4, out _connection));
                lock (_callbackGate) _connected = true;
                StartPinWatcher(root);
            }
            catch (Exception failure)
            {
                if (_connected)
                {
                    lock (_callbackGate) _connected = false;
                    StopPinWatcher();
                    _lifetime.Cancel();
                    Check(CfDisconnectSyncRoot(_connection));
                    await DrainWorkersAsync().ConfigureAwait(false);
                }
                _hydrate = null;
                _connection = 0;
                if (registeredNow && !wasRegistered)
                {
                    // No engine is running and no placeholders existed before this registration.
                    // Rolling back this new root cannot evict or delete pre-existing cloud data.
                    try { StorageProviderSyncRootManager.Unregister(id); _registrationId = null; }
                    catch (Exception rollback) { throw new AggregateException("Windows sync folder setup failed, and its new registration could not be rolled back.", failure, rollback); }
                }
                if (failure is COMException && !registeredNow)
                    throw new IOException("Windows could not register this sync folder. Choose a visible local folder outside AppData, temporary, or system folders.", failure);
                if (failure is COMException && registeredNow && _root is null)
                    throw new IOException("Windows could not expose this folder in File Explorer. Choose a visible folder outside AppData, temporary, or system folders.", failure);
                throw;
            }
        }
        finally { _connectionGate.Release(); }
    }

    public Task CreateOrUpdateAsync(string fullPath, CloudObject file, bool inSync, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = Serialize(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ValidateParentLinks(path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                var pinned = GCHandle.Alloc(identity, GCHandleType.Pinned);
                try
                {
                    var items = new[] { new CreateInfo
                    {
                        RelativeFileName = Path.GetFileName(path), FsMetadata = GetMetadata(file),
                        FileIdentity = pinned.AddrOfPinnedObject(), FileIdentityLength = (uint)identity.Length,
                        Flags = inSync ? 2u : 0u,
                    } };
                    Check(CfCreatePlaceholders(Path.GetDirectoryName(path)!, items, 1, 1, out var processed));
                    if (processed != 1) throw new IOException("Windows did not create the cloud placeholder.");
                    Check(items[0].Result);
                }
                finally { pinned.Free(); }
                return;
            }
            if (!IsPlaceholder(path)) throw new IOException($"Local data already exists at '{path}'. It must be uploaded or resolved before adding a cloud version.");
            using var handle = Open(path, exclusive: true, writable: true);
            var current = ReadPlaceholder(handle);
            if (current.File?.FileId == file.FileId) return;
            if (current.Info.InSyncState != 1 || current.Info.ModifiedDataSize != 0)
                throw new IOException($"'{path}' has local changes. CloudBay preserved the file for conflict resolution.");
            // Pinned content must be invalidated before it can represent a different remote version.
            var wasPinned = current.Info.PinState == 1;
            if (wasPinned) Check(CfSetPinState(handle, 0, 0, IntPtr.Zero));
            try
            {
                var metadata = GetMetadata(file);
                Check(CfUpdatePlaceholder(handle, metadata, identity, (uint)identity.Length, IntPtr.Zero, 0,
                    UpdateVerifyInSync | UpdateDehydrate | (inSync ? UpdateMarkInSync : 0x40u), IntPtr.Zero, IntPtr.Zero));
            }
            finally
            {
                if (wasPinned) Check(CfSetPinState(handle, 1, 0, IntPtr.Zero));
            }
            if (wasPinned) Check(CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
        }, cancellationToken);
    }

    public Task MarkInSyncAsync(string fullPath, CloudObject file, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath);
        return Task.Run(() =>
        {
            TransferResources.Hashing.Wait(cancellationToken);
            try
            {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = Serialize(file);
            using var handle = Open(path, exclusive: true, writable: true);
            if (IsPlaceholder(path))
            {
                var beforeUpload = ReadPlaceholder(handle).Info;
                if (beforeUpload.PinState == 2 && (beforeUpload.InSyncState != 1 || beforeUpload.ModifiedDataSize != 0))
                    _pendingDehydration[beforeUpload.FileId] = 0;
            }
            // Hold a reference while comparing the uploaded snapshot and marking it synced. An oplock
            // break waits for this reference; another writer cannot slip between these operations.
            if (!CfReferenceProtectedHandle(handle)) throw new IOException("The file changed while CloudBay was completing its upload.");
            try
            {
                var rawHandle = CfGetWin32HandleFromProtectedHandle(handle);
                if (!GetFileInformationByHandle(rawHandle, out var local)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (local.Size != file.Size || Math.Abs(local.Modified - file.ModifiedUtc.UtcDateTime.ToFileTimeUtc()) >= TimeSpan.TicksPerMillisecond)
                    throw new IOException("The file changed during its upload. Its newer contents remain queued for backup.");
                // Size and timestamp alone cannot detect a same-size save that preserves mtime.
                // Hash through the referenced exclusive handle, then update identity before releasing
                // it, so a late save cannot be incorrectly marked clean and subsequently evicted.
                if (file.Sha1 is { Length: 40 } sha1 && sha1.All(Uri.IsHexDigit) &&
                    !HashProtectedFile(rawHandle, local.Size, cancellationToken).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The file contents changed during its upload. Its newer contents remain queued for backup.");
                if (IsPlaceholder(path))
                    Check(CfUpdateIdentity(handle, IntPtr.Zero, identity, (uint)identity.Length, IntPtr.Zero, 0, UpdateMarkInSync, IntPtr.Zero, IntPtr.Zero));
                else
                    Check(CfConvertToPlaceholder(handle, identity, (uint)identity.Length, 1, IntPtr.Zero, IntPtr.Zero));
                Check(CfSetInSyncState(handle, 1, 0, IntPtr.Zero));
                // Windows' in-sync transition need not produce a directory attribute event on
                // every build. Retry deferred Shell unpin intent after a verified upload too.
                QueuePinChange(path);
            }
            finally { CfReleaseProtectedHandle(handle); }
            }
            finally { TransferResources.Hashing.Release(); }
        }, cancellationToken);
    }

    private static string HashProtectedFile(IntPtr handle, long length, CancellationToken cancellationToken)
    {
        // CfOpenFileWithOplock returns an asynchronous native handle. Use our own OVERLAPPED event;
        // do not bind or close the borrowed handle, whose lifetime is owned by the Cloud Files API.
        var buffer = new byte[128 * 1024];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<ReadOverlapped>());
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        try
        {
            for (long offset = 0; offset < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completed.Reset();
                Marshal.StructureToPtr(new ReadOverlapped
                {
                    Offset = (uint)offset, OffsetHigh = (uint)(offset >> 32),
                    // Suppress I/O completion-port dispatch: the borrowed handle may belong to
                    // CldApi's own port, whose callbacks must never receive our OVERLAPPED storage.
                    EventHandle = new IntPtr(completed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1),
                }, overlapped, false);
                var requested = (uint)Math.Min(buffer.Length, length - offset);
                if (!ReadFile(handle, pinned.AddrOfPinnedObject(), requested, IntPtr.Zero, overlapped))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 997) throw new Win32Exception(error); // ERROR_IO_PENDING is expected.
                }
                // Retain the pinned buffer, event and OVERLAPPED storage until native I/O completes.
                if (!GetOverlappedResult(handle, overlapped, out var read, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (read == 0 || read > requested) throw new IOException("The file changed during checksum verification.");
                hash.AppendData(buffer, 0, (int)read);
                offset += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { Marshal.FreeHGlobal(overlapped); pinned.Free(); }
    }

    public bool IsPlaceholder(string fullPath)
    {
        try { var state = State(fullPath); return state != uint.MaxValue && (state & Placeholder) != 0; }
        catch (Win32Exception) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    public bool IsHydrated(string fullPath)
    {
        try
        {
            // Attribute flags can lag an interrupted hydration. Only the resident range map
            // proves that every logical byte is actually available.
            return IsFullyResident(fullPath);
        }
        catch (Win32Exception) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    public bool HasLocalChanges(string fullPath)
    {
        var path = ValidateFilePath(fullPath);
        // A regular file at a tracked path can be an atomic replacement whose metadata matches
        // the baseline. Uploaded files become placeholders, so it must be reconciled again.
        if (!IsPlaceholder(path)) return true;
        // CfOpenFileWithOplock can hydrate a file on open. An attribute-only reparse handle is
        // sufficient for CfGetPlaceholderInfo and leaves online-only content untouched.
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(4160);
        try
        {
            Check(CfGetPlaceholderInfo(handle, 1, buffer, 4160, out _));
            var info = Marshal.PtrToStructure<StandardInfo>(buffer);
            return info.InSyncState != 1 || info.ModifiedDataSize != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public Task SetPinAsync(string fullPath, PinMode mode, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath, allowRoot: true);
        return Task.Run(async () =>
        {
            await _residencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            cancellationToken.ThrowIfCancellationRequested();
            var pin = mode == PinMode.AlwaysAvailable ? 1u : mode == PinMode.OnlineOnly ? 2u : 0u;
            // Traverse only our guarded namespace. A native recursive operation must not walk
            // through an unrelated junction below a user-selected folder.
            foreach (var item in EnumerateResidencyPaths(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFilePath(item, allowRoot: true);
                // Opening with the CFAPI data oplock can implicitly hydrate the first file
                // before later workers are admitted. Pin intent needs attributes only.
                using var handle = CreateFileW(item, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                Check(CfSetPinState(handle, pin, 0, IntPtr.Zero));
            }
            if (mode == PinMode.OnlineOnly) { FreeSpaceCore(path, cancellationToken); return; }
            await HydrateFilesAsync(path, mode == PinMode.AlwaysAvailable, cancellationToken).ConfigureAwait(false);
            foreach (var item in EnumerateFiles(path)) RememberPinState(item);
            }
            finally { _residencyGate.Release(); }
        }, cancellationToken);
    }

    public Task HydrateAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath, allowRoot: true);
        return Task.Run(async () =>
        {
            await _residencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            await HydrateFilesAsync(path, requirePinned: false, cancellationToken).ConfigureAwait(false);
            }
            finally { _residencyGate.Release(); }
        }, cancellationToken);
    }

    private Task HydrateFilesAsync(string path, bool requirePinned, CancellationToken ct) =>
        Parallel.ForEachAsync(EnumerateFiles(path), new ParallelOptions
        { MaxDegreeOfParallelism = Volatile.Read(ref _downloadWorkers), CancellationToken = ct },
        async (item, token) =>
        {
            ValidateFilePath(item);
            if (!IsPlaceholder(item) || IsHydrated(item)) return;
            await HydrateOneAsync(item, requirePinned, token).ConfigureAwait(false);
        });

    private static async Task HydrateOneAsync(string path, bool requirePinned, CancellationToken ct)
    {
        await ResidencyOperations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (requirePinned)
                {
                    var buffer = Marshal.AllocHGlobal(4160);
                    try
                    {
                        Check(CfGetPlaceholderInfo(handle, 1, buffer, 4160, out _));
                        if (Marshal.PtrToStructure<StandardInfo>(buffer).PinState != 1) return;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                Check(CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
            }, ct).ConfigureAwait(false);
        }
        finally { ResidencyOperations.Release(); }
    }

    public Task FreeSpaceAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath, allowRoot: true);
        return Task.Run(async () =>
        {
            await _residencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { FreeSpaceCore(path, cancellationToken); }
            finally { _residencyGate.Release(); }
        }, cancellationToken);
    }

    /// <summary>
    /// Used only for the user's explicit account disconnect. Ordinary shutdown retains placeholders.
    /// Revert before unregister, which would otherwise delete dehydrated files or silently skip failures.
    /// The caller must quiesce the sync engine before calling this method.
    /// </summary>
    public async Task PrepareForUnregisterAsync(CancellationToken cancellationToken = default)
    {
        var root = ValidateFilePath(_root ?? throw new InvalidOperationException("No sync root is connected."), allowRoot: true);
        try
        {
            await SetPinAsync(root, PinMode.AlwaysAvailable, cancellationToken).ConfigureAwait(false);
            // The final CfExecute can release Windows' hydrate call just before its provider callback
            // releases network buffers/handles. Finish those callbacks before acquiring revert oplocks.
            await Task.WhenAll(_requests.Values.Select(work => work.Completion.Task))
                .WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or Win32Exception or IOException or TimeoutException)
        { throw UnregisterPreparationError(root, "download files for disconnect", error); }
        await Task.Run(() =>
        {
            foreach (var path in EnumerateFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlaceholder(path)) continue;
                try
                {
                    using var handle = Open(path, exclusive: true, writable: true);
                    Check(CfRevertPlaceholder(handle, 0, IntPtr.Zero));
                    if (IsPlaceholder(path)) throw new IOException("Windows did not remove the cloud file state.");
                }
                catch (Exception error) when (error is COMException or Win32Exception or IOException)
                { throw UnregisterPreparationError(path, "convert the cloud file into an ordinary local file", error); }
            }
            foreach (var path in EnumerateDirectories(root).OrderByDescending(p => p.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlaceholder(path)) continue;
                try
                {
                    using var handle = Open(path, exclusive: true, writable: true);
                    Check(CfRevertPlaceholder(handle, 0, IntPtr.Zero));
                    if (IsPlaceholder(path)) throw new IOException("Windows did not remove the cloud folder state.");
                }
                catch (Exception error) when (error is COMException or Win32Exception or IOException)
                { throw UnregisterPreparationError(path, "convert the cloud folder into an ordinary local folder", error); }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IOException UnregisterPreparationError(string path, string operation, Exception error) =>
        new($"CloudBay could not {operation} at '{path}' (Windows error 0x{error.HResult:X8}). " +
            "Close apps using these files, check the connection, and retry. The provider registration and remaining cloud files were retained.", error);

    private void FreeSpaceCore(string path, CancellationToken cancellationToken)
    {
        foreach (var item in EnumerateResidencyPaths(path))
        {
            // Clear the selected directory's offline intent too, so newly created children do not
            // inherit PINNED after "Free up space". Dirty files still retain their local bytes.
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFilePath(item, allowRoot: true);
            if (!Directory.Exists(item)) continue;
            using var directory = CreateFileW(item, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (directory.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Check(CfSetPinState(directory, 2, 0, IntPtr.Zero));
        }
        foreach (var item in EnumerateFiles(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPlaceholder(item)) continue;
            using var handle = Open(item, exclusive: true, writable: true);
            var current = ReadPlaceholder(handle);
            if (current.Info.InSyncState != 1 || current.Info.ModifiedDataSize != 0 || current.File is null)
                throw new IOException($"'{item}' has changes that are not backed up. It cannot be made online only.");
            Check(CfSetPinState(handle, 2, 0, IntPtr.Zero));
            Check(CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
            _knownPinStates[current.Info.FileId] = 2;
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_connected) return;
            lock (_callbackGate) _connected = false;
            StopPinWatcher();
            _lifetime.Cancel();
            // Disconnect releases waiting user I/O. Registration and user data remain for the next run.
            Check(CfDisconnectSyncRoot(_connection));
            _connection = 0;
            await DrainWorkersAsync().ConfigureAwait(false);
            _pinWorker = null;
            _knownPinStates.Clear();
            _pendingDehydration.Clear();
            _pendingPinPaths.Clear();
            _hydrate = null;
        }
        finally { _connectionGate.Release(); }
    }

    private async Task DrainWorkersAsync()
    {
        // A defective or unavailable transport must not hold shutdown forever. Native callbacks
        // have been disconnected before this wait; remaining tasks retain their own cancelled
        // lifetime and owned buffers until they unwind, and cannot start a new hydration.
        var pending = _requests.Values.Select(w => w.Completion.Task).ToList();
        if (_pinWorker is { } pinWorker) pending.Add(pinWorker);
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
        _lifetime.Dispose();
        // Callback delegates and their table stay rooted for this service's complete lifetime.
        GC.KeepAlive(_callbacks);
        GC.KeepAlive(_fetchCallback);
        GC.KeepAlive(_cancelCallback);
        GC.KeepAlive(_validateCallback);
        GC.KeepAlive(_closeCallback);
    }

    private void OnFetchData(in CallbackInfo info, in CallbackParameters parameters) => OnData(info, parameters, validateOnly: false);
    private void OnValidateData(in CallbackInfo info, in CallbackParameters parameters) => OnData(info, parameters, validateOnly: true);

    private void OnFileClosed(in CallbackInfo info, in CallbackParameters parameters)
    {
        // Keep the exhausted retry marker until Windows closes the user request's last handle.
        // Removing it in an overlapping callback would give the same read unbounded retries.
        lock (_callbackGate)
        {
            var fileId = info.FileId;
            if (_requests.Values.Any(work => work.FileId == fileId)) return;
            _checksumRestarts.TryRemove(fileId, out _);
            _checksumGenerations.TryRemove(fileId, out _);
            _cacheVerified.TryRemove(fileId, out _);
            _cacheVerification.TryRemove(fileId, out _);
        }
    }

    private void OnData(in CallbackInfo callbackInfo, in CallbackParameters parameters, bool validateOnly)
    {
        // Native pointers are valid only during this callback. Copy the identity and operation keys.
        var operation = new OperationInfo
        {
            StructSize = (uint)Marshal.SizeOf<OperationInfo>(), Type = 0,
            ConnectionKey = callbackInfo.ConnectionKey, TransferKey = callbackInfo.TransferKey, RequestKey = callbackInfo.RequestKey,
        };
        var offset = Math.Max(0, parameters.RequiredOffset & ~4095L);
        var requestedLength = parameters.RequiredLength;
        var fileSize = callbackInfo.FileSize;
        var nativeFileId = callbackInfo.FileId;
        var length = fileSize - offset;
        try
        {
            if (!_connected || _hydrate is null) throw new OperationCanceledException();
            if (_checksumRestarts.TryGetValue(nativeFileId, out var restartCount) && restartCount >= 2)
            {
                // The second rejected attempt has already discarded every resident range.
                // End its reissued request before downloading again; a fresh user retry starts
                // from the now-empty cache and gets its own bounded recovery opportunity.
                throw new IOException("The native download failed checksum verification after a bounded retry.");
            }
            if (callbackInfo.FileIdentityLength is 0 or > 4096 || fileSize < 0 || parameters.RequiredOffset < 0 || (requestedLength <= 0 && requestedLength != -1))
                throw new IOException("Windows supplied an invalid cloud hydration request.");
            var bytes = new byte[callbackInfo.FileIdentityLength];
            Marshal.Copy(callbackInfo.FileIdentity, bytes, 0, bytes.Length);
            var file = JsonSerializer.Deserialize<CloudObject>(bytes) ?? throw new IOException("Cloud object identity is missing.");
            var generation = _checksumGenerations.GetOrAdd(nativeFileId, 0);
            var locallyModified = validateOnly && HasModifiedNativeRanges(operation, nativeFileId, fileSize);
            if ((!locallyModified && file.Size != fileSize) || offset >= fileSize)
                throw new IOException("Cloud object identity does not match the placeholder.");
            if (locallyModified) file = file with { Size = fileSize };
            // Windows can request a smaller required range even with FULL policy. Retrieve the
            // whole missing remainder, as CFAPI explicitly permits, so a first 0..EOF request
            // receives transport checksum verification and its final bytes stay unpublished.
            length = fileSize - offset;
            long id;
            HydrationWork work;
            HydrationHandler hydrate;
            lock (_callbackGate)
            {
                // Disconnect can race the callback after its native identity has been copied.
                // Admit work atomically with the connected state so the drain cannot miss it.
                if (!_connected || _hydrate is null) throw new OperationCanceledException();
                id = Interlocked.Increment(ref _nextRequest);
                work = new HydrationWork(operation, nativeFileId, validateOnly, offset, length, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _requests[id] = work;
                hydrate = _hydrate;
            }
            _ = Task.Run(async () =>
            {
                HydrationStream? destination = null;
                Exception? validationFailure = null;
                try
                {
                    if (validateOnly)
                    {
                        var transfers = _requests.Values.Where(pending => pending.FileId == nativeFileId && !pending.ValidateOnly)
                            .Select(pending => pending.DataReady.Task).ToArray();
                        // A validation callback can arrive while the fetch is still transferring.
                        // Wait for persisted EOF without occupying the shared disk/hash budget.
                        var ready = await Task.WhenAll(transfers).WaitAsync(work.Cancellation.Token).ConfigureAwait(false);
                        if (ready.Any(success => !success)) return; // The fetch itself failed/restarted this request.
                    }
                    else
                    {
                        destination = new HydrationStream(operation, offset, length, work.Cancellation.Token);
                        await hydrate(file, offset, length, destination, work.Cancellation.Token).ConfigureAwait(false);
                        destination.Complete();
                        work.DataReady.TrySetResult(true);
                    }
                    await VerifyNativeCacheAsync(operation, nativeFileId, file, work.Cancellation.Token).ConfigureAwait(false);
                    destination?.MarkValidated();
                    _checksumRestarts.TryRemove(nativeFileId, out _);
                }
                catch (Exception ex)
                {
                    validationFailure = ex;
                    if (ex is InvalidDataException && _connected && !work.Cancellation.IsCancellationRequested)
                    {
                        try
                        {
                            if (HasModifiedNativeRanges(operation, nativeFileId, fileSize))
                            {
                                // Locally written ranges are authoritative, even if an older remote
                                // identity no longer matches. Never restart/dehydrate unsent edits.
                                AcknowledgeNativeCache(operation, fileSize);
                                destination?.MarkValidated();
                                return;
                            }
                            lock (_restartGate)
                            {
                                if (_checksumGenerations.GetValueOrDefault(nativeFileId) != generation)
                                {
                                    validationFailure = new OperationCanceledException("A newer native cache recovery superseded this callback.");
                                    return;
                                }
                                var attempts = _checksumRestarts.AddOrUpdate(nativeFileId, 1, (_, previous) => Math.Min(2, previous + 1));
                                _checksumGenerations[nativeFileId] = generation + 1;
                                _cacheVerified.TryRemove(nativeFileId, out _);
                                // This FULL-policy operation is specifically defined by CFAPI for bad
                                // checksum recovery. It discards the rejected prefix before Windows can
                                // reissue a tail-only fetch and make corrupted bytes readable.
                                var restart = operation; restart.Type = 3;
                                var result = CfRestartHydration(restart, new RestartParameters { ParamSize = 40 });
                                if (result >= 0)
                                {
                                    // A recoverable attempt is interrupted, rather than a completed
                                    // download or final error in Activity. The replacement callback owns
                                    // its own completion. The exhausted attempt remains a final fault.
                                    if (attempts < 2) validationFailure = new OperationCanceledException("Native checksum recovery is restarting the download.");
                                    return;
                                }
                            }
                        }
                        catch (Exception recovery) when (recovery is COMException or Win32Exception or IOException or EntryPointNotFoundException)
                        { /* Preserve the failed request if safe native recovery is unavailable. */ }
                    }
                    CompleteFailure(operation, offset, length,
                        ex is OperationCanceledException ? CloudCancelled : ex is HttpRequestException ? CloudNetworkUnavailable : CloudUnsuccessful);
                }
                finally
                {
                    destination?.MarkValidationFailed(validationFailure ?? new OperationCanceledException(work.Cancellation.Token));
                    destination?.Dispose();
                    lock (_callbackGate)
                    {
                        _requests.TryRemove(id, out _);
                        if (!_requests.Values.Any(pending => pending.FileId == nativeFileId))
                        {
                            _cacheVerification.TryRemove(nativeFileId, out _);
                            _cacheVerified.TryRemove(nativeFileId, out _);
                        }
                    }
                    work.DataReady.TrySetResult(false);
                    work.Cancellation.Dispose();
                    work.Completion.TrySetResult();
                }
            });
        }
        catch (Exception ex) { CompleteFailure(operation, offset, Math.Max(1, length), ex is OperationCanceledException ? CloudCancelled : CloudUnsuccessful); }
    }

    private void OnCancelFetchData(in CallbackInfo info, in CallbackParameters parameters)
    {
        var transfer = info.TransferKey;
        var request = info.RequestKey;
        var offset = parameters.RequiredOffset;
        var length = parameters.RequiredLength;
        foreach (var work in _requests.Values)
        {
            if (work.Operation.TransferKey != transfer || (request != 0 && work.Operation.RequestKey != request)) continue;
            if (length > 0 && (offset >= work.Offset + work.Length || offset + length <= work.Offset)) continue;
            try { work.Cancellation.Cancel(); }
            catch (Exception) { } // Exceptions must never escape a native callback boundary.
        }
    }

    private static void CompleteFailure(OperationInfo operation, long offset, long length, int status)
    {
        operation.Type = 2;
        var parameters = new AckParameters { ParamSize = 32, CompletionStatus = status, Offset = offset, Length = length };
        _ = CfAckData(operation, parameters); // A cancelled/disconnected request may already be completed by Windows.
    }

    private async Task VerifyNativeCacheAsync(OperationInfo operation, long nativeFileId, CloudObject file, CancellationToken token)
    {
        var gate = _cacheVerification.GetOrAdd(nativeFileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HasModifiedNativeRanges(operation, nativeFileId, file.Size))
            {
                AcknowledgeNativeCache(operation, file.Size);
                return;
            }
            if (_cacheVerified.GetValueOrDefault(nativeFileId) == file.FileId && NativeRangeIsComplete(operation, nativeFileId, file.Size, 2))
            {
                AcknowledgeNativeCache(operation, file.Size);
                return;
            }
            if (!NativeRangeIsComplete(operation, nativeFileId, file.Size, 1))
                throw new IOException("The native cache is not yet fully resident for validation.");
            await TransferResources.NativeValidation.WaitAsync(token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var retrieve = operation; retrieve.Type = 1;
                for (long offset = 0; offset < file.Size;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(64 * 1024, file.Size - offset);
                    var parameters = new RetrieveParameters
                    { ParamSize = 48, Buffer = pinned.AddrOfPinnedObject(), Offset = offset, Length = count };
                    Check(CfRetrieveData(retrieve, ref parameters));
                    if (parameters.ReturnedLength != count) throw new EndOfStreamException("Windows returned an incomplete native cache range.");
                    hash.AppendData(buffer.AsSpan(0, checked((int)count)));
                    offset += count;
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                var locallyModified = HasModifiedNativeRanges(operation, nativeFileId, file.Size);
                if (!locallyModified && file.Sha1 is { Length: 40 } expected && expected.All(Uri.IsHexDigit) &&
                    !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The assembled native cache failed whole-file SHA1 verification.");
                if (!locallyModified && (file.Sha1 is not { Length: 40 } digest || !digest.All(Uri.IsHexDigit)))
                    System.Diagnostics.Trace.TraceWarning("A legacy cloud version has no whole-file SHA1. Its native download uses authenticated HTTPS, immutable version identity and exact range/length validation; it is not checksum-verified.");
                token.ThrowIfCancellationRequested();
                // VALIDATION_REQUIRED keeps every user read blocked until the bytes actually
                // persisted in Windows' own cache, including a resumed prefix, pass this check.
                AcknowledgeNativeCache(operation, file.Size);
                _cacheVerified[nativeFileId] = file.FileId;
            }
            finally
            {
                pinned.Free(); ArrayPool<byte>.Shared.Return(buffer); TransferResources.NativeValidation.Release();
            }
        }
        finally { gate.Release(); }
    }

    private static bool HasModifiedNativeRanges(OperationInfo operation, long fileId, long size)
    {
        if (size == 0) return false;
        // Never open a data handle from a hydration callback: antivirus/minifilters could
        // recursively recall the same file. This API queries cldflt directly by native keys.
        var result = CfGetPlaceholderRangeInfoForHydration(operation.ConnectionKey, operation.TransferKey, fileId,
            3, 0, size, out var range, 16, out var returned);
        if (result == unchecked((int)0x800700EA)) return true; // More than one dirty range also means modified.
        Check(result);
        return returned >= 16 && range.Length > 0;
    }

    private static bool NativeRangeIsComplete(OperationInfo operation, long fileId, long size, uint kind)
    {
        if (size == 0) return true;
        var result = CfGetPlaceholderRangeInfoForHydration(operation.ConnectionKey, operation.TransferKey, fileId,
            kind, 0, size, out var range, 16, out var returned);
        return result >= 0 && returned == 16 && range.Offset == 0 && range.Length >= size;
    }

    private static void AcknowledgeNativeCache(OperationInfo operation, long size)
    {
        operation.Type = 2;
        Check(CfAckData(operation, new AckParameters { ParamSize = 32, Offset = 0, Length = size }));
    }

    private sealed class HydrationWork(OperationInfo operation, long fileId, bool validateOnly, long offset, long length, CancellationTokenSource cancellation)
    {
        internal OperationInfo Operation { get; } = operation;
        internal long FileId { get; } = fileId;
        internal bool ValidateOnly { get; } = validateOnly;
        internal long Offset { get; } = offset;
        internal long Length { get; } = length;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DataReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void StartPinWatcher(string root)
    {
        _knownPinStates.Clear();
        _pendingDehydration.Clear();
        _pendingPinPaths.Clear();
        _pinRescanRequested = 0;
        _pinRetryNeeded = 0;
        _pinStartupPending = 1;
        var changes = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _pinChanges = changes;
        _pinWatcher = new FileSystemWatcher(root)
        { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Attributes, InternalBufferSize = 16 * 1024 };
        _pinWatcher.Changed += (_, args) => QueuePinChange(args.FullPath);
        _pinWatcher.Error += (_, _) => { Interlocked.Exchange(ref _pinRescanRequested, 1); QueuePinChange(root); };
        // Watching a directory reports its children, not changes to the directory itself.
        // Observe this one root entry from its parent so root-wide Shell availability actions
        // also apply to cached descendants whose existing UNPINNED bit did not change.
        _rootPinWatcher = new FileSystemWatcher(Path.GetDirectoryName(root)!, Path.GetFileName(root))
        { NotifyFilter = NotifyFilters.Attributes, IncludeSubdirectories = false };
        _rootPinWatcher.Changed += (_, _) => QueuePinChange(root);
        _rootPinWatcher.Error += (_, _) => { Interlocked.Exchange(ref _pinRescanRequested, 1); QueuePinChange(root); };
        var lifetime = _lifetime.Token;
        _pinWorker = Task.Run(() => ProcessPinChangesAsync(changes.Reader, lifetime));
        _pinWatcher.EnableRaisingEvents = true;
        _rootPinWatcher.EnableRaisingEvents = true;
        _pinRetryTimer = new Timer(_ =>
        {
            if (_connected && Interlocked.Exchange(ref _pinRetryNeeded, 0) != 0)
            { Interlocked.Exchange(ref _pinRescanRequested, 1); QueuePinChange(root); }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        // Persisted PINNED intent must also work after a provider restart while offline.
        QueuePinChange(root);
    }

    private void StopPinWatcher()
    {
        _pinWatcher?.Dispose(); _pinWatcher = null;
        _rootPinWatcher?.Dispose(); _rootPinWatcher = null;
        _pinRetryTimer?.Dispose(); _pinRetryTimer = null;
        _pinChanges?.Writer.TryComplete(); _pinChanges = null;
    }

    private void QueuePinChange(string path)
    {
        if (!_connected || _pinChanges is not { } changes || !_pendingPinPaths.TryAdd(path, 0)) return;
        if (changes.Writer.TryWrite(path)) return;
        _pendingPinPaths.TryRemove(path, out _);
        // Work remains bounded during a recursive Shell operation. A single scan recovers any
        // paths dropped by a full queue or native watcher buffer overflow.
        Interlocked.Exchange(ref _pinRescanRequested, 1);
    }

    private async Task ProcessPinChangesAsync(ChannelReader<string> changes, CancellationToken ct)
    {
        try
        {
            await foreach (var path in changes.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _pendingPinPaths.TryRemove(path, out _);
                var initialScan = string.Equals(path, _root, StringComparison.OrdinalIgnoreCase) &&
                    Interlocked.Exchange(ref _pinStartupPending, 0) != 0;
                await ProcessPinPathAsync(path, startup: initialScan, ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _pinRescanRequested, 0) != 0 && _root is { } root)
                    await ProcessPinPathAsync(root, startup: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ProcessPinPathAsync(string fullPath, bool startup, CancellationToken ct)
    {
        await _residencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || (!File.Exists(fullPath) && !Directory.Exists(fullPath))) return;
            var path = ValidateFilePath(fullPath, allowRoot: true);
            var recursiveUnpin = false;
            if (Directory.Exists(path))
            {
                foreach (var directory in new[] { path }.Concat(EnumerateDirectories(path)))
                {
                    ct.ThrowIfCancellationRequested();
                    var intent = ReadDirectoryPinIntent(directory);
                    var previous = _knownPinStates.GetValueOrDefault(intent.FileId);
                    if (!startup && directory.Equals(path, StringComparison.OrdinalIgnoreCase) && intent.PinState == 2 && previous != 2)
                        recursiveUnpin = true;
                    if (intent.PinState == 0) _knownPinStates.TryRemove(intent.FileId, out _);
                    else _knownPinStates[intent.FileId] = intent.PinState;
                }
            }
            await Parallel.ForEachAsync(EnumerateFiles(path), new ParallelOptions
            { MaxDegreeOfParallelism = Volatile.Read(ref _downloadWorkers), CancellationToken = ct },
                (item, token) => ProcessPinFileAsync(item, startup, recursiveUnpin, token)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or Win32Exception or IOException)
        {
            Interlocked.Exchange(ref _pinRetryNeeded, 1);
            System.Diagnostics.Trace.TraceWarning("CloudBay will retry a Windows file availability change (0x{0:X8}).", error.HResult);
        }
        catch (InvalidOperationException) when (ct.IsCancellationRequested || !_connected)
        { /* A watcher notification admitted immediately before disconnect is cancelled work. */ }
        finally { _residencyGate.Release(); }
    }

    private async ValueTask ProcessPinFileAsync(string item, bool startup, bool recursiveUnpin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateFilePath(item);
        if (!IsPlaceholder(item)) return;
        var information = ReadPlaceholderInformation(item);
        var previous = _knownPinStates.GetValueOrDefault(information.FileId);
        if (information.PinState != 2) _pendingDehydration.TryRemove(information.FileId, out _);
        if (information.PinState == 2 && (information.InSyncState != 1 || information.ModifiedDataSize != 0))
            _pendingDehydration[information.FileId] = 0;
        if (startup && information.PinState == 2 && previous == 0 && !_pendingDehydration.ContainsKey(information.FileId))
        {
            _knownPinStates[information.FileId] = 2;
            return; // Existing cached, unpinned files remain cached across restarts.
        }
        if (information.PinState == 1 && (startup || previous != 1) && !IsHydrated(item))
            await HydrateOneAsync(item, requirePinned: true, ct).ConfigureAwait(false);
        else if (information.PinState == 2 && (recursiveUnpin || previous != 2 || _pendingDehydration.ContainsKey(information.FileId)) && information.OnDiskDataSize != 0)
        {
            // The exclusive oplock plus a second dirty check protects edits racing a
            // Shell unpin notification. Never dehydrate an unuploaded or anonymous file.
            using var handle = Open(item, exclusive: true, writable: true);
            var current = ReadPlaceholder(handle);
            if (current.Info.FileId != information.FileId || current.Info.PinState != 2 || current.Info.InSyncState != 1 ||
                current.Info.ModifiedDataSize != 0 || current.File is null) return;
            Check(CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
            _pendingDehydration.TryRemove(information.FileId, out _);
        }
        if (information.PinState == 0) _knownPinStates.TryRemove(information.FileId, out _);
        else _knownPinStates[information.FileId] = information.PinState;
    }

    private void RememberPinState(string path)
    {
        if (!IsPlaceholder(path)) return;
        var information = ReadPlaceholderInformation(path);
        if (information.PinState == 0) _knownPinStates.TryRemove(information.FileId, out _);
        else _knownPinStates[information.FileId] = information.PinState;
    }

    private static (long FileId, uint PinState) ReadDirectoryPinIntent(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle.DangerousGetHandle(), out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var pin = (information.Attributes & 0x80000u) != 0 ? 1u : (information.Attributes & 0x100000u) != 0 ? 2u : 0u;
        return (((long)information.IndexHigh << 32) | information.IndexLow, pin);
    }

    private static StandardInfo ReadPlaceholderInformation(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(4160);
        try
        {
            Check(CfGetPlaceholderInfo(handle, 1, buffer, 4160, out _));
            return Marshal.PtrToStructure<StandardInfo>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static byte[] Serialize(CloudObject file)
    {
        if (file.Size < 0 || string.IsNullOrEmpty(file.FileId) || file.Action != "upload") throw new IOException("Only a valid uploaded cloud file can become a placeholder.");
        var identity = JsonSerializer.SerializeToUtf8Bytes(file);
        if (identity.Length > 4096) throw new IOException("The cloud file identity is too long for Windows Cloud Files.");
        return identity;
    }

    private static Metadata GetMetadata(CloudObject file) => new()
    {
        BasicInfo = new BasicInfo { LastWriteTime = file.ModifiedUtc.UtcDateTime.ToFileTimeUtc(), FileAttributes = 0x20 },
        FileSize = file.Size,
    };

    private static (StandardInfo Info, CloudObject? File) ReadPlaceholder(ProtectedHandle handle)
    {
        const uint capacity = 64 + 4096;
        var buffer = Marshal.AllocHGlobal((int)capacity);
        try
        {
            Check(CfGetPlaceholderInfo(handle, 1, buffer, capacity, out var returned));
            var info = Marshal.PtrToStructure<StandardInfo>(buffer);
            if (info.FileIdentityLength == 0) return (info, null);
            if (info.FileIdentityLength > 4096 || returned < 60 + info.FileIdentityLength) throw new IOException("Windows returned a malformed placeholder identity.");
            var bytes = new byte[info.FileIdentityLength];
            Marshal.Copy(IntPtr.Add(buffer, 60), bytes, 0, bytes.Length);
            return (info, JsonSerializer.Deserialize<CloudObject>(bytes));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string ValidateFilePath(string path, bool allowRoot = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected || _root is null) throw new InvalidOperationException("CloudBay is not connected to its Windows sync root.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!(allowRoot && string.Equals(normalized, _root, StringComparison.OrdinalIgnoreCase)) &&
            !normalized.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The file is outside the CloudBay sync root.");
        ValidateParentLinks(normalized);
        return normalized;
    }

    private static bool ValidateRoot(string root, string account)
    {
        if (root.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Files On Demand requires a local NTFS drive.");
        var volume = new DriveInfo(Path.GetPathRoot(root)!);
        if (volume.DriveType != DriveType.Fixed || !string.Equals(volume.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The CloudBay folder must be on a local fixed NTFS drive.");
        if (string.Equals(Path.TrimEndingDirectorySeparator(volume.RootDirectory.FullName), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A drive root cannot be used as the CloudBay folder.");
        var id = GetRegistrationId(account);
        var sameRoot = false;
        foreach (var existing in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            var other = Path.TrimEndingDirectorySeparator(existing.Path.Path);
            if (string.IsNullOrEmpty(other)) continue;
            if (existing.Id == id && string.Equals(root, other, StringComparison.OrdinalIgnoreCase)) { sameRoot = true; continue; }
            if (string.Equals(root, other, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                other.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The CloudBay folder overlaps another registered cloud provider. Choose a separate folder.");
            if (existing.Id == id) throw new IOException("This account already has a CloudBay folder. Disconnect it explicitly before changing folders.");
        }
        // Cloud Files can register roots which the Shell filters out (e.g. hidden AppData roots).
        // Consult CfAPI as well so an unlisted provider root cannot be nested accidentally.
        if (!sameRoot)
        {
            for (var existingPath = new DirectoryInfo(root); existingPath is not null; existingPath = existingPath.Parent)
            {
                if (!existingPath.Exists) continue;
                if (CfGetSyncRootInfoByPath(existingPath.FullName, 0, out _, 8, out _) >= 0)
                    throw new IOException("The chosen folder is already inside a Windows cloud sync root. Choose a separate folder.");
                break;
            }
        }
        ValidateAncestorLinks(root);
        return sameRoot;
    }

    private static void RejectOrphanedPlaceholders(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var state = State(path);
                    if (state != uint.MaxValue && (state & (Placeholder | 2)) != 0)
                        throw new IOException("This folder contains existing cloud placeholders without this account's registration. Restore their original provider before using CloudBay.");
                    continue; // Never traverse junctions or links during setup.
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    private static string GetRegistrationId(string account)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("The current Windows user could not be identified.");
        return "CloudBay!" + sid + "!" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..24];
    }

    private static void ValidateAncestorLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null) throw new IOException("CloudBay cannot follow a directory junction or symbolic link for its sync root.");
            if (!current.Exists) continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A registered Cloud Files root is itself a reparse point, but is not a junction.
                var state = State(current.FullName);
                if (state == uint.MaxValue || (state & 2) == 0) throw new IOException("CloudBay cannot follow a directory junction or symbolic link for its sync root.");
            }
        }
    }

    private void ValidateParentLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current is not null; current = current.Parent)
        {
            if (string.Equals(current.FullName, _root, StringComparison.OrdinalIgnoreCase)) break;
            if (current.LinkTarget is not null) throw new IOException("CloudBay does not follow directory junctions or symbolic links.");
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var state = State(current.FullName);
                if (state == uint.MaxValue || (state & Placeholder) == 0) throw new IOException("CloudBay does not follow directory junctions or symbolic links.");
            }
        }
        if (new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && !IsPlaceholder(path)))
            throw new IOException("CloudBay does not follow file symbolic links.");
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        if (!Directory.Exists(path)) { yield return path; yield break; }
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var state = State(entry);
                    if (state == uint.MaxValue || (state & Placeholder) == 0 || new DirectoryInfo(entry).LinkTarget is not null || new FileInfo(entry).LinkTarget is not null) continue;
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var path in Directory.EnumerateDirectories(pending.Pop()))
            {
                if (new DirectoryInfo(path).LinkTarget is not null) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    var state = State(path);
                    if (state == uint.MaxValue || (state & (Placeholder | 2)) == 0) continue;
                }
                yield return path;
                pending.Push(path);
            }
        }
    }

    private static IEnumerable<string> EnumerateResidencyPaths(string path)
    {
        yield return path;
        if (!Directory.Exists(path)) yield break;
        foreach (var directory in EnumerateDirectories(path)) yield return directory;
        foreach (var file in EnumerateFiles(path)) yield return file;
    }

    private static void VerifyNativeLayouts()
    {
        if (Marshal.SizeOf<CallbackInfo>() != 152 || Marshal.SizeOf<OperationInfo>() != 48 ||
            Marshal.SizeOf<Metadata>() != 48 || Marshal.SizeOf<CreateInfo>() != 88 ||
            Marshal.SizeOf<TransferParameters>() != 40 || Marshal.SizeOf<RestartParameters>() != 40 ||
            Marshal.SizeOf<RetrieveParameters>() != 48 || Marshal.SizeOf<AckParameters>() != 32 ||
            Marshal.OffsetOf<RetrieveParameters>(nameof(RetrieveParameters.ReturnedLength)).ToInt32() != 40 ||
            Marshal.OffsetOf<StandardInfo>(nameof(StandardInfo.FileIdentityLength)).ToInt32() != 56)
            throw new PlatformNotSupportedException("Cloud Files native structure layouts do not match the Windows x64 ABI.");
    }
}

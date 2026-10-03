using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<long, HydrationWork> _requests = new();
    private readonly Callback _fetchCallback;
    private readonly Callback _cancelCallback;
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

    public WindowsPlaceholderService()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("CloudBay Cloud Files requires a 64-bit Windows process.");
        VerifyNativeLayouts();
        _fetchCallback = OnFetchData;
        _cancelCallback = OnCancelFetchData;
        _callbacks =
        [
            new() { Type = 0, Function = Marshal.GetFunctionPointerForDelegate(_fetchCallback) },
            new() { Type = 2, Function = Marshal.GetFunctionPointerForDelegate(_cancelCallback) },
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
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
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
                Check(CfConnectSyncRoot(root, _callbacks, IntPtr.Zero, 4, out _connection));
                _connected = true;
            }
            catch (Exception failure)
            {
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
            cancellationToken.ThrowIfCancellationRequested();
            var identity = Serialize(file);
            using var handle = Open(path, exclusive: true, writable: true);
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
            }
            finally { CfReleaseProtectedHandle(handle); }
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
            var state = State(fullPath);
            if (state == uint.MaxValue) return false;
            return (state & Placeholder) == 0 || (state & (Partial | PartiallyOnDisk)) == 0;
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
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Directory.Exists(path);
            using (var handle = Open(path, writable: true))
                Check(CfSetPinState(handle, mode == PinMode.AlwaysAvailable ? 1u : mode == PinMode.OnlineOnly ? 2u : 0u,
                    directory ? 5u : 0u, IntPtr.Zero));
            if (mode == PinMode.OnlineOnly) { FreeSpaceCore(path, cancellationToken); return; }
            foreach (var item in EnumerateFiles(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlaceholder(item) || IsHydrated(item)) continue;
                using var handle = Open(item);
                Check(CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
            }
        }, cancellationToken);
    }

    public Task HydrateAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath, allowRoot: true);
        return Task.Run(() =>
        {
            foreach (var item in EnumerateFiles(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlaceholder(item) || IsHydrated(item)) continue;
                using var handle = Open(item);
                Check(CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero));
            }
        }, cancellationToken);
    }

    public Task FreeSpaceAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var path = ValidateFilePath(fullPath, allowRoot: true);
        return Task.Run(() => FreeSpaceCore(path, cancellationToken), cancellationToken);
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
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_connected) return;
            _connected = false;
            _lifetime.Cancel();
            // Disconnect releases waiting user I/O. Registration and user data remain for the next run.
            Check(CfDisconnectSyncRoot(_connection));
            _connection = 0;
            await Task.WhenAll(_requests.Values.Select(w => w.Completion.Task)).ConfigureAwait(false);
            _hydrate = null;
        }
        finally { _connectionGate.Release(); }
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
    }

    private void OnFetchData(in CallbackInfo callbackInfo, in CallbackParameters parameters)
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
        var length = fileSize - offset;
        try
        {
            if (!_connected || _hydrate is null) throw new OperationCanceledException();
            if (callbackInfo.FileIdentityLength is 0 or > 4096 || fileSize < 0 || parameters.RequiredOffset < 0 || (requestedLength <= 0 && requestedLength != -1))
                throw new IOException("Windows supplied an invalid cloud hydration request.");
            var bytes = new byte[callbackInfo.FileIdentityLength];
            Marshal.Copy(callbackInfo.FileIdentity, bytes, 0, bytes.Length);
            var file = JsonSerializer.Deserialize<CloudObject>(bytes) ?? throw new IOException("Cloud object identity is missing.");
            if (file.Size != fileSize || offset >= fileSize) throw new IOException("Cloud object identity does not match the placeholder.");
            // FULL policy retrieves the whole remainder required by the platform. Range edges are
            // rounded to 4 KB; only the final operation may end with an unaligned EOF length.
            var end = requestedLength == -1 ? fileSize : Math.Min(fileSize, checked(parameters.RequiredOffset + requestedLength));
            if (end < fileSize) end = Math.Min(fileSize, checked((end + 4095) & ~4095L));
            length = end - offset;
            var id = Interlocked.Increment(ref _nextRequest);
            var work = new HydrationWork(operation, offset, length, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _requests[id] = work;
            var hydrate = _hydrate;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var destination = new HydrationStream(operation, offset, length, work.Cancellation.Token);
                    await hydrate(file, offset, length, destination, work.Cancellation.Token).ConfigureAwait(false);
                    destination.Complete();
                }
                catch (Exception ex)
                {
                    CompleteFailure(operation, offset, length,
                        ex is OperationCanceledException ? CloudCancelled : ex is HttpRequestException ? CloudNetworkUnavailable : CloudUnsuccessful);
                }
                finally
                {
                    _requests.TryRemove(id, out _);
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
        var parameters = new TransferParameters { ParamSize = 40, CompletionStatus = status, Offset = offset, Length = length };
        _ = CfExecute(operation, parameters); // A cancelled/disconnected request may already be completed by Windows.
    }

    private sealed class HydrationWork(OperationInfo operation, long offset, long length, CancellationTokenSource cancellation)
    {
        internal OperationInfo Operation { get; } = operation;
        internal long Offset { get; } = offset;
        internal long Length { get; } = length;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (!current.Exists) continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A registered Cloud Files root is itself a reparse point, but is not a junction.
                var state = State(current.FullName);
                if ((state & 2) == 0) throw new IOException("CloudBay cannot follow a directory junction or symbolic link for its sync root.");
            }
        }
    }

    private void ValidateParentLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current is not null; current = current.Parent)
        {
            if (string.Equals(current.FullName, _root, StringComparison.OrdinalIgnoreCase)) break;
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((State(current.FullName) & Placeholder) == 0) throw new IOException("CloudBay does not follow directory junctions or symbolic links.");
            }
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && !IsPlaceholder(path))
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
                if ((attributes & FileAttributes.ReparsePoint) != 0 && (State(entry) & Placeholder) == 0) continue;
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
                yield return path;
                pending.Push(path);
            }
        }
    }

    private static void VerifyNativeLayouts()
    {
        if (Marshal.SizeOf<CallbackInfo>() != 152 || Marshal.SizeOf<OperationInfo>() != 48 ||
            Marshal.SizeOf<Metadata>() != 48 || Marshal.SizeOf<CreateInfo>() != 88 ||
            Marshal.SizeOf<TransferParameters>() != 40 || Marshal.OffsetOf<StandardInfo>(nameof(StandardInfo.FileIdentityLength)).ToInt32() != 56)
            throw new PlatformNotSupportedException("Cloud Files native structure layouts do not match the Windows x64 ABI.");
    }
}

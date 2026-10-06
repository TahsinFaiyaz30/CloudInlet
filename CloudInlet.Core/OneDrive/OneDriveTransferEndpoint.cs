using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Core.OneDrive;

/// <summary>Replayable Graph source/destination adapter. Only metadata, sessions and receipts are durable.</summary>
public sealed class OneDriveTransferEndpoint : ITransferEndpoint
{
    public const int FragmentBytes = 5 * 1024 * 1024; // 16 * 320 KiB; below Graph's 60 MiB fragment maximum.
    public const int SmallFileBytes = 1024 * 1024;
    private readonly OneDriveClient _client;
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.OrdinalIgnoreCase);
    public TransferLocation Location { get; }

    public OneDriveTransferEndpoint(OneDriveClient client, TransferLocation location)
    {
        if (!location.Provider.Equals("onedrive", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(location.ContainerId) || string.IsNullOrEmpty(location.FolderId))
            throw new ArgumentException("A OneDrive endpoint requires an actual Graph drive and folder identity.", nameof(location));
        _client = client;
        Location = location;
        _folders[""] = location.FolderId;
    }

    public async Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await _client.ListChildrenPageAsync(Location.ContainerId, Location.FolderId, cursor, cancellationToken);
        return new(page.Items.Where(item => item.IsFolder).Select(item => new TransferFolder(item.Id, item.Name,
            Join(Location.Path, item.Name))).ToArray(), page.NextLink);
    }

    private sealed record DiscoveryFolder(string Id, string Path);
    private sealed record DiscoveryCursor(string DriveId, string RootId, DiscoveryFolder Current, string? NextLink,
        List<DiscoveryFolder> Pending, int ReplayGeneration = 0);

    public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
        DiscoverAsync(cursor, Array.Empty<string>(), cancellationToken);

    public async Task<TransferDiscoveryPage> DiscoverAsync(string? cursor, IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
    {
        var state = cursor is null ? new(Location.ContainerId, Location.FolderId, new(Location.FolderId, ""), null, [])
            : JsonSerializer.Deserialize<DiscoveryCursor>(cursor) ?? throw new InvalidDataException("The saved OneDrive discovery cursor is invalid.");
        if (state.DriveId != Location.ContainerId || state.RootId != Location.FolderId)
            throw new InvalidDataException("The saved OneDrive discovery cursor belongs to another source folder.");
        OneDrivePage page;
        try
        {
            page = await _client.ListChildrenPageAsync(Location.ContainerId, state.Current.Id, state.NextLink, cancellationToken);
            state = state with { ReplayGeneration = 0 };
        }
        catch (OneDriveApiException error) when (state.NextLink is not null && error.StatusCode is 404 or 410)
        {
            // Only this incomplete folder is revisited if Graph expires a pagination token.
            // The engine's durable identity queue deduplicates previously discovered entries.
            page = await _client.ListChildrenPageAsync(Location.ContainerId, state.Current.Id, null, cancellationToken);
            if (state.ReplayGeneration >= 3) throw new IOException("Microsoft repeatedly expired this folder's discovery page. Saved discovery and completed work are retained; resume after reviewing the source.");
            state = state with { ReplayGeneration = checked(state.ReplayGeneration + 1) };
        }
        var entries = new List<TransferEntry>(page.Items.Count);
        foreach (var item in page.Items)
        {
            ValidateName(item.Name);
            var path = Join(state.Current.Path, item.Name);
            if (CloudInlet.Core.Sync.PathRules.IsExcluded(path, exclusions)) continue;
            entries.Add(new(item.Id, path, item.ETag, item.IsFolder ? 0 : item.Size, item.ModifiedUtc, item.Sha1, item.IsFolder));
            if (item.IsFolder && !state.Pending.Any(folder => folder.Id == item.Id)) state.Pending.Add(new(item.Id, path));
        }
        if (page.NextLink is not null) state = state with { NextLink = page.NextLink };
        else if (state.Pending.Count > 0)
        {
            var next = state.Pending[0];
            state.Pending.RemoveAt(0);
            state = state with { Current = next, NextLink = null };
        }
        else return new(entries, null);
        return new(entries, JsonSerializer.Serialize(state));
    }

    public ITransferSourceFile OpenSource(TransferEntry entry) => new OneDriveSource(_client, Location.ContainerId, entry);

    public async Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default)
    {
        if (checkpoint is null) return null;
        ValidateCheckpoint(request, source, checkpoint);
        var path = Data(checkpoint, "target")!;
        OneDriveItem? existing;
        if (Data(checkpoint, "itemId") is { } id)
        {
            try { existing = await _client.GetItemAsync(Location.ContainerId, id, cancellationToken); }
            catch (OneDriveApiException error) when (error.StatusCode == 404)
            { throw new TransferConflictException("The acknowledged OneDrive destination was removed. Its saved receipt and source are retained for review."); }
            // A completed checkpoint identifies one exact acknowledged version. Recreating
            // the receipt from a later version would erase this evidence on recovery.
            if (existing.ETag != Data(checkpoint, "itemVersion") || existing.IsFolder != source.Entry.IsFolder ||
                !source.Entry.IsFolder && existing.Size != source.Entry.Size)
                throw new TransferConflictException("The acknowledged OneDrive destination changed after completion. Its saved receipt and source are retained for review.");
            var atPath = await _client.GetByPathAsync(Location.ContainerId, Location.FolderId, path, cancellationToken);
            if (atPath?.Id != id)
                throw new TransferConflictException("The acknowledged OneDrive destination no longer occupies the selected path. Its source is retained for review.");
        }
        else existing = await _client.GetByPathAsync(Location.ContainerId, Location.FolderId, path, cancellationToken);
        if (existing is null) return null;
        if (Data(checkpoint, "reservationState") == "creating")
            throw new TransferConflictException("The empty OneDrive reservation may have completed without an acknowledgment. Its saved intent and source are retained; review this destination before retrying.");
        if (!source.Entry.IsFolder && Data(checkpoint, "beforeVersion") == existing.ETag) return null;
        var expectedHash = Data(checkpoint, "uploadedSha1") ?? source.Entry.Sha1;
        if (!source.Entry.IsFolder && !ValidSha1(expectedHash)) expectedHash = await HashSourceAsync(source, cancellationToken);
        var receipt = Receipt(existing, path, request.OperationId, expectedHash);
        // Content equality is required when the final response was lost; a mere matching name/size is insufficient.
        await VerifyAsync(receipt, source, cancellationToken);
        return receipt;
    }

    public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var entry = source.Entry;
        var path = request.RelativePath.Replace('\\', '/');
        if (entry.IsFolder) path = path.TrimEnd('/');
        ValidateRelativePath(path);
        if (checkpoint is not null && !entry.IsFolder)
        {
            ValidateCheckpoint(request, source, checkpoint);
            path = Data(checkpoint, "target")!;
        }
        var slash = path.LastIndexOf('/');
        var parentPath = slash < 0 ? "" : path[..slash];
        var name = slash < 0 ? path : path[(slash + 1)..];
        string parentId;
        OneDriveItem? before = null;
        if (!entry.IsFolder && _folders.TryGetValue(parentPath, out var knownParent))
        {
            // Independent reads can overlap for an already known parent. No
            // destination mutation occurs until source validation succeeds.
            var validation = source.ValidateAsync(cancellationToken);
            var destination = _client.GetByPathAsync(Location.ContainerId, knownParent, name, cancellationToken);
            await Task.WhenAll(validation, destination).ConfigureAwait(false);
            parentId = knownParent;
            before = await destination.ConfigureAwait(false);
        }
        else
        {
            await source.ValidateAsync(cancellationToken).ConfigureAwait(false);
            // Creating a missing parent still requires validated source state.
            parentId = await EnsureParentAsync(parentPath, cancellationToken).ConfigureAwait(false);
            if (!entry.IsFolder)
                before = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken).ConfigureAwait(false);
        }
        if (entry.IsFolder)
        {
            var folder = await _client.EnsureFolderAsync(Location.ContainerId, parentId, name, cancellationToken);
            return Receipt(folder, request.RelativePath, request.OperationId);
        }
        if (checkpoint is null && before is not null)
        {
            if (request.ConflictPolicy == TransferConflictPolicy.Skip) throw new TransferSkippedException("The destination OneDrive file already exists.");
            if (request.ConflictPolicy == TransferConflictPolicy.Fail) throw new TransferConflictException("The destination OneDrive file already exists.");
            if (request.ConflictPolicy == TransferConflictPolicy.Rename)
            {
                path = TransferConflictNames.RenamePath(path, request.OperationId);
                name = path[(path.LastIndexOf('/') + 1)..];
                before = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken);
                if (before is not null) throw new TransferConflictException("The deterministic renamed destination already exists and cannot be attributed to this transfer.");
            }
        }
        if (before?.IsFolder == true) throw new TransferConflictException("A OneDrive destination folder has the selected file name.");
        OneDriveUploadSession? session = null;
        if (checkpoint is { SessionId.Length: > 0 }) session = await _client.GetUploadSessionAsync(checkpoint.SessionId, cancellationToken);
        if (before is null && checkpoint is { SessionId.Length: > 0 } && session is null && Data(checkpoint, "reservationState") == "owned")
        {
            // Provider cleanup can remove our zero-byte item when its session
            // expires. Only absence plus unusable session evidence releases the
            // prior ownership; a new reservation still uses atomic fail semantics.
            var expiredData = new Dictionary<string, string>(checkpoint.Data!);
            expiredData.Remove("beforeId"); expiredData.Remove("beforeVersion"); expiredData.Remove("reservationState"); expiredData.Remove("expires");
            checkpoint = new("onedrive", "", 0, expiredData);
        }
        if (checkpoint is not null && before is not null && (before.ETag != Data(checkpoint, "beforeVersion") ||
            Data(checkpoint, "beforeId") is { } reviewedId && before.Id != reviewedId))
            throw new TransferConflictException("The destination changed after this upload began. Review the conflict before retrying.");
        if (checkpoint is not null && before is null && Data(checkpoint, "beforeVersion") is not null)
            throw new TransferConflictException("The reviewed OneDrive replacement was removed after this upload began. Review the conflict before retrying.");

        var data = checkpoint?.Data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(checkpoint.Data);
        data["target"] = path;
        data["operation"] = request.OperationId;
        data["drive"] = Location.ContainerId;
        data["folder"] = Location.FolderId;
        data["sourceId"] = entry.Id;
        data["sourceVersion"] = entry.Version;
        data["sourceSize"] = entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (before is not null && checkpoint is null)
        { data["beforeVersion"] = before.ETag; data["beforeId"] = before.Id; }
        if (entry.Size <= SmallFileBytes && checkpoint is not { SessionId.Length: > 0 })
        {
            // Tiny files use one payload request instead of creating an upload session per file.
            // Persist the exact destination intent before the request so a lost final response is recoverable.
            var smallBuffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, entry.Size));
            try
            {
                await using (var smallSource = await source.OpenReadAsync(0, entry.Size, cancellationToken))
                    await smallSource.ReadExactlyAsync(smallBuffer.AsMemory(0, (int)entry.Size), cancellationToken);
                var smallHash = Convert.ToHexString(SHA1.HashData(smallBuffer.AsSpan(0, (int)entry.Size))).ToLowerInvariant();
                CheckSourceHash(entry, smallHash);
                data["uploadedSha1"] = smallHash;
                await saveCheckpoint(new("onedrive", "", 0, new Dictionary<string, string>(data)), cancellationToken);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var uploaded = await _client.PutSmallAsync(Location.ContainerId, parentId, name,
                            smallBuffer.AsMemory(0, (int)entry.Size), request.ConflictPolicy == TransferConflictPolicy.Replace ? "replace" : "fail",
                            before?.ETag, cancellationToken, progress);
                        uploaded = await CanonicalCompletionAsync(uploaded, cancellationToken);
                        await SaveCompletedAsync(uploaded, path, request.OperationId, data, saveCheckpoint, cancellationToken);
                        progress?.Report(new(entry.Size, entry.Size));
                        return Receipt(uploaded, path, request.OperationId, smallHash);
                    }
                    catch (Exception error) when (error is HttpRequestException || error is OneDriveApiException { StatusCode: >= 500 or 429 })
                    {
                        var reconciled = await ReconcileAsync(request, source, new("onedrive", "", 0, data), cancellationToken);
                        if (reconciled is not null) return reconciled;
                        if (attempt >= 4) throw;
                        await DelayRetryAsync(error, attempt, cancellationToken);
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(smallBuffer, clearArray: true); }
        }
        for (var restarts = 0; ; restarts++)
        {
            try { return await TransferLargeAsync(); }
            catch (UploadSessionExpiredException error)
            {
                var interrupted = new TransferCheckpoint("onedrive", session!.UploadUrl, session.NextOffset, new Dictionary<string, string>(data));
                var completed = await ReconcileAsync(request, source, interrupted, cancellationToken);
                if (completed is not null) return completed;
                var current = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken);
                if (current is null && data.GetValueOrDefault("reservationState") == "owned")
                {
                    data.Remove("beforeId"); data.Remove("beforeVersion"); data.Remove("reservationState");
                    before = null;
                }
                else if (current?.ETag != data.GetValueOrDefault("beforeVersion") ||
                    data.GetValueOrDefault("beforeId") is { } expiredReviewedId && current?.Id != expiredReviewedId ||
                    data.GetValueOrDefault("reservationState") == "owned" && current is not { Size: 0, IsFolder: false })
                    throw new TransferConflictException("The OneDrive destination changed after its upload session expired. Its saved progress and source are retained for review.");
                // The expired server ranges cannot be resumed. Keep ownership,
                // source identity and any complete digest, clearing only this
                // unfinished file's unusable session and acknowledged bytes.
                data.Remove("expires");
                checkpoint = new("onedrive", "", 0, new Dictionary<string, string>(data));
                await saveCheckpoint(checkpoint, CancellationToken.None);
                session = null;
                if (restarts >= 2)
                    throw new IOException("Microsoft repeatedly expired this file's upload session. Its source, destination identity and completed job files are retained; resume will restart only this unfinished file.", error);
                await source.ValidateAsync(cancellationToken);
            }
        }

        async Task<TransferReceipt> TransferLargeAsync()
        {
            if (session is null)
            {
                if (before is null)
                {
                    // A personal OneDrive session exposes a zero-byte destination
                    // before its payload is complete. Create our own empty target
                    // and persist its acknowledged identity/version so this visible
                    // item can never be confused with a concurrent user's file.
                    data["reservationState"] = "creating";
                    await saveCheckpoint(new("onedrive", "", 0, new Dictionary<string, string>(data)), cancellationToken);
                    var reserved = await _client.PutSmallAsync(Location.ContainerId, parentId, name,
                        ReadOnlyMemory<byte>.Empty, "fail", null, cancellationToken);
                    if (reserved.IsFolder || reserved.Size != 0 || string.IsNullOrEmpty(reserved.Id) || string.IsNullOrEmpty(reserved.ETag))
                        throw new InvalidDataException("Microsoft did not acknowledge the empty destination's exact identity and version. Its source and saved reservation intent are retained for review.");
                    data["beforeId"] = reserved.Id;
                    data["beforeVersion"] = reserved.ETag;
                    data["reservationState"] = "owned";
                    // A cancellation after Microsoft's acknowledgment must not
                    // discard the ownership evidence needed for a safe restart.
                    await saveCheckpoint(new("onedrive", "", 0, new Dictionary<string, string>(data)), CancellationToken.None);
                    before = reserved;
                }
                session = await _client.CreateUploadSessionAsync(Location.ContainerId, parentId, name,
                    // Replace an explicitly reviewed existing version only. If the target was
                    // absent, only our acknowledged empty reservation may be replaced.
                    before is not null && (request.ConflictPolicy == TransferConflictPolicy.Replace || data.GetValueOrDefault("reservationState") == "owned")
                        ? "replace" : "fail", before?.ETag, cancellationToken);
                data["expires"] = session.ExpiresUtc.ToString("O");
                await saveCheckpoint(new("onedrive", session.UploadUrl, 0, new Dictionary<string, string>(data)), CancellationToken.None);
            }
            if (session.ExpiresUtc <= DateTimeOffset.UtcNow) throw new UploadSessionExpiredException();
            if (session.NextOffset < 0 || session.NextOffset >= entry.Size || session.NextOffset % 327680 != 0)
                throw new InvalidDataException("OneDrive returned an unsafe upload resume offset. Saved progress is retained for review.");
            var offset = session.NextOffset;
            var uploadedHash = Data(checkpoint ?? new("onedrive", "", 0), "uploadedSha1") ?? entry.Sha1;
            if (offset > 0 && !ValidSha1(uploadedHash)) uploadedHash = await HashSourceAsync(source, cancellationToken);
            if (ValidSha1(uploadedHash)) data["uploadedSha1"] = uploadedHash!;
            progress?.Report(new(offset, entry.Size) { IsBaseline = true });
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(FragmentBytes, entry.Size));
            using var contentHash = offset == 0 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
            try
            {
                // One continuous source range keeps the download connection open across fragments.
                // The engine wraps it in bounded read-ahead so source reads overlap Graph uploads.
                await using var input = await source.OpenReadAsync(offset, entry.Size - offset, cancellationToken);
                while (offset < entry.Size)
                {
                    if (session.ExpiresUtc <= DateTimeOffset.UtcNow) throw new UploadSessionExpiredException();
                    var count = (int)Math.Min(FragmentBytes, entry.Size - offset);
                    await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                    contentHash?.AppendData(buffer, 0, count);
                    if (offset + count == entry.Size)
                    {
                        // The final fragment now owns all remaining bytes in RAM.
                        // Release source download admission before destination
                        // checks, durable intent and the final upload response.
                        await input.DisposeAsync().ConfigureAwait(false);
                        if (contentHash is not null) uploadedHash = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
                        if (!ValidSha1(uploadedHash)) throw new InvalidDataException("The OneDrive upload has no complete content checksum.");
                        CheckSourceHash(entry, uploadedHash!);
                        data["uploadedSha1"] = uploadedHash!;
                        var currentTarget = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken);
                        if (currentTarget?.ETag != data.GetValueOrDefault("beforeVersion") ||
                            data.GetValueOrDefault("beforeId") is { } beforeId && currentTarget?.Id != beforeId ||
                            data.GetValueOrDefault("reservationState") == "owned" && currentTarget is not { Size: 0, IsFolder: false })
                            throw new TransferConflictException("The OneDrive destination changed during this upload. Its unfinished session and source are retained for review.");
                        // The digest is durable before the final commit, including a lost final response.
                        await saveCheckpoint(new("onedrive", session.UploadUrl, offset, new Dictionary<string, string>(data)), cancellationToken);
                    }
                    (OneDriveItem? Item, OneDriveUploadSession? Session) response;
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            response = await _client.UploadFragmentAsync(session.UploadUrl, buffer.AsMemory(0, count), offset, entry.Size, cancellationToken, progress);
                            break;
                        }
                        catch (Exception error) when (error is HttpRequestException || error is OneDriveApiException { StatusCode: 404 or 410 or 416 or >= 500 or 429 })
                        {
                            // A lost response must be reconciled against server acknowledgments before any fragment is replayed.
                            var status = error is OneDriveApiException { StatusCode: 404 or 410 } ? null :
                                await _client.GetUploadSessionAsync(session.UploadUrl, cancellationToken);
                            if (status is null)
                                throw new UploadSessionExpiredException(error);
                            if (status.NextOffset == offset + count)
                            { response = (null, status); break; }
                            if (status.NextOffset != offset) throw new InvalidDataException("OneDrive returned an unexpected acknowledged byte offset.");
                            if (attempt >= 4) throw;
                            await DelayRetryAsync(error, attempt, cancellationToken);
                        }
                    }
                    if (response.Item is { } completed)
                    {
                        if (offset + count != entry.Size) throw new InvalidDataException("OneDrive completed an upload before the final byte range.");
                        completed = await CanonicalCompletionAsync(completed, cancellationToken);
                        await SaveCompletedAsync(completed, path, request.OperationId, data, saveCheckpoint, cancellationToken);
                        progress?.Report(new(entry.Size, entry.Size));
                        return Receipt(completed, path, request.OperationId, uploadedHash);
                    }
                    session = response.Session ?? throw new InvalidDataException("OneDrive did not acknowledge the upload fragment.");
                    if (session.NextOffset != offset + count) throw new InvalidDataException("OneDrive acknowledged an unexpected byte range.");
                    offset = session.NextOffset;
                    data["expires"] = session.ExpiresUtc.ToString("O");
                    await saveCheckpoint(new("onedrive", session.UploadUrl, offset, new Dictionary<string, string>(data)), CancellationToken.None);
                    progress?.Report(new(offset, entry.Size));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            throw new IOException("OneDrive did not return a completed destination file.");
        }
    }

    public async Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
    {
        var current = await _client.GetItemAsync(Location.ContainerId, receipt.Id, cancellationToken);
        if (source.Entry.IsFolder)
        {
            if (!current.IsFolder) throw new InvalidDataException("The OneDrive destination folder is missing.");
            return;
        }
        if (current.IsFolder || current.Size != source.Entry.Size || (!string.IsNullOrEmpty(receipt.Version) && current.ETag != receipt.Version))
            throw new InvalidDataException("The OneDrive destination version or length changed before verification.");
        await source.ValidateAsync(cancellationToken);
        var expectedHash = ValidSha1(receipt.Sha1) ? receipt.Sha1 : source.Entry.Sha1;
        if (ValidSha1(expectedHash) && ValidSha1(current.Sha1))
        {
            if (!expectedHash!.Equals(current.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The OneDrive destination failed its SHA-1 checksum verification.");
        }
        else if (ValidSha1(expectedHash))
        {
            await using var destination = await _client.OpenValidatedReadAsync(Location.ContainerId, current, current, 0, current.Size, cancellationToken);
            var actualHash = Convert.ToHexString(await SHA1.HashDataAsync(destination, cancellationToken));
            if (!expectedHash!.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The OneDrive destination failed independent streaming SHA-1 content verification.");
        }
        else
        {
            // Graph commonly exposes QuickXorHash rather than a cryptographic hash on business drives.
            // In that case compare independent streaming SHA-256 reads through bounded hash buffers.
            // Each hash must start consuming and dispose its own range before
            // waiting for another download. One shared download slot then works
            // serially, while larger limits still allow the independent reads
            // to overlap without retaining one unconsumed response behind another.
            async Task<byte[]> HashSourceAsync()
            {
                await using var original = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
                return await SHA256.HashDataAsync(original, cancellationToken);
            }
            async Task<byte[]> HashDestinationAsync()
            {
                await using var destination = await _client.OpenValidatedReadAsync(Location.ContainerId, current, current, 0, current.Size, cancellationToken);
                return await SHA256.HashDataAsync(destination, cancellationToken);
            }
            var sourceHash = HashSourceAsync();
            var targetHash = HashDestinationAsync();
            await Task.WhenAll(sourceHash, targetHash);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash.Result, targetHash.Result))
                throw new InvalidDataException("The OneDrive destination failed streaming SHA-256 content verification.");
        }
        await source.ValidateAsync(cancellationToken);
        await _client.ValidateAsync(Location.ContainerId, current, cancellationToken);
    }

    public async Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.IsFolder) return; // Never recursively delete a source tree after moving only selected files.
        await _client.DeleteUnchangedAsync(Location.ContainerId, ToItem(entry), cancellationToken);
    }

    public async Task<bool> IsSourceDeletedAsync(TransferEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await _client.GetItemAsync(Location.ContainerId, entry.Id, cancellationToken);
            if (entry.IsFolder) return false;
            OneDriveClient.EnsureUnchanged(ToItem(entry), current);
            return false;
        }
        catch (OneDriveApiException error) when (error.StatusCode == 404) { return true; }
    }

    private async Task<string> EnsureParentAsync(string path, CancellationToken cancellationToken)
    {
        if (_folders.TryGetValue(path, out var cached)) return cached;
        var current = "";
        var parent = Location.FolderId;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Join(current, name);
            if (!_folders.TryGetValue(current, out var folder))
            {
                folder = (await _client.EnsureFolderAsync(Location.ContainerId, parent, name, cancellationToken)).Id;
                _folders[current] = folder;
            }
            parent = folder;
        }
        return parent;
    }

    private Task<OneDriveItem> CanonicalCompletionAsync(OneDriveItem item, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(item.ETag) ? _client.GetItemAsync(Location.ContainerId, item.Id, cancellationToken) : Task.FromResult(item);

    private static async Task SaveCompletedAsync(OneDriveItem item, string path, string operation,
        Dictionary<string, string> data, Func<TransferCheckpoint, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        data["itemId"] = item.Id;
        data["itemVersion"] = item.ETag;
        await save(new("onedrive", "", item.Size, new Dictionary<string, string>(data)), CancellationToken.None);
    }
    private void ValidateCheckpoint(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint checkpoint)
    {
        if (checkpoint.Provider != "onedrive" || Data(checkpoint, "drive") != Location.ContainerId || Data(checkpoint, "folder") != Location.FolderId ||
            Data(checkpoint, "operation") != request.OperationId || Data(checkpoint, "sourceId") != source.Entry.Id ||
            Data(checkpoint, "sourceVersion") != source.Entry.Version || Data(checkpoint, "sourceSize") != source.Entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            string.IsNullOrEmpty(Data(checkpoint, "target")) || !IsCheckpointTarget(request, Data(checkpoint, "target")!))
            throw new InvalidDataException("The saved OneDrive upload session does not match its reviewed source and destination.");
        if (checkpoint.AcknowledgedBytes < 0 || checkpoint.AcknowledgedBytes > source.Entry.Size ||
            (Data(checkpoint, "itemId") is not null && string.IsNullOrEmpty(Data(checkpoint, "itemVersion"))) ||
            Data(checkpoint, "reservationState") is { } reservation &&
                (source.Entry.IsFolder || source.Entry.Size <= SmallFileBytes || reservation is not ("creating" or "owned") ||
                    reservation == "owned" && (string.IsNullOrEmpty(Data(checkpoint, "beforeId")) || string.IsNullOrEmpty(Data(checkpoint, "beforeVersion"))) ||
                    reservation == "creating" && (checkpoint.AcknowledgedBytes != 0 || checkpoint.SessionId.Length != 0 ||
                        Data(checkpoint, "beforeId") is not null || Data(checkpoint, "beforeVersion") is not null)))
            throw new InvalidDataException("The saved OneDrive acknowledgment is incomplete or outside its source length.");
        ValidateRelativePath(Data(checkpoint, "target")!);
    }
    private static bool IsCheckpointTarget(TransferUploadRequest request, string target)
    {
        var original = request.RelativePath.Replace('\\', '/');
        if (target == original) return true;
        if (request.ConflictPolicy != TransferConflictPolicy.Rename) return false;
        if (target == TransferConflictNames.RenamePath(original, request.OperationId)) return true;
        // Admit only the exact deterministic name generated by the prior Graph
        // adapter, retaining its receipts and unfinished checkpoints on upgrade.
        var slash = original.LastIndexOf('/');
        return target == (slash < 0 ? "" : original[..(slash + 1)]) + LegacyRename(original[(slash + 1)..], request.OperationId);
    }
    private static string? Data(TransferCheckpoint checkpoint, string name) => checkpoint.Data?.GetValueOrDefault(name);
    private static TransferReceipt Receipt(OneDriveItem item, string path, string operation, string? uploadedHash = null) =>
        new(item.Id, path, item.ETag, item.IsFolder ? 0 : item.Size, uploadedHash ?? item.Sha1, operation);
    private static OneDriveItem ToItem(TransferEntry entry) => new(entry.Id, Path.GetFileName(entry.RelativePath), entry.Size,
        entry.Version, null, entry.IsFolder, entry.ModifiedUtc, entry.Sha1);
    private static bool ValidSha1(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static void CheckSourceHash(TransferEntry entry, string actual)
    {
        if (ValidSha1(entry.Sha1) && !entry.Sha1!.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source stream differs from its saved checksum; the OneDrive copy was not acknowledged.");
    }
    private static async Task<string> HashSourceAsync(ITransferSourceFile source, CancellationToken cancellationToken)
    {
        await using var stream = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
        var digest = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        await source.ValidateAsync(cancellationToken);
        return digest;
    }
    private static Task DelayRetryAsync(Exception error, int attempt, CancellationToken cancellationToken) =>
        Task.Delay(error is OneDriveApiException { RetryAfter: { } delay } ? delay :
            TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * (1 << attempt)) + Random.Shared.Next(100, 400)), cancellationToken);
    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent.TrimEnd('/') + "/" + name;
    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
            throw new InvalidDataException("A OneDrive item has an unsafe name.");
    }
    private static void ValidateRelativePath(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\') || path.Split(['/', '\\']).Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("The OneDrive destination path must stay inside the selected folder.");
        foreach (var name in path.Split(['/', '\\'])) ValidateName(name);
    }
    private static string LegacyRename(string name, string operation)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation)))[..10].ToLowerInvariant();
        var extension = Path.GetExtension(name);
        return name[..(name.Length - extension.Length)] + " (CloudInlet " + suffix + ")" + extension;
    }

    private sealed class UploadSessionExpiredException(Exception? inner = null)
        : IOException("The OneDrive upload session expired.", inner);

    private sealed class OneDriveSource(OneDriveClient client, string driveId, TransferEntry entry) : ITransferSourceFile
    {
        private readonly object _gate = new();
        private (OneDriveItem Item, long ValidatedAt)? _prepared;
        public TransferEntry Entry { get; } = entry;
        public bool HasContentBoundVersion => true;
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            (OneDriveItem Item, long ValidatedAt)? prepared;
            lock (_gate) { prepared = _prepared; _prepared = null; }
            // Consume only the immediately preceding check. Every Validate call,
            // expired URL refresh, later read and Move deletion still fetches Graph.
            return prepared is { } recent && Stopwatch.GetElapsedTime(recent.ValidatedAt) <= TimeSpan.FromSeconds(2)
                ? client.OpenValidatedReadAsync(driveId, ToItem(Entry), recent.Item, offset, length, cancellationToken)
                : client.OpenReadAsync(driveId, ToItem(Entry), offset, length, cancellationToken);
        }
        public async Task ValidateAsync(CancellationToken cancellationToken = default)
        {
            var current = await client.GetItemAsync(driveId, Entry.Id, cancellationToken);
            if (!Entry.IsFolder)
            {
                OneDriveClient.EnsureUnchanged(ToItem(Entry), current);
                lock (_gate) _prepared = (current, Stopwatch.GetTimestamp());
                return;
            }
            if (!current.IsFolder) throw new TransferSourceChangedException("The OneDrive source folder no longer exists.");
        }
    }
}

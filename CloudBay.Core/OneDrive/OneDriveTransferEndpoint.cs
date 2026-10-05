using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core.Transfers;

namespace CloudBay.Core.OneDrive;

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
            if (CloudBay.Core.Sync.PathRules.IsExcluded(path, exclusions)) continue;
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
        await source.ValidateAsync(cancellationToken);
        var path = request.RelativePath.Replace('\\', '/');
        if (entry.IsFolder) path = path.TrimEnd('/');
        ValidateRelativePath(path);
        var slash = path.LastIndexOf('/');
        var parentPath = slash < 0 ? "" : path[..slash];
        var name = slash < 0 ? path : path[(slash + 1)..];
        var parentId = await EnsureParentAsync(parentPath, cancellationToken);
        if (entry.IsFolder)
        {
            var folder = await _client.EnsureFolderAsync(Location.ContainerId, parentId, name, cancellationToken);
            return Receipt(folder, request.RelativePath, request.OperationId);
        }
        if (checkpoint is not null)
        {
            ValidateCheckpoint(request, source, checkpoint);
            path = Data(checkpoint, "target")!;
            slash = path.LastIndexOf('/');
            name = slash < 0 ? path : path[(slash + 1)..];
            parentId = await EnsureParentAsync(slash < 0 ? "" : path[..slash], cancellationToken);
        }
        var before = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken);
        if (checkpoint is null && before is not null)
        {
            if (request.ConflictPolicy == TransferConflictPolicy.Skip) throw new TransferSkippedException("The destination OneDrive file already exists.");
            if (request.ConflictPolicy == TransferConflictPolicy.Fail) throw new TransferConflictException("The destination OneDrive file already exists.");
            if (request.ConflictPolicy == TransferConflictPolicy.Rename)
            {
                name = Rename(name, request.OperationId);
                path = Join(parentPath, name);
                before = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken);
                if (before is not null) throw new TransferConflictException("The deterministic renamed destination already exists and cannot be attributed to this transfer.");
            }
        }
        if (before?.IsFolder == true) throw new TransferConflictException("A OneDrive destination folder has the selected file name.");
        if (checkpoint is not null && before is not null && before.ETag != Data(checkpoint, "beforeVersion"))
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
        OneDriveUploadSession? session = null;
        if (checkpoint is { SessionId.Length: > 0 }) session = await _client.GetUploadSessionAsync(checkpoint.SessionId, cancellationToken);
        if (entry.Size <= SmallFileBytes && checkpoint is not { SessionId.Length: > 0 })
        {
            // Tiny files use one payload request instead of creating an upload session per file.
            // Persist the exact destination intent before the request so a lost final response is recoverable.
            var smallBuffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, entry.Size));
            try
            {
                await using var smallSource = await source.OpenReadAsync(0, entry.Size, cancellationToken);
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
        if (session is null)
        {
            session = await _client.CreateUploadSessionAsync(Location.ContainerId, parentId, name,
                // Replace an explicitly reviewed existing version only. If the target was
                // absent, a file appearing during this session is a new conflict.
                before is not null && request.ConflictPolicy == TransferConflictPolicy.Replace ? "replace" : "fail", before?.ETag, cancellationToken);
            data["expires"] = session.ExpiresUtc.ToString("O");
            await saveCheckpoint(new("onedrive", session.UploadUrl, 0, new Dictionary<string, string>(data)), cancellationToken);
        }
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
                var count = (int)Math.Min(FragmentBytes, entry.Size - offset);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                contentHash?.AppendData(buffer, 0, count);
                if (offset + count == entry.Size)
                {
                    if (contentHash is not null) uploadedHash = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
                    if (!ValidSha1(uploadedHash)) throw new InvalidDataException("The OneDrive upload has no complete content checksum.");
                    CheckSourceHash(entry, uploadedHash!);
                    data["uploadedSha1"] = uploadedHash!;
                    var currentTarget = await _client.GetByPathAsync(Location.ContainerId, parentId, name, cancellationToken);
                    if (currentTarget?.ETag != data.GetValueOrDefault("beforeVersion") ||
                        data.GetValueOrDefault("beforeId") is { } beforeId && currentTarget?.Id != beforeId)
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
                    catch (Exception error) when (error is HttpRequestException || error is OneDriveApiException { StatusCode: 416 or >= 500 or 429 })
                    {
                        // A lost response must be reconciled against server acknowledgments before any fragment is replayed.
                        var status = await _client.GetUploadSessionAsync(session.UploadUrl, cancellationToken);
                        if (status is null)
                        {
                            var final = await ReconcileAsync(request, source, new("onedrive", session.UploadUrl, offset, data), cancellationToken);
                            if (final is not null) return final;
                            throw new IOException("The OneDrive upload session expired. Resume will restart only this unfinished file.", error);
                        }
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
                await saveCheckpoint(new("onedrive", session.UploadUrl, offset, new Dictionary<string, string>(data)), cancellationToken);
                progress?.Report(new(offset, entry.Size));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        throw new IOException("OneDrive did not return a completed destination file.");
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
            await using var destination = await _client.OpenReadAsync(Location.ContainerId, current, 0, current.Size, cancellationToken);
            var actualHash = Convert.ToHexString(await SHA1.HashDataAsync(destination, cancellationToken));
            if (!expectedHash!.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The OneDrive destination failed independent streaming SHA-1 content verification.");
        }
        else
        {
            // Graph commonly exposes QuickXorHash rather than a cryptographic hash on business drives.
            // In that case compare independent streaming SHA-256 reads through bounded hash buffers.
            await using var original = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
            await using var destination = await _client.OpenReadAsync(Location.ContainerId, current, 0, current.Size, cancellationToken);
            var sourceHash = SHA256.HashDataAsync(original, cancellationToken).AsTask();
            var targetHash = SHA256.HashDataAsync(destination, cancellationToken).AsTask();
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
        await save(new("onedrive", "", item.Size, new Dictionary<string, string>(data)), cancellationToken);
    }
    private void ValidateCheckpoint(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint checkpoint)
    {
        if (checkpoint.Provider != "onedrive" || Data(checkpoint, "drive") != Location.ContainerId || Data(checkpoint, "folder") != Location.FolderId ||
            Data(checkpoint, "operation") != request.OperationId || Data(checkpoint, "sourceId") != source.Entry.Id ||
            Data(checkpoint, "sourceVersion") != source.Entry.Version || Data(checkpoint, "sourceSize") != source.Entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            string.IsNullOrEmpty(Data(checkpoint, "target")) || Data(checkpoint, "target") != request.RelativePath.Replace('\\', '/'))
            throw new InvalidDataException("The saved OneDrive upload session does not match its reviewed source and destination.");
        if (checkpoint.AcknowledgedBytes < 0 || checkpoint.AcknowledgedBytes > source.Entry.Size ||
            (Data(checkpoint, "itemId") is not null && string.IsNullOrEmpty(Data(checkpoint, "itemVersion"))))
            throw new InvalidDataException("The saved OneDrive acknowledgment is incomplete or outside its source length.");
        ValidateRelativePath(Data(checkpoint, "target")!);
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
    private static string Rename(string name, string operation)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation)))[..10].ToLowerInvariant();
        var extension = Path.GetExtension(name);
        return name[..(name.Length - extension.Length)] + " (CloudBay " + suffix + ")" + extension;
    }

    private sealed class OneDriveSource(OneDriveClient client, string driveId, TransferEntry entry) : ITransferSourceFile
    {
        private readonly object _gate = new();
        private (OneDriveItem Item, long ValidatedAt)? _prepared;
        public TransferEntry Entry { get; } = entry;
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

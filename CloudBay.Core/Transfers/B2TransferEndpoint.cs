using CloudBay.Core.B2;

namespace CloudBay.Core.Transfers;

/// <summary>B2 Native API adapter. All file payloads remain in bounded RAM and HTTPS streams.</summary>
public sealed class B2TransferEndpoint : ITransferEndpoint
{
    private readonly B2CloudStore _store;
    private readonly string _prefix;
    private const int DiscoveryEvidenceLimit = 4096;
    private readonly object _discoveryGate = new();
    private readonly Dictionary<string, CloudObject> _discovered = new(StringComparer.Ordinal);
    private readonly Queue<string> _discoveryOrder = new();
    public TransferLocation Location { get; }

    public B2TransferEndpoint(B2CloudStore store, TransferLocation location)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Location = location ?? throw new ArgumentNullException(nameof(location));
        if (!string.Equals(location.Provider, "b2", StringComparison.OrdinalIgnoreCase) ||
            location.AccountId != store.TransferAccountId || string.IsNullOrWhiteSpace(location.ContainerId))
            throw new ArgumentException("The B2 location must belong to this connected account.", nameof(location));
        _prefix = location.Path.Length == 0 ? "" : location.Path.TrimEnd('/') + "/";
    }

    public async Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await _store.ListTransferPageAsync(Location.ContainerId, _prefix, cursor, true, cancellationToken).ConfigureAwait(false);
        return new(page.Files.Where(f => f.Key.EndsWith('/') && f.Key != _prefix).Select(f =>
            new TransferFolder(f.Key, f.Key[_prefix.Length..].TrimEnd('/'), f.Key)).ToArray(), page.Next);
    }

    public async Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await _store.ListTransferPageAsync(Location.ContainerId, _prefix, cursor, false, cancellationToken).ConfigureAwait(false);
        // A successful provider listing already binds each immutable version to
        // this bucket, key, length, checksum and timestamp. Keep only bounded,
        // in-memory evidence; saved entries alone never establish this proof.
        var evidence = new Dictionary<string, CloudObject>(StringComparer.Ordinal);
        foreach (var file in page.Files.Where(file => file.Size > 0 && file.Action == "upload" && file.Key != _prefix))
            if (!evidence.TryAdd(file.FileId, file) && evidence[file.FileId] != file)
                throw new InvalidDataException("B2 discovery repeated a version with different metadata.");
        lock (_discoveryGate)
        {
            foreach (var file in evidence.Values)
                if (_discovered.TryGetValue(file.FileId, out var previous) && previous != file)
                    throw new InvalidDataException("B2 discovery changed immutable version metadata.");
            foreach (var file in evidence.Values)
            {
                if (_discovered.ContainsKey(file.FileId)) continue;
                _discovered.Add(file.FileId, file);
                _discoveryOrder.Enqueue(file.FileId);
                while (_discoveryOrder.Count > DiscoveryEvidenceLimit) _discovered.Remove(_discoveryOrder.Dequeue());
            }
        }
        return new(page.Files.Where(f => f.Key != _prefix).Select(f => new TransferEntry(f.FileId,
            f.Key[_prefix.Length..].TrimEnd('/'), f.FileId, f.Size, f.ModifiedUtc, f.Sha1,
            f.Size == 0 && f.Key.EndsWith('/'))).ToArray(), page.Next);
    }

    public ITransferSourceFile OpenSource(TransferEntry entry)
    {
        var file = ToObject(entry);
        bool discovered;
        lock (_discoveryGate) discovered = _discovered.TryGetValue(file.FileId, out var evidence) && evidence == file;
        return new Source(_store, Location.ContainerId, file, entry, discovered);
    }

    public async Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default)
    {
        var key = CheckpointKey(request, checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        // Every B2 create/start request is preceded by an acknowledged durable intent.
        // No checkpoint means this planned item has never attempted remote creation;
        // its normal conflict lookup is sufficient and avoids a version-list round trip.
        if (checkpoint is null) return null;
        ValidateCheckpoint(request, source, checkpoint);
        if (source.Entry.IsFolder && !key.EndsWith('/')) key += "/";
        var receipt = await _store.FindTransferReceiptAsync(Location.ContainerId, key, request.OperationId, source, cancellationToken).ConfigureAwait(false);
        return receipt is null ? null : receipt with { RelativePath = request.ConflictPolicy == TransferConflictPolicy.Rename
            ? receipt.Data!["key"][_prefix.Length..].TrimEnd('/') : request.RelativePath };
    }

    public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source,
        TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var key = CheckpointKey(request, checkpoint);
        if (source.Entry.IsFolder && !key.EndsWith('/')) key += "/";
        var originalKey = _prefix + request.RelativePath.Replace('\\', '/').TrimEnd('/') + (source.Entry.IsFolder ? "/" : "");
        var alternateSelected = key != originalKey;
        if (checkpoint is not null)
        {
            ValidateCheckpoint(request, source, checkpoint);
            if (checkpoint.AcknowledgedBytes == 0 && checkpoint.Data!.GetValueOrDefault("kind") == "small" &&
                checkpoint.Data!.GetValueOrDefault("pending") == "false")
            {
                // A definite small-upload rejection created no remote file. A
                // restart must recheck its selected path against today's conflicts.
                // Once selected, an alternate name is retained even if the original
                // later becomes available.
                checkpoint = null;
            }
            else if (checkpoint.Data!.GetValueOrDefault("kind") == "large" &&
                request.ConflictPolicy is TransferConflictPolicy.Rename or TransferConflictPolicy.Fail or TransferConflictPolicy.Skip &&
                await _store.FindTransferCurrentAsync(Location.ContainerId, key, cancellationToken).ConfigureAwait(false) is not null)
            {
                var recovered = await _store.FindTransferReceiptAsync(Location.ContainerId, key, request.OperationId, source, cancellationToken).ConfigureAwait(false);
                if (recovered is not null)
                {
                    progress?.Report(new(source.Entry.Size, source.Entry.Size) { IsBaseline = true });
                    return recovered with { RelativePath = key[_prefix.Length..].TrimEnd('/') };
                }
                throw new TransferConflictException("The selected B2 upload path became occupied. Its unfinished upload and source are retained for review.");
            }
        }
        if (checkpoint is null)
        {
            var current = await _store.FindTransferCurrentAsync(Location.ContainerId, key, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                if (request.ConflictPolicy == TransferConflictPolicy.Skip)
                    throw new TransferSkippedException("The B2 destination already contains this name.");
                if (request.ConflictPolicy == TransferConflictPolicy.Fail)
                    throw new TransferConflictException("The B2 destination already contains this name.");
                if (request.ConflictPolicy == TransferConflictPolicy.Rename)
                {
                    if (alternateSelected)
                        throw new TransferConflictException("The stable renamed B2 destination became occupied and belongs to another operation.");
                    key = RenamedKey(request) + (source.Entry.IsFolder ? "/" : "");
                    if (await _store.FindTransferCurrentAsync(Location.ContainerId, key, cancellationToken).ConfigureAwait(false) is not null)
                        throw new TransferConflictException("The stable renamed B2 destination already exists and belongs to another operation.");
                }
            }
        }
        var receipt = await _store.UploadTransferAsync(Location.ContainerId, key, request, source, checkpoint,
            saveCheckpoint, progress, cancellationToken).ConfigureAwait(false);
        return receipt with { RelativePath = request.ConflictPolicy == TransferConflictPolicy.Rename
            ? key[_prefix.Length..].TrimEnd('/') : request.RelativePath };
    }

    public Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default) =>
        _store.VerifyTransferReceiptAsync(Location.ContainerId, receipt, source, cancellationToken);
    public async Task<TransferReceipt> VerifyReceiptAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
    {
        var hash = await _store.VerifyTransferReceiptAsync(Location.ContainerId, receipt, source, cancellationToken).ConfigureAwait(false);
        return receipt with { Sha1 = hash };
    }
    public Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default) =>
        _store.DeleteTransferSourceAsync(Location.ContainerId, ToObject(entry), cancellationToken);
    public Task<bool> IsSourceDeletedAsync(TransferEntry entry, CancellationToken cancellationToken = default) =>
        _store.IsTransferSourceDeletedAsync(Location.ContainerId, ToObject(entry), cancellationToken);

    private string CheckpointKey(TransferUploadRequest request, TransferCheckpoint? checkpoint)
    {
        var relative = request.RelativePath.Replace('\\', '/').TrimEnd('/');
        if (relative.Length == 0 || relative.StartsWith('/') || relative.Split('/').Any(p => p is ".." or "." or ""))
            throw new ArgumentException("The destination path must be relative to its selected B2 folder.", nameof(request));
        var expected = _prefix + relative;
        var key = checkpoint?.Data?.GetValueOrDefault("key") ?? expected;
        if (key != expected && key != expected + "/" &&
            !(request.ConflictPolicy == TransferConflictPolicy.Rename && (key == RenamedKey(request) || key == RenamedKey(request) + "/" ||
                key == LegacyRenamedKey(request) || key == LegacyRenamedKey(request) + "/")))
            throw new InvalidDataException("The saved B2 destination path differs from the reviewed plan.");
        return key;
    }

    private string RenamedKey(TransferUploadRequest request)
    {
        if (request.OperationId is not { Length: 64 } || !request.OperationId.All(Uri.IsHexDigit))
            throw new ArgumentException("A stable SHA256 operation identity is required.", nameof(request));
        return _prefix + TransferConflictNames.RenamePath(request.RelativePath.TrimEnd('/'), request.OperationId);
    }

    private string LegacyRenamedKey(TransferUploadRequest request)
    {
        if (request.OperationId is not { Length: 64 } || !request.OperationId.All(Uri.IsHexDigit))
            throw new ArgumentException("A stable SHA256 operation identity is required.", nameof(request));
        var relative = request.RelativePath.Replace('\\', '/').TrimEnd('/');
        var slash = relative.LastIndexOf('/');
        var dot = relative.LastIndexOf('.');
        if (dot <= slash) dot = relative.Length;
        return _prefix + relative[..dot] + " (" + request.OperationId[..12] + ")" + relative[dot..];
    }

    private static void ValidateCheckpoint(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint checkpoint)
    {
        if (checkpoint.Provider != "b2" || checkpoint.Data is null ||
            checkpoint.Data.GetValueOrDefault("operation_id") != request.OperationId ||
            checkpoint.Data.GetValueOrDefault("source_id") != B2CloudStore.TransferSourceId(source))
            throw new InvalidDataException("The saved B2 checkpoint belongs to another source or transfer operation.");
    }

    private CloudObject ToObject(TransferEntry entry)
    {
        var relative = entry.RelativePath.Replace('\\', '/');
        if (relative.Length == 0 || relative.StartsWith('/') || relative.Split('/').Any(p => p is ".." or "." or ""))
            throw new ArgumentException("The source path must be relative to its selected B2 folder.", nameof(entry));
        if (entry.Id != entry.Version || string.IsNullOrWhiteSpace(entry.Id))
            throw new InvalidDataException("The saved B2 source requires its immutable file version identity.");
        return new(entry.Id, _prefix + relative + (entry.IsFolder ? "/" : ""), entry.Size, entry.Sha1, entry.ModifiedUtc);
    }

    private sealed class Source(B2CloudStore store, string bucketId, CloudObject file, TransferEntry entry, bool discovered) : ITransferSourceFile
    {
        private readonly object _gate = new();
        private Task? _validation = discovered ? Task.CompletedTask : null;
        public TransferEntry Entry => entry;
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default) =>
            store.OpenTransferReadAsync(file, offset, length, cancellationToken);
        public async Task ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task validation;
            // A B2 version ID is immutable. Reuse its metadata validation through
            // preparation, transfer and verification; restart constructs a fresh source.
            // Range response identity is always checked and Move performs a fresh lookup.
            lock (_gate) validation = _validation ??= store.ValidateTransferSourceAsync(bucketId, file, cancellationToken);
            try { await validation.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                lock (_gate) if (ReferenceEquals(_validation, validation) && validation.IsCompleted) _validation = null;
                throw;
            }
        }
    }
}

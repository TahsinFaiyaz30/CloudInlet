using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Core.B2;

namespace CloudInlet.Tests;

/// <summary>Exact-ID, bounded-RAM boundary for one real multipart fixture. Payloads are never persisted.</summary>
internal sealed class CloudPrefixMultipartAcceptanceTransport(string directory, byte[] seed, string wholeSha1,
    IReadOnlyList<string> migrationHashes, HttpMessageHandler? inner = null) : DelegatingHandler(
        inner ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    internal const int SeedLength = 50_000_000;
    internal const long LargeLength = 5_000_000_000;
    internal const long MigrationPartLength = 100 * 1024 * 1024;
    private string? _apiUrl, _authorization, _accountId;
    private bool _unconfirmedMutation;
    private readonly HashSet<string> _uploadUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, string>> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unfinished = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _copiedKeys = new(StringComparer.Ordinal);
    internal CloudPrefixAcceptanceScope? Scope { get; set; }
    internal int Mutations { get; private set; }
    internal int SourcePartRequests { get; private set; }
    internal int DestinationPartRequests { get; private set; }
    internal long UploadedPayloadBytes { get; private set; }
    internal string SeedHash { get; } = Convert.ToHexString(SHA1.HashData(seed)).ToLowerInvariant();
    internal string? SourceLargeId { get; private set; }
    internal string? DestinationLargeId { get; private set; }
    internal IReadOnlyDictionary<string, string> Unfinished => _unfinished;
    internal IReadOnlyCollection<string> Deleted => _deleted;
    internal bool HasUnconfirmedMutation => _unconfirmedMutation;
    private sealed record PartAttempt(string Key, string FileId, string SourceId, int Number, long Offset, long Length, string Hash);
    private PartAttempt? _pendingPart;
    internal int ReplayedUnconfirmedParts { get; private set; }

    internal void RestoreAuthenticatedMultipart(CloudInlet.Core.CloudAccount account, B2CloudPrefixMigrationInventory inventory,
        CloudInlet.Core.Migration.CloudPrefixMigrationCheckpoint history, B2CloudPrefixCopyCheckpoint provider)
    {
        var scope = Scope ?? throw new InvalidDataException("Missing recovery scope.");
        if (Mutations != 0 || _unfinished.Count != 0 || _parts.Count != 0 || _unconfirmedMutation ||
            history.Phase != CloudInlet.Core.Migration.CloudPrefixMigrationPhase.Copying ||
            history.AccountId != account.AccountId || history.BucketId != scope.Bucket ||
            history.SourcePrefix != scope.Source || history.DestinationPrefix != scope.Destination ||
            provider.AccountId != account.AccountId || provider.BucketId != scope.Bucket ||
            provider.SourcePrefix != scope.Source || provider.DestinationPrefix != scope.Destination ||
            history.Entries.Count != 2 || history.Entries.Any(value => value.Destination is not null || value.Removed) ||
            provider.Intents.Count != 1 || provider.HideReceipts?.Count > 0)
            throw new InvalidDataException("Multipart recovery needs the exact incomplete authenticated fixture journals.");
        var intent = provider.Intents.Single();
        var source = history.Entries.Single(value => value.Source.Key == scope.Source + "multipart.bin");
        var seedSource = history.Entries.Single(value => value.Source.Key == scope.Source + "seed.bin");
        if (!source.RequestPending || seedSource.RequestPending || intent.OperationId != source.OperationId ||
            JsonSerializer.Serialize(source.Source) != JsonSerializer.Serialize(intent.Source) ||
            intent.Source.Size != LargeLength || intent.Source.Sha1 != wholeSha1 ||
            seedSource.Source.Size != SeedLength || seedSource.Source.Sha1 != SeedHash ||
            intent.DestinationKey != scope.Destination + "multipart.bin" || intent.Completed is not null ||
            intent.Phase != B2CloudPrefixCopyPhase.CopyingParts || string.IsNullOrWhiteSpace(intent.LargeFileId) ||
            intent.PartSize != MigrationPartLength || intent.PartHashes?.Count != migrationHashes.Count ||
            intent.PartHashes.Where((hash, index) => hash is not null && hash != migrationHashes[index]).Any() ||
            inventory.DestinationVersions.Any(value => value.Action != "start") ||
            inventory.SourceVersions.Count != 2 || history.Entries.Any(entry => !inventory.SourceVersions.Any(value =>
                JsonSerializer.Serialize(value) == JsonSerializer.Serialize(entry.Source))))
            throw new InvalidDataException("Multipart recovery content or saved progress differs from its original fixture.");
        CloudPrefixAcceptanceScope.OwnedVersion unfinished = new(intent.LargeFileId, intent.DestinationKey);
        var acknowledged = history.Entries.Select(value => new CloudPrefixAcceptanceScope.OwnedVersion(value.Source.FileId, value.Source.Key))
            .Append(unfinished).ToArray();
        scope.RestoreForMultipartRecovery(account, inventory, acknowledged, [unfinished]);
        SourceLargeId = source.Source.FileId; DestinationLargeId = intent.LargeFileId;
        _startedKeys.Add(scope.Source + "multipart.bin"); _startedKeys.Add(scope.Destination + "multipart.bin");
        _unfinished.Add(intent.LargeFileId, intent.DestinationKey);
        _parts.Add(intent.LargeFileId, intent.PartHashes.Select((hash, index) => (hash, index))
            .Where(value => value.hash is not null).ToDictionary(value => value.index + 1, value => value.hash!));
        UploadedPayloadBytes = SeedLength; // Already created and authenticated; never upload again.
        RequirePolicy(inventory, allowOwnedUnfinished: true);
    }

    internal async Task<JsonDocument> FixturePostAsync(string operation, object body, CancellationToken token)
    {
        if (operation is not ("b2_start_large_file" or "b2_copy_part" or "b2_finish_large_file" or "b2_delete_file_version") ||
            _apiUrl is null || _authorization is null)
            throw new InvalidOperationException("Unsupported multipart fixture request.");
        using var client = new HttpClient(this, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(3) };
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl + "/b2api/v4/" + operation) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        // Fixture creation deliberately has no automatic retry. Unknown outcomes preserve evidence.
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new IOException("A scoped multipart fixture request was rejected.");
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    internal void RequirePolicy(B2CloudPrefixMigrationInventory inventory, bool allowOwnedUnfinished)
    {
        var scope = Scope ?? throw new InvalidDataException("Missing multipart fixture scope.");
        var all = inventory.SourceVersions.Concat(inventory.DestinationVersions).Concat(inventory.UnfinishedUploads).ToArray();
        var visible = inventory.SourceVersions.Concat(inventory.DestinationVersions).ToArray();
        if (visible.Select(version => version.FileId).Distinct(StringComparer.Ordinal).Count() != visible.Length ||
            inventory.UnfinishedUploads.Select(version => version.FileId).Distinct(StringComparer.Ordinal).Count() != inventory.UnfinishedUploads.Count)
            throw new InvalidDataException("The multipart fixture inventory repeats an immutable identity.");
        foreach (var version in all)
        {
            var owned = scope.RequireOwned(version.FileId);
            if (owned.Key != version.Key || !version.ProtectionReadable || version.RetentionMode is not null ||
                version.RetainUntilMillis is not null || version.LegalHold is not (null or "off") || version.ReplicationStatus is not null)
                throw new InvalidDataException("Multipart fixture identity or protection changed.");
            if (version.Action == "start" && (!allowOwnedUnfinished || !_unfinished.TryGetValue(version.FileId, out var key) || key != version.Key))
                throw new InvalidDataException("Unfinished multipart history lacks this run's exact acknowledgment.");
        }
        if (inventory.UnfinishedUploads.Any(version => version.Action != "start"))
            throw new InvalidDataException("Unexpected unfinished fixture metadata.");
        // Only exact acknowledged unfinished IDs may be excluded from the ordinary small-fixture policy.
        scope.RequirePolicy(inventory with
        {
            SourceVersions = inventory.SourceVersions.Where(version => version.Action != "start").ToArray(),
            DestinationVersions = inventory.DestinationVersions.Where(version => version.Action != "start").ToArray(),
            UnfinishedUploads = [],
            Blockers = inventory.Blockers.Where(blocker => !(allowOwnedUnfinished && blocker.Code == "unfinished-uploads")).ToArray()
        });
        var actualIds = all.Select(version => version.FileId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var expectedIds = scope.RemainingOwned.Select(version => version.Id).ToHashSet(StringComparer.Ordinal);
        if (!actualIds.SetEquals(expectedIds)) throw new InvalidDataException("The exact multipart fixture inventory changed.");
        if (!_unfinished.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(inventory.UnfinishedUploads.Select(version => version.FileId)))
            throw new InvalidDataException("The acknowledged unfinished multipart fixture changed.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri ?? throw new InvalidDataException("Missing provider URI.");
        var upload = _uploadUrls.Contains(uri.AbsoluteUri);
        if (!ProviderUri(uri, upload)) throw new InvalidDataException("Unexpected multipart provider origin.");
        var operation = upload ? "b2_upload_file" : uri.Segments[^1];
        if (!upload && (uri.AbsolutePath != "/b2api/v4/" + operation || uri.Query.Length != 0))
            throw new InvalidDataException("Unexpected multipart provider path.");
        if (!upload && operation != "b2_authorize_account" &&
            (_apiUrl is null || uri.GetLeftPart(UriPartial.Authority) != _apiUrl))
            throw new InvalidDataException("The multipart API origin changed from its authenticated endpoint.");
        if (request.Method != (operation == "b2_authorize_account" ? HttpMethod.Get : HttpMethod.Post))
            throw new InvalidDataException("Unexpected multipart request method.");
        JsonDocument? body = null;
        string? key = null, fileId = null, sourceId = null, expectedHash = null;
        long length = 0, offset = 0;
        int part = 0;
        var mutation = false;
        try
        {
            if (request.Method == HttpMethod.Post && !upload)
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (bytes.Length > 64 * 1024) throw new InvalidDataException("Multipart metadata request exceeded its bound.");
                body = JsonDocument.Parse(bytes);
            }
            string Text(string name) => body!.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing fixture metadata.");
            void Bucket() { if (Text("bucketId") != Scope!.Bucket) throw new InvalidDataException("Multipart request changed buckets."); }
            void Write(string name)
            {
                Scope!.RequireWrite(name);
                if (seed.Length != SeedLength || wholeSha1.Length != 40 || !wholeSha1.All(Uri.IsHexDigit) ||
                    migrationHashes.Count != (LargeLength + MigrationPartLength - 1) / MigrationPartLength ||
                    migrationHashes.Any(hash => hash.Length != 40 || !hash.All(Uri.IsHexDigit)))
                    throw new InvalidDataException("The bounded multipart fixture definition is invalid.");
                key = name; mutation = true;
            }
            switch (operation)
            {
                case "b2_authorize_account":
                    if (uri.Host != "api.backblazeb2.com") throw new InvalidDataException("Unexpected authorization origin.");
                    break;
                case "b2_list_buckets": break;
                case "b2_list_file_versions":
                case "b2_list_unfinished_large_files":
                    Bucket();
                    var prefix = Text(operation == "b2_list_unfinished_large_files" ? "namePrefix" : "prefix");
                    if (prefix != Scope!.Source && prefix != Scope.Destination) Scope.RequireKey(prefix);
                    break;
                case "b2_get_upload_url":
                    Bucket();
                    if (Scope?.WritesEnabled != true || Scope.CleanupEnabled || UploadedPayloadBytes != 0)
                        throw new InvalidDataException("Only one isolated seed upload is permitted.");
                    break;
                case "b2_upload_file":
                    if (seed.Length != SeedLength || request.Content!.Headers.ContentLength != SeedLength || UploadedPayloadBytes != 0 ||
                        request.Headers.GetValues("X-Bz-Content-Sha1").Single() != SeedHash)
                        throw new InvalidDataException("The seed upload changed size or checksum.");
                    var seedKey = Uri.UnescapeDataString(request.Headers.GetValues("X-Bz-File-Name").Single());
                    if (seedKey != Scope!.Source + "seed.bin") throw new InvalidDataException("Seed upload escaped its exact filename.");
                    Write(seedKey); length = SeedLength; expectedHash = SeedHash;
                    UploadedPayloadBytes += SeedLength;
                    break;
                case "b2_start_large_file":
                    Bucket(); Write(Text("fileName"));
                    if (key != Scope!.Source + "multipart.bin" && key != Scope.Destination + "multipart.bin" || !_startedKeys.Add(key))
                        throw new InvalidDataException("A large-file start escaped its mapping or would be duplicated.");
                    var info = body!.RootElement.GetProperty("fileInfo");
                    if (info.GetProperty("large_file_sha1").GetString() != wholeSha1)
                        throw new InvalidDataException("Large-file start has a different whole-file checksum.");
                    break;
                case "b2_copy_file":
                    if (Text("destinationBucketId") != Scope!.Bucket) throw new InvalidDataException("A seed copy changed buckets.");
                    sourceId = Text("sourceFileId");
                    if (Scope.RequireOwned(sourceId).Key != Scope.Source + "seed.bin" || Text("fileName") != Scope.Destination + "seed.bin")
                        throw new InvalidDataException("Only the exact seed migration may use a small copy.");
                    Write(Text("fileName")); length = SeedLength; expectedHash = SeedHash;
                    if (!_copiedKeys.Add(key!)) throw new InvalidDataException("The acceptance run refuses a duplicate seed copy.");
                    break;
                case "b2_copy_part":
                    fileId = Text("largeFileId"); sourceId = Text("sourceFileId");
                    if (!_unfinished.TryGetValue(fileId, out var largeKey)) throw new InvalidDataException("The large-file ID is not an acknowledged unfinished fixture.");
                    Write(largeKey); part = body!.RootElement.GetProperty("partNumber").GetInt32();
                    if (largeKey == Scope!.Source + "multipart.bin")
                    {
                        if (part is < 1 or > 100 || Scope.RequireOwned(sourceId).Key != Scope.Source + "seed.bin")
                            throw new InvalidDataException("Source construction escaped its exact seed and part range.");
                        length = SeedLength; expectedHash = SeedHash;
                    }
                    else
                    {
                        if (largeKey != Scope.Destination + "multipart.bin" || sourceId != SourceLargeId || part < 1 || part > migrationHashes.Count)
                            throw new InvalidDataException("Migration part escaped its exact immutable source.");
                        offset = (part - 1) * MigrationPartLength;
                        length = Math.Min(MigrationPartLength, LargeLength - offset); expectedHash = migrationHashes[part - 1];
                    }
                    if (Text("range") != FormattableString.Invariant($"bytes={offset}-{offset + length - 1}"))
                        throw new InvalidDataException("Multipart request changed its immutable source range.");
                    if (_parts[fileId].ContainsKey(part)) throw new InvalidDataException("The acceptance run refuses to silently recopy acknowledged parts.");
                    break;
                case "b2_list_parts":
                    fileId = Text("fileId"); Scope!.RequireOwned(fileId);
                    if (!_unfinished.ContainsKey(fileId)) throw new InvalidDataException("Only exact unfinished fixture parts can be listed.");
                    break;
                case "b2_finish_large_file":
                    fileId = Text("fileId");
                    if (!_unfinished.TryGetValue(fileId, out var finishKey)) throw new InvalidDataException("Only an acknowledged unfinished fixture can be finished.");
                    Write(finishKey);
                    var expected = finishKey == Scope!.Source + "multipart.bin" ? Enumerable.Repeat(SeedHash, 100).ToArray() : migrationHashes.ToArray();
                    var hashes = body!.RootElement.GetProperty("partSha1Array").EnumerateArray().Select(value => value.GetString()).ToArray();
                    if (!hashes.SequenceEqual(expected) || _parts[fileId].Count != expected.Length ||
                        expected.Where((hash, index) => !_parts[fileId].TryGetValue(index + 1, out var known) || known != hash).Any())
                        throw new InvalidDataException("Finish requires every exact acknowledged expected part.");
                    length = LargeLength; expectedHash = wholeSha1;
                    break;
                case "b2_get_file_info": Scope!.RequireOwned(Text("fileId")); break;
                case "b2_delete_file_version":
                    fileId = Text("fileId"); key = Text("fileName"); Scope!.RequireDelete(fileId, key);
                    if (_unfinished.ContainsKey(fileId) || _deleted.Contains(fileId)) throw new InvalidDataException("Cleanup cannot guess unfinished or repeated deletion outcomes.");
                    mutation = true; break;
                default: throw new InvalidDataException("Unsupported multipart acceptance endpoint.");
            }
            if (mutation)
            {
                var nextPart = operation == "b2_copy_part" ? new PartAttempt(key!, fileId!, sourceId!, part, offset, length, expectedHash!) : null;
                // Replacing the same part of our acknowledged unfinished ID with the same
                // immutable source/range is idempotent. No other unknown mutation may replay.
                if (_unconfirmedMutation && (nextPart is null || nextPart != _pendingPart))
                    throw new InvalidDataException("An earlier multipart request has an unconfirmed outcome. Only its identical unfinished part may retry.");
                Mutations++;
                Append("multipart-attempts.jsonl", new { attempt = Mutations, utc = DateTimeOffset.UtcNow, operation, key, fileId, sourceId, part, offset, length, expectedHash });
                if (_unconfirmedMutation) ReplayedUnconfirmedParts++;
                _unconfirmedMutation = true;
                _pendingPart = nextPart;
                if (nextPart is not null)
                {
                    if (key == Scope!.Source + "multipart.bin") SourcePartRequests++;
                    else DestinationPartRequests++;
                }
            }
            var response = await base.SendAsync(request, token);
            try
            {
                if (!response.IsSuccessStatusCode) return response;
                var responseBytes = await ReadMetadataAsync(response, token);
                using var json = JsonDocument.Parse(responseBytes);
                var root = json.RootElement;
                if (operation == "b2_authorize_account")
                {
                    _authorization = root.GetProperty("authorizationToken").GetString();
                    _apiUrl = root.GetProperty("apiInfo").GetProperty("storageApi").GetProperty("apiUrl").GetString();
                    _accountId = root.GetProperty("accountId").GetString();
                    if (!Uri.TryCreate(_apiUrl, UriKind.Absolute, out var api) || !ProviderUri(api, false) ||
                        api.AbsolutePath != "/" || api.Query.Length != 0 || string.IsNullOrWhiteSpace(_authorization) || string.IsNullOrWhiteSpace(_accountId))
                        throw new InvalidDataException("Incomplete or unsafe multipart authorization response.");
                    _apiUrl = api.GetLeftPart(UriPartial.Authority);
                }
                else if (operation == "b2_get_upload_url")
                {
                    var url = new Uri(root.GetProperty("uploadUrl").GetString()!, UriKind.Absolute);
                    if (!ProviderUri(url, true)) throw new InvalidDataException("Unexpected upload provider origin.");
                    _uploadUrls.Add(url.AbsoluteUri);
                }
                if (mutation)
                {
                    var actualId = root.GetProperty("fileId").GetString() ?? throw new InvalidDataException("Missing acknowledged immutable ID.");
                    if (fileId is not null && actualId != fileId) throw new InvalidDataException("Multipart acknowledgment changed immutable identity.");
                    if (operation == "b2_copy_part")
                    {
                        if (root.GetProperty("partNumber").GetInt32() != part || root.GetProperty("contentLength").GetInt64() != length ||
                            root.GetProperty("contentSha1").GetString() != expectedHash)
                            throw new InvalidDataException("The real server-side part differs from the independently computed content checksum.");
                    }
                    else
                    {
                        if (root.GetProperty("fileName").GetString() != key) throw new InvalidDataException("Multipart acknowledgment changed its filename.");
                        if (operation != "b2_delete_file_version" &&
                            (root.GetProperty("accountId").GetString() != _accountId || root.GetProperty("bucketId").GetString() != Scope!.Bucket))
                            throw new InvalidDataException("Multipart acknowledgment changed account or bucket.");
                        if (operation is "b2_upload_file" or "b2_copy_file" or "b2_finish_large_file")
                        {
                            var sha = root.GetProperty("contentSha1").GetString();
                            if (sha == "none") sha = root.GetProperty("fileInfo").GetProperty("large_file_sha1").GetString();
                            if (root.GetProperty("contentLength").GetInt64() != length || sha != expectedHash)
                                throw new InvalidDataException("The acknowledged fixture file changed size or whole-file checksum.");
                        }
                    }
                    // Flush the acknowledgment before permitting subsequent requests, including owned unfinished IDs.
                    Append("multipart-acknowledgments.jsonl", new { attempt = Mutations, utc = DateTimeOffset.UtcNow, operation, key, fileId = actualId, sourceId, part, offset, length, expectedHash });
                    if (operation == "b2_copy_part") _parts[actualId].Add(part, expectedHash!);
                    else if (operation == "b2_delete_file_version")
                    {
                        Scope!.AcknowledgeDeletion(actualId, key!);
                        _deleted.Add(actualId);
                    }
                    else
                    {
                        Scope!.Acknowledge(actualId, key!);
                        if (operation == "b2_start_large_file")
                        {
                            _unfinished.Add(actualId, key!); _parts.Add(actualId, []);
                            if (key == Scope.Source + "multipart.bin") SourceLargeId = actualId;
                            else DestinationLargeId = actualId;
                        }
                        if (operation == "b2_finish_large_file") _unfinished.Remove(actualId);
                    }
                    _unconfirmedMutation = false;
                    _pendingPart = null;
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        finally { body?.Dispose(); }
    }

    private void Append(string name, object record)
    {
        using var file = new FileStream(Path.Combine(directory, name), FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(JsonSerializer.SerializeToUtf8Bytes(record)); file.WriteByte((byte)'\n'); file.Flush(flushToDisk: true);
    }
    private static async Task<byte[]> ReadMetadataAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 1024 * 1024;
        var original = response.Content;
        if (original.Headers.ContentLength > limit) throw new InvalidDataException("Multipart metadata response exceeded its bound.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var stream = await original.ReadAsStreamAsync(token);
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Multipart metadata response exceeded its bound.");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        var replacement = new ByteArrayContent(bytes);
        foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        original.Dispose();
        return bytes;
    }
    private static bool ProviderUri(Uri uri, bool upload) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host.EndsWith(".backblazeb2.com", StringComparison.OrdinalIgnoreCase) ||
            upload && uri.Host.EndsWith(".backblaze.com", StringComparison.OrdinalIgnoreCase));
}

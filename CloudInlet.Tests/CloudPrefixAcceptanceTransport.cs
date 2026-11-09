using System.Net.Http.Json;
using System.Text.Json;

namespace CloudInlet.Tests;

/// <summary>Test-only network boundary and creation-acknowledgment ledger. Never records auth material.</summary>
internal sealed class CloudPrefixAcceptanceTransport(string directory, HttpMessageHandler? inner = null) : DelegatingHandler(
    inner ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    private string? _apiUrl, _authorization;
    private readonly HashSet<string> _uploadUrls = new(StringComparer.Ordinal);
    private bool _dropNextCopyAcknowledgment;
    private bool _dropNextDeleteAcknowledgment;
    private bool _dropNextHideAcknowledgment;
    internal CloudPrefixAcceptanceScope? Scope { get; set; }
    internal int Mutations { get; private set; }
    internal int PayloadDownloads { get; private set; }
    internal CloudPrefixAcceptanceScope.OwnedVersion? DroppedCopyAcknowledgment { get; private set; }
    internal CloudPrefixAcceptanceScope.OwnedVersion? DroppedDeleteAcknowledgment { get; private set; }
    internal CloudPrefixAcceptanceScope.OwnedVersion? DroppedHideAcknowledgment { get; private set; }
    internal sealed record MutationAttempt(int Attempt, DateTimeOffset Utc, string Endpoint, string FileName,
        string? SourceFileId, string? FileId, string? OperationReceipt);

    internal void ArmOneCopyAcknowledgmentLoss()
    {
        if (Scope?.WritesEnabled != true || Scope.CleanupEnabled || _dropNextCopyAcknowledgment || DroppedCopyAcknowledgment is not null)
            throw new InvalidOperationException("The isolated copy-response fault can be armed only once after preflight.");
        _dropNextCopyAcknowledgment = true;
    }

    internal void ArmOneDeleteAcknowledgmentLoss()
    {
        if (Scope?.RevertEnabled != true || Scope.CleanupEnabled || _dropNextDeleteAcknowledgment || DroppedDeleteAcknowledgment is not null)
            throw new InvalidOperationException("The isolated delete-response fault can be armed only once for reviewed destination recovery.");
        _dropNextDeleteAcknowledgment = true;
    }

    internal void ArmOneHideAcknowledgmentLoss()
    {
        if (Scope?.WritesEnabled != true || Scope.CleanupEnabled || Scope.RevertEnabled || _dropNextHideAcknowledgment || DroppedHideAcknowledgment is not null)
            throw new InvalidOperationException("The isolated hide-response fault can be armed only once after fixture seeding.");
        _dropNextHideAcknowledgment = true;
    }

    internal async Task<JsonDocument> PostAsync(string operation, object body, CancellationToken token)
    {
        if (operation is not ("b2_hide_file" or "b2_delete_file_version") || _apiUrl is null || _authorization is null)
            throw new InvalidOperationException("Only exact fixture hide/delete requests are supported here.");
        using var client = new HttpClient(this, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl + "/b2api/v4/" + operation) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        // Deliberately no retry: an unacknowledged creating/deleting request stops this acceptance run.
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new IOException("The scoped fixture request was rejected.");
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri ?? throw new InvalidDataException("Missing provider URI.");
        var acknowledgedUpload = _uploadUrls.Contains(uri.AbsoluteUri);
        if (!ProviderUri(uri, allowUploadHost: acknowledgedUpload))
            throw new InvalidDataException("The fixture refuses an unexpected provider origin.");
        // Real upload URLs contain account/session path suffixes. Only the exact acknowledged URL
        // identifies a payload request; never interpret its body as JSON or infer it from a suffix.
        var operation = acknowledgedUpload ? "b2_upload_file" : uri.Segments[^1];
        if (!acknowledgedUpload && (uri.AbsolutePath != "/b2api/v4/" + operation ||
            operation != "b2_download_file_by_id" && uri.Query.Length != 0))
            throw new InvalidDataException("Unexpected fixture API path or parameters.");
        var expectedMethod = operation is "b2_authorize_account" or "b2_download_file_by_id" ? HttpMethod.Get : HttpMethod.Post;
        if (request.Method != expectedMethod) throw new InvalidDataException("Unexpected fixture HTTP method.");
        JsonDocument? body = null;
        string? creationKey = null;
        string? sourceFileId = null, deletedFileId = null, deletedKey = null, operationReceipt = null;
        try
        {
            if (request.Method == HttpMethod.Post && operation != "b2_upload_file")
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (bytes.Length > 64 * 1024) throw new InvalidDataException("Unexpected fixture metadata request size.");
                body = JsonDocument.Parse(bytes);
            }
            string Text(string name) => body!.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing fixture metadata.");
            void Bucket() { if (Text("bucketId") != Scope!.Bucket) throw new InvalidDataException("The request changed buckets."); }
            void Creation(string key) { Scope!.RequireWrite(key); creationKey = key; Mutations++; }
            switch (operation)
            {
                case "b2_authorize_account":
                    if (request.Method != HttpMethod.Get || uri.Host != "api.backblazeb2.com") throw new InvalidDataException("Unexpected authorization endpoint.");
                    break;
                case "b2_list_buckets": break;
                case "b2_list_file_versions":
                case "b2_list_unfinished_large_files":
                case "b2_list_file_names":
                    Bucket();
                    var prefix = Text(operation == "b2_list_unfinished_large_files" ? "namePrefix" : "prefix");
                    if (prefix != Scope!.Source && prefix != Scope.Destination) Scope.RequireKey(prefix);
                    break;
                case "b2_get_upload_url":
                    Bucket();
                    if (Scope?.WritesEnabled != true || Scope.CleanupEnabled) throw new InvalidDataException("Uploads are not enabled.");
                    break;
                case "b2_upload_file":
                    if (!_uploadUrls.Contains(uri.AbsoluteUri) || request.Content!.Headers.ContentLength is not (>= 0 and <= 1024 * 1024))
                        throw new InvalidDataException("Only bounded fixture uploads to an acknowledged upload endpoint are allowed.");
                    var name = Uri.UnescapeDataString(request.Headers.GetValues("X-Bz-File-Name").Single());
                    if (!name.StartsWith(Scope!.Source, StringComparison.Ordinal)) throw new InvalidDataException("Seed uploads require the fixture source.");
                    Creation(name);
                    break;
                case "b2_copy_file":
                    if (Text("destinationBucketId") != Scope!.Bucket) throw new InvalidDataException("A copy changed buckets.");
                    var original = Scope.RequireOwned(Text("sourceFileId"));
                    var target = Text("fileName");
                    if (!original.Key.StartsWith(Scope.Source, StringComparison.Ordinal) || target != Scope.Destination + original.Key[Scope.Source.Length..])
                        throw new InvalidDataException("A copy escaped its exact source-to-destination mapping.");
                    sourceFileId = original.Id;
                    if (body!.RootElement.TryGetProperty("fileInfo", out var fileInfo) && fileInfo.ValueKind == JsonValueKind.Object &&
                        fileInfo.TryGetProperty(CloudInlet.Core.Migration.CloudPrefixMigration.ReceiptMetadataKey, out var receipt) &&
                        receipt.ValueKind == JsonValueKind.String && receipt.GetString() is { Length: 64 } receiptValue && receiptValue.All(Uri.IsHexDigit))
                        operationReceipt = receiptValue;
                    Creation(target);
                    break;
                case "b2_hide_file": Bucket(); Creation(Text("fileName")); break;
                case "b2_delete_file_version":
                    deletedFileId = Text("fileId"); deletedKey = Text("fileName");
                    Scope!.RequireDelete(deletedFileId, deletedKey); Mutations++; break;
                case "b2_get_file_info": Scope!.RequireOwned(Text("fileId")); break;
                case "b2_download_file_by_id":
                    var query = uri.Query.TrimStart('?').Split('&');
                    if (query.Length != 1 || !query[0].StartsWith("fileId=", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected download parameters.");
                    Scope!.RequireOwned(Uri.UnescapeDataString(query[0][7..])); PayloadDownloads++; break;
                default: throw new InvalidDataException("The bounded acceptance runner refused an unsupported endpoint.");
            }
            if (creationKey is not null || deletedKey is not null)
            {
                // Record only allowlisted identifiers before dispatch. A failed durable write stops
                // the network call; a record means an attempt was prepared, not that B2 committed it.
                using var attempts = new FileStream(Path.Combine(directory, "mutation-attempts.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                var record = JsonSerializer.SerializeToUtf8Bytes(new MutationAttempt(Mutations, DateTimeOffset.UtcNow, operation,
                    creationKey ?? deletedKey!, sourceFileId, deletedFileId, operationReceipt));
                attempts.Write(record); attempts.WriteByte((byte)'\n'); attempts.Flush(flushToDisk: true);
            }
            var response = await base.SendAsync(request, token);
            try
            {
                if (!response.IsSuccessStatusCode || operation == "b2_download_file_by_id") return response;
                // Metadata only. Never inspect, buffer or persist download bodies here.
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                if (bytes.Length > 1024 * 1024) throw new InvalidDataException("The fixture metadata response exceeded its bound.");
                using var json = JsonDocument.Parse(bytes);
                if (operation == "b2_authorize_account")
                {
                    _authorization = json.RootElement.GetProperty("authorizationToken").GetString();
                    _apiUrl = json.RootElement.GetProperty("apiInfo").GetProperty("storageApi").GetProperty("apiUrl").GetString();
                }
                else if (operation == "b2_get_upload_url")
                {
                    var upload = new Uri(json.RootElement.GetProperty("uploadUrl").GetString()!, UriKind.Absolute);
                    if (!ProviderUri(upload, allowUploadHost: true)) throw new InvalidDataException("Unexpected upload provider origin.");
                    _uploadUrls.Add(upload.AbsoluteUri);
                }
                if (creationKey is not null)
                {
                    var actualKey = json.RootElement.GetProperty("fileName").GetString();
                    if (actualKey != creationKey) throw new InvalidDataException("The creation acknowledgment changed the fixture filename.");
                    Scope!.Acknowledge(json.RootElement.GetProperty("fileId").GetString()!, creationKey);
                    // Append and flush acknowledgment metadata before returning success to the caller.
                    using var ledger = new FileStream(Path.Combine(directory, "owned-versions.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                    var record = JsonSerializer.SerializeToUtf8Bytes(new CloudPrefixAcceptanceScope.OwnedVersion(json.RootElement.GetProperty("fileId").GetString()!, creationKey));
                    ledger.Write(record); ledger.WriteByte((byte)'\n'); ledger.Flush(flushToDisk: true);
                    if (operation == "b2_hide_file" && _dropNextHideAcknowledgment)
                    {
                        _dropNextHideAcknowledgment = false;
                        DroppedHideAcknowledgment = Scope.RequireOwned(json.RootElement.GetProperty("fileId").GetString()!);
                        throw new HttpRequestException("Injected test-only loss of an acknowledged hide response.");
                    }
                    if (operation == "b2_copy_file" && _dropNextCopyAcknowledgment)
                    {
                        // The provider has committed and the test has a durable ownership receipt,
                        // but production receives neither response metadata nor a completion receipt.
                        // Keep the fault one-shot so reopened journals can exercise real recovery.
                        _dropNextCopyAcknowledgment = false;
                        DroppedCopyAcknowledgment = Scope.RequireOwned(json.RootElement.GetProperty("fileId").GetString()!);
                        throw new HttpRequestException("Injected test-only loss of an acknowledged copy response.");
                    }
                }
                if (deletedFileId is not null)
                {
                    if (json.RootElement.GetProperty("fileId").GetString() != deletedFileId ||
                        json.RootElement.GetProperty("fileName").GetString() != deletedKey)
                        throw new InvalidDataException("The deletion acknowledgment changed the exact fixture identity.");
                    Scope!.RequireDelete(deletedFileId, deletedKey!);
                    var acknowledged = new CloudPrefixAcceptanceScope.OwnedVersion(deletedFileId, deletedKey!);
                    using (var ledger = new FileStream(Path.Combine(directory, "deleted-versions.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        var record = JsonSerializer.SerializeToUtf8Bytes(acknowledged);
                        ledger.Write(record); ledger.WriteByte((byte)'\n'); ledger.Flush(flushToDisk: true);
                    }
                    Scope.AcknowledgeDeletion(deletedFileId, deletedKey!);
                    if (_dropNextDeleteAcknowledgment)
                    {
                        _dropNextDeleteAcknowledgment = false;
                        DroppedDeleteAcknowledgment = acknowledged;
                        throw new HttpRequestException("Injected test-only loss of an acknowledged delete response.");
                    }
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        finally { body?.Dispose(); }
    }

    private static bool ProviderUri(Uri uri, bool allowUploadHost) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host.EndsWith(".backblazeb2.com", StringComparison.OrdinalIgnoreCase) ||
            allowUploadHost && uri.Host.EndsWith(".backblaze.com", StringComparison.OrdinalIgnoreCase));
}

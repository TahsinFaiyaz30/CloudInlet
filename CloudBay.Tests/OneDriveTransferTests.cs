using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.OneDrive;
using CloudBay.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class OneDriveTransferTests
{
    private const string ClientId = "00000000-0000-0000-0000-000000000001";
    private static readonly TransferLocation Location = new("onedrive", "account", "drive", "root", "", "OneDrive");

    [TestMethod]
    public async Task ConcurrentFilesReuseCachedAuthorizationAndRefreshOnlyOnce()
    {
        var refreshes = 0;
        var saved = 0;
        var handler = new DelegateHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref refreshes);
            await Task.Delay(40, ct);
            return Json(new { access_token = "refreshed", refresh_token = "rotated", expires_in = 3600, scope = "Files.ReadWrite" });
        });
        using var http = new HttpClient(handler);
        var auth = new OneDriveAuthClient(ClientId, tokens: new("expired", "refresh", DateTimeOffset.UtcNow.AddHours(-1), "Files.ReadWrite"),
            persist: (tokens, ct) => { Assert.AreEqual("rotated", tokens.RefreshToken); Interlocked.Increment(ref saved); return Task.CompletedTask; }, http: http);
        var values = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => auth.GetAccessTokenAsync()));
        Assert.AreEqual(1, refreshes);
        Assert.AreEqual(1, saved);
        Assert.IsTrue(values.All(value => value == "refreshed"));
        auth.InvalidateAccessToken("expired");
        Assert.AreEqual("refreshed", await auth.GetAccessTokenAsync());
        Assert.AreEqual(1, refreshes, "A stale worker must not invalidate another worker's refreshed token.");
    }

    [TestMethod]
    public async Task PkceStateMismatchNeverRedeemsCode()
    {
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) => { requests++; return Task.FromResult(Json(new { })); }));
        var auth = new OneDriveAuthClient(ClientId, http: http);
        var request = auth.CreateAuthorizationRequest(new("http://localhost:5000/callback/"));
        Assert.IsTrue(request.AuthorizationUri.Query.Contains("code_challenge_method=S256"));
        Assert.IsFalse(request.AuthorizationUri.Query.Contains(request.CodeVerifier));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => auth.RedeemAuthorizationCodeAsync(request, "code", "different-state"));
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task PublicClientRegistrationFailureGivesActionableDiagnosticsWithoutEchoingAccountOrTokenDetails()
    {
        using var http = new HttpClient(new DelegateHandler((request, ct) => Task.FromResult(Json(new
        {
            error = "invalid_client", error_codes = new[] { 7000218 },
            error_description = "account-private@example.test SECRET_TOKEN_VALUE"
        }, HttpStatusCode.BadRequest))));
        var auth = new OneDriveAuthClient(ClientId, http: http);
        var error = await Assert.ThrowsExceptionAsync<OneDriveApiException>(() => auth.BeginDeviceSignInAsync());
        StringAssert.Contains(error.Message, "AADSTS7000218");
        StringAssert.Contains(error.Message, "Allow public client flows");
        Assert.IsFalse(error.Message.Contains("account-private"));
        Assert.IsFalse(error.Message.Contains("SECRET_TOKEN_VALUE"));
    }

    [TestMethod]
    public async Task DrivePickerStartsWithPrimaryOneDriveAndFiltersInvalidAdditionalEntriesAcrossPages()
    {
        var requests = new List<string>();
        object Drive(string id, string name) => new { id, name, owner = new { user = new { displayName = "Test account" } } };
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path + request.RequestUri.Query);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            if (path == "/v1.0/me/drive") return Task.FromResult(Json(Drive("personal", "Primary OneDrive")));
            if (path == "/v1.0/me/drives")
            {
                if (request.RequestUri.Query.Contains("page=2"))
                    return Task.FromResult(Json(new { value = new[] { Drive("phantom", "ObjectHandle"), Drive("personal", "OneDrive"),
                        Drive("additional", "Additional library"), Drive("forbidden", "Unavailable"), Drive("deleted", "Removed") } }));
                return Task.FromResult(Json(new Dictionary<string, object>
                {
                    ["value"] = new[] { Drive("phantom", "ObjectHandle"), Drive("personal", "OneDrive") },
                    ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/me/drives?page=2"
                }));
            }
            if (path == "/v1.0/drives/additional") return Task.FromResult(Json(Drive("additional", "Verified additional library")));
            var status = path == "/v1.0/drives/forbidden" ? HttpStatusCode.Forbidden
                : path == "/v1.0/drives/deleted" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest;
            return Task.FromResult(Json(new { error = new { code = "invalidRequest" } }, status));
        }));
        var drives = await Client(http).ListDrivesAsync();
        CollectionAssert.AreEqual(new[] { "personal", "additional" }, drives.Select(drive => drive.Id).ToArray());
        Assert.AreEqual("Primary OneDrive", drives[0].Name);
        Assert.AreEqual("Verified additional library", drives[1].Name);
        Assert.AreEqual("/v1.0/me/drive?$select=id,name,owner", requests[0]);
        Assert.AreEqual(1, requests.Count(path => path.StartsWith("/v1.0/drives/phantom?")));
        Assert.AreEqual(1, requests.Count(path => path.StartsWith("/v1.0/drives/additional?")));
        Assert.AreEqual(0, requests.Count(path => path.StartsWith("/v1.0/drives/personal?")));
        Assert.AreEqual(2, requests.Count(path => path.StartsWith("/v1.0/me/drives?")));
    }

    [TestMethod]
    public async Task DrivePickerDoesNotHideUnexpectedAdditionalDriveFailures()
    {
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me/drive") return Task.FromResult(Json(new { id = "personal", name = "OneDrive" }));
            if (path == "/v1.0/me/drives") return Task.FromResult(Json(new { value = new[] { new { id = "additional" } } }));
            return Task.FromResult(Json(new { error = new { code = "unsupportedQuery" } }, HttpStatusCode.BadRequest));
        }));
        var error = await Assert.ThrowsExceptionAsync<OneDriveApiException>(() => Client(http).ListDrivesAsync());
        Assert.AreEqual(400, error.StatusCode);
        Assert.AreEqual("unsupportedQuery", error.Code);
    }

    [TestMethod]
    public async Task SavedOneDrivePagesContinueAtCurrentFolderWithoutRescanningRoot()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath + request.RequestUri.Query);
            object payload = request.RequestUri.Query.Contains("page=2")
                ? new { value = new[] { Item("file", "second.txt", 3, "v1", false) } }
                : request.RequestUri.AbsolutePath.EndsWith("/folder/children")
                    ? new { value = new[] { Item("nested", "nested.txt", 7, "v1", false) } }
                    : new Dictionary<string, object> { ["value"] = new[] { Item("folder", "Folder", 0, "v1", true) },
                        ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/drives/drive/items/root/children?page=2" };
            return Task.FromResult(Json(payload));
        }));
        var client = Client(http);
        var first = await new OneDriveTransferEndpoint(client, Location).DiscoverAsync();
        Assert.IsTrue(first.Entries.Single().IsFolder);
        var second = await new OneDriveTransferEndpoint(client, Location).DiscoverAsync(first.NextCursor);
        Assert.AreEqual("second.txt", second.Entries.Single().RelativePath);
        var third = await new OneDriveTransferEndpoint(client, Location).DiscoverAsync(second.NextCursor);
        Assert.AreEqual("Folder/nested.txt", third.Entries.Single().RelativePath);
        Assert.IsNull(third.NextCursor);
        Assert.AreEqual(3, paths.Count);
        Assert.AreEqual(1, paths.Count(path => path.Contains("root/children") && !path.Contains("page=2")));
    }

    [TestMethod]
    public async Task ExcludedOneDriveFoldersAreNotTraversedOrCounted()
    {
        var requested = new List<string>();
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            requested.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new { value = new[] { Item("excluded", "Ignore", 0, "v1", true), Item("kept", "keep.txt", 3, "v1", false) } }));
        }));
        var page = await new OneDriveTransferEndpoint(Client(http), Location).DiscoverAsync(null, new[] { "Ignore" });
        Assert.IsNull(page.NextCursor);
        Assert.AreEqual("keep.txt", page.Entries.Single().RelativePath);
        Assert.AreEqual(1, requested.Count);
    }

    [TestMethod]
    public async Task SeparateConnectedAccountsShareTheClientBandwidthBudget()
    {
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            return Json(Item("uploaded", "file.bin", 64 * 1024, "version", false));
        }));
        var budget = new TransferBandwidthBudget();
        OneDriveClient Account(string token) => new(new(ClientId, tokens: new(token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"), http: http), http, budget);
        var first = Account("first-account"); var second = Account("second-account");
        first.Configure(128 * 1024, 0, 2, 1);
        second.Configure(128 * 1024, 0, 2, 1);
        var timer = Stopwatch.StartNew();
        await Task.WhenAll(first.PutSmallAsync("drive", "root", "first.bin", new byte[64 * 1024], "fail", null),
            second.PutSmallAsync("drive", "root", "second.bin", new byte[64 * 1024], "fail", null));
        Assert.IsTrue(timer.Elapsed >= TimeSpan.FromMilliseconds(850), "The two accounts must share 128 KiB/s rather than each receiving that rate.");
    }

    [TestMethod]
    public async Task RangeReadsTargetPreauthenticatedUrlAndRejectIgnoredPartialRanges()
    {
        var requestCount = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            requestCount++;
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                return Task.FromResult(Json(Item("item", "test.txt", 8, "etag", false, downloadUrl: "https://download.example.test/item")));
            }
            Assert.IsNull(request.Headers.Authorization);
            Assert.AreEqual("bytes=2-4", request.Headers.Range?.ToString());
            Assert.IsFalse(request.Headers.Contains("If-Match"), "Graph does not promise that its eTag is accepted by a signed CDN URL.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("12345678"u8.ToArray()) });
        }));
        var client = Client(http);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.OpenReadAsync("drive", new("item", "test.txt", 8, "etag", null, false, DateTimeOffset.UnixEpoch), 2, 3));
        Assert.AreEqual(2, requestCount);
    }

    [TestMethod]
    public async Task PersonalOneDriveRangeReadsGetDownloadUrlInOneFullMetadataRequest()
    {
        var metadataRequests = 0;
        var payloadRequests = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                metadataRequests++;
                Assert.AreEqual("/v1.0/drives/drive/items/item", request.RequestUri.AbsolutePath);
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                // Matches the observed personal-account behavior: a selected
                // item omits this annotation even when it is in the selection.
                var selected = Uri.UnescapeDataString(request.RequestUri.Query).Contains("$select=", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(Json(Item("item", "test.txt", 8, "saved-etag", false,
                    downloadUrl: selected ? null : "https://my.microsoftpersonalcontent.com/signed/item")));
            }
            payloadRequests++;
            Assert.AreEqual("my.microsoftpersonalcontent.com", request.RequestUri.Host);
            Assert.IsNull(request.Headers.Authorization, "Microsoft Graph bearer tokens must not reach the signed content host.");
            Assert.AreEqual("bytes=2-4", request.Headers.Range?.ToString());
            Assert.IsFalse(request.Headers.Contains("If-Match"));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent("345"u8.ToArray()) };
            response.Content.Headers.ContentRange = new(2, 4, 8);
            return Task.FromResult(response);
        }));
        await using var stream = await Client(http).OpenReadAsync("drive",
            new("item", "test.txt", 8, "saved-etag", null, false, DateTimeOffset.UnixEpoch), 2, 3);
        var buffer = new byte[64];
        Assert.AreEqual(3, await stream.ReadAsync(buffer));
        CollectionAssert.AreEqual("345"u8.ToArray(), buffer[..3]);
        Assert.AreEqual(0, await stream.ReadAsync(buffer), "A range stream must finish at its requested length.");
        Assert.AreEqual(1, metadataRequests, "Avoid a selected metadata request followed by another request for the download URL.");
        Assert.AreEqual(1, payloadRequests);
    }

    [DataTestMethod]
    [DataRow(8L, "changed-etag")]
    [DataRow(9L, "saved-etag")]
    public async Task RangeReadsRejectChangedSourceBeforeDownloadingPayload(long currentSize, string currentETag)
    {
        var payloadReads = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            if (request.RequestUri!.Host != "graph.microsoft.com") payloadReads++;
            return Task.FromResult(Json(Item("item", "test.txt", currentSize, currentETag, false, downloadUrl: "https://download.example.test/item")));
        }));
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => Client(http).OpenReadAsync("drive",
            new("item", "test.txt", 8, "saved-etag", null, false, DateTimeOffset.UnixEpoch), 0, 8));
        Assert.AreEqual(0, payloadReads);
    }

    [DataTestMethod]
    [DataRow("fail", null)]
    [DataRow("replace", "destination-version")]
    [DataRow("rename", null)]
    public async Task PersonalUploadSessionUsesPathNameAndPreservesConflictAndVersionGuards(string conflictBehavior, string? replaceETag)
    {
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            requests++;
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("/v1.0/drives/drive/items/parent:/Nested Folder/large file.bin:/createUploadSession",
                Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            Assert.AreEqual(replaceETag, request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var item = json.RootElement.GetProperty("item");
            Assert.AreEqual(conflictBehavior, item.GetProperty("@microsoft.graph.conflictBehavior").GetString());
            // Matches the live personal-account response to a redundant name.
            if (item.TryGetProperty("name", out _))
                return Json(new { error = new { code = "invalidRequest" } }, HttpStatusCode.BadRequest);
            return Json(new { uploadUrl = "https://upload.example.test/session", expirationDateTime = DateTimeOffset.UtcNow.AddHours(1),
                nextExpectedRanges = new[] { "0-" } });
        }));
        var session = await Client(http).CreateUploadSessionAsync("drive", "parent", "Nested Folder/large file.bin", conflictBehavior, replaceETag);
        Assert.AreEqual("https://upload.example.test/session", session.UploadUrl);
        Assert.AreEqual(0L, session.NextOffset);
        Assert.AreEqual(1, requests, "A compatible session request must succeed without a fallback round trip.");
    }

    [TestMethod]
    public async Task RestartContinuesAtProviderAcknowledgedFragmentAndDoesNotResendCompletedBytes()
    {
        using var provider = new UploadProvider();
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var bytes = new byte[OneDriveTransferEndpoint.FragmentBytes + 123];
        RandomNumberGenerator.Fill(bytes);
        var source = new MemorySource(bytes);
        TransferCheckpoint? saved = null;
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => endpoint.UploadAsync(request, source, null, (checkpoint, ct) =>
        {
            saved = checkpoint;
            if (checkpoint.AcknowledgedBytes > 0) throw new OperationCanceledException();
            return Task.CompletedTask;
        }));
        Assert.AreEqual(OneDriveTransferEndpoint.FragmentBytes, saved!.AcknowledgedBytes);
        var reopenedSource = new MemorySource(bytes);
        var receipt = await new OneDriveTransferEndpoint(Client(provider.Http), Location).UploadAsync(request, reopenedSource, saved,
            (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        await endpoint.VerifyAsync(receipt, reopenedSource);
        Assert.AreEqual(2, provider.UploadOffsets.Count);
        Assert.AreEqual(0L, provider.UploadOffsets[0]);
        Assert.AreEqual((long)OneDriveTransferEndpoint.FragmentBytes, provider.UploadOffsets[1]);
        Assert.AreEqual(1, provider.SessionsCreated);
        Assert.AreEqual(bytes.LongLength, saved!.AcknowledgedBytes);
        Assert.AreEqual("destination", saved.Data!["itemId"]);
        Assert.AreEqual((long)OneDriveTransferEndpoint.FragmentBytes, reopenedSource.Reads[0].Offset);
    }

    [TestMethod]
    public async Task LostFinalResponseReconcilesContentWithoutCreatingDuplicateUpload()
    {
        using var provider = new UploadProvider { LoseFinalResponse = true };
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var source = new MemorySource("transfer data"u8.ToArray());
        TransferCheckpoint? saved = null;
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        var receipt = await endpoint.UploadAsync(request, source, null, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        await endpoint.VerifyAsync(receipt, source);
        var recovered = await new OneDriveTransferEndpoint(Client(provider.Http), Location).ReconcileAsync(request, source, saved);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(receipt.Id, recovered.Id);
        Assert.AreEqual(0, provider.SessionsCreated);
        Assert.AreEqual(1, provider.UploadOffsets.Count);
    }

    [TestMethod]
    public async Task TinyFilesUseOnePayloadRequestAndReceiptKeepsStreamedHashForMove()
    {
        using var provider = new UploadProvider { OmitMetadataHash = true };
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var bytes = "tiny file content"u8.ToArray();
        var source = new MemorySource(bytes, omitSha1: true);
        TransferCheckpoint? saved = null;
        var receipt = await endpoint.UploadAsync(new("operation", "file.bin", TransferConflictPolicy.Fail), source, null,
            (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        Assert.AreEqual(Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), receipt.Sha1);
        Assert.AreEqual(receipt.Sha1, saved!.Data!["uploadedSha1"]);
        await endpoint.VerifyAsync(receipt, source);
        Assert.AreEqual(0, provider.SessionsCreated);
        Assert.AreEqual(1, provider.UploadOffsets.Count);
        Assert.AreEqual(1, source.Reads.Count, "Independent destination verification can reuse the saved upload digest without rereading the source payload.");
        Assert.AreEqual(1, provider.Downloads);
    }

    [TestMethod]
    public async Task LargeFileKeepsOneContinuousSourceRangeAcrossSequentialFragments()
    {
        using var provider = new UploadProvider();
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var bytes = new byte[OneDriveTransferEndpoint.FragmentBytes * 2 + 1];
        var source = new MemorySource(bytes);
        var receipt = await endpoint.UploadAsync(new("operation", "file.bin", TransferConflictPolicy.Fail), source, null,
            (checkpoint, ct) => Task.CompletedTask);
        await endpoint.VerifyAsync(receipt, source);
        Assert.AreEqual(3, provider.UploadOffsets.Count);
        Assert.AreEqual(1, source.Reads.Count, "Each fragment must not trigger a new source authorization, metadata fetch and download connection.");
        Assert.AreEqual(bytes.LongLength, source.Reads.Single().Length);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual("replace", provider.SessionConflictBehavior);
        Assert.AreEqual("reservation-etag", provider.SessionIfMatch);
    }

    [TestMethod]
    public async Task LargePersonalUploadResumesItsAcknowledgedEmptyReservationWithoutCreatingAnotherTarget()
    {
        using var provider = new UploadProvider();
        var bytes = new byte[OneDriveTransferEndpoint.FragmentBytes + 3];
        var source = new MemorySource(bytes);
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new OneDriveTransferEndpoint(Client(provider.Http), Location)
            .UploadAsync(request, source, null, (checkpoint, ct) =>
            {
                saved = checkpoint;
                if (checkpoint.AcknowledgedBytes > 0) throw new OperationCanceledException();
                return Task.CompletedTask;
            }));
        Assert.AreEqual("owned", saved!.Data!["reservationState"]);
        Assert.AreEqual("destination", saved.Data["beforeId"]);
        Assert.AreEqual("reservation-etag", saved.Data["beforeVersion"]);
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        Assert.IsNull(await endpoint.ReconcileAsync(request, source, saved));
        var resumedSource = new MemorySource(bytes);
        var receipt = await endpoint.UploadAsync(request, resumedSource, saved, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        await endpoint.VerifyAsync(receipt, resumedSource);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(1, provider.SessionsCreated);
        CollectionAssert.AreEqual(new[] { 0L, (long)OneDriveTransferEndpoint.FragmentBytes }, provider.UploadOffsets.ToArray());
        Assert.AreEqual((long)OneDriveTransferEndpoint.FragmentBytes, resumedSource.Reads[0].Offset);
    }

    [TestMethod]
    public async Task LostEmptyReservationAcknowledgmentRetainsUncertaintyWithoutDuplicateOrPayloadUpload()
    {
        using var provider = new UploadProvider { LoseReservationResponse = true };
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 1]);
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => new OneDriveTransferEndpoint(Client(provider.Http), Location)
            .UploadAsync(request, source, null, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; }));
        Assert.AreEqual("creating", saved!.Data!["reservationState"]);
        var recovered = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => recovered.ReconcileAsync(request, source, saved));
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => recovered.UploadAsync(request, source, saved,
            (checkpoint, ct) => Task.CompletedTask));
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(0, provider.SessionsCreated);
        Assert.AreEqual(0, provider.UploadOffsets.Count);
        Assert.AreEqual(0, source.Reads.Count);
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task CancellationDuringReservationAcknowledgmentSaveRetainsItsExactOwnedReceipt()
    {
        using var provider = new UploadProvider();
        using var cancellation = new CancellationTokenSource();
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 1]);
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        try
        {
            await new OneDriveTransferEndpoint(Client(provider.Http), Location).UploadAsync(request, source, null, (checkpoint, ct) =>
            {
                if (checkpoint.Data?.GetValueOrDefault("reservationState") == "owned") cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                saved = checkpoint;
                return Task.CompletedTask;
            }, cancellationToken: cancellation.Token);
            Assert.Fail("Cancellation should stop before session creation or source payload reads.");
        }
        catch (OperationCanceledException) { }
        Assert.AreEqual("owned", saved!.Data!["reservationState"]);
        Assert.AreEqual("destination", saved.Data["beforeId"]);
        Assert.AreEqual("reservation-etag", saved.Data["beforeVersion"]);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(0, provider.SessionsCreated);
        Assert.AreEqual(0, source.Reads.Count);
        var resumed = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var receipt = await resumed.UploadAsync(request, source, saved, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        await resumed.VerifyAsync(receipt, source);
        Assert.AreEqual(1, provider.ReservationsCreated, "Recovery must reuse the acknowledged empty destination after cancellation.");
        Assert.AreEqual(1, provider.SessionsCreated);
    }

    [DataTestMethod]
    [DataRow("changed-reservation-version", null)]
    [DataRow(null, "unrelated-item-at-same-path")]
    public async Task ChangedEmptyReservationIsNotAdoptedOrOverwrittenOnResume(string? changedVersion, string? changedId)
    {
        using var provider = new UploadProvider();
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 1]);
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new OneDriveTransferEndpoint(Client(provider.Http), Location)
            .UploadAsync(request, source, null, (checkpoint, ct) =>
            {
                saved = checkpoint;
                if (checkpoint.AcknowledgedBytes > 0) throw new OperationCanceledException();
                return Task.CompletedTask;
            }));
        provider.OverrideETag = changedVersion;
        provider.PathLookupId = changedId;
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => new OneDriveTransferEndpoint(Client(provider.Http), Location)
            .UploadAsync(request, source, saved, (checkpoint, ct) => Task.CompletedTask));
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(1, provider.SessionsCreated);
        Assert.AreEqual(1, provider.UploadOffsets.Count);
        Assert.AreEqual(0, provider.Deletes);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ExpiredSessionRestartsOnlyUnfinishedFile(bool retainedExpiredRecord, bool providerRemovedReservation)
    {
        using var provider = new UploadProvider();
        var bytes = new byte[OneDriveTransferEndpoint.FragmentBytes + 3];
        var source = new MemorySource(bytes);
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => endpoint.UploadAsync(request, source, null, (checkpoint, ct) =>
        {
            saved = checkpoint;
            if (checkpoint.AcknowledgedBytes > 0) throw new OperationCanceledException();
            return Task.CompletedTask;
        }));
        provider.ExpireSession(retainedExpiredRecord, providerRemovedReservation);
        var resumedSource = new MemorySource(bytes);
        var receipt = await new OneDriveTransferEndpoint(Client(provider.Http), Location).UploadAsync(request, resumedSource, saved,
            (checkpoint, ct) => Task.CompletedTask);
        await endpoint.VerifyAsync(receipt, resumedSource);
        Assert.AreEqual(2, provider.SessionsCreated);
        Assert.AreEqual(0L, provider.UploadOffsets[1]);
        Assert.AreEqual(0L, resumedSource.Reads.Single().Offset, "An HTTP200 expired session record must never cause a source read at its obsolete acknowledged offset.");
        Assert.AreEqual(providerRemovedReservation ? 2 : 1, provider.ReservationsCreated);
        Assert.AreEqual(bytes.LongLength, receipt.Size);
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.Gone)]
    public async Task SessionExpiryDuringFinalFragmentRestartsOnlyThatFileWithItsExactReservation(HttpStatusCode status)
    {
        using var provider = new UploadProvider { FragmentExpiryStatus = status };
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 3]);
        TransferCheckpoint? saved = null;
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var receipt = await endpoint.UploadAsync(new("operation", "file.bin", TransferConflictPolicy.Fail), source, null,
            (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        await endpoint.VerifyAsync(receipt, source);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(2, provider.SessionsCreated);
        CollectionAssert.AreEqual(new[] { 0L, (long)OneDriveTransferEndpoint.FragmentBytes, 0L, (long)OneDriveTransferEndpoint.FragmentBytes }, provider.UploadOffsets.ToArray());
        Assert.AreEqual(2, source.Reads.Count);
        Assert.IsTrue(source.Reads.All(read => read.Offset == 0));
        Assert.AreEqual(source.Entry.Size, saved!.AcknowledgedBytes);
        Assert.AreEqual("destination", saved.Data!["beforeId"]);
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task MissingSessionAfterFinalCommitReconcilesContentWithoutRestartOrDuplicate()
    {
        using var provider = new UploadProvider { MissingFinalResponseStatus = HttpStatusCode.NotFound };
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 3]);
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var receipt = await endpoint.UploadAsync(new("operation", "file.bin", TransferConflictPolicy.Fail), source, null,
            (checkpoint, ct) => Task.CompletedTask);
        await endpoint.VerifyAsync(receipt, source);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(1, provider.SessionsCreated);
        Assert.AreEqual(2, provider.UploadOffsets.Count);
        Assert.AreEqual(source.Entry.Size, receipt.Size);
        Assert.AreEqual(1, source.Reads.Count);
    }

    [TestMethod]
    public async Task ChangedReservationAfterSessionExpiryStopsBeforeAnotherSessionOrPayload()
    {
        using var provider = new UploadProvider { FragmentExpiryStatus = HttpStatusCode.Gone, ChangeReservationOnExpiry = true };
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 3]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => new OneDriveTransferEndpoint(Client(provider.Http), Location)
            .UploadAsync(new("operation", "file.bin", TransferConflictPolicy.Fail), source, null, (checkpoint, ct) => Task.CompletedTask));
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(1, provider.SessionsCreated);
        Assert.AreEqual(2, provider.UploadOffsets.Count);
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task RepeatedFragmentExpiryStopsBoundedlyWithRecoverableOwnedIntent()
    {
        using var provider = new UploadProvider { FragmentExpiryStatus = HttpStatusCode.NotFound, RepeatFragmentExpiry = true };
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 3]);
        TransferCheckpoint? saved = null;
        var error = await Assert.ThrowsExceptionAsync<IOException>(() => new OneDriveTransferEndpoint(Client(provider.Http), Location)
            .UploadAsync(new("operation", "file.bin", TransferConflictPolicy.Fail), source, null,
                (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; }));
        StringAssert.Contains(error.Message, "repeatedly expired");
        Assert.AreEqual(3, provider.SessionsCreated);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual("", saved!.SessionId);
        Assert.AreEqual(0L, saved.AcknowledgedBytes);
        Assert.AreEqual("owned", saved.Data!["reservationState"]);
        Assert.AreEqual("destination", saved.Data["beforeId"]);
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task MatchingSizeCannotReconcileWrongContentOrDeleteSource()
    {
        using var provider = new UploadProvider();
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var source = new MemorySource("right data"u8.ToArray());
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        var receipt = await endpoint.UploadAsync(request, source, null, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        provider.OverrideHash = new string('0', 40);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => endpoint.VerifyAsync(receipt, source));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => endpoint.ReconcileAsync(request, source, saved));
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task RecoveryPreservesAcknowledgedDestinationVersionInsteadOfAcceptingALaterVersion()
    {
        using var provider = new UploadProvider();
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var source = new MemorySource("content unchanged, version changed"u8.ToArray());
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        await endpoint.UploadAsync(request, source, null, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        provider.OverrideETag = "later-destination-version";
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => endpoint.ReconcileAsync(request, source, saved));
        Assert.AreEqual("destination-etag", saved!.Data!["itemVersion"]);
        Assert.AreEqual(1, provider.UploadOffsets.Count);
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task RecoveryRejectsRemovedOrMovedAcknowledgedDestinationWithoutRecreatingPayload()
    {
        using var provider = new UploadProvider();
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var source = new MemorySource("acknowledged content"u8.ToArray());
        var request = new TransferUploadRequest("operation", "file.bin", TransferConflictPolicy.Fail);
        TransferCheckpoint? saved = null;
        await endpoint.UploadAsync(request, source, null, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        provider.PathLookupId = "unrelated-at-saved-path";
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => endpoint.ReconcileAsync(request, source, saved));
        provider.ForgetCompleted();
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => endpoint.ReconcileAsync(request, source, saved));
        Assert.AreEqual(1, provider.UploadOffsets.Count);
        Assert.AreEqual(0, provider.Deletes);
    }

    [TestMethod]
    public async Task ReplaceOfAbsentLargeTargetBindsOwnedReservationAndRetainsSessionOnLateConflict()
    {
        using var provider = new UploadProvider { LateConflict = true };
        var endpoint = new OneDriveTransferEndpoint(Client(provider.Http), Location);
        var source = new MemorySource(new byte[OneDriveTransferEndpoint.FragmentBytes + 1]);
        TransferCheckpoint? saved = null;
        await Assert.ThrowsExceptionAsync<TransferConflictException>(() => endpoint.UploadAsync(
            new("operation", "file.bin", TransferConflictPolicy.Replace), source, null,
            (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; }));
        Assert.AreEqual("replace", provider.SessionConflictBehavior);
        Assert.AreEqual("reservation-etag", provider.SessionIfMatch);
        Assert.AreEqual(1, provider.ReservationsCreated);
        Assert.AreEqual(1, provider.UploadOffsets.Count, "The final fragment must not overwrite a newly appeared destination.");
        Assert.AreEqual(OneDriveTransferEndpoint.FragmentBytes, saved!.AcknowledgedBytes);
        Assert.IsFalse(string.IsNullOrEmpty(saved.SessionId));
        Assert.AreEqual(0, provider.Deletes);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RenameKeepsAbsentNamesAndRecoversOnlyItsExactReviewedTarget(bool destinationExists)
    {
        var operation = new string('a', 64);
        var expectedName = destinationExists ? "file (CloudBay aaaaaaaaaaaa).bin" : "file.bin";
        var bytes = "new contents"u8.ToArray();
        var originalBytes = "original contents"u8.ToArray();
        var files = new Dictionary<string, (string Id, string Version, byte[] Bytes)>();
        if (destinationExists) files["file.bin"] = ("original", "original-version", originalBytes);
        var payloadRequests = 0;
        var metadataRequests = 0;
        HttpResponseMessage Metadata(string name, (string Id, string Version, byte[] Bytes) file) => Json(Item(file.Id, name, file.Bytes.Length,
            file.Version, false, Convert.ToHexString(SHA1.HashData(file.Bytes)).ToLowerInvariant()));
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            const string prefix = "/v1.0/drives/drive/items/root:/";
            if (path.StartsWith(prefix))
            {
                var name = path[prefix.Length..path.IndexOf(":", prefix.Length, StringComparison.Ordinal)];
                if (request.Method == HttpMethod.Put)
                {
                    payloadRequests++;
                    Assert.AreEqual(expectedName, name);
                    Assert.IsFalse(files.ContainsKey(name), "Keep both must never overwrite the original or an unrelated deterministic name.");
                    files[name] = ("uploaded", "uploaded-version", await request.Content!.ReadAsByteArrayAsync(ct));
                    return Metadata(name, files[name]);
                }
                metadataRequests++;
                return files.TryGetValue(name, out var file) ? Metadata(name, file)
                    : Json(new { error = new { code = "itemNotFound" } }, HttpStatusCode.NotFound);
            }
            metadataRequests++;
            Assert.AreEqual("/v1.0/drives/drive/items/uploaded", path);
            return Metadata(expectedName, files[expectedName]);
        }));
        var source = new MemorySource(bytes);
        var request = new TransferUploadRequest(operation, "file.bin", TransferConflictPolicy.Rename);
        TransferCheckpoint? saved = null;
        var endpoint = new OneDriveTransferEndpoint(Client(http), Location);
        var receipt = await endpoint.UploadAsync(request, source, null, (checkpoint, ct) => { saved = checkpoint; return Task.CompletedTask; });
        Assert.AreEqual(expectedName, receipt.RelativePath);
        Assert.AreEqual(expectedName, saved!.Data!["target"]);
        var recovered = await new OneDriveTransferEndpoint(Client(http), Location).ReconcileAsync(request, source, saved);
        Assert.AreEqual(receipt, recovered);
        Assert.AreEqual(1, payloadRequests);
        if (destinationExists) CollectionAssert.AreEqual(originalBytes, files["file.bin"].Bytes);
        var beforeInvalid = metadataRequests;
        var invalidData = new Dictionary<string, string>(saved.Data) { ["target"] = "unreviewed.bin" };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => endpoint.ReconcileAsync(request, source,
            new(saved.Provider, saved.SessionId, saved.AcknowledgedBytes, invalidData)));
        Assert.AreEqual(beforeInvalid, metadataRequests, "Reject a changed target before contacting the provider.");
        Assert.AreEqual(1, payloadRequests);
    }

    [TestMethod]
    public async Task LegacyGraphRenameReceiptStillRecoversAtItsExactDeterministicPath()
    {
        const string operation = "operation";
        var source = new MemorySource("legacy contents"u8.ToArray());
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation)))[..10].ToLowerInvariant();
        var target = "file (CloudBay " + suffix + ").bin";
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            requests++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(Item("legacy-item", target, source.Entry.Size, "legacy-version", false, source.Entry.Sha1)));
        }));
        var checkpoint = new TransferCheckpoint("onedrive", "", source.Entry.Size, new Dictionary<string, string>
        {
            ["target"] = target, ["operation"] = operation, ["drive"] = "drive", ["folder"] = "root",
            ["sourceId"] = source.Entry.Id, ["sourceVersion"] = source.Entry.Version,
            ["sourceSize"] = source.Entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["itemId"] = "legacy-item", ["itemVersion"] = "legacy-version", ["uploadedSha1"] = source.Entry.Sha1!
        });
        var receipt = await new OneDriveTransferEndpoint(Client(http), Location).ReconcileAsync(
            new(operation, "file.bin", TransferConflictPolicy.Rename), source, checkpoint);
        Assert.AreEqual(target, receipt!.RelativePath);
        Assert.AreEqual("legacy-item", receipt.Id);
        Assert.AreEqual(4, requests);
    }

    [TestMethod]
    public async Task OneDriveUploadBandwidthCapIsSharedAcrossWorkersAndCanChangeDuringTransfer()
    {
        var active = 0;
        var peak = 0;
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            var concurrent = Interlocked.Increment(ref active);
            peak = Math.Max(peak, concurrent);
            try
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                return Json(Item("uploaded", "file.bin", 64 * 1024, "version", false));
            }
            finally { Interlocked.Decrement(ref active); }
        }));
        var client = Client(http);
        client.Configure(256 * 1024, 0, 2);
        var timer = Stopwatch.StartNew();
        await Task.WhenAll(client.PutSmallAsync("drive", "root", "first.bin", new byte[64 * 1024], "fail", null),
            client.PutSmallAsync("drive", "root", "second.bin", new byte[64 * 1024], "fail", null));
        Assert.IsTrue(timer.Elapsed >= TimeSpan.FromMilliseconds(350), "Two workers must share one aggregate 256 KiB/s budget.");
        Assert.AreEqual(2, peak);
        client.Configure(1, 0, 1);
        var throttled = client.PutSmallAsync("drive", "root", "third.bin", new byte[64 * 1024], "fail", null);
        await Task.Delay(25);
        client.Configure(0, 0, 1);
        await throttled.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task OneDriveDownloadConcurrencyLeaseLastsUntilTheReturnedStreamIsDisposed()
    {
        var payloadRequests = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            if (request.RequestUri!.Host == "graph.microsoft.com")
                return Task.FromResult(Json(Item("item", "file.bin", 3, "version", false, downloadUrl: "https://download.example.test/item")));
            Interlocked.Increment(ref payloadRequests);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentRange = new(0, 2, 3);
            return Task.FromResult(response);
        }));
        var client = Client(http);
        client.Configure(0, 0, 1);
        var item = new OneDriveItem("item", "file.bin", 3, "version", null, false, DateTimeOffset.UnixEpoch);
        var first = await client.OpenReadAsync("drive", item, 0, 3);
        var second = client.OpenReadAsync("drive", item, 0, 3);
        await Task.Delay(25);
        Assert.AreEqual(1, payloadRequests);
        await first.DisposeAsync();
        first.Dispose(); // Mixed disposal must not release the same lease twice.
        await using var resumed = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, payloadRequests);
        var bytes = new byte[3];
        await resumed.ReadExactlyAsync(bytes);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
    }

    [TestMethod]
    public async Task MoveDeleteChecksExactIdentityAndIfMatchAndRefusesChangedVersion()
    {
        var changed = false;
        var deletes = 0;
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/items/immutable-id"));
            if (request.Method == HttpMethod.Delete)
            {
                deletes++;
                Assert.AreEqual("saved", request.Headers.GetValues("If-Match").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(Json(Item("immutable-id", "renamed.txt", 4, changed ? "changed" : "saved", false)));
        }));
        var endpoint = new OneDriveTransferEndpoint(Client(http), Location);
        var entry = new TransferEntry("immutable-id", "old-name.txt", "saved", 4, DateTimeOffset.UnixEpoch);
        await endpoint.DeleteSourceAsync(entry);
        Assert.AreEqual(1, deletes);
        changed = true;
        await Assert.ThrowsExceptionAsync<TransferSourceChangedException>(() => endpoint.DeleteSourceAsync(entry));
        Assert.AreEqual(1, deletes);
    }

    [TestMethod]
    public async Task MoveRecoveryTreatsAbsentSavedIdentityAsDeletedWithoutDeletingPathReplacement()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(new { error = new { code = "itemNotFound" } }, HttpStatusCode.NotFound));
        }));
        var endpoint = new OneDriveTransferEndpoint(Client(http), Location);
        Assert.IsTrue(await endpoint.IsSourceDeletedAsync(new("old-item-id", "same-name.txt", "saved", 3, DateTimeOffset.UnixEpoch)));
        Assert.AreEqual("/v1.0/drives/drive/items/old-item-id", requests.Single());
    }

    private static OneDriveClient Client(HttpClient http) => new(new(ClientId,
        tokens: new("token", "refresh", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"), http: http), http);
    private static Dictionary<string, object?> Item(string id, string name, long size, string etag, bool folder,
        string? sha1 = null, string? downloadUrl = null) => new()
    {
        ["id"] = id, ["name"] = name, ["size"] = size, ["eTag"] = etag, ["lastModifiedDateTime"] = "2026-10-05T00:00:00Z",
        [folder ? "folder" : "file"] = folder ? new { } : new { hashes = new { sha1Hash = sha1 } },
        ["@microsoft.graph.downloadUrl"] = downloadUrl
    };
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class MemorySource(byte[] bytes, bool omitSha1 = false) : ITransferSourceFile
    {
        public TransferEntry Entry { get; } = new("source", "file.bin", "v1", bytes.LongLength, DateTimeOffset.UnixEpoch,
            omitSha1 ? null : Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
        public List<(long Offset, long Length)> Reads { get; } = [];
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            Reads.Add((offset, length));
            return Task.FromResult<Stream>(new MemoryStream(bytes, (int)offset, (int)length, writable: false));
        }
        public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class UploadProvider : IDisposable
    {
        public HttpClient Http { get; }
        public List<long> UploadOffsets { get; } = [];
        public int SessionsCreated { get; private set; }
        public int Deletes { get; private set; }
        public int Downloads { get; private set; }
        public bool LoseFinalResponse { get; init; }
        public bool LoseReservationResponse { get; init; }
        public bool OmitMetadataHash { get; init; }
        public HttpStatusCode? FragmentExpiryStatus { get; init; }
        public HttpStatusCode? MissingFinalResponseStatus { get; init; }
        public bool RepeatFragmentExpiry { get; init; }
        public bool ChangeReservationOnExpiry { get; init; }
        public string? OverrideHash { get; set; }
        public string? OverrideETag { get; set; }
        public string? PathLookupId { get; set; }
        public bool LateConflict { get; init; }
        public string? SessionConflictBehavior { get; private set; }
        public string? SessionIfMatch { get; private set; }
        public int ReservationsCreated { get; private set; }
        private MemoryStream _bytes = new();
        private bool _session;
        private bool _completed;
        private bool _reserved;
        private bool _fragmentExpiryTriggered;
        private bool _retainedExpiredRecord;
        private long _expiredRecordOffset;
        private string _sessionUrl = "";
        public UploadProvider() { Http = new(new DelegateHandler(SendAsync)); }
        public void ExpireSession(bool retainExpiredRecord = false, bool removeReservation = false)
        {
            _expiredRecordOffset = _bytes.Length;
            _retainedExpiredRecord = retainExpiredRecord;
            _session = false;
            _bytes.SetLength(0);
            if (removeReservation) _reserved = false;
        }
        public void ForgetCompleted() => _completed = false;
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            if (url.Host == "download.example.test")
            {
                Assert.IsNull(request.Headers.Authorization);
                Downloads++;
                var range = request.Headers.Range!.Ranges.Single();
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(_bytes.ToArray().AsSpan((int)range.From!.Value, (int)(range.To!.Value - range.From.Value + 1)).ToArray()) };
                response.Content.Headers.ContentRange = new(range.From.Value, range.To.Value, _bytes.Length);
                return response;
            }
            if (url.Host == "upload.example.test")
            {
                Assert.IsNull(request.Headers.Authorization);
                if (request.Method == HttpMethod.Get)
                    return _session ? Session() : _retainedExpiredRecord ? Json(new
                    {
                        expirationDateTime = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                        nextExpectedRanges = new[] { _expiredRecordOffset + "-" }
                    }) : Json(new { error = new { code = "itemNotFound" } }, HttpStatusCode.NotFound);
                var range = request.Content!.Headers.ContentRange!;
                UploadOffsets.Add(range.From!.Value);
                Assert.AreEqual(_bytes.Length, range.From);
                var payload = await request.Content.ReadAsByteArrayAsync(ct);
                if (range.From > 0 && FragmentExpiryStatus is { } expiredStatus && (!_fragmentExpiryTriggered || RepeatFragmentExpiry))
                {
                    _fragmentExpiryTriggered = true;
                    ExpireSession();
                    if (ChangeReservationOnExpiry) { OverrideETag = "changed-reservation"; PathLookupId = "unrelated-item"; }
                    return Json(new { error = new { code = "itemNotFound" } }, expiredStatus);
                }
                _bytes.Write(payload);
                if (_bytes.Length == range.Length)
                {
                    _completed = true;
                    _session = false;
                    if (LoseFinalResponse) throw new HttpRequestException("Connection was lost after commit.");
                    if (MissingFinalResponseStatus is { } missingStatus)
                        return Json(new { error = new { code = "itemNotFound" } }, missingStatus);
                    return Destination();
                }
                return Session(HttpStatusCode.Accepted);
            }
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Put && url.AbsolutePath.EndsWith("/content"))
            {
                Assert.IsTrue(url.Query.Contains("conflictBehavior=fail"));
                Assert.IsTrue(request.Headers.GetValues("If-None-Match").Contains("*"));
                _bytes.SetLength(0);
                await request.Content!.CopyToAsync(_bytes, ct);
                if (_bytes.Length == 0)
                {
                    ReservationsCreated++;
                    _reserved = true;
                    if (LoseReservationResponse) throw new HttpRequestException("Connection was lost after empty reservation creation.");
                    return Reservation();
                }
                UploadOffsets.Add(0);
                _completed = true;
                if (LoseFinalResponse) throw new HttpRequestException("Connection was lost after commit.");
                return Destination();
            }
            if (request.Method == HttpMethod.Post && url.AbsolutePath.EndsWith("/createUploadSession"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                SessionConflictBehavior = body.RootElement.GetProperty("item").GetProperty("@microsoft.graph.conflictBehavior").GetString();
                SessionIfMatch = request.Headers.TryGetValues("If-Match", out var match) ? match.Single() : null;
                if (_reserved)
                {
                    Assert.AreEqual("replace", SessionConflictBehavior);
                    Assert.AreEqual("reservation-etag", SessionIfMatch);
                }
                SessionsCreated++;
                _session = true;
                _retainedExpiredRecord = false;
                _bytes.SetLength(0);
                _sessionUrl = "https://upload.example.test/session" + SessionsCreated;
                return Session();
            }
            if (request.Method == HttpMethod.Delete) { Deletes++; return new(HttpStatusCode.NoContent); }
            if (LateConflict && !_completed && _bytes.Length > 0)
                return Json(Item("unrelated", "file.bin", 7, "later-file", false));
            return _completed ? Destination(url.AbsolutePath.Contains(":/") ? PathLookupId : null) : _reserved
                ? Reservation(url.AbsolutePath.Contains(":/") ? PathLookupId : null)
                : Json(new { error = new { code = "itemNotFound" } }, HttpStatusCode.NotFound);
        }
        private HttpResponseMessage Session(HttpStatusCode status = HttpStatusCode.OK) => Json(new
        {
            uploadUrl = _sessionUrl, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            nextExpectedRanges = new[] { _bytes.Length + "-" }
        }, status);
        private HttpResponseMessage Destination(string? overrideId = null) => Json(Item(overrideId ?? "destination", "file.bin", _bytes.Length, OverrideETag ?? "destination-etag", false,
            OmitMetadataHash ? null : OverrideHash ?? Convert.ToHexString(SHA1.HashData(_bytes.ToArray())).ToLowerInvariant(),
            "https://download.example.test/destination"));
        private HttpResponseMessage Reservation(string? overrideId = null) => Json(Item(overrideId ?? "destination", "file.bin", 0,
            OverrideETag ?? "reservation-etag", false, Convert.ToHexString(SHA1.HashData(Array.Empty<byte>())).ToLowerInvariant()));
        public void Dispose() { Http.Dispose(); _bytes.Dispose(); }
    }
}

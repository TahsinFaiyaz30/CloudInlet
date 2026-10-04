using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Core.Sync;
using CloudBay.Windows.CloudFiles;
using Windows.Storage.Provider;
using ActivityEvent = CloudBay.Core.ActivityEvent;
using ActivityKind = CloudBay.Core.ActivityKind;

if (args.Contains("--transfer-worker")) return await TransferAcceptance.RunWorkerAsync(args);
if (args.Contains("--repair-folder-icons")) return await FolderIconRepair.RunAsync();
return await Validation.RunAsync(args);

internal static class Validation
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string Workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Id = Guid.NewGuid().ToString("N");
    private static readonly string ReportPath = Path.Combine(Workspace, "artifacts", "validation", $"b2-{Id}.json");
    private static readonly List<Acceptance> Checks = [];
    private static readonly ConcurrentBag<string> Notes = [];
    private static string Prefix = "CloudBayValidation/" + Id + "/";
    private static string? BucketName;
    private static readonly Stopwatch Timer = Stopwatch.StartNew();

    public static async Task<int> RunAsync(string[] args)
    {
        var transport = new CountingHandler();
        using var store = new B2CloudStore(transport);
        store.Diagnostic += (_, note) => Notes.Add(note);
        store.Configure(0, 0, 4);
        var keys = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        CloudBucket? bucket = null;
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", Id);
        try
        {
            var protectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
            var plaintext = ProtectedData.Unprotect(await File.ReadAllBytesAsync(protectedPath), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            B2Credentials credentials;
            try { credentials = JsonSerializer.Deserialize<B2Credentials>(plaintext) ?? throw new InvalidDataException("Missing validation credentials."); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var ct = lifetime.Token;
            var account = await store.ConnectAsync(credentials, ct);
            var buckets = await store.ListBucketsAsync(ct);
            Console.WriteLine(JsonSerializer.Serialize(new { bucketNames = buckets.Select(b => b.Name).ToArray(), restrictedBucket = account.AllowedBucketId is not null, allowedPrefix = account.AllowedNamePrefix }, Json));
            if (args.Contains("--list")) return 0;
            if (!args.Contains("--b2")) throw new InvalidOperationException("Specify --list or --b2; add --native for Explorer integration.");
            if (buckets.Count != 1 || account.AllowedBucketId != buckets[0].Id)
                throw new InvalidOperationException("Live writes require an application key restricted to exactly one selected bucket.");
            bucket = buckets[0]; BucketName = bucket.Name;
            Prefix = (account.AllowedNamePrefix ?? "") + Prefix;
            var type = transport.BucketTypes.GetValueOrDefault(bucket.Id);
            if (args.Contains("--transfers") && type != "allPrivate")
                throw new InvalidOperationException("Fresh-process transfer checks require the restricted test bucket to be private.");
            if (type != "allPrivate")
            {
                var empty = true;
                await foreach (var _ in store.ListAsync(bucket.Id, account.AllowedNamePrefix ?? "", ct)) { empty = false; break; }
                if (!empty) throw new InvalidOperationException("The restricted test bucket is not empty and its privacy could not be verified.");
            }
            Directory.CreateDirectory(temp);
            if (args.Contains("--transfers"))
            {
                credentials = null!;
                await TransferAcceptance.RunAsync(store, bucket, Prefix, Id, temp, CheckAsync, ct);
            }
            else if (args.Contains("--controller"))
            {
                await ControllerAcceptance.RunAsync(store, bucket, credentials, Prefix, Id, temp, CheckAsync, ct);
            }
            else
            {
            credentials = null!;
            await CheckAsync("sequential_small_files_reuse_upload_session", async () =>
            {
                var before = transport.Count("b2_get_upload_url");
                for (var i = 0; i < 4; i++)
                    await UploadBytes(store, bucket.Id, keys, $"small/sequence-{i}.txt", System.Text.Encoding.UTF8.GetBytes($"CloudBay native validation {Id} {i}"), ct);
                Require(transport.Count("b2_get_upload_url") - before == 1, "Sequential small files allocated more than one reusable upload endpoint.");
            });
            await CheckAsync("concurrent_small_files_keep_sessions_exclusive", async () =>
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
                {
                    var bytes = new byte[16 * 1024 + i]; new Random(i).NextBytes(bytes);
                    var file = await UploadBytes(store, bucket.Id, keys, $"small/concurrent-{i}.bin", bytes, ct);
                    using var downloaded = new MemoryStream(); await store.DownloadAsync(file, downloaded, cancellationToken: ct);
                    Require(bytes.SequenceEqual(downloaded.ToArray()), "Small-file round trip changed the bytes.");
                }));
                Require(transport.ConcurrentTokenViolations == 0, "An upload token was used by two simultaneous requests.");
            });
            await CheckAsync("prefix_listing_paginates_without_loss", async () =>
            {
                store.ListPageSize = 3;
                var before = transport.Count("b2_list_file_versions");
                var files = await List(store, bucket.Id, ct);
                Require(files.Count(f => f.Action == "upload") == 12, "The scoped listing lost or duplicated test objects.");
                Require(files.All(f => f.Key.StartsWith(Prefix, StringComparison.Ordinal)), "Listing crossed the isolated prefix.");
                Require(transport.Count("b2_list_file_versions") - before >= 4, "Live listing did not cross page boundaries.");
                store.ListPageSize = 1_000;
            });
            await CheckAsync("current_name_listing_paginates_without_history", async () =>
            {
                var pageSize = store.ListPageSize;
                try
                {
                    store.ListPageSize = 3;
                    var before = transport.Count("b2_list_file_names");
                    var files = new List<CloudObject>();
                    await foreach (var file in store.ListCurrentAsync(bucket.Id, Prefix, ct)) files.Add(file);
                    Require(files.Count == 12 && files.All(f => f.Action == "upload"), "The current snapshot lost or duplicated uploaded test objects.");
                    Require(files.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() == 12, "The current snapshot repeated a name.");
                    Require(files.All(f => f.Key.StartsWith(Prefix, StringComparison.Ordinal)), "Current listing crossed the isolated prefix.");
                    Require(transport.Count("b2_list_file_names") - before >= 4, "Live current-name listing did not cross page boundaries.");
                }
                finally { store.ListPageSize = pageSize; }
            });
            await CheckAsync("edit_version_restore_and_hide", async () =>
            {
                var first = await UploadBytes(store, bucket.Id, keys, "versions/edit.txt", "original version"u8.ToArray(), ct);
                await UploadBytes(store, bucket.Id, keys, "versions/edit.txt", "edited version"u8.ToArray(), ct);
                var versions = await store.VersionsAsync(bucket.Id, first.Key, ct);
                Require(versions.Count(v => v.Action == "upload") == 2, "Two edits did not create two retained B2 versions.");
                var restored = await store.RestoreAsync(bucket.Id, first, ct);
                Require(restored.FileId != first.FileId, "Restore did not create a new current version.");
                using var output = new MemoryStream(); await store.DownloadAsync(restored, output, cancellationToken: ct);
                Require(output.ToArray().SequenceEqual("original version"u8.ToArray()), "Restored content differs from the selected version.");
                await store.HideAsync(bucket.Id, first.Key, ct);
                versions = await store.VersionsAsync(bucket.Id, first.Key, ct);
                Require(versions[0].Action == "hide" && versions.Count(v => v.Action == "upload") == 3, "Hide did not preserve restorable history.");
                await foreach (var file in store.ListCurrentAsync(bucket.Id, Prefix, ct))
                    Require(file.Key != first.Key, "A hidden file still appeared in the current-name snapshot.");
            });
            await CheckAsync("empty_directory_marker_upload_list_and_hide", async () =>
            {
                var marker = await UploadBytes(store, bucket.Id, keys, "empty-folder/", [], ct);
                Require(marker.Key.EndsWith('/') && marker.Size == 0 && marker.Sha1 == "da39a3ee5e6b4b0d3255bfef95601890afd80709", "B2 changed the empty-directory marker.");
                Require((await List(store, bucket.Id, ct)).Any(f => f.Key == marker.Key && f.Action == "upload"), "B2 listing omitted the empty-directory marker.");
                await store.HideAsync(bucket.Id, marker.Key, ct);
                Require((await store.VersionsAsync(bucket.Id, marker.Key, ct))[0].Action == "hide", "Directory marker hide did not preserve the B2 version.");
            });
            if (!args.Contains("--quick"))
            {
            var largePath = Path.Combine(temp, "multipart.bin");
            string largeSha = "";
            const long largeSize = 205L * 1024 * 1024;
            await CheckAsync("multipart_stream_upload_and_verified_download", async () =>
            {
                largeSha = await GenerateFile(largePath, largeSize, ct);
                var key = Prefix + "large/multipart.bin"; keys[key] = true;
                await using var input = new FileStream(largePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                var file = await store.UploadAsync(bucket.Id, key, input, largeSize, largeSha, DateTimeOffset.UtcNow,
                    new ConsoleProgress("large upload"), ct);
                Require(file.Size == largeSize && file.Sha1 == largeSha, "Multipart metadata did not preserve the whole-file SHA1.");
                await using var output = new FileStream(Path.Combine(temp, "downloaded.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                await store.DownloadAsync(file, output, progress: new ConsoleProgress("large download"), cancellationToken: ct);
                output.Position = 0;
                Require(Convert.ToHexString(await SHA1.HashDataAsync(output, ct)).Equals(largeSha, StringComparison.OrdinalIgnoreCase), "Large-file download SHA1 mismatch.");
                using var range = new MemoryStream(); await store.DownloadAsync(file, range, 4096, 16384, cancellationToken: ct);
                input.Position = 4096; var expected = new byte[16384]; await input.ReadExactlyAsync(expected, ct);
                Require(range.ToArray().SequenceEqual(expected), "The large-file range returned incorrect bytes.");
            });
            await CheckAsync("multipart_cancellation_removes_unfinished_parts", async () =>
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var before = transport.SuccessCount("b2_cancel_large_file");
                await using var source = new FileStream(largePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                try
                {
                    await store.UploadAsync(bucket.Id, Prefix + "large/cancelled.bin", source, source.Length, largeSha, DateTimeOffset.UtcNow,
                        new CancelProgress(cancel), cancel.Token);
                    throw new InvalidOperationException("A canceled multipart upload reported success.");
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                Require(transport.SuccessCount("b2_cancel_large_file") == before + 1, "B2 did not acknowledge removal of the unfinished canceled upload.");
            });
            }
            if (args.Contains("--native")) await NativeSmoke(store, bucket.Id, account.AccountId, keys, temp, ct);
            }
        }
        catch (Exception error)
        {
            // Only safe transport/validation messages are retained; no exception dump can include credentials.
            Checks.Add(new("validation_run", false, Timer.Elapsed.TotalSeconds, ErrorDetail(error)));
            Console.WriteLine("Validation failed: " + Checks.Last().Detail);
        }
        finally
        {
            if (bucket is not null)
            {
                try { await CheckAsync("cleanup_hides_only_isolated_test_objects", async () =>
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var remaining = await List(store, bucket.Id, cleanup.Token);
                    foreach (var file in remaining.Where(f => f.Action == "upload"))
                    {
                        Require(file.Key.StartsWith(Prefix, StringComparison.Ordinal), "Cleanup encountered an object outside the validation prefix.");
                        await store.HideAsync(bucket.Id, file.Key, cleanup.Token);
                    }
                    Require((await List(store, bucket.Id, cleanup.Token)).All(f => f.Action != "upload"), "Visible validation objects remain after cleanup.");
                }); }
                catch { /* The failed cleanup check is retained in the acceptance report. */ }
            }
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var validationBase = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation"));
                Require(Path.GetDirectoryName(Path.GetFullPath(temp)) == validationBase && Path.GetFileName(temp) == Id, "Unsafe local validation cleanup path.");
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
                Checks.Add(new("cleanup_removes_only_local_validation_temp", true, 0, "Passed"));
            }
            catch (Exception error)
            {
                // Preserve a complete acceptance report even when a scanner holds a temporary file open.
                Checks.Add(new("cleanup_removes_only_local_validation_temp", false, 0, error.GetType().Name));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(new
            {
                timeUtc = DateTimeOffset.UtcNow, id = Id, bucketName = BucketName, prefix = Prefix, quick = args.Contains("--quick"), native = args.Contains("--native"), controller = args.Contains("--controller"), transfers = args.Contains("--transfers"),
                passed = Checks.Count > 0 && Checks.All(c => c.Passed), checks = Checks, notes = Notes,
                requestCounterScope = args.Contains("--transfers") ? "Observer transport only; fresh-process transfer worker counters are retained separately." :
                    args.Contains("--controller") ? "Observer transport only; controller uses an independent transport." : "Complete validation transport.",
                requestCounts = transport.Counts, successfulRequestCounts = transport.SuccessCounts,
                concurrentTokenViolations = transport.ConcurrentTokenViolations, durationSeconds = Timer.Elapsed.TotalSeconds,
                processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
            }, Json));
            Console.WriteLine("Acceptance report: " + ReportPath);
        }
        return Checks.Count > 0 && Checks.All(c => c.Passed) ? 0 : 1;
    }

    private static async Task CheckAsync(string name, Func<Task> action)
    {
        var watch = Stopwatch.StartNew(); Console.WriteLine("Running " + name);
        try { await action(); Checks.Add(new(name, true, watch.Elapsed.TotalSeconds, "Passed")); Console.WriteLine("Passed " + name); }
        catch (Exception error)
        {
            var detail = ErrorDetail(error);
            Checks.Add(new(name, false, watch.Elapsed.TotalSeconds, detail)); Console.WriteLine("Failed " + name + ": " + detail);
            if (error is ArgumentException or System.Runtime.InteropServices.COMException) Console.WriteLine(error.StackTrace);
            throw;
        }
    }
    private static string ErrorDetail(Exception error) => error is System.Runtime.InteropServices.COMException
        ? $"COMException HRESULT 0x{error.HResult:X8}: {error.Message}"
        : error is B2RequestException or InvalidOperationException or InvalidDataException or HttpRequestException or ArgumentException
            ? error.Message : error.GetType().Name;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<CloudObject> UploadBytes(B2CloudStore store, string bucket, ConcurrentDictionary<string, bool> keys, string relative, byte[] bytes, CancellationToken ct)
    {
        var key = Prefix + relative; keys[key] = true;
        using var source = new MemoryStream(bytes);
        return await store.UploadAsync(bucket, key, source, bytes.Length, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow, cancellationToken: ct);
    }
    private static async Task<List<CloudObject>> List(B2CloudStore store, string bucket, CancellationToken ct)
    {
        var result = new List<CloudObject>(); await foreach (var file in store.ListAsync(bucket, Prefix, ct)) result.Add(file); return result;
    }
    private static async Task<string> GenerateFile(string path, long length, CancellationToken ct)
    {
        var bytes = new byte[64 * 1024]; new Random(45193).NextBytes(bytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bytes.Length, FileOptions.Asynchronous);
        while (length > 0) { var count = (int)Math.Min(length, bytes.Length); await file.WriteAsync(bytes.AsMemory(0, count), ct); hash.AppendData(bytes, 0, count); length -= count; }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static async Task NativeSmoke(B2CloudStore store, string bucket, string accountId, ConcurrentDictionary<string, bool> keys, string temp, CancellationToken ct)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudBayValidation-" + Id);
        await using var placeholders = new WindowsPlaceholderService();
        var registered = false;
        try
        {
            await CheckAsync("native_cloud_files_explorer_registration_and_hydration", async () =>
            {
                await placeholders.ConnectAsync(root, accountId + ":validation:" + Id,
                    (file, offset, length, destination, token) => store.DownloadAsync(file, destination, offset, length, cancellationToken: token), ct);
                registered = true;
                var info = StorageProviderSyncRootManager.GetSyncRootInformationForId(placeholders.RegistrationId!);
                Require(info.Path.Path.Equals(root, StringComparison.OrdinalIgnoreCase), "Explorer registered an incorrect path.");
                var bytes = System.Text.Encoding.UTF8.GetBytes("CloudBay Explorer hydration validation " + Id);
                var remote = await UploadBytes(store, bucket, keys, "native/online.txt", bytes, ct);
                var local = Path.Combine(root, "online.txt");
                await placeholders.CreateOrUpdateAsync(local, remote, true, ct);
                Require(placeholders.IsPlaceholder(local), "Windows did not create a native Cloud Files placeholder.");
                var actual = await Task.Run(() => File.ReadAllBytes(local), ct).WaitAsync(TimeSpan.FromMinutes(2), ct);
                Require(bytes.SequenceEqual(actual), "Explorer placeholder hydration did not return the B2 content.");
            });
            await CheckAsync("native_sync_engine_upload_edit_delete_to_b2", async () =>
            {
                var settings = new AppSettings { RootPath = root, BucketId = bucket, Prefix = Prefix + "native/", PollSeconds = 60, UploadConcurrency = 4 };
                var manifest = new SyncManifest(Path.Combine(temp, "native.sqlite"));
                var activities = new ConcurrentBag<ActivityEvent>();
                SyncSnapshot? snapshot = null;
                await using var engine = new SyncEngine(store, placeholders, manifest, settings, Path.Combine(temp, "recovery"), activities.Add, s => snapshot = s);
                await engine.SyncNowAsync(ct);
                var path = Path.Combine(root, "local.txt"); await File.WriteAllTextAsync(path, "local native backup", ct);
                await engine.SyncNowAsync(ct);
                Require(snapshot?.State == ClientState.UpToDate, "Native local upload did not reach UpToDate: " + snapshot?.Message);
                Require(manifest.ReadAll().ContainsKey("local.txt"), "Native local upload did not enter the durable manifest.");
                await File.WriteAllTextAsync(path, "edited native backup", ct); await engine.SyncNowAsync(ct);
                var versions = await store.VersionsAsync(bucket, Prefix + "native/local.txt", ct);
                Require(versions.Count(v => v.Action == "upload") >= 2, "Native edit did not retain the previous B2 version.");
                File.Delete(path); await engine.SyncNowAsync(ct);
                Require((await store.VersionsAsync(bucket, Prefix + "native/local.txt", ct))[0].Action == "hide", "Native local deletion was not hidden on B2.");
                Require(activities.Any(a => a.Kind == ActivityKind.Upload && a.Completed), "Native uploads did not produce activity history.");
            });
        }
        finally
        {
            await placeholders.DisconnectAsync();
            if (registered && placeholders.RegistrationId is { } registration) StorageProviderSyncRootManager.Unregister(registration);
            // The entire target is a literal GUID folder made exclusively by this run.
            Require(Path.GetDirectoryName(root)!.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root) == "CloudBayValidation-" + Id, "Unsafe native cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Acceptance(string Name, bool Passed, double DurationSeconds, string Detail);
    private sealed class ConsoleProgress(string name) : IProgress<TransferProgress>
    {
        private int _last = -1;
        public void Report(TransferProgress value)
        {
            var tenth = value.TotalBytes > 0 ? (int)(value.Bytes * 10 / value.TotalBytes) : 10;
            if (tenth <= Volatile.Read(ref _last)) return;
            Interlocked.Exchange(ref _last, tenth); Console.WriteLine(name + " " + Math.Min(tenth * 10, 100) + "%");
        }
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) { if (value.Bytes >= 8 * 1024 * 1024) cancellation.Cancel(); }
    }
}

internal sealed class CountingHandler : DelegatingHandler
{
    public ConcurrentDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, int> SuccessCounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> BucketTypes { get; } = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _activeTokens = new(StringComparer.Ordinal);
    private int _tokenViolations;
    public int ConcurrentTokenViolations => Volatile.Read(ref _tokenViolations);
    public int Count(string name) => Counts.GetValueOrDefault(name);
    public int SuccessCount(string name) => SuccessCounts.GetValueOrDefault(name);
    public CountingHandler() : base(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        PooledConnectionLifetime = TimeSpan.FromHours(1), MaxConnectionsPerServer = 32,
        KeepAlivePingDelay = TimeSpan.FromSeconds(60), KeepAlivePingTimeout = TimeSpan.FromSeconds(20), KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
    }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Counters store only an API operation name. Actual URLs, headers, and credentials are never logged.
        var operation = request.RequestUri!.AbsolutePath.Split('/').FirstOrDefault(p => p.StartsWith("b2_", StringComparison.Ordinal)) ?? "other";
        Counts.AddOrUpdate(operation, 1, (_, old) => old + 1);
        string? tokenKey = null;
        if (operation is "b2_upload_file" or "b2_upload_part")
        {
            var header = request.Headers.GetValues("Authorization").Single();
            tokenKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(header)));
            if (!_activeTokens.TryAdd(tokenKey, true)) Interlocked.Increment(ref _tokenViolations);
        }
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) SuccessCounts.AddOrUpdate(operation, 1, (_, old) => old + 1);
            if (operation == "b2_list_buckets" && response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                using var json = JsonDocument.Parse(bytes);
                foreach (var b in json.RootElement.GetProperty("buckets").EnumerateArray())
                    BucketTypes[b.GetProperty("bucketId").GetString()!] = b.GetProperty("bucketType").GetString()!;
            }
            return response;
        }
        finally { if (tokenKey is not null) _activeTokens.TryRemove(tokenKey, out _); }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Core.Transfers;

internal static class CloudRelayAcceptance
{
    private const long LargeSize = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Plan(string Stage, string Id, string Temp, string AccountId, string BucketId,
        string Prefix, TransferEntry Source, string OperationId);
    private sealed record WorkerResult(int ProcessId, bool Passed, bool Interrupted, string? ErrorType,
        long AcknowledgedBytes, long[] SourceOffsets, TransferReceipt? Receipt, IReadOnlyDictionary<string, int> Requests);

    public static async Task RunAsync(B2CloudStore store, CloudBucket bucket, string accountId, string prefix, string id,
        string temp, Func<string, Func<Task>, Task> check, CancellationToken token, bool tinyOnly = false, bool coldSourceMetadata = false)
    {
        ValidateScope(temp, id, prefix);
        var sourceLocation = new TransferLocation("b2", accountId, bucket.Id, "", prefix + "relay/source/", "Generated relay source");
        var sourceEndpoint = new B2TransferEndpoint(store, sourceLocation);
        TransferReceipt? tiny = null, large = null;
        await check("ram_source_sha1_trailer_upload_accepted_by_live_b2", async () =>
        {
            var source = new GeneratedSource(1024);
            tiny = await sourceEndpoint.UploadAsync(Upload(id, "tiny.bin"), source, null, (_, _) => Task.CompletedTask, cancellationToken: token);
            await sourceEndpoint.VerifyAsync(tiny, source, token);
            Require(source.Opens == 1, "The tiny source was fetched more than once.");
        });
        WorkerResult? interrupted = null, resumed = null;
        if (!tinyOnly)
        {
        await check("ram_multipart_sha1_trailers_accepted_by_live_b2", async () =>
        {
            var source = new GeneratedSource(LargeSize);
            large = await sourceEndpoint.UploadAsync(Upload(id, "large.bin"), source, null,
                (_, _) => Task.CompletedTask, cancellationToken: token);
            Require(source.Opens == 2, "The multipart source was prescanned or fetched with an unexpected layout.");
        });
        var entry = new TransferEntry(large!.Id, "large.bin", large.Version, large.Size,
            GeneratedSource.Modified, large.Sha1);
        var plan = new Plan("", id, temp, accountId, bucket.Id, prefix, entry, Upload(id, "copied-large.bin").OperationId);
        await check("ram_cloud_relay_fresh_process_interrupt_keeps_acknowledged_part", async () =>
        {
            interrupted = await RunWorkerAsync(plan with { Stage = "interrupt" }, token);
            Require(interrupted.Passed && interrupted.Interrupted && interrupted.AcknowledgedBytes == LargeSize / 2 &&
                interrupted.SourceOffsets.SequenceEqual(new[] { 0L }), "Interruption did not retain exactly the first B2 range.");
            Require(interrupted.Requests.GetValueOrDefault("b2_cancel_large_file") == 0, "Pause cancelled the resumable B2 file.");
        });
        await check("ram_cloud_relay_fresh_process_resume_skips_acknowledged_range_and_verifies", async () =>
        {
            resumed = await RunWorkerAsync(plan with { Stage = "resume" }, token);
            Require(resumed.Passed && !resumed.Interrupted && resumed.ProcessId != interrupted!.ProcessId &&
                resumed.Receipt?.Size == LargeSize && resumed.SourceOffsets.SequenceEqual(new[] { LargeSize / 2 }) &&
                resumed.Requests.GetValueOrDefault("b2_start_large_file") == 0, "Restart did not reuse the saved immutable range checkpoint.");
        });
        await check("ram_cloud_relay_unknown_small_ack_reconciles_without_duplicate", async () =>
        {
            var trace = new RelayTrace(new CountingHandler()) { LoseFirstSmallAcknowledgment = true };
            using var relayStore = new B2CloudStore(trace);
            await relayStore.ConnectAsync(await CredentialsAsync(), token);
            var source = new B2TransferEndpoint(relayStore, sourceLocation).OpenSource(new(tiny!.Id, "tiny.bin", tiny.Version,
                tiny.Size, GeneratedSource.Modified, tiny.Sha1));
            var destination = new B2TransferEndpoint(relayStore, sourceLocation with { Path = prefix + "relay/uncertain/" });
            var request = Upload(id, "uncertain.bin");
            TransferCheckpoint? saved = null;
            var receipt = await destination.UploadAsync(request, source, null,
                (value, _) => { saved = value; return Task.CompletedTask; }, cancellationToken: token);
            await destination.VerifyAsync(receipt, source, token);
            Require(trace.Uploads.Count == 1, "A fully committed small cloud file was replayed after losing its acknowledgment.");
            var recovered = await destination.ReconcileAsync(request, source, saved, token);
            Require(recovered?.Id == receipt.Id, "The immutable receipt could not be recovered after the lost acknowledgment.");
        });
        }
        object? benchmark = null;
        await check("ram_cloud_tiny_benchmark_bounded_workers_reuses_sessions", async () =>
        {
            var count = 24;
            var fixtureReceipts = new TransferReceipt[count];
            var benchmarkSourceLocation = sourceLocation with { Path = prefix + "relay/tiny-benchmark-source/" };
            var benchmarkSource = new B2TransferEndpoint(store, benchmarkSourceLocation);
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (i, ct) =>
            {
                fixtureReceipts[i] = await benchmarkSource.UploadAsync(Upload(id, "benchmark-source-" + i + ".bin"),
                    new GeneratedSource(1024), null, (_, _) => Task.CompletedTask, cancellationToken: ct);
            });
            var counter = new CountingHandler();
            var trace = new RelayTrace(counter);
            using var relayStore = new B2CloudStore(trace);
            relayStore.Configure(0, 0, 4);
            await relayStore.ConnectAsync(await CredentialsAsync(), token);
            ITransferEndpoint sourceAdapter = new B2TransferEndpoint(relayStore, benchmarkSourceLocation);
            if (coldSourceMetadata) sourceAdapter = new ColdSourceEndpoint(sourceAdapter, relayStore);
            var destination = new B2TransferEndpoint(relayStore, sourceLocation with { Path = prefix + "relay/tiny-benchmark/" });
            var jobPlan = new TransferJobPlan(Guid.NewGuid().ToString("N"), sourceAdapter.Location, destination.Location,
                TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
            await using var engine = new TransferJobEngine(new(Path.Combine(temp, "benchmark-jobs.sqlite"), new JournalProtector()),
                location => location == sourceAdapter.Location ? sourceAdapter : destination, 4);
            await engine.CreateAsync(jobPlan, token);
            var watch = Stopwatch.StartNew();
            await engine.RunAsync(jobPlan.Id, token);
            watch.Stop();
            var snapshot = engine.Snapshots().Single();
            Require(snapshot.State == TransferJobState.Completed && snapshot.CompletedFiles == count,
                "The durable tiny-file job did not finish all verified copies: " + snapshot.Error);
            var intervals = trace.Uploads.OrderBy(value => value.Start).ToArray();
            var gaps = new List<double>();
            var activeUntil = intervals[0].End;
            foreach (var interval in intervals.Skip(1))
            {
                if (interval.Start > activeUntil) gaps.Add((interval.Start - activeUntil) * 1000d / Stopwatch.Frequency);
                activeUntil = Math.Max(activeUntil, interval.End);
            }
            Require(counter.Count("b2_authorize_account") == 1 && counter.Count("b2_get_upload_url") <= 4 && counter.ConcurrentTokenViolations == 0,
                "Tiny-file work repeated authorization or failed upload-token exclusivity.");
            Require(intervals.Length == count, "The tiny relay repeated or omitted a payload upload.");
            benchmark = new { files = count, bytesPerFile = tiny!.Size, workers = 4, elapsedSeconds = watch.Elapsed.TotalSeconds,
                filesPerSecond = count / watch.Elapsed.TotalSeconds, payloadBytesPerSecond = count * tiny.Size / watch.Elapsed.TotalSeconds,
                uploadRequests = intervals.Length, uploadEndpointRequests = counter.Count("b2_get_upload_url"), authorizationRequests = counter.Count("b2_authorize_account"),
                sourceMetadataEvidence = coldSourceMetadata ? "Fresh metadata GET per source (controlled baseline)" : "Exact immutable metadata from fresh source discovery",
                aggregateUploadIdleGapCount = gaps.Count, aggregateUploadIdleGapMilliseconds = gaps.Sum(),
                maximumAggregateUploadIdleGapMilliseconds = gaps.Count > 0 ? gaps.Max() : 0,
                requests = counter.Counts, httpVersions = counter.HttpVersions,
                requestTimings = counter.RequestMilliseconds.ToDictionary(pair => pair.Key, pair => new
                {
                    totalMilliseconds = pair.Value, meanMilliseconds = pair.Value / counter.Count(pair.Key),
                    note = "Request send through response headers; concurrent totals overlap and are not wall-clock time."
                }),
                interpretation = "Live B2-to-B2 durable transfer engine with discovery, bounded upload admission and overlapping verification. Includes immutable source checks, conflict lookup, durable intents, source GET and provider receipt verification. Tiny-file throughput is latency-bound; no constant maximum-speed claim." };
            Console.WriteLine(JsonSerializer.Serialize(benchmark, Json));
        });
        await check("ram_relay_state_directory_contains_only_small_durable_records", () =>
        {
            var files = Directory.GetFiles(temp, "*", SearchOption.AllDirectories);
            Require(files.All(file => (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).StartsWith("benchmark-jobs.sqlite", StringComparison.Ordinal)) && new FileInfo(file).Length < 1_000_000),
                "A relay validation payload or cache appeared in its local state directory.");
            return Task.CompletedTask;
        });
        var report = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/validation/cloud-relay-" + id + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { timeUtc = DateTimeOffset.UtcNow, id, largeBytes = tinyOnly ? 0 : LargeSize,
            interrupted, resumed, benchmark, payloadStorage = "Generated source streams and provider range streams only; state-directory contents checked. No OS-level filesystem tracing was performed.",
            processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }, Json), token);
        Console.WriteLine("Cloud relay report: " + report);
    }

    private sealed class JournalProtector : ITransferCheckpointProtector
    {
        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, "CloudBay.Validation.Transfer.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, "CloudBay.Validation.Transfer.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    }

    public static async Task<int> RunWorkerEntryAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--cloud-relay-worker");
        if (index < 0 || index + 1 >= args.Length) return 2;
        var envelope = Path.GetFullPath(args[index + 1]);
        Plan? plan = null;
        WorkerResult? result = null;
        try
        {
            plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(envelope)) ?? throw new InvalidDataException();
            ValidateScope(plan.Temp, plan.Id, plan.Prefix);
            Require(Path.GetDirectoryName(envelope) == plan.Temp && plan.Stage is "interrupt" or "resume", "The relay worker plan is outside its validation scope.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var counter = new CountingHandler();
            using var store = new B2CloudStore(counter);
            store.Configure(0, 0, 1);
            var account = await store.ConnectAsync(await CredentialsAsync(), timeout.Token);
            Require(account.AccountId == plan.AccountId && account.AllowedBucketId == plan.BucketId &&
                plan.Prefix.StartsWith((account.AllowedNamePrefix ?? "") + "CloudBayValidation/" + plan.Id + "/", StringComparison.Ordinal), "The relay worker account restriction differs from its plan.");
            var sourceLocation = new TransferLocation("b2", plan.AccountId, plan.BucketId, "", plan.Prefix + "relay/source/", "Generated cloud source");
            var source = new TrackedSource(new B2TransferEndpoint(store, sourceLocation).OpenSource(plan.Source));
            var destination = new B2TransferEndpoint(store, sourceLocation with { Path = plan.Prefix + "relay/destination/" });
            var request = new TransferUploadRequest(plan.OperationId, "copied-large.bin", TransferConflictPolicy.Fail);
            var checkpointPath = Path.Combine(plan.Temp, "relay.checkpoint.json");
            var saved = File.Exists(checkpointPath) ? JsonSerializer.Deserialize<TransferCheckpoint>(await File.ReadAllTextAsync(checkpointPath)) : null;
            TransferReceipt? receipt = null;
            var interrupted = false;
            try
            {
                receipt = await destination.ReconcileAsync(request, source, saved, timeout.Token);
                receipt ??= await destination.UploadAsync(request, source, saved, async (value, _) =>
                {
                    saved = value;
                    await SaveDurablyAsync(checkpointPath, value);
                    if (plan.Stage == "interrupt" && value.AcknowledgedBytes > 0) throw new OperationCanceledException();
                }, cancellationToken: timeout.Token);
                await destination.VerifyAsync(receipt, source, timeout.Token);
            }
            catch (OperationCanceledException) when (plan.Stage == "interrupt" && saved?.AcknowledgedBytes > 0) { interrupted = true; }
            result = new(Environment.ProcessId, interrupted || receipt is not null, interrupted, null,
                saved?.AcknowledgedBytes ?? 0, source.Offsets.ToArray(), receipt, counter.Counts);
        }
        catch (Exception error) { result = new(Environment.ProcessId, false, false,
            error is InvalidDataException or B2RequestException or InvalidOperationException ? error.GetType().Name + ": " + error.Message : error.GetType().Name,
            0, [], null, new Dictionary<string, int>()); }
        if (plan is not null) await SaveDurablyAsync(envelope + ".result.json", result);
        return result?.Passed == true ? 0 : 1;
    }

    private static async Task<WorkerResult> RunWorkerAsync(Plan plan, CancellationToken token)
    {
        var envelope = Path.Combine(plan.Temp, "relay-" + plan.Stage + ".json");
        await SaveDurablyAsync(envelope, plan);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The validation executable is unavailable.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--cloud-relay-worker"); start.ArgumentList.Add(envelope);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The relay validation worker could not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(output, errors);
        var result = JsonSerializer.Deserialize<WorkerResult>(await File.ReadAllTextAsync(envelope + ".result.json", token)) ?? throw new InvalidDataException();
        var report = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/validation/cloud-relay-" + plan.Id + "-" + plan.Stage + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, Json), token);
        Require(process.ExitCode == 0 && result.Passed, "A RAM relay worker failed: " + result.ErrorType);
        return result;
    }

    public static async Task CleanupAsync(B2CloudStore store, string bucketId, string prefix, string temp, CancellationToken token)
    {
        if (!Directory.Exists(temp)) return;
        foreach (var path in Directory.GetFiles(temp, "*.checkpoint.json", SearchOption.TopDirectoryOnly))
        {
            var checkpoint = JsonSerializer.Deserialize<TransferCheckpoint>(await File.ReadAllTextAsync(path, token));
            if (checkpoint?.Data?.GetValueOrDefault("kind") != "large") continue;
            Require(checkpoint.Data.GetValueOrDefault("key")?.StartsWith(prefix, StringComparison.Ordinal) == true,
                "Relay cleanup encountered an upload outside its generated prefix.");
            await store.CancelTransferUploadAsync(bucketId, checkpoint, token);
        }
    }

    public static async Task<int> CleanupOldRunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--cloud-relay-cleanup");
        if (index < 0 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out _)) return 2;
        var id = args[index + 1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var store = new B2CloudStore();
        var account = await store.ConnectAsync(await CredentialsAsync(), timeout.Token);
        var buckets = await store.ListBucketsAsync(timeout.Token);
        Require(buckets.Count == 1 && account.AllowedBucketId == buckets[0].Id, "Cleanup requires the restricted validation account.");
        var prefix = (account.AllowedNamePrefix ?? "") + "CloudBayValidation/" + id + "/";
        var versions = await store.VersionsAsync(buckets[0].Id, prefix + "relay/source/large.bin", timeout.Token);
        var source = versions.FirstOrDefault(file => file.Action == "upload" && file.Size == LargeSize && file.ModifiedUtc == GeneratedSource.Modified);
        Require(source is not null, "The generated relay source identity was not found; nothing was cancelled.");
        var entry = new TransferEntry(source!.FileId, "large.bin", source.FileId, source.Size, source.ModifiedUtc, source.Sha1);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { entry.Id, entry.Version, entry.Size, entry.ModifiedUtc })))).ToLowerInvariant();
        var data = new Dictionary<string, string> { ["kind"] = "large", ["key"] = prefix + "relay/destination/copied-large.bin",
            ["source_id"] = identity, ["operation_id"] = Upload(id, "copied-large.bin").OperationId };
        await store.CancelTransferUploadAsync(buckets[0].Id, new("b2", "", 0, data), timeout.Token);
        Console.WriteLine("Cancelled only the caller-owned unfinished relay upload under CloudBayValidation/" + id + "/.");
        return 0;
    }

    private static async Task SaveDurablyAsync<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
        await JsonSerializer.SerializeAsync(file, value, Json);
        file.Flush(flushToDisk: true);
    }
    private static TransferUploadRequest Upload(string id, string name) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "|" + name))).ToLowerInvariant(), name, TransferConflictPolicy.Fail);
    private static async Task<B2Credentials> CredentialsAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        var plaintext = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<B2Credentials>(plaintext) ?? throw new InvalidDataException("Validation credentials are unavailable."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static void ValidateScope(string temp, string id, string prefix)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation");
        Require(Guid.TryParseExact(id, "N", out _) && Path.GetDirectoryName(Path.GetFullPath(temp)) == root &&
            Path.GetFileName(temp) == id && prefix.Contains("CloudBayValidation/" + id + "/", StringComparison.Ordinal), "Relay validation is outside its generated GUID scope.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TrackedSource(ITransferSourceFile inner) : ITransferSourceFile
    {
        public ConcurrentQueue<long> Offsets { get; } = new();
        public TransferEntry Entry => inner.Entry;
        public bool HasContentBoundVersion => inner.HasContentBoundVersion;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => inner.ValidateAsync(cancellationToken);
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        { Offsets.Enqueue(offset); return inner.OpenReadAsync(offset, length, cancellationToken); }
    }
    // Measurement-only control: discovery still uses the real API, but a new
    // endpoint opens each source so no in-memory discovery evidence is available.
    // Destination verification and all other transport/recovery behavior are identical.
    private sealed class ColdSourceEndpoint(ITransferEndpoint inner, B2CloudStore store) : ITransferEndpoint
    {
        public TransferLocation Location => inner.Location;
        public ITransferSourceFile OpenSource(TransferEntry entry) => new B2TransferEndpoint(store, Location).OpenSource(entry);
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) => inner.BrowseFoldersAsync(cursor, cancellationToken);
        public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) => inner.DiscoverAsync(cursor, cancellationToken);
        public Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default) => inner.ReconcileAsync(request, source, checkpoint, cancellationToken);
        public Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint,
            IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => inner.UploadAsync(request, source, checkpoint, saveCheckpoint, progress, cancellationToken);
        public Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default) => inner.VerifyAsync(receipt, source, cancellationToken);
        public Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default) => inner.DeleteSourceAsync(entry, cancellationToken);
        public Task<bool> IsSourceDeletedAsync(TransferEntry entry, CancellationToken cancellationToken = default) => inner.IsSourceDeletedAsync(entry, cancellationToken);
    }
    private sealed class GeneratedSource(long size) : ITransferSourceFile
    {
        public static DateTimeOffset Modified => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        private int _opens;
        public int Opens => Volatile.Read(ref _opens);
        public TransferEntry Entry { get; } = new("generated-" + size, "generated.bin", "immutable-v1", size, Modified, Hash(size));
        public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _opens); return Task.FromResult<Stream>(new GeneratedStream(offset, length)); }
        private static string Hash(long size)
        { using var input = new GeneratedStream(0, size); return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant(); }
    }
    private sealed class GeneratedStream(long offset, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            for (var i = 0; i < read; i++) buffer[start + i] = (byte)((offset + _position + i) % 251);
            _position += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - _position);
            for (var i = 0; i < read; i++) buffer.Span[i] = (byte)((offset + _position + i) % 251);
            _position += read; return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class RelayTrace(CountingHandler inner) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<(long Start, long End)> Uploads { get; } = new();
        public bool LoseFirstSmallAcknowledgment { get; init; }
        private int _lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var upload = request.RequestUri!.AbsolutePath.Contains("b2_upload_file", StringComparison.Ordinal);
            var begin = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, token);
            if (upload)
            {
                Uploads.Enqueue((begin, Stopwatch.GetTimestamp()));
                if (LoseFirstSmallAcknowledgment && response.IsSuccessStatusCode && Interlocked.Exchange(ref _lost, 1) == 0)
                { response.Dispose(); throw new HttpRequestException("Injected loss of the successful upload acknowledgment."); }
            }
            return response;
        }
    }
}

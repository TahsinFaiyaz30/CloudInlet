using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CloudBay.Core;
using CloudBay.Core.B2;

internal static class TransferAcceptance
{
    private const long SourceSize = 205L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Plan(string Stage, string Id, string Temp, string BucketId, string Prefix,
        string SourceSha1, CloudObject? File = null);
    private sealed record Result(string Stage, int ProcessId, bool Passed, bool Interrupted, string? ErrorType,
        CloudObject? File, string? UnfinishedFileId, int[] UploadedParts, long[] DownloadOffsets,
        int MaximumActiveDownloads, IReadOnlyDictionary<string, int> RequestCounts, DownloadChunk[] Chunks);

    public static async Task RunAsync(B2CloudStore observer, CloudBucket bucket, string prefix, string id,
        string temp, Func<string, Func<Task>, Task> check, CancellationToken token)
    {
        ValidateTemp(temp, id);
        var reportRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/validation"));
        Directory.CreateDirectory(reportRoot);
        var sha1 = "";
        Result? pausedUpload = null, finishedUpload = null, pausedDownload = null, finishedDownload = null;
        await check("transfer_artificial_source_generated_without_user_files", async () =>
        {
            sha1 = await GenerateAsync(Path.Combine(temp, "transfer-source.bin"), token);
            Require(new FileInfo(Path.Combine(temp, "transfer-source.bin")).Length == SourceSize, "The generated transfer test source has the wrong size.");
        });
        var plan = new Plan("", id, temp, bucket.Id, prefix + "transfers/", sha1);
        try
        {
            await check("multipart_interrupt_preserves_server_acknowledged_part_and_journal", async () =>
            {
                pausedUpload = await WorkerAsync(plan with { Stage = "interrupt-upload" }, reportRoot, token);
                Require(pausedUpload.Passed && pausedUpload.Interrupted && pausedUpload.UploadedParts.SequenceEqual(new[] { 1 }) &&
                    pausedUpload.UnfinishedFileId is { Length: > 0 }, "The interrupted upload did not preserve exactly the first acknowledged part.");
                Require(pausedUpload.RequestCounts.GetValueOrDefault("b2_cancel_large_file") == 0,
                    "Interruption cancelled the resumable server-side upload.");
            });
            await check("multipart_new_process_resumes_immutable_upload_without_resending_part", async () =>
            {
                finishedUpload = await WorkerAsync(plan with { Stage = "resume-upload" }, reportRoot, token);
                Require(finishedUpload.Passed && finishedUpload.File is { } file && file.Size == SourceSize && file.Sha1 == sha1 &&
                    file.FileId == pausedUpload!.UnfinishedFileId, "Restart did not finish the original immutable multipart version.");
                Require(finishedUpload.ProcessId != pausedUpload!.ProcessId && finishedUpload.RequestCounts.GetValueOrDefault("b2_list_parts") > 0 &&
                    finishedUpload.RequestCounts.GetValueOrDefault("b2_start_large_file") == 0 &&
                    finishedUpload.UploadedParts.Length > 0 && !finishedUpload.UploadedParts.Contains(1),
                    "Restart did not reuse the server-confirmed first part.");
                Require(finishedUpload.RequestCounts.GetValueOrDefault("b2_get_file_info") > 0,
                    "The completed immutable B2 version was not independently verified.");
            });
            plan = plan with { File = finishedUpload!.File };
            await check("download_interrupt_preserves_one_durable_verified_chunk", async () =>
            {
                pausedDownload = await WorkerAsync(plan with { Stage = "interrupt-download" }, reportRoot, token);
                Require(pausedDownload.Passed && pausedDownload.Interrupted && pausedDownload.Chunks.Length == 1 &&
                    pausedDownload.Chunks[0].Offset == 0 && pausedDownload.Chunks[0].Length == B2CloudStore.DownloadChunkSize &&
                    pausedDownload.DownloadOffsets.SequenceEqual(new[] { 0L }), "The interrupted download did not preserve exactly its first durable chunk.");
            });
            await check("download_new_process_resumes_parallel_ranges_and_verifies_disk_sha1", async () =>
            {
                finishedDownload = await WorkerAsync(plan with { Stage = "resume-download" }, reportRoot, token);
                Require(finishedDownload.Passed && finishedDownload.ProcessId != pausedDownload!.ProcessId &&
                    finishedDownload.DownloadOffsets.Length > 1 && !finishedDownload.DownloadOffsets.Contains(0) &&
                    finishedDownload.MaximumActiveDownloads is > 1 and <= 4,
                    "The restarted download did not skip the completed chunk within the shared parallel request budget.");
                Require(finishedDownload.Chunks.Length == (SourceSize + B2CloudStore.DownloadChunkSize - 1) / B2CloudStore.DownloadChunkSize,
                    "The resumed download lost a chunk checkpoint.");
            });
        }
        finally
        {
            // This journal belongs only to the generated GUID test prefix. Cancel it if a worker
            // failed before finishing, without touching the user's regular background client.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            observer.ConfigureResumableUploads(Path.Combine(temp, "transfer-upload-journal"));
            await observer.CancelPendingUploadsAsync(cleanup.Token);
            var reports = new[] { pausedUpload, finishedUpload, pausedDownload, finishedDownload }.Where(result => result is not null).ToArray();
            await File.WriteAllTextAsync(Path.Combine(reportRoot, "transfer-processes-" + id + ".json"), JsonSerializer.Serialize(new
            {
                timeUtc = DateTimeOffset.UtcNow, id, sourceBytes = SourceSize,
                workerProcesses = reports, passed = reports.Length == 4 && reports.All(result => result!.Passed)
            }, Json), CancellationToken.None);
        }
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--transfer-worker");
        if (index < 0 || index + 1 >= args.Length) return 2;
        var envelope = Path.GetFullPath(args[index + 1]);
        Plan? plan = null;
        Result? result = null;
        var scopeValidated = false;
        try
        {
            var directory = Path.GetDirectoryName(envelope) ?? throw new InvalidDataException();
            ValidateTemp(directory, Path.GetFileName(directory));
            plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(envelope)) ?? throw new InvalidDataException();
            ValidatePlan(plan, envelope);
            scopeValidated = true;
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(25));
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var counter = new CountingHandler();
            using var trace = new TransferTrace(counter, plan.Stage == "interrupt-upload" ? interrupted : null);
            using var store = new B2CloudStore(trace);
            store.Configure(0, 0, plan.Stage == "resume-upload" ? 4 : 1);
            store.ConfigureDownloads(plan.Stage == "resume-download" ? 4 : 1);
            store.ConfigureResumableUploads(Path.Combine(plan.Temp, "transfer-upload-journal"));
            var account = await store.ConnectAsync(await CredentialsAsync(), lifetime.Token);
            Require(account.AllowedBucketId == plan.BucketId &&
                plan.Prefix.StartsWith((account.AllowedNamePrefix ?? "") + "CloudBayValidation/" + plan.Id + "/", StringComparison.Ordinal),
                "Transfer workers require the restricted validation bucket and GUID prefix.");
            CloudObject? uploaded = null;
            string? unfinished = null;
            var wasInterrupted = false;
            var chunks = new List<DownloadChunk>();
            if (plan.Stage is "interrupt-upload" or "resume-upload")
            {
                var sourcePath = Path.Combine(plan.Temp, "transfer-source.bin");
                await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath));
                var hash = await store.PrepareUploadChecksumAsync(plan.BucketId, plan.Prefix + "source.bin", source, source.Length, modified, lifetime.Token);
                Require(hash == plan.SourceSha1 && source.Length == SourceSize, "The generated upload source changed before transfer.");
                try
                {
                    uploaded = await store.UploadAsync(plan.BucketId, plan.Prefix + "source.bin", source, source.Length, hash,
                        modified, cancellationToken: interrupted.Token);
                    if (plan.Stage == "interrupt-upload") throw new InvalidOperationException("The interruption did not stop the upload.");
                    await store.VerifyUploadAsync(uploaded, plan.BucketId, lifetime.Token);
                }
                catch (OperationCanceledException) when (plan.Stage == "interrupt-upload" && interrupted.IsCancellationRequested && !lifetime.IsCancellationRequested)
                { wasInterrupted = true; }
                if (wasInterrupted)
                {
                    var journal = Directory.GetFiles(Path.Combine(plan.Temp, "transfer-upload-journal"), "*.json").Single();
                    using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(journal, lifetime.Token));
                    unfinished = saved.RootElement.GetProperty("FileId").GetString();
                }
            }
            else
            {
                var file = plan.File ?? throw new InvalidDataException("The download worker has no immutable cloud version.");
                Require(file.Key == plan.Prefix + "source.bin" && file.Size == SourceSize && file.Sha1 == plan.SourceSha1,
                    "The download worker's immutable version is outside its test identity.");
                var chunkPath = Path.Combine(plan.Temp, "transfer-download-chunks.json");
                if (plan.Stage == "resume-download")
                    chunks = JsonSerializer.Deserialize<List<DownloadChunk>>(await File.ReadAllTextAsync(chunkPath, lifetime.Token)) ?? throw new InvalidDataException();
                await using var staged = new FileStream(Path.Combine(plan.Temp, "transfer-download.part"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                try
                {
                    await store.DownloadFileAsync(file, staged, chunks.ToArray(), async (chunk, token) =>
                    {
                        chunks.RemoveAll(previous => previous.Offset == chunk.Offset);
                        chunks.Add(chunk);
                        await SaveDurablyAsync(chunkPath, chunks, token);
                        if (plan.Stage == "interrupt-download") interrupted.Cancel();
                    }, cancellationToken: interrupted.Token);
                    if (plan.Stage == "interrupt-download") throw new InvalidOperationException("The interruption did not stop the download.");
                    Require(staged.Length == SourceSize && staged.Position == SourceSize, "The completed download length or position changed.");
                    staged.Position = 0;
                    var diskHash = Convert.ToHexString(await SHA1.HashDataAsync(staged, lifetime.Token)).ToLowerInvariant();
                    Require(diskHash == plan.SourceSha1, "The resumed download's independently read disk bytes differ from the source.");
                }
                catch (OperationCanceledException) when (plan.Stage == "interrupt-download" && interrupted.IsCancellationRequested && !lifetime.IsCancellationRequested)
                { wasInterrupted = true; }
            }
            result = new(plan.Stage, Environment.ProcessId, true, wasInterrupted, null, uploaded, unfinished,
                trace.UploadedParts.ToArray(), trace.DownloadOffsets.ToArray(), trace.MaximumActiveDownloads,
                new Dictionary<string, int>(counter.Counts), chunks.ToArray());
        }
        catch (Exception error)
        {
            // Exception text, request URLs and headers are deliberately excluded from worker reports.
            result = new(plan?.Stage ?? "invalid", Environment.ProcessId, false, false, error.GetType().Name,
                null, null, [], [], 0, new Dictionary<string, int>(), []);
        }
        if (plan is null || !scopeValidated) return 2;
        var resultPath = envelope + ".result.json";
        await SaveDurablyAsync(resultPath, result, CancellationToken.None);
        return result.Passed ? 0 : 1;
    }

    private static async Task<Result> WorkerAsync(Plan plan, string reportRoot, CancellationToken token)
    {
        var envelope = Path.Combine(plan.Temp, "transfer-" + plan.Stage + ".json");
        await SaveDurablyAsync(envelope, plan, token);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The validation worker host is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--transfer-worker");
        start.ArgumentList.Add(envelope);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("The transfer validation worker could not start.");
        var output = worker.StandardOutput.ReadToEndAsync(token);
        var errors = worker.StandardError.ReadToEndAsync(token);
        try { await worker.WaitForExitAsync(token); }
        catch { if (!worker.HasExited) worker.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(output, errors);
        var result = JsonSerializer.Deserialize<Result>(await File.ReadAllTextAsync(envelope + ".result.json", token))
            ?? throw new InvalidDataException("The transfer validation worker returned no result.");
        await File.WriteAllTextAsync(Path.Combine(reportRoot, "transfer-" + plan.Id + "-" + plan.Stage + ".json"),
            JsonSerializer.Serialize(result, Json), token);
        Require(worker.ExitCode == 0 && result.Passed, "A transfer validation worker failed: " + (result.ErrorType ?? "invalid result"));
        return result;
    }

    private static async Task<B2Credentials> CredentialsAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "credentials.dpapi");
        var plaintext = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<B2Credentials>(plaintext) ?? throw new InvalidDataException("Validation credentials are unavailable."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static async Task<string> GenerateAsync(string path, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var random = new Random(50431);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);
            long remaining = SourceSize;
            while (remaining > 0)
            {
                random.NextBytes(buffer);
                var count = (int)Math.Min(buffer.Length, remaining);
                await file.WriteAsync(buffer.AsMemory(0, count), token);
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            await file.FlushAsync(token); file.Flush(true);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task SaveDurablyAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            { await JsonSerializer.SerializeAsync(file, value, Json, token); await file.FlushAsync(token); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidatePlan(Plan plan, string envelope)
    {
        ValidateTemp(plan.Temp, plan.Id);
        Require(Path.GetDirectoryName(envelope) == Path.GetFullPath(plan.Temp) &&
            Path.GetFileName(envelope) == "transfer-" + plan.Stage + ".json" &&
            plan.Stage is "interrupt-upload" or "resume-upload" or "interrupt-download" or "resume-download" &&
            plan.Prefix.Contains("CloudBayValidation/" + plan.Id + "/transfers/", StringComparison.Ordinal) &&
            plan.SourceSha1 is { Length: 40 } && plan.SourceSha1.All(Uri.IsHexDigit), "The worker plan is outside its isolated validation scope.");
    }
    private static void ValidateTemp(string temp, string id)
    {
        var validationBase = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation"));
        Require(Guid.TryParseExact(id, "N", out _) && Path.GetDirectoryName(Path.GetFullPath(temp)) == validationBase &&
            Path.GetFileName(Path.TrimEndingDirectorySeparator(temp)) == id, "The transfer validation temporary directory is outside its generated GUID scope.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TransferTrace(CountingHandler inner, CancellationTokenSource? interruptAfterFirstPart) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<int> UploadedParts { get; } = new();
        public ConcurrentQueue<long> DownloadOffsets { get; } = new();
        private int _activeDownloads, _maximumDownloads;
        public int MaximumActiveDownloads => Volatile.Read(ref _maximumDownloads);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var part = request.Headers.TryGetValues("X-Bz-Part-Number", out var numbers) ? int.Parse(numbers.Single(), System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (part != 0 && interruptAfterFirstPart is not null && !UploadedParts.IsEmpty)
            { interruptAfterFirstPart.Cancel(); token.ThrowIfCancellationRequested(); }
            var download = request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("b2_download_file_by_id", StringComparison.Ordinal);
            if (download)
            {
                DownloadOffsets.Enqueue(request.Headers.Range?.Ranges.Single().From ?? 0);
                var active = Interlocked.Increment(ref _activeDownloads);
                int old;
                do { old = Volatile.Read(ref _maximumDownloads); if (old >= active) break; }
                while (Interlocked.CompareExchange(ref _maximumDownloads, active, old) != old);
            }
            try
            {
                var response = await base.SendAsync(request, token);
                if (part != 0 && response.IsSuccessStatusCode) UploadedParts.Enqueue(part);
                if (download) response.Content = new CountedContent(response.Content, () => Interlocked.Decrement(ref _activeDownloads));
                return response;
            }
            catch { if (download) Interlocked.Decrement(ref _activeDownloads); throw; }
        }
    }

    private sealed class CountedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _release;
        private int _disposed;
        public CountedContent(HttpContent inner, Action release)
        {
            _inner = inner; _release = release;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _inner.ReadAsStreamAsync(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) { _inner.Dispose(); _release(); }
            base.Dispose(disposing);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using CloudBay.Core;
using CloudBay.Core.Transfers;

namespace CloudBay.Benchmarks;

internal sealed record TraceEvent(string File, string Stage, double StartMs, double EndMs, long Bytes = 0, string? Detail = null);

internal sealed class TransferTrace
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly ConcurrentQueue<TraceEvent> _events = new();
    public double Now => (Stopwatch.GetTimestamp() - _origin) * 1000d / Stopwatch.Frequency;
    public bool Enabled { get; set; }
    public IReadOnlyList<TraceEvent> Events => _events.ToArray();
    public void Add(string file, string stage, double start, double end, long bytes = 0, string? detail = null)
    { if (Enabled) _events.Enqueue(new(file, stage, start, end, bytes, detail)); }
    public IDisposable Span(string file, string stage, string? detail = null) => new TraceSpan(this, file, stage, detail);
    private sealed class TraceSpan(TransferTrace trace, string file, string stage, string? detail) : IDisposable
    {
        private readonly double _start = trace.Now;
        public void Dispose() => trace.Add(file, stage, _start, trace.Now, detail: detail);
    }
    public object Summary(double started, double finished, int files, long bytes)
    {
        var events = Events.Where(value => value.StartMs >= started && value.EndMs <= finished).ToArray();
        var payload = events.Where(value => value.Stage is "payload-body-write" or "payload-body-read" or "simulated-payload").ToArray();
        var meaningful = events.Where(value => value.Stage is "upload" or "verify" or "source-open" or "source-validate" or "reconcile" or "discover").ToArray();
        var requestGroups = events.Where(value => value.Stage.StartsWith("http:", StringComparison.Ordinal)).GroupBy(value => value.Stage)
            .ToDictionary(group => group.Key, group => new { count = group.Count(), summedOverlappingMs = group.Sum(value => value.EndMs - value.StartMs), bytes = group.Sum(value => value.Bytes) });
        var perFile = events.Where(value => value.File.Length > 0).GroupBy(value => value.File).Select(group =>
        {
            double? First(string stage) => group.Where(value => value.Stage == stage).Select(value => (double?)value.StartMs).Min();
            double? Last(string stage) => group.Where(value => value.Stage == stage).Select(value => (double?)value.EndMs).Max();
            var uploadStart = First("upload");
            var receipt = Last("upload");
            var verifyStart = First("verify");
            return new { path = group.Key, claimedMs = First("claimed"), reconcileStartMs = First("reconcile"), uploadStartMs = uploadStart,
                firstSourceReadMs = group.Where(value => value.Stage == "source-read" && value.Bytes > 0).Select(value => (double?)value.StartMs).Min(),
                lastSourceReadMs = group.Where(value => value.Stage == "source-read" && value.Bytes > 0).Select(value => (double?)value.EndMs).Max(),
                firstPayloadMs = group.Where(value => value.Bytes > 0 && value.Stage is "payload-write" or "payload-read" or "simulated-payload").Select(value => (double?)value.StartMs).Min(),
                lastPayloadMs = group.Where(value => value.Bytes > 0 && value.Stage is "payload-write" or "payload-read" or "simulated-payload").Select(value => (double?)value.EndMs).Max(),
                receiptMs = receipt, verifyStartMs = verifyStart, verifiedMs = Last("verify"), completeMs = Last("complete"),
                claimToUploadMs = uploadStart - First("claimed"), receiptToVerifyMs = verifyStart - receipt,
                sourceBytesRead = group.Where(value => value.Stage == "source-read").Sum(value => value.Bytes),
                sourceRangeOpens = group.Count(value => value.Stage == "source-open"), sourceValidationCount = group.Count(value => value.Stage == "source-validate") };
        }).OrderBy(value => value.claimedMs).ToArray();
        return new { files, bytes, elapsedSeconds = (finished - started) / 1000, filesPerSecond = files * 1000d / (finished - started), verifiedBytesPerSecond = bytes * 1000d / (finished - started),
            payloadBodyActivity = Union(payload, started, finished), streamCallActivity = Union(events.Where(value => value.Stage is "payload-write" or "payload-read" or "source-read").ToArray(), started, finished),
            applicationActivity = Union(meaningful, started, finished), requests = requestGroups, perFile,
            interpretation = "Body serialization/response-stream lifetimes are pipeline activity, not remote line utilization; response lifetimes include consumer/read-ahead backpressure. Adapter spans include preparation and durable acknowledgments; uncovered intervals estimate application scheduling gaps. Concurrent request totals overlap. Local filesystem writes are inferred from upload progress and source ranges, not instrumented below the adapter." };
    }
    private static object Union(IReadOnlyList<TraceEvent> intervals, double start, double finish)
    {
        var sorted = intervals.Where(value => value.EndMs > value.StartMs).OrderBy(value => value.StartMs).ToArray();
        var gaps = new List<double>(); double active = 0, previous = start;
        foreach (var interval in sorted)
        {
            var left = Math.Max(start, interval.StartMs); var right = Math.Min(finish, interval.EndMs);
            if (left > previous) gaps.Add(left - previous);
            if (right > Math.Max(previous, left)) active += right - Math.Max(previous, left);
            previous = Math.Max(previous, right);
        }
        if (finish > previous) gaps.Add(finish - previous);
        var ordered = gaps.Order().ToArray();
        return new { activeMs = active, uncoveredMs = gaps.Sum(), uncoveredIntervals = gaps.Count, maximumUncoveredMs = gaps.Count == 0 ? 0 : gaps.Max(),
            p95UncoveredMs = ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], activeFraction = active / (finish - start) };
    }
}

internal static class TraceContext
{
    private static readonly AsyncLocal<(string File, string Phase)?> Current = new();
    public static (string File, string Phase) Value => Current.Value ?? ("", "setup");
    public static IDisposable Enter(string file, string phase)
    { var before = Current.Value; Current.Value = (file, phase); return new Restore(() => Current.Value = before); }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}

internal sealed class TracedEndpoint(ITransferEndpoint endpoint, TransferTrace trace) : ITransferEndpoint
{
    public TransferLocation Location => endpoint.Location;
    public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) => endpoint.BrowseFoldersAsync(cursor, cancellationToken);
    public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) => DiscoverAsync(cursor, [], cancellationToken);
    public async Task<TransferDiscoveryPage> DiscoverAsync(string? cursor, IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
    { using var span = trace.Span("", "discover", Location.Provider); return await endpoint.DiscoverAsync(cursor, exclusions, cancellationToken); }
    public ITransferSourceFile OpenSource(TransferEntry entry)
    { trace.Add(entry.RelativePath, "claimed", trace.Now, trace.Now); return new TracedSource(endpoint.OpenSource(entry), trace); }
    public async Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default)
    { using var scope = TraceContext.Enter(source.Entry.RelativePath, "reconcile"); using var span = trace.Span(source.Entry.RelativePath, "reconcile"); return await endpoint.ReconcileAsync(request, source, checkpoint, cancellationToken); }
    public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var scope = TraceContext.Enter(source.Entry.RelativePath, "upload"); using var span = trace.Span(source.Entry.RelativePath, "upload");
        return await endpoint.UploadAsync(request, source, checkpoint, (value, token) =>
        { using var checkpointSpan = trace.Span(source.Entry.RelativePath, "checkpoint"); return saveCheckpoint(value, token); }, new InlineProgress(value =>
        { trace.Add(source.Entry.RelativePath, "progress", trace.Now, trace.Now, value.Bytes); progress?.Report(value); }), cancellationToken);
    }
    public async Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default) => await VerifyReceiptAsync(receipt, source, cancellationToken);
    public async Task<TransferReceipt> VerifyReceiptAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
    { using var scope = TraceContext.Enter(source.Entry.RelativePath, "verify"); using var span = trace.Span(source.Entry.RelativePath, "verify"); return await endpoint.VerifyReceiptAsync(receipt, source, cancellationToken); }
    public Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default) => endpoint.DeleteSourceAsync(entry, cancellationToken);
    public Task<bool> IsSourceDeletedAsync(TransferEntry entry, CancellationToken cancellationToken = default) => endpoint.IsSourceDeletedAsync(entry, cancellationToken);
    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress> { public void Report(TransferProgress value) => report(value); }
}

internal sealed class TracedSource(ITransferSourceFile source, TransferTrace trace) : ITransferSourceFile
{
    public TransferEntry Entry => source.Entry;
    public bool HasContentBoundVersion => source.HasContentBoundVersion;
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    { using var span = trace.Span(Entry.RelativePath, "source-validate", TraceContext.Value.Phase); await source.ValidateAsync(cancellationToken); }
    public async Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
    { using var span = trace.Span(Entry.RelativePath, "source-open", $"offset={offset};length={length}"); return new ProbeStream(await source.OpenReadAsync(offset, length, cancellationToken), trace, Entry.RelativePath, "source-read", null); }
}

internal sealed class ProbeStream(Stream inner, TransferTrace trace, string file, string? readStage, string? writeStage, Action? disposed = null) : Stream
{
    private int _disposed;
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count)
    { var start = trace.Now; var read = inner.Read(buffer, offset, count); if (readStage is not null) trace.Add(file, readStage, start, trace.Now, read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var start = trace.Now; var read = await inner.ReadAsync(buffer, cancellationToken); if (readStage is not null) trace.Add(file, readStage, start, trace.Now, read); return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count)
    { var start = trace.Now; inner.Write(buffer, offset, count); if (writeStage is not null) trace.Add(file, writeStage, start, trace.Now, count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { var start = trace.Now; await inner.WriteAsync(buffer, cancellationToken); if (writeStage is not null) trace.Add(file, writeStage, start, trace.Now, buffer.Length); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); Finish(); } base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); Finish(); }
    private void Finish() { if (Interlocked.Exchange(ref _disposed, 1) == 0) disposed?.Invoke(); }
}

internal sealed class TracedHttpHandler(TransferTrace trace) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 32 })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (file, phase) = TraceContext.Value;
        var kind = Kind(request); var started = trace.Now;
        var originalContent = request.Content;
        if (request.Content is not null && request.Content.Headers.ContentLength != 0 && kind is "upload-file" or "upload-part" or "graph-content-put" or "graph-fragment")
            request.Content = new ProbeContent(request.Content, trace, file, null, "payload-write");
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            trace.Add(file, "http:" + kind, started, trace.Now, detail: $"{phase};status={(int)response.StatusCode};http={response.Version}");
            if (kind is "download" && response.Content is not null)
                response.Content = new ProbeContent(response.Content, trace, file, "payload-read", null);
            return response;
        }
        catch (HttpRequestException error)
        { trace.Add(file, "http:" + kind, started, trace.Now, detail: $"{phase};transport={error.HttpRequestError}"); throw; }
        finally { request.Content = originalContent; }
    }
    private static string Kind(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var last = path[(path.LastIndexOf('/') + 1)..];
        if (last == "b2_download_file_by_id") return "download";
        if (last.StartsWith("b2_", StringComparison.Ordinal)) return last[3..].Replace('_', '-');
        if (path.Contains("/b2api/") && request.Method == HttpMethod.Post) return "upload-file";
        if (request.RequestUri.Host == "login.microsoftonline.com") return "authorization";
        if (request.RequestUri.Host == "graph.microsoft.com")
            return request.Method == HttpMethod.Put && path.EndsWith("/content") ? "graph-content-put" : "graph-metadata";
        return request.Method == HttpMethod.Put ? "graph-fragment" : request.Method == HttpMethod.Get && request.Headers.Range is not null ? "download" : "signed-session-metadata";
    }
    private sealed class ProbeContent : HttpContent
    {
        private readonly HttpContent _inner; private readonly TransferTrace _trace; private readonly string _file; private readonly string? _read, _write;
        public ProbeContent(HttpContent inner, TransferTrace trace, string file, string? read, string? write)
        { _inner = inner; _trace = trace; _file = file; _read = read; _write = write; foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength is not null; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var started = _trace.Now;
            try { await _inner.CopyToAsync(new ProbeStream(stream, _trace, _file, null, _write ?? _read), context, cancellationToken); }
            finally { _trace.Add(_file, _write is null ? "payload-body-read" : "payload-body-write", started, _trace.Now); }
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => WrapRead(_inner.ReadAsStream(cancellationToken));
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => WrapRead(await _inner.ReadAsStreamAsync(cancellationToken));
        private Stream WrapRead(Stream stream)
        {
            var started = _trace.Now;
            return new ProbeStream(stream, _trace, _file, _read, null, () => _trace.Add(_file, "payload-body-read", started, _trace.Now));
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}

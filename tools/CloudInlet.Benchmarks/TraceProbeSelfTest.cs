using System.Net;

namespace CloudInlet.Benchmarks;

internal static class TraceProbeSelfTest
{
    public static async Task<int> RunAsync()
    {
        var trace = new TransferTrace { Enabled = true };
        var loopback = new BodyLoopbackHandler();
        using var handler = new TracedHttpHandler(trace) { InnerHandler = loopback };
        using var http = new HttpClient(handler);
        using var original = new StringContent("bounded content probe");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.backblazeb2.invalid/b2api/v3/b2_upload_file") { Content = original };
        using (TraceContext.Enter("fixture.bin", "upload"))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            if (!ReferenceEquals(request.Content, original) || loopback.RequestBytes != original.Headers.ContentLength ||
                !trace.Events.Any(value => value.Stage == "payload-write") || !trace.Events.Any(value => value.Stage == "payload-body-write"))
                throw new InvalidDataException("The probe changed content identity/length or lost payload timing.");
        }
        using (TraceContext.Enter("fixture.bin", "verify"))
        using (var response = await http.GetAsync("https://api.backblazeb2.invalid/b2api/v3/b2_download_file_by_id", HttpCompletionOption.ResponseHeadersRead))
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var bytes = new byte[4096];
            if (await stream.ReadAsync(bytes) != 4096 || !trace.Events.Any(value => value.Stage == "payload-read"))
                throw new InvalidDataException("The download probe changed the replay stream or lost payload timing.");
        }
        if (!trace.Events.Any(value => value.Stage == "payload-body-read")) throw new InvalidDataException("The bounded download lifecycle was not measured.");
        using (var response = await http.GetAsync("https://api.backblazeb2.invalid/b2api/v3/b2_download_file_by_id", HttpCompletionOption.ResponseHeadersRead))
        using (var stream = response.Content.ReadAsStream())
        {
            if (stream.ReadByte() != 0) throw new InvalidDataException("The synchronous streaming probe changed the response.");
        }
        Console.WriteLine("Instrumentation self-test passed: original HTTP content identity/length restored; upload/download stream timings preserved; no network or disk payload operations.");
        return 0;
    }
    private sealed class BodyLoopbackHandler : HttpMessageHandler
    {
        public long RequestBytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            { using var sink = new MemoryStream(); await request.Content.CopyToAsync(sink, cancellationToken); RequestBytes = sink.Length; }
            return new(HttpStatusCode.OK) { Content = new ForwardOnlyContent() };
        }
    }
    private sealed class ForwardOnlyContent : HttpContent
    {
        private readonly MemoryStream _stream = new(new byte[4096], writable: false);
        protected override bool TryComputeLength(out long length) { length = 4096; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("A streaming response must be forwarded directly, not serialized into a buffer.");
        protected override Stream CreateContentReadStream(CancellationToken token) => _stream;
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(_stream);
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}

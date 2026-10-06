using CloudInlet.Core.B2;
using System.Net;

namespace CloudInlet.Core;

/// <summary>Directional byte and payload-request budgets shared by every provider and sync root in a client.</summary>
public sealed class TransferBandwidthBudget
{
    internal BandwidthLimiter Uploads { get; } = new();
    internal BandwidthLimiter Downloads { get; } = new();
    private readonly ConcurrencyGate _uploadRequests = new();
    private readonly ConcurrencyGate _downloadRequests = new();

    /// <summary>Apply the existing performance preferences across every connected provider.</summary>
    public void ConfigureRequestLimits(int uploadRequests, int downloadRequests)
    {
        ValidateRequests(uploadRequests);
        ValidateRequests(downloadRequests);
        _uploadRequests.Configure(uploadRequests);
        _downloadRequests.Configure(downloadRequests);
    }

    internal void ConfigureDownloadRequests(int requests)
    {
        ValidateRequests(requests);
        _downloadRequests.Configure(requests);
    }

    internal Task<IDisposable> EnterUploadAsync(CancellationToken token) => EnterAsync(_uploadRequests, token);
    internal Task<IDisposable> EnterDownloadAsync(CancellationToken token) => EnterAsync(_downloadRequests, token);

    private static async Task<IDisposable> EnterAsync(ConcurrencyGate gate, CancellationToken token)
    {
        await gate.EnterAsync(token).ConfigureAwait(false);
        return new RequestLease(gate);
    }

    private static void ValidateRequests(int requests)
    {
        if (requests is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(requests));
    }

    // Response ownership also owns admission. Forward the existing stream directly:
    // a wrapping HttpContent must never buffer a cloud response to implement ReadAsStream.
    internal static void AttachResponseLease(HttpResponseMessage response, IDisposable lease) =>
        response.Content = new LeasedContent(response.Content, lease);

    private sealed class RequestLease(ConcurrencyGate gate) : IDisposable
    {
        private ConcurrencyGate? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Exit();
    }

    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly IDisposable _lease;
        public LeasedContent(HttpContent content, IDisposable lease)
        {
            _content = content;
            _lease = lease;
            foreach (var header in content.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = _content.Headers.ContentLength ?? 0;
            return _content.Headers.ContentLength.HasValue;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _content.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            _content.CopyToAsync(stream, token);
        protected override Stream CreateContentReadStream(CancellationToken token) => _content.ReadAsStream(token);
        protected override Task<Stream> CreateContentReadStreamAsync() => _content.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => _content.ReadAsStreamAsync(token);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _content.Dispose(); }
                finally { _lease.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uploadBytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(downloadBytesPerSecond);
        Uploads.Configure(uploadBytesPerSecond);
        Downloads.Configure(downloadBytesPerSecond);
    }
}

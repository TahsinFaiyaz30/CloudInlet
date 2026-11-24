using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace CloudInlet.Core.B2;

// Serialize access to the shared caller-owned source while streaming multipart segments.
internal sealed class SegmentContent(Stream source, SemaphoreSlim sourceLock, long start, long length,
    string sha1, BandwidthLimiter limiter, Action<int>? progress, CancellationToken operationToken) : HttpContent
{
    internal TransferInactivity? Inactivity { get; set; }
    protected override bool TryComputeLength(out long result) { result = length; return true; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WriteAsync(stream, operationToken);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
        WriteAsync(stream, token);

    private async Task WriteAsync(Stream destination, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken, Inactivity?.Token ?? CancellationToken.None);
        var ct = linked.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long position = 0;
        try
        {
            while (position < length)
            {
                int read;
                await sourceLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    source.Position = checked(start + position);
                    read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - position)), ct).ConfigureAwait(false);
                }
                finally { sourceLock.Release(); }
                if (read == 0) throw new EndOfStreamException("The upload source ended before its declared length.");
                hash.AppendData(buffer, 0, read);
                // A deliberately low bandwidth cap is not a stalled network transfer.
                Inactivity?.Suspend();
                try { await limiter.WaitAsync(read, ct).ConfigureAwait(false); }
                finally { Inactivity?.Reset(); }
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                Inactivity?.Reset();
                position += read;
                progress?.Invoke(read);
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The upload source changed after its checksum was calculated.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

// Bounds inactivity across sending a request body and waiting for its acknowledgment, while
// allowing arbitrarily long transfers that continue making progress.

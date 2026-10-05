using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CloudBay.Core.Transfers;

namespace CloudBay.Core.B2;

// Four 64 KiB buffers overlap cloud reads with socket writes. The SHA1 trailer lets B2
// validate a non-seekable range in one pass; no file or payload-sized allocation is used.
internal sealed class ReplayableContent(ITransferSourceFile source, long offset, long length,
    BandwidthLimiter limiter, Action<int>? progress, CancellationToken operationToken) : HttpContent
{
    internal TransferInactivity? Inactivity { get; set; }
    internal string? Sha1 { get; private set; }
    internal long SentBytes { get; private set; }
    internal bool TrailerStarted { get; private set; }
    internal Exception? SourceError { get; private set; }
    protected override bool TryComputeLength(out long result) { result = checked(length + 40); return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, operationToken);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);

    private async Task WriteAsync(Stream destination, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken,
            Inactivity?.Token ?? CancellationToken.None);
        var ct = cancellation.Token;
        var channel = Channel.CreateBounded<(byte[] Buffer, int Count)>(new BoundedChannelOptions(4)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var prepare = ProduceAsync();
        try
        {
            await foreach (var block in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Inactivity?.Suspend();
                    try { await limiter.WaitAsync(block.Count, ct).ConfigureAwait(false); }
                    finally { Inactivity?.Reset(); }
                    await destination.WriteAsync(block.Buffer.AsMemory(0, block.Count), ct).ConfigureAwait(false);
                    Inactivity?.Reset();
                    SentBytes += block.Count;
                    progress?.Invoke(block.Count);
                }
                finally { ArrayPool<byte>.Shared.Return(block.Buffer); }
            }
            await prepare.ConfigureAwait(false);
            TrailerStarted = true;
            await destination.WriteAsync(Encoding.ASCII.GetBytes(Sha1!), ct).ConfigureAwait(false);
            Inactivity?.Reset();
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            try { await prepare.ConfigureAwait(false); } catch when (ct.IsCancellationRequested) { }
            while (channel.Reader.TryRead(out var block)) ArrayPool<byte>.Shared.Return(block.Buffer);
        }

        async Task ProduceAsync()
        {
            try
            {
                await using var input = await source.OpenReadAsync(offset, length, ct).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                long readTotal = 0;
                while (readTotal < length)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    var handedOff = false;
                    try
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(64 * 1024, length - readTotal)), ct).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("The cloud source ended before its selected range.");
                        hash.AppendData(buffer, 0, read);
                        readTotal += read;
                        await channel.Writer.WriteAsync((buffer, read), ct).ConfigureAwait(false);
                        handedOff = true;
                    }
                    finally { if (!handedOff) ArrayPool<byte>.Shared.Return(buffer); }
                }
                Sha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (offset == 0 && length == source.Entry.Size && source.Entry.Sha1 is { Length: 40 } expected &&
                    !expected.Equals(Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new TransferSourceChangedException("The source content no longer matches its saved checksum.");
                channel.Writer.TryComplete();
            }
            catch (Exception error) { SourceError = error; channel.Writer.TryComplete(error); throw; }
        }
    }
}

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CloudBay.Core.Transfers;

namespace CloudBay.Core.B2;

// Four queued 64 KiB buffers plus one producer and one consumer buffer overlap reads
// with socket writes. Preparation opens the range and queues its first block before
// B2 receives request headers. The SHA1 trailer lets B2
// validate a non-seekable range in one pass; no file or payload-sized allocation is used.
internal sealed class ReplayableContent(ITransferSourceFile source, long offset, long length,
    BandwidthLimiter limiter, Action<int>? progress, CancellationToken operationToken) : HttpContent, IAsyncDisposable
{
    private readonly Channel<(byte[] Buffer, int Count)> _channel = Channel.CreateBounded<(byte[] Buffer, int Count)>(new BoundedChannelOptions(4)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _firstBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _preparationCancellation;
    private Task? _producer;
    private bool _serializationStarted;
    internal TransferInactivity? Inactivity { get; set; }
    internal string? Sha1 { get; private set; }
    internal long SentBytes { get; private set; }
    internal bool TrailerStarted { get; private set; }
    internal Exception? SourceError { get; private set; }
    internal long? SourceOpenMilliseconds { get; private set; }
    internal long? FirstBlockMilliseconds { get; private set; }
    internal long? SourceReadMilliseconds { get; private set; }
    internal long? SendMilliseconds { get; set; }
    internal long PreparedBytes { get; private set; }

    internal void BeginPreparation(CancellationToken token)
    {
        if (_producer is not null) return;
        _preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken, token);
        _producer = ProduceAsync(_preparationCancellation.Token);
    }

    internal async Task PrepareAsync(CancellationToken token)
    {
        BeginPreparation(token);
        await _firstBlock.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal async Task StopPreparationAsync()
    {
        if (_preparationCancellation is not null) await _preparationCancellation.CancelAsync().ConfigureAwait(false);
        if (_producer is not null) { try { await _producer.ConfigureAwait(false); } catch { /* The transfer path owns the producer failure. */ } }
        if (_firstBlock.Task.IsFaulted) _ = _firstBlock.Task.Exception;
        while (_channel.Reader.TryRead(out var block)) ArrayPool<byte>.Shared.Return(block.Buffer);
    }

    public async ValueTask DisposeAsync()
    {
        await StopPreparationAsync().ConfigureAwait(false);
        _preparationCancellation?.Dispose();
        _preparationCancellation = null;
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _preparationCancellation?.Cancel();
        base.Dispose(disposing);
    }
    protected override bool TryComputeLength(out long result) { result = checked(length + 40); return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, operationToken);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);

    private async Task WriteAsync(Stream destination, CancellationToken token)
    {
        await PrepareAsync(token).ConfigureAwait(false);
        if (_serializationStarted) throw new InvalidOperationException("A cloud upload range must use a new replayable content instance for each attempt.");
        _serializationStarted = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken,
            Inactivity?.Token ?? CancellationToken.None, _preparationCancellation!.Token);
        var ct = cancellation.Token;
        try
        {
            await foreach (var block in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
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
            await _producer!.ConfigureAwait(false);
            TrailerStarted = true;
            await destination.WriteAsync(Encoding.ASCII.GetBytes(Sha1!), ct).ConfigureAwait(false);
            Inactivity?.Reset();
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await StopPreparationAsync().ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var input = await source.OpenReadAsync(offset, length, ct).ConfigureAwait(false);
            SourceOpenMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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
                    PreparedBytes = readTotal;
                    await _channel.Writer.WriteAsync((buffer, read), ct).ConfigureAwait(false);
                    handedOff = true;
                    if (FirstBlockMilliseconds is null)
                    {
                        FirstBlockMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _firstBlock.TrySetResult();
                    }
                }
                finally { if (!handedOff) ArrayPool<byte>.Shared.Return(buffer); }
            }
            Sha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (offset == 0 && length == source.Entry.Size && source.Entry.Sha1 is { Length: 40 } expected &&
                !expected.Equals(Sha1, StringComparison.OrdinalIgnoreCase))
                throw new TransferSourceChangedException("The source content no longer matches its saved checksum.");
            SourceReadMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _channel.Writer.TryComplete();
            _firstBlock.TrySetResult();
        }
        catch (Exception error) { SourceError = error; _channel.Writer.TryComplete(error); _firstBlock.TrySetException(error); throw; }
    }
}

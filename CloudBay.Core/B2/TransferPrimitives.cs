using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace CloudBay.Core.B2;

// Aggregate bandwidth limits apply across workers, rather than independently per file.
internal sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private long _rate;
    private long _last;
    private double _credit;

    public void Configure(long rate)
    {
        lock (_lock) { _rate = rate; _last = Stopwatch.GetTimestamp(); _credit = 0; }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken token)
    {
        var remaining = bytes;
        while (remaining > 0)
        {
            double seconds;
            lock (_lock)
            {
                if (_rate == 0) return;
                var now = Stopwatch.GetTimestamp();
                var capacity = Math.Max(1, _rate / 10d);
                _credit = Math.Min(capacity, _credit + (double)(now - _last) / Stopwatch.Frequency * _rate);
                _last = now;
                var take = (int)Math.Min(remaining, Math.Floor(_credit));
                _credit -= take;
                remaining -= take;
                if (remaining == 0) return;
                seconds = Math.Clamp((Math.Min(remaining, capacity) - _credit) / _rate, .001, .1);
            }
            // Respond promptly to cancellation and live settings changes, even under very low caps.
            await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        }
    }
}

internal sealed class ConcurrencyGate
{
    private readonly object _lock = new();
    private int _active;
    private int _limit = 4;
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Configure(int limit)
    {
        lock (_lock) { _limit = limit; Signal(); }
    }

    public async Task EnterAsync(CancellationToken token)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (_active < _limit) { _active++; return; }
                signal = _changed.Task;
            }
            await signal.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void Exit()
    {
        lock (_lock) { _active--; Signal(); }
    }

    private void Signal() { var old = _changed; _changed = NewSignal(); old.TrySetResult(); }
}

// Reads a segment under one source-position lock, allowing bounded streaming of multipart workers.
// The source and its position lock remain owned by the caller.
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
internal sealed class TransferInactivity : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TimeSpan _timeout;
    public CancellationToken Token => _cancellation.Token;
    public TransferInactivity(CancellationToken token, TimeSpan timeout)
    {
        _timeout = timeout;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Reset();
    }
    public void Reset() => _cancellation.CancelAfter(_timeout);
    public void Suspend() => _cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
    public void Dispose() => _cancellation.Dispose();
}

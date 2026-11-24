using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace CloudInlet.Core;

// Aggregate bandwidth limits apply across workers, rather than independently per file.
public sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private long _rate;
    private long _last;
    private double _credit;

    public void Configure(long rate)
    {
        lock (_lock)
        {
            if (_rate == rate) return;
            _rate = rate; _last = Stopwatch.GetTimestamp(); _credit = 0;
        }
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

public sealed class ConcurrencyGate
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

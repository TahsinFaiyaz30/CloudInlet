namespace CloudBay.Core;

/// <summary>Monotonic payload measurement with a short smoothing window and idle expiry.</summary>
public sealed class TransferSpeedMeter
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private long _lastBytes, _sampleBytes, _sampleStarted, _lastPayload;
    private double _rate;

    public TransferSpeedMeter(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Reset(0);
    }

    public void Reset(long baseline)
    {
        lock (_gate)
        {
            _lastBytes = Math.Max(0, baseline); _sampleBytes = 0; _rate = 0;
            _sampleStarted = _lastPayload = _clock.GetTimestamp();
        }
    }

    public double Sample(long cumulativeBytes)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (cumulativeBytes < _lastBytes) { Reset(cumulativeBytes); return 0; }
            var added = cumulativeBytes - _lastBytes;
            _lastBytes = cumulativeBytes;
            if (added > 0 && _clock.GetElapsedTime(_lastPayload, now).TotalSeconds >= 5)
            {
                // Idle time is outside a new transfer burst's measurement.
                // Start fresh without losing its first payload sample or
                // incorporating the rate from a previous file or request.
                _sampleBytes = 0; _sampleStarted = now; _rate = 0;
            }
            if (added > 0) { _sampleBytes += added; _lastPayload = now; }
            var seconds = _clock.GetElapsedTime(_sampleStarted, now).TotalSeconds;
            // Avoid flickering rates from individual buffer writes or sub-millisecond files.
            if (seconds >= .25)
            {
                var measured = _sampleBytes / seconds;
                var weight = 1 - Math.Exp(-seconds / 1.5);
                _rate = _rate <= 0 ? measured : _rate + weight * (measured - _rate);
                _sampleBytes = 0; _sampleStarted = now;
            }
            return Read(now);
        }
    }

    public double BytesPerSecond
    {
        get { lock (_gate) return Read(_clock.GetTimestamp()); }
    }

    private double Read(long now)
    {
        var idle = _clock.GetElapsedTime(_lastPayload, now).TotalSeconds;
        if (idle >= 5 || _rate <= 0) return 0;
        return _rate * Math.Exp(-Math.Max(0, idle - 1) / 1.5);
    }
}

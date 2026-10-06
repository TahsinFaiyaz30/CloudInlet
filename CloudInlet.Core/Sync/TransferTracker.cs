using System.Diagnostics;

namespace CloudInlet.Core.Sync;

/// <summary>Per-root transfer state. Only a bounded, immutable window is published to the UI.</summary>
public sealed class TransferTracker(string rootPath, string rootName, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, TransferSnapshot> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferSpeedMeter> _speeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransferSpeedMeter _uploadSpeed = new(clock);
    private readonly TransferSpeedMeter _downloadSpeed = new(clock);
    private long _uploadPayload, _downloadPayload;
    private long _completedBytes;
    private long _totalBytes;
    private long _progressBytes;
    private long _lastProgress;
    private bool _paused;
    public void Reset()
    {
        lock (_gate)
        {
            _items.Clear(); _active.Clear(); _speeds.Clear();
            _completedBytes = _totalBytes = _progressBytes = _lastProgress = _uploadPayload = _downloadPayload = 0;
            _uploadSpeed.Reset(0); _downloadSpeed.Reset(0); _paused = false;
        }
    }
    private string Id(string path, ActivityKind kind) => rootPath + "|" + kind + "|" + path;
    public void Queue(IEnumerable<(string Path, ActivityKind Kind, long Size)> files)
    {
        lock (_gate)
            foreach (var (path, kind, size) in files)
            {
                var id = Id(path, kind);
                if (_items.ContainsKey(id)) continue;
                _items.Add(id, new(id, rootName, path, kind, _paused ? TransferPhase.Paused : TransferPhase.Queued, 0, size));
                _totalBytes += size;
            }
    }
    public void Phase(string path, ActivityKind kind, TransferPhase phase)
    {
        lock (_gate)
        {
            var id = Id(path, kind);
            if (!_items.TryGetValue(id, out var item)) return;
            if (_paused) phase = TransferPhase.Paused;
            if (phase is TransferPhase.Uploading or TransferPhase.Downloading && item.Phase != phase)
            {
                if (!_speeds.TryGetValue(id, out var speed)) _speeds[id] = speed = new(_clock);
                speed.Reset(item.Bytes);
            }
            _items[id] = item with { Phase = phase };
            if (phase is TransferPhase.Queued or TransferPhase.Paused or TransferPhase.Retrying) _active.Remove(id);
            else _active.Add(id);
        }
    }
    public bool Progress(string path, ActivityKind kind, TransferProgress value)
    {
        lock (_gate)
        {
            var id = Id(path, kind);
            if (!_items.TryGetValue(id, out var item)) return false;
            _totalBytes += value.TotalBytes - item.TotalBytes;
            var bytes = Math.Clamp(value.Bytes, 0, Math.Max(0, value.TotalBytes));
            if (!_paused && _speeds.TryGetValue(id, out var speed))
            {
                if (value.IsBaseline)
                {
                    speed.Reset(bytes);
                    if (kind == ActivityKind.Upload && _uploadPayload == 0) _uploadSpeed.Reset(0);
                    else if (kind == ActivityKind.Download && _downloadPayload == 0) _downloadSpeed.Reset(0);
                }
                else speed.Sample(bytes);
                var added = value.IsBaseline ? 0 : Math.Max(0, bytes - item.Bytes);
                if (kind == ActivityKind.Upload) { _uploadPayload += added; _uploadSpeed.Sample(_uploadPayload); }
                else if (kind == ActivityKind.Download) { _downloadPayload += added; _downloadSpeed.Sample(_downloadPayload); }
            }
            _progressBytes += bytes - item.Bytes;
            _items[id] = item with { Bytes = bytes, TotalBytes = value.TotalBytes };
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastProgress, now) < TimeSpan.FromMilliseconds(250)) return false;
            _lastProgress = now;
            return true;
        }
    }
    public void Complete(string path, ActivityKind kind)
    {
        lock (_gate)
        {
            var id = Id(path, kind);
            if (_items.Remove(id, out var item)) { _completedBytes += item.TotalBytes; _progressBytes -= item.Bytes; }
            _active.Remove(id);
            _speeds.Remove(id);
        }
    }
    public void Discard(string path, ActivityKind kind)
    {
        lock (_gate)
        {
            var id = Id(path, kind);
            if (_items.Remove(id, out var item)) { _totalBytes -= item.TotalBytes; _progressBytes -= item.Bytes; }
            _active.Remove(id);
            _speeds.Remove(id);
        }
    }
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            foreach (var id in _items.Keys.ToArray()) _items[id] = _items[id] with { Phase = TransferPhase.Paused };
            _active.Clear();
            _uploadSpeed.Reset(_uploadPayload); _downloadSpeed.Reset(_downloadPayload);
        }
    }
    public SyncSnapshot Apply(SyncSnapshot snapshot)
    {
        lock (_gate)
        {
            var active = _active.Select(id => _items[id] with
            {
                BytesPerSecond = _items[id].Phase is TransferPhase.Uploading or TransferPhase.Downloading &&
                    _speeds.TryGetValue(id, out var speed) ? speed.BytesPerSecond : 0
            }).ToArray();
            var window = active.Take(256).Concat(_items.Values.Where(item => !_active.Contains(item.Id)).Take(Math.Max(0, 256 - active.Length))).ToArray();
            return snapshot with { Transfers = window, ActiveTransfers = active.Length,
                QueuedTransfers = _items.Count - active.Length,
                UploadBytesPerSecond = !_paused && _items.Values.Any(item => item.Kind == ActivityKind.Upload && item.Phase == TransferPhase.Uploading) ? _uploadSpeed.BytesPerSecond : 0,
                DownloadBytesPerSecond = !_paused && _items.Values.Any(item => item.Kind == ActivityKind.Download && item.Phase == TransferPhase.Downloading) ? _downloadSpeed.BytesPerSecond : 0,
                TransferredBytes = _completedBytes + _progressBytes, TransferTotalBytes = _totalBytes };
        }
    }
}

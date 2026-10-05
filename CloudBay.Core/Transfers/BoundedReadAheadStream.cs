using System.Buffers;
using System.Threading.Channels;

namespace CloudBay.Core.Transfers;

/// <summary>Overlaps remote reads with upload writes. At most four 256 KiB buffers per stream.</summary>
internal sealed class BoundedReadAheadStream : Stream
{
    private const int BufferSize = 256 * 1024;
    private readonly Stream _source;
    private readonly CancellationTokenSource _cancellation;
    private readonly Channel<Block> _ready = Channel.CreateBounded<Block>(new BoundedChannelOptions(2)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _producer;
    private Block? _current;
    private int _position;
    private bool _disposed;

    public BoundedReadAheadStream(Stream source, CancellationToken cancellationToken)
    {
        _source = source;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _producer = ProduceAsync();
    }

    private async Task ProduceAsync()
    {
        byte[]? buffer = null;
        try
        {
            while (await _ready.Writer.WaitToWriteAsync(_cancellation.Token).ConfigureAwait(false))
            {
                buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                var length = await _source.ReadAsync(buffer.AsMemory(0, BufferSize), _cancellation.Token).ConfigureAwait(false);
                if (length == 0) break;
                await _ready.Writer.WriteAsync(new(buffer, length), _cancellation.Token).ConfigureAwait(false);
                buffer = null;
            }
            _ready.Writer.TryComplete();
        }
        catch (Exception error) { _ready.Writer.TryComplete(error); }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length == 0) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        if (_current is null)
        {
            if (!await _ready.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) return 0;
            _current = await _ready.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            _position = 0;
        }
        var count = Math.Min(destination.Length, _current.Length - _position);
        _current.Buffer.AsMemory(_position, count).CopyTo(destination);
        _position += count;
        if (_position == _current.Length)
        {
            ArrayPool<byte>.Shared.Return(_current.Buffer, clearArray: true);
            _current = null;
        }
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        await _producer.ConfigureAwait(false);
        if (_current is not null) { ArrayPool<byte>.Shared.Return(_current.Buffer, clearArray: true); _current = null; }
        while (_ready.Reader.TryRead(out var block)) ArrayPool<byte>.Shared.Return(block.Buffer, clearArray: true);
        await _source.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    private sealed record Block(byte[] Buffer, int Length);
}

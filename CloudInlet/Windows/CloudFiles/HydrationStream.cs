using System.Buffers;
using System.Runtime.InteropServices;
using static CloudInlet.Windows.CloudFiles.CloudFilesNative;

namespace CloudInlet.Windows.CloudFiles;

// HTTP clients may write arbitrary chunk sizes. Buffer one aligned block, never the whole file.
internal sealed class HydrationStream(OperationInfo operation, long offset, long length, CancellationToken cancellationToken) : Stream
{
    private const int BufferSize = 256 * 1024;
    private byte[]? _buffer;
    private long _transferred;
    private int _buffered;
    private bool _complete;
    private bool _disposed;
    private readonly TaskCompletionSource _validationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task ValidationCompletion => _validationCompletion.Task;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_complete && !_disposed;
    public override long Length => length;
    public override long Position { get => _transferred + _buffered; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int start, int count) => Write(buffer.AsSpan(start, count));
    public override void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_complete) throw new InvalidOperationException("The hydration request has already completed.");
        if (data.Length > length - Position) throw new IOException("The cloud download exceeded its requested range.");
        // The controller publishes queued callbacks before buffer/transport admission. Constructing
        // a waiting stream therefore must not reserve a block for every outstanding Shell request.
        var buffer = _buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
        while (!data.IsEmpty)
        {
            var size = Math.Min(data.Length, BufferSize - _buffered);
            data[..size].CopyTo(buffer.AsSpan(_buffered));
            _buffered += size;
            data = data[size..];
            // Keep the final block until the transport has checked the remote checksum and
            // returned successfully. Publishing an aligned EOF here would otherwise release
            // the native file reader before DownloadAsync can report corrupted content.
            if (_buffered == BufferSize && Position < length) Transfer();
        }
    }
    public override Task WriteAsync(byte[] buffer, int start, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Write(buffer, start, count);
        return Task.CompletedTask;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Write(data.Span);
        return ValueTask.CompletedTask;
    }
    internal void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Position != length) throw new EndOfStreamException("The cloud download ended before the requested range was complete.");
        if (_buffered > 0) Transfer();
        _complete = true;
        ReleaseBuffer();
    }
    internal void MarkValidated() => _validationCompletion.TrySetResult();
    internal void MarkValidationFailed(Exception error)
    {
        if (error is OperationCanceledException cancelled) _validationCompletion.TrySetCanceled(cancelled.CancellationToken);
        else _validationCompletion.TrySetException(error);
    }
    private void Transfer()
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pinned = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            var parameters = new TransferParameters
            {
                ParamSize = 40, Buffer = pinned.AddrOfPinnedObject(), Offset = offset + _transferred, Length = _buffered,
            };
            Check(CfExecute(operation, parameters));
            _transferred += _buffered;
            _buffered = 0;
            _ = CfReportProviderProgress(operation.ConnectionKey, operation.TransferKey, length, _transferred);
        }
        finally { pinned.Free(); }
    }
    // Flush must not publish an unaligned HTTP chunk; Complete handles the only permissible EOF tail.
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        ReleaseBuffer();
        base.Dispose(disposing);
    }
    private void ReleaseBuffer()
    {
        var buffer = _buffer;
        _buffer = null;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
    public override int Read(byte[] buffer, int start, int count) => throw new NotSupportedException();
    public override long Seek(long position, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

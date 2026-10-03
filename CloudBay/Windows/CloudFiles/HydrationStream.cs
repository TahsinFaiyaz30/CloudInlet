using System.Runtime.InteropServices;
using static CloudBay.Windows.CloudFiles.CloudFilesNative;

namespace CloudBay.Windows.CloudFiles;

// HTTP clients may write arbitrary chunk sizes. Buffer one aligned block, never the whole file.
internal sealed class HydrationStream(OperationInfo operation, long offset, long length, CancellationToken cancellationToken) : Stream
{
    private readonly byte[] _buffer = new byte[256 * 1024];
    private long _transferred;
    private int _buffered;
    private bool _complete;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_complete;
    public override long Length => length;
    public override long Position { get => _transferred + _buffered; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int start, int count) => Write(buffer.AsSpan(start, count));
    public override void Write(ReadOnlySpan<byte> data)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_complete) throw new InvalidOperationException("The hydration request has already completed.");
        if (data.Length > length - Position) throw new IOException("The cloud download exceeded its requested range.");
        while (!data.IsEmpty)
        {
            var size = Math.Min(data.Length, _buffer.Length - _buffered);
            data[..size].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += size;
            data = data[size..];
            if (_buffered == _buffer.Length) Transfer();
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
        cancellationToken.ThrowIfCancellationRequested();
        if (Position != length) throw new EndOfStreamException("The cloud download ended before the requested range was complete.");
        if (_buffered > 0) Transfer();
        _complete = true;
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
    public override int Read(byte[] buffer, int start, int count) => throw new NotSupportedException();
    public override long Seek(long position, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

using System.Security.Cryptography;
using CloudInlet.Core;
using CloudInlet.Core.Transfers;

namespace CloudInlet.Benchmarks;

internal sealed class GeneratedSource : ITransferSourceFile
{
    public static readonly DateTimeOffset Modified = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    public TransferEntry Entry { get; }
    private readonly bool _delayed;
    public GeneratedSource(string name, long size, string hash, bool delayed = false)
    { Entry = new("generated:" + name, name, "fixture-v1", size, Modified, hash); _delayed = delayed; }
    public GeneratedSource(TransferEntry entry, bool delayed) { Entry = entry; _delayed = delayed; }
    public Task ValidateAsync(CancellationToken cancellationToken = default) => _delayed ? Task.Delay(20, cancellationToken) : Task.CompletedTask;
    public async Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
    { if (_delayed) await Task.Delay(40, cancellationToken); return new GeneratedStream(offset, length, _delayed); }
    public static async Task<string> HashAsync(long size, CancellationToken token)
    { await using var stream = new GeneratedStream(0, size, false); return Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).ToLowerInvariant(); }
    private sealed class GeneratedStream(long offset, long length, bool delayed) : Stream
    {
        private long _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count) => Fill(buffer.AsSpan(start, count));
        private int Fill(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++) buffer[index] = (byte)((offset + _position + index) * 17 + 43);
            _position += count; return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (delayed && _position < length) await Task.Delay(5, cancellationToken); return Fill(buffer.Span); }
        public override void Flush() { } public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}

internal sealed class SchedulerEndpoint(TransferLocation location, IReadOnlyList<TransferEntry> entries, TransferTrace trace, bool destination) : ITransferEndpoint
{
    private readonly Dictionary<string, TransferReceipt> _receipts = new();
    public TransferLocation Location => location;
    public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new TransferFolderPage([], null));
    public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new TransferDiscoveryPage(entries, null));
    public ITransferSourceFile OpenSource(TransferEntry entry) => new GeneratedSource(entry, delayed: true);
    public async Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, CancellationToken cancellationToken = default)
    { await Task.Delay(75, cancellationToken); return null; }
    public async Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, Func<TransferCheckpoint, CancellationToken, Task> saveCheckpoint, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!destination) throw new InvalidOperationException();
        await source.ValidateAsync(cancellationToken);
        await using var input = await source.OpenReadAsync(0, source.Entry.Size, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[64 * 1024]; long copied = 0;
        while (copied < source.Entry.Size)
        {
            var started = trace.Now; var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, source.Entry.Size - copied)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read); await Task.Delay(8, cancellationToken);
            trace.Add(source.Entry.RelativePath, "simulated-payload", started, trace.Now, read); copied += read; progress?.Report(new(copied, source.Entry.Size));
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (digest != source.Entry.Sha1) throw new InvalidDataException("Generated content changed.");
        var receipt = new TransferReceipt("receipt:" + request.OperationId, request.RelativePath, "ack-v1", source.Entry.Size, digest, request.OperationId);
        await saveCheckpoint(new("b2", "", copied), cancellationToken);
        lock (_receipts) _receipts.Add(receipt.Id, receipt);
        return receipt;
    }
    public async Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken cancellationToken = default)
    {
        await Task.Delay(140, cancellationToken);
        lock (_receipts) if (_receipts.GetValueOrDefault(receipt.Id) != receipt || receipt.Sha1 != source.Entry.Sha1) throw new InvalidDataException("Simulated verified receipt changed.");
    }
    public Task DeleteSourceAsync(TransferEntry entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

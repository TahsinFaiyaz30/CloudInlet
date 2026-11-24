using System.Security.Cryptography;
using CloudInlet.Connectors;
using CloudInlet.Core;
using CloudInlet.Core.Transfers;

namespace ExampleConnector;

/// <summary>A read-only provider with immutable content. Copy its file to a test folder using Cloud transfer.</summary>
public sealed class ExampleProvider : IProviderConnector, IConnectorSession
{
    public string Id => "example.readonly";
    public ConnectorCapabilities Capabilities => ConnectorCapabilities.Browse | ConnectorCapabilities.Read | ConnectorCapabilities.Checksums;
    public IConnectorSession Open(ConnectorContext context) => this;
    public Task<IReadOnlyList<TransferLocation>> GetRootsAsync(string accountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TransferLocation>>([new(Id, accountId, "samples", "root", "", "Sample files")]);
    public ITransferEndpoint OpenEndpoint(TransferLocation location) => new Endpoint(location);

    private sealed class Endpoint(TransferLocation location) : ITransferEndpoint, ITransferSourceFile
    {
        private static readonly byte[] Bytes = "Hello from a private CloudInlet connector!\n"u8.ToArray();
        public TransferLocation Location => location;
        public TransferEntry Entry { get; } = new("welcome", "welcome.txt", "v1", Bytes.Length, DateTimeOffset.UnixEpoch,
            Convert.ToHexString(SHA1.HashData(Bytes)).ToLowerInvariant());
        public bool HasContentBoundVersion => true;
        public Task<TransferFolderPage> BrowseFoldersAsync(string? cursor, CancellationToken ct) => Task.FromResult(new TransferFolderPage([], null));
        public Task<TransferDiscoveryPage> DiscoverAsync(string? cursor, CancellationToken ct) => Task.FromResult(new TransferDiscoveryPage([Entry], null));
        public ITransferSourceFile OpenSource(TransferEntry entry) => entry == Entry ? this : throw new TransferSourceChangedException("Sample changed.");
        public Task ValidateAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<Stream> OpenReadAsync(long offset, long length, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(Bytes, checked((int)offset), checked((int)length), writable: false));
        }
        public Task DeleteSourceAsync(TransferEntry entry, CancellationToken ct) => throw new NotSupportedException();
        public Task<TransferReceipt?> ReconcileAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<TransferReceipt> UploadAsync(TransferUploadRequest request, ITransferSourceFile source, TransferCheckpoint? checkpoint,
            Func<TransferCheckpoint, CancellationToken, Task> save, IProgress<TransferProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task VerifyAsync(TransferReceipt receipt, ITransferSourceFile source, CancellationToken ct) => throw new NotSupportedException();
    }
}

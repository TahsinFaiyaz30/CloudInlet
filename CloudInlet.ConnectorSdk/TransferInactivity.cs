using System.Diagnostics;

namespace CloudInlet.Core;

public sealed class TransferInactivity : IDisposable
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

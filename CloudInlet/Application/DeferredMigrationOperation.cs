using CloudInlet.Core.Migration;

namespace CloudInlet.Application;

/// <summary>Opens protected adapter receipts inside a visible, recoverable session stage.</summary>
internal sealed class DeferredMigrationOperation(string id, string fingerprint, Func<IMigrationOperation> create,
    Func<bool> canRecover, Func<CancellationToken, Task> acknowledgeUnstarted, Func<bool> isUnstarted) : IMigrationOperation, IKeepOriginalMigrationOperation, IDisposable
{
    private IMigrationOperation? _operation;
    public string ItemId => id;
    public string ReviewedPlanFingerprint => fingerprint;
    public bool CanRevert => _operation?.CanRevert ?? (canRecover() || isUnstarted());
    public bool CanKeepOriginal
    {
        get { try { return Operation is IKeepOriginalMigrationOperation { CanKeepOriginal: true }; } catch { return false; } }
    }
    private IMigrationOperation Operation => _operation ??= create();
    public Task CheckAsync(CancellationToken ct) => Operation.CheckAsync(ct);
    public Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken ct) => Operation.ApplyAsync(progress, ct);
    public Task VerifyAsync(CancellationToken ct) => Operation.VerifyAsync(ct);
    public Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken ct) =>
        _operation is null && isUnstarted() ? acknowledgeUnstarted(ct) : Operation.RevertAsync(progress, ct);
    public Task VerifyRevertedAsync(CancellationToken ct) =>
        _operation is null && isUnstarted() ? acknowledgeUnstarted(ct) : Operation.VerifyRevertedAsync(ct);
    public Task KeepOriginalAsync(IProgress<MigrationProgress> progress, CancellationToken ct) =>
        Operation is IKeepOriginalMigrationOperation recovery ? recovery.KeepOriginalAsync(progress, ct) :
            throw new InvalidOperationException("This change cannot keep its original location.");
    public Task VerifyOriginalKeptAsync(CancellationToken ct) =>
        Operation is IKeepOriginalMigrationOperation recovery ? recovery.VerifyOriginalKeptAsync(ct) :
            throw new InvalidOperationException("This change has no retained-original recovery record.");
    public void Dispose() { (_operation as IDisposable)?.Dispose(); }
}

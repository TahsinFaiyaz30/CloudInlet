using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CloudBay.Core.B2;

namespace CloudBay.Core.Transfers;

/// <summary>One durable, bounded pipeline for local/cloud and cloud/cloud jobs.</summary>
public sealed class TransferJobEngine : IAsyncDisposable
{
    private readonly TransferJobJournal _journal;
    private readonly Func<TransferLocation, ITransferEndpoint> _endpointFactory;
    private const int PipelineWorkers = 16;
    private readonly ConcurrencyGate _transferAdmission = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningJob> _running = new(StringComparer.Ordinal);
    private bool _disposed;
    public event Action<TransferJobSnapshot>? Changed;
    public event Action<ActivityEvent>? Activity;

    public TransferJobEngine(TransferJobJournal journal, Func<TransferLocation, ITransferEndpoint> endpointFactory, int workers = 4)
    {
        if (workers is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(workers));
        _journal = journal; _endpointFactory = endpointFactory;
        _transferAdmission.Configure(workers);
    }

    /// <summary>Apply transfer preferences to running jobs without discarding their queues or checkpoints.</summary>
    public void ConfigureWorkers(int workers)
    {
        if (workers is < 1 or > PipelineWorkers) throw new ArgumentOutOfRangeException(nameof(workers));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _transferAdmission.Configure(workers);
        }
    }

    public Task CreateAsync(TransferJobPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _journal.Create(plan); }
        Publish(plan.Id);
        return Task.CompletedTask;
    }

    public Task RunAsync(string jobId, CancellationToken cancellationToken = default) => Start(jobId, false, cancellationToken);
    public Task ResumeAsync(string jobId, CancellationToken cancellationToken = default) => Start(jobId, true, cancellationToken);

    private Task Start(string jobId, bool retryFailures, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running.TryGetValue(jobId, out var existing)) return existing.Task;
            var job = _journal.GetJob(jobId);
            if (job.State == TransferJobState.Completed) return Task.CompletedTask;
            if (job.State == TransferJobState.Cancelled && !retryFailures) return Task.CompletedTask;
            var runtime = new RunningJob(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _running.Add(jobId, runtime);
            runtime.Task = Task.Run(() => ExecuteAsync(jobId, runtime, retryFailures), CancellationToken.None);
            return runtime.Task;
        }
    }

    public Task PauseAsync(string jobId) => StopAsync(jobId, TransferJobState.Paused);
    public Task CancelAsync(string jobId) => StopAsync(jobId, TransferJobState.Cancelled);
    private async Task StopAsync(string jobId, TransferJobState state)
    {
        Task? work;
        lock (_gate)
        {
            _journal.GetJob(jobId);
            if (_journal.GetJob(jobId).State == TransferJobState.Completed) return;
            _journal.SetState(jobId, state);
            work = null;
            if (_running.TryGetValue(jobId, out var runtime))
            {
                runtime.StopState = state;
                runtime.Cancellation.Cancel();
                work = runtime.Task;
            }
        }
        if (work is not null) await work.ConfigureAwait(false);
        Publish(jobId);
    }

    public IReadOnlyList<TransferJobSnapshot> Snapshots() => _journal.Snapshots().Select(ApplyLive).ToArray();

    private async Task ExecuteAsync(string jobId, RunningJob runtime, bool retryFailures)
    {
        var token = runtime.Cancellation.Token;
        try
        {
            var job = _journal.GetJob(jobId);
            var source = _endpointFactory(job.Plan.Source);
            var destination = _endpointFactory(job.Plan.Destination);
            if (source.Location != job.Plan.Source || destination.Location != job.Plan.Destination)
                throw new InvalidDataException("A saved transfer resolved to a different account or folder.");
            _journal.ResetUnfinished(jobId, retryFailures);
            _journal.SetState(jobId, job.DiscoveryComplete ? TransferJobState.Running : TransferJobState.Discovering);
            Publish(jobId);
            var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(PipelineWorkers)
            { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = false, SingleWriter = true });
            var verification = Channel.CreateBounded<VerificationWork>(new BoundedChannelOptions(PipelineWorkers * 2)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
            var discoveryDone = job.DiscoveryComplete ? 1 : 0;

            async Task DiscoverAsync()
            {
                try
                {
                    var saved = job;
                    while (!saved.DiscoveryComplete)
                    {
                        token.ThrowIfCancellationRequested();
                        var page = await source.DiscoverAsync(saved.Cursor, job.Plan.Exclusions, token).ConfigureAwait(false);
                        _journal.SaveDiscovery(jobId, page);
                        saved = _journal.GetJob(jobId);
                        for (var i = 0; i < PipelineWorkers; i++) wake.Writer.TryWrite(true);
                        Publish(jobId);
                    }
                    _journal.SetState(jobId, TransferJobState.Running);
                }
                finally
                {
                    Volatile.Write(ref discoveryDone, 1);
                    wake.Writer.TryComplete();
                }
            }

            async Task TransferAsync()
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var item = _journal.Claim(jobId);
                    if (item is null)
                    {
                        if (Volatile.Read(ref discoveryDone) != 0) return;
                        if (!await wake.Reader.WaitToReadAsync(token).ConfigureAwait(false)) continue;
                        wake.Reader.TryRead(out _);
                        continue;
                    }
                    var live = new LiveItem(item.Entry, item.Bytes);
                    runtime.Items[item.Entry.Id] = live;
                    try
                    {
                        var file = new ReadAheadSource(source.OpenSource(item.Entry));
                        var request = Request(job.Plan, item.Entry);
                        if (item.Receipt is not null)
                        {
                            TransferValidation.ValidateReceipt(item.Entry, item.Receipt, request.OperationId);
                            if (!TransferConflictNames.IsAllowedTarget(request, item.Receipt.RelativePath, job.Plan.Destination.Provider))
                                throw new InvalidDataException("The saved receipt points to a different destination path.");
                            live.State = TransferItemState.Verifying;
                            await verification.Writer.WriteAsync(new(item, file), token).ConfigureAwait(false);
                            continue;
                        }
                        TransferReceipt? receipt;
                        await _transferAdmission.EnterAsync(token).ConfigureAwait(false);
                        try
                        {
                            await file.ValidateAsync(token).ConfigureAwait(false);
                            await TransferResources.RelayTransfers.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                                live.State = TransferItemState.Transferring;
                                receipt = await destination.ReconcileAsync(request, file, item.Checkpoint, token).ConfigureAwait(false);
                                receipt ??= await destination.UploadAsync(request, file, item.Checkpoint, (checkpoint, ct) =>
                                {
                                    ct.ThrowIfCancellationRequested();
                                    if (checkpoint.Provider != job.Plan.Destination.Provider) throw new InvalidDataException("A checkpoint belongs to a different provider.");
                                    _journal.SaveCheckpoint(jobId, item.Entry, checkpoint);
                                    live.AcknowledgedBytes = checkpoint.AcknowledgedBytes;
                                    return Task.CompletedTask;
                                }, new InlineProgress(value => Progress(runtime, live, value)), token).ConfigureAwait(false);
                            }
                            finally { TransferResources.RelayTransfers.Release(); }
                        }
                        finally { _transferAdmission.Exit(); }
                        if (!TransferConflictNames.IsAllowedTarget(request, receipt.RelativePath, job.Plan.Destination.Provider))
                            throw new InvalidDataException("The destination created an unexpected file name.");
                        // An acknowledged receipt is durable before source revalidation or expensive verification.
                        _journal.SaveReceipt(jobId, item.Entry, receipt, request.OperationId);
                        live.Bytes = live.AcknowledgedBytes = item.Entry.Size;
                        live.State = TransferItemState.Verifying;
                        await verification.Writer.WriteAsync(new(item with { Receipt = receipt, Bytes = item.Entry.Size }, file), token).ConfigureAwait(false);
                    }
                    catch (TransferSkippedException)
                    {
                        _journal.Finish(jobId, item.Entry, skipped: true);
                        runtime.Items.TryRemove(item.Entry.Id, out _);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { Fail(jobId, item.Entry, error, runtime); }
                }
            }

            async Task VerifyAsync()
            {
                await foreach (var work in verification.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var item = work.Item;
                    try
                    {
                        var file = work.Source;
                        if (item.Receipt is null) throw new InvalidDataException("Verification requires a durable destination receipt.");
                        if (item.DeleteStarted && item.Verified && await source.IsSourceDeletedAsync(item.Entry, token).ConfigureAwait(false))
                        {
                            _journal.Finish(jobId, item.Entry);
                            runtime.Items.TryRemove(item.Entry.Id, out _);
                            continue;
                        }
                        await file.ValidateAsync(token).ConfigureAwait(false);
                        await TransferResources.Verification.WaitAsync(token).ConfigureAwait(false);
                        TransferReceipt verifiedReceipt;
                        try { verifiedReceipt = await destination.VerifyReceiptAsync(item.Receipt, file, token).ConfigureAwait(false); }
                        finally { TransferResources.Verification.Release(); }
                        await file.ValidateAsync(token).ConfigureAwait(false);
                        if (verifiedReceipt != item.Receipt)
                            _journal.SaveReceipt(jobId, item.Entry, verifiedReceipt, Request(job.Plan,item.Entry).OperationId);
                        if (job.Plan.Operation == TransferOperation.Move && !item.Entry.IsFolder)
                        {
                            _journal.SetItemState(jobId, item.Entry.Id, TransferItemState.DeletingSource);
                            if (runtime.Items.TryGetValue(item.Entry.Id, out var live)) live.State = TransferItemState.DeletingSource;
                            await source.DeleteSourceAsync(item.Entry with { Sha1 = item.Entry.Sha1 ?? verifiedReceipt.Sha1 }, token).ConfigureAwait(false);
                        }
                        _journal.Finish(jobId, item.Entry);
                        runtime.Items.TryRemove(item.Entry.Id, out _);
                        var message = job.Plan.Operation == TransferOperation.Move ? "Cloud transfer verified; unchanged source file moved" : "Transfer completed and verified";
                        if (job.Plan.Destination.Provider == "local" && verifiedReceipt.Data?.GetValueOrDefault("recoveryRelativePath") is { } original)
                            message += ". Previous local copy retained at " + Path.Combine(job.Plan.Destination.Path, original.Replace('/', Path.DirectorySeparatorChar));
                        Activity?.Invoke(new(DateTimeOffset.UtcNow, job.Plan.Destination.Provider == "local" ? ActivityKind.Download : ActivityKind.Upload, item.Entry.RelativePath,
                            message,
                            item.Entry.Size) { TransferJobId = jobId });
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { Fail(jobId, item.Entry, error, runtime); }
                }
            }

            async Task ProduceAsync()
            {
                try { await Task.WhenAll(Enumerable.Range(0, PipelineWorkers).Select(_ => GuardAsync(TransferAsync)).Append(GuardAsync(DiscoverAsync))).ConfigureAwait(false); }
                catch { runtime.Cancellation.Cancel(); throw; }
                finally { verification.Writer.TryComplete(); }
            }
            async Task RefreshAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                try { while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) Publish(jobId); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            var refresh = RefreshAsync();
            try
            {
                var consumers = Enumerable.Range(0, 8).Select(_ => GuardAsync(VerifyAsync)).ToArray();
                await Task.WhenAll(consumers.Append(ProduceAsync())).ConfigureAwait(false);
                _journal.SetState(jobId, _journal.HasUnfinished(jobId) ? TransferJobState.Attention : TransferJobState.Completed,
                    _journal.HasUnfinished(jobId) ? "Some files need attention. Completed copies and checkpoints are retained." : null);
            }
            finally { runtime.Cancellation.Cancel(); await refresh.ConfigureAwait(false); }
            async Task GuardAsync(Func<Task> work)
            {
                try { await work().ConfigureAwait(false); }
                catch { runtime.Cancellation.Cancel(); throw; }
            }
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            _journal.ResetUnfinished(jobId, false);
            _journal.SetState(jobId, runtime.StopState ?? TransferJobState.Paused);
        }
        catch (Exception error)
        {
            _journal.ResetUnfinished(jobId, false);
            _journal.SetState(jobId, TransferJobState.Attention, error.Message);
            Activity?.Invoke(new(DateTimeOffset.UtcNow, ActivityKind.Error, jobId, error.Message, Completed: false) { TransferJobId = jobId });
        }
        finally
        {
            lock (_gate) { _running.Remove(jobId); runtime.Cancellation.Dispose(); }
            Publish(jobId);
        }
    }

    private void Fail(string jobId, TransferEntry entry, Exception error, RunningJob runtime)
    {
        _journal.SetItemState(jobId, entry.Id, TransferItemState.Attention, error.Message);
        runtime.Items.TryRemove(entry.Id, out _);
        Activity?.Invoke(new(DateTimeOffset.UtcNow, ActivityKind.Error, entry.RelativePath, error.Message, Completed: false) { TransferJobId = jobId });
    }

    public static TransferUploadRequest Request(TransferJobPlan plan, TransferEntry entry)
    {
        var operation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Id + "|" + entry.Id + "|" + entry.Version))).ToLowerInvariant();
        var path = entry.RelativePath.TrimEnd('/');
        var policy = plan.ConflictPolicy;
        // Adapters select a stable alternate name only when the original is
        // occupied, and persist that selected target in their checkpoint.
        if (entry.IsFolder && policy == TransferConflictPolicy.Rename) policy = TransferConflictPolicy.Replace;
        return new(operation, path, policy);
    }

    private static void Progress(RunningJob runtime, LiveItem live, TransferProgress value)
    {
        lock (live.Gate)
        {
            if (value.TotalBytes != live.Entry.Size) throw new InvalidDataException("The provider reported a different source length.");
            var bytes = Math.Clamp(value.Bytes, 0, value.TotalBytes);
            if (value.IsBaseline) live.Speed.Reset(bytes);
            else
            {
                live.Speed.Sample(bytes);
                var added = Math.Max(0, bytes - live.Bytes);
                lock (runtime.SpeedGate)
                {
                    runtime.PayloadBytes += added;
                    runtime.Speed.Sample(runtime.PayloadBytes);
                }
            }
            live.Bytes = bytes;
        }
    }

    private TransferJobSnapshot ApplyLive(TransferJobSnapshot snapshot)
    {
        RunningJob? runtime;
        lock (_gate) _running.TryGetValue(snapshot.Plan.Id, out runtime);
        if (runtime is null) return snapshot;
        long additional = 0;
        var liveItems = runtime.Items.Values.ToArray();
        foreach (var live in liveItems) additional += Math.Max(0, live.Bytes - live.AcknowledgedBytes);
        var window = snapshot.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var live in liveItems)
            window[live.Entry.Id] = new(live.Entry.Id, live.Entry.RelativePath, live.State, live.Bytes, live.Entry.Size,
                live.State == TransferItemState.Transferring ? live.Speed.BytesPerSecond : 0, null);
        var transferred = Math.Min(snapshot.TotalBytes, snapshot.TransferredBytes + additional);
        return snapshot with { TransferredBytes = transferred, RemainingBytes = Math.Max(0, snapshot.RemainingBytes - additional),
            QueuedFiles = snapshot.QueuedFiles + liveItems.Count(item => item.State == TransferItemState.Queued),
            BytesPerSecond = liveItems.Any(item => item.State == TransferItemState.Transferring) ? runtime.Speed.BytesPerSecond : 0,
            Items = window.Values.OrderBy(item => item.State == TransferItemState.Queued ? 1 : 0).Take(256).ToArray() };
    }
    private void Publish(string jobId) => Changed?.Invoke(ApplyLive(_journal.Snapshot(jobId)));

    public async ValueTask DisposeAsync()
    {
        Task[] work;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            work = _running.Values.Select(runtime => runtime.Task).ToArray();
            foreach (var runtime in _running.Values)
            {
                // A graceful app shutdown is an interruption, not the user's Pause choice.
                // Keep its durable running intent so the next startup continues it automatically.
                runtime.StopState ??= TransferJobState.Running;
                runtime.Cancellation.Cancel();
            }
        }
        await Task.WhenAll(work).ConfigureAwait(false);
        _journal.Dispose();
        // Provider connection pools remain owned by the account controller.
    }

    private sealed class RunningJob(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public TransferJobState? StopState { get; set; }
        public ConcurrentDictionary<string, LiveItem> Items { get; } = new(StringComparer.Ordinal);
        public object SpeedGate { get; } = new();
        public TransferSpeedMeter Speed { get; } = new();
        public long PayloadBytes;
    }
    private sealed record VerificationWork(JournalItem Item, ITransferSourceFile Source);
    private sealed class LiveItem(TransferEntry entry, long baseline)
    {
        public object Gate { get; } = new();
        public TransferEntry Entry { get; } = entry;
        public TransferItemState State = TransferItemState.Queued;
        public long Bytes = baseline;
        public long AcknowledgedBytes = baseline;
        public TransferSpeedMeter Speed { get; } = new();
    }
    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }
    private sealed class ReadAheadSource(ITransferSourceFile source) : ITransferSourceFile
    {
        public TransferEntry Entry => source.Entry;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => source.ValidateAsync(cancellationToken);
        public async Task<Stream> OpenReadAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            var stream = await source.OpenReadAsync(offset, length, cancellationToken).ConfigureAwait(false);
            return length > 256 * 1024 ? new BoundedReadAheadStream(stream, cancellationToken) : stream;
        }
    }
}

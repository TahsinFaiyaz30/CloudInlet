using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudInlet.Core.Migration;

public enum MigrationItemState { Pending, Checking, Running, Verifying, Completed, Failed, Reverting, Reverted, RevertFailed, Paused, Blocked, KeepingOriginal, OriginalKept, KeepOriginalFailed }
public sealed record MigrationItemStatus([property: JsonRequired] string Id, [property: JsonRequired] MigrationItemState State, string? Error = null);
public sealed record MigrationProgress(string ItemId, string Operation, string Detail,
    long Completed = 0, long? Total = null, string Unit = "items");
public sealed record MigrationLogEntry(long Sequence, DateTimeOffset TimeUtc, string ItemId, string Operation, string Detail);

public sealed class MigrationJournalException(string message, Exception inner) : IOException(message, inner);

/// <summary>Only an adapter that has positively identified a recoverable condition may request automatic retry.</summary>
public sealed class MigrationTransientException(string message, TimeSpan? retryAfter = null) : IOException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed record MigrationRetryPolicy(int MaximumAttempts = 8, TimeSpan? InitialDelay = null, TimeSpan? MaximumDelay = null)
{
    internal TimeSpan Delay(int attempt, TimeSpan? requested)
    {
        var initial = InitialDelay ?? TimeSpan.FromSeconds(1);
        var maximum = MaximumDelay ?? TimeSpan.FromSeconds(30);
        if (MaximumAttempts is < 1 or > 100 || initial < TimeSpan.Zero || maximum < initial || maximum > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Invalid migration retry policy.");
        // A provider's Retry-After is a minimum, never shorten it to the local backoff cap.
        if (requested is { } provider && provider > maximum) return provider;
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds,
            Math.Max(requested?.TotalMilliseconds ?? 0, initial.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 20)))));
    }
}

/// <summary>
/// A single user-reviewed atomic unit. Adapters own durable substep receipts and must recheck source ownership,
/// collisions and external state in CheckAsync. Apply, Verify and Revert must be idempotent across process loss.
/// An adapter must never act on another item's resources, including when reverting. No adapter is implicitly trusted
/// merely because a review exists: installer, native and cloud adapters require their own acceptance before wiring.
/// </summary>
public interface IMigrationOperation
{
    string ItemId { get; }
    string ReviewedPlanFingerprint { get; }
    bool CanRevert { get; }
    Task CheckAsync(CancellationToken cancellationToken);
    Task ApplyAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken);
    Task VerifyAsync(CancellationToken cancellationToken);
    Task RevertAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken);
    Task VerifyRevertedAsync(CancellationToken cancellationToken);
}

/// <summary>Explicitly abandon an incomplete cloud change without deleting or adopting uncertain copies.
/// A separate authenticated adapter receipt, not the session's display state, proves the original binding.</summary>
public interface IKeepOriginalMigrationOperation
{
    bool CanKeepOriginal { get; }
    Task KeepOriginalAsync(IProgress<MigrationProgress> progress, CancellationToken cancellationToken);
    Task VerifyOriginalKeptAsync(CancellationToken cancellationToken);
}

/// <summary>Durable per-item coordination. Never rolls back a completed item or treats a reverted item as migrated.</summary>
public sealed class MigrationSession : IDisposable
{
    private sealed record Journal(int SchemaVersion, MigrationPlan Plan, IReadOnlyList<MigrationItemStatus> Items,
        IReadOnlyList<MigrationLogEntry>? History = null);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<MigrationItemState>(allowIntegerValues: false) }
    };
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly Dictionary<string, IMigrationOperation> _operations;
    private readonly ConcurrentDictionary<string, MigrationItemStatus> _items;
    private readonly MigrationRetryPolicy _retry;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private Exception? _journalFailure;
    private readonly List<MigrationLogEntry> _history = [];
    private readonly object _historyGate = new();
    public MigrationPlan Plan { get; }
    public IReadOnlyList<MigrationItemStatus> Items => Plan.Items.Where(item => _items.ContainsKey(item.Id)).Select(item => _items[item.Id]).ToArray();
    public bool IsComplete => _items.Count > 0 && _items.Values.All(item => item.State == MigrationItemState.Completed);
    public bool NeedsJournalRecovery => _journalFailure is not null;
    public IReadOnlyList<MigrationLogEntry> History { get { lock (_historyGate) return _history.ToArray(); } }
    public bool CanRetry(string id) => !_disposed && !NeedsJournalRecovery && _items.TryGetValue(id, out var item) &&
        item.State is MigrationItemState.Failed or MigrationItemState.Paused &&
        Plan.Items.Single(value => value.Id == id).Dependencies!.All(dependency => _items[dependency].State == MigrationItemState.Completed);
    public bool CanRevert(string id) => !_disposed && !NeedsJournalRecovery && _items.TryGetValue(id, out var item) &&
        item.State is MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed &&
        _operations[id].CanRevert && !Plan.Items.Any(candidate => candidate.Dependencies!.Contains(id) &&
            _items.TryGetValue(candidate.Id, out var dependent) && dependent.State == MigrationItemState.Completed);
    public bool CanKeepOriginal(string id) => !_disposed && !NeedsJournalRecovery && _items.TryGetValue(id, out var item) &&
        item.State is MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed &&
        Plan.Items.Single(value => value.Id == id).Area == MigrationArea.CloudPrefix &&
        _operations[id] is IKeepOriginalMigrationOperation { CanKeepOriginal: true } &&
        !Plan.Items.Any(candidate => candidate.Dependencies!.Contains(id) &&
            _items.TryGetValue(candidate.Id, out var dependent) && dependent.State == MigrationItemState.Completed);
    public event Action? Changed;
    public event Action<MigrationProgress>? ProgressChanged;

    private MigrationSession(string path, FileStream lease, Journal journal, IEnumerable<IMigrationOperation> operations,
        MigrationRetryPolicy? retry, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _path = path; _lease = lease;
        _retry = retry ?? new(); _ = _retry.Delay(1, null);
        _delay = delay ?? Task.Delay;
        if (journal.SchemaVersion != 1 || journal.Plan is null || journal.Items is null || journal.Items.Count == 0)
            throw new InvalidDataException("The migration journal has an unsupported or empty selection.");
        Plan = MigrationPlan.Create(journal.Plan.Items);
        if (Plan.Fingerprint != journal.Plan.Fingerprint) throw new InvalidDataException("The migration review fingerprint is invalid.");
        _items = new(StringComparer.Ordinal);
        var selection = new MigrationSelection(Plan);
        foreach (var item in journal.Items)
        {
            if (item is null || !Enum.IsDefined(item.State) || !_items.TryAdd(item.Id, item))
                throw new InvalidDataException("The migration journal contains invalid or duplicate item states.");
            selection.Select(item.Id);
        }
        if (!selection.SelectedIds.SetEquals(_items.Keys)) throw new InvalidDataException("The migration journal omitted a required item.");
        _operations = new(StringComparer.Ordinal);
        foreach (var operation in operations)
            if (operation is null || !_operations.TryAdd(operation.ItemId, operation))
                throw new ArgumentException("Migration adapters must have distinct identities.", nameof(operations));
        if (_items.Keys.Any(id => !_operations.ContainsKey(id))) throw new InvalidOperationException("A selected migration has no verified execution adapter.");
        if (_items.Keys.Any(id => _operations[id].ReviewedPlanFingerprint != Plan.Fingerprint))
            throw new InvalidOperationException("A migration adapter does not match the reviewed locations. Create and confirm a fresh review.");
        foreach (var entry in journal.History ?? [])
        {
            if (entry is null || !_items.ContainsKey(entry.ItemId) || entry.Sequence <= (_history.LastOrDefault()?.Sequence ?? 0) ||
                entry.TimeUtc == default || string.IsNullOrWhiteSpace(entry.Operation) || entry.Operation.Length > 256 || entry.Detail is null || entry.Detail.Length > 2048)
                throw new InvalidDataException("The migration history is invalid.");
            _history.Add(entry);
        }
        if (_history.Count > 200) throw new InvalidDataException("The migration history exceeds the supported size.");
        foreach (var item in Plan.Items.Where(item => _items.ContainsKey(item.Id)))
            if (_items[item.Id].State is not (MigrationItemState.Pending or MigrationItemState.Blocked) &&
                item.Dependencies!.Any(id => _items[id].State != MigrationItemState.Completed))
                throw new InvalidDataException("A started migration is missing its completed dependency.");
    }

    public static MigrationSession Create(MigrationPlan plan, IEnumerable<string> selectedIds, string journalPath,
        IEnumerable<IMigrationOperation> operations, MigrationRetryPolicy? retryPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var selection = new MigrationSelection(plan);
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in selected) selection.Select(id);
        if (selected.Count == 0 || !selection.SelectedIds.SetEquals(selected))
            throw new ArgumentException("Review all required migration items before continuing.", nameof(selectedIds));
        var path = ValidateJournalPath(journalPath);
        var lease = Acquire(path);
        try
        {
            if (File.Exists(path)) throw new IOException("A migration journal already exists. Resume that migration instead.");
            var session = new MigrationSession(path, lease, new(1, plan, selected.Select(id => new MigrationItemStatus(id, MigrationItemState.Pending)).ToArray()),
                operations, retryPolicy, delay);
            session.Save();
            return session;
        }
        catch { lease.Dispose(); throw; }
    }

    public static MigrationSession Open(string journalPath, IEnumerable<IMigrationOperation> operations,
        MigrationRetryPolicy? retryPolicy = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var path = ValidateJournalPath(journalPath);
        var lease = Acquire(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("The migration journal is too large.");
            var journal = JsonSerializer.Deserialize<Journal>(input, JsonOptions) ?? throw new InvalidDataException("The migration journal is empty.");
            var session = new MigrationSession(path, lease, journal, operations, retryPolicy, delay);
            input.Dispose();
            foreach (var item in session.Items)
                if (item.State is MigrationItemState.Checking or MigrationItemState.Running or MigrationItemState.Verifying)
                    session._items[item.Id] = item with { State = MigrationItemState.Paused, Error = "Migration was interrupted. Retry resumes this item after checking its saved progress." };
                else if (item.State == MigrationItemState.Reverting)
                    session._items[item.Id] = item with { State = MigrationItemState.RevertFailed, Error = "Revert was interrupted. Retry reverting this item before migrating it again." };
                else if (item.State == MigrationItemState.KeepingOriginal)
                    session._items[item.Id] = item with { State = MigrationItemState.KeepOriginalFailed, Error = "Keeping the original location was interrupted. Confirm it again to verify the saved recovery record; cloud copies remain retained." };
            session.Save();
            return session;
        }
        catch { lease.Dispose(); throw; }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A previous failure needs explicit retry/revert; a fresh launch never retries destructive work silently.
            await VerifyKeptOriginalsCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_items.Values.Any(item => item.State is MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed)) return;
            while (true)
            {
                MarkBlockedDependents();
                // Local moves retain their original account identity. When both are selected,
                // finish them before changing the cloud identity; this does not select any extra item.
                var next = Plan.Items.OrderBy(item => item.Area == MigrationArea.CloudPrefix ? 1 : 0).FirstOrDefault(item => _items.TryGetValue(item.Id, out var status) && status.State == MigrationItemState.Pending &&
                    item.Dependencies!.All(id => _items[id].State == MigrationItemState.Completed));
                if (next is null || cancellationToken.IsCancellationRequested) return;
                await MigrateAsync(next.Id, cancellationToken).ConfigureAwait(false);
                if (_items[next.Id].State != MigrationItemState.Completed) return;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = RequireItem(id);
            if (item.State is not (MigrationItemState.Failed or MigrationItemState.Paused))
                throw new InvalidOperationException("Only a failed or interrupted item can be retried.");
            if (Plan.Items.Single(value => value.Id == id).Dependencies!.Any(dependency => _items[dependency].State != MigrationItemState.Completed))
                throw new InvalidOperationException("Complete this item's dependencies before retrying it.");
            await MigrateAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RevertAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = RequireItem(id);
            var operation = _operations[id];
            if (item.State is not (MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed) || !operation.CanRevert)
                throw new InvalidOperationException("Only an incomplete item with a verified recovery path can be reverted.");
            if (Plan.Items.Any(candidate => candidate.Dependencies!.Contains(id) && _items.TryGetValue(candidate.Id, out var dependent) && dependent.State == MigrationItemState.Completed))
                throw new InvalidOperationException("A completed migration depends on this item; it cannot be reverted.");
            SetState(id, MigrationItemState.Reverting);
            try
            {
                await RetryStageAsync(id, "Reverting", async () =>
                {
                    using var reporter = Reporter(id);
                    await operation.RevertAsync(reporter, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                await RetryStageAsync(id, "Checking restored item", () => operation.VerifyRevertedAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (MigrationJournalException) { throw; }
            catch (Exception error)
            {
                SetState(id, MigrationItemState.RevertFailed, error is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? "Revert paused. Retry reverting this item to finish restoring it. Completed migrations are unchanged." : error.Message);
                return;
            }
            SetState(id, MigrationItemState.Reverted);
            MarkBlockedDependents();
        }
        finally { _gate.Release(); }
    }

    public async Task KeepOriginalAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanKeepOriginal(id)) throw new InvalidOperationException("Only an incomplete cloud change with its original local binding can keep the original location.");
            var operation = (IKeepOriginalMigrationOperation)_operations[id];
            SetState(id, MigrationItemState.KeepingOriginal);
            try
            {
                using var reporter = Reporter(id);
                await operation.KeepOriginalAsync(reporter, cancellationToken).ConfigureAwait(false);
                await operation.VerifyOriginalKeptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MigrationJournalException) { throw; }
            catch (Exception error)
            { SetState(id, MigrationItemState.KeepOriginalFailed, error.Message); return; }
            SetState(id, MigrationItemState.OriginalKept,
                "Partial cloud copies and recovery records are retained; this change was not migrated or reverted.");
            MarkBlockedDependents();
        }
        finally { _gate.Release(); }
    }

    // Recheck authenticated adapter evidence before accepting an editable session journal's terminal claim.
    public async Task VerifyKeptOriginalsAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { await VerifyKeptOriginalsCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task VerifyKeptOriginalsCoreAsync(CancellationToken token)
    {
        foreach (var item in Items.Where(value => value.State == MigrationItemState.OriginalKept))
        {
            if (Plan.Items.Single(value => value.Id == item.Id).Area != MigrationArea.CloudPrefix ||
                _operations[item.Id] is not IKeepOriginalMigrationOperation operation)
                throw new InvalidDataException("The retained original location has no cloud recovery adapter.");
            await operation.VerifyOriginalKeptAsync(token).ConfigureAwait(false);
        }
    }

    private async Task MigrateAsync(string id, CancellationToken ct)
    {
        var operation = _operations[id];
        // Do not catch durable journal failures as adapter failures: no further mutation is allowed without a receipt.
        SetState(id, MigrationItemState.Checking);
        if (!await StageAsync(id, "Checking safety", () => operation.CheckAsync(ct), ct).ConfigureAwait(false)) return;
        SetState(id, MigrationItemState.Running);
        if (!await StageAsync(id, "Migrating", async () =>
        {
            // Recheck before every mutation attempt, including retries after another process could have changed a destination.
            await operation.CheckAsync(ct).ConfigureAwait(false);
            using var reporter = Reporter(id);
            await operation.ApplyAsync(reporter, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false)) return;
        SetState(id, MigrationItemState.Verifying);
        if (!await StageAsync(id, "Verifying", () => operation.VerifyAsync(ct), ct).ConfigureAwait(false)) return;
        SetState(id, MigrationItemState.Completed);
    }

    private async Task<bool> StageAsync(string id, string stage, Func<Task> action, CancellationToken ct)
    {
        try { await RetryStageAsync(id, stage, action, ct).ConfigureAwait(false); return true; }
        catch (MigrationJournalException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { SetState(id, MigrationItemState.Paused, "Migration paused. Completed items are unchanged; retry this item to continue."); return false; }
        catch (Exception error) { SetState(id, MigrationItemState.Failed, error.Message); return false; }
    }

    private async Task RetryStageAsync(string id, string stage, Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Announce(new(id, stage, attempt == 1 ? stage : $"{stage}: attempt {attempt} of {_retry.MaximumAttempts}."));
            try { await action().ConfigureAwait(false); return; }
            catch (MigrationTransientException error) when (attempt < _retry.MaximumAttempts)
            {
                var wait = _retry.Delay(attempt, error.RetryAfter);
                Announce(new(id, "Waiting to retry", $"{error.Message} Retrying in {Math.Ceiling(wait.TotalSeconds)} seconds (attempt {attempt + 1} of {_retry.MaximumAttempts})."));
                await _delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    private DirectProgress Reporter(string id) => new(progress =>
    {
        if (progress.ItemId != id || progress.Completed < 0 || progress.Total is < 0 || progress.Total is { } total && progress.Completed > total)
            throw new InvalidDataException("Migration progress does not match the current item or verified total.");
        ProgressChanged?.Invoke(progress);
    });
    private sealed class DirectProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress>, IDisposable
    {
        private readonly object _gate = new();
        private bool _closed;
        public void Report(MigrationProgress value) { lock (_gate) { if (!_closed) report(value); } }
        public void Dispose() { lock (_gate) _closed = true; }
    }

    private void MarkBlockedDependents()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var item in Plan.Items.Where(item => _items.ContainsKey(item.Id)))
                if (_items[item.Id].State == MigrationItemState.Pending && item.Dependencies!.Any(id =>
                    _items[id].State is MigrationItemState.Reverted or MigrationItemState.OriginalKept or MigrationItemState.Blocked))
                {
                    SetState(item.Id, MigrationItemState.Blocked, "A required change was not completed. Review these changes again before migrating this item.");
                    changed = true;
                }
        } while (changed);
    }

    private MigrationItemStatus RequireItem(string id) => _items.TryGetValue(id, out var item) ? item : throw new ArgumentException("This item was not selected.", nameof(id));
    private void SetState(string id, MigrationItemState state, string? error = null)
    {
        var previous = _items[id];
        _items[id] = new(id, state, error);
        AppendHistory(id, state.ToString(), error ?? state.ToString());
        try { Persist(); }
        catch (Exception failure) { _items[id] = previous; _journalFailure = failure; throw; }
        Changed?.Invoke();
    }

    private void Announce(MigrationProgress progress)
    {
        AppendHistory(progress.ItemId, progress.Operation, progress.Detail);
        Persist(); // Save retry/stage details before another attempt, never payload or credentials.
        ProgressChanged?.Invoke(progress);
    }

    private void AppendHistory(string id, string operation, string detail)
    {
        lock (_historyGate)
        {
            var sequence = (_history.LastOrDefault()?.Sequence ?? 0) + 1;
            _history.Add(new(sequence, DateTimeOffset.UtcNow, id, operation[..Math.Min(operation.Length, 256)], detail[..Math.Min(detail.Length, 2048)]));
            if (_history.Count > 200) _history.RemoveAt(0);
        }
    }

    private void Persist()
    {
        try { Save(); }
        catch (Exception error)
        {
            _journalFailure = error;
            throw new MigrationJournalException("Migration stopped because recovery information could not be saved. No further changes will run in this session.", error);
        }
    }

    private void Save()
    {
        _ = ValidateJournalPath(_path);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new Journal(1, Plan, Items, History), JsonOptions);
                if (output.Length > 4 * 1024 * 1024) throw new InvalidDataException("The migration journal is too large.");
                output.Flush(true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ValidateJournalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute migration journal path is required.", nameof(path));
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Migration records cannot follow symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }
    private static FileStream Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = ValidateJournalPath(path + ".lock");
        return new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private async Task EnterAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_journalFailure is not null)
            throw new InvalidOperationException("Migration records could not be saved. Close and recover this session before changing anything else.", _journalFailure);
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("Another migration action is still running.");
        if (_disposed || _journalFailure is not null)
        {
            _gate.Release();
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new InvalidOperationException("The migration journal needs recovery before more changes can run.", _journalFailure);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (!_gate.Wait(0)) throw new InvalidOperationException("Wait for the current migration action to stop before closing its journal.");
        try { _disposed = true; _lease.Dispose(); }
        finally { _gate.Release(); }
    }
}

using System.Runtime.ExceptionServices;
using CloudInlet.Core.Migration;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

/// <summary>Connects the migration journal to the existing window's modal host. The caller owns the session lease.</summary>
internal static class MigrationDialogHost
{
    internal static async Task ShowAsync(MigrationSession session,
        Func<ContentDialog, Task<ContentDialogResult>> showDialog, CancellationToken shutdown = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(showDialog);
        var selected = session.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var dialog = new MigrationProgressDialog(session.Plan, selected);
        dialog.RestoreHistory(session.History);
        var titles = session.Plan.Items.ToDictionary(item => item.Id, item => item.Title, StringComparer.Ordinal);
        var work = new List<Task>();
        CancellationTokenSource? stop = null;
        Exception? journalFailure = null;
        var busy = 0;
        var closing = false;

        void RefreshRows()
        {
            foreach (var item in session.Items)
                dialog.SetItemStatus(item, journalFailure is null && session.CanRetry(item.Id),
                    journalFailure is null && session.CanRevert(item.Id), journalFailure is null && session.CanKeepOriginal(item.Id));
        }

        void ShowResult()
        {
            RefreshRows();
            var items = session.Items;
            var states = items.ToDictionary(item => item.Id, item => item.State, StringComparer.Ordinal);
            var canContinue = journalFailure is null && !session.NeedsJournalRecovery &&
                !items.Any(item => item.State is MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed) &&
                session.Plan.Items.Any(item => states.TryGetValue(item.Id, out var state) && state == MigrationItemState.Pending &&
                    item.Dependencies!.All(id => states[id] == MigrationItemState.Completed));
            dialog.SetCanContinue(canContinue);
            var completed = items.Count(item => item.State == MigrationItemState.Completed);
            var retained = completed == 0 ? "No selected changes are verified as complete." :
                $"{completed:N0} of {items.Count:N0} selected changes are verified and remain in place.";
            if (journalFailure is not null)
            {
                dialog.Fail("Migration stopped because its recovery record or configuration could not be verified. " +
                    journalFailure.Message + " " + retained + " Close this review and resolve the reported problem before resuming.");
            }
            else if (session.IsComplete)
                dialog.CompleteVerified($"All {items.Count:N0} selected changes passed verification.");
            else if (items.Any(item => item.State is MigrationItemState.Failed or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed))
                dialog.Fail(retained + " Review the failed change below and choose an available recovery action.");
            else if (items.Any(item => item.State == MigrationItemState.Paused))
                dialog.MarkStopped(retained + " Incomplete changes are paused. Retry an interrupted change to continue, or revert that change when available.");
            else
            {
                var reverted = items.Count(item => item.State == MigrationItemState.Reverted);
                var kept = items.Count(item => item.State == MigrationItemState.OriginalKept);
                var pending = items.Count(item => item.State == MigrationItemState.Pending);
                var blocked = items.Count(item => item.State == MigrationItemState.Blocked);
                dialog.FinishPartial(retained + (reverted == 0 ? "" :
                    $" {reverted:N0} incomplete {(reverted == 1 ? "change was" : "changes were")} reverted and have not been migrated.") +
                    (kept == 0 ? "" : $" {kept:N0} cloud {(kept == 1 ? "change keeps its" : "changes keep their")} original location; partial copies and recovery records remain retained.") +
                    (pending == 0 ? "" : $" {pending:N0} selected changes have not started.") +
                    (canContinue ? " Continue remaining changes to resume the selected work." :
                        blocked > 0 ? " Dependent changes need a new review." : ""));
            }
        }

        void OnChanged() => RefreshRows();

        void OnProgress(MigrationProgress value)
        {
            var status = session.Items.Single(item => item.Id == value.ItemId);
            dialog.Report(new(value.Operation, titles[value.ItemId], value.Detail, value.Completed, value.Total,
                value.Unit, value.Detail, value.ItemId, status.State,
                journalFailure is null && session.CanRetry(value.ItemId), journalFailure is null && session.CanRevert(value.ItemId),
                journalFailure is null && session.CanKeepOriginal(value.ItemId)));
        }

        void Begin(Func<CancellationToken, Task> operation)
        {
            if (closing || journalFailure is not null)
            {
                // A rejected action must restore the journal-backed row state and leave the modal closable.
                ShowResult();
                return;
            }
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            stop?.Dispose();
            stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            var token = stop.Token;
            // Native calls and durable record flushes must not stall the WinUI dispatcher.
            // UI changes are marshalled by the dialog's reporting methods.
            work.Add(Task.Run(async () =>
            {
                try { await operation(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Cancellation before a first item starts leaves Pending items rather than
                    // inventing a completed/rolled-back state; the summary reflects the journal.
                }
                catch (Exception error)
                {
                    // Adapter failures are recorded by MigrationSession. An exception escaping it
                    // means execution itself cannot proceed safely (for example a failed journal write).
                    journalFailure = error;
                }
                finally
                {
                    Interlocked.Exchange(ref busy, 0);
                    ShowResult();
                }
            }));
        }

        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            RefreshRows();
            if (session.Items.Any(item => item.State is MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed))
                ShowResult();
            else Begin(session.RunAsync);
        }

        void OnStop(object? sender, EventArgs args) => stop?.Cancel();
        void OnContinue(object? sender, EventArgs args) => Begin(session.RunAsync);

        void OnRetry(object? sender, MigrationItemActionEventArgs args) => Begin(async token =>
        {
            await session.RetryAsync(args.ItemId, token).ConfigureAwait(false);
            // This resumes eligible pending items; it never replays already completed changes.
            await session.RunAsync(token).ConfigureAwait(false);
        });

        void OnRevert(object? sender, MigrationItemActionEventArgs args) => Begin(async token =>
        {
            await session.RevertAsync(args.ItemId, token).ConfigureAwait(false);
            // Dependent changes become Blocked because Reverted does not satisfy their dependency.
            await session.RunAsync(token).ConfigureAwait(false);
        });
        void OnKeepOriginal(object? sender, MigrationItemActionEventArgs args) => Begin(async token =>
        {
            await session.KeepOriginalAsync(args.ItemId, token).ConfigureAwait(false);
            await session.RunAsync(token).ConfigureAwait(false);
        });

        session.Changed += OnChanged;
        session.ProgressChanged += OnProgress;
        dialog.Opened += OnOpened;
        dialog.SafeStopRequested += OnStop;
        dialog.ContinueRequested += OnContinue;
        dialog.RetryRequested += OnRetry;
        dialog.RevertRequested += OnRevert;
        dialog.KeepOriginalRequested += OnKeepOriginal;
        try { await showDialog(dialog); }
        finally
        {
            closing = true;
            stop?.Cancel();
            try { await Task.WhenAll(work); }
            finally
            {
                session.Changed -= OnChanged;
                session.ProgressChanged -= OnProgress;
                dialog.Opened -= OnOpened;
                dialog.SafeStopRequested -= OnStop;
                dialog.ContinueRequested -= OnContinue;
                dialog.RetryRequested -= OnRetry;
                dialog.RevertRequested -= OnRevert;
                dialog.KeepOriginalRequested -= OnKeepOriginal;
                stop?.Dispose();
            }
        }
        // The error was shown in the dialog; also preserve failure semantics for the caller.
        if (journalFailure is not null) ExceptionDispatchInfo.Capture(journalFailure).Throw();
    }
}

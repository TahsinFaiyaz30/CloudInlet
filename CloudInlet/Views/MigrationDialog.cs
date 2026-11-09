using CloudInlet.Core.Migration;
using CloudInlet.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

/// <summary>Chooses exact migration items, then asks for confirmation without changing any state.</summary>
internal sealed class MigrationDialog : ContentDialog
{
    private readonly MigrationPlan _plan;
    private readonly MigrationSelection _selection;
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly TextBlock _summary = SourceImportDialog.Text("No changes selected.", true);
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private bool _reviewing;
    private bool _updatingChoices;
    private readonly string _introduction;
    private CheckBox? _cloudMaintenance;
    internal bool CloudMaintenanceAcknowledged => _cloudMaintenance?.IsChecked == true;
    internal void AcknowledgeCloudForValidation() => (_cloudMaintenance ?? throw new InvalidOperationException("The cloud review is not open.")).IsChecked = true;

    /// <summary>Null until the user confirms the separate review page.</summary>
    internal MigrationSelection? ConfirmedSelection { get; private set; }
    internal FrameworkElement CaptureContent => _body;
    internal IReadOnlySet<string> SelectedIdsForValidation => _selection.SelectedIds;
    internal void SelectAllForValidation() => ChangeSelection(_selection.SelectAll);
    internal void DeselectAllForValidation() => ChangeSelection(_selection.DeselectAll);

    /// <summary>Renders a chosen review for isolated UI captures; never confirms or executes migration.</summary>
    internal void PrepareReviewForValidation(IEnumerable<string> selectedIds)
    {
        _selection.DeselectAll();
        foreach (var id in selectedIds) _selection.Select(id);
        if (!_selection.CanContinue) throw new ArgumentException("Choose at least one migration item for a review capture.", nameof(selectedIds));
        UpdateSelection();
        ShowReview();
    }

    internal MigrationDialog(MigrationPlan plan, string? introduction = null, string closeButtonText = "Later")
    {
        ArgumentNullException.ThrowIfNull(plan);
        _selection = new MigrationSelection(plan);
        _plan = MigrationPlan.Create(plan.Items);
        if (_plan.Fingerprint != plan.Fingerprint) throw new ArgumentException("The migration plan changed during review setup.", nameof(plan));
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        _introduction = introduction ?? "Select the changes to review. Required changes are grouped together. Unselected locations keep working and receiving updates.";
        CloseButtonText = closeButtonText;
        DefaultButton = ContentDialogButton.Close;
        SourceImportDialog.ConfigureLayout(this);
        _scroll = SourceImportDialog.ScrollContent(_body);
        Content = _scroll;
        PrimaryButtonClick += (_, args) =>
        {
            if (!_selection.CanContinue) { args.Cancel = true; return; }
            if (!_reviewing) { args.Cancel = true; ShowReview(); return; }
            if (_cloudMaintenance is not null && !CloudMaintenanceAcknowledged) { args.Cancel = true; return; }
            ConfirmedSelection = _selection;
        };
        SecondaryButtonClick += (_, args) => { args.Cancel = true; ShowChoices(); };
        Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary) ConfirmedSelection = null;
        };
        ShowChoices();
    }

    private void ShowChoices()
    {
        _reviewing = false;
        _cloudMaintenance = null;
        ConfirmedSelection = null;
        Title = "Move CloudBay names to CloudInlet";
        PrimaryButtonText = "Review selected changes";
        SecondaryButtonText = "";
        DefaultButton = ContentDialogButton.Close;
        _body.Children.Clear();
        _choices.Clear();
        _body.Children.Add(SourceImportDialog.Text(_introduction, true));
        var actions = new ActionWrapPanel { Spacing = 12 };
        var selectAll = SourceImportDialog.ActionButton("Select all available");
        selectAll.Click += (_, _) => ChangeSelection(_selection.SelectAll);
        var deselectAll = SourceImportDialog.ActionButton("Deselect all");
        deselectAll.Click += (_, _) => ChangeSelection(_selection.DeselectAll);
        actions.Children.Add(selectAll);
        actions.Children.Add(deselectAll);
        _body.Children.Add(actions);
        _body.Children.Add(_summary);
        _body.Children.Add(_error);
        foreach (var group in _plan.Items.GroupBy(item => item.Area).OrderBy(group => group.Key))
        {
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            heading.Children.Add(FluentIcons.Create(AreaIcon(group.Key), 20));
            var label = SourceImportDialog.Heading(AreaTitle(group.Key));
            Grid.SetColumn(label, 1);
            heading.Children.Add(label);
            _body.Children.Add(heading);
            foreach (var item in group)
            {
                var blocked = BlockedReason(item, new HashSet<string>(StringComparer.Ordinal));
                var check = new CheckBox
                {
                    Content = SourceImportDialog.Heading(item.Title),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsEnabled = blocked is null,
                    MinWidth = 0,
                    IsChecked = _selection.SelectedIds.Contains(item.Id)
                };
                AutomationProperties.SetName(check, item.Title);
                if (blocked is not null) AutomationProperties.SetHelpText(check, blocked);
                check.Checked += (_, _) =>
                {
                    if (!_updatingChoices) ChangeSelection(() => _selection.Select(item.Id));
                };
                check.Unchecked += (_, _) =>
                {
                    if (!_updatingChoices) ChangeSelection(() => _selection.Deselect(item.Id));
                };
                _choices.Add(item.Id, check);
                var card = ItemDetails(item, showDependencies: true);
                card.Children.Insert(0, check);
                if (blocked is not null)
                    card.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                        Title = "Not available yet", Message = blocked });
                _body.Children.Add(SourceImportDialog.Surface(card));
            }
        }
        if (_plan.Items.Count == 0)
            _body.Children.Add(SourceImportDialog.Text("No migration changes were found for this installation.", true));
        else if (_choices.Values.All(choice => !choice.IsEnabled))
            _body.Children.Add(SourceImportDialog.Text("No changes can be migrated yet. Resolve the requirements shown above, then open migration again.", true));
        UpdateSelection();
        _scroll.ChangeView(null, 0, null, true);
    }

    private void ChangeSelection(Action change)
    {
        try { change(); _error.IsOpen = false; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { _error.Message = error.Message; _error.IsOpen = true; }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        _updatingChoices = true;
        try
        {
            foreach (var pair in _choices) pair.Value.IsChecked = _selection.SelectedIds.Contains(pair.Key);
        }
        finally { _updatingChoices = false; }
        var count = _selection.SelectedIds.Count;
        _summary.Text = count == 0 ? "No changes selected." : $"{count:N0} {(count == 1 ? "change" : "changes")} selected, including required changes.";
        IsPrimaryButtonEnabled = _selection.CanContinue;
    }

    private void ShowReview()
    {
        _reviewing = true;
        Title = "Confirm selected migration";
        PrimaryButtonText = "Migrate selected";
        SecondaryButtonText = "Back";
        DefaultButton = ContentDialogButton.Close;
        _body.Children.Clear();
        _body.Children.Add(SourceImportDialog.Text("Only the changes below will be requested. Required changes are included. CloudInlet checks that the reviewed locations are still valid before changing them.", true));
        foreach (var item in _plan.Items.Where(item => _selection.SelectedIds.Contains(item.Id)))
        {
            var details = ItemDetails(item, showDependencies: true);
            details.Children.Insert(0, SourceImportDialog.Heading(item.Title));
            _body.Children.Add(SourceImportDialog.Surface(details));
        }
        _body.Children.Add(SourceImportDialog.Text("Keep CloudInlet open while it shows progress. If a step cannot be verified, migration stops and displays the details.", true));
        IsPrimaryButtonEnabled = _selection.CanContinue;
        if (_plan.Items.Any(item => item.Area == MigrationArea.CloudPrefix && _selection.SelectedIds.Contains(item.Id)))
        {
            _body.Children.Add(SourceImportDialog.Text("Cloud migration retains the original versions for recovery. Copies can use additional cloud storage. Other clients must stay paused until this migration finishes; CloudInlet cannot lock the bucket against other applications.", true));
            _cloudMaintenance = new CheckBox { Content = SourceImportDialog.Text("I have paused other clients that can change these cloud folders and will keep them paused until recovery is finished."),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _cloudMaintenance.Checked += (_, _) => IsPrimaryButtonEnabled = true;
            _cloudMaintenance.Unchecked += (_, _) => IsPrimaryButtonEnabled = false;
            _body.Children.Add(_cloudMaintenance);
            IsPrimaryButtonEnabled = false;
        }
        _scroll.ChangeView(null, 0, null, true);
    }

    private StackPanel ItemDetails(MigrationItem item, bool showDependencies)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 0 };
        body.Children.Add(SourceImportDialog.Text(item.Description, true));
        body.Children.Add(Location("Current", item.CurrentLocation));
        body.Children.Add(Location("After migration", item.ProposedLocation));
        if (showDependencies && item.Dependencies is { Count: > 0 })
        {
            var titles = item.Dependencies.Select(id => _plan.Items.FirstOrDefault(candidate => candidate.Id == id)?.Title ?? id);
            body.Children.Add(SourceImportDialog.Text("Required changes: " + string.Join(", ", titles), true));
        }
        return body;
    }

    private string? BlockedReason(MigrationItem item, HashSet<string> visited)
    {
        var pending = new Stack<MigrationItem>();
        pending.Push(item);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current.Id)) continue;
            if (current.BlockedReason is { Length: > 0 } reason)
                return current.Id == item.Id ? reason : current.Title + ": " + reason;
            foreach (var id in current.Dependencies ?? [])
            {
                var dependency = _plan.Items.FirstOrDefault(candidate => candidate.Id == id);
                if (dependency is null) return "A required migration change is unavailable. Refresh the migration review.";
                pending.Push(dependency);
            }
        }
        return null;
    }

    private static StackPanel Location(string label, string path)
    {
        var body = new StackPanel { Spacing = 2, MinWidth = 0 };
        body.Children.Add(SourceImportDialog.Text(label, true));
        var value = SourceImportDialog.Text(path);
        value.IsTextSelectionEnabled = true;
        // Wrapping preserves the whole path even at increased Windows text scaling.
        value.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(value);
        return body;
    }

    private static string AreaTitle(MigrationArea area) => area switch
    {
        MigrationArea.Installation => "App installation",
        MigrationArea.ApplicationState => "Accounts and saved settings",
        MigrationArea.WindowsIntegration => "Windows integration",
        MigrationArea.SyncFolder => "Sync folder",
        MigrationArea.BackupFolder => "Backup folders",
        MigrationArea.CloudPrefix => "Cloud storage",
        _ => "Other changes"
    };

    private static string AreaIcon(MigrationArea area) => area switch
    {
        MigrationArea.Installation => "apps",
        MigrationArea.ApplicationState => "database",
        MigrationArea.WindowsIntegration => "desktop",
        MigrationArea.SyncFolder or MigrationArea.BackupFolder => "folder",
        MigrationArea.CloudPrefix => "cloud",
        _ => "settings"
    };
}

/// <summary>Measured progress for the current operation. Null totals remain indeterminate.</summary>
internal sealed record MigrationProgressUpdate(string Operation, string Item, string Status,
    long Completed = 0, long? Total = null, string Unit = "items", string? Detail = null,
    string? ItemId = null, MigrationItemState State = MigrationItemState.Running,
    bool CanRetry = false, bool CanRevert = false, bool CanKeepOriginal = false);

internal enum MigrationDialogCompletion { Running, Verified, Failed, Stopped, Partial }
internal sealed class MigrationItemActionEventArgs(string itemId) : EventArgs
{
    internal string ItemId { get; } = itemId;
}

/// <summary>Shows coordinator-reported progress; it never performs or infers a successful migration.</summary>
internal sealed class MigrationProgressDialog : ContentDialog
{
    private const int MaximumLogLines = 120;
    private const int MaximumDetailLength = 2048;
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly StackPanel _currentStep = new() { Spacing = 8 };
    private readonly TextBlock _operation = SourceImportDialog.Heading("Preparing migration");
    private readonly TextBlock _item = SourceImportDialog.Text("");
    private readonly TextBlock _status = SourceImportDialog.Text("Waiting for the migration checks.", true);
    private readonly TextBlock _count = SourceImportDialog.Text("", true);
    private readonly TextBlock _overall = SourceImportDialog.Text("", true);
    private readonly TextBlock _guidance = SourceImportDialog.Text("Keep CloudInlet open. Stop safely pauses at a recoverable boundary and keeps completed changes.", true);
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
    private readonly InfoBar _result = new() { IsClosable = false };
    private readonly TextBlock _log = SourceImportDialog.Text("");
    private readonly Queue<string> _lines = new();
    private readonly Dictionary<string, ItemRow> _items = new(StringComparer.Ordinal);
    private bool _running = true;
    private bool _stopRequested;
    private bool _closed;
    private bool _canContinue;

    internal event EventHandler? SafeStopRequested;
    internal event EventHandler? ContinueRequested;
    internal event EventHandler<MigrationItemActionEventArgs>? RetryRequested;
    internal event EventHandler<MigrationItemActionEventArgs>? RevertRequested;
    internal event EventHandler<MigrationItemActionEventArgs>? KeepOriginalRequested;
    internal bool IsRunning => _running;
    internal MigrationDialogCompletion Completion { get; private set; } = MigrationDialogCompletion.Running;
    internal bool IsFinished => !_running;
    internal FrameworkElement CaptureContent => _body;
    internal IReadOnlyDictionary<string, MigrationItemState> ItemStatesForValidation =>
        _items.ToDictionary(pair => pair.Key, pair => pair.Value.State, StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, (bool CanRetry, bool CanRevert)> ItemActionsForValidation =>
        _items.ToDictionary(pair => pair.Key, pair => (pair.Value.Retry.IsEnabled, pair.Value.Revert.IsEnabled), StringComparer.Ordinal);
    internal void RequestRetryForValidation(string itemId) => RequestItemAction(_items[itemId], revert: false);
    internal void RequestRevertForValidation(string itemId) => RequestItemAction(_items[itemId], revert: true);
    internal bool CanKeepOriginalForValidation(string itemId) => _items[itemId].KeepOriginal.IsEnabled;
    internal void ReviewKeepOriginalForValidation(string itemId)
    {
        var row = _items[itemId];
        if (!_running && row.CanKeepOriginal && KeepOriginalRequested is not null) row.Confirmation.Visibility = Visibility.Visible;
    }
    internal void ConfirmKeepOriginalForValidation(string itemId) => RequestKeepOriginal(_items[itemId]);
    internal void RequestContinueForValidation() => RequestContinue();
    internal void RequestStopForValidation() => RequestStop();
    internal string ActivityForValidation => _log.Text;
    internal (bool IsIndeterminate, double Value, string Label) ProgressForValidation => (_progress.IsIndeterminate, _progress.Value, _count.Text);
    internal bool CanContinueForValidation => !_running && _canContinue && ContinueRequested is not null;

    internal MigrationProgressDialog(MigrationPlan plan, IReadOnlySet<string> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var selection = new MigrationSelection(plan);
        foreach (var id in selectedIds) selection.Select(id);
        if (!selection.SelectedIds.SetEquals(selectedIds) || !selection.CanContinue)
            throw new ArgumentException("Select a complete, available set of migration changes before starting.", nameof(selectedIds));
        Title = "Migrating to CloudInlet";
        PrimaryButtonText = "Stop safely";
        CloseButtonText = "";
        DefaultButton = ContentDialogButton.None;
        SourceImportDialog.ConfigureLayout(this);
        _item.IsTextSelectionEnabled = true;
        _log.IsTextSelectionEnabled = true;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, "Current migration step progress");
        _currentStep.Children.Add(_operation);
        _currentStep.Children.Add(_item);
        _currentStep.Children.Add(_status);
        _currentStep.Children.Add(_progress);
        _currentStep.Children.Add(_count);
        _body.Children.Add(_currentStep);
        _body.Children.Add(_overall);
        _body.Children.Add(_result);
        _body.Children.Add(_guidance);
        var itemRows = new StackPanel { Spacing = 8 };
        foreach (var item in plan.Items.Where(item => selectedIds.Contains(item.Id)))
        {
            var row = new ItemRow(item.Id, item.Title);
            row.Retry.Click += (_, _) => RequestItemAction(row, revert: false);
            row.Revert.Click += (_, _) => RequestItemAction(row, revert: true);
            row.KeepOriginal.Click += (_, _) =>
            {
                if (_running || !row.CanKeepOriginal || KeepOriginalRequested is null) return;
                row.Confirmation.Visibility = Visibility.Visible;
            };
            row.CancelKeep.Click += (_, _) => row.Confirmation.Visibility = Visibility.Collapsed;
            row.ConfirmKeep.Click += (_, _) => RequestKeepOriginal(row);
            _items.Add(item.Id, row);
            itemRows.Children.Add(SourceImportDialog.Surface(row.Content));
        }
        UpdateOverall();
        _body.Children.Add(itemRows);
        var logScroll = new ScrollViewer
        {
            Content = _log, MaxHeight = 240,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _body.Children.Add(new Expander
        {
            Header = "Details · most recent activity", Content = logScroll, IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        Content = SourceImportDialog.ScrollContent(_body);
        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (!_running) { RequestContinue(); return; }
            RequestStop();
        };
        // Covers Escape as well as programmatic Hide; the coordinator must report a terminal state first.
        Closing += (_, args) => { if (_running) args.Cancel = true; };
        Closed += (_, _) => _closed = true;
    }

    private void RequestStop()
    {
        if (!_running || _stopRequested) return;
        _stopRequested = true;
        IsPrimaryButtonEnabled = false;
        PrimaryButtonText = "Stopping safely…";
        _status.Text = "Stop requested. Waiting for the current step to reach a recoverable boundary.";
        AddDetail(_status.Text);
        SafeStopRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void Report(MigrationProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Dispatch(() =>
        {
            if (!_running) return;
            if (update.ItemId is not null && _items.TryGetValue(update.ItemId, out var row))
            {
                // A delayed progress message must never make a verified item eligible for rollback.
                if (row.State == MigrationItemState.Completed && update.State != MigrationItemState.Completed) return;
                UpdateItem(row, update.State, update.Status, update.CanRetry, update.CanRevert, update.CanKeepOriginal);
            }
            _operation.Text = update.Operation;
            _item.Text = update.Item;
            _status.Text = _stopRequested ? "Stop requested. " + update.Status : update.Status;
            var completed = Math.Max(0, update.Completed);
            if (update.Total is > 0 && completed <= update.Total.Value)
            {
                var total = update.Total.Value;
                var value = completed < total ? Math.Min(99.9, 100d * completed / total) : 100;
                _progress.IsIndeterminate = false;
                _progress.Value = value;
                _count.Text = string.Equals(update.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                    ? "Current step: " + ProgressPresentation.ForBytes(completed, total).Label
                    : $"Current step: {value:0.#}% · {completed:N0} of {total:N0} {update.Unit}";
            }
            else if (update.Total == 0 && completed == 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = 0;
                _count.Text = $"Current step: No {update.Unit} to process";
            }
            else
            {
                _progress.IsIndeterminate = true;
                var amount = string.Equals(update.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                    ? ProgressPresentation.FormatSize(completed) : $"{completed:N0} {update.Unit}";
                _count.Text = completed > 0 ? $"Current step: {amount} completed · Total not yet known" : "Current step: Total not yet known";
            }
            if (!string.IsNullOrWhiteSpace(update.Detail))
                AddDetail(update.Item + " · " + update.Operation + " · " + update.Detail);
        });
    }

    /// <summary>Restores saved details with their original times without treating them as new progress.</summary>
    internal void RestoreHistory(IEnumerable<MigrationLogEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var saved = history.ToArray();
        Dispatch(() =>
        {
            _lines.Clear();
            foreach (var entry in saved)
            {
                if (!_items.TryGetValue(entry.ItemId, out var row)) continue;
                AddDetail(row.Title + " · " + entry.Operation + " · " + entry.Detail, entry.TimeUtc);
            }
        });
    }

    /// <summary>Only the coordinator can authorize continuing pending work after a safe stop.</summary>
    internal void SetCanContinue(bool canContinue) => Dispatch(() =>
    {
        _canContinue = canContinue;
        if (!_running) RefreshContinue();
    });

    /// <summary>Refreshes a journal row without replacing the active operation's measured progress.</summary>
    internal void SetItemStatus(MigrationItemStatus status, bool canRetry = false, bool canRevert = false, bool canKeepOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(status);
        Dispatch(() =>
        {
            if (!_items.TryGetValue(status.Id, out var row)) return;
            if (row.State == MigrationItemState.Completed && status.State != MigrationItemState.Completed) return;
            if (row.State != status.State || !string.IsNullOrWhiteSpace(status.Error) && row.Status.Text != StateLabel(status.State) + " · " + status.Error)
                AddDetail(row.Title + " · " + StateLabel(status.State) + (string.IsNullOrWhiteSpace(status.Error) ? "" : " · " + status.Error));
            UpdateItem(row, status.State, status.Error, canRetry, canRevert, canKeepOriginal);
        });
    }

    private void UpdateItem(ItemRow row, MigrationItemState state, string? detail, bool canRetry, bool canRevert, bool canKeepOriginal)
    {
        row.State = state;
        row.CanRetry = canRetry;
        row.CanRevert = canRevert;
        row.CanKeepOriginal = canKeepOriginal;
        row.Status.Text = StateLabel(state) + (string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail);
        row.Icon.Content = FluentIcons.Create(StateIcon(state), 20);
        UpdateOverall();
        RefreshActions();
    }

    private void UpdateOverall() => _overall.Text =
        $"Selected changes: {_items.Values.Count(item => item.State == MigrationItemState.Completed):N0} of {_items.Count:N0} verified";

    /// <summary>Call only after the coordinator has verified all selected changes.</summary>
    internal void CompleteVerified(string detail) => Finish("Migration verified", detail, InfoBarSeverity.Success, MigrationDialogCompletion.Verified);

    /// <summary>Keeps errors visible and offers only coordinator-approved actions on incomplete items.</summary>
    internal void Fail(string detail) => Finish("Migration needs attention", detail, InfoBarSeverity.Error, MigrationDialogCompletion.Failed);

    internal void MarkStopped(string detail) => Finish("Migration stopped safely", detail, InfoBarSeverity.Informational, MigrationDialogCompletion.Stopped);

    internal void FinishPartial(string detail) => Finish("Migration partially completed", detail, InfoBarSeverity.Warning, MigrationDialogCompletion.Partial);

    private void Finish(string title, string detail, InfoBarSeverity severity, MigrationDialogCompletion completion) => Dispatch(() =>
    {
        if (!_running) return;
        if (completion == MigrationDialogCompletion.Verified && _items.Values.Any(item => item.State != MigrationItemState.Completed))
        {
            title = "Migration needs attention";
            detail = "Some selected changes have not been reported as verified. Review their status below.";
            severity = InfoBarSeverity.Error;
            completion = MigrationDialogCompletion.Failed;
        }
        _running = false;
        Completion = completion;
        var completed = completion == MigrationDialogCompletion.Verified;
        Title = title;
        // The title and result carry the terminal summary once. Hide the previous step and its
        // empty progress space; each row and the activity log retain its useful operation details.
        _currentStep.Visibility = Visibility.Collapsed;
        _overall.Visibility = Visibility.Collapsed;
        _progress.IsIndeterminate = false;
        _guidance.Text = "Closing this review does not undo completed changes.";
        _guidance.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
        _result.Title = "";
        _result.Message = detail;
        _result.Severity = severity;
        _result.IsOpen = true;
        AddDetail(detail);
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        RefreshActions();
        RefreshContinue();
    });

    private void RefreshContinue()
    {
        var available = _canContinue && ContinueRequested is not null;
        PrimaryButtonText = available ? "Continue remaining changes" : "";
        IsPrimaryButtonEnabled = available;
    }

    private void RequestContinue()
    {
        if (_running || !_canContinue || ContinueRequested is null) return;
        BeginAction("Continuing migration", "", "Continuing the remaining selected changes. Completed changes remain in place.");
        ContinueRequested.Invoke(this, EventArgs.Empty);
    }

    private void RequestItemAction(ItemRow row, bool revert)
    {
        if (_running || (revert
                ? row.State is not (MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed) || !row.CanRevert || RevertRequested is null
                : row.State is not (MigrationItemState.Failed or MigrationItemState.Paused) || !row.CanRetry || RetryRequested is null)) return;
        BeginAction(revert ? "Reverting incomplete change" : "Retrying incomplete change", row.Title,
            (revert ? "Revert requested: " : "Retry requested: ") + row.Title + ". Completed changes remain in place.");
        row.State = revert ? MigrationItemState.Reverting : MigrationItemState.Checking;
        row.Status.Text = StateLabel(row.State);
        row.Icon.Content = FluentIcons.Create(StateIcon(row.State), 20);
        RefreshActions();
        var args = new MigrationItemActionEventArgs(row.Id);
        if (revert) RevertRequested?.Invoke(this, args);
        else RetryRequested?.Invoke(this, args);
    }

    private void BeginAction(string title, string item, string detail)
    {
        foreach (var row in _items.Values) row.Confirmation.Visibility = Visibility.Collapsed;
        _running = true;
        Completion = MigrationDialogCompletion.Running;
        _stopRequested = false;
        _result.IsOpen = false;
        _currentStep.Visibility = Visibility.Visible;
        _overall.Visibility = Visibility.Visible;
        _guidance.Text = "Keep CloudInlet open. Stop safely pauses at a recoverable boundary and keeps completed changes.";
        _guidance.Visibility = Visibility.Visible;
        Title = "Migration progress";
        _operation.Text = title;
        _item.Text = item;
        _status.Text = detail;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        _count.Text = "";
        PrimaryButtonText = "Stop safely";
        IsPrimaryButtonEnabled = true;
        CloseButtonText = "";
        DefaultButton = ContentDialogButton.None;
        AddDetail(detail);
        RefreshActions();
    }

    private void RefreshActions()
    {
        foreach (var row in _items.Values)
        {
            var canRetryState = row.State is MigrationItemState.Failed or MigrationItemState.Paused;
            var canRevertState = canRetryState || row.State == MigrationItemState.RevertFailed;
            var canKeepState = canRevertState || row.State == MigrationItemState.KeepOriginalFailed;
            row.Actions.Visibility = canKeepState ? Visibility.Visible : Visibility.Collapsed;
            row.Retry.Visibility = canRetryState ? Visibility.Visible : Visibility.Collapsed;
            row.Retry.IsEnabled = !_running && canRetryState && row.CanRetry && RetryRequested is not null;
            row.Revert.IsEnabled = !_running && canRevertState && row.CanRevert && RevertRequested is not null;
            row.Revert.Content = new TextBlock { Text = row.State == MigrationItemState.RevertFailed ? "Retry reverting this change" : "Revert this incomplete change", TextWrapping = TextWrapping.Wrap };
            row.Revert.Visibility = canRevertState ? Visibility.Visible : Visibility.Collapsed;
            row.KeepOriginal.Visibility = canKeepState && row.CanKeepOriginal ? Visibility.Visible : Visibility.Collapsed;
            row.KeepOriginal.IsEnabled = !_running && canKeepState && row.CanKeepOriginal && KeepOriginalRequested is not null;
            row.ConfirmKeep.IsEnabled = row.KeepOriginal.IsEnabled;
            if (!canKeepState || !row.CanKeepOriginal) row.Confirmation.Visibility = Visibility.Collapsed;
        }
    }

    private void RequestKeepOriginal(ItemRow row)
    {
        if (_running || row.Confirmation.Visibility != Visibility.Visible || !row.CanKeepOriginal || KeepOriginalRequested is null ||
            row.State is not (MigrationItemState.Failed or MigrationItemState.Paused or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed)) return;
        BeginAction("Keeping original cloud location", row.Title, "Checking the original local references. Cloud copies and recovery records will be retained.");
        row.State = MigrationItemState.KeepingOriginal;
        row.Status.Text = StateLabel(row.State); row.Icon.Content = FluentIcons.Create(StateIcon(row.State), 20);
        RefreshActions();
        KeepOriginalRequested.Invoke(this, new(row.Id));
    }

    private static string StateLabel(MigrationItemState state) => state switch
    {
        MigrationItemState.Pending => "Waiting",
        MigrationItemState.Checking => "Checking",
        MigrationItemState.Running => "Migrating",
        MigrationItemState.Verifying => "Verifying",
        MigrationItemState.Completed => "Verified",
        MigrationItemState.Failed => "Failed",
        MigrationItemState.Reverting => "Reverting this incomplete change",
        MigrationItemState.Reverted => "Incomplete change reverted",
        MigrationItemState.RevertFailed => "Revert failed · Review required",
        MigrationItemState.Paused => "Paused",
        MigrationItemState.Blocked => "Waiting for a new review",
        MigrationItemState.KeepingOriginal => "Verifying original location",
        MigrationItemState.OriginalKept => "Original location kept",
        MigrationItemState.KeepOriginalFailed => "Original location needs verification",
        _ => "Waiting"
    };

    private static string StateIcon(MigrationItemState state) => state switch
    {
        MigrationItemState.Completed => "checkmark",
        MigrationItemState.Failed or MigrationItemState.RevertFailed or MigrationItemState.KeepOriginalFailed or MigrationItemState.Blocked => "warning",
        MigrationItemState.Pending or MigrationItemState.Paused => "clock",
        MigrationItemState.Reverted or MigrationItemState.OriginalKept => "info",
        _ => "sync"
    };

    private sealed class ItemRow
    {
        internal string Id { get; }
        internal string Title { get; }
        internal MigrationItemState State { get; set; } = MigrationItemState.Pending;
        internal bool CanRetry { get; set; }
        internal bool CanRevert { get; set; }
        internal bool CanKeepOriginal { get; set; }
        internal StackPanel Content { get; } = new() { Spacing = 8 };
        internal ContentControl Icon { get; } = new() { Content = FluentIcons.Create("clock", 20), VerticalAlignment = VerticalAlignment.Top };
        internal TextBlock Status { get; } = SourceImportDialog.Text("Waiting for required changes and checks.", true);
        internal ActionWrapPanel Actions { get; } = new() { Spacing = 8, Visibility = Visibility.Collapsed };
        internal Button Retry { get; } = SourceImportDialog.ActionButton("Retry this change");
        internal Button Revert { get; } = SourceImportDialog.ActionButton("Revert this incomplete change");
        internal Button KeepOriginal { get; } = SourceImportDialog.ActionButton("Keep original location…");
        internal Button ConfirmKeep { get; } = SourceImportDialog.ActionButton("Keep original and retain copies");
        internal Button CancelKeep { get; } = SourceImportDialog.ActionButton("Cancel");
        internal StackPanel Confirmation { get; } = new() { Spacing = 8, Visibility = Visibility.Collapsed };

        internal ItemRow(string id, string title)
        {
            Id = id;
            Title = title;
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            heading.Children.Add(Icon);
            var label = SourceImportDialog.Heading(title);
            Grid.SetColumn(label, 1);
            heading.Children.Add(label);
            Content.Children.Add(heading);
            Content.Children.Add(Status);
            Actions.Children.Add(Retry);
            Actions.Children.Add(Revert);
            Actions.Children.Add(KeepOriginal);
            Content.Children.Add(Actions);
            Confirmation.Children.Add(SourceImportDialog.Text("Keep the original cloud location for this change? CloudInlet will verify that local references have not switched. Partial cloud copies, uncertain requests and recovery records will remain retained and may use storage. This does not complete or revert migration. Completed changes stay in place.", true));
            var confirmationActions = new ActionWrapPanel { Spacing = 8 };
            confirmationActions.Children.Add(ConfirmKeep); confirmationActions.Children.Add(CancelKeep);
            Confirmation.Children.Add(confirmationActions); Content.Children.Add(Confirmation);
            AutomationProperties.SetName(Retry, "Retry " + title);
            AutomationProperties.SetName(Revert, "Revert incomplete change: " + title);
            AutomationProperties.SetName(KeepOriginal, "Keep original location: " + title);
            AutomationProperties.SetName(ConfirmKeep, "Confirm keeping original location and retaining partial copies: " + title);
        }
    }

    private void AddDetail(string detail, DateTimeOffset? time = null)
    {
        // Detail messages contain no payload, credential or token data; callers must redact secrets.
        var text = detail.Length <= MaximumDetailLength ? detail : detail[..MaximumDetailLength] + "…";
        // Include the date so a restored log spanning several launches is not misleading.
        _lines.Enqueue((time ?? DateTimeOffset.Now).ToLocalTime().ToString("g") + " · " + text);
        while (_lines.Count > MaximumLogLines) _lines.Dequeue();
        _log.Text = string.Join(Environment.NewLine, _lines);
    }

    private void Dispatch(Action update)
    {
        if (DispatcherQueue.HasThreadAccess) { if (!_closed) update(); }
        else DispatcherQueue.TryEnqueue(() => { if (!_closed) update(); });
    }
}

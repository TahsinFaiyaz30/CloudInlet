using CloudInlet.Application;
using CloudInlet.Core.Transfers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudInlet.Views;

internal sealed record CloudTransferDraft(CloudLocationSelection? Source, CloudLocationSelection? Destination,
    int Operation, int Conflicts, IReadOnlyList<TransferExclusionRule> Exclusions,
    string? EditingSide = null, CloudLocationSelection? PendingLocation = null);

/// <summary>Independent source and destination selection, followed by an explicit transfer review.</summary>
internal sealed class CloudTransferDialog : ContentDialog
{
    private readonly ClientController _controller;
    private readonly nint _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _body = new() { Spacing = 20 };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _sourceSummary = new() { Spacing = 12 }, _destinationSummary = new() { Spacing = 12 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true, Visibility = Visibility.Collapsed };
    private readonly ComboBox _operation = new() { Header = "Action", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36 };
    private readonly ComboBox _conflicts = new() { Header = "If the destination file exists", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36 };
    private readonly TransferExclusionEditor _exclusions;
    private readonly CloudLocationPicker _source, _destination;
    private readonly Grid _locations;
    private readonly Border _options;
    private readonly TransferLocation? _fixedSource, _fixedDestination;
    private readonly CloudTransferDraft? _draft;
    private readonly bool _preferOneDrive;
    private CloudLocationSelection? _selectedSource, _selectedDestination;
    private CloudLocationPicker? _activePicker;
    private bool _review, _working, _closed, _initialized, _signInPresentation, _disposed;
    private int _pending;
    internal TransferLocation? Source => _selectedSource?.Location;
    internal TransferLocation? Destination => _selectedDestination?.Location;
    internal TransferOperation Operation => _operation.SelectedIndex == 1 ? TransferOperation.Move : TransferOperation.Copy;
    internal TransferConflictPolicy Conflicts => (TransferConflictPolicy)Math.Max(0, _conflicts.SelectedIndex);
    internal IReadOnlyList<string> Exclusions => _exclusions.Patterns;
    internal string? RequestedConnectionProvider { get; private set; }

    internal CloudTransferDialog(ClientController controller, nint owner, TransferLocation? source = null,
        TransferLocation? destination = null, bool preferOneDrive = false, CloudTransferDraft? draft = null)
    {
        _controller = controller; _owner = owner; _fixedSource = source; _fixedDestination = destination;
        _draft = draft; _preferOneDrive = preferOneDrive;
        _selectedSource = source is null ? draft?.Source : new(source.Provider, source.AccountId, source);
        _selectedDestination = destination is null ? draft?.Destination : new(destination.Provider, destination.AccountId, destination);
        Title = "Transfer files"; PrimaryButtonText = "Review transfer"; CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.None;
        SourceImportDialog.ConfigureLayout(this, 1000);
        _operation.Items.Add("Copy — keep originals"); _operation.Items.Add("Move — remove verified originals");
        _operation.SelectedIndex = draft?.Operation ?? 0;
        _conflicts.Items.Add("Stop and ask for review"); _conflicts.Items.Add("Skip existing files");
        _conflicts.Items.Add("Replace destination files"); _conflicts.Items.Add("Keep both with a different name");
        _conflicts.SelectedIndex = draft?.Conflicts ?? 0;
        _exclusions = draft is null ? new TransferExclusionEditor(controller.Settings.Exclusions
                .Select(pattern => new TransferExclusionRule(pattern.Trim(),
                    !controller.Settings.DisabledLegacyExclusions.Contains(pattern, StringComparer.OrdinalIgnoreCase))))
            : new TransferExclusionEditor(draft.Exclusions);
        _exclusions.StateChanged += (_, _) => SelectionChanged();
        _source = new CloudLocationPicker(controller, owner, "Source", _lifetime.Token, SelectionChanged, ShowError, RequestConnection);
        _destination = new CloudLocationPicker(controller, owner, "Destination", _lifetime.Token, SelectionChanged, ShowError, RequestConnection);
        _locations = SourceImportDialog.PairedContent(SourceImportDialog.Surface(_sourceSummary), SourceImportDialog.Surface(_destinationSummary));
        var options = new StackPanel { Spacing = 12 };
        options.Children.Add(SourceImportDialog.Heading("Transfer options"));
        options.Children.Add(SourceImportDialog.PairedContent(_operation, _conflicts));
        _options = SourceImportDialog.Surface(options);
        _scroll = SourceImportDialog.ScrollContent(_body, 660);
        Content = _scroll;
        // A maximum width alone lets ContentDialog shrink to its shortest labels.
        // Give the paired pickers room, while following the actual window size.
        XamlRoot? layoutRoot = null;
        void ResizeContent(XamlRoot root) => _body.Width = Math.Min(940, Math.Max(0, root.Size.Width - 112));
        void RootChanged(XamlRoot root, XamlRootChangedEventArgs args) => ResizeContent(root);
        _body.Loaded += (_, _) =>
        {
            if (layoutRoot is not null) layoutRoot.Changed -= RootChanged;
            layoutRoot = _body.XamlRoot;
            if (layoutRoot is null) return;
            layoutRoot.Changed += RootChanged;
            ResizeContent(layoutRoot);
        };
        _body.Unloaded += (_, _) =>
        {
            if (layoutRoot is not null) layoutRoot.Changed -= RootChanged;
            layoutRoot = null;
        };
        PrimaryButtonClick += Primary_Click;
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (_working || _source.IsWorking || _destination.IsWorking) return;
            _activePicker = null; _review = false; RenderChoices();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); DisposeWhenIdle(); };
        Loaded += async (_, _) => { if (!_signInPresentation) await InitializeLocationsAsync(); };
        RenderChoices();
    }

    internal CloudTransferDraft CaptureDraft() => new(_selectedSource, _selectedDestination,
        _operation.SelectedIndex, _conflicts.SelectedIndex, _exclusions.Rules,
        _activePicker is null ? null : _activePicker == _source ? "source" : "destination", _activePicker?.Selection);

    private void RenderChoices()
    {
        Title = "Transfer files"; PrimaryButtonText = "Review transfer"; SecondaryButtonText = "";
        _body.Children.Clear();
        _body.Children.Add(SourceImportDialog.Text("Choose a source and destination. Review your choices before starting.", true));
        _body.Children.Add(_error); _body.Children.Add(_locations); _body.Children.Add(_options); _body.Children.Add(_exclusions);
        RenderLocationSummary(_sourceSummary, Source, false);
        RenderLocationSummary(_destinationSummary, Destination, true);
        _scroll.ChangeView(null, 0, null, true);
        SelectionChanged();
    }

    private void RenderLocationSummary(StackPanel summary, TransferLocation? location, bool destination)
    {
        var name = destination ? "destination" : "source";
        summary.Children.Clear();
        summary.Children.Add(SourceImportDialog.Heading(destination ? "Destination" : "Source"));
        if (location is not null)
        {
            summary.Children.Add(SourceImportDialog.Text(location.DisplayName));
            summary.Children.Add(SourceImportDialog.Text(location.Path.Length == 0 ? "Root folder" : location.Path, true));
        }
        else summary.Children.Add(SourceImportDialog.Text(destination ? "Where your files will go." : "Where your files come from.", true));
        if ((destination ? _fixedDestination : _fixedSource) is not null) return;
        var choose = SourceImportDialog.ActionButton((location is null ? "Choose " : "Change ") + name);
        choose.Click += async (_, _) => await OpenPickerAsync(destination);
        summary.Children.Add(choose);
    }

    private async Task OpenPickerAsync(bool destination, CloudLocationSelection? pending = null)
    {
        if (_closed || _working || _source.IsWorking || _destination.IsWorking) return;
        _activePicker = destination ? _destination : _source;
        var name = destination ? "destination" : "source";
        Title = "Choose " + name; PrimaryButtonText = "Use " + name; SecondaryButtonText = "Back";
        _error.IsOpen = false; _error.Visibility = Visibility.Collapsed;
        _body.Children.Clear();
        _body.Children.Add(SourceImportDialog.Text("Search your services and accounts, then choose a folder.", true));
        _body.Children.Add(_error); _body.Children.Add(_activePicker.Surface);
        _scroll.ChangeView(null, 0, null, true);
        _working = true; _pending++; SelectionChanged();
        try
        {
            await _activePicker.InitializeAsync(null, pending ?? (destination ? _selectedDestination : _selectedSource));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
        finally { _working = false; _pending--; DisposeWhenIdle(); SelectionChanged(); }
    }

    private async Task InitializeLocationsAsync()
    {
        if (_closed || _initialized || _working) return;
        _initialized = true;
        if (_draft?.EditingSide is { } side)
            await OpenPickerAsync(side == "destination", _draft.PendingLocation);
        else if (_preferOneDrive && _fixedSource is null && _selectedSource is null)
            await OpenPickerAsync(false, new("onedrive", null, null));
        SelectionChanged();
    }

    private void RequestConnection(string provider)
    {
        if (_closed || _working || _source.IsWorking || _destination.IsWorking) return;
        if (_exclusions.IsEditing) { ShowError(new InvalidOperationException("Save or cancel the exclusion you are editing before opening a connection page.")); return; }
        RequestedConnectionProvider = provider;
        Hide();
    }

    private void SelectionChanged()
    {
        if (_closed || _source is null || _destination is null || _exclusions is null) return;
        var idle = !_working && !_source.IsWorking && !_destination.IsWorking;
        IsSecondaryButtonEnabled = idle;
        IsPrimaryButtonEnabled = idle && (_activePicker is not null ? _activePicker.Location is not null :
            !_exclusions.IsEditing && Source is not null && Destination is not null);
    }
    private void ShowError(Exception error)
    { if (_closed) return; _error.Message = error.Message; _error.Visibility = Visibility.Visible; _error.IsOpen = true; }
    private void DisposeWhenIdle()
    {
        if (!_closed || _pending != 0 || _disposed) return;
        _disposed = true; _source.Dispose(); _destination.Dispose(); _lifetime.Dispose();
    }
    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_working || _source.IsWorking || _destination.IsWorking)
        { args.Cancel = true; return; }
        if (_activePicker is not null)
        {
            args.Cancel = true;
            if (_activePicker.Location is null) return;
            if (_activePicker == _source) _selectedSource = _activePicker.Selection;
            else _selectedDestination = _activePicker.Selection;
            _activePicker = null;
            RenderChoices();
            return;
        }
        if (_exclusions.IsEditing || Source is null || Destination is null) { args.Cancel = true; return; }
        if (_review) return;
        args.Cancel = true;
        // Reject overlapping endpoints before the Start action is offered.
        try
        {
            if (_fixedSource?.Provider == "b2" && Destination.Provider != "onedrive")
                throw new InvalidOperationException("Choose OneDrive when moving a backup to another cloud. Use the folder backup options to restore files to this PC.");
            if (Source.Provider == Destination.Provider)
                throw new InvalidOperationException("Choose different source and destination services. Transfers within the same service are not available yet.");
            TransferValidation.ValidatePlan(new TransferJobPlan(Guid.NewGuid().ToString("N"), Source, Destination,
                Operation, Conflicts, Exclusions, DateTimeOffset.UtcNow));
            RenderReview();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void RenderReview()
    {
        if (Source is null || Destination is null) return;
        _review = true; Title = "Review transfer"; PrimaryButtonText = "Start " + Operation.ToString().ToLowerInvariant(); SecondaryButtonText = "Back";
        _body.Children.Clear();
        _body.Children.Add(SourceImportDialog.PairedContent(
            SourceImportDialog.PathCard("Source", Source.DisplayName, Source.Path.Length == 0 ? "Root folder" : Source.Path),
            SourceImportDialog.PathCard("Destination", Destination.DisplayName, Destination.Path.Length == 0 ? "Root folder" : Destination.Path)));
        _body.Children.Add(SourceImportDialog.Text(Operation == TransferOperation.Move
            ? "Each destination is verified before CloudInlet deletes the exact, unchanged source file. Changed files stay at the source and need review."
            : "CloudInlet verifies each destination copy. Original source files stay in the selected folder.", true));
        _body.Children.Add(SourceImportDialog.Text("Conflicts: " + _conflicts.SelectedItem + Environment.NewLine +
            (Exclusions.Count == 0 ? "No exclusions" : "Exclusions: " + string.Join(", ", Exclusions)), true));
        _body.Children.Add(SourceImportDialog.Text("Activity shows progress and pause, resume, or cancel controls. Cloud-to-cloud transfers pass through this PC without saving a temporary local copy.", true));
        _body.Children.Add(_error);
    }
    internal async Task ShowReviewPresentationAsync()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) throw new InvalidOperationException("Cloud transfer presentation requires isolated UI validation.");
        await InitializeLocationsAsync(); RenderReview();
    }
    internal void ShowSignInPresentation(bool advanced = false)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) throw new InvalidOperationException("Sign-in presentation requires isolated UI validation.");
        _signInPresentation = true; Title = "Connect OneDrive"; PrimaryButtonText = ""; SecondaryButtonText = "";
        _body.Children.Clear(); _body.Children.Add(new OneDriveConnectionView(_controller, _owner, advanced));
    }
}

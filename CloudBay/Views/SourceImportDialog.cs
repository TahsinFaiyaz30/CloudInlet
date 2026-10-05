using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.Sync;
using CloudBay.ViewModels;
using CloudBay.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;

namespace CloudBay.Views;

/// <summary>Progressively chooses and reviews a copy source without changing its ownership.</summary>
internal sealed class SourceImportDialog : ContentDialog
{
    private readonly ClientController _controller;
    private readonly nint _owner;
    private readonly bool _chooseFolderOnly;
    private readonly string? _knownFolderName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _body = new() { Spacing = 20 };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 540, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly ProgressRing _progress = new() { Width = 24, Height = 24, Visibility = Visibility.Collapsed };
    private readonly TextBox _destination = new() { Header = "Folder inside CloudBay", PlaceholderText = "For example, Imported files" };
    private readonly TextBox _prefix = new() { Header = "Source folder (optional)", PlaceholderText = "Leave empty to use the whole bucket" };
    private readonly ComboBox _bucket = new() { Header = "Source bucket", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    private string _stage = "choose";
    private string _folder = "";
    private string _sourceName = "";
    private bool _working;
    private bool _closed;
    private bool _lifetimeDisposed;
    private int _pendingOperations;
    private int _stageGeneration;
    private Task<IReadOnlyList<ImportSourceCandidate>>? _discovery;
    private readonly List<(Control Control, bool Enabled)> _disabledControls = [];
    private string? _reviewedResumeId;
    private readonly IReadOnlyList<ImportSourceCandidate>? _presentationCandidates;
    private sealed record HistoryEntry(string Source, string Destination, string State,
        DateTimeOffset StartedUtc, string? Error, Action? Review);
    private IReadOnlyList<HistoryEntry> _history = [];
    private int _historyPage;
    private int _historyIssues;
    private ImportHistoryCursor? _folderCursor, _cloudCursor;
    private bool _olderFolders, _olderClouds;

    internal FolderImportPlan? FolderPlan { get; private set; }
    internal CloudImportPlan? CloudPlan { get; private set; }
    internal string? ResumeCloudId { get; private set; }
    internal string? SelectedFolderPath { get; private set; }
    internal bool DirectCloudTransferRequested { get; private set; }
    internal FrameworkElement CaptureContent => _body;

    internal SourceImportDialog(ClientController controller, nint owner, bool chooseFolderOnly = false,
        IReadOnlyList<ImportSourceCandidate>? presentationCandidates = null, string? initialSourcePath = null,
        string? knownFolderName = null)
    {
        _controller = controller;
        _owner = owner;
        _chooseFolderOnly = chooseFolderOnly;
        _knownFolderName = knownFolderName;
        _presentationCandidates = presentationCandidates;
        Title = chooseFolderOnly ? "Choose an additional source" : "Import files";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.None;
        Resources["ContentDialogMaxWidth"] = 640d;
        var content = new StackPanel { Spacing = 12, MinWidth = 0 };
        content.Children.Add(_error);
        _scroll.Content = _body;
        content.Children.Add(_scroll);
        content.Children.Add(_progress);
        Content = content;
        _destination.TextChanged += (_, _) => UpdatePrimary();
        _bucket.SelectionChanged += (_, _) => UpdatePrimary();
        PrimaryButtonClick += Primary_Click;
        SecondaryButtonClick += (_, args) => { args.Cancel = true; if (!_working) ShowChoices(); };
        CloseButtonClick += (_, _) => CancelLifetime();
        Closed += (_, _) => { _closed = true; CancelLifetime(); DisposeLifetimeWhenIdle(); };
        if (initialSourcePath is { Length: > 0 }) ShowFolder(initialSourcePath, Path.GetFileName(Path.TrimEndingDirectorySeparator(initialSourcePath)));
        else ShowChoices();
    }

    private void BeginStage(string stage, string title, string primary = "")
    {
        _stage = stage;
        _stageGeneration++;
        Title = title;
        _body.Children.Clear();
        _scroll.ChangeView(null, 0, null, true);
        _error.IsOpen = false;
        PrimaryButtonText = primary;
        DefaultButton = stage == "review" ? ContentDialogButton.Primary : ContentDialogButton.None;
        SecondaryButtonText = stage == "choose" ? "" : "Back";
        FolderPlan = null;
        CloudPlan = null;
        ResumeCloudId = null;
        _reviewedResumeId = null;
        DirectCloudTransferRequested = false;
        UpdatePrimary();
    }

    private void ShowChoices()
    {
        BeginStage("choose", _chooseFolderOnly ? "Choose a source" : "Import files");
        _body.Children.Add(Text(_chooseFolderOnly
            ? "Choose the folder whose files you want to bring into CloudBay. You will review the source and transfer choice before anything changes."
            : "Bring existing files into CloudBay. Your source files stay where they are.", secondary: true));
        AddAsyncChoice("Folder on this PC", "Choose a local folder, an external drive, or a mounted cloud drive.", "\uE8B7", BrowseAsync);
        AddAsyncChoice("Files from another app", "Choose an existing Windows cloud folder, including OneDrive.", "\uE753", ShowExistingFoldersAsync);
        if (!_chooseFolderOnly)
        {
            AddChoice("Cloud storage", "Browse OneDrive accounts directly or import from a Backblaze B2 bucket.", "\uE753", ShowProviders);
            if (_presentationCandidates is null)
            {
                // History is secondary navigation. Loading it happens only when requested,
                // so large checkpoints cannot stall the initial source choices.
                var history = new HyperlinkButton { Content = "Import history", HorizontalAlignment = HorizontalAlignment.Left };
                history.Click += async (_, _) => await ShowHistoryAsync();
                _body.Children.Add(history);
            }
        }
    }

    private async Task BrowseAsync()
    {
        if (_working || _closed || _presentationCandidates is not null) return;
        var generation = _stageGeneration;
        SetWorking(true);
        var token = EnterOperation();
        try
        {
            var folder = await DesktopPickers.PickFolderAsync(_owner, "Choose source folder", token);
            if (folder is not null && !_closed && generation == _stageGeneration)
                ShowFolder(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && generation == _stageGeneration) ShowError(error); }
        finally { SetWorking(false); LeaveOperation(); }
    }

    internal async Task ShowExistingFoldersAsync()
    {
        if (_closed) return;
        BeginStage("existing", _knownFolderName is null ? "Existing cloud folders" : _knownFolderName + " in other clouds");
        _body.Children.Add(Text("These are folders found in Windows. Choose the source yourself; CloudBay does not disconnect or remove another app.", true));
        var rows = new StackPanel { Spacing = 8 };
        _body.Children.Add(rows);
        var browse = new Button { Content = "Browse for a folder…", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += async (_, _) => await BrowseAsync();
        _body.Children.Add(browse);
        _body.Children.Add(Text("Keep the source app signed in and running if any of its files are online only. Mounted drives can also be chosen with Browse.", true));
        var generation = _stageGeneration;
        if (_presentationCandidates is not null)
        {
            RenderCandidates(rows, _presentationCandidates);
            return;
        }
        var waiting = new ProgressRing { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left };
        rows.Children.Add(waiting);
        var token = EnterOperation();
        try
        {
            var settings = _controller.Settings;
            // Discovery reads only registered roots and top-level folder hints. A stalled
            // mounted provider must not block the dispatcher or prevent browsing manually.
            if (_discovery is null)
            {
                _discovery = Task.Run(() => ImportSourceDiscovery.FindCandidates(settings, _knownFolderName));
                _ = _discovery.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            var candidates = await _discovery.WaitAsync(TimeSpan.FromSeconds(8), token);
            if (!_closed && generation == _stageGeneration) RenderCandidates(rows, candidates);
        }
        catch (TimeoutException)
        {
            if (!_closed && generation == _stageGeneration)
            {
                rows.Children.Clear();
                rows.Children.Add(Text("Windows is taking longer to find cloud folders. Browse to your source folder to continue.", true));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && generation == _stageGeneration) ShowError(error); }
        finally { LeaveOperation(); }
    }

    private void RenderCandidates(StackPanel rows, IReadOnlyList<ImportSourceCandidate> candidates)
    {
        rows.Children.Clear();
        foreach (var candidate in candidates)
        {
            var selected = candidate;
            var card = new SettingsCard { Header = candidate.DisplayName,
                Description = candidate.ProviderName + " · " + candidate.Kind + Environment.NewLine + candidate.Path,
                HeaderIcon = new FontIcon { Glyph = "\uE8B7" }, IsClickEnabled = true };
            card.Click += (_, _) => { if (!_working && !_closed) ShowFolder(selected.Path, selected.DisplayName); };
            rows.Children.Add(card);
        }
        if (candidates.Count == 0) rows.Children.Add(Text("No other cloud folders were found. You can still browse to an existing source.", true));
    }

    private void ShowFolder(string path, string label, string? destination = null)
    {
        _folder = path;
        _sourceName = label;
        BeginStage("folder", _chooseFolderOnly ? "Use this source?" : "Import from a folder", _chooseFolderOnly ? "Use folder" : "Review import");
        _body.Children.Add(PathCard("Source", label, path));
        if (_chooseFolderOnly)
        {
            _body.Children.Add(Text("The backup review will show this exact source and your transfer choice before any files are copied or moved.", true));
        }
        else
        {
            _destination.Text = destination ?? SafeDestinationName(label);
            _body.Children.Add(_destination);
            _body.Children.Add(Text("Destination: " + _controller.Settings.RootPath, true));
            _body.Children.Add(Text("Files are copied and verified. Differing files already at the destination are retained as separate copies.", true));
        }
        UpdatePrimary();
    }

    internal void ShowProviders()
    {
        BeginStage("providers", "Cloud storage");
        _body.Children.Add(Text("Your connected B2 account can import from the buckets its application key is allowed to access.", true));
        AddAsyncChoice("Backblaze B2", "Connected account · Available now", "\uE753", ShowB2Async);
        AddChoice("OneDrive ↔ Backblaze B2", "Connect an account and browse actual cloud folders. Choose Copy or Move, exclusions, and conflicts.", "\uE753", () =>
        {
            BeginStage("direct", "Direct cloud transfer", "Choose cloud locations");
            DirectCloudTransferRequested = true;
            _body.Children.Add(Text("Choose a source and destination in OneDrive and Backblaze B2. CloudBay streams content through bounded memory and verifies each copy. Activity keeps recoverable job progress.", true));
            UpdatePrimary();
        });
        var planned = new StackPanel { Spacing = 8 };
        foreach (var provider in ProductCatalog.Providers.Where(item => !item.Available && item.Id != "onedrive"))
        {
            var row = new SettingsCard { Header = provider.Name, Description = provider.Group,
                Content = Text("Coming soon", true), IsEnabled = false };
            AutomationProperties.SetName(row, provider.Name + ", coming soon");
            planned.Children.Add(row);
        }
        _body.Children.Add(new Expander { Header = "More providers · Coming soon", Content = planned, HorizontalAlignment = HorizontalAlignment.Stretch });
        var browse = new Button { Content = "Use an existing Windows folder…", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += async (_, _) => await ShowExistingFoldersAsync();
        _body.Children.Add(browse);
    }

    private async Task ShowB2Async()
    {
        if (_working || _closed) return;
        SetWorking(true);
        var token = EnterOperation();
        try
        {
            var buckets = await _controller.GetImportBucketsAsync(token);
            if (!_closed) ShowB2Configuration(buckets);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally { SetWorking(false); LeaveOperation(); }
    }

    internal void ShowB2Configuration(IReadOnlyList<CloudBucket> buckets)
    {
        BeginStage("cloud", "Import from Backblaze B2", "Review import");
        _body.Children.Add(Text("Connected account", true));
        _bucket.ItemsSource = buckets;
        _bucket.SelectedIndex = buckets.Count == 1 ? 0 : -1;
        _body.Children.Add(_bucket);
        _body.Children.Add(_prefix);
        _destination.Text = "Imported B2 files";
        _body.Children.Add(_destination);
        _body.Children.Add(Text("Copies are made in B2 without downloading the source through this PC. Choose a new, empty destination folder; original cloud versions are retained.", true));
        if (buckets.Count == 0) _body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Message = "No source buckets are accessible to the connected application key. Check its allowed buckets and permissions." });
        UpdatePrimary();
    }

    private async Task ShowHistoryAsync(bool older = false)
    {
        if (_working || _closed) return;
        BeginStage("history", "Import history");
        _body.Children.Add(Text("Interrupted imports need your review. Nothing resumes automatically.", true));
        var generation = _stageGeneration;
        var token = EnterOperation();
        SetWorking(true);
        try
        {
            var result = await Task.Run(() =>
            {
                var folders = older && !_olderFolders ? Array.Empty<FolderImportRecord>() : _controller.GetImportHistory(older ? _folderCursor : null);
                var folderIssues = _controller.ImportHistoryIssueCount;
                var moreFolders = (!older || _olderFolders) && _controller.HasOlderFolderImports;
                var clouds = older && !_olderClouds ? Array.Empty<CloudImportRecord>() : _controller.GetCloudImportHistory(older ? _cloudCursor : null);
                return (Folders: folders, Clouds: clouds, Issues: folderIssues + _controller.CloudImportHistoryIssueCount,
                    MoreFolders: moreFolders, MoreClouds: (!older || _olderClouds) && _controller.HasOlderCloudImports);
            }, token).WaitAsync(token);
            if (_closed || generation != _stageGeneration) return;
            _olderFolders = result.MoreFolders;
            _olderClouds = result.MoreClouds;
            _folderCursor = result.Folders.LastOrDefault() is { } lastFolder ?
                new(lastFolder.State == "Completed", lastFolder.StartedUtc, lastFolder.Id) : _folderCursor;
            _cloudCursor = result.Clouds.LastOrDefault() is { } lastCloud ?
                new(lastCloud.State == "Completed", lastCloud.StartedUtc, lastCloud.Id) : _cloudCursor;
            PopulateHistory(result.Folders, result.Clouds, result.Issues);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally { SetWorking(false); LeaveOperation(); }
    }

    internal void ShowHistoryPresentation(IReadOnlyList<FolderImportRecord> folders, IReadOnlyList<CloudImportRecord> clouds, int issues)
    {
        if (_presentationCandidates is null) throw new InvalidOperationException("History presentation requires the UI validation fixture.");
        BeginStage("history", "Import history");
        PopulateHistory(folders, clouds, issues);
    }

    private void PopulateHistory(IReadOnlyList<FolderImportRecord> folders, IReadOnlyList<CloudImportRecord> clouds, int issues)
    {
        var entries = new List<HistoryEntry>();
        foreach (var record in folders)
        {
            var item = record;
            entries.Add(new(record.Plan.SourcePath, record.Plan.DestinationPath, record.State, record.StartedUtc, record.Error,
                record.State == "Completed" ? null : () => ShowFolder(item.Plan.SourcePath, Path.GetFileName(item.Plan.SourcePath),
                    Path.GetRelativePath(_controller.Settings.RootPath, item.Plan.DestinationPath))));
        }
        foreach (var record in clouds)
        {
            var item = record;
            entries.Add(new("B2 · " + record.Plan.SourceBucketId + " / " + record.Plan.SourcePrefix,
                record.Plan.DestinationPath, record.State, record.StartedUtc, record.Error,
                record.State == "Completed" ? null : () => ShowCloudReview(item.Plan, item.Id, item.Completed.Count)));
        }
        // Merge both kinds before ordering: an interrupted cloud job must not be
        // hidden behind completed local jobs, regardless of its original date.
        _history = entries.OrderBy(item => item.State == "Completed" ? 1 : 0).ThenByDescending(item => item.StartedUtc).ToArray();
        _historyPage = 0;
        _historyIssues = issues;
        RenderHistory();
    }

    private void RenderHistory()
    {
        _body.Children.Clear();
        _body.Children.Add(Text("Interrupted imports need your review. Nothing resumes automatically.", true));
        if (_historyIssues > 0)
            _body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Message = $"{_historyIssues:N0} damaged or unreadable import {(_historyIssues == 1 ? "record was" : "records were")} retained. Source files were unchanged. Checkpoints that could not be safely read cannot be continued here." });
        const int pageSize = 10;
        var start = _historyPage * pageSize;
        var pending = _history.Count(item => item.State != "Completed");
        if (_history.Count > 0) _body.Children.Add(Text($"{_history.Count:N0} imports · {pending:N0} need review · " +
            $"Page {_historyPage + 1} of {(_history.Count + pageSize - 1) / pageSize}", true));
        if (_history.Count == 0) _body.Children.Add(Text("No readable imports for this connected account and CloudBay folder.", true));
        foreach (var record in _history.Skip(start).Take(pageSize))
        {
            var item = record;
            var card = HistoryCard(item.Source, item.Destination, item.State, item.StartedUtc, item.Error);
            if (item.Review is { } review)
            {
                var button = new Button { Content = "Review and continue" };
                button.Click += (_, _) => review();
                card.Content = button;
            }
            _body.Children.Add(card);
        }
        if (_history.Count > pageSize)
        {
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var previous = new Button { Content = "Previous", IsEnabled = _historyPage > 0 };
            previous.Click += (_, _) => NavigateHistoryPage(-1);
            var next = new Button { Content = "Next", IsEnabled = start + pageSize < _history.Count };
            next.Click += (_, _) => NavigateHistoryPage(1);
            navigation.Children.Add(previous);
            navigation.Children.Add(Text($"Page {_historyPage + 1} of {(_history.Count + pageSize - 1) / pageSize}", true));
            navigation.Children.Add(next);
            _body.Children.Add(navigation);
        }
        if (_presentationCandidates is null && (_olderFolders || _olderClouds))
        {
            var older = new Button { Content = "Load older imports", HorizontalAlignment = HorizontalAlignment.Left };
            older.Click += async (_, _) => await ShowHistoryAsync(older: true);
            _body.Children.Add(older);
        }
        var page = _historyPage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed && _stage == "history" && _historyPage == page) _scroll.ChangeView(null, 0, null, true);
        });
    }

    internal bool CanAdvanceHistoryPage => (_historyPage + 1) * 10 < _history.Count;
    internal bool IsHistoryDestinationDisplayed(string destination) =>
        _body.Children.OfType<SettingsCard>().Any(card => card.Tag as string == destination);
    internal void NavigateHistoryPage(int delta)
    {
        var page = _historyPage + delta;
        if (page < 0 || page * 10 >= _history.Count) return;
        _historyPage = page;
        RenderHistory();
    }
    internal void ShowHistoryDestinationInViewport(string destination)
    {
        var card = _body.Children.OfType<SettingsCard>().Single(item => item.Tag as string == destination);
        card.StartBringIntoView();
    }

    private static SettingsCard HistoryCard(string source, string destination, string state, DateTimeOffset started, string? error) =>
        new() { Header = Path.GetFileName(destination), Tag = destination, Description = source + Environment.NewLine + destination + Environment.NewLine +
            started.ToLocalTime().ToString("g") + " · " + state + (error is null ? "" : Environment.NewLine + error),
            HeaderIcon = new FontIcon { Glyph = state == "Completed" ? "\uE73E" : "\uE81C" } };

    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_stage == "direct") return;
        if (_stage == "review") { ResumeCloudId = _reviewedResumeId; return; }
        if (_stage == "folder" && _chooseFolderOnly) { SelectedFolderPath = _folder; return; }
        args.Cancel = true;
        if (_working || _closed || _presentationCandidates is not null) return;
        var deferral = args.GetDeferral();
        SetWorking(true);
        var token = EnterOperation();
        try
        {
            if (_stage == "folder")
            {
                var plan = await _controller.PreviewFolderImportAsync(_folder, _destination.Text.Trim(), token);
                if (!_closed) ShowFolderReview(plan, _sourceName);
            }
            else if (_stage == "cloud" && _bucket.SelectedItem is CloudBucket bucket)
            {
                var plan = await _controller.PreviewCloudImportAsync(bucket.Id, _prefix.Text.Trim(), _destination.Text.Trim(), token);
                if (!_closed) ShowCloudReview(plan, null, 0, bucket.Name);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally { SetWorking(false); LeaveOperation(); deferral.Complete(); }
    }

    internal void ShowFolderReview(FolderImportPlan plan, string label)
    {
        BeginStage("review", "Review import", "Import files");
        FolderPlan = plan;
        _body.Children.Add(PathCard("Source", label, plan.SourcePath));
        _body.Children.Add(PathCard("Destination", Path.GetFileName(plan.DestinationPath), plan.DestinationPath));
        _body.Children.Add(Summary(plan.FileCount, plan.TotalBytes, plan.AvailableBytes));
        if (plan.HasOnlineOnlyFiles) _body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Message = "Some source files are online only. Keep their cloud app signed in and running until this import finishes." });
        _body.Children.Add(Text("Your source is retained. Each copy is verified before it is accepted. Differing destination files are preserved; CloudBay then backs up the imported files.", true));
        UpdatePrimary();
    }

    internal void ShowCloudReview(CloudImportPlan plan, string? resumeId = null, int completed = 0, string? bucketName = null)
    {
        BeginStage("review", resumeId is null ? "Review cloud import" : "Continue cloud import?", resumeId is null ? "Import files" : "Continue import");
        CloudPlan = plan;
        _reviewedResumeId = resumeId;
        _body.Children.Add(PathCard("Source", "Backblaze B2 · " + (bucketName ?? plan.SourceBucketId),
            string.IsNullOrEmpty(plan.SourcePrefix) ? "Whole bucket" : plan.SourcePrefix));
        _body.Children.Add(PathCard("Destination", Path.GetFileName(plan.DestinationPath), plan.DestinationPath));
        _body.Children.Add(Summary(plan.FileCount, plan.TotalBytes, null));
        if (resumeId is not null) _body.Children.Add(Text($"{completed:N0} completed file versions are already recorded. CloudBay checks them before continuing the remaining copies.", true));
        _body.Children.Add(Text("Copies are verified in B2. The original cloud versions are retained. Files appear in CloudBay with Windows Files On-Demand.", true));
        UpdatePrimary();
    }

    private void SetWorking(bool working)
    {
        _working = working;
        if (_closed) return;
        _body.IsHitTestVisible = !working;
        if (working) DisableControls(_body);
        else
        {
            foreach (var (control, enabled) in _disabledControls) control.IsEnabled = enabled;
            _disabledControls.Clear();
        }
        _progress.IsActive = working;
        _progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        IsSecondaryButtonEnabled = !working;
        UpdatePrimary();
    }

    private void UpdatePrimary() => IsPrimaryButtonEnabled = !_working && (_stage is "review" or "direct" ||
        _stage == "folder" && (_chooseFolderOnly || !string.IsNullOrWhiteSpace(_destination.Text)) ||
        _stage == "cloud" && _bucket.SelectedItem is CloudBucket && !string.IsNullOrWhiteSpace(_destination.Text));

    private void ShowError(Exception error)
    {
        _error.Message = string.IsNullOrWhiteSpace(error.Message)
            ? "Windows could not complete this step. Try again. Your original files have been kept."
            : error.Message;
        _error.IsOpen = true;
    }

    private void DisableControls(DependencyObject node)
    {
        if (node is Control control)
        {
            _disabledControls.Add((control, control.IsEnabled));
            control.IsEnabled = false;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) DisableControls(VisualTreeHelper.GetChild(node, i));
    }

    private CancellationToken EnterOperation() { _pendingOperations++; return _lifetime.Token; }
    private void LeaveOperation() { _pendingOperations--; DisposeLifetimeWhenIdle(); }
    private void CancelLifetime() { if (!_lifetimeDisposed) _lifetime.Cancel(); }
    private void DisposeLifetimeWhenIdle()
    {
        if (_closed && _pendingOperations == 0 && !_lifetimeDisposed)
        {
            _lifetimeDisposed = true;
            _lifetime.Dispose();
        }
    }

    private void AddChoice(string header, string description, string glyph, Action action)
    {
        var card = new SettingsCard { Header = header, Description = description, HeaderIcon = new FontIcon { Glyph = glyph }, IsClickEnabled = true };
        card.Click += (_, _) => { if (!_working) action(); };
        _body.Children.Add(card);
    }

    private void AddAsyncChoice(string header, string description, string glyph, Func<Task> action)
    {
        var card = new SettingsCard { Header = header, Description = description, HeaderIcon = new FontIcon { Glyph = glyph }, IsClickEnabled = true };
        card.Click += async (_, _) => { if (!_working) await action(); };
        _body.Children.Add(card);
    }

    internal static TextBlock Text(string value, bool secondary = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[secondary ? "CloudBayBodySecondaryTextStyle" : "BodyTextBlockStyle"] };

    internal static SettingsCard PathCard(string heading, string name, string path) => new()
    {
        Header = heading + " · " + name, Description = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CloudBayBodySecondaryTextStyle"] },
        HeaderIcon = new FontIcon { Glyph = "\uE8B7" }
    };

    internal static SettingsCard Summary(long files, long bytes, long? available) => new()
    {
        Header = $"{files:N0} {(files == 1 ? "file" : "files")} · {ClientViewModel.FormatSize(bytes)}",
        Description = available is { } free ? ClientViewModel.FormatSize(free) + " available on the destination drive" : "Reviewed file contents",
        HeaderIcon = new FontIcon { Glyph = "\uE8A5" }
    };

    private static string SafeDestinationName(string label)
    {
        var name = new string(label.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(name) ? "Imported files" : name;
    }
}

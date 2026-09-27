using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudBay.Application;
using CloudBay.Core.Filters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudBay.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ICloudBayFacade _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _filtersDirty;
    private bool _applyingSnapshot;
    private string _lastRootPath = string.Empty;
    private bool _disposed;
    private bool _hasScannedOrphans;

    private string _rootPathInput = string.Empty;
    private string _rootPath = "No cloud root selected";
    private string _rootHealth = "Not configured";
    private string _rootMessage = "Choose a mounted cloud folder to begin.";
    private string _storageSummary = "Storage information will appear after connecting.";
    private string _fileSystem = "—";
    private double _storageUsedPercent;
    private bool _isRootConfigured;
    private bool _isRootReachable;
    private bool _canWriteToRoot;
    private bool _isBusy;
    private string _busyMessage = "Ready";
    private string _lastRefreshText = string.Empty;
    private bool _isAlertOpen;
    private string _alertTitle = string.Empty;
    private string _alertMessage = string.Empty;
    private InfoBarSeverity _alertSeverity = InfoBarSeverity.Informational;
    private bool _includeDownloads;
    private string _selectedTheme = "System";
    private string _customLocalPath = string.Empty;
    private string _customTargetName = string.Empty;
    private string _newCustomPattern = string.Empty;
    private bool _filterGit = true;
    private bool _filterMountainDuck = true;
    private bool _filterCyberduck = true;
    private bool _filterRclone = true;
    private int _managedFoldersCount;
    private int _customLinksCount;
    private int _activeFiltersCount;
    private double _progressPercent;
    private bool _isProgressIndeterminate = true;

    public ShellViewModel(ICloudBayFacade facade)
    {
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _facade.ProgressChanged += Facade_ProgressChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ConfigureRootCommand = new AsyncRelayCommand(ConfigureRootAsync);
        LinkFolderCommand = new AsyncRelayCommand(LinkFolderAsync);
        SaveFiltersCommand = new AsyncRelayCommand(SaveFiltersAsync);
        AddCustomPatternCommand = new RelayCommand(AddCustomPattern);
        RefreshOrphansCommand = new AsyncRelayCommand(RefreshOrphansAsync);

        foreach (var (name, description) in new (string, string)[]
        {
            ("node_modules", "Node.js installed dependencies"),
            (".pnpm", "pnpm package store"),
            (".next", "Next.js build output"),
            (".turbo", "Turborepo task cache"),
            (".cache", "General build and tool caches"),
            ("__pycache__", "Python bytecode cache"),
            (".venv", "Hidden Python virtual environment"),
            ("venv", "Python virtual environment"),
            ("target", "Rust and Java build output"),
            ("bin", "Compiled binaries"),
            ("obj", ".NET intermediate files")
        })
        {
            var preset = new FilterPresetViewModel(name, description);
            preset.PropertyChanged += Preset_PropertyChanged;
            FilterPresets.Add(preset);
        }
    }

    public ObservableCollection<KnownFolderItemViewModel> KnownFolders { get; } = new();
    public ObservableCollection<CustomLinkItemViewModel> CustomLinks { get; } = new();
    public ObservableCollection<JournalItemViewModel> Journals { get; } = new();
    public ObservableCollection<JournalItemViewModel> RecentJournals { get; } = new();
    public ObservableCollection<FilterPresetViewModel> FilterPresets { get; } = new();
    public ObservableCollection<CustomPatternItemViewModel> CustomPatterns { get; } = new();
    public ObservableCollection<OrphanedFolderItemViewModel> OrphanedFolders { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ConfigureRootCommand { get; }
    public IAsyncRelayCommand LinkFolderCommand { get; }
    public IAsyncRelayCommand SaveFiltersCommand { get; }
    public IRelayCommand AddCustomPatternCommand { get; }
    public IAsyncRelayCommand RefreshOrphansCommand { get; }

    public string RootPathInput { get => _rootPathInput; set => SetProperty(ref _rootPathInput, value); }
    public string RootPath { get => _rootPath; private set => SetProperty(ref _rootPath, value); }
    public string RootHealth { get => _rootHealth; private set => SetProperty(ref _rootHealth, value); }
    public string RootMessage { get => _rootMessage; private set => SetProperty(ref _rootMessage, value); }
    public string StorageSummary { get => _storageSummary; private set => SetProperty(ref _storageSummary, value); }
    public string FileSystem { get => _fileSystem; private set => SetProperty(ref _fileSystem, value); }
    public double StorageUsedPercent { get => _storageUsedPercent; private set => SetProperty(ref _storageUsedPercent, value); }
    public bool IsRootConfigured { get => _isRootConfigured; private set => SetProperty(ref _isRootConfigured, value); }
    public bool IsRootReachable
    {
        get => _isRootReachable;
        private set { if (SetProperty(ref _isRootReachable, value)) OnPropertyChanged(nameof(CanApplyCloudChanges)); }
    }
    public bool CanWriteToRoot
    {
        get => _canWriteToRoot;
        private set { if (SetProperty(ref _canWriteToRoot, value)) OnPropertyChanged(nameof(CanApplyCloudChanges)); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRunOperations));
                OnPropertyChanged(nameof(CanApplyCloudChanges));
                OnPropertyChanged(nameof(ProgressOpacity));
                if (value)
                {
                    IsProgressIndeterminate = true;
                    ProgressPercent = 0;
                }
                RefreshItemCommands();
            }
        }
    }
    public bool CanRunOperations => !IsBusy;
    public bool CanApplyCloudChanges => !IsBusy && IsRootReachable && CanWriteToRoot;
    public double ProgressOpacity => IsBusy ? 1 : 0;
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    public string BusyMessage { get => _busyMessage; private set => SetProperty(ref _busyMessage, value); }
    public string LastRefreshText { get => _lastRefreshText; private set => SetProperty(ref _lastRefreshText, value); }
    public bool IsAlertOpen { get => _isAlertOpen; set => SetProperty(ref _isAlertOpen, value); }
    public string AlertTitle { get => _alertTitle; private set => SetProperty(ref _alertTitle, value); }
    public string AlertMessage { get => _alertMessage; private set => SetProperty(ref _alertMessage, value); }
    public InfoBarSeverity AlertSeverity { get => _alertSeverity; private set => SetProperty(ref _alertSeverity, value); }
    public bool IncludeDownloads { get => _includeDownloads; private set => SetProperty(ref _includeDownloads, value); }
    public string SelectedTheme { get => _selectedTheme; private set => SetProperty(ref _selectedTheme, value); }
    public string CustomLocalPath { get => _customLocalPath; set => SetProperty(ref _customLocalPath, value); }
    public string CustomTargetName { get => _customTargetName; set => SetProperty(ref _customTargetName, value); }
    public string NewCustomPattern { get => _newCustomPattern; set => SetProperty(ref _newCustomPattern, value); }
    public bool HasOrphanedFolders => OrphanedFolders.Count > 0;
    public Visibility NoOrphansVisibility => _hasScannedOrphans && !HasOrphanedFolders ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OrphanScanPromptVisibility => _hasScannedOrphans ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoCustomPatternsVisibility => CustomPatterns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public int ManagedFoldersCount { get => _managedFoldersCount; private set => SetProperty(ref _managedFoldersCount, value); }
    public int CustomLinksCount { get => _customLinksCount; private set => SetProperty(ref _customLinksCount, value); }
    public Visibility NoLinksVisibility => CustomLinksCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoJournalsVisibility => Journals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public int ActiveFiltersCount { get => _activeFiltersCount; private set => SetProperty(ref _activeFiltersCount, value); }

    public bool FilterGit
    {
        get => _filterGit;
        set { if (SetProperty(ref _filterGit, value) && !_applyingSnapshot) _filtersDirty = true; }
    }
    public bool FilterMountainDuck
    {
        get => _filterMountainDuck;
        set { if (SetProperty(ref _filterMountainDuck, value) && !_applyingSnapshot) _filtersDirty = true; }
    }
    public bool FilterCyberduck
    {
        get => _filterCyberduck;
        set { if (SetProperty(ref _filterCyberduck, value) && !_applyingSnapshot) _filtersDirty = true; }
    }
    public bool FilterRclone
    {
        get => _filterRclone;
        set { if (SetProperty(ref _filterRclone, value) && !_applyingSnapshot) _filtersDirty = true; }
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _operationGate.WaitAsync(0)) return;
        try
        {
            IsBusy = true;
            BusyMessage = "Checking folders and storage…";
            await LoadSnapshotAsync(updateEditableSettings: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ShowAlert("Refresh failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "Ready";
            _operationGate.Release();
        }
    }

    public async Task RefreshSilentlyAsync()
    {
        if (_disposed || !await _operationGate.WaitAsync(0)) return;
        try
        {
            await LoadSnapshotAsync(updateEditableSettings: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ShowAlert("Connection check failed", ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ConfigureRootAsync()
    {
        var path = RootPathInput?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            ShowAlert("Choose a cloud root", "Select a mounted folder or enter its local path first.", InfoBarSeverity.Warning);
            return;
        }
        await ExecuteOperationAsync("Validating cloud storage…", () => _facade.ConfigureRootAsync(path, _lifetime.Token));
    }

    private async Task LinkFolderAsync()
    {
        var path = CustomLocalPath?.Trim() ?? string.Empty;
        var name = CustomTargetName?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            ShowAlert("Choose a local folder", "Select the project folder you want to link.", InfoBarSeverity.Warning);
            return;
        }
        if (name.Length == 0)
        {
            name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        }
        var result = await ExecuteOperationAsync("Linking project folder…", () => _facade.LinkFolderAsync(path, name, _lifetime.Token));
        if (result?.Success == true)
        {
            CustomLocalPath = string.Empty;
            CustomTargetName = string.Empty;
        }
    }

    private async Task SaveFiltersAsync()
    {
        IReadOnlyList<string> customPatterns;
        try
        {
            customPatterns = DeveloperFilterService.ValidateCustomPatterns(CustomPatterns.Select(x => x.Pattern));
        }
        catch (ArgumentException ex)
        {
            ShowAlert("Check custom patterns", ex.Message, InfoBarSeverity.Warning);
            return;
        }
        var settings = new FilterSettings
        {
            Git = FilterGit,
            MountainDuck = FilterMountainDuck,
            Cyberduck = FilterCyberduck,
            Rclone = FilterRclone,
            EnabledNames = FilterPresets.Where(x => x.IsEnabled).Select(x => x.Name).ToList(),
            CustomPatterns = customPatterns.ToList()
        };
        var result = await ExecuteOperationAsync("Writing exclusion rules…", () => _facade.SaveFilterSettingsAsync(settings, _lifetime.Token));
        if (result?.Success == true)
        {
            _filtersDirty = false;
            await RefreshSilentlyAsync();
        }
    }

    private void AddCustomPattern()
    {
        var pattern = NewCustomPattern.Trim();
        if (pattern.Length == 0) return;
        if (CustomPatterns.Any(item => string.Equals(item.Pattern, pattern, StringComparison.Ordinal)))
        {
            ShowAlert("Pattern already added", "Each custom pattern only needs to appear once.", InfoBarSeverity.Informational);
            return;
        }
        try
        {
            _ = DeveloperFilterService.ValidateCustomPatterns(CustomPatterns.Select(x => x.Pattern).Append(pattern));
        }
        catch (ArgumentException ex)
        {
            ShowAlert("Check custom pattern", ex.Message, InfoBarSeverity.Warning);
            return;
        }
        var item = new CustomPatternItemViewModel(pattern, RemoveCustomPattern);
        item.PropertyChanged += CustomPattern_PropertyChanged;
        CustomPatterns.Add(item);
        NewCustomPattern = string.Empty;
        _filtersDirty = true;
        OnPropertyChanged(nameof(NoCustomPatternsVisibility));
    }

    private void RemoveCustomPattern(CustomPatternItemViewModel item)
    {
        item.PropertyChanged -= CustomPattern_PropertyChanged;
        CustomPatterns.Remove(item);
        _filtersDirty = true;
        OnPropertyChanged(nameof(NoCustomPatternsVisibility));
    }

    private void CustomPattern_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applyingSnapshot && e.PropertyName == nameof(CustomPatternItemViewModel.Pattern)) _filtersDirty = true;
    }

    public Task RedirectFolderAsync(KnownFolderItemViewModel folder) =>
        ExecuteOperationAsync($"Redirecting {folder.DisplayName}…", () => _facade.RedirectKnownFolderAsync(folder.Id, _lifetime.Token));

    public Task RestoreFolderAsync(KnownFolderItemViewModel folder) =>
        ExecuteOperationAsync($"Restoring {folder.DisplayName}…", () => _facade.RestoreKnownFolderAsync(folder.Id, _lifetime.Token));

    public Task RecoverUnmanagedFolderAsync(KnownFolderItemViewModel folder) =>
        ExecuteOperationAsync($"Recovering {folder.DisplayName}…", () => _facade.RestoreUnmanagedKnownFolderAsync(folder.Id, _lifetime.Token));

    public Task UnlinkFolderAsync(CustomLinkItemViewModel link) =>
        ExecuteOperationAsync("Removing the link safely…", () => _facade.UnlinkFolderAsync(link.Id, _lifetime.Token));

    public Task RollbackAsync(JournalItemViewModel journal) =>
        ExecuteOperationAsync("Rolling back the operation…", () => _facade.RollbackAsync(journal.Id, _lifetime.Token));

    public Task<FolderRecoveryPreview> PreviewUnmanagedRestoreAsync(KnownFolderItemViewModel folder) =>
        _facade.PreviewUnmanagedRestoreAsync(folder.Id, _lifetime.Token);

    public Task<FolderRecoveryPreview> PreviewOrphanRecoveryAsync(OrphanedFolderItemViewModel orphan, string destinationChoice) =>
        _facade.PreviewOrphanRecoveryAsync(orphan.Id, destinationChoice, _lifetime.Token);

    public Task RecoverOrphanAsync(OrphanedFolderItemViewModel orphan, string destinationChoice) =>
        ExecuteOperationAsync($"Recovering {orphan.Kind} files…", () =>
            _facade.RecoverOrphanAsync(orphan.Id, destinationChoice, _lifetime.Token));

    public async Task RefreshOrphansAsync()
    {
        if (_disposed || !await _operationGate.WaitAsync(0)) return;
        try
        {
            IsBusy = true;
            BusyMessage = "Checking for orphaned OneDrive folders…";
            await LoadOrphanedFoldersAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ShowAlert("OneDrive folder scan failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "Ready";
            _operationGate.Release();
        }
    }

    private async Task LoadOrphanedFoldersAsync()
    {
        var orphaned = await _facade.GetOrphanedOneDriveFoldersAsync(_lifetime.Token);
        _hasScannedOrphans = true;
        OrphanedFolders.Clear();
        foreach (var orphan in orphaned) OrphanedFolders.Add(new OrphanedFolderItemViewModel(this, orphan));
        OnPropertyChanged(nameof(HasOrphanedFolders));
        OnPropertyChanged(nameof(NoOrphansVisibility));
        OnPropertyChanged(nameof(OrphanScanPromptVisibility));
    }

    public async Task SetIncludeDownloadsAsync(bool include)
    {
        if (include == IncludeDownloads) return;
        await ExecuteOperationAsync("Updating Downloads preference…", () => _facade.SetIncludeDownloadsAsync(include, _lifetime.Token));
    }

    public async Task SetThemeAsync(string theme)
    {
        if (theme == SelectedTheme) return;
        if (theme is not ("System" or "Light" or "Dark")) return;
        await ExecuteOperationAsync("Saving appearance…", () => _facade.SetThemeAsync(theme, _lifetime.Token));
    }

    private async Task<OperationResult?> ExecuteOperationAsync(string progressMessage, Func<Task<OperationResult>> action)
    {
        if (_disposed) return null;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        try
        {
            IsBusy = true;
            BusyMessage = progressMessage;
            IsAlertOpen = false;
            var result = await action();
            ShowAlert(result.Success ? "Operation complete" : "Action needs attention", result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            try
            {
                await LoadSnapshotAsync(updateEditableSettings: true);
            }
            catch (Exception refreshError) when (refreshError is not OperationCanceledException)
            {
                ShowAlert(result.Success ? "Operation completed; refresh failed" : "Action needs attention",
                    $"{result.Message} Status refresh failed: {refreshError.Message}",
                    result.Success ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
            return result;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ShowAlert("Operation failed", ex.Message, InfoBarSeverity.Error);
            try { await LoadSnapshotAsync(updateEditableSettings: true); } catch { /* Preserve original error. */ }
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "Ready";
            _operationGate.Release();
        }
    }

    private async Task LoadSnapshotAsync(bool updateEditableSettings)
    {
        var snapshot = await _facade.GetSnapshotAsync(_lifetime.Token);
        var root = snapshot.Root;
        if (RootPathInput == _lastRootPath || string.IsNullOrWhiteSpace(RootPathInput)) RootPathInput = root.Path;
        _lastRootPath = root.Path;
        RootPath = root.IsConfigured ? root.Path : "No cloud root selected";
        RootHealth = root.Health;
        RootMessage = root.Message;
        IsRootConfigured = root.IsConfigured;
        IsRootReachable = root.IsReachable;
        CanWriteToRoot = root.CanWrite;
        FileSystem = string.IsNullOrWhiteSpace(root.FileSystem) ? "Unknown" : root.FileSystem;
        if (root.TotalBytes is > 0 && root.AvailableBytes is >= 0)
        {
            var used = Math.Clamp(root.TotalBytes.Value - root.AvailableBytes.Value, 0, root.TotalBytes.Value);
            StorageUsedPercent = used * 100d / root.TotalBytes.Value;
            StorageSummary = $"{FormatBytes(root.AvailableBytes.Value)} free of {FormatBytes(root.TotalBytes.Value)}";
        }
        else
        {
            StorageUsedPercent = 0;
            StorageSummary = root.IsConfigured ? "Capacity is unavailable for this mount." : "Storage information will appear after connecting.";
        }

        IncludeDownloads = snapshot.IncludeDownloads;
        SelectedTheme = snapshot.Theme;

        KnownFolders.Clear();
        foreach (var folder in snapshot.KnownFolders)
        {
            KnownFolders.Add(new KnownFolderItemViewModel(this, folder));
        }
        ManagedFoldersCount = snapshot.KnownFolders.Count(x => x.IsCloudManaged);

        CustomLinks.Clear();
        foreach (var link in snapshot.Links)
        {
            CustomLinks.Add(new CustomLinkItemViewModel(this, link));
        }
        CustomLinksCount = snapshot.Links.Count;
        OnPropertyChanged(nameof(NoLinksVisibility));

        Journals.Clear();
        RecentJournals.Clear();
        foreach (var entry in snapshot.Journals
                     .Where(x => x.Operation != "settings.theme")
                     .OrderByDescending(x => x.Timestamp)
                     .Take(25))
        {
            var item = new JournalItemViewModel(this, entry);
            Journals.Add(item);
            if (RecentJournals.Count < 4) RecentJournals.Add(item);
        }
        OnPropertyChanged(nameof(NoJournalsVisibility));

        if (updateEditableSettings && !_filtersDirty)
        {
            _applyingSnapshot = true;
            try
            {
                FilterGit = snapshot.Filters.Git;
                FilterMountainDuck = snapshot.Filters.MountainDuck;
                FilterCyberduck = snapshot.Filters.Cyberduck;
                FilterRclone = snapshot.Filters.Rclone;
                var enabled = new HashSet<string>(snapshot.Filters.EnabledNames, StringComparer.OrdinalIgnoreCase);
                foreach (var preset in FilterPresets) preset.IsEnabled = enabled.Contains(preset.Name);
                foreach (var item in CustomPatterns) item.PropertyChanged -= CustomPattern_PropertyChanged;
                CustomPatterns.Clear();
                foreach (var pattern in snapshot.Filters.CustomPatterns)
                {
                    var item = new CustomPatternItemViewModel(pattern, RemoveCustomPattern);
                    item.PropertyChanged += CustomPattern_PropertyChanged;
                    CustomPatterns.Add(item);
                }
                OnPropertyChanged(nameof(NoCustomPatternsVisibility));
            }
            finally
            {
                _applyingSnapshot = false;
            }
        }
        ActiveFiltersCount = snapshot.Filters.EnabledNames.Count;
        LastRefreshText = $"Updated {DateTime.Now:t}";
    }

    private void RefreshItemCommands()
    {
        foreach (var item in KnownFolders) item.UpdateAvailability();
        foreach (var item in CustomLinks) item.UpdateAvailability();
        foreach (var item in Journals) item.UpdateAvailability();
        foreach (var item in OrphanedFolders) item.UpdateAvailability();
    }

    private void Preset_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applyingSnapshot && e.PropertyName == nameof(FilterPresetViewModel.IsEnabled)) _filtersDirty = true;
    }

    private void ShowAlert(string title, string message, InfoBarSeverity severity)
    {
        AlertTitle = title;
        AlertMessage = message;
        AlertSeverity = severity;
        IsAlertOpen = true;
    }

    public void ReportUiError(string title, Exception exception) =>
        ShowAlert(title, exception.Message, InfoBarSeverity.Error);

    private void Facade_ProgressChanged(object? sender, CloudBayProgress progress)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) UpdateProgress(progress);
        else _dispatcher.TryEnqueue(() => UpdateProgress(progress));
    }

    private void UpdateProgress(CloudBayProgress progress)
    {
        if (_disposed || !IsBusy) return;
        BusyMessage = progress.FilesProcessed > 0
            ? $"{progress.Message} · {progress.FilesProcessed} files"
            : progress.Message;
        IsProgressIndeterminate = progress.TotalBytes is not > 0;
        ProgressPercent = progress.TotalBytes is > 0
            ? Math.Clamp(progress.BytesProcessed * 100d / progress.TotalBytes.Value, 0, 100)
            : 0;
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _facade.ProgressChanged -= Facade_ProgressChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed class KnownFolderItemViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly KnownFolderStatus _status;

    public KnownFolderItemViewModel(ShellViewModel shell, KnownFolderStatus status)
    {
        _shell = shell;
        _status = status;
        RedirectCommand = new AsyncRelayCommand(() => _shell.RedirectFolderAsync(this));
        RestoreCommand = new AsyncRelayCommand(() => _shell.RestoreFolderAsync(this));
    }

    public string Id => _status.Id;
    public string IconGlyph => _status.Id switch
    {
        "Desktop" => "\uE80F",
        "Documents" => "\uE8A5",
        "Pictures" => "\uE91B",
        "Videos" => "\uE714",
        "Music" => "\uE8D6",
        "Downloads" => "\uE896",
        _ => "\uE8B7"
    };
    public string DisplayName => _status.DisplayName;
    public string CurrentPath => _status.CurrentPath;
    public string DefaultPath => _status.DefaultPath;
    public string State => _status.State switch
    {
        "LocalDefault" => "Local default",
        "CloudManaged" => "Managed by CloudBay",
        "RedirectedElsewhere" => "Redirected elsewhere",
        "LegacyOneDrive" => "Legacy OneDrive redirect",
        "BrokenRedirect" => "Broken redirect",
        _ => _status.State
    };
    public string Message => _status.Message;
    public bool IsCloudManaged => _status.IsCloudManaged;
    public bool IsOptional => _status.IsOptional;
    public bool CanRedirect => !_shell.IsBusy && !_status.IsCloudManaged && _shell.IsRootReachable && _shell.CanWriteToRoot && (!_status.IsOptional || _shell.IncludeDownloads);
    public bool CanRestore => !_shell.IsBusy && _status.IsCloudManaged;
    public bool CanRecoverLegacy => !_shell.IsBusy && IsRecoverableLegacy;
    public Visibility RecoveryVisibility => IsRecoverableLegacy ? Visibility.Visible : Visibility.Collapsed;
    private bool IsRecoverableLegacy => _status.State is "LegacyOneDrive" or "RedirectedElsewhere";
    public IAsyncRelayCommand RedirectCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }

    public void UpdateAvailability()
    {
        OnPropertyChanged(nameof(CanRedirect));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanRecoverLegacy));
    }
}

public sealed class CustomLinkItemViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly CustomLinkStatus _status;

    public CustomLinkItemViewModel(ShellViewModel shell, CustomLinkStatus status)
    {
        _shell = shell;
        _status = status;
        UnlinkCommand = new AsyncRelayCommand(() => _shell.UnlinkFolderAsync(this));
    }

    public string Id => _status.Id;
    public string LocalPath => _status.LocalPath;
    public string CloudPath => _status.CloudPath;
    public string Kind => _status.Kind;
    public string State => _status.State;
    public bool CanUnlink => !_shell.IsBusy;
    public IAsyncRelayCommand UnlinkCommand { get; }
    public void UpdateAvailability() => OnPropertyChanged(nameof(CanUnlink));
}

public sealed class JournalItemViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly JournalEntrySummary _entry;

    public JournalItemViewModel(ShellViewModel shell, JournalEntrySummary entry)
    {
        _shell = shell;
        _entry = entry;
        RollbackCommand = new AsyncRelayCommand(() => _shell.RollbackAsync(this));
    }

    public string Id => _entry.Id;
    public string Operation => _entry.Operation switch
    {
        "settings.theme" => "Appearance changed",
        "settings.root" => "Cloud root changed",
        "settings.downloads" => "Downloads preference changed",
        "settings.filters" => "Developer filters updated",
        "KnownFolder.Redirect" => "System folder redirected",
        "KnownFolder.Restore" => "System folder restored",
        "KnownFolder.Rollback" => "Folder change rolled back",
        "OneDrive.Recover" => "OneDrive files recovered",
        "link.create" => "Project link created",
        "link.unlink" => "Project link removed",
        "link.rollback" => "Project link rolled back",
        "filter.git" => "Git exclusions updated",
        "filter.rclone" => "Rclone exclusions updated",
        "filter.duck" => "Mountain Duck exclusions updated",
        "filter.cyberduckexport" => "Cyberduck exclusions exported",
        "filter.rollback" => "Exclusion rules rolled back",
        _ => _entry.Operation.Replace('.', ' ')
    };
    public string State => _entry.State;
    public string When => _entry.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Detail => _entry.Operation switch
    {
        "settings.downloads" => "Downloads redirection preference updated",
        "settings.filters" => "Developer exclusion settings and rule files updated",
        "settings.root" when !string.IsNullOrWhiteSpace(_entry.Destination) =>
            $"Storage location: {_entry.Destination}",
        "settings.theme" => "Appearance preference updated",
        _ when !string.IsNullOrWhiteSpace(_entry.Source) &&
               !string.IsNullOrWhiteSpace(_entry.Destination) &&
               !string.Equals(_entry.Source, _entry.Destination, StringComparison.OrdinalIgnoreCase) =>
            $"{_entry.Source}  →  {_entry.Destination}",
        _ when !string.IsNullOrWhiteSpace(_entry.Destination) => _entry.Destination!,
        _ when !string.IsNullOrWhiteSpace(_entry.Source) => _entry.Source,
        _ => "Recorded by CloudBay"
    };
    public string Message => _entry.Message ?? string.Empty;
    public bool CanRollback => !_shell.IsBusy && _entry.CanRollback;
    public IAsyncRelayCommand RollbackCommand { get; }
    public void UpdateAvailability() => OnPropertyChanged(nameof(CanRollback));
}

public sealed class FilterPresetViewModel : ObservableObject
{
    private bool _isEnabled = true;

    public FilterPresetViewModel(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
}

public sealed class CustomPatternItemViewModel : ObservableObject
{
    private string _pattern;

    public CustomPatternItemViewModel(string pattern, Action<CustomPatternItemViewModel> remove)
    {
        _pattern = pattern;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Pattern { get => _pattern; set => SetProperty(ref _pattern, value); }
    public IRelayCommand RemoveCommand { get; }
}

public sealed class OrphanedFolderItemViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly OrphanedFolderStatus _status;

    public OrphanedFolderItemViewModel(ShellViewModel shell, OrphanedFolderStatus status)
    {
        _shell = shell;
        _status = status;
    }

    public string Id => _status.Id;
    public string Kind => _status.Kind;
    public string Path => _status.Path;
    public string ActivePath => _status.ActivePath;
    public string State => _status.State;
    public string Message => _status.Message;
    public string FileCount => _status.FileCount.ToString("N0", CultureInfo.CurrentCulture);
    public string Size => ShellViewModel.FormatBytes(_status.TotalBytes);
    public bool CanRecoverLocal => !_shell.IsBusy;
    public bool CanRecoverCloud => _shell.CanApplyCloudChanges;
    public void UpdateAvailability()
    {
        OnPropertyChanged(nameof(CanRecoverLocal));
        OnPropertyChanged(nameof(CanRecoverCloud));
    }
}

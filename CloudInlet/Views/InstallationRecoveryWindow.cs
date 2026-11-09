using CloudInlet.ViewModels;
using CloudInlet.Packaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CloudInlet.Views;

/// <summary>Runs outside the installed application. Recovery actions are delegated to an
/// authenticated helper; the UI never chooses installer paths or edits the recovery receipt.</summary>
internal sealed class InstallationRecoveryWindow : Window
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _stage = SourceImportDialog.Heading("Checking installation recovery");
    private readonly TextBlock _title = SourceImportDialog.Heading("Installation migration");
    private readonly TextBlock _detail = SourceImportDialog.Text("Reading the saved migration before taking any action.");
    private readonly TextBlock _locations = SourceImportDialog.Text("");
    private readonly TextBlock _history = SourceImportDialog.Text("");
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
        Message = "Your backup folders, accounts and completed migration items are preserved." };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button _retry = new() { Content = "Retry", IsEnabled = false };
    private readonly Button _revert = new() { Content = "Restore original location", IsEnabled = false };
    private readonly Button _open = new() { Content = "Open CloudInlet", Visibility = Visibility.Collapsed };
    private readonly List<string> _events = [];
    private string? _lastEvent;
    private bool _busy;
    private bool _registrationOnly;
    private bool _requiresRestart;
    private bool _statusUnavailable;
    private string? _phase;
    internal Task Ready => _ready.Task;
    internal Task ClosedTask => _closed.Task;
    internal FrameworkElement CaptureContent { get; }
    internal Func<bool, Task>? Recover { get; set; }
    internal Func<Task>? OpenApplication { get; set; }

    internal InstallationRecoveryWindow(string theme = "System")
    {
        Title = "CloudInlet"; SystemBackdrop = new MicaBackdrop();
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(FluentIcons.Create("apps", 28));
        Grid.SetColumn(_title, 1); heading.Children.Add(_title);
        var body = new StackPanel { Spacing = 16 };
        foreach (var element in new UIElement[] { heading, _notice, _stage, _detail, _progress, _locations }) body.Children.Add(element);
        _detail.IsTextSelectionEnabled = true; _locations.IsTextSelectionEnabled = true; _history.IsTextSelectionEnabled = true;
        body.Children.Add(new Expander { Header = "Details", Content = _history, IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var actions = new ActionWrapPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        _retry.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        _retry.Click += async (_, _) => await RunAsync(false);
        _revert.Click += async (_, _) => await RunAsync(true);
        _open.Click += async (_, _) =>
        {
            if (_busy || _requiresRestart || _statusUnavailable) return;
            _busy = true;
            _retry.IsEnabled = _revert.IsEnabled = _open.IsEnabled = false;
            try { if (OpenApplication is not null) await OpenApplication(); }
            catch (Exception error) { ShowFailure(error.Message); }
            finally { _busy = false; }
        };
        var close = new Button { Content = "Close" }; close.Click += (_, _) => Close();
        foreach (var button in new[] { _retry, _revert, _open, close }) actions.Children.Add(button);
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 20, RequestedTheme = theme switch
        { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default } };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(actions, 1); root.Children.Add(actions); Content = root; CaptureContent = root;
        root.Loaded += (_, _) => _ready.TrySetResult(); Closed += (_, _) => _closed.TrySetResult();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(760, 640));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", CloudInlet.Core.BuildInfo.IconFileName);
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
    }

    internal void Report(string phase, string detail, string source, string destination, bool running, bool canRevert,
        bool requiresRestart = false, string? restartMessage = null)
    {
        _statusUnavailable = false;
        _registrationOnly = string.Equals(InstallationMigrationPolicy.Normalize(source),
            InstallationMigrationPolicy.Normalize(destination), StringComparison.OrdinalIgnoreCase);
        _phase = phase;
        _requiresRestart = requiresRestart;
        _title.Text = _registrationOnly ? "Installer registration" : "Installation migration";
        _stage.Text = _registrationOnly ? phase switch
        {
            "UpdatingOriginal" => "Updating the original installation",
            "Prepared" => "Ready to update installer registration", "UninstallingOriginal" => "Retiring the previous registration",
            "InstallingDestination" => "Installing in the same location", "RestoringOriginal" => "Restoring the application",
            "Completed" => "Installer registration verified", "Reverted" => "Previous registration retained", _ => "Installer registration needs attention"
        } : phase switch
        {
            "UpdatingOriginal" => "Updating the original installation",
            "Prepared" => "Ready to move the installation", "UninstallingOriginal" => "Retiring the previous installation",
            "InstallingDestination" => "Installing at the selected location", "RestoringOriginal" => "Restoring the previous location",
            "Completed" => "Installation migration verified", "Reverted" => "Previous location restored", _ => "Installation needs attention"
        };
        _detail.Text = detail;
        _locations.Text = _registrationOnly ? "Installation location\n" + source
            : "Previous location\n" + source + "\n\nSelected location\n" + destination;
        _retry.Content = phase == "UpdatingOriginal" ? "Retry update" : _registrationOnly ? "Retry registration" : "Retry";
        _revert.Content = _registrationOnly ? phase is "UpdatingOriginal" or "Prepared" ? "Keep current registration" : "Restore application"
            : phase == "UpdatingOriginal" ? "Keep original location" : "Restore original location";
        var terminal = phase is "Completed" or "Reverted";
        _progress.Visibility = running && !requiresRestart ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsEnabled = !running && !_busy && !terminal && !requiresRestart;
        _revert.IsEnabled = !running && !_busy && !terminal && canRevert && !requiresRestart;
        _retry.Visibility = _revert.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        _open.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        _open.IsEnabled = terminal && !running && !_busy && !requiresRestart;
        _notice.Severity = terminal ? InfoBarSeverity.Success : running ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        _notice.Message = (phase, _registrationOnly) switch
        {
            ("Completed", true) => "CloudInlet is verified in the same folder with its updated installer registration. Your backup folders and accounts were retained.",
            ("Reverted", true) => "The updated application and its previous registration were kept in the same folder. Other completed migrations were retained.",
            ("Completed", false) => "The selected installation is verified. Your backup folders and accounts were retained.",
            ("Reverted", false) => "The completed application update was kept at its previous location. Other completed migrations were retained.",
            _ when running => "You can close this window. The installer will continue; reopen recovery to see its result.",
            ("UpdatingOriginal", _) => "The application update is paused in its original folder. Retry continues the update. Keeping the current installation first finishes and verifies this update; it does not move the installation or change its registration.",
            ("Prepared", true) => "Migration is paused. Retry updates the installer registration in this folder. Keep current registration stops only this incomplete change.",
            (_, true) => "Migration is paused. Retry continues from the saved step. Restoring the application may finish its registration under CloudInlet; completed migration items stay in place.",
            _ => "Migration is paused. Retry continues from the saved step. Restore original location applies only to this incomplete installation change."
        };
        if (requiresRestart)
        {
            _stage.Text = "Restart Windows";
            _detail.Text = restartMessage ?? "Save your work and restart Windows, then reopen installation recovery to continue. Migration has not been marked complete.";
            _notice.Severity = InfoBarSeverity.Warning;
            _notice.Message = "Recovery is waiting for a Windows restart. CloudInlet will not restart your PC automatically. Retry, restore and opening the application stay unavailable until after the restart.";
        }
        AddEvent(_stage.Text, _detail.Text);
    }
    internal void ShowFailure(string message)
    {
        if (_requiresRestart) { AddEvent("Recovery is waiting for a Windows restart", message); return; }
        _detail.Text = message; _notice.Severity = InfoBarSeverity.Error;
        _notice.Message = "Recovery could not finish. Its saved records and original data were retained.";
        _progress.Visibility = Visibility.Collapsed; AddEvent("Recovery needs attention", message);
    }
    internal void ShowStatusUnavailable()
    {
        DisableRecoveryActions();
        _stage.Text = "Waiting for recovery status";
        _detail.Text = "The saved recovery status is temporarily in use. This window will check again automatically.";
        _notice.Severity = InfoBarSeverity.Warning;
        _notice.Message = "Recovery actions are unavailable until the current status can be verified. No installer action will be repeated automatically.";
        _progress.Visibility = Visibility.Visible;
        AddEvent(_stage.Text, _detail.Text);
    }
    internal void ShowObservationFailure(string message)
    {
        DisableRecoveryActions();
        _requiresRestart = false;
        ShowFailure(message);
    }
    private void DisableRecoveryActions()
    {
        _statusUnavailable = true;
        _retry.IsEnabled = _revert.IsEnabled = _open.IsEnabled = false;
    }
    private async Task RunAsync(bool revert)
    {
        if (_busy || _requiresRestart || _statusUnavailable || Recover is null) return;
        if (revert)
        {
            var confirmation = new ContentDialog { Title = _phase == "UpdatingOriginal" ? "Keep the original installation?" : _registrationOnly
                    ? _phase == "Prepared" ? "Keep the current registration?" : "Restore this application?"
                    : "Restore this installation location?",
                Content = _phase == "UpdatingOriginal"
                    ? "CloudInlet will first finish and verify the application update in its original folder, then keep its existing location and registration. Other completed migration items stay in place."
                    : _registrationOnly
                    ? "If the previous installation is still intact, CloudInlet will keep its current registration. If it has been removed, CloudInlet will restore the verified updated application in the same folder and finish its registration under CloudInlet. Completed migration items stay in place."
                    : "CloudInlet will try to restore the updated application at its previous location. This affects only the incomplete installation change. Completed migration items stay in place.",
                PrimaryButtonText = _phase == "UpdatingOriginal" ? "Finish update and keep" : _registrationOnly ? _phase == "Prepared" ? "Keep registration" : "Restore application" : "Restore location",
                CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                XamlRoot = CaptureContent.XamlRoot, RequestedTheme = CaptureContent.RequestedTheme };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            if (_requiresRestart || _statusUnavailable) return;
        }
        _busy = true; _retry.IsEnabled = _revert.IsEnabled = false; _progress.Visibility = Visibility.Visible;
        try { await Recover(revert); }
        catch (Exception error) { ShowFailure(error.Message); }
        finally { _busy = false; }
    }
    private void AddEvent(string stage, string detail)
    {
        if (detail.Length > 2048) detail = detail[..2048] + "…";
        var value = stage + "\n" + detail; if (value == _lastEvent) return; _lastEvent = value;
        _events.Add(DateTimeOffset.Now.ToString("t") + " · " + value);
        if (_events.Count > 80) _events.RemoveAt(0);
        _history.Text = string.Join("\n\n", _events.AsEnumerable().Reverse());
    }
}

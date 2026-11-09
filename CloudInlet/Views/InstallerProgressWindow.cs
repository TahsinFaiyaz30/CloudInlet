using CloudInlet.Packaging;
using CloudInlet.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CloudInlet.Views;

/// <summary>Read-only progress view; closing this window never cancels a Windows installer.</summary>
internal sealed class InstallerProgressWindow : Window
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _stage = SourceImportDialog.Heading("Preparing installation");
    private readonly TextBlock _detail = SourceImportDialog.Text("Waiting for the installer to report its first verified step.");
    private readonly ProgressBar _progress = new() { IsIndeterminate = true };
    private readonly InfoBar _notice = new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
        Message = "You can close this window. The installer will continue, and CloudInlet will show any failure when it reopens." };
    private readonly TextBlock _history = SourceImportDialog.Text("");
    private readonly List<string> _events = [];
    private long _sequence;
    internal FrameworkElement CaptureContent { get; }
    internal Task ClosedTask => _closed.Task;
    internal Task Ready => _ready.Task;

    internal InstallerProgressWindow(string theme = "System")
    {
        Title = "CloudInlet";
        SystemBackdrop = new MicaBackdrop();
        var body = new StackPanel { Spacing = 16 };
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(FluentIcons.Create("apps", 28));
        var title = SourceImportDialog.Heading("Updating CloudInlet"); Grid.SetColumn(title, 1); heading.Children.Add(title);
        body.Children.Add(heading); body.Children.Add(_notice); body.Children.Add(_stage);
        body.Children.Add(_detail); body.Children.Add(_progress);
        _detail.IsTextSelectionEnabled = true;
        _history.IsTextSelectionEnabled = true;
        body.Children.Add(new Expander { Header = "Details", Content = _history, IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 20, RequestedTheme = theme switch
        { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default } };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(close, 1); root.Children.Add(close);
        Content = root; CaptureContent = root;
        root.Loaded += (_, _) => _ready.TrySetResult();
        Closed += (_, _) => _closed.TrySetResult();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(760, 640));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", CloudInlet.Core.BuildInfo.IconFileName);
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
    }

    internal void Report(InstallerProgressRecord value)
    {
        if (value.Sequence <= _sequence) return;
        _sequence = value.Sequence;
        _stage.Text = value.Stage switch
        {
            "Validating" => "Checking this update", "WaitingForAppExit" => "Waiting for CloudInlet to close",
            "VerifyingPackage" => "Verifying the installer", "PackageVerified" => "Installer verified",
            "InstallerRunning" => "Installing the update", "InstallerBusy" => "Waiting for Windows Installer",
            "VerifyingInstallation" => "Verifying the installed app", "Complete" => "Update verified",
            "RestartRequired" => "Restart Windows to finish", "Failed" => "Update needs attention", _ => "Updating CloudInlet"
        };
        _detail.Text = value.Detail;
        _progress.Visibility = value.Status == "Active" ? Visibility.Visible : Visibility.Collapsed;
        _events.Add(DateTimeOffset.Parse(value.UpdatedUtc).ToLocalTime().ToString("t") + " · " + _stage.Text + "\n" + value.Detail);
        if (_events.Count > 80) _events.RemoveAt(0);
        _history.Text = string.Join("\n\n", _events.AsEnumerable().Reverse());
        if (value.Status == "Active") return;
        _notice.Severity = value.Status == "Succeeded" ? InfoBarSeverity.Success :
            value.Status == "Failed" ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        _notice.Message = value.Status switch
        {
            "Succeeded" => value.MigrationCompleted ? "The selected installation migration and update passed verification." : "The update passed verification.",
            "RestartRequired" => value.MigrationRequested
                ? "Save your work and restart Windows, then reopen CloudInlet to continue recovery. Migration is not marked complete until its remaining work is verified."
                : "Save your work and restart Windows before reopening CloudInlet. Windows still needs to finish applying the update.",
            _ => value.MigrationCompleted ? "The completed migration remains in place. Review the remaining installation problem below." :
                "The update did not finish. Reopen CloudInlet to review the failure before retrying. Existing recovery records were retained."
        };
    }

    internal void FailObservation(string message)
    {
        _stage.Text = "Progress needs attention";
        _detail.Text = message;
        _progress.Visibility = Visibility.Collapsed;
        _notice.Severity = InfoBarSeverity.Warning;
        _notice.Message = "This window cannot confirm completion. It has not stopped or retried the installer.";
    }
}

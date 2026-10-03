using CloudBay.Application;
using Microsoft.UI.Xaml;
using CloudBay.Views;
using CloudBay.Windows;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudBay;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static MainWindow? MainWindow { get; private set; }
    private ClientController? _controller;
    private TrayWindow? _trayWindow;
    private TrayIcon? _trayIcon;
    private Mutex? _singleInstance;
    private CancellationTokenSource _lifetime = new();
    private string _pipeName = "";
    private bool _exiting;
    private int _trayUpdatePending;
    private int _isolatedFailurePending;
    private bool _isUiSmoke;
    private bool _isUiLive;
    private bool _startupCompleted;
    private string? _smokeTheme;
    private string _startupStage = "Initialize application";
    private readonly DateTimeOffset _startupUtc = DateTimeOffset.UtcNow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (_isUiSmoke || _isUiLive)
            {
                _ = FailIsolatedStartupAsync(args.Exception, _startupCompleted ? "Unhandled UI exception" : _startupStage);
                return;
            }
            _controller?.Pause();
            MainWindow?.ShowWindow();
            if (MainWindow?.Content is FrameworkElement content)
                content.DispatcherQueue.TryEnqueue(async () =>
                {
                    var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog { Title = "CloudBay needs attention",
                        Content = args.Message, CloseButtonText = "Close", XamlRoot = content.XamlRoot };
                    try { await dialog.ShowAsync(); } catch { }
                });
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var isSmoke = commandLine.Contains("--ui-smoke");
        var isLive = commandLine.Contains("--ui-live");
        var smokeTheme = commandLine.FirstOrDefault(arg => arg.StartsWith("--ui-smoke-theme=", StringComparison.Ordinal))?[17..];
        _isUiSmoke = isSmoke;
        _isUiLive = isLive;
        _smokeTheme = smokeTheme;
        if (isSmoke && smokeTheme is not (null or "Dark" or "Light"))
        {
            Environment.ExitCode = 64;
            Exit();
            return;
        }
        if (isSmoke && smokeTheme is null)
        {
            await RunUiSmokeSuiteAsync(commandLine.Contains("--ui-smoke-tray"));
            Exit();
            return;
        }
        var isolationSuffix = isSmoke ? ".UiSmoke." + smokeTheme : isLive ? ".UiLive" : "";
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "CloudBay.Client." + sid + isolationSuffix;
        _singleInstance = new Mutex(true, @"Local\CloudBay.Client." + sid + isolationSuffix, out var first);
        if (!first)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                await client.ConnectAsync(3000);
                var activation = commandLine.Contains("--shutdown") ? "quit"
                    : isLive && commandLine.Contains("--show-tray") ? "tray" : "show";
                await client.WriteAsync(Encoding.UTF8.GetBytes(activation));
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            Exit(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--shutdown")) { _singleInstance.Dispose(); Exit(); return; }
        try
        {
            _startupStage = "Initialize client state";
            var isolatedStorage = isSmoke || isLive ? new ClientStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudBay", isSmoke ? "UiValidation" : "UiLive", isSmoke ? Guid.NewGuid().ToString("N") : "Client")) : null;
            _controller = new ClientController(isolatedStorage, manageStartup: !(isSmoke || isLive));
            _startupStage = "Create main window";
            MainWindow = new MainWindow(_controller);
            _startupStage = "Create tray window";
            _trayWindow = new TrayWindow(_controller, () => { MainWindow.ShowSettings(); MainWindow.ShowWindow(); }, () => _ = QuitAsync());
            _startupStage = "Register notification icon";
            _trayIcon = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow),
                Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico"), () => _trayWindow.ShowAtTray(), MainWindow.ShowWindow);
            _controller.Changed += Controller_Changed;
            _ = ListenForActivationAsync();
            if (!commandLine.Contains("--background") || !_controller.Settings.IsConfigured) MainWindow.ShowWindow();
            _startupStage = "Start client controller";
            var clientStartup = _controller.StartAsync();
            if (isLive)
            {
                if (MainWindow.AppWindow.IsVisible)
                {
                    _startupStage = "Restore main window navigation";
                    await MainWindow.InitialNavigationReady.WaitAsync(TimeSpan.FromSeconds(30));
                }
                _startupStage = "Publish live preview readiness";
                await WriteLiveReadinessAsync();
            }
            _startupStage = "Start client controller";
            await clientStartup;
            _startupCompleted = true;
        }
        catch (Exception error) when (isSmoke || isLive)
        {
            await FailIsolatedStartupAsync(error, _startupStage);
            return;
        }
        if (commandLine.Contains("--ui-smoke"))
        {
            var output = UiSmokeOutput;
            Directory.CreateDirectory(output);
            // These markers belong only to this isolated capture run. Retain
            // previous images for comparison, but never reuse their success.
            var completionPath = Path.Combine(output, $"complete-{smokeTheme}.txt");
            var failurePath = Path.Combine(output, $"failure-{smokeTheme}.txt");
            File.Delete(completionPath);
            File.Delete(failurePath);
            try
            {
                if (!commandLine.Contains("--ui-smoke-tray")) await MainWindow.RunUiSmokeAsync(output);
                await _trayWindow.RunUiSmokeAsync(output);
                await File.WriteAllTextAsync(completionPath, $"{smokeTheme} {(commandLine.Contains("--ui-smoke-tray") ? "tray" : "UI")} capture completed");
            }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                await File.WriteAllTextAsync(failurePath, DescribeIsolatedFailure(error, "Capture UI"));
            }
            finally { await QuitAsync(); }
        }
    }

    private static string LiveStateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudBay", "UiLive", "Client");
    private static string UiSmokeOutput => Path.Combine(Environment.CurrentDirectory, "artifacts",
        Environment.GetCommandLineArgs().Contains("--ui-smoke-tray") ? "ui-smoke-tray" : "ui-smoke");

    private async Task WriteLiveReadinessAsync()
    {
        Directory.CreateDirectory(LiveStateDirectory);
        var temporary = Path.Combine(LiveStateDirectory, $"ui-ready-{Environment.ProcessId}.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            startupUtc = _startupUtc,
            trayReady = _trayIcon is not null,
            controllerReady = true,
            navigationReady = MainWindow?.InitialNavigationReady.IsCompletedSuccessfully == true,
            visibleRoute = MainWindow?.CurrentRoute,
            pageTitle = MainWindow?.CurrentPageTitle
        }));
        File.Move(temporary, Path.Combine(LiveStateDirectory, "ui-ready.json"), overwrite: true);
    }

    private async Task FailIsolatedStartupAsync(Exception error, string stage)
    {
        if (Interlocked.Exchange(ref _isolatedFailurePending, 1) != 0) return;
        Environment.ExitCode = 1;
        try
        {
            var diagnostic = DescribeIsolatedFailure(error, stage);
            if (_isUiSmoke)
            {
                var output = UiSmokeOutput;
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, _smokeTheme is null ? "failure.txt" : $"failure-{_smokeTheme}.txt"), diagnostic);
            }
            if (_isUiLive)
            {
                Directory.CreateDirectory(LiveStateDirectory);
                await File.WriteAllTextAsync(Path.Combine(LiveStateDirectory, $"ui-startup-failure-{Environment.ProcessId}.txt"), diagnostic);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { await QuitAsync(); }
        catch { Exit(); }
    }

    private static string DescribeIsolatedFailure(Exception error, string stage)
    {
        // Exception messages and source paths can contain account credentials
        // or personal file names. Keep useful type/HRESULT/stack diagnostics
        // without persisting those values in preview or capture output.
        var diagnostic = new StringBuilder().AppendLine($"Stage: {stage}")
            .AppendLine($"Process: {Environment.ProcessId}")
            .AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            diagnostic.AppendLine($"Exception: {current.GetType().FullName}")
                .AppendLine($"HRESULT: 0x{current.HResult:X8}");
            if (current.StackTrace is { } stack)
                diagnostic.AppendLine(Regex.Replace(stack, @" in [^\r\n]+:line \d+", ""));
        }
        return diagnostic.ToString();
    }

    private static async Task RunUiSmokeSuiteAsync(bool trayOnly)
    {
        var output = UiSmokeOutput;
        Directory.CreateDirectory(output);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var suiteGate = new Semaphore(1, 1, @"Local\CloudBay.UIValidation.Suite." + sid);
        if (!suiteGate.WaitOne(0))
        {
            Environment.ExitCode = 2;
            await File.WriteAllTextAsync(Path.Combine(output, $"failure-concurrent-{Environment.ProcessId}.txt"), "Another isolated UI validation suite is already running.");
            return;
        }
        File.Delete(Path.Combine(output, "complete.txt"));
        File.Delete(Path.Combine(output, "failure.txt"));
        try
        {
            // Fresh native windows/render contexts per theme prevent a prior
            // ContentDialog capture from leaving stale RenderTargetBitmap
            // bounds when the window resizes and its theme changes together.
            foreach (var theme in new[] { "Dark", "Light" })
            {
                File.Delete(Path.Combine(output, $"complete-{theme}.txt"));
                File.Delete(Path.Combine(output, $"failure-{theme}.txt"));
                var launch = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                launch.ArgumentList.Add("--ui-smoke");
                launch.ArgumentList.Add($"--ui-smoke-theme={theme}");
                if (trayOnly) launch.ArgumentList.Add("--ui-smoke-tray");
                var initialPage = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--page=", StringComparison.Ordinal));
                if (initialPage is not null) launch.ArgumentList.Add(initialPage);
                using var process = Process.Start(launch) ?? throw new InvalidOperationException("Could not launch the isolated UI validation process.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try
                    {
                        await using var pipe = new NamedPipeClientStream(".", "CloudBay.Client." + sid + ".UiSmoke." + theme, PipeDirection.Out);
                        await pipe.ConnectAsync(2000);
                        await pipe.WriteAsync(Encoding.UTF8.GetBytes("quit"));
                        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await process.WaitForExitAsync(shutdownTimeout.Token);
                    }
                    catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    throw new TimeoutException($"The isolated {theme} UI capture exceeded three minutes.");
                }
                if (process.ExitCode != 0 || !File.Exists(Path.Combine(output, $"complete-{theme}.txt")))
                    throw new InvalidOperationException($"The fresh {theme} UI capture failed. See failure-{theme}.txt.");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "complete.txt"), $"Fresh Dark and Light {(trayOnly ? "tray" : "UI")} captures completed");
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), DescribeIsolatedFailure(error, "Run isolated theme suite"));
        }
        finally { suiteGate.Release(); }
    }

    private async Task ListenForActivationAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_lifetime.Token);
                var bytes = new byte[16];
                var length = await server.ReadAsync(bytes, _lifetime.Token);
                if (Encoding.UTF8.GetString(bytes, 0, length) == "show")
                    MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow.ShowWindow());
                else if (Encoding.UTF8.GetString(bytes, 0, length) == "quit")
                    MainWindow?.DispatcherQueue.TryEnqueue(() => _ = QuitAsync());
                else if (_isUiLive && Encoding.UTF8.GetString(bytes, 0, length) == "tray")
                    MainWindow?.DispatcherQueue.TryEnqueue(() => _trayWindow?.ShowAtTray(keepOpenForInspection: true));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void Controller_Changed(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _trayUpdatePending, 1) != 0) return;
        var window = MainWindow;
        if (window is null || !window.DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _trayUpdatePending, 0);
            if (!_exiting && _controller is not null) _trayIcon?.Update(_controller.Snapshot.Message);
        })) Interlocked.Exchange(ref _trayUpdatePending, 0);
    }
    private async Task QuitAsync()
    {
        if (_exiting) return; _exiting = true;
        _lifetime.Cancel();
        if (_controller is not null) _controller.Changed -= Controller_Changed;
        _trayIcon?.Dispose();
        if (_controller is not null) await _controller.DisposeAsync();
        _trayWindow?.Close();
        if (MainWindow is not null) { MainWindow.AllowClose = true; MainWindow.Close(); }
        _singleInstance?.Dispose();
        Exit();
    }
}

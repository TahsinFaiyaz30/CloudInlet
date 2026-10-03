using CloudBay.Application;
using Microsoft.UI.Xaml;
using CloudBay.Views;
using CloudBay.Windows;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;

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

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
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
        if (isSmoke && smokeTheme is not (null or "Dark" or "Light"))
        {
            Environment.ExitCode = 64;
            Exit();
            return;
        }
        if (isSmoke && smokeTheme is null)
        {
            await RunUiSmokeSuiteAsync();
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
                await client.WriteAsync(Encoding.UTF8.GetBytes(Environment.GetCommandLineArgs().Contains("--shutdown") ? "quit" : "show"));
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            Exit(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--shutdown")) { _singleInstance.Dispose(); Exit(); return; }
        var isolatedStorage = isSmoke || isLive ? new ClientStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudBay", isSmoke ? "UiValidation" : "UiLive", isSmoke ? Guid.NewGuid().ToString("N") : "Client")) : null;
        _controller = new ClientController(isolatedStorage, manageStartup: !(isSmoke || isLive));
        MainWindow = new MainWindow(_controller);
        _trayWindow = new TrayWindow(_controller, () => { MainWindow.ShowSettings(); MainWindow.ShowWindow(); }, () => _ = QuitAsync());
        _trayIcon = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow),
            Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico"), () => _trayWindow.ShowAtTray(), MainWindow.ShowWindow);
        _controller.Changed += Controller_Changed;
        _ = ListenForActivationAsync();
        if (!Environment.GetCommandLineArgs().Contains("--background") || !_controller.Settings.IsConfigured) MainWindow.ShowWindow();
        await _controller.StartAsync();
        if (commandLine.Contains("--ui-smoke"))
        {
            var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-smoke");
            Directory.CreateDirectory(output);
            // These markers belong only to this isolated capture run. Retain
            // previous images for comparison, but never reuse their success.
            var completionPath = Path.Combine(output, $"complete-{smokeTheme}.txt");
            var failurePath = Path.Combine(output, $"failure-{smokeTheme}.txt");
            File.Delete(completionPath);
            File.Delete(failurePath);
            try
            {
                await MainWindow.RunUiSmokeAsync(output);
                await _trayWindow.RunUiSmokeAsync(output);
                await File.WriteAllTextAsync(completionPath, $"{smokeTheme} UI capture completed");
            }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                await File.WriteAllTextAsync(failurePath, error.ToString());
            }
            finally { await QuitAsync(); }
        }
    }

    private static async Task RunUiSmokeSuiteAsync()
    {
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-smoke");
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
                using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    Arguments = $"--ui-smoke --ui-smoke-theme={theme}",
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }) ?? throw new InvalidOperationException("Could not launch the isolated UI validation process.");
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
            await File.WriteAllTextAsync(Path.Combine(output, "complete.txt"), "Fresh Dark and Light UI captures completed");
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), error.ToString());
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

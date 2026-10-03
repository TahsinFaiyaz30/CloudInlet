using CloudBay.Application;
using Microsoft.UI.Xaml;
using CloudBay.Views;
using CloudBay.Windows;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

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
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "CloudBay.Client." + sid;
        _singleInstance = new Mutex(true, @"Local\CloudBay.Client." + sid, out var first);
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
        var isSmoke = Environment.GetCommandLineArgs().Contains("--ui-smoke");
        _controller = new ClientController(isSmoke ? new ClientStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudBay", "UiValidation", Guid.NewGuid().ToString("N"))) : null);
        MainWindow = new MainWindow(_controller);
        _trayWindow = new TrayWindow(_controller, () => { MainWindow.ShowSettings(); MainWindow.ShowWindow(); }, () => _ = QuitAsync());
        _trayIcon = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow),
            Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico"), () => _trayWindow.ShowAtTray(), MainWindow.ShowWindow);
        _controller.Changed += (_, _) => MainWindow.DispatcherQueue.TryEnqueue(() => _trayIcon?.Update(_controller.Snapshot.Message));
        _ = ListenForActivationAsync();
        if (!Environment.GetCommandLineArgs().Contains("--background") || !_controller.Settings.IsConfigured) MainWindow.ShowWindow();
        await _controller.StartAsync();
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--ui-smoke"))
        {
            var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-smoke");
            await MainWindow.RunUiSmokeAsync(output);
            await _trayWindow.RunUiSmokeAsync(output);
            await File.WriteAllTextAsync(Path.Combine(output, "complete.txt"), "UI capture completed");
            await QuitAsync();
        }
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
    private async Task QuitAsync()
    {
        if (_exiting) return; _exiting = true;
        _lifetime.Cancel();
        _trayIcon?.Dispose();
        if (_controller is not null) await _controller.DisposeAsync();
        _trayWindow?.Close();
        if (MainWindow is not null) { MainWindow.AllowClose = true; MainWindow.Close(); }
        _singleInstance?.Dispose();
        Exit();
    }
}

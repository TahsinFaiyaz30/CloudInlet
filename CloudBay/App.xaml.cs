using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.Updates;
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
    private UpdateCoordinator? _updates;
    private TrayWindow? _trayWindow;
    private TrayContextMenuWindow? _trayContextMenu;
    private TrayIconActions? _trayActions;
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
            // A failed secondary launch has no window or controller to surface an
            // exception. Do not leave a headless shutdown helper running forever.
            if (MainWindow is null) { Environment.ExitCode = 1; Exit(); return; }
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
        if (commandLine.Contains("--unregister-notifications"))
        {
            Environment.ExitCode = await WindowsNotifications.RemoveRegistrationAsync() ? 0 : 1;
            Exit(); return;
        }
        if (commandLine.Contains("--notification-smoke"))
        {
            var output = commandLine.FirstOrDefault(value => value.StartsWith("--validation-output=", StringComparison.Ordinal))?[20..]
                ?? Path.Combine(Environment.CurrentDirectory, "Evidence");
            try { Environment.ExitCode = await NativeNotificationValidation.RunAsync(output) ? 0 : 1; }
            catch (Exception) { Environment.ExitCode = 1; }
            Exit(); return;
        }
        if (commandLine.Contains("--store-runtime-smoke"))
        {
            var directory = commandLine.FirstOrDefault(argument => argument.StartsWith("--validation-output=", StringComparison.Ordinal))?[20..]
                ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "store-runtime-smoke");
            Directory.CreateDirectory(directory);
            try
            {
                if (!UpdateInstallation.IsPackaged || UpdateInstallation.Load().Identity.InstallerKind != UpdateInstallerKind.Store)
                    throw new InvalidOperationException("The Store test must run with Windows package identity.");
                RegisterNotifications();
                if (_notificationRegistrationWarning is not null)
                    throw new InvalidOperationException("The packaged Windows notification registration failed.");
                var package = global::Windows.ApplicationModel.Package.Current;
                var startup = await global::Windows.ApplicationModel.StartupTask.GetAsync("CloudBayStartup");
                var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.InstalledLocation.Path));
                if (!installed.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Windows package location does not match the executing application.");
                await File.WriteAllTextAsync(Path.Combine(directory, "complete.json"), JsonSerializer.Serialize(new
                {
                    version = BuildInfo.Version, packageName = package.Id.Name, packageFamilyName = package.Id.FamilyName,
                    installerKind = "Store", startupTaskId = startup.TaskId, startupState = startup.State.ToString(),
                    notificationRegistration = true,
                    activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind.ToString(),
                    completedUtc = DateTimeOffset.UtcNow
                }));
                Environment.ExitCode = 0;
            }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString());
            }
            if (_notifications is not null) await _notifications.DisposeAsync();
            Exit(); return;
        }
        if (isSmoke && commandLine.Contains("--tray-focus-smoke"))
        {
            try
            {
                Environment.ExitCode = await TrayFocusValidation.RunAsync(Path.Combine(Environment.CurrentDirectory, "artifacts", "tray-focus-smoke")) ? 0 : 1;
            }
            catch (Exception error)
            {
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "tray-focus-smoke");
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), DescribeIsolatedFailure(error, "Validate native tray focus"));
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (isSmoke && smokeTheme is not (null or "Dark" or "Light"))
        {
            Environment.ExitCode = 64;
            Exit();
            return;
        }
        if (isSmoke && commandLine.Contains("--picker-smoke"))
        {
            try
            {
                var passed = await NativePickerValidation.RunAsync(Path.Combine(Environment.CurrentDirectory, "artifacts", "picker-smoke"));
                Environment.ExitCode = passed ? 0 : 1;
            }
            catch (Exception error)
            {
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "picker-smoke");
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), DescribeIsolatedFailure(error, "Validate native chooser"));
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }
        if (isSmoke && smokeTheme is null)
        {
            await RunUiSmokeSuiteAsync(commandLine.Contains("--ui-smoke-tray"));
            Exit();
            return;
        }
        if (!(isSmoke || isLive))
        {
            try
            {
                if (WindowsUpdateInstaller.IsInstallationInProgress(Path.Combine(BuildInfo.DefaultDataDirectory, "Updates")))
                { Exit(); return; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* An unavailable update cache must not prevent normal backup startup. */ }
        }
        var isolationSuffix = isSmoke ? ".UiSmoke." + smokeTheme : isLive ? ".UiLive" : BuildInfo.PipeSuffix;
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "CloudBay.Client." + sid + isolationSuffix;
        if (commandLine.Contains("--shutdown"))
        {
            Environment.ExitCode = ClientActivation.ExitCode(await ClientActivation.SendAsync(_pipeName, "quit"));
            Exit(); return;
        }
        _singleInstance = new Mutex(true, @"Local\CloudBay.Client." + sid + isolationSuffix, out var first);
        if (!first)
        {
            if (!(isSmoke || isLive) && commandLine.Any(value => value.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal)))
            {
                RegisterNotifications();
                ReadNotificationLifecycleActivation();
                try
                {
                    var notification = await _firstNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Environment.ExitCode = ClientActivation.ExitCode(await ClientActivation.SendAsync(_pipeName,
                        CloudBay.Core.Notifications.NotificationCommandCodec.Encode(notification)));
                }
                catch (TimeoutException) { Environment.ExitCode = 2; }
                if (_notifications is not null) await _notifications.DisposeAsync();
                Exit(); return;
            }
            var activation = isLive && commandLine.Contains("--show-tray") ? "tray" : "show";
            Environment.ExitCode = ClientActivation.ExitCode(await ClientActivation.SendAsync(_pipeName, activation));
            Exit(); return;
        }
        try
        {
            if (!(isSmoke || isLive))
            {
                RegisterNotifications();
                ReadNotificationLifecycleActivation();
            }
            _startupStage = "Initialize client state";
            var isolatedStorage = isSmoke || isLive ? new ClientStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudBay", isSmoke ? "UiValidation" : "UiLive", isSmoke ? Guid.NewGuid().ToString("N") : "Client")) : null;
            _controller = new ClientController(isolatedStorage, manageStartup: !(isSmoke || isLive));
            _startupStage = "Create main window";
            MainWindow = new MainWindow(_controller);
            if (!(isSmoke || isLive))
            {
                try
                {
                    var installation = UpdateInstallation.Load();
                    var updateDirectory = Path.Combine(BuildInfo.DefaultDataDirectory, "Updates");
                    var installer = installation.CanInstall ? new WindowsUpdateInstaller(installation, updateDirectory,
                        ReadRestartPreferenceAsync,
                        () => MainWindow.DispatcherQueue.TryEnqueue(() => _ = QuitAsync())) : null;
                    _updates = new UpdateCoordinator(installation.Identity, updateDirectory, installer);
                    _notificationCanInstallUpdates = installation.CanInstall;
                    _updates.Changed += Updates_NotificationChanged;
                    MainWindow.AttachUpdates(_updates, installation);
                    _ = StartUpdatesAsync();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { _updatesInitialized.TrySetResult(); MainWindow.ShowUpdateInitializationError(error.Message); }
            }
            _startupStage = "Create tray window";
            _trayWindow = new TrayWindow(_controller, () => { MainWindow.ShowSettings(); MainWindow.ShowWindow(); }, () => _ = QuitAsync());
            _startupStage = "Register notification icon";
            var trayActions = new TrayIconActions(MainWindow.ShowWindow, () => _controller.LaunchFolder(),
                    () => { if (_controller.Snapshot.State == ClientState.Paused) _controller.Resume(); else _controller.Pause(); },
                    () => { MainWindow.ShowSettings(); MainWindow.ShowWindow(); }, () => _ = QuitAsync())
                {
                    Availability = () => new(_controller.Settings.IsConfigured, _controller.Settings.IsConfigured,
                        _controller.Snapshot.State == ClientState.Paused),
                    BeforeMenuOpen = () => _trayWindow.AppWindow.Hide(),
                    ShowContextMenu = point => ShowTrayContextMenu(point),
                    Error = error => _ = ShowTrayCommandErrorAsync(error)
                };
            _trayActions = trayActions;
            _trayIcon = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow),
                Path.Combine(AppContext.BaseDirectory, "Assets", "CloudBay.ico"),
                () => { _trayContextMenu?.Hide(); _trayWindow.ShowAtTray(); }, MainWindow.ShowWindow, trayActions);
            _trayIcon.Update(_controller.Snapshot);
            _controller.Changed += Controller_Changed;
            _ = ListenForActivationAsync();
            StartNotificationPolicy();
            var startupActivation = UpdateInstallation.IsPackaged && _notificationStartupTask;
            var notificationActivation = commandLine.Any(value => value.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal)) ||
                _firstNotification.Task.IsCompleted;
            if (!notificationActivation && (!(commandLine.Contains("--background") || startupActivation) || !_controller.Settings.IsConfigured)) MainWindow.ShowWindow();
            _startupStage = "Start client controller";
            var clientStartup = _controller.StartAsync();
            _notificationCommandsReady = true;
            DrainNotificationCommands();
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
                if (commandLine.Contains("--ui-smoke-updates")) await MainWindow.RunUpdateOnlyUiValidationAsync(output);
                else
                {
                    if (!commandLine.Contains("--ui-smoke-tray")) await MainWindow.RunUiSmokeAsync(output);
                    await _trayWindow.RunUiSmokeAsync(output);
                    var menu = GetTrayContextMenu();
                    await menu.RunUiValidationAsync(output);
                    await _trayWindow.RunQuickSettingsAlignmentValidationAsync(menu, output);
                }
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
        Environment.GetCommandLineArgs().Contains("--ui-smoke-updates") ? "ui-smoke-updates" :
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
                if (Environment.GetCommandLineArgs().Contains("--ui-smoke-updates")) launch.ArgumentList.Add("--ui-smoke-updates");
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
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                string? command;
                try { command = await ClientActivation.ReadCommandAsync(server, readDeadline.Token); }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
                if (command == "show")
                    MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow.ShowWindow());
                else if (command == "quit")
                    MainWindow?.DispatcherQueue.TryEnqueue(() => _ = QuitAsync());
                else if (_isUiLive && command == "tray")
                    MainWindow?.DispatcherQueue.TryEnqueue(() => _trayWindow?.ShowAtTray(keepOpenForInspection: true));
                else if (CloudBay.Core.Notifications.NotificationCommandCodec.TryDecode(command, out var notification))
                    QueueNotificationCommand(notification);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void Controller_Changed(object? sender, EventArgs args)
    {
        PumpNotifications();
        if (Interlocked.Exchange(ref _trayUpdatePending, 1) != 0) return;
        var window = MainWindow;
        if (window is null || !window.DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _trayUpdatePending, 0);
            if (!_exiting && _controller is not null) _trayIcon?.Update(_controller.Snapshot);
        })) Interlocked.Exchange(ref _trayUpdatePending, 0);
    }
    private TrayContextMenuWindow GetTrayContextMenu()
    {
        if (_trayContextMenu is not null) return _trayContextMenu;
        if (_trayActions is null || _controller is null) throw new InvalidOperationException("Tray commands are not ready.");
        _trayContextMenu = new TrayContextMenuWindow(_trayActions.Availability!, _trayActions, () => _controller.Settings.Theme)
        {
            ReturnKeyboardFocus = () => _trayIcon?.ReturnKeyboardFocus()
        };
        return _trayContextMenu;
    }

    private void ShowTrayContextMenu(global::Windows.Graphics.PointInt32 point) => GetTrayContextMenu().ShowAt(point);

    private async Task ShowTrayCommandErrorAsync(Exception error)
    {
        if (_exiting || MainWindow?.Content is not FrameworkElement content) return;
        MainWindow.ShowWindow();
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "CloudBay couldn't complete this action",
            Content = error.Message,
            CloseButtonText = "Close",
            XamlRoot = content.XamlRoot
        };
        try { await dialog.ShowAsync(); }
        catch (InvalidOperationException) { /* An existing dialog already owns the window. */ }
    }
    private async Task QuitAsync()
    {
        if (_exiting) return; _exiting = true;
        _lifetime.Cancel();
        _notificationTimer?.Dispose();
        if (_notifications is not null) await _notifications.DisposeAsync();
        if (_controller is not null) _controller.Changed -= Controller_Changed;
        _trayIcon?.Dispose();
        _trayContextMenu?.Close();
        if (_updates is not null) { _updates.Changed -= Updates_NotificationChanged; await _updates.DisposeAsync(); }
        if (_controller is not null) await _controller.DisposeAsync();
        _trayWindow?.Close();
        if (MainWindow is not null) { MainWindow.AllowClose = true; MainWindow.Close(); }
        _singleInstance?.Dispose();
        Exit();
    }

    private async Task StartUpdatesAsync()
    {
        try
        {
            await _updates!.InitializeAsync(_lifetime.Token);
            try
            {
                var failure = await Task.Run(() => WindowsUpdateInstaller.ReadFailureAndPruneHosts(
                    Path.Combine(BuildInfo.DefaultDataDirectory, "Updates")), _lifetime.Token);
                if (failure is not null) MainWindow?.ShowUpdateInitializationError(failure);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { MainWindow?.ShowUpdateInitializationError("Update maintenance could not finish: " + error.Message); }
            if (!_exiting) _updates.Start();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { MainWindow?.ShowUpdateInitializationError(error.Message); }
        finally { _updatesInitialized.TrySetResult(); }
    }

    private Task<bool> ReadRestartPreferenceAsync()
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (MainWindow is null || !MainWindow.DispatcherQueue.TryEnqueue(() => result.TrySetResult(!MainWindow.AppWindow.IsVisible)))
            result.TrySetException(new IOException("The application is closing. Try installing the update after restarting CloudBay."));
        return result.Task;
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CloudBay.Application;
using CloudBay.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace CloudBay.Windows;

/// <summary>
/// An opt-in native focus regression probe using only windows owned by this
/// isolated process. It does not click Explorer's notification area or configure
/// an account, sync root, known folder, startup entry, or backup operation.
/// </summary>
internal static class TrayFocusValidation
{
    // Retain an independent host until the isolated caller exits. Closing the
    // last XAML window could otherwise interrupt the result-writing continuation.
    private static Window? _host;
    private sealed record Check(string Name, bool Passed, string Evidence);
    private sealed record Observation(string Name, string Evidence);

    public static async Task<bool> RunAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (_host is not null)
            throw new InvalidOperationException("Native tray focus validation runs once per isolated process.");
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var runId = Guid.NewGuid().ToString("N");
        var storagePath = Path.Combine(Path.GetTempPath(), "CloudBay-TrayFocusValidation-" + runId);
        var startedUtc = DateTimeOffset.UtcNow;
        var menuFirst = Environment.GetCommandLineArgs().Contains("--tray-focus-menu-first");
        var checks = new List<Check>();
        var observations = new List<Observation>();
        var menuCallbacks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Open app"] = 0, ["Open folder"] = 0, ["Toggle pause"] = 0, ["Settings"] = 0, ["Quit"] = 0
        };
        var menuErrors = new List<string>();
        var status = "Running";
        ClientController? controller = null;
        TrayWindow? tray = null;
        TrayContextMenuWindow? menu = null;
        nint trayHandle = 0;
        nint menuHandle = 0;
        var work = DisplayArea.Primary.WorkArea;
        var menuPoint = new PointInt32(work.X + work.Width - 16, work.Y + work.Height - 16);
        var hostButton = new Button
        {
            Content = "Focus validation host",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };
        var host = new Window { Title = "CloudBay tray focus validation", Content = hostButton };
        host.AppWindow.Resize(new SizeInt32(420, 180));
        _host = host;
        var hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(host);
        try
        {
            await WriteReportAsync();
            // ShowAtTray's default smoke branch is selected from the real process
            // command line. Do not claim to test that branch when it was not used.
            Record("Smoke process is isolated", Environment.GetCommandLineArgs().Contains("--ui-smoke"),
                "The --ui-smoke flag must accompany --tray-focus-smoke.");
            controller = new ClientController(new ClientStorage(storagePath),
                unregisterSyncRoot: _ => throw new InvalidOperationException("Focus validation cannot unregister a sync root."),
                manageStartup: false);
            tray = new TrayWindow(controller, () => { }, () => { });
            trayHandle = WinRT.Interop.WindowNative.GetWindowHandle(tray);


            await MakeHostForegroundAsync("Independent host receives foreground input");
            if (menuFirst)
            {
                menu = EnsureMenu();
                await OpenMenuAndVerifyAsync("Right-click-first cold Fluent menu receives foreground and XAML focus");
                await MakeHostForegroundAsync("Right-click-first menu loses foreground to independent host");
                await RequireAsync("Right-click-first menu dismisses before activity has ever opened", () => !menu!.IsVisible);
            }
            // Exercise the cold first show before a nonactivating render can
            // load the XAML and conceal a first-opening focus timing defect.
            await OpenAndVerifyAsync("First tray opening receives foreground and XAML focus");
            await MakeHostForegroundAsync("Returning to another owned window dismisses tray");
            await RequireAsync("Tray hides after foreground deactivation", () => !tray.AppWindow.IsVisible);

            await OpenAndVerifyAsync("Tray reopening receives foreground and XAML focus");
            tray.AppWindow.Hide();
            await MakeHostForegroundAsync("Hidden tray returns foreground to independent host");
            await OpenAndVerifyAsync("Explicitly hidden tray reopens with foreground and XAML focus");
            await MakeHostForegroundAsync("Reopened tray loses foreground to independent host");
            await RequireAsync("Reopened tray still dismisses on deactivation", () => !tray.AppWindow.IsVisible);

            tray.ShowAtTray();
            observations.Add(new("Ordinary smoke tray immediately after ShowAtTray", DescribeState()));
            await Task.Delay(350, cancellationToken);
            Record("Ordinary smoke show remains nonactivating",
                tray.AppWindow.IsVisible && GetForegroundWindow() == hostHandle,
                DescribeState());
            tray.AppWindow.Hide();

            // Production creates the independent menu only on its first
            // explicit request, after the activity surface has been used.
            menu = EnsureMenu();
            await OpenMenuAndVerifyAsync("First Fluent menu opening receives foreground and XAML focus");
            await MakeHostForegroundAsync("Returning to another owned window dismisses Fluent menu");
            await RequireAsync("Fluent menu hides after foreground deactivation", () => !menu.IsVisible);
            await OpenMenuAndVerifyAsync("Fluent menu reopening receives foreground and XAML focus");
            menu.Hide();
            await MakeHostForegroundAsync("Hidden Fluent menu returns foreground to independent host");
            await OpenMenuAndVerifyAsync("Explicitly hidden Fluent menu reopens with foreground and XAML focus");
            await MakeHostForegroundAsync("Reopened Fluent menu loses foreground to independent host");
            await RequireAsync("Reopened Fluent menu still dismisses on deactivation", () => !menu.IsVisible);
            await OpenAndVerifyAsync("Activity window regains focus after the Fluent menu has loaded");
            await MakeHostForegroundAsync("Switching away still dismisses activity after menu use");
            await RequireAsync("Activity dismisses after switching between tray surfaces", () => !tray.AppWindow.IsVisible);
            await OpenMenuAndVerifyAsync("Fluent menu regains focus after switching back from activity");
            await MakeHostForegroundAsync("Switching away dismisses the reused Fluent menu");
            await RequireAsync("Fluent menu dismisses after switching between tray surfaces", () => !menu.IsVisible);

            menu.ShowAt(menuPoint);
            observations.Add(new("Ordinary smoke menu immediately after ShowAt", DescribeState()));
            await Task.Delay(350, cancellationToken);
            Record("Ordinary smoke menu show remains nonactivating",
                menu.IsVisible && GetForegroundWindow() == hostHandle, DescribeState());
            menu.Hide();
            Record("Opening and dismissing Fluent menu does not invoke commands",
                menuCallbacks.Values.All(count => count == 0) && menuErrors.Count == 0,
                "Only dummy command callbacks were supplied; all five callback counts are zero and no command errors occurred.");

            Record("Validation leaves account and backup state unconfigured",
                !controller.Settings.IsConfigured && controller.Settings.Backups.Count == 0 && controller.Settings.CustomBackups.Count == 0 &&
                !File.Exists(Path.Combine(storagePath, "credentials.dpapi")) && !Directory.Exists(Path.Combine(storagePath, "State")),
                "Fresh temporary ClientStorage; StartAsync and backup operations were never called.");
            status = "Passed";
        }
        catch (Exception error)
        {
            status = "Failed";
            if (checks.All(check => check.Passed))
                checks.Add(new("Probe completion", false, $"{error.GetType().Name}: 0x{error.GetBaseException().HResult:X8}"));
        }
        finally
        {
            try { menu?.Close(); }
            catch (Exception error)
            {
                status = "Failed";
                checks.Add(new("Isolated menu close", false, $"{error.GetType().Name}: 0x{error.GetBaseException().HResult:X8}"));
            }
            try { tray?.Close(); }
            catch (Exception error)
            {
                status = "Failed";
                checks.Add(new("Isolated tray close", false, $"{error.GetType().Name}: 0x{error.GetBaseException().HResult:X8}"));
            }
            if (controller is not null)
            {
                try { await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception error)
                {
                    status = "Failed";
                    checks.Add(new("Isolated controller disposal", false, $"{error.GetType().Name}: 0x{error.GetBaseException().HResult:X8}"));
                }
            }
            host.AppWindow.Hide();
            await WriteReportAsync();
        }
        return status == "Passed";

        async Task MakeHostForegroundAsync(string name)
        {
            host.AppWindow.Show(false);
            // One foreground request for this test step; observation below never
            // retries activation or repeatedly takes input away from the user.
            host.Activate();
            SetForegroundWindow(hostHandle);
            hostButton.Focus(FocusState.Programmatic);
            observations.Add(new(name + " immediately after foreground request", DescribeState()));
            await RequireAsync(name, () => GetForegroundWindow() == hostHandle);
        }

        async Task OpenAndVerifyAsync(string name)
        {
            var focusTray = tray ?? throw new InvalidOperationException("The isolated tray has not been created.");
            focusTray.ShowAtTray(forceActivationForValidation: true);
            observations.Add(new(name + " immediately after ShowAtTray", DescribeState()));
            await RequireAsync(name, () => focusTray.AppWindow.IsVisible && GetForegroundWindow() == trayHandle &&
                focusTray.FocusStateForValidation != FocusState.Unfocused);
            // A bounded stability observation catches immediate deactivation or
            // dismissal after the initial synchronous activation callback.
            await Task.Delay(350, cancellationToken);
            Record(name + " remains stable", focusTray.AppWindow.IsVisible && GetForegroundWindow() == trayHandle &&
                focusTray.FocusStateForValidation != FocusState.Unfocused, DescribeState());
        }

        async Task OpenMenuAndVerifyAsync(string name)
        {
            var focusMenu = menu ?? throw new InvalidOperationException("The isolated menu has not been created.");
            focusMenu.ShowAt(menuPoint, forceActivationForValidation: true);
            observations.Add(new(name + " immediately after ShowAt", DescribeState()));
            await RequireAsync(name, () => focusMenu.IsVisible && GetForegroundWindow() == menuHandle &&
                focusMenu.FocusStateForValidation != FocusState.Unfocused);
            await Task.Delay(350, cancellationToken);
            Record(name + " remains stable", focusMenu.IsVisible && GetForegroundWindow() == menuHandle &&
                focusMenu.FocusStateForValidation != FocusState.Unfocused, DescribeState());
        }

        Action MenuCallback(string command) => () => menuCallbacks[command]++;

        TrayContextMenuWindow EnsureMenu()
        {
            if (menu is not null) return menu;
            var actions = new TrayIconActions(MenuCallback("Open app"), MenuCallback("Open folder"),
                MenuCallback("Toggle pause"), MenuCallback("Settings"), MenuCallback("Quit"))
            {
                Error = error => menuErrors.Add($"{error.GetType().Name}: 0x{error.GetBaseException().HResult:X8}")
            };
            menu = new TrayContextMenuWindow(() => new TrayMenuAvailability(true, true), actions, () => "Dark");
            menuHandle = WinRT.Interop.WindowNative.GetWindowHandle(menu);
            return menu;
        }

        async Task RequireAsync(string name, Func<bool> condition)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(50, cancellationToken);
            Record(name, condition(), DescribeState());
        }

        void Record(string name, bool passed, string evidence)
        {
            checks.Add(new(name, passed, evidence));
            if (!passed) throw new InvalidOperationException("Native tray focus check failed: " + name);
        }

        string DescribeState()
        {
            var foreground = GetForegroundWindow();
            var thread = GetWindowThreadProcessId(foreground, out var process);
            return $"foreground=0x{foreground:X}; foregroundProcess={process}; foregroundThread={thread}; " +
                $"foregroundOwnedByProbe={process == (uint)Environment.ProcessId}; host=0x{hostHandle:X}; tray=0x{trayHandle:X}; " +
                $"trayVisible={tray?.AppWindow.IsVisible}; trayFocus={tray?.FocusStateForValidation}; menu=0x{menuHandle:X}; " +
                $"menuVisible={menu?.IsVisible}; menuFocus={menu?.FocusStateForValidation}";
        }

        async Task WriteReportAsync()
        {
            var report = new
            {
                RunId = runId, StartedUtc = startedUtc, UpdatedUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
                ApplicationVersion = typeof(TrayFocusValidation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Status = status, Passed = status == "Passed", MenuFirst = menuFirst, Checks = checks, Observations = observations,
                MenuCommandCallbacks = menuCallbacks, MenuCommandErrors = menuErrors,
                StoragePath = storagePath,
                Scope = "Own-process native foreground ownership, initial XAML action focus, deactivation dismissal, hide/reopen lifecycle, and nonactivating smoke capture for the activity window and Fluent tray menu.",
                FocusStateVerification = "Pointer, Keyboard, and Programmatic indicate a focused action; only Unfocused fails. Exact HWND foreground ownership is required separately.",
                LiveExplorerClickAutomated = false,
                InputAutomation = "None. Only this process's WinUI windows and their HWND foreground state were used.",
                PersonalStateTouched = false
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "native-tray-focus-validation.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var lines = new List<string>
            {
                "Native tray focus validation: " + status,
                "Run: " + runId,
                report.Scope,
                "This does not automate or prove a live Explorer notification-icon click."
            };
            lines.AddRange(checks.Select(check => $"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Evidence}"));
            lines.AddRange(observations.Select(observation => $"OBSERVE {observation.Name}: {observation.Evidence}"));
            lines.Add(report.FocusStateVerification);
            await File.WriteAllLinesAsync(Path.Combine(outputDirectory, "native-tray-focus-validation.txt"), lines);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

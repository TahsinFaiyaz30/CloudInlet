using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace CloudBay.Windows;

/// <summary>
/// Isolated, opt-in native activation checks. This uses the real desktop picker API,
/// inspects only this process's owned native windows, and never supplies a selection.
/// </summary>
internal static class NativePickerValidation
{
    // The isolated caller exits the application after writing the result. Retaining
    // this host avoids last-window shutdown before its await continuation can run.
    private static Window? _host;
    private sealed record Check(string Name, bool Passed, string Evidence);
    private sealed record PickerProbe(string Kind, bool VisibleOwnedNativeDialog, bool PendingAtActivation,
        bool ConcurrentRequestRejected, bool OwnerClosed, bool FinishedAfterCancellation,
        string Completion, int SelectedItems, string? Error)
    {
        public bool Passed => VisibleOwnedNativeDialog && PendingAtActivation && ConcurrentRequestRejected &&
            OwnerClosed && FinishedAfterCancellation && Error is null;
    }

    public static async Task<bool> RunAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (_host is not null) throw new InvalidOperationException("Native chooser validation runs once per isolated process.");
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var runId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTimeOffset.UtcNow;
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var checks = new List<Check>();
        var probes = new List<PickerProbe>();
        // Keep an independent host alive while each modal dialog's own test window closes.
        // No ClientController, account state, or backup mapping is created by this probe.
        var host = CreateWindow("CloudBay chooser validation", "Checking native Windows chooser activation. No backup settings are changed.");
        _host = host;
        host.Activate();
        try
        {
            await WriteReportAsync("Running");
            var hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(host);
            checks.Add(await CheckRejectionAsync("Invalid owner", () => DesktopPickers.PickFileAsync(0), "not ready"));
            checks.Add(await CheckRejectionAsync("Wrong UI thread", () => Task.Run(() => DesktopPickers.PickFileAsync(hostHandle)), "UI thread"));
            if (checks.Any(check => !check.Passed))
            {
                await WriteReportAsync("Failed");
                return false;
            }

            foreach (var kind in new[] { "Single file", "Multiple files", "Folder" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                probes.Add(await ProbeAsync(kind, cancellationToken));
                await WriteReportAsync("Running");
                // A native dialog that did not finish after its owner closed must not
                // be followed by additional modal windows. The isolated process exits.
                if (!probes[^1].FinishedAfterCancellation) break;
            }
            var passed = checks.All(check => check.Passed) && probes.Count == 3 && probes.All(probe => probe.Passed);
            await WriteReportAsync(passed ? "Passed" : "Failed");
            return passed;
        }
        catch (Exception error)
        {
            checks.Add(new("Probe completion", false, Describe(error)));
            await WriteReportAsync("Failed");
            return false;
        }

        async Task WriteReportAsync(string status)
        {
            var report = new
            {
                RunId = runId, StartedUtc = startedUtc, UpdatedUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId, Elevated = elevated, Status = status,
                Passed = status == "Passed", Checks = checks, Pickers = probes,
                Scope = "Actual API activation, visible owned Windows dialog, cancellation and owner-close lifecycle.",
                SelectionVerification = "No input was automated and no file or folder was supplied. This does not prove successful user selection or multiselection."
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "native-picker-validation.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var lines = new List<string> { $"Native Windows chooser validation: {status}", $"Elevated: {elevated}", $"Run: {runId}" };
            lines.AddRange(checks.Select(check => $"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Evidence}"));
            lines.AddRange(probes.Select(probe => $"{(probe.Passed ? "PASS" : "FAIL")} {probe.Kind}: visible={probe.VisibleOwnedNativeDialog}, pending={probe.PendingAtActivation}, concurrency={probe.ConcurrentRequestRejected}, ownerClosed={probe.OwnerClosed}, completion={probe.Completion}"));
            lines.Add(report.SelectionVerification);
            await File.WriteAllLinesAsync(Path.Combine(outputDirectory, "native-picker-validation.txt"), lines);
        }
    }

    private static async Task<Check> CheckRejectionAsync(string name, Func<Task> attempt, string expectedText)
    {
        Task? request = null;
        try
        {
            request = attempt();
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            return new(name, false, "The invalid request was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains(expectedText, StringComparison.Ordinal))
        {
            return new(name, true, error.Message);
        }
        catch (TimeoutException)
        {
            if (request is not null)
                _ = request.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new(name, false, "The invalid request remained pending instead of being rejected.");
        }
        catch (Exception error) { return new(name, false, Describe(error)); }
    }

    private static async Task<PickerProbe> ProbeAsync(string kind, CancellationToken cancellationToken)
    {
        var window = CreateWindow("CloudBay chooser validation - " + kind,
            "Checking native Windows chooser activation. No files are read and no selection is supplied.");
        window.Activate();
        var owner = WinRT.Interop.WindowNative.GetWindowHandle(window);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var visible = false;
        var pending = false;
        var concurrentRejected = false;
        var ownerClosed = false;
        var finished = false;
        var completion = "Not started";
        var selectedItems = 0;
        string? error = null;
        Task<int>? choose = null;
        try
        {
            choose = kind switch
            {
                "Single file" => PickSingleAsync(),
                "Multiple files" => PickMultipleAsync(),
                _ => PickFolderAsync()
            };
            var activationDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!choose.IsCompleted && DateTimeOffset.UtcNow < activationDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visible = HasVisibleOwnedDialog(owner);
                if (visible) break;
                await Task.Delay(100, cancellationToken);
            }
            pending = !choose.IsCompleted;
            visible |= HasVisibleOwnedDialog(owner);
            if (pending)
            {
                var guard = await CheckRejectionAsync("Duplicate picker", () => DesktopPickers.PickFileAsync(owner), "Finish or cancel");
                concurrentRejected = guard.Passed;
                if (!guard.Passed) error = guard.Evidence;
            }
            if (!pending)
            {
                selectedItems = await choose;
                completion = selectedItems == 0 ? "Completed before native dialog was observed" : "User completed a selection before observation";
            }
        }
        catch (Exception failure) { error = Describe(failure); }
        finally
        {
            // The SDK checks its cancellation token around the modal Show call; it
            // does not guarantee that Cancel itself dismisses an already open dialog.
            // Close only the WinUI owner created above, through its normal lifecycle.
            cancel.Cancel();
            window.Close();
            ownerClosed = !IsWindow(owner);
            if (choose is not null)
            {
                try
                {
                    selectedItems = await choose.WaitAsync(TimeSpan.FromSeconds(3));
                    completion = selectedItems == 0 ? "Completed without a selection after cancellation/owner close" : "User supplied a selection";
                    finished = true;
                }
                catch (OperationCanceledException)
                {
                    completion = "Cancelled by the official picker async operation";
                    finished = true;
                }
                catch (TimeoutException)
                {
                    completion = "Still pending after token cancellation and owner close";
                    _ = choose.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                catch (Exception failure)
                {
                    completion = "Failed after cancellation/owner close";
                    error ??= Describe(failure);
                    finished = true;
                }
            }
        }
        return new(kind, visible, pending, concurrentRejected, ownerClosed, finished, completion, selectedItems, error);

        async Task<int> PickSingleAsync() => await DesktopPickers.PickFileAsync(owner, "Choose test file",
            cancellationToken: cancel.Token) is null ? 0 : 1;
        async Task<int> PickMultipleAsync() => (await DesktopPickers.PickFilesAsync(owner, "Choose test files",
            cancellationToken: cancel.Token)).Count;
        async Task<int> PickFolderAsync() => await DesktopPickers.PickFolderAsync(owner, "Choose test folder",
            cancellationToken: cancel.Token) is null ? 0 : 1;
    }

    private static Window CreateWindow(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
                FontSize = 14, Margin = new Thickness(24) }
        };
        window.AppWindow.Resize(new SizeInt32(480, 200));
        return window;
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: 0x{error.GetBaseException().HResult:X8}";

    private static bool HasVisibleOwnedDialog(nint owner)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            if (window == owner || !IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId != (uint)Environment.ProcessId) return true;
            var ownedBy = GetWindow(window, 4); // GW_OWNER
            for (var depth = 0; ownedBy != 0 && depth < 8; depth++, ownedBy = GetWindow(ownedBy, 4))
            {
                if (ownedBy != owner) continue;
                var name = new StringBuilder(128);
                GetClassName(window, name, name.Capacity);
                if (!name.ToString().Equals("#32770", StringComparison.Ordinal)) return true;
                found = true;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximum);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

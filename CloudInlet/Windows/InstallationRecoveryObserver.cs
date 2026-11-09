using System.Diagnostics;
using CloudInlet.Core;
using CloudInlet.Packaging;
using CloudInlet.Views;

namespace CloudInlet.Windows;

/// <summary>A recovery-only process: no profile, mutex, accounts or sync controller is opened.</summary>
internal static class InstallationRecoveryObserver
{
    internal static async Task<bool> OpenPendingAsync()
    {
        var pending = ExeMigrationReceipt.FindPending(BuildInfo.Flavor);
        if (pending is null) return false;
        if (pending.IsObserverReady()) return true;
        if (pending.ObserverDirectory is null)
            throw new IOException("An unfinished installation migration has no verified recovery window. Its recovery files were retained for repair.");
        var executable = Path.Combine(pending.ObserverDirectory, "CloudInlet.exe");
        pending.VerifyObserver(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = pending.ObserverDirectory };
        start.ArgumentList.Add("--exe-migration-progress=" + Path.Combine(ExeMigrationReceipt.Root(pending.Flavor), pending.Id, "migration.dpapi"));
        using var observer = Process.Start(start) ?? throw new IOException("Windows could not reopen installation recovery.");
        var timeout = Stopwatch.StartNew();
        while (!pending.IsObserverReady())
        {
            if (observer.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(30))
                throw new IOException("The installation recovery window could not become ready. Its files and progress were retained.");
            await Task.Delay(100);
        }
        return true;
    }

    internal static async Task RunAsync(string[] arguments)
    {
        var window = new InstallationRecoveryWindow(); window.Activate();
        FileStream? runtimeLease = null;
        var runningAction = false;
        var sequence = -1L;
        string? identity = null;
        // An action may still finish its read after the window closes. No wait
        // handle is allocated; let the captured gate live until those tasks end.
        var readGate = new SemaphoreSlim(1, 1);
        try
        {
            await window.Ready;
            var value = arguments.Single(arg => arg.StartsWith("--exe-migration-progress=", StringComparison.Ordinal));
            var path = Path.GetFullPath(value["--exe-migration-progress=".Length..]);
            var initial = await InstallationRecoveryPolling.ReadAsync(() => ExeMigrationReceipt.Inspect(path));
            while (initial is null)
            {
                window.ShowStatusUnavailable();
                await Task.WhenAny(window.ClosedTask, Task.Delay(400));
                if (window.ClosedTask.IsCompleted) return;
                initial = await InstallationRecoveryPolling.ReadAsync(() => ExeMigrationReceipt.Inspect(path));
            }
            if (initial.Flavor != BuildInfo.Flavor) throw new IOException("This recovery window belongs to a different application build.");
            var currentBoot = InstallerMigrationRecovery.CurrentBootIdentity();
            runtimeLease = InstallerProgressHost.AcquireObserverLease(AppContext.BaseDirectory);
            initial.VerifyObserver(Environment.ProcessPath!);
            identity = initial.Id + "|" + initial.RequestHash;
            async Task<ExeMigrationReceipt?> CurrentAsync()
            {
                await readGate.WaitAsync();
                try
                {
                    var record = await InstallationRecoveryPolling.ReadAsync(() => ExeMigrationReceipt.Inspect(path));
                    if (record is null) { window.ShowStatusUnavailable(); return null; }
                    if (record.Id + "|" + record.RequestHash != identity || record.State.Sequence < sequence)
                        throw new IOException("The installation recovery record changed identity or moved backwards.");
                    sequence = record.State.Sequence;
                    return record;
                }
                finally { readGate.Release(); }
            }
            void Report(ExeMigrationReceipt record)
            {
                var active = runningAction || record.IsActive();
                var requiresRestart = !active && record.NativeAttempt?.RequiresRestart(currentBoot) == true;
                var restartSatisfied = !active && record.State.Phase is not ("Completed" or "Reverted") &&
                    record.NativeAttempt is { ExitCode: null or 3010 } && !requiresRestart;
                // Use the persisted error kind, including unknown native outcomes.
                // A new verification failure clears this flag and remains visible.
                var staleRestartFailure = restartSatisfied && record.FailureRequiresRestart;
                var restartMessage = requiresRestart ? record.NativeAttempt!.ExitCode == 3010
                    ? "The installer requires a Windows restart to finish its changes. Save your work and restart Windows, then reopen installation recovery and choose Retry. Migration has not been marked complete."
                    : "The last installer operation has no confirmed outcome. Save your work and restart Windows before retrying so recovery cannot overlap an unfinished installer. Your recovery records have been retained."
                    : null;
                var detail = restartSatisfied && (record.Failure is null || staleRestartFailure)
                    ? record.State.Detail + " Windows has restarted. Choose Retry to verify the saved installer step and continue recovery."
                    : record.Failure ?? record.State.Detail;
                window.Report(record.State.Phase, detail,
                    record.State.Source, record.State.Destination, active,
                    record.State.Phase is not ("Completed" or "Reverted"), requiresRestart, restartMessage);
                if (!active && !requiresRestart && !staleRestartFailure && record.Failure is { Length: > 0 } failure) window.ShowFailure(failure);
            }
            window.Recover = async revert =>
            {
                var record = await CurrentAsync();
                if (record is null) return;
                if (record.IsActive()) throw new IOException("Installation recovery is already running. Its progress will appear here.");
                if (record.NativeAttempt?.RequiresRestart(currentBoot) == true) { Report(record); return; }
                if (record.State.Phase is "Completed" or "Reverted") throw new IOException("This installation change is already verified.");
                runningAction = true;
                try
                {
                    using var helper = record.OpenVerifiedHelper();
                    var start = new ProcessStartInfo(record.HelperPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add(revert ? "--revert-exe-migration" : "--resume-exe-migration"); start.ArgumentList.Add(path);
                    using var worker = Process.Start(start) ?? throw new IOException("Windows could not open the verified recovery helper.");
                    // Closing this view never cancels the installer. Its durable receipt and
                    // sign-in recovery entry remain available after this process exits.
                    await Task.WhenAny(worker.WaitForExitAsync(), window.ClosedTask);
                    if (!window.ClosedTask.IsCompleted)
                    {
                        var after = await CurrentAsync();
                        if (after is null) return;
                        if (worker.HasExited && worker.ExitCode != 0 && after.Failure is null)
                            throw new IOException("The recovery helper stopped without a verified result. Retry continues from its saved step.");
                    }
                }
                finally { runningAction = false; }
            };
            window.OpenApplication = async () =>
            {
                var record = await CurrentAsync();
                if (record is null) return;
                if (record.IsActive()) throw new IOException("Wait for installation recovery to finish before opening CloudInlet.");
                if (record.NativeAttempt?.RequiresRestart(currentBoot) == true) { Report(record); return; }
                var executable = record.VerifyCompletedApplication();
                using var app = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(executable)! }) ?? throw new IOException("Windows could not open the verified installation.");
                window.Close();
            };
            if (await CurrentAsync() is { } ready) Report(ready);
            initial.PublishObserverReady();
            while (!window.ClosedTask.IsCompleted)
            {
                await Task.WhenAny(window.ClosedTask, Task.Delay(400));
                if (window.ClosedTask.IsCompleted) break;
                if (await CurrentAsync() is { } current) Report(current);
            }
        }
        catch (Exception error) { window.ShowObservationFailure(error.Message); }
        await window.ClosedTask;
        runtimeLease?.Dispose();
    }
}

using System.Diagnostics;
using System.Globalization;
using CloudInlet.Packaging;
using CloudInlet.Views;

namespace CloudInlet.Windows;

internal static class InstallerProgressObserver
{
    internal static async Task RunAsync(string[] arguments)
    {
        var window = new InstallerProgressWindow();
        window.Activate();
        FileStream? runtimeLease = null;
        try
        {
            runtimeLease = InstallerProgressHost.AcquireObserverLease(AppContext.BaseDirectory);
            await window.Ready;
            string Argument(string name) => arguments.Single(value => value.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];
            var path = Path.GetFullPath(Argument("--installer-progress"));
            var id = Argument("--progress-id"); var hash = Argument("--request-hash");
            var cache = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
            InstallerProgressJournal.ValidatePath(cache, path, id);
            // Observers can only read this build's recognized update cache and its exact
            // request. They never initialize application data, run transfers or installers.
            var request = Path.Combine(cache, "host", id + ".json");
            UpdateCacheLocation.Validate(CloudInlet.Core.BuildInfo.Flavor, request,
                Path.Combine(cache, "observer-validation"), Path.Combine(cache, "install-result.json"),
                Path.Combine(cache, "host", id + ".ready.json"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using (var input = new FileStream(request, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length is < 1 or > 65536) throw new IOException("The update request cannot be verified.");
                var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                if (InstallerProgressJournal.RequestHash(bytes) != hash) throw new IOException("The update request changed after this progress window was opened.");
            }
            var workerId = int.Parse(Argument("--worker-id"), CultureInfo.InvariantCulture);
            var workerStart = long.Parse(Argument("--worker-start"), CultureInfo.InvariantCulture);
            var initial = InstallerProgressJournal.Read(path, id, hash)
                ?? throw new IOException("The installer has not published its verified progress record.");
            window.Report(initial);
            InstallerProgressJournal.PublishObserverReady(path, id, hash);
            var terminal = false;
            while (!window.ClosedTask.IsCompleted && !terminal)
            {
                var record = InstallerProgressJournal.Read(path, id, hash);
                // The helper sets this only after the replacement recovery window
                // has authenticated its visible-ready handshake. Retire this view
                // so one window owns subsequent retry/revert and completion.
                if (record?.RecoveryWindowShown == true) { window.Close(); break; }
                if (record is not null) { window.Report(record); terminal = record.Status != "Active"; }
                if (terminal) break;
                var alive = false;
                try
                {
                    using var process = Process.GetProcessById(workerId);
                    alive = process.StartTime.ToUniversalTime().Ticks == workerStart && !process.HasExited;
                }
                catch (ArgumentException) { }
                if (!alive)
                {
                    // A final atomic flush can race process exit; read once more before
                    // reporting interruption. Never infer successful installation from exit.
                    record = InstallerProgressJournal.Read(path, id, hash);
                    if (record is not null && record.Status != "Active") window.Report(record);
                    else window.FailObservation("The installer exited without a verified final progress record. Reopen CloudInlet to review its saved update result.");
                    break;
                }
                await Task.WhenAny(window.ClosedTask, Task.Delay(300));
            }
        }
        catch (Exception error) { window.FailObservation(error.Message); }
        await window.ClosedTask;
        runtimeLease?.Dispose();
    }
}

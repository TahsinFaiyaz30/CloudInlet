using CloudInlet.Core;
using CloudInlet.Core.Transfers;

namespace CloudInlet.ViewModels;

public readonly record struct ProgressDisplay(double Value, bool IsIndeterminate, string Label);

/// <summary>Shows only measured completion; discovery and verification have no invented total.</summary>
public static class ProgressPresentation
{
    public static ProgressDisplay ForSnapshot(SyncSnapshot snapshot) => snapshot.TransferTotalKnown
        ? ForBytes(snapshot.TransferredBytes, snapshot.TransferTotalBytes)
        : new(0, true, $"{FormatSize(snapshot.TransferredBytes)} transferred · {FormatSize(snapshot.TransferTotalBytes)} discovered so far");

    public static ProgressDisplay ForBytes(long completed, long total)
    {
        completed = Math.Max(0, completed);
        if (total <= 0)
            return new(0, true, completed > 0 ? $"{FormatSize(completed)} transferred" : "Waiting for transfer size");
        completed = Math.Min(completed, total);
        // Very large integer counters can round to 100 even before division; retain their integer completion test.
        var value = completed < total ? Math.Min(100d * completed / total, 99.9) : 100;
        return new(value, false, $"{FormatPercent(value)} · {FormatSize(completed)} of {FormatSize(total)}");
    }

    public static ProgressDisplay ForTransfer(TransferSnapshot transfer)
    {
        if (transfer.Phase is TransferPhase.Queued or TransferPhase.Hashing or TransferPhase.Verifying or TransferPhase.Retrying)
            return new(0, true, "");
        return ForBytes(transfer.Bytes, transfer.TotalBytes);
    }

    public static ProgressDisplay ForCloudJob(TransferJobSnapshot job)
    {
        // More files can arrive while discovery runs. A partial inventory is not the job's total.
        if (!job.DiscoveryComplete)
            return new(0, true, $"{FormatSize(job.TransferredBytes)} transferred · {FormatSize(job.TotalBytes)} discovered so far");
        if (job.TotalBytes > 0)
        {
            // Remaining excludes skipped files. Call this processed rather than transferred.
            var display = ForBytes(job.TotalBytes - job.RemainingBytes, job.TotalBytes);
            return display with { Label = display.Label + " processed" };
        }
        if (job.FileCount > 0)
        {
            var processed = Math.Clamp(job.CompletedFiles + job.SkippedFiles, 0, job.FileCount);
            var value = 100d * processed / job.FileCount;
            return new(value, false, $"{FormatPercent(value)} · {processed:N0} of {job.FileCount:N0} files processed");
        }
        return job.State == TransferJobState.Completed
            ? new(100, false, "100% · No files to transfer")
            : new(0, true, "No files discovered");
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes) * 1d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatPercent(double value)
    {
        // Rounding a still-incomplete transfer to 100% would imply completion too early.
        var displayed = value < 100 ? Math.Min(value, 99.9) : 100;
        return $"{displayed:0.#}%";
    }
}

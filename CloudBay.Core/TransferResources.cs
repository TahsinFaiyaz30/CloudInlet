namespace CloudBay.Core;

/// <summary>Shared across sync roots: disk checks cannot multiply with network worker counts.</summary>
public static class TransferResources
{
    public static SemaphoreSlim Hashing { get; } = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 2));
    public static SemaphoreSlim Verification { get; } = new(8);
    // A shared bound across cloud jobs and local endpoint jobs; each stream uses bounded RAM.
    public static SemaphoreSlim RelayTransfers { get; } = new(16);
    // Recovery metadata/readback prepares queued files independently of payload
    // admission so one slow reconciliation cannot idle every transfer worker.
    public static SemaphoreSlim RelayReconciliation { get; } = new(8);
    // Acquired after native downloads are registered as queued, before transport starts.
    // Sixteen 256KiB final-block buffers cost at most 4MiB across all connected roots.
    public static SemaphoreSlim NativeHydration { get; } = new(16);
    // Synchronous file reads can hold Hashing while cldflt calls VALIDATE_DATA. Native
    // validation must never wait on that gate; one shared worker avoids this lock cycle.
    public static SemaphoreSlim NativeValidation { get; } = new(1);
}

public sealed record TransferLimits(int Uploads, int Downloads)
{
    public static TransferLimits For(AppSettings settings) => For(settings, Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public static TransferLimits For(AppSettings settings, int processors, long availableMemory) => settings.UploadMode switch
    {
        UploadMode.Manual => new(settings.UploadConcurrency, settings.DownloadConcurrency),
        UploadMode.MaximumThroughput => new(16, 16),
        _ => new(Intelligent(processors, availableMemory), Intelligent(processors, availableMemory))
    };

    private static int Intelligent(int processors, long memory) =>
        memory > 0 && memory < 1_073_741_824 ? 2 : Math.Clamp(processors * 2, 4, 8);
}

namespace CloudInlet.Core;

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

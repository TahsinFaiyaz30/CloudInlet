namespace CloudInlet.Core;

public static class DownloadChunks
{
    public const long DownloadChunkSize = 8 * 1024 * 1024;
    public static long GetDownloadChunkSize(long fileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        var minimum = fileSize / 10_000 + (fileSize % 10_000 == 0 ? 0 : 1);
        const long alignment = 64 * 1024;
        return Math.Max(DownloadChunkSize, checked((minimum + alignment - 1) / alignment * alignment));
    }

}

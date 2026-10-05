using CloudBay.Core.B2;

namespace CloudBay.Core;

/// <summary>One directional byte budget shared by every provider and sync root in a client.</summary>
public sealed class TransferBandwidthBudget
{
    internal BandwidthLimiter Uploads { get; } = new();
    internal BandwidthLimiter Downloads { get; } = new();
    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uploadBytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(downloadBytesPerSecond);
        Uploads.Configure(uploadBytesPerSecond);
        Downloads.Configure(downloadBytesPerSecond);
    }
}

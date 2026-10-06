using CloudInlet.Core;
using CloudInlet.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class TransferSpeedTests
{
    [TestMethod]
    public void MeasuresPayloadSmoothsChangesAndExpiresAnIdleRate()
    {
        var clock = new ControlledClock();
        var meter = new TransferSpeedMeter(clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(0d, meter.Sample(100_000), "One short buffer sample must not create an unstable rate.");
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.AreEqual(2_000_000, meter.Sample(1_000_000), .01);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var smoothed = meter.Sample(1_500_000);
        Assert.IsTrue(smoothed > 1_000_000 && smoothed < 2_000_000, "Rates must reflect measured bytes while smoothing transient changes.");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsTrue(meter.BytesPerSecond > 0 && meter.BytesPerSecond < smoothed);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0d, meter.BytesPerSecond, "A stalled transfer must not keep reporting its old throughput.");
    }

    [TestMethod]
    public void ResumedBytesAreABaselineAndCounterRestartDoesNotInventTraffic()
    {
        var clock = new ControlledClock();
        var meter = new TransferSpeedMeter(clock);
        meter.Reset(8 * 1024 * 1024);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0d, meter.Sample(8 * 1024 * 1024));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2 * 1024 * 1024, meter.Sample(10 * 1024 * 1024), .01);
        Assert.AreEqual(0d, meter.Sample(0), "A retry's new byte counter must reset its measurement safely.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1024, meter.Sample(1024), .01);
    }

    [TestMethod]
    public void NewBurstAfterLongIdleExcludesIdleTimeAndPreviousThroughput()
    {
        var clock = new ControlledClock();
        var meter = new TransferSpeedMeter(clock);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(2_000_000d, meter.Sample(1_000_000), .01);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(0d, meter.BytesPerSecond);
        Assert.AreEqual(0d, meter.Sample(1_100_000), "The first fresh sample must begin a new stable measurement window.");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(400_000d, meter.Sample(1_200_000), .01,
            "Measure only the new burst, retaining its first payload sample and excluding two minutes of idle time.");
    }

    [TestMethod]
    public void AggregateRatesRequireWirePhasesButContinueAcrossTinyFileHandoffs()
    {
        var clock = new ControlledClock();
        var tracker = new TransferTracker("root", "Root", clock);
        tracker.Queue([("first.txt", ActivityKind.Upload, 1000), ("second.txt", ActivityKind.Upload, 1000),
            ("image.jpg", ActivityKind.Download, 1000)]);
        tracker.Phase("first.txt", ActivityKind.Upload, TransferPhase.Uploading);
        tracker.Progress("first.txt", ActivityKind.Upload, new(0, 1000) { IsBaseline = true });
        clock.Advance(TimeSpan.FromMilliseconds(500));
        tracker.Progress("first.txt", ActivityKind.Upload, new(1000, 1000));
        Assert.AreEqual(2000d, Snapshot().UploadBytesPerSecond, .01);
        tracker.Phase("first.txt", ActivityKind.Upload, TransferPhase.Verifying);
        tracker.Phase("second.txt", ActivityKind.Upload, TransferPhase.Hashing);
        Assert.AreEqual(0d, Snapshot().UploadBytesPerSecond,
            "Queued, hashing and verifying uploads are not transmitting bytes.");
        Assert.IsTrue(Snapshot().Transfers.All(row => row.BytesPerSecond == 0));
        tracker.Phase("second.txt", ActivityKind.Upload, TransferPhase.Uploading);
        tracker.Progress("second.txt", ActivityKind.Upload, new(0, 1000) { IsBaseline = true });
        Assert.AreEqual(2000d, Snapshot().UploadBytesPerSecond, .01,
            "A new file must retain the same direction's aggregate measurement instead of resetting after every tiny file.");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        tracker.Progress("second.txt", ActivityKind.Upload, new(1000, 1000));
        Assert.AreEqual(2000d, Snapshot().UploadBytesPerSecond, .01);
        tracker.Phase("second.txt", ActivityKind.Upload, TransferPhase.Verifying);
        tracker.Phase("image.jpg", ActivityKind.Download, TransferPhase.Downloading);
        tracker.Progress("image.jpg", ActivityKind.Download, new(0, 1000) { IsBaseline = true });
        clock.Advance(TimeSpan.FromMilliseconds(500));
        tracker.Progress("image.jpg", ActivityKind.Download, new(1000, 1000));
        Assert.AreEqual(2000d, Snapshot().DownloadBytesPerSecond, .01);
        tracker.Phase("image.jpg", ActivityKind.Download, TransferPhase.Verifying);
        Assert.AreEqual(0d, Snapshot().DownloadBytesPerSecond);

        SyncSnapshot Snapshot() => tracker.Apply(new(ClientState.Syncing, "Syncing"));
    }

    [TestMethod]
    public void RestoredAndRollbackBaselinesExcludeSavedBytesWhilePauseClearsWireRates()
    {
        var clock = new ControlledClock();
        var tracker = new TransferTracker("root", "Root", clock);
        tracker.Queue([("large.bin", ActivityKind.Upload, 10000)]);
        tracker.Phase("large.bin", ActivityKind.Upload, TransferPhase.Uploading);
        tracker.Progress("large.bin", ActivityKind.Upload, new(8000, 10000) { IsBaseline = true });
        clock.Advance(TimeSpan.FromMilliseconds(500));
        tracker.Progress("large.bin", ActivityKind.Upload, new(9000, 10000));
        Assert.AreEqual(2000d, Snapshot().UploadBytesPerSecond, .01,
            "Eight thousand bytes recovered from B2 are not new network traffic.");
        tracker.Progress("large.bin", ActivityKind.Upload, new(8000, 10000) { IsBaseline = true });
        Assert.AreEqual(0d, Snapshot().Transfers.Single().BytesPerSecond);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        tracker.Progress("large.bin", ActivityKind.Upload, new(9000, 10000));
        Assert.AreEqual(2000d, Snapshot().UploadBytesPerSecond, .01,
            "A retransmitted thousand bytes are real traffic; rollback itself contributes none.");
        tracker.Pause();
        tracker.Progress("large.bin", ActivityKind.Upload, new(10000, 10000));
        var paused = Snapshot();
        Assert.AreEqual(0d, paused.UploadBytesPerSecond);
        Assert.AreEqual(0d, paused.Transfers.Single().BytesPerSecond);
        Assert.AreEqual(TransferPhase.Paused, paused.Transfers.Single().Phase);
        Assert.AreEqual(0, paused.ActiveTransfers);

        SyncSnapshot Snapshot() => tracker.Apply(new(ClientState.Syncing, "Syncing"));
    }

    private sealed class ControlledClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
}

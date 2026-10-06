using CloudInlet.Core;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class TrayIconPresentationTests
{
    [DataTestMethod]
    [DataRow(ClientState.NotConnected, TrayIconVisualState.Disconnected)]
    [DataRow(ClientState.UpToDate, TrayIconVisualState.Idle)]
    [DataRow(ClientState.Connecting, TrayIconVisualState.Checking)]
    [DataRow(ClientState.Syncing, TrayIconVisualState.Checking)]
    [DataRow(ClientState.Paused, TrayIconVisualState.Paused)]
    [DataRow(ClientState.Offline, TrayIconVisualState.Offline)]
    [DataRow(ClientState.Attention, TrayIconVisualState.Attention)]
    public void PeriodicChecksAndIdleStatusesDoNotAnimate(ClientState state, TrayIconVisualState expected)
    {
        var snapshot = new SyncSnapshot(state, "Scanning") { QueuedTransfers = 42 };
        Assert.AreEqual(expected, TrayIconPresentation.VisualState(snapshot));
    }

    [TestMethod]
    public void TransfersAnimateEvenWhenAnotherRootNeedsAttention()
    {
        foreach (var state in new[] { ClientState.Syncing, ClientState.Attention, ClientState.Offline })
            Assert.AreEqual(TrayIconVisualState.Transferring,
                TrayIconPresentation.VisualState(new(state, "Working") { ActiveTransfers = 2 }));
        Assert.AreEqual(TrayIconVisualState.Paused,
            TrayIconPresentation.VisualState(new(ClientState.Paused, "Paused") { ActiveTransfers = 2 }));
    }

    [TestMethod]
    public void Version4RightClickAndKeyboardOpenTheMenuRatherThanActivity()
    {
        var context = (1L << 16) | 0x7b;
        Assert.AreEqual(TrayIconInteraction.ContextMenu, TrayIconPresentation.DecodeCallback(ulong.MaxValue, context, true, 1));
        Assert.AreEqual(TrayIconInteraction.None, TrayIconPresentation.DecodeCallback(1, (1L << 16) | 0x205, true, 1));
        Assert.AreEqual(TrayIconInteraction.None, TrayIconPresentation.DecodeCallback(1, (2L << 16) | 0x7b, true, 1));
    }

    [TestMethod]
    public void Version4AccessibleSelectionIsHandledOnce()
    {
        Assert.AreEqual(TrayIconInteraction.ShowActivity, TrayIconPresentation.DecodeCallback(0, (1L << 16) | 0x400, true, 1));
        Assert.AreEqual(TrayIconInteraction.ShowActivity, TrayIconPresentation.DecodeCallback(0, (1L << 16) | 0x401, true, 1));
        Assert.AreEqual(TrayIconInteraction.OpenApp, TrayIconPresentation.DecodeCallback(0, (1L << 16) | 0x203, true, 1));
        Assert.AreEqual(TrayIconInteraction.None, TrayIconPresentation.DecodeCallback(0, (1L << 16) | 0x202, true, 1));
        Assert.AreEqual(TrayIconInteraction.None, TrayIconPresentation.DecodeCallback(0, (1L << 16) | 0x406, true, 1));
    }

    [TestMethod]
    public void OlderShellCallbacksRemainUsableWhenVersionNegotiationFails()
    {
        Assert.AreEqual(TrayIconInteraction.ContextMenu, TrayIconPresentation.DecodeCallback(1, 0x205, false, 1));
        Assert.AreEqual(TrayIconInteraction.ContextMenu, TrayIconPresentation.DecodeCallback(1, 0x7b, false, 1));
        Assert.AreEqual(TrayIconInteraction.ShowActivity, TrayIconPresentation.DecodeCallback(1, 0x202, false, 1));
        Assert.AreEqual(TrayIconInteraction.OpenApp, TrayIconPresentation.DecodeCallback(1, 0x203, false, 1));
        Assert.AreEqual(TrayIconInteraction.None, TrayIconPresentation.DecodeCallback(2, 0x205, false, 1));
    }

    [TestMethod]
    public void TooltipFitsShellBufferWithoutBrokenSurrogatesOrMultilineText()
    {
        var full = TrayIconPresentation.Tooltip("Uploading\r\nDocuments");
        Assert.IsFalse(full.Contains('\r') || full.Contains('\n'));
        var longName = TrayIconPresentation.Tooltip(new string('x', 115) + "😀" + new string('x', 20));
        Assert.IsTrue(longName.Length <= 127);
        Assert.IsFalse(char.IsHighSurrogate(longName[^1]));
    }

    [TestMethod]
    public void CachedFramePixelsPreserveSourceAndWrapSmoothly()
    {
        var source = new byte[32 * 32 * 4];
        source[7] = 220;
        var original = (byte[])source.Clone();
        var frames = Enumerable.Range(0, TrayIconPresentation.FrameCount)
            .Select(frame => TrayIconPixels.WithBadge(source, 32, TrayIconVisualState.Transferring, frame)).ToArray();
        CollectionAssert.AreEqual(original, source);
        Assert.IsTrue(frames.Select(Convert.ToBase64String).Distinct().Count() >= 6);
        CollectionAssert.AreEqual(frames[0], TrayIconPixels.WithBadge(source, 32, TrayIconVisualState.Transferring, TrayIconPresentation.FrameCount));
        Assert.AreEqual(source[7], frames[0][7]);
        Assert.AreEqual(250u, TrayIconPresentation.FrameIntervalMilliseconds);
        foreach (var pixels in frames)
            for (var offset = 0; offset < pixels.Length; offset += 4)
                Assert.IsTrue(pixels[offset] <= pixels[offset + 3] && pixels[offset + 1] <= pixels[offset + 3] && pixels[offset + 2] <= pixels[offset + 3]);
    }

    [TestMethod]
    public void StatesHaveDistinctStaticBadgesAndDisconnectedKeepsOriginal()
    {
        var source = new byte[16 * 16 * 4];
        var states = Enum.GetValues<TrayIconVisualState>();
        var pixels = states.Select(state => TrayIconPixels.WithBadge(source, 16, state, 0)).ToArray();
        CollectionAssert.AreEqual(source, pixels[Array.IndexOf(states, TrayIconVisualState.Disconnected)]);
        // Checking is the same recognizable sync badge, held still.
        Assert.AreEqual(states.Length - 1, pixels.Select(Convert.ToBase64String).Distinct().Count());
        Assert.ThrowsException<ArgumentException>(() => TrayIconPixels.WithBadge([], 32, TrayIconVisualState.Idle, 0));
    }
}

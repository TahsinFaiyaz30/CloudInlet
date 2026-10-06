using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CloudInlet.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TrayIconImagesNativeTests
{
    [TestMethod]
    public void CacheReusesFramesAndReleasesEveryOwnedGdiAndIconHandle()
    {
        // System stock icon is shared, so it is not owned/destroyed by this
        // fixture. The tested cache owns every generated badge/frame.
        var source = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        Assert.AreNotEqual(IntPtr.Zero, source);
        using var process = Process.GetCurrentProcess();
        using (var warmup = new TrayIconImages(source, 32)) Assert.AreNotEqual(IntPtr.Zero, warmup.Get(TrayIconVisualState.Idle, 0));
        var beforeGdi = GetGuiResources(process.Handle, 0);
        var beforeUser = GetGuiResources(process.Handle, 1);
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var cache = new TrayIconImages(source, cycle % 2 == 0 ? 32 : 64);
            try
            {
                var residentIcons = new HashSet<IntPtr>();
                foreach (var state in Enum.GetValues<TrayIconVisualState>())
                {
                    var first = cache.Get(state, 0);
                    residentIcons.Add(first);
                    Assert.AreNotEqual(IntPtr.Zero, first);
                    for (var iteration = 0; iteration < 120; iteration++)
                        Assert.AreEqual(first, cache.Get(state, iteration * TrayIconPresentation.FrameCount));
                }
                var unique = Enumerable.Range(0, TrayIconPresentation.FrameCount)
                    .Select(frame => cache.Get(TrayIconVisualState.Transferring, frame)).Distinct().ToArray();
                Assert.AreEqual(TrayIconPresentation.FrameCount, unique.Length);
                residentIcons.UnionWith(unique);
                Assert.IsTrue(residentIcons.Count <= 24, "The resident icon cache must remain bounded.");
                // CreateIconIndirect copies the color/mask bitmaps into the
                // resulting icon; those icon-owned resources remain counted
                // until DestroyIcon. Bound the live cache rather than assume
                // it consumes no GDI objects. Four GDI resources per icon is
                // the acceptance budget, not a Windows internal-layout claim.
                var liveGdi = GetGuiResources(process.Handle, 0);
                Assert.IsTrue(liveGdi <= beforeGdi + residentIcons.Count * 4,
                    "Resident GDI resources must fit the bounded icon cache, including icon-owned bitmap copies.");
                Assert.IsTrue(GetGuiResources(process.Handle, 1) <= beforeUser + residentIcons.Count,
                    "The resident USER resources must fit the bounded icon cache.");
            }
            finally { cache.Dispose(); cache.Dispose(); }
            Assert.AreEqual(beforeGdi, GetGuiResources(process.Handle, 0), "No GDI bitmap may survive cache disposal.");
            Assert.AreEqual(beforeUser, GetGuiResources(process.Handle, 1), "Every generated HICON must be destroyed.");
        }
    }

    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}

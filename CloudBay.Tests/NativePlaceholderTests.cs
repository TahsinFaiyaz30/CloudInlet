using CloudBay.Windows.CloudFiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativePlaceholderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(120_000)]
    public async Task NativeCloudFilesHydrationPinningConflictsAndDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var checks = await NativePlaceholderSmokeTest.RunAsync(timeout.Token);
        foreach (var check in checks) TestContext.WriteLine("PASS " + check);
        Assert.AreEqual(13, checks.Count, "Every native smoke scenario must execute.");
    }
}

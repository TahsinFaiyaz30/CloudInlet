using System.Diagnostics;
using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ExeMigrationReceiptSnapshotTests
{
    [TestMethod]
    [TestCategory("Native")]
    public async Task NativeReceiptSnapshotSurvivesShortExclusiveWriteWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudInlet-receipt-read-" + Guid.NewGuid().ToString("N"));
        byte[] expected = [1, 2, 3, 4];
        File.WriteAllBytes(path, expected);
        var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150); writer.Dispose(); });
        try
        {
            var actual = ExeMigrationReceipt.ReadSnapshot(path, 1024);
            CollectionAssert.AreEqual(expected, actual);
        }
        finally { await release; writer.Dispose(); File.Delete(path); }
    }

    [TestMethod]
    [TestCategory("Native")]
    public void NativeReceiptSnapshotDoesNotWaitForeverOnPersistentWriter()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudInlet-receipt-read-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, [1]);
        try
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var watch = Stopwatch.StartNew();
            var error = Assert.ThrowsException<IOException>(() => ExeMigrationReceipt.ReadSnapshot(path, 1024));
            Assert.AreEqual(32, error.HResult & 0xffff);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), "A persistent sharing conflict must remain bounded.");
        }
        finally { File.Delete(path); }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void ReceiptSnapshotStillRejectsInvalidSizes(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudInlet-receipt-read-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[size]);
        try { Assert.ThrowsException<IOException>(() => ExeMigrationReceipt.ReadSnapshot(path, 4)); }
        finally { File.Delete(path); }
    }
}

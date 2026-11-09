using System.Security.Cryptography;
using CloudInlet.Packaging;
using CloudInlet.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class InstallationRecoveryPollingTests
{
    [TestMethod]
    [TestCategory("Native")]
    public async Task NativeObserverCanReadAgainAfterSharingOutlastsSnapshotBudget()
    {
        var path = Path.Combine(Path.GetTempPath(), "CloudInlet-polling-" + Guid.NewGuid().ToString("N"));
        byte[] expected = [1, 2, 3, 4];
        File.WriteAllBytes(path, expected);
        try
        {
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.IsNull(await InstallationRecoveryPolling.ReadAsync(() => ExeMigrationReceipt.ReadSnapshot(path, 1024)));
            CollectionAssert.AreEqual(expected, await InstallationRecoveryPolling.ReadAsync(() => ExeMigrationReceipt.ReadSnapshot(path, 1024)));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task AuthenticationAndIdentityFailuresRemainTerminal()
    {
        await Assert.ThrowsExceptionAsync<CryptographicException>(() => InstallationRecoveryPolling.ReadAsync<byte[]>(() => throw new CryptographicException("Invalid protected record")));
        await Assert.ThrowsExceptionAsync<IOException>(() => InstallationRecoveryPolling.ReadAsync<byte[]>(() => throw new IOException("Identity moved backwards")));
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => InstallationRecoveryPolling.ReadAsync<byte[]>(() => throw new UnauthorizedAccessException()));
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => InstallationRecoveryPolling.ReadAsync<byte[]>(() => throw new FileNotFoundException()));
    }
}

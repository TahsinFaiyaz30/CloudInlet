using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class InstallerProgressHandoffTests
{
    [TestMethod]
    public void NativeHandoffRequiresExplicitCommitAndCancellationCannotBeReused()
    {
        WithFixture((path, id, hash) =>
        {
            Assert.IsNull(InstallerProgressHandoff.ReadState(path, id, hash));
            Assert.ThrowsException<IOException>(() => InstallerProgressHandoff.Commit(path, id, hash));
            InstallerProgressHandoff.Create(path, id, hash);
            Assert.AreEqual("Pending", InstallerProgressHandoff.ReadState(path, id, hash));
            Assert.ThrowsException<IOException>(() => InstallerProgressHandoff.Create(path, id, hash));
            InstallerProgressHandoff.Commit(path, id, hash);
            Assert.AreEqual("Committed", InstallerProgressHandoff.ReadState(path, id, hash));
            InstallerProgressHandoff.Cancel(path, id, hash);
            Assert.AreEqual("Canceled", InstallerProgressHandoff.ReadState(path, id, hash));
            InstallerProgressHandoff.Cancel(path, id, hash);
            Assert.ThrowsException<IOException>(() => InstallerProgressHandoff.Commit(path, id, hash));
        });
    }

    [TestMethod]
    public void NativeHandoffCannotAcceptAnotherRequestOrChangedProtectedDecision()
    {
        WithFixture((path, id, hash) =>
        {
            InstallerProgressHandoff.Create(path, id, hash);
            Assert.ThrowsException<InvalidDataException>(() => InstallerProgressHandoff.ReadState(path, id, new string('0', 64)));
            var record = path + ".handoff.dpapi";
            var bytes = File.ReadAllBytes(record); bytes[bytes.Length / 2] ^= 0x40; File.WriteAllBytes(record, bytes);
            Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() => InstallerProgressHandoff.Commit(path, id, hash));
        });
    }

    private static void WithFixture(Action<string, string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudInlet-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "host"));
        try
        {
            var id = Guid.NewGuid().ToString("N");
            action(Path.Combine(directory, "host", id + ".progress.dpapi"), id, InstallerProgressJournal.RequestHash("fixture"u8.ToArray()));
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(directory).StartsWith("CloudInlet-handoff-", StringComparison.Ordinal)) throw new IOException("Unsafe test cleanup path.");
            Directory.Delete(directory, true);
        }
    }
}

using System.Runtime.Serialization.Json;
using System.Text;
using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class WindowsBootIdentityTests
{
    private const string Before = "2026-10-19T12:11:16.7060423Z";
    private const string After = "2026-10-19T12:48:18.7602209Z";
    private static string Event(string start = Before, string record = "877") =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>" +
        "<Provider Name='Microsoft-Windows-Kernel-General' Guid='{a68ca8b7-004f-d7b6-a698-07e2de0f1f5d}'/>" +
        "<EventID>12</EventID><Channel>System</Channel><EventRecordID>" + record + "</EventRecordID>" +
        "<Computer>DisposableLab</Computer><TimeCreated SystemTime='2026-10-19T12:11:18Z'/></System>" +
        "<EventData><Data Name='StartTime'>" + start + "</Data></EventData></Event>";

    [TestMethod]
    public void DifferentKernelStartsProduceDifferentIdentities()
    {
        var before = InstallerMigrationRecovery.BootIdentityFromEventXml(Event());
        var after = InstallerMigrationRecovery.BootIdentityFromEventXml(Event(After, "983"));
        Assert.AreNotEqual(before, after);
        var attempt = new ExeNativeAttempt { Kind = "Install", ExitCode = 3010, BootIdentity = before };
        Assert.IsTrue(attempt.RequiresRestart(before));
        Assert.IsFalse(attempt.RequiresRestart(after));
    }

    [TestMethod]
    public void LogRecordChangesCannotManufactureAReboot()
    {
        var before = InstallerMigrationRecovery.BootIdentityFromEventXml(Event());
        Assert.AreEqual(before, InstallerMigrationRecovery.BootIdentityFromEventXml(Event(record: "1")));
        Assert.AreEqual(before, InstallerMigrationRecovery.BootIdentityFromEventXml(Event().Replace("2026-10-19T12:11:18Z", "2030-01-01T00:00:00Z")));
    }

    [DataTestMethod]
    [DataRow("Microsoft-Windows-Kernel-General", "Another-Provider")]
    [DataRow("a68ca8b7-004f-d7b6-a698-07e2de0f1f5d", "a68ca8b7-004f-d7b6-a698-07e2de0f1f5e")]
    [DataRow("<EventID>12</EventID>", "<EventID>13</EventID>")]
    [DataRow("<Channel>System</Channel>", "<Channel>Application</Channel>")]
    [DataRow("<EventRecordID>877</EventRecordID>", "<EventRecordID>0</EventRecordID>")]
    [DataRow("<Computer>DisposableLab</Computer>", "<Computer></Computer>")]
    [DataRow("Name='StartTime'", "Name='UnrelatedTime'")]
    [DataRow(Before, "invalid")]
    [DataRow(Before, "1990-01-01T00:00:00Z")]
    [DataRow("<EventID>12</EventID>", "<EventID>12</EventID><EventID>12</EventID>")]
    public void MissingOrUnrelatedStartupEvidenceIsRejected(string from, string to) =>
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.BootIdentityFromEventXml(Event().Replace(from, to)));

    [TestMethod]
    public void OversizedXmlAndDtdAreRejected()
    {
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.BootIdentityFromEventXml(new string('x', 33 * 1024)));
        Assert.ThrowsException<IOException>(() => InstallerMigrationRecovery.BootIdentityFromEventXml("<!DOCTYPE Event [<!ENTITY a 'ignored'>]>" + Event()));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow(3010)]
    public void OldNativeAttemptCannotTreatAnAlgorithmChangeAsAReboot(int? exitCode)
    {
        // Missing DataMember values deserialize as zero; they must never inherit
        // the new initializer and silently release an old pending barrier.
        var json = "{\"Kind\":\"Install\",\"BootIdentity\":\"11111111-1111-1111-1111-111111111111\",\"ExitCode\":" +
                   (exitCode?.ToString() ?? "null") + "}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var attempt = (ExeNativeAttempt)new DataContractJsonSerializer(typeof(ExeNativeAttempt)).ReadObject(stream)!;
        Assert.AreEqual(0, attempt.BootIdentityVersion);
        attempt.Validate();
        Assert.ThrowsException<IOException>(() => attempt.RequiresRestart(InstallerMigrationRecovery.BootIdentityFromEventXml(Event())));
        Assert.AreEqual(0, attempt.BootIdentityVersion);
        Assert.AreEqual("11111111-1111-1111-1111-111111111111", attempt.BootIdentity);
    }
}

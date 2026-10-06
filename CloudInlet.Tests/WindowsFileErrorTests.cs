using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class WindowsFileErrorTests
{
    [DataTestMethod]
    [DataRow(5, "permissions")]
    [DataRow(32, "using this file")]
    [DataRow(33, "using this file")]
    [DataRow(112, "free space")]
    [DataRow(395, "permissions")]
    [DataRow(362, "cloud app")]
    [DataRow(396, "hard-linked")]
    public void WindowsFailuresHaveUsefulRecoveryInstructions(int code, string expected)
    {
        var error = new System.ComponentModel.Win32Exception(code);
        StringAssert.Contains(FileSystemError.Describe(error), expected);
    }
    [TestMethod]
    public void ContextOfVerifiedCloudCopySurvivesNativeErrorExplanation()
    {
        var error = new IOException("The verified B2 copy is retained.", new UnauthorizedAccessException());
        StringAssert.Contains(FileSystemError.Describe(error), "verified B2 copy");
        StringAssert.Contains(FileSystemError.Describe(error), "permissions");
    }
    [TestMethod]
    public void RepeatedFileErrorsDoNotBuryOtherActivityAndRecoveryResetsSuppression()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudInlet-ActivityTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ClientStorage(directory);
            var time = DateTimeOffset.UtcNow;
            var failure = new ActivityEvent(time, ActivityKind.Error, "Documents/a.bin", "Denied");
            Assert.IsTrue(storage.Log(failure));
            Assert.IsFalse(storage.Log(failure with { Time = time.AddMinutes(1) }));
            var reopened = new ClientStorage(directory);
            Assert.IsFalse(reopened.Log(failure with { Time = time.AddMinutes(2) }), "Restart should not immediately duplicate the same problem.");
            Assert.IsTrue(reopened.Log(failure with { Time = time.AddMinutes(5) }), "An ongoing failure still needs periodic visibility.");
            Assert.IsTrue(reopened.Log(new(time.AddMinutes(6), ActivityKind.Information, failure.Path, "Windows sync status updated")));
            Assert.IsTrue(reopened.Log(failure with { Time = time.AddMinutes(7) }), "A failure after recovery is a new event.");
            Assert.AreEqual(4, reopened.Activity.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

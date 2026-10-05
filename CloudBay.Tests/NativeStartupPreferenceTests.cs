using System.Text.Json;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace CloudBay.Tests;

[TestClass]
public sealed class NativeStartupPreferenceTests
{
    [TestMethod]
    public async Task AppStartupReadsWindowsChoiceWithoutRewritingRegistrationOrOtherPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudBayStartupRead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var before = ReadRegistration();
        var actual = await SystemIntegration.IsStartupEnabledAsync();
        try
        {
            var storage = new ClientStorage(directory);
            var original = new AppSettings { RootPath = Path.Combine(directory, "Files"), StartAtSignIn = !actual,
                Theme = "Dark", UploadConcurrency = 3, Prefix = "preserved-prefix/" };
            storage.SaveSettings(original);
            await using (var controller = new ClientController(storage))
            {
                await controller.StartAsync();
                Assert.AreEqual(actual, controller.Settings.StartAtSignIn);
                Assert.AreEqual(JsonSerializer.Serialize(original with { StartAtSignIn = actual }), JsonSerializer.Serialize(controller.Settings));
            }
            Assert.AreEqual(before, ReadRegistration(), "Opening the app must not override installer or Windows startup choices.");
            Assert.AreEqual(actual, storage.LoadSettings().StartAtSignIn);
        }
        finally
        {
            var absolute = Path.GetFullPath(directory);
            if (!absolute.StartsWith(Path.Combine(Path.GetTempPath(), "CloudBayStartupRead-"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid startup test cleanup path.");
            Directory.Delete(absolute, true);
        }
    }

    private static string ReadRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return JsonSerializer.Serialize(key?.GetValue(BuildInfo.StartupRegistryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }
}

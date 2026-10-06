using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.OneDrive;
using CloudInlet.Core.Transfers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class RebrandCompatibilityTests
{
    [TestMethod]
    public void PublishedCredentialEntropyAndSavedBackupNamespaceRemainReadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudInlet-Rebrand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var b2 = new B2Credentials("fixture-id", "fixture-key");
            var account = new ClientStorage.OneDriveConnection("fixture-account", "Personal",
                OneDriveSignInConfiguration.DefaultClientId, "consumers",
                new("fixture-access", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1), "Files.ReadWrite"));
            // Construct records with the published encryption contract, independently of new Save methods.
            var b2Bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(b2), "CloudBay.B2.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            var oneDriveBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new[] { account }), "CloudBay.OneDrive.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(directory, "credentials.dpapi"), b2Bytes);
            File.WriteAllBytes(Path.Combine(directory, "onedrive.dpapi"), oneDriveBytes);
            var settings = new AppSettings { KeyId = b2.KeyId, BucketId = "bucket", RootPath = Path.Combine(directory, "CloudBay"),
                Prefix = "CloudBay/", CustomBackups = [new("Projects", Path.Combine(directory, "Projects"), "CloudBay/.cloudbay-backups/Projects/")] };
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings));
            var storage = new ClientStorage(directory);
            Assert.AreEqual(b2, storage.LoadCredentials());
            Assert.AreEqual(account, storage.LoadOneDriveConnections().Single());
            Assert.AreEqual(settings.RootPath, storage.LoadSettings().RootPath);
            Assert.AreEqual(settings.Prefix, storage.LoadSettings().Prefix);
            Assert.AreEqual(settings.CustomBackups[0], storage.LoadSettings().CustomBackups[0]);
            CollectionAssert.AreEqual(b2Bytes, File.ReadAllBytes(Path.Combine(directory, "credentials.dpapi")));
            CollectionAssert.AreEqual(oneDriveBytes, File.ReadAllBytes(Path.Combine(directory, "onedrive.dpapi")));
            Assert.AreEqual("CloudInlet/", new AppSettings().Prefix);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void NewEnvironmentNamesOverrideLegacyConfigurationWithoutChangingTheAppRegistration()
    {
        var values = new Dictionary<string, string?>
        {
            [OneDriveSignInConfiguration.LegacyClientIdEnvironmentVariable] = "aabcad61-318b-400d-9804-e8c129173570",
            [OneDriveSignInConfiguration.LegacyTenantEnvironmentVariable] = "organizations"
        };
        var method = typeof(OneDriveSignInConfiguration).GetMethod("FromEnvironment", BindingFlags.NonPublic | BindingFlags.Static)!;
        OneDriveSignInOptions Read() => (OneDriveSignInOptions)method.Invoke(null, [new Func<string, string?>(name => values.GetValueOrDefault(name)), false])!;
        Assert.AreEqual("organizations", Read().Tenant);
        Assert.AreEqual(values[OneDriveSignInConfiguration.LegacyClientIdEnvironmentVariable], Read().ClientId);
        values[OneDriveSignInConfiguration.ClientIdEnvironmentVariable] = OneDriveSignInConfiguration.DefaultClientId;
        values[OneDriveSignInConfiguration.TenantEnvironmentVariable] = "consumers";
        Assert.AreEqual(OneDriveSignInConfiguration.DefaultClientId, Read().ClientId);
        Assert.AreEqual("consumers", Read().Tenant);
    }

    [TestMethod]
    public void SavedKeepBothReceiptsAcceptBothExactBrandsAndRejectUnrelatedTargets()
    {
        var type = typeof(TransferUploadRequest).Assembly.GetType("CloudInlet.Core.Transfers.TransferConflictNames")!;
        var rename = type.GetMethod("RenamePath", BindingFlags.Public | BindingFlags.Static)!;
        var allowed = type.GetMethod("IsAllowedTarget", BindingFlags.Public | BindingFlags.Static)!;
        var request = new TransferUploadRequest(new string('a', 64), "Folder/file.txt", TransferConflictPolicy.Rename);
        var current = (string)rename.Invoke(null, [request.RelativePath, request.OperationId])!;
        StringAssert.Contains(current, "(CloudInlet ");
        bool Accept(string path) => (bool)allowed.Invoke(null, [request, path, "onedrive"])!;
        Assert.IsTrue(Accept(current));
        Assert.IsTrue(Accept(current.Replace("CloudInlet", "CloudBay", StringComparison.Ordinal)));
        Assert.IsFalse(Accept("Folder/file (CloudBay unrelated).txt"));
        Assert.IsFalse(Accept("Other/file (CloudBay aaaaaaaaaaaa).txt"));
    }
}

using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace CloudInlet.Packaging;

// Read the final 1.2.2 receipts without running any conversion or recovery commands.
internal static class PreviousInstallation
{
    internal const string FinishMessage = "Open CloudInlet 1.2.2 and finish its pending conversion before upgrading. Your existing files and accounts are preserved.";
    internal const string VersionMessage = "Install and open CloudInlet 1.2.2 before upgrading this installation. Download the matching installer from https://github.com/TahsinFaiyaz30/CloudInlet/releases/tag/v1.2.2.";
    internal static void RequireSupported(string version)
    {
        if (!Version.TryParse(version, out var previous) || previous < new Version(1, 2, 2)) throw new IOException(VersionMessage);
    }

    [DataContract] private sealed class MsiRecord
    {
        [DataMember(IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(IsRequired = true)] public string BuildFlavor { get; set; } = "";
        [DataMember(IsRequired = true)] public string State { get; set; } = "";
    }
    [DataContract] private sealed class ExeRecord
    {
        [DataMember(IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(IsRequired = true)] public string Flavor { get; set; } = "";
        [DataMember(IsRequired = true)] public string Id { get; set; } = "";
        [DataMember(IsRequired = true)] public ExeState State { get; set; } = null!;
    }
    [DataContract] private sealed class ExeState
    {
        [DataMember(IsRequired = true)] public string Phase { get; set; } = "";
    }

    internal static void RequireFinished(string home, string flavor)
    {
        if (flavor != "Release" && flavor != "Debug") throw new IOException("Unknown build flavor.");
        var root = Path.Combine(home, "CloudInlet", "Migration", flavor);
        var review = Path.Combine(root, "State", "pending-review.dpapi");
        UpdateCacheLocation.EnsureNormalPath(review, false);
        if (File.Exists(review)) throw new IOException(FinishMessage);
        var msi = Path.Combine(root, "InstallerRecovery", "installation.dpapi");
        UpdateCacheLocation.EnsureNormalPath(msi, false);
        if (File.Exists(msi))
        {
            var record = Open<MsiRecord>(msi, "CloudInlet.InstallationMigrationRecovery.v1");
            if (record.SchemaVersion != 1 || record.BuildFlavor != flavor || record.State is not ("Complete" or "RolledBack"))
                throw new IOException(FinishMessage);
        }
        var exes = Path.Combine(root, "ExeInstallation");
        UpdateCacheLocation.EnsureNormalPath(exes, true);
        if (!Directory.Exists(exes)) return;
        foreach (var folder in Directory.EnumerateFileSystemEntries(exes))
        {
            var id = Path.GetFileName(folder);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            var receipt = Path.Combine(folder, "migration.dpapi");
            UpdateCacheLocation.EnsureNormalPath(receipt, false);
            var record = Open<ExeRecord>(receipt, "CloudInlet.ExeInstallationMigration.v1");
            if (record.SchemaVersion != 1 || record.Flavor != flavor || record.Id != id || record.State?.Phase is not ("Completed" or "Reverted"))
                throw new IOException(FinishMessage);
        }
    }

    private static T Open<T>(string path, string entropy) where T : class
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= 0 || file.Length > 8 * 1024 * 1024) throw new IOException(FinishMessage);
        using var reader = new BinaryReader(file);
        var bytes = ProtectedData.Unprotect(reader.ReadBytes((int)file.Length), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser);
        try
        {
            using var input = new MemoryStream(bytes, false);
            return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(input) ?? throw new IOException(FinishMessage));
        }
        finally { Array.Clear(bytes, 0, bytes.Length); }
    }
}

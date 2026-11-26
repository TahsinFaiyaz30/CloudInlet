using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using CloudInlet.Core;
using CloudInlet.Core.Updates;
using CloudInlet.Packaging;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace CloudInlet.Windows;

internal static class UpgradeBaseline
{
    // MSIX can skip versions. On the first open of an existing profile, verify a
    // completed per-user deployment instead of guessing from its settings schema.
    internal static void Require()
    {
        var installation = UpdateInstallation.Load();
        if (installation.CanInstall) return; // Its versioned EXE/MSI installer already checked the baseline.
        if (installation.Identity.InstallerKind == UpdateInstallerKind.Store)
        {
            var package = Package.Current;
            if (!StoreStateRoutingPolicy.SupportsRouting(Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml")))
                throw new IOException("This package cannot access the existing shared CloudInlet profile. Install the official Store package.");
            var id = package.Id;
            var baseline = $"{id.Name}_1.2.2.0_{id.Architecture.ToString().ToLowerInvariant()}_{id.ResourceId}_{id.PublisherId}";
            using var user = WindowsIdentity.GetCurrent();
            if (user.User is { } sid && WasRegistered(baseline, sid.Value)) return;
        }
        else if (BuildInfo.Flavor == "Release")
        {
            using var portable = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudInlet");
            if (portable?.GetValue("InstallLocation") is string root && portable.GetValue("DisplayVersion") is string version &&
                version == BuildInfo.Version && string.Equals(Path.Combine(root, version),
                    Path.TrimEndingDirectorySeparator(installation.Directory), StringComparison.OrdinalIgnoreCase))
                return; // Install.ps1 checked the registered predecessor before replacing it.
        }
        // A user with expired deployment history can run the published 1.2.2
        // desktop installer in this account and then reopen the Store edition.
        foreach (var kind in new[] { "Exe", "Msi" })
        {
            var installed = DistributionRegistration.Read(BuildInfo.Flavor, kind, (path, name) =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                return key?.GetValue(name) as string;
            });
            if (installed?.Version != "1.2.2") continue;
            var image = Path.Combine(installed.Directory, "CloudInlet.exe");
            UpdateCacheLocation.EnsureNormalPath(image, false);
            if (File.Exists(image) && FileVersionInfo.GetVersionInfo(image).ProductVersion?.Split('+')[0] == installed.Version)
                return;
        }
        throw new IOException("Windows could not confirm CloudInlet 1.2.2 for this existing profile. Close CloudInlet, " +
            "install and open the 1.2.2 desktop EXE or MSI under this Windows account, then reopen this edition. Your profile is preserved. " +
            "https://github.com/TahsinFaiyaz30/CloudInlet/releases/tag/v1.2.2");
    }

    internal static bool WasRegistered(string packageFullName, string userSid)
    {
        const string channel = "Microsoft-Windows-AppXDeploymentServer/Operational";
        if (packageFullName.Contains('\'') || userSid.Contains('\'')) return false;
        // 607 starts the named user's deployment. 400 confirms a successful
        // operation in the same activity. Add (1) and Register (6) install for a user.
        var operation = "Data[@Name='DeploymentOperation']='1' or Data[@Name='DeploymentOperation']='6'";
        var package = $"Data[@Name='PackageFullName']='{packageFullName}'";
        var query = $"*[System[EventID=607] and EventData[{package} and Data[@Name='UserSid']='{userSid}' and ({operation})]]";
        try
        {
            using var starts = new EventLogReader(new EventLogQuery(channel, PathType.LogName, query) { ReverseDirection = true });
            while (true)
            {
                using var start = starts.ReadEvent();
                if (start is null) return false;
                if (start.ActivityId is not { } activity) continue;
                var success = $"*[System[EventID=400 and Correlation[@ActivityID='{activity:B}']] and EventData[{package} and ({operation})]]";
                using var completions = new EventLogReader(new EventLogQuery(channel, PathType.LogName, success));
                using var completed = completions.ReadEvent();
                if (completed is not null) return true;
            }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

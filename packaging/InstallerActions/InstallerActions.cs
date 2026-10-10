using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;
using FileAttributes = System.IO.FileAttributes;

namespace CloudInlet.Packaging
{
    // These actions run as the installing user, never elevated. Paths are data;
    // no PowerShell, command interpreter, taskkill, or process-name termination.
    public static class InstallerActions
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersion
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersion version);

        [CustomAction]
        public static ActionResult CheckWindowsVersion(Session session) => Guard(session, () =>
        {
            // MSI's compatibility WindowsBuild property reports 9600 even on
            // Windows 11. Query the native OS version before LaunchConditions.
            var version = new OsVersion { Size = Marshal.SizeOf(typeof(OsVersion)), ServicePack = "" };
            if (RtlGetVersion(ref version) != 0) throw new IOException("Windows version could not be determined.");
            session["CB_WINDOWS_BUILD"] = version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ActionResult.Success;
        });

        [CustomAction]
        public static ActionResult PrepareInstall(Session session)
        {
            return Guard(session, () =>
            {
                var flavor = Flavor(session);
                if (session["REMOVE"].Split(',').Contains("ALL", StringComparer.OrdinalIgnoreCase))
                {
                    using (var installed = UserKey(session, Key(flavor, "Msi")))
                    {
                        if (installed?.GetValue("InstallDirectory") is string directory)
                        {
                            session["INSTALLDIR"] = ValidateDirectory(directory);
                        }
                    }
                    return ActionResult.Success;
                }
                using (var other = UserKey(session, Key(flavor, "Exe")))
                    if (other?.GetValue("InstallDirectory") is string)
                        throw new InvalidOperationException("CloudInlet is installed using the EXE installer. Uninstall that installer before changing to MSI. Your backup settings and files will be preserved.");

                using (var previous = UserKey(session, Key(flavor, "Msi")))
                {
                    var previousDirectory = previous?.GetValue("InstallDirectory") as string;
                    session["CB_PREVIOUS_DIRECTORY"] = "";
                    if (session["UPDATE"] == "1" && string.IsNullOrEmpty(previousDirectory))
                        throw new InvalidOperationException("This update requires an existing MSI installation of the same CloudInlet build.");
                    if (string.IsNullOrEmpty(session["INSTALLDIR"]))
                        session["INSTALLDIR"] = string.IsNullOrEmpty(previousDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName(flavor)) : previousDirectory;
                    if (!string.IsNullOrEmpty(previousDirectory))
                    {
                        previousDirectory = ValidateDirectory(previousDirectory!);
                        session["CB_PREVIOUS_DIRECTORY"] = previousDirectory;
                        // An installed MSI owns its location. Repairs retain it, and
                        // upgrades retain custom names rather than moving user data.
                        session["INSTALLDIR"] = previousDirectory;
                        using (var run = UserKey(session, @"Software\Microsoft\Windows\CurrentVersion\Run"))
                            session["CB_CURRENT_STARTUP"] = run?.GetValue(RunName(flavor)) is string ? "1" : "0";
                        session["CB_CURRENT_DESKTOP"] = File.Exists(DesktopShortcut(flavor)) ? "1" : "0";
                        session["CB_PREVIOUS_INSTALL"] = "1";
                        if (session["UPDATE"] == "1")
                        {
                            session["Preselected"] = "1";
                            session["ADDLOCAL"] = "Main" + (session["CB_CURRENT_STARTUP"] == "1" ? ",Startup" : "") + (session["CB_CURRENT_DESKTOP"] == "1" ? ",DesktopShortcut" : "");
                            session["REMOVE"] = string.Join(",", new[] { session["CB_CURRENT_STARTUP"] == "1" ? "" : "Startup", session["CB_CURRENT_DESKTOP"] == "1" ? "" : "DesktopShortcut" }.Where(x => x.Length > 0));
                        }
                    }
                }
                return ActionResult.Success;
            });
        }

        [CustomAction]
        public static ActionResult ValidateAndStop(Session session)
        {
            return Guard(session, () =>
            {
                var directory = ValidateDirectory(session["INSTALLDIR"]);
                var flavor = Flavor(session);
                var previousDirectory = session["CB_PREVIOUS_DIRECTORY"];
                var source = string.IsNullOrEmpty(previousDirectory) ? directory : ValidateDirectory(previousDirectory);
                if (session["UPDATE"] == "1" || (!string.IsNullOrEmpty(previousDirectory) && string.IsNullOrEmpty(session["Installed"])))
                {
                    ValidateInstalledSource(source, flavor);
                }
                AssertNormalTree(source);
                InstallerClientStop.Stop(flavor, source);
                var data = new CustomActionData();
                data.Add("Directory", directory);
                data.Add("Version", session["ProductVersion"]);
                data.Add("Flavor", flavor);
                data.Add("Revision", session["CB_REVISION"]);
                data.Add("Startup", WillInstall(session, "Startup") ? "1" : "0");
                data.Add("Desktop", WillInstall(session, "DesktopShortcut") ? "1" : "0");
                session["WriteDistribution"] = data.ToString();
                return ActionResult.Success;
            });
        }

        [CustomAction]
        public static ActionResult StopBeforeUninstall(Session session) => Guard(session, () =>
        {
            InstallerClientStop.Stop(Flavor(session), ValidateDirectory(session["INSTALLDIR"]), removeNotifications: true);
            return ActionResult.Success;
        });

        [CustomAction]
        public static ActionResult WriteDistribution(Session session)
        {
            return Guard(session, () =>
            {
                var data = session.CustomActionData;
                var directory = ValidateDirectory(data["Directory"]);
                var descriptor = new Distribution
                {
                    SchemaVersion = 1, Version = data["Version"], BuildFlavor = data["Flavor"],
                    InstallerKind = "Msi", Architecture = "x64", InstallScope = "perUser", InstallDirectory = directory,
                    StartWithWindows = data["Startup"] == "1", DesktopShortcut = data["Desktop"] == "1", SourceRevision = data["Revision"]
                };
                var path = Path.Combine(directory, "distribution.json");
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Installation metadata cannot be a linked file.");
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(Distribution)).WriteObject(output, descriptor);
                return ActionResult.Success;
            });
        }

        private static bool WillInstall(Session session, string feature)
        {
            // ADDLOCAL/REMOVE overrides feature states for updater-driven upgrades.
            if (session["UPDATE"] == "1" && session["CB_PREVIOUS_INSTALL"] == "1")
                return session["ADDLOCAL"].Split(',').Contains(feature, StringComparer.Ordinal);
            var info = session.Features[feature];
            return info.RequestState == InstallState.Local || (info.RequestState == InstallState.Default && info.CurrentState == InstallState.Local);
        }

        private static ActionResult Guard(Session session, Func<ActionResult> action)
        {
            try { return action(); }
            catch (Exception error)
            {
                session.Log("CloudInlet installer: {0}", error.Message);
                using (var record = new Record(1))
                {
                    record.FormatString = "CloudInlet could not complete installation: [1]";
                    record[1] = error.Message;
                    session.Message(InstallMessage.Error, record);
                }
                return ActionResult.Failure;
            }
        }

        private static string Flavor(Session session) => session["CB_FLAVOR"] == "Debug" ? "Debug" : "Release";
        private static string AppName(string flavor) => flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet";
        private static string RunName(string flavor) => flavor == "Debug" ? "CloudInletDebug" : "CloudInlet";
        // Keep the installed Windows product and cross-installer identity stable.
        private static string Key(string flavor, string kind) => @"Software\CloudBay\Distribution\" + flavor + @"\" + kind;
        private static RegistryKey? UserKey(Session session, string path)
        {
            // Bind reads to the MSI installing user's hive rather than an HKCU
            // handle a custom-action CLR host may cache before impersonation.
            var sid = session["UserSID"];
            if (!sid.StartsWith("S-1-", StringComparison.Ordinal)) throw new IOException("The installing Windows user could not be identified.");
            using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
                return users.OpenSubKey(sid + "\\" + path);
        }
        private static string DesktopShortcut(string flavor) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName(flavor) + ".lnk");

        private static void AssertNormalTree(string directory)
        {
            ValidateDirectory(directory);
            if (!Directory.Exists(directory)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The installation contains a linked file or directory. Remove the link before upgrading.");
                if ((attributes & FileAttributes.Directory) != 0) AssertNormalTree(entry);
            }
        }

        private static void ValidateInstalledSource(string directory, string flavor)
        {
            var descriptor = ReadDescriptor(Path.Combine(directory, "distribution.json"));
            if (descriptor == null || descriptor.SchemaVersion != 1 || descriptor.BuildFlavor != flavor ||
                descriptor.InstallerKind != "Msi" || descriptor.InstallScope != "perUser" || descriptor.Architecture != "x64" ||
                string.IsNullOrEmpty(descriptor.InstallDirectory) ||
                !string.Equals(ValidateDirectory(descriptor.InstallDirectory), directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The existing installation does not match this MSI. Repair that installation before retrying the upgrade.");
            var executable = Path.Combine(directory, "CloudInlet.exe");
            if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0 ||
                FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0] != descriptor.Version)
                throw new IOException("Upgrade CloudBay through the published 1.1.2 bridge first, or repair the existing CloudInlet installation before updating.");
        }

        private static string ValidateDirectory(string value)
        {
            var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(value) || value.IndexOf('"') >= 0 || path.Length < 8 || path == Path.GetPathRoot(path)?.TrimEnd('\\') || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Choose a normal, local installation directory.");
            var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay");
            var brandedState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudInlet");
            if (IsWithin(path, state) || IsWithin(path, brandedState) || IsWithin(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                throw new IOException("The application cannot be installed into Windows or its private backup settings directory.");
            for (var ancestor = new DirectoryInfo(path); ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The installation directory contains a linked path.");
            return path;
        }

        private static bool IsWithin(string path, string root) => path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static Distribution? ReadDescriptor(string path)
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using (var input = File.OpenRead(path))
            {
                if (input.Length > 16384) return null;
                return new DataContractJsonSerializer(typeof(Distribution)).ReadObject(input) as Distribution;
            }
        }

        [DataContract]
        private sealed class Distribution
        {
            [DataMember(Name = "schemaVersion")] public int SchemaVersion;
            [DataMember(Name = "version")] public string Version = "";
            [DataMember(Name = "buildFlavor")] public string BuildFlavor = "";
            [DataMember(Name = "installerKind")] public string InstallerKind = "";
            [DataMember(Name = "architecture")] public string Architecture = "";
            [DataMember(Name = "installScope")] public string InstallScope = "";
            [DataMember(Name = "installDirectory")] public string InstallDirectory = "";
            [DataMember(Name = "startWithWindows")] public bool StartWithWindows;
            [DataMember(Name = "desktopShortcut")] public bool DesktopShortcut;
            [DataMember(Name = "sourceRevision")] public string SourceRevision = "";
        }
    }
}

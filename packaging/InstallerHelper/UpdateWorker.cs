using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal static class UpdateWorker
{
    // This helper runs outside the installation tree, so the updater never locks
    // the executable it is replacing. It has no cloud credentials or network access.
    internal static int Run(string requestFile)
    {
        UpdateRequest? request = null;
        string? root = null;
        var validated = false;
        var parentExited = false;
        var code = 1;
        var success = false;
        var message = "The update did not start.";
        FileStream? installationLock = null;
        try
        {
            request = Read<UpdateRequest>(requestFile);
            root = Validate(request, requestFile);
            var lockPath = Path.Combine(CacheRoot(request.BuildFlavor), "update-install.lock");
            CheckNormalPath(lockPath, false);
            installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            validated = true;
            WaitForParent(request, () => Write(request.ReadinessPath, new Readiness { SchemaVersion = 1, TargetVersion = request.TargetVersion, Ready = true }));
            parentExited = true;
            ValidateDistribution(request, root);
            CheckNormalPath(request.PackagePath, false);
            using (var package = new FileStream(request.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
            {
                if (package.Length != request.PackageSize) throw new IOException("The downloaded installer size changed.");
                using (var digest = SHA256.Create())
                    if (!FixedEquals(digest.ComputeHash(package), request.PackageSha256)) throw new IOException("The downloaded installer checksum changed. Download it again.");
                var start = request.InstallerKind == "Exe"
                    ? new ProcessStartInfo(request.PackagePath, "/UPDATE /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=" + Quote(root))
                    : new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), "/i " + Quote(request.PackagePath) + " /qn /norestart UPDATE=1 INSTALLDIR=" + Quote(root));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.WorkingDirectory = Path.GetDirectoryName(request.PackagePath);
                using (var installer = Process.Start(start) ?? throw new IOException("Windows could not start the installer."))
                {
                    // Installation owns its own rollback; never terminate it on a timer.
                    installer.WaitForExit();
                    code = installer.ExitCode;
                }
            }
            if (code != 0 && code != 3010) throw new IOException("The installer did not complete (code " + code + "). Open CloudInlet or run the installer again to repair the installation.");
            CheckNormalPath(root, true);
            var installed = Read<Distribution>(Path.Combine(root, "distribution.json"));
            if (installed.Version != request.TargetVersion || installed.InstallerKind != request.InstallerKind || installed.BuildFlavor != request.BuildFlavor)
                throw new IOException("The installer did not produce the expected CloudInlet version.");
            var productVersion = FileVersionInfo.GetVersionInfo(ApplicationImage(root)).ProductVersion?.Split('+')[0];
            if (productVersion != request.TargetVersion) throw new IOException("The installed application does not match the requested update.");
            success = true;
            message = code == 3010 ? "CloudInlet was updated. Windows requested a restart." : "CloudInlet was updated successfully.";
        }
        catch (Exception error)
        {
            message = error.Message;
            if (code == 0 || code == 3010) code = 1;
        }
        finally
        {
            if (validated && request != null && root != null)
            {
                try { Write(request.ResultPath, new UpdateResult { Success = success, ExitCode = code, Message = message, TargetVersion = request.TargetVersion, CompletedUtc = DateTime.UtcNow.ToString("O") }); }
                catch (Exception error) { Console.Error.WriteLine("Update result could not be saved: " + error.Message); }
                installationLock?.Dispose();
                installationLock = null;
                try
                {
                    if (parentExited)
                    {
                        CheckNormalPath(root, true);
                        var executable = ApplicationImage(root);
                        CheckNormalPath(executable, false);
                        Process.Start(new ProcessStartInfo(executable, request.RestartBackground ? "--background" : "") { UseShellExecute = false, WorkingDirectory = root });
                    }
                }
                catch (Exception error) { Console.Error.WriteLine("CloudInlet could not be reopened: " + error.Message); }
            }
            installationLock?.Dispose();
        }
        if (!success) Console.Error.WriteLine(message);
        return code;
    }

    private static string Validate(UpdateRequest request, string requestFile)
    {
        if (request.SchemaVersion != 1 || request.ParentProcessId <= 0 || request.ParentStartUtcTicks <= 0 ||
            (request.BuildFlavor != "Release" && request.BuildFlavor != "Debug") || (request.InstallerKind != "Exe" && request.InstallerKind != "Msi") ||
            request.PackageSize <= 0 || request.PackageSize > 1024L * 1024 * 1024 || !Regex.IsMatch(request.PackageSha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new IOException("The update request is invalid.");
        var installedVersion = ParseVersion(request.InstalledVersion);
        if (ParseVersion(request.TargetVersion) <= installedVersion) throw new IOException("An automatic update must be newer than the installed version.");
        var cache = CacheRoot(request.BuildFlavor);
        CheckNormalPath(cache, true);
        // The cache marker is a durable ownership identity, not a release format.
        var owner = Path.Combine(cache, ".cloudbay-update-cache-v1");
        CheckNormalPath(owner, false);
        const string marker = "CloudBay update cache v1\n";
        if (new FileInfo(owner).Length != marker.Length || File.ReadAllText(owner) != marker) throw new IOException("The update cache owner is invalid.");
        CheckOwned(requestFile, cache);
        CheckOwned(request.ResultPath, cache);
        CheckOwned(request.ReadinessPath, cache);
        CheckOwned(request.PackagePath, cache);
        var suffix = request.TargetVersion + "-win-x64-" + request.BuildFlavor.ToLowerInvariant() + "-setup." + request.InstallerKind.ToLowerInvariant();
        var packageName = Path.GetFileName(request.PackagePath);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(request.PackagePath)), cache, StringComparison.OrdinalIgnoreCase) ||
            packageName != "pending-CloudInlet-" + suffix)
            throw new IOException("The update package does not match this installation.");
        var root = Path.GetFullPath(request.InstallDirectory).TrimEnd('\\');
        if (!Path.IsPathRooted(request.InstallDirectory) || root.Length < 8 || root.StartsWith("\\\\", StringComparison.Ordinal) || root.IndexOf('"') >= 0)
            throw new IOException("The installation path is invalid.");
        CheckNormalPath(root, true);
        ValidateDistribution(request, root);
        return root;
    }

    private static void ValidateDistribution(UpdateRequest request, string root)
    {
        var descriptor = Read<Distribution>(Path.Combine(root, "distribution.json"));
        if (descriptor.SchemaVersion != 1 || descriptor.Version != request.InstalledVersion || descriptor.BuildFlavor != request.BuildFlavor || descriptor.InstallerKind != request.InstallerKind || descriptor.Architecture != "x64" || descriptor.InstallScope != "perUser" ||
            !string.Equals(Path.GetFullPath(descriptor.InstallDirectory).TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed distribution identity changed.");
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\CloudBay\Distribution\" + request.BuildFlavor + "\\" + request.InstallerKind))
            if (key?.GetValue("InstallDirectory") is not string directory || !string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installed registration does not match this update.");
        if (FileVersionInfo.GetVersionInfo(ApplicationImage(root)).ProductVersion?.Split('+')[0] != request.InstalledVersion)
            throw new IOException("The current application version does not match this update request.");
    }

    private static void WaitForParent(UpdateRequest request, Action ready)
    {
        Process process;
        try { process = Process.GetProcessById(request.ParentProcessId); }
        catch (ArgumentException) { ready(); return; } // Graceful shutdown can win the helper startup race.
        using (process)
        {
            var image = process.MainModule?.FileName;
            var directory = Path.GetFullPath(request.InstallDirectory);
            if (process.StartTime.ToUniversalTime().Ticks != request.ParentStartUtcTicks ||
                !string.Equals(image, Path.Combine(directory, "CloudInlet.exe"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The process waiting for an update is not this CloudInlet installation.");
            ready();
            if (!process.WaitForExit(90000)) throw new IOException("CloudInlet is still safely finishing transfers. Try the update again when syncing is idle.");
        }
    }

    private static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value ?? "", "^(0|[1-9][0-9]{0,2})\\.(0|[1-9][0-9]{0,2})\\.(0|[1-9][0-9]{0,4})$")) throw new IOException("The update version is invalid.");
        var result = new Version(value);
        if (result.Major > 255 || result.Minor > 255 || result.Build > 65535) throw new IOException("The update version exceeds Windows Installer limits.");
        return result;
    }

    private static string CacheRoot(string flavor) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", flavor == "Debug" ? @"Debug\Client\Updates" : @"Client\Updates");
    private static string ApplicationImage(string directory) => Path.Combine(Path.GetFullPath(directory), "CloudInlet.exe");
    private static void CheckOwned(string path, string cache)
    {
        var absolute = Path.GetFullPath(path);
        if (!Path.IsPathRooted(path) || !absolute.StartsWith(cache.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Update files must stay inside CloudInlet's private update cache.");
        CheckNormalPath(absolute, false);
    }
    private static void CheckNormalPath(string path, bool directory)
    {
        var absolute = Path.GetFullPath(path);
        var attributes = Attributes(absolute);
        if (attributes.HasValue && ((attributes.Value & FileAttributes.ReparsePoint) != 0 || !directory && (attributes.Value & FileAttributes.Directory) != 0)) throw new IOException("Updates cannot follow linked files.");
        for (var ancestor = new DirectoryInfo(directory ? absolute : Path.GetDirectoryName(absolute)); ancestor != null; ancestor = ancestor.Parent)
        {
            var parentAttributes = Attributes(ancestor.FullName);
            if (parentAttributes.HasValue && (parentAttributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("Updates cannot follow linked directories.");
        }
    }
    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static T Read<T>(string path)
    {
        CheckNormalPath(path, false);
        using (var input = File.OpenRead(path))
        {
            if (input.Length > 32768) throw new IOException("Update metadata is too large.");
            return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(input) ?? throw new IOException("Update metadata is empty."));
        }
    }
    private static bool FixedEquals(byte[] bytes, string expected)
    {
        var difference = 0;
        for (var i = 0; i < bytes.Length; i++) difference |= bytes[i] ^ Convert.ToByte(expected.Substring(i * 2, 2), 16);
        return difference == 0;
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"").TrimEnd('\\') + "\"";
    private static void Write<T>(string path, T result)
    {
        CheckNormalPath(path, false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(T)).WriteObject(output, result); output.Flush(true); }
            CheckNormalPath(path, false);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { CheckNormalPath(temporary, false); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [DataContract] private sealed class UpdateRequest
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "parentProcessId")] public int ParentProcessId { get; set; }
        [DataMember(Name = "parentStartUtcTicks")] public long ParentStartUtcTicks { get; set; }
        [DataMember(Name = "installDirectory")] public string InstallDirectory { get; set; } = "";
        [DataMember(Name = "buildFlavor")] public string BuildFlavor { get; set; } = "";
        [DataMember(Name = "installerKind")] public string InstallerKind { get; set; } = "";
        [DataMember(Name = "installedVersion")] public string InstalledVersion { get; set; } = "";
        [DataMember(Name = "targetVersion")] public string TargetVersion { get; set; } = "";
        [DataMember(Name = "packagePath")] public string PackagePath { get; set; } = "";
        [DataMember(Name = "packageSha256")] public string PackageSha256 { get; set; } = "";
        [DataMember(Name = "packageSize")] public long PackageSize { get; set; }
        [DataMember(Name = "resultPath")] public string ResultPath { get; set; } = "";
        [DataMember(Name = "readinessPath")] public string ReadinessPath { get; set; } = "";
        [DataMember(Name = "restartBackground")] public bool RestartBackground { get; set; }
    }
    [DataContract] private sealed class Readiness
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "ready")] public bool Ready { get; set; }
        [DataMember(Name = "targetVersion")] public string TargetVersion { get; set; } = "";
    }
    [DataContract] private sealed class UpdateResult
    {
        [DataMember(Name = "success")] public bool Success { get; set; }
        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; } = "";
        [DataMember(Name = "targetVersion")] public string TargetVersion { get; set; } = "";
        [DataMember(Name = "completedUtc")] public string CompletedUtc { get; set; } = "";
    }
    [DataContract] private sealed class Distribution
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; } = "";
        [DataMember(Name = "buildFlavor")] public string BuildFlavor { get; set; } = "";
        [DataMember(Name = "installerKind")] public string InstallerKind { get; set; } = "";
        [DataMember(Name = "architecture")] public string Architecture { get; set; } = "";
        [DataMember(Name = "installScope")] public string InstallScope { get; set; } = "";
        [DataMember(Name = "installDirectory")] public string InstallDirectory { get; set; } = "";
    }
}

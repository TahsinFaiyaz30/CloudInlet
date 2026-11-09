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
using CloudInlet.Packaging;

internal static class UpdateWorker
{
    // This helper runs outside the installation tree, so the updater never locks
    // the executable it is replacing. It has no cloud credentials or network access.
    internal static int Run(string requestFile)
    {
        UpdateRequest? request = null;
        string? root = null;
        string? destination = null;
        var validated = false;
        var parentExited = false;
        var code = 1;
        var success = false;
        var attentionRequired = false;
        var installerRequiresRestart = false;
        var migrationRequiresRestart = false;
        var migrationCompleted = false;
        var migrationReverted = false;
        var exeRecoveryOwnsRelaunch = false;
        string? progressFailure = null;
        var message = "The update did not start.";
        FileStream? installationLock = null;
        InstallerProgressJournal? progress = null;
        InstallerMigrationRecovery? migrationRecovery = null;
        string? requestHash = null;
        try
        {
            byte[] requestBytes;
            request = Read<UpdateRequest>(requestFile, out requestBytes);
            root = Validate(request, requestFile, out var cache);
            destination = request.MigrateInstallation ? request.MigrationInstallDirectory : root;
            var lockPath = Path.Combine(cache, "update-install.lock");
            CheckNormalPath(lockPath, false);
            installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            migrationRecovery = new InstallerMigrationRecovery(request.BuildFlavor);
            migrationRecovery.EnsureNoPending();
            ExeMigrationReceipt.EnsureNoPending(request.BuildFlavor);
            validated = true;
            if (!string.IsNullOrEmpty(request.ProgressPath))
            {
                var handoffState = InstallerProgressHandoff.ReadState(request.ProgressPath, request.ProgressId,
                    InstallerProgressJournal.RequestHash(requestBytes));
                if (handoffState != "Pending" && handoffState != "Committed")
                    throw new IOException("The installation progress handoff is missing or canceled. Your existing installation was retained.");
                progress = InstallerProgressJournal.Create(request.ProgressPath, request.ProgressId,
                    InstallerProgressJournal.RequestHash(requestBytes));
                progress.Report("Validating", "The update request and installation identity were verified.", migrationRequested: request.MigrateInstallation);
                progress.Report("WaitingForAppExit", "Waiting for CloudInlet to finish transfers and close safely.", migrationRequested: request.MigrateInstallation);
            }
            requestHash = InstallerProgressJournal.RequestHash(requestBytes);
            WaitForParent(request, () => Write(request.ReadinessPath, new Readiness { SchemaVersion = 1, TargetVersion = request.TargetVersion, Ready = true }),
                string.IsNullOrEmpty(request.ProgressPath) ? null : () =>
                    InstallerProgressHandoff.ReadState(request.ProgressPath, request.ProgressId, requestHash) == "Canceled");
            parentExited = true;
            if (!string.IsNullOrEmpty(request.ProgressPath) &&
                InstallerProgressHandoff.ReadState(request.ProgressPath, request.ProgressId, requestHash) != "Committed")
                throw new IOException("The installation progress handoff was not completed. No installer was started; reopen CloudInlet and retry the update.");
            ValidateDistribution(request, root);
            if (request.MigrateInstallation) ValidateMigrationDestination(request, root);
            CheckNormalPath(request.PackagePath, false);
            progress?.Report("VerifyingPackage", "Checking the downloaded installer size and SHA-256.", migrationRequested: request.MigrateInstallation);
            using (var package = new FileStream(request.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
            {
                if (package.Length != request.PackageSize) throw new IOException("The downloaded installer size changed.");
                using (var digest = SHA256.Create())
                    if (!FixedEquals(digest.ComputeHash(package), request.PackageSha256)) throw new IOException("The downloaded installer checksum changed. Download it again.");
                // MSI remembers the source package's basename for later repair. Never
                // register a private pending-* name that users cannot download again.
                using var msiSource = request.InstallerKind == "Msi" ? OpenMsiRepairSource(request, package, cache) : null;
                progress?.Report("PackageVerified", "The downloaded installer was verified and its repair source is ready.", migrationRequested: request.MigrateInstallation);
                var installerPath = msiSource?.Name ?? request.PackagePath;
                if (request.MigrateInstallation && request.InstallerKind == "Msi")
                    migrationRecovery.Begin(requestHash, root, destination, request.InstalledVersion, request.TargetVersion,
                        ReadRegistration(request.BuildFlavor, request.InstallerKind)!.Brand, request.PackageSha256, request.PackageSize);
                var start = request.InstallerKind == "Exe"
                    ? new ProcessStartInfo(installerPath, "/UPDATE /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP- /DIR=" + Quote(root))
                    : new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), "/i " + Quote(installerPath) + " /qn /norestart UPDATE=1 INSTALLDIR=" + Quote(destination) +
                        (request.MigrateInstallation ? " MIGRATEINSTALLATION=1 MIGRATIONDIRECTORY=" + Quote(destination) : ""));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.WorkingDirectory = Path.GetDirectoryName(request.PackagePath);
                if (request.MigrateInstallation && request.InstallerKind == "Exe")
                {
                    // Save consent and the verified package before updating. A
                    // restart must not lose migration when the version advances.
                    var outcome = ExeMigrationWorker.Execute(root, destination!, request.BuildFlavor, request.TargetVersion,
                        request.PackagePath, request.PackageSha256, request.PackageSize, requestHash!,
                        detail => { exeRecoveryOwnsRelaunch = true;
                            TryReportAfterInstaller(() => progress?.Report("InstallerRunning", detail, migrationRequested: true, recoveryWindowShown: true), ref progressFailure); },
                        request.RecoveryObserverExecutable, request.InstalledVersion);
                    migrationCompleted = outcome.Phase == "Completed";
                    migrationReverted = outcome.Phase == "Reverted";
                    destination = outcome.Directory;
                    if (!migrationCompleted && !migrationReverted) throw new IOException("Installation recovery has not reached a verified result.");
                    if (migrationReverted) attentionRequired = true;
                    code = 0;
                }
                else for (var attempt = 0; ; attempt++)
                {
                    progress?.Report("InstallerRunning", "Windows is applying the verified update. The installer controls rollback; do not turn off this PC.", migrationRequested: request.MigrateInstallation);
                    using (var installer = Process.Start(start) ?? throw new IOException("Windows could not start the installer."))
                    {
                        // Installation owns its own rollback; never terminate it on a timer.
                        installer.WaitForExit();
                        code = installer.ExitCode;
                        installerRequiresRestart = code == 3010;
                    }
                    if (request.InstallerKind != "Msi" || code != 1618 || attempt == 5) break;
                    Console.Error.WriteLine("Windows Installer is busy. Waiting before retry " + (attempt + 1) + " of 5.");
                    progress?.Report("InstallerBusy", "Another installation is running. Waiting 10 seconds before retry " + (attempt + 1) + " of 5.", migrationRequested: request.MigrateInstallation);
                    System.Threading.Thread.Sleep(10000);
                    ValidateDistribution(request, root);
                    if (request.MigrateInstallation) ValidateMigrationDestination(request, root);
                }
                if (request.MigrateInstallation && request.InstallerKind == "Msi")
                    migrationRecovery.RecordInstallerExit(requestHash, code);
            }
            if (code == 1618) throw new IOException("Another Windows installation is still running. CloudInlet waited and retried five times. Finish that installation, then retry this update; your existing installation was retained.");
            if (code != 0 && code != 3010) throw new IOException("The installer did not complete (code " + code + "). Open CloudInlet or run the installer again to repair the installation.");
            // The installer has already committed or requested restart. Reporting failure
            // must never skip verification or strand reopening at the retired location.
            TryReportAfterInstaller(() => progress?.Report("VerifyingInstallation", "Checking the installed version, registry, selected location and retired application files.",
                migrationRequested: request.MigrateInstallation), ref progressFailure);
            CheckNormalPath(destination, true);
            var installed = Read<Distribution>(Path.Combine(destination, "distribution.json"));
            if (installed.SchemaVersion != 1 || installed.Version != request.TargetVersion || installed.InstallerKind != request.InstallerKind || installed.BuildFlavor != request.BuildFlavor ||
                !string.Equals(InstallationMigrationPolicy.Normalize(installed.InstallDirectory), destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installer did not produce the expected CloudInlet version.");
            var productVersion = FileVersionInfo.GetVersionInfo(ApplicationImage(destination)).ProductVersion?.Split('+')[0];
            if (productVersion != request.TargetVersion) throw new IOException("The installed application does not match the requested update.");
            var registration = ReadRegistration(request.BuildFlavor, request.InstallerKind);
            if (registration == null || registration.Version != request.TargetVersion ||
                !string.Equals(registration.Directory, destination, StringComparison.OrdinalIgnoreCase) ||
                request.MigrateInstallation && !migrationReverted && registration.Brand != "CloudInlet")
                throw new IOException("The installer registration could not be verified. Repair the installation before retrying migration.");
            var previousRoot = root;
            // Reopen the independently verified new installation even if a
            // subsequent retirement check fails. The former path may already
            // have been removed by Windows Installer's completed transaction.
            root = destination;
            if (request.MigrateInstallation)
            {
                migrationRequiresRestart = code == 3010;
                if (request.InstallerKind == "Msi") migrationRecovery!.RecordVerifiedInstallation(requestHash!);
                if (!migrationRequiresRestart && !string.Equals(previousRoot, destination, StringComparison.OrdinalIgnoreCase))
                    VerifyRetiredApplicationFiles(previousRoot, destination);
                if (!migrationRequiresRestart && request.InstallerKind == "Msi")
                {
                    var recovery = migrationRecovery!.Resolve();
                    if (!recovery.Completed) throw new IOException(recovery.Message ?? "Installation migration could not be verified.");
                    migrationCompleted = true;
                }
            }
            success = true;
            message = migrationReverted ? "CloudInlet was updated and restored at its original location. The installation move did not complete; your completed update was preserved." :
                migrationRequiresRestart ? "CloudInlet was updated. Restart Windows to finish installation migration; it has not been marked complete." :
                code == 3010 ? "CloudInlet was updated. Windows requested a restart." : "CloudInlet was updated successfully.";
        }
        catch (Exception error)
        {
            message = error.Message;
            if (error is ExeInstallationMigration.PauseException && request?.MigrateInstallation == true)
            {
                migrationRequiresRestart = true;
                attentionRequired = true;
            }
            if (request?.MigrateInstallation == true && request.InstallerKind == "Msi" && migrationRecovery != null)
            {
                try
                {
                    var recovery = migrationRecovery.Resolve();
                    migrationRequiresRestart = recovery.Pending;
                    if (recovery.Pending && recovery.Message != null) message += " " + recovery.Message;
                }
                catch (Exception recoveryError)
                {
                    migrationRequiresRestart = true;
                    message += " Migration recovery needs attention: " + recoveryError.Message;
                }
            }
            if (!success && (code == 0 || code == 3010)) code = 1;
        }
        finally
        {
            if (validated && request != null && root != null)
            {
                if (progressFailure != null)
                {
                    attentionRequired = true;
                    message += " Progress reporting needs attention: " + progressFailure;
                }
                void SaveResult()
                {
                    try { Write(request.ResultPath, new UpdateResult { Success = success, ExitCode = code, Message = message, TargetVersion = request.TargetVersion, CompletedUtc = DateTime.UtcNow.ToString("O"),
                        MigrationRequested = request.MigrateInstallation, MigrationCompleted = migrationCompleted,
                        MigrationRequiresRestart = migrationRequiresRestart, InstallDirectory = root, AttentionRequired = attentionRequired }); }
                    catch (Exception error)
                    {
                        attentionRequired = true;
                        message += " Update result could not be saved: " + error.Message;
                        Console.Error.WriteLine(message);
                    }
                }
                // The reopened app must see this outcome, even if it starts immediately.
                SaveResult();
                migrationRecovery?.Dispose();
                migrationRecovery = null;
                installationLock?.Dispose();
                installationLock = null;
                try
                {
                    if (parentExited && !exeRecoveryOwnsRelaunch && !installerRequiresRestart && !migrationRequiresRestart)
                    {
                        CheckNormalPath(root, true);
                        var executable = ApplicationImage(root);
                        CheckNormalPath(executable, false);
                        using var restarted = Process.Start(new ProcessStartInfo(executable, request.RestartBackground ? "--background" : "") { UseShellExecute = false, WorkingDirectory = root })
                            ?? throw new IOException("Windows did not start the installed application.");
                    }
                }
                catch (Exception error)
                {
                    attentionRequired = true;
                    message += " CloudInlet could not be reopened: " + error.Message;
                    Console.Error.WriteLine(message);
                    SaveResult();
                }
                var previousProgressFailure = progressFailure;
                TryReportAfterInstaller(() => progress?.Report(migrationRequiresRestart || installerRequiresRestart ? "RestartRequired" : success && !attentionRequired ? "Complete" : "Failed",
                    message.Length <= 2048 ? message : message.Substring(0, 2048),
                    migrationRequiresRestart || installerRequiresRestart ? "RestartRequired" : success && !attentionRequired ? "Succeeded" : "Failed", request.MigrateInstallation,
                    migrationCompleted, migrationRequiresRestart || installerRequiresRestart), ref progressFailure);
                if (progressFailure != previousProgressFailure)
                {
                    attentionRequired = true;
                    message += " Final progress reporting needs attention: " + progressFailure;
                    SaveResult();
                }
            }
            installationLock?.Dispose();
            migrationRecovery?.Dispose();
            progress?.Dispose();
        }
        if (!success) Console.Error.WriteLine(message);
        return code;
    }

    private static void TryReportAfterInstaller(Action report, ref string? failure)
    {
        try { report(); }
        catch (Exception error)
        {
            failure = error.Message;
            Console.Error.WriteLine("Installer progress could not be saved: " + error.Message);
        }
    }

    private static FileStream OpenMsiRepairSource(UpdateRequest request, FileStream verified, string cache)
    {
        var path = Path.Combine(cache, Path.GetFileName(request.PackagePath).Substring("pending-".Length));
        CheckNormalPath(path, false);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { verified.Position = 0; verified.CopyTo(output); output.Flush(true); }
            File.Move(temporary, path); // Never replace a concurrently created file.
        }
        CheckNormalPath(path, false);
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var digest = SHA256.Create();
            if (source.Length != request.PackageSize || !FixedEquals(digest.ComputeHash(source), request.PackageSha256))
                throw new IOException("The cached MSI repair package could not be verified. Delete downloaded updates and download it again.");
            return source; // Block replacement/writes until Windows Installer exits.
        }
        catch { source.Dispose(); throw; }
    }

    private static string Validate(UpdateRequest request, string requestFile, out string cache)
    {
        if ((request.SchemaVersion != 1 && request.SchemaVersion != 2) || request.ParentProcessId <= 0 || request.ParentStartUtcTicks <= 0 ||
            (request.BuildFlavor != "Release" && request.BuildFlavor != "Debug") || (request.InstallerKind != "Exe" && request.InstallerKind != "Msi") ||
            request.PackageSize <= 0 || request.PackageSize > 1024L * 1024 * 1024 || !Regex.IsMatch(request.PackageSha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new IOException("The update request is invalid.");
        if (request.SchemaVersion == 1 && request.MigrateInstallation ||
            !request.MigrateInstallation && !string.IsNullOrEmpty(request.MigrationInstallDirectory))
            throw new IOException("The installation migration request is invalid.");
        var installedVersion = ParseVersion(request.InstalledVersion);
        if (ParseVersion(request.TargetVersion) <= installedVersion) throw new IOException("An automatic update must be newer than the installed version.");
        cache = UpdateCacheLocation.Validate(request.BuildFlavor, requestFile, request.PackagePath,
            request.ResultPath, request.ReadinessPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!string.IsNullOrEmpty(request.ProgressPath) || !string.IsNullOrEmpty(request.ProgressId))
        {
            if (request.SchemaVersion != 2) throw new IOException("Installer progress requires a version 2 update request.");
            request.ProgressPath = InstallerProgressJournal.ValidatePath(cache, request.ProgressPath, request.ProgressId);
        }
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
        if (request.MigrateInstallation) ValidateMigrationDestination(request, root);
        return root;
    }

    private static void ValidateDistribution(UpdateRequest request, string root)
    {
        var descriptor = Read<Distribution>(Path.Combine(root, "distribution.json"));
        if (descriptor.SchemaVersion != 1 || descriptor.Version != request.InstalledVersion || descriptor.BuildFlavor != request.BuildFlavor || descriptor.InstallerKind != request.InstallerKind || descriptor.Architecture != "x64" || descriptor.InstallScope != "perUser" ||
            !string.Equals(Path.GetFullPath(descriptor.InstallDirectory).TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed distribution identity changed.");
        var registration = ReadRegistration(request.BuildFlavor, request.InstallerKind);
        if (registration == null || registration.Version != request.InstalledVersion || !string.Equals(registration.Directory, root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed registration does not match this update.");
        if (FileVersionInfo.GetVersionInfo(ApplicationImage(root)).ProductVersion?.Split('+')[0] != request.InstalledVersion)
            throw new IOException("The current application version does not match this update request.");
    }

    private static DistributionRegistration? ReadRegistration(string flavor, string kind) =>
        DistributionRegistration.Read(flavor, kind, (path, name) =>
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path)) return key?.GetValue(name) as string;
        });

    private static void ValidateMigrationDestination(UpdateRequest request, string root)
    {
        var destination = InstallationMigrationPolicy.Validate(root, request.MigrationInstallDirectory,
            request.BuildFlavor, request.InstallerKind, request.InstalledVersion, request.TargetVersion);
        CheckNormalPath(destination, true);
        if (!string.Equals(root, destination, StringComparison.OrdinalIgnoreCase) &&
            (Directory.Exists(destination) || File.Exists(destination)))
            throw new IOException("The reviewed CloudInlet installation destination already exists. No files were overwritten.");
        request.MigrationInstallDirectory = destination;
        if (request.InstallerKind == "Exe")
        {
            var expectedObserver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudInlet", "Migration", request.BuildFlavor, "InstallerProgress", "host", "progress-" + request.ProgressId, "CloudInlet.exe");
            if (string.IsNullOrEmpty(request.ProgressPath) || string.IsNullOrEmpty(request.RecoveryObserverExecutable) ||
                !string.Equals(Path.GetFullPath(request.RecoveryObserverExecutable), expectedObserver, StringComparison.OrdinalIgnoreCase))
                throw new IOException("EXE migration requires its verified recovery window before the app exits.");
            CheckNormalPath(expectedObserver, false);
        }
    }

    private static void VerifyRetiredApplicationFiles(string source, string destination)
    {
        CheckNormalPath(source, true);
        if (!Directory.Exists(source)) return;
        // Windows Installer removes only owned components. Do not delete or
        // relocate unknown personal files left in the former application folder.
        foreach (var installed in EnumerateNormalFiles(destination))
        {
            CheckNormalPath(installed, false);
            var relative = installed.Substring(destination.TrimEnd('\\').Length + 1);
            var previous = Path.Combine(source, relative);
            CheckNormalPath(previous, false);
            if (File.Exists(previous))
                throw new IOException("The new installation is ready, but an application file remains in the former installation folder: " + relative +
                    ". Installation migration has not been marked complete. Repair the installation before retrying.");
        }
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateNormalFiles(string directory)
    {
        CheckNormalPath(directory, true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The installed application contains a linked path.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateNormalFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    private static void WaitForParent(UpdateRequest request, Action ready, Func<bool>? canceled)
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
            var elapsed = Stopwatch.StartNew();
            while (!process.WaitForExit(250))
            {
                if (canceled?.Invoke() == true) throw new IOException("The update handoff was canceled before installation. Your existing installation was retained.");
                if (elapsed.ElapsedMilliseconds >= 90000) throw new IOException("CloudInlet is still safely finishing transfers. Try the update again when syncing is idle.");
            }
        }
    }

    private static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value ?? "", "^(0|[1-9][0-9]{0,2})\\.(0|[1-9][0-9]{0,2})\\.(0|[1-9][0-9]{0,4})$")) throw new IOException("The update version is invalid.");
        var result = new Version(value);
        if (result.Major > 255 || result.Minor > 255 || result.Build > 65535) throw new IOException("The update version exceeds Windows Installer limits.");
        return result;
    }

    private static string ApplicationImage(string directory) => Path.Combine(Path.GetFullPath(directory), "CloudInlet.exe");
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
    { return Read<T>(path, out _); }
    private static T Read<T>(string path, out byte[] bytes)
    {
        CheckNormalPath(path, false);
        using (var input = File.OpenRead(path))
        {
            if (input.Length > 32768) throw new IOException("Update metadata is too large.");
            using var memory = new MemoryStream(); input.CopyTo(memory); bytes = memory.ToArray();
            using var payload = new MemoryStream(bytes, writable: false);
            return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(payload) ?? throw new IOException("Update metadata is empty."));
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
        [DataMember(Name = "migrateInstallation")] public bool MigrateInstallation { get; set; }
        [DataMember(Name = "migrationInstallDirectory")] public string MigrationInstallDirectory { get; set; } = "";
        [DataMember(Name = "progressId")] public string ProgressId { get; set; } = "";
        [DataMember(Name = "progressPath")] public string ProgressPath { get; set; } = "";
        [DataMember(Name = "recoveryObserverExecutable")] public string? RecoveryObserverExecutable { get; set; }
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
        [DataMember(Name = "migrationRequested")] public bool MigrationRequested { get; set; }
        [DataMember(Name = "migrationCompleted")] public bool MigrationCompleted { get; set; }
        [DataMember(Name = "migrationRequiresRestart")] public bool MigrationRequiresRestart { get; set; }
        [DataMember(Name = "installDirectory")] public string InstallDirectory { get; set; } = "";
        [DataMember(Name = "attentionRequired")] public bool AttentionRequired { get; set; }
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

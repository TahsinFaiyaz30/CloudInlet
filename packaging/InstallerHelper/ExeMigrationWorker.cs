using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using CloudInlet.Packaging;

// This adapter deliberately does not offer a /DIR shortcut around Inno's
// previous-installation ownership. It removes the verified current installation
// through its own uninstaller, then installs the same verified newer package.
internal sealed class ExeMigrationWorker : ExeInstallationMigration.IRuntime, ExeInstallationMigration.IJournal, IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudInlet.ExeInstallationMigration.v1");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _directory, _path;
    private readonly FileStream _lease;
    private readonly AncestorGuards _guards;
    private ExeMigrationReceipt _receipt;
    private const int MaximumReceipt = 8 * 1024 * 1024;

    [DataContract] private sealed class Payload
    {
        [DataMember(IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(IsRequired = true)] public List<ExeMigrationFile> Files { get; set; } = new();
    }
    [DataContract] private sealed class StagedObserver
    {
        [DataMember(IsRequired = true)] public string Id { get; set; } = "";
        [DataMember(IsRequired = true)] public string Directory { get; set; } = "";
        [DataMember(IsRequired = true)] public Payload Payload { get; set; } = new();
    }
    [DataContract] private sealed class Distribution
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; } = "";
        [DataMember(Name = "installerKind")] public string InstallerKind { get; set; } = "";
        [DataMember(Name = "buildFlavor")] public string Flavor { get; set; } = "";
        [DataMember(Name = "installDirectory")] public string Directory { get; set; } = "";
        [DataMember(Name = "architecture")] public string Architecture { get; set; } = "";
        [DataMember(Name = "installScope")] public string Scope { get; set; } = "";
    }
    internal sealed class Outcome
    {
        internal string Phase { get; }
        internal string Directory { get; }
        internal string Detail { get; }
        internal Outcome(ExeInstallationMigration.State state)
        { Phase = state.Phase; Directory = state.Phase == "Completed" ? state.Destination : state.Source; Detail = state.Detail; }
    }

    internal static Outcome Execute(string source, string destination, string flavor, string version,
        string package, string sha256, long size, string requestHash, Action<string>? progress, string? observerExecutable = null,
        string? installedVersion = null)
    {
        using var worker = CreateCore(source, destination, flavor, version, package, sha256, size, requestHash, observerExecutable, installedVersion);
        try
        {
            worker.EnsureObserverReady();
            try { progress?.Invoke("The installation recovery window is ready. The verified package and selected migration were saved."); } catch { }
            return new Outcome(ExeInstallationMigration.Resume(worker, worker, progress));
        }
        catch (Exception error) { worker.RecordFailure(error); throw; }
    }

    internal static int Resume(string receiptPath, bool revert = false)
    {
        using var worker = Open(receiptPath);
        try
        {
            worker._receipt.Failure = null; worker._receipt.FailureRequiresRestart = false; worker.WriteReceipt(create: false);
            worker.ArmRecovery();
            worker.EnsureObserverReady();
            ExeInstallationMigration.Resume(worker, worker, null, revert);
            return 0; // The verified WinUI observer owns Open after completion.
        }
        catch (Exception error) { worker.RecordFailure(error); throw; }
    }

    private ExeMigrationWorker(string directory, ExeMigrationReceipt receipt)
    {
        _directory = directory; _path = Path.Combine(directory, "migration.dpapi"); _receipt = receipt;
        Normal(_directory, true); Normal(_path + ".lock", false);
        _guards = new AncestorGuards(_directory);
        try { _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch { _guards.Dispose(); throw; }
    }

    private static ExeMigrationWorker Create(string source, string destination, string flavor, string version,
        string package, string hash, long size, string requestHash, string? observerExecutable)
        => CreateCore(source, destination, flavor, version, package, hash, size, requestHash, observerExecutable, null);

    private static ExeMigrationWorker CreateCore(string source, string destination, string flavor, string version,
        string package, string hash, long size, string requestHash, string? observerExecutable, string? installedVersion)
    {
        if (hash is null || requestHash is null) throw new IOException("The EXE migration checksums are missing.");
        source = InstallationMigrationPolicy.Normalize(source); destination = InstallationMigrationPolicy.Normalize(destination);
        if (flavor != "Release" && flavor != "Debug" || !System.Version.TryParse(version, out _) ||
            !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || !Regex.IsMatch(requestHash, "^[a-f0-9]{64}$") || size <= 0 || size > 1024L * 1024 * 1024 ||
            !string.Equals(InstallationMigrationPolicy.Target(source, flavor), destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The EXE migration identity is invalid.");
        var registrationOnly = source.Equals(destination, StringComparison.OrdinalIgnoreCase);
        var originalVersion = installedVersion ?? version;
        if (!System.Version.TryParse(originalVersion, out var previousVersion) ||
            installedVersion != null && System.Version.Parse(version) <= previousVersion)
            throw new IOException("The EXE migration update must be newer than the original installation.");
        ExeMigrationReceipt.EnsureNoPending(flavor);
        using var locations = new AncestorGuards(Path.GetDirectoryName(source)!, Path.GetDirectoryName(destination)!);
        var registration = Registration(flavor) ?? throw new IOException("The current EXE installer registration is missing.");
        if (registration.Directory != source || registration.Version != originalVersion) throw new IOException("The original EXE installation must match the reviewed update.");
        if (registrationOnly && registration.Brand != "CloudBay") throw new IOException("This installation already uses the CloudInlet registry location.");
        Normal(source, true); Normal(destination, true);
        if (!registrationOnly && (Directory.Exists(destination) || File.Exists(destination))) throw new IOException("The reviewed destination already exists. No files were overwritten.");
        var id = Guid.NewGuid().ToString("N");
        var recoveryRoot = RecoveryRoot(flavor);
        Normal(recoveryRoot, true); Directory.CreateDirectory(recoveryRoot);
        using var recoveryParent = new AncestorGuards(recoveryRoot);
        // Incomplete preparation has not authorized any installer mutation. Publish
        // the GUID directory only after its package and protected receipt are durable,
        // so a copy/encryption failure cannot permanently block every future update.
        var directory = Path.Combine(recoveryRoot, ".preparing-" + id);
        Normal(directory, true); Directory.CreateDirectory(directory); Normal(directory, true);
        var payload = Read<Payload>(Path.Combine(source, "installer-progress-payload.json"), 4 * 1024 * 1024);
        if (payload.SchemaVersion != 1 || payload.Files.Count < 1 || payload.Files.Count > 10000 || !payload.Files.Any(x => x.Path == "CloudInlet.exe") ||
            payload.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Files.Count)
            throw new IOException("The installed EXE payload inventory is invalid.");
        foreach (var file in payload.Files) VerifyFile(Resolve(source, file.Path), file);
        var uninstaller = OwnedUninstaller(source, flavor, originalVersion);
        var receipt = new ExeMigrationReceipt { Id = id, RequestHash = requestHash, Flavor = flavor, Version = version,
            OriginalVersion = originalVersion,
            OriginalBrand = registration.Brand, Uninstaller = Path.GetFileName(uninstaller), PayloadFiles = payload.Files,
            State = new ExeInstallationMigration.State { Source = source, Destination = destination, RegistrationOnly = registrationOnly,
                Phase = installedVersion == null ? "Prepared" : "UpdatingOriginal",
                Detail = installedVersion == null ? "The updated installation is ready for migration." : "The application update and selected migration are saved for recovery." },
            Startup = StartupEnabled(source, flavor), Desktop = File.Exists(DesktopShortcut(flavor)),
            OriginalDirectoryIdentity = DirectoryIdentity(source) };
        if (registrationOnly) receipt.DestinationDirectoryIdentity = receipt.OriginalDirectoryIdentity;
        // The snapshot is ownership evidence, not a payload backup. Inno retains
        // responsibility for deleting only files present in its uninstall log.
        receipt.OriginalFiles = Files(source).Select(path => Snapshot(path, path.Substring(source.Length + 1))).ToList();
        receipt.Package = new ExeMigrationFile { Path = "installer.exe", Size = size, Sha256 = hash.ToLowerInvariant() };
        CopyVerified(package, Resolve(directory, receipt.Package.Path), receipt.Package);
        var helperSource = Path.Combine(source, "CloudInlet.SetupHelper.exe");
        receipt.Helper = Snapshot(helperSource, "recovery.exe");
        CopyVerified(helperSource, Resolve(directory, receipt.Helper.Path), receipt.Helper);
        var helperConfig = helperSource + ".config";
        if (File.Exists(helperConfig))
        {
            receipt.HelperConfig = Snapshot(helperConfig, "recovery.exe.config");
            CopyVerified(helperConfig, Resolve(directory, receipt.HelperConfig.Path), receipt.HelperConfig);
        }
        if (observerExecutable != null) AttachObserver(receipt, observerExecutable);
        using (var prepared = new ExeMigrationWorker(directory, receipt))
        {
            prepared.VerifyOriginal(); prepared.WriteReceipt(create: true);
        }
        var published = Path.Combine(recoveryRoot, id);
        Normal(published, true); Directory.Move(directory, published);
        var worker = Open(Path.Combine(published, "migration.dpapi"));
        try
        {
            worker.ArmRecovery(); worker.VerifyRecovery(); return worker;
        }
        catch (Exception error) { worker.RecordFailure(error); worker.Dispose(); throw; }
    }

    internal static string RecoveryRoot(string flavor)
    {
        if (flavor != "Debug" && flavor != "Release") throw new IOException("The EXE recovery build is invalid.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudInlet", "Migration", flavor, "ExeInstallation");
    }

    private static ExeMigrationWorker Open(string path)
    {
        path = Path.GetFullPath(path); Normal(path, false);
        var directory = Path.GetDirectoryName(path)!;
        var id = Path.GetFileName(directory);
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$") || Path.GetFileName(path) != "migration.dpapi") throw new IOException("The EXE recovery path is invalid.");
        var flavor = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory))!);
        if (!directory.Equals(Path.Combine(RecoveryRoot(flavor), id), StringComparison.OrdinalIgnoreCase)) throw new IOException("The EXE recovery receipt is outside its private location.");
        // Acquire before reading so concurrent startup/manual retries cannot race.
        var worker = new ExeMigrationWorker(directory, new ExeMigrationReceipt());
        try { worker._receipt = worker.ReadReceipt(); worker.ValidateReceipt(id); return worker; }
        catch { worker.Dispose(); throw; }
    }

    private void ValidateReceipt(string id)
    {
        var r = _receipt;
        r.Validate(_path);
        if (r.Id != id) throw new IOException("The EXE migration recovery identifier changed.");
        foreach (var file in r.PayloadFiles.Concat(r.OriginalFiles)) Resolve(r.State.Source, file.Path);
    }

    public ExeInstallationMigration.State Read() => _receipt.State;
    public void Write(ExeInstallationMigration.State state)
    {
        ExeInstallationMigration.Validate(state);
        if (state.Source != _receipt.State.Source || state.Destination != _receipt.State.Destination || state.RegistrationOnly != _receipt.State.RegistrationOnly || state.Sequence != _receipt.State.Sequence + 1 ||
            _receipt.State.Phase == "Completed" || _receipt.State.Phase == "Reverted") throw new IOException("The EXE migration journal cannot be rewritten or moved backwards.");
        var previous = _receipt.State; _receipt.State = state;
        try { WriteReceipt(create: false); } catch { _receipt.State = previous; throw; }
    }
    public void VerifyRecovery()
    {
        EnsureRestartSatisfied();
        VerifyFile(Resolve(_directory, _receipt.Package.Path), _receipt.Package);
        VerifyFile(Resolve(_directory, _receipt.Helper.Path), _receipt.Helper);
        if (_receipt.HelperConfig != null) VerifyFile(Resolve(_directory, _receipt.HelperConfig.Path), _receipt.HelperConfig);
        else if (File.Exists(Path.Combine(_directory, "recovery.exe.config"))) throw new IOException("An unreviewed recovery configuration appeared.");
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(RunName) as string != RecoveryCommand) throw new IOException("The installation recovery startup command changed. Restore the recovery registration before continuing.");
        Normal(_receipt.State.Source, true); Normal(_receipt.State.Destination, true);
    }
    public void UpdateOriginal()
    {
        if (_receipt.State.Phase != "UpdatingOriginal") throw new IOException("No original application update is pending.");
        using var locations = LocationGuards();
        using var sourceDirectory = new AncestorGuards(_receipt.State.Source);
        VerifyRecovery(); VerifyOriginalDirectory();
        if (!IsInstalledAt(_receipt.State.Source))
        {
            VerifyOriginal();
            var installer = Resolve(_directory, _receipt.Package.Path);
            using var pinned = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyStream(pinned, _receipt.Package);
            var code = RunNative("Update", installer, "/UPDATE /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP- /DIR=" + Quote(_receipt.State.Source));
            if (code != 0) throw new IOException("The verified application update needs attention (code " + code + "). Its installation recovery remains available.");
        }
        // After a crash/reboot, a committed update is verified instead of being
        // rerun or rejected merely because its version is now already installed.
        var payload = Read<Payload>(Path.Combine(_receipt.State.Source, "installer-progress-payload.json"), 4 * 1024 * 1024);
        if (payload.SchemaVersion != 1 || payload.Files == null || payload.Files.Count < 1 || payload.Files.Count > 10000 ||
            !payload.Files.Any(file => file.Path == "CloudInlet.exe") || payload.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Files.Count)
            throw new IOException("The updated installation payload inventory is invalid.");
        var oldPayload = _receipt.PayloadFiles; var oldFiles = _receipt.OriginalFiles;
        var oldVersion = _receipt.OriginalVersion; var oldUninstaller = _receipt.Uninstaller;
        try
        {
            _receipt.PayloadFiles = payload.Files; _receipt.OriginalVersion = _receipt.Version;
            _receipt.Uninstaller = Path.GetFileName(OwnedUninstaller(_receipt.State.Source, _receipt.Flavor, _receipt.Version));
            VerifyInstalledCore(_receipt.State.Source, requireCanonical: false);
            if (Registration(_receipt.Flavor)?.Brand != _receipt.OriginalBrand) throw new IOException("The update changed the reviewed original registration.");
            _receipt.OriginalFiles = Files(_receipt.State.Source).Select(path => Snapshot(path, path.Substring(_receipt.State.Source.Length + 1))).ToList();
            WriteReceipt(create: false);
        }
        catch
        {
            _receipt.PayloadFiles = oldPayload; _receipt.OriginalFiles = oldFiles;
            _receipt.OriginalVersion = oldVersion; _receipt.Uninstaller = oldUninstaller; throw;
        }
    }
    public bool IsInstalledAt(string directory)
    {
        var registration = Registration(_receipt.Flavor);
        return registration != null && registration.Version == _receipt.Version && registration.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase);
    }
    public bool HasAnyRegistration() => Registration(_receipt.Flavor) != null ||
        ReadRegistry(UninstallKey(_receipt.Flavor), "UninstallString") != null;

    public void VerifyOriginal()
    {
        using var locations = LocationGuards();
        if (DirectoryIdentity(_receipt.State.Source) != _receipt.OriginalDirectoryIdentity)
            throw new IOException("The original installation directory object changed. Migration stopped without touching the replacement.");
        VerifyInstalledCore(_receipt.State.Source, requireCanonical: false);
        if (Registration(_receipt.Flavor)?.Brand != _receipt.OriginalBrand) throw new IOException("The original installation registration changed.");
        if (Path.GetFileName(OwnedUninstaller(_receipt.State.Source, _receipt.Flavor, _receipt.OriginalVersion ?? _receipt.Version)) != _receipt.Uninstaller)
            throw new IOException("The original uninstaller identity changed.");
        foreach (var file in _receipt.OriginalFiles) VerifyFile(Resolve(_receipt.State.Source, file.Path), file);
    }
    public void VerifyDestinationAvailable()
    {
        var destination = _receipt.State.Destination;
        Normal(destination, true);
        if (_receipt.State.RegistrationOnly)
        {
            VerifyOriginalDirectory();
            if (_receipt.State.Phase == "Prepared") VerifyOriginal();
            else VerifyPersonalFiles();
            VerifyRetainedFiles(destination, _receipt.OriginalFiles);
            return;
        }
        if (File.Exists(destination)) throw new IOException("A file occupies the migration destination.");
        if (!Directory.Exists(destination)) return;
        if (_receipt.State.Phase != "InstallingDestination") throw new IOException("The migration destination is no longer empty.");
        if (_receipt.DestinationDirectoryIdentity == null || DirectoryIdentity(destination) != _receipt.DestinationDirectoryIdentity)
            throw new IOException("The destination directory is not the exact object created by this migration. It was left unchanged.");
        // A failed fresh setup may leave files. Only byte-identical expected
        // payload files are acceptable for a retry; unknown files are retained.
        VerifyRetainedFiles(destination, _receipt.PayloadFiles);
    }
    public void UninstallOriginal()
    {
        using var locations = LocationGuards();
        // Keep the original directory object in place while Inno removes owned
        // children. Unknown personal files remain; the empty directory is harmless.
        using var originalDirectory = new AncestorGuards(_receipt.State.Source);
        VerifyRecovery(); VerifyOriginal();
        var uninstaller = OwnedUninstaller(_receipt.State.Source, _receipt.Flavor, _receipt.Version);
        var code = RunNative("Uninstall", uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        if (code != 0) throw new IOException("The original uninstaller needs attention (code " + code + "). Recovery remains available; no new installation was started.");
    }
    public void VerifyOriginalRetired()
    {
        using var locations = LocationGuards();
        using var originalDirectory = _receipt.State.RegistrationOnly ? new AncestorGuards(_receipt.State.Source) : null;
        Normal(_receipt.State.Source, true);
        if (_receipt.State.RegistrationOnly)
        {
            VerifyOriginalDirectory();
            if ((_receipt.State.Phase == "InstallingDestination" || _receipt.State.Phase == "RestoringOriginal" || _receipt.State.Phase == "Completed") &&
                IsInstalledAt(_receipt.State.Source))
            {
                VerifyInstalled(_receipt.State.Source);
                return;
            }
        }
        foreach (var file in _receipt.PayloadFiles)
            if (File.Exists(Resolve(_receipt.State.Source, file.Path))) throw new IOException("An installer-owned original file remains: " + file.Path + ". Close applications or restart Windows, then retry migration.");
        if (Registration(_receipt.Flavor)?.Directory.Equals(_receipt.State.Source, StringComparison.OrdinalIgnoreCase) == true)
            throw new IOException("The original installer registration remains; migration has not been marked complete.");
        if (_receipt.State.RegistrationOnly)
        {
            if (HasAnyRegistration()) throw new IOException("Installer ownership changed before retained files could be recorded.");
            // Inno's final self-delete can outlive the process that returned its
            // exit code. It is owned metadata, never a retained personal file.
            var uninstaller = Resolve(_receipt.State.Source, _receipt.Uninstaller);
            var uninstallLog = Path.ChangeExtension(uninstaller, ".dat");
            var retirement = Stopwatch.StartNew();
            while (File.Exists(uninstaller) || File.Exists(uninstallLog))
            {
                if (retirement.Elapsed > TimeSpan.FromSeconds(20))
                    throw new IOException("The original uninstaller is still finishing. Wait briefly, then retry; recovery has retained the application package.");
                System.Threading.Thread.Sleep(100);
                Normal(uninstaller, false); Normal(uninstallLog, false);
            }
            VerifyRetainedFiles(_receipt.State.Source, _receipt.OriginalFiles);
            if (_receipt.RetainedOriginalFiles == null)
            {
                // The verified native uninstaller is the authority for files it
                // owns, including obsolete bridge components absent from today's
                // payload. Record every survivor before setup can replace files.
                var originals = _receipt.OriginalFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
                var retained = new List<ExeMigrationFile>();
                foreach (var path in Files(_receipt.State.Source))
                {
                    if (!originals.TryGetValue(path.Substring(_receipt.State.Source.Length + 1), out var file))
                        throw new IOException("An unreviewed file appeared after uninstall. Recovery retained it for review.");
                    VerifyFile(path, file); retained.Add(file);
                }
                _receipt.RetainedOriginalFiles = retained;
                try { WriteReceipt(create: false); }
                catch { _receipt.RetainedOriginalFiles = null; throw; }
            }
            VerifyPersonalFiles();
        }
    }
    public void Install(string directory)
    {
        using var locations = LocationGuards();
        VerifyRecovery();
        if (HasAnyRegistration()) throw new IOException("A registered installation prevents safe fresh EXE recovery.");
        if (_receipt.State.Phase == "RestoringOriginal") VerifyRestoreAvailable(); else VerifyDestinationAvailable();
        if (directory.Equals(_receipt.State.Destination, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(directory))
        {
            if (!CreateDirectory(directory, IntPtr.Zero)) throw new IOException("The migration destination could not be exclusively created.");
            _receipt.DestinationDirectoryIdentity = DirectoryIdentity(directory);
            WriteReceipt(create: false); // Exact ownership before any installer file writes.
        }
        else if (directory.Equals(_receipt.State.Source, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(directory))
        {
            if (!CreateDirectory(directory, IntPtr.Zero)) throw new IOException("The original installation location could not be exclusively recreated.");
            _receipt.OriginalDirectoryIdentity = DirectoryIdentity(directory);
            WriteReceipt(create: false);
        }
        using var installDirectory = new AncestorGuards(directory);
        // Pin the leaf before the final identity/content check. A replacement
        // between preflight and this handle must never become an install target.
        var expectedIdentity = directory.Equals(_receipt.State.Source, StringComparison.OrdinalIgnoreCase)
            ? _receipt.OriginalDirectoryIdentity : _receipt.DestinationDirectoryIdentity;
        if (expectedIdentity == null || DirectoryIdentity(directory) != expectedIdentity)
            throw new IOException("The installation directory changed before setup. Its replacement was left untouched.");
        if (_receipt.State.Phase == "RestoringOriginal") VerifyRestoreAvailable(); else VerifyDestinationAvailable();
        var tasks = (_receipt.Startup ? "startup" : "!startup") + "," + (_receipt.Desktop ? "desktopicon" : "!desktopicon");
        var installer = Resolve(_directory, _receipt.Package.Path);
        using var pinned = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifyStream(pinned, _receipt.Package);
        var code = RunNative("Install", installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP- /DIR=" + Quote(directory) + " /TASKS=" + Quote(tasks));
        if (code != 0) throw new IOException("The verified EXE installer did not finish (code " + code + ").");
    }
    public void VerifyInstalled(string directory) => VerifyInstalledCore(directory,
        requireCanonical: directory.Equals(_receipt.State.Destination, StringComparison.OrdinalIgnoreCase));

    private void VerifyInstalledCore(string directory, bool requireCanonical)
    {
        EnsureRestartSatisfied();
        Normal(directory, true);
        var expectedVersion = requireCanonical ? _receipt.Version : _receipt.OriginalVersion ?? _receipt.Version;
        var value = Read<Distribution>(Path.Combine(directory, "distribution.json"), 32768);
        var registration = Registration(_receipt.Flavor);
        if (value.SchemaVersion != 1 || value.Version != expectedVersion || value.InstallerKind != "Exe" || value.Flavor != _receipt.Flavor ||
            value.Architecture != "x64" || value.Scope != "perUser" || !directory.Equals(value.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            registration == null || registration.Version != expectedVersion || !directory.Equals(registration.Directory, StringComparison.OrdinalIgnoreCase) ||
            FileVersionInfo.GetVersionInfo(Path.Combine(directory, "CloudInlet.exe")).ProductVersion?.Split('+')[0] != expectedVersion)
            throw new IOException("The recovered EXE installation identity could not be verified.");
        foreach (var file in _receipt.PayloadFiles) VerifyFile(Resolve(directory, file.Path), file);
        OwnedUninstaller(directory, _receipt.Flavor, expectedVersion);
        if (requireCanonical && Registration(_receipt.Flavor)?.Brand != "CloudInlet")
            throw new IOException("The migrated installation did not use the canonical registry location.");
        if (_receipt.State.RegistrationOnly)
        {
            VerifyOriginalDirectory();
            if (requireCanonical) VerifyPersonalFiles();
        }
        if (StartupEnabled(directory, _receipt.Flavor) != _receipt.Startup || File.Exists(DesktopShortcut(_receipt.Flavor)) != _receipt.Desktop)
            throw new IOException("The recovered startup or desktop shortcut choice changed.");
    }
    public void VerifyRestoreAvailable()
    {
        using var locations = LocationGuards();
        Normal(_receipt.State.Source, true);
        if (_receipt.State.RegistrationOnly) VerifyOriginalDirectory();
        if (_receipt.State.RegistrationOnly && _receipt.RetainedOriginalFiles == null) VerifyOriginalRetired();
        if (_receipt.State.RegistrationOnly) VerifyPersonalFiles();
        if (File.Exists(_receipt.State.Source)) throw new IOException("A file occupies the original installation location.");
        if (Directory.Exists(_receipt.State.Source))
        {
            if (DirectoryIdentity(_receipt.State.Source) != _receipt.OriginalDirectoryIdentity)
                throw new IOException("The original directory was replaced. Recovery will not overwrite the replacement.");
            VerifyRetainedFiles(_receipt.State.Source, _receipt.OriginalFiles);
        }
    }
    private void VerifyOriginalDirectory()
    {
        if (!Directory.Exists(_receipt.State.Source) || DirectoryIdentity(_receipt.State.Source) != _receipt.OriginalDirectoryIdentity)
            throw new IOException("The original installation directory was removed or replaced. Recovery will not recreate or overwrite it.");
    }
    private void VerifyPersonalFiles()
    {
        if (_receipt.RetainedOriginalFiles == null) throw new IOException("The original uninstall has not yet recorded its retained files.");
        foreach (var file in _receipt.RetainedOriginalFiles) VerifyFile(Resolve(_receipt.State.Source, file.Path), file);
    }
    private static void VerifyRetainedFiles(string directory, List<ExeMigrationFile> expected)
    {
        var map = expected.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files(directory))
        {
            var name = file.Substring(directory.Length + 1);
            if (!map.TryGetValue(name, out var record)) throw new IOException("Recovery found an unreviewed file and will not overwrite it: " + name);
            VerifyFile(file, record);
        }
    }
    private string RunName => "CloudInletInstallerRecovery" + _receipt.Flavor;
    private string RecoveryCommand => _receipt.ObserverDirectory == null
        ? Quote(Path.Combine(_directory, "recovery.exe")) + " --resume-exe-migration " + Quote(_path)
        : Quote(Path.Combine(_receipt.ObserverDirectory, "CloudInlet.exe")) + " " + Quote("--exe-migration-progress=" + _path);
    private void ArmRecovery()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new IOException("Windows could not register installation recovery.");
        var previous = key.GetValue(RunName);
        if (previous as string == RecoveryCommand) return;
        if (previous != null) throw new IOException("An earlier EXE migration still needs recovery. It was not replaced.");
        key.SetValue(RunName, RecoveryCommand, RegistryValueKind.String); key.Flush();
    }
    public void DisarmRecovery()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        var previous = key?.GetValue(RunName);
        if (previous == null) return;
        if (previous as string != RecoveryCommand) throw new IOException("The recovery startup entry changed and was retained for review.");
        key!.DeleteValue(RunName); key.Flush();
    }
    public void Dispose() { _lease.Dispose(); _guards.Dispose(); }

    private ExeMigrationReceipt ReadReceipt()
    {
        Normal(_path, false);
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > MaximumReceipt) throw new IOException("The EXE migration receipt is too large.");
        using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var plain = ProtectedData.Unprotect(buffer.ToArray(), Entropy, DataProtectionScope.CurrentUser);
        try { using var memory = new MemoryStream(plain, false); return (ExeMigrationReceipt)new DataContractJsonSerializer(typeof(ExeMigrationReceipt)).ReadObject(memory)!; }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
    private void WriteReceipt(bool create)
    {
        byte[] plain;
        using (var memory = new MemoryStream()) { new DataContractJsonSerializer(typeof(ExeMigrationReceipt)).WriteObject(memory, _receipt); plain = memory.ToArray(); }
        byte[] encrypted;
        try { if (plain.Length > MaximumReceipt - 4096) throw new IOException("The EXE recovery inventory is too large."); encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(plain, 0, plain.Length); }
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
        Normal(_path, false);
        if (create) File.Move(temporary, _path); else File.Replace(temporary, _path, null);
    }
    private void RecordFailure(Exception error)
    {
        // A failed attempt remains visible across worker/app/Windows restarts.
        // Failure recording never changes a completed item's phase.
        _receipt.Failure = error.Message.Length <= 16384 ? error.Message : error.Message.Substring(0, 16384);
        _receipt.FailureRequiresRestart = error is ExeInstallationMigration.PauseException;
        try { WriteReceipt(create: false); }
        catch (Exception journalError) { Console.Error.WriteLine("Recovery status could not be saved: " + journalError.Message); }
    }
    private void EnsureObserverReady()
    {
        // Null is reserved for the isolated native installer fixture. Production
        // handoffs supply a verified full WinUI runtime before relocation is enabled.
        if (_receipt.ObserverDirectory == null) return;
        if (_receipt.IsObserverReady()) return;
        var executable = Path.Combine(_receipt.ObserverDirectory, "CloudInlet.exe");
        _receipt.VerifyObserver(executable);
        using var process = Process.Start(new ProcessStartInfo(executable, Quote("--exe-migration-progress=" + _path))
        { UseShellExecute = false, WorkingDirectory = _receipt.ObserverDirectory }) ?? throw new IOException("The installation recovery window could not open.");
        var timeout = Stopwatch.StartNew();
        while (!_receipt.IsObserverReady())
        {
            if (process.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("The installation recovery window did not become ready. The updated original installation was retained; reopen recovery to retry.");
            System.Threading.Thread.Sleep(100);
        }
    }
    private static void AttachObserver(ExeMigrationReceipt receipt, string executable)
    {
        executable = Path.GetFullPath(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var path = Path.Combine(directory, ".runtime.dpapi"); Normal(path, false);
        byte[] encrypted;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length < 1 || input.Length > MaximumReceipt) throw new IOException("The migration observer ownership receipt is invalid.");
            using var buffer = new MemoryStream(); input.CopyTo(buffer); encrypted = buffer.ToArray();
        }
        var plain = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("CloudInlet.InstallerProgressRuntime.v1"), DataProtectionScope.CurrentUser);
        try
        {
            using var input = new MemoryStream(plain, false);
            var staged = (StagedObserver)new DataContractJsonSerializer(typeof(StagedObserver)).ReadObject(input)!;
            if (staged.Directory != directory || Path.GetFileName(directory) != "progress-" + staged.Id || staged.Payload.SchemaVersion != 1)
                throw new IOException("The staged migration observer identity changed.");
            receipt.ObserverDirectory = directory; receipt.ObserverFiles = staged.Payload.Files;
            receipt.Validate(Path.Combine(RecoveryRoot(receipt.Flavor), receipt.Id, "migration.dpapi"));
            receipt.VerifyObserver(executable);
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
    private static string OwnedUninstaller(string directory, string flavor, string version)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey(flavor)) ?? throw new IOException("The Inno uninstaller registration is missing.");
        var command = key.GetValue("UninstallString") as string;
        var location = key.GetValue("InstallLocation") as string;
        if (command == null || location == null || key.GetValue("DisplayVersion") as string != version ||
            !InstallationMigrationPolicy.Normalize(location).Equals(directory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The registered Inno uninstaller does not own this exact installation.");
        var match = Regex.Match(command, "^\"(?<path>[^\"]+)\"$");
        if (!match.Success) throw new IOException("The registered uninstall command contains unsupported arguments.");
        var path = InstallationMigrationPolicy.Normalize(match.Groups["path"].Value);
        if (!Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(path), "^unins[0-9]{3}\\.exe$", RegexOptions.IgnoreCase))
            throw new IOException("The registered uninstaller is outside this installation.");
        Normal(path, false); Normal(Path.ChangeExtension(path, ".dat"), false);
        if (!File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".dat"))) throw new IOException("The original uninstall ownership log is missing.");
        return path;
    }
    private static string UninstallKey(string flavor) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" +
        (flavor == "Debug" ? "{061831F7-6C43-4835-AB26-E4AA777BB4F8}" : "{45F4331D-E3BB-4F94-A3F7-CA225B16A8B5}") + "_is1";
    private static DistributionRegistration? Registration(string flavor) => DistributionRegistration.Read(flavor, "Exe", ReadRegistry);
    private static string? ReadRegistry(string key, string name) { using var value = Registry.CurrentUser.OpenSubKey(key); return value?.GetValue(name) as string; }
    private static string DesktopShortcut(string flavor) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), flavor == "Debug" ? "CloudInlet Debug.lnk" : "CloudInlet.lnk");
    private static bool StartupEnabled(string directory, string flavor)
    {
        var value = ReadRegistry(RunKey, flavor == "Debug" ? "CloudInletDebug" : "CloudInlet");
        if (value == null) return false;
        if (!value.Equals(Quote(Path.Combine(directory, "CloudInlet.exe")) + " --background", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The startup command does not point at the reviewed installation.");
        return true;
    }
    private static int Run(string executable, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = workingDirectory })
            ?? throw new IOException("Windows could not start the installation recovery step.");
        process.WaitForExit(); return process.ExitCode; // Native transactions are never killed on a timer.
    }
    private void EnsureRestartSatisfied()
    {
        if (_receipt.NativeAttempt != null)
            _receipt.NativeAttempt.EnsureRestartSatisfied(InstallerMigrationRecovery.CurrentBootIdentity());
    }
    private int RunNative(string kind, string executable, string arguments)
    {
        EnsureRestartSatisfied();
        var previous = _receipt.NativeAttempt;
        _receipt.NativeAttempt = new ExeNativeAttempt { Kind = kind, BootIdentity = InstallerMigrationRecovery.CurrentBootIdentity() };
        try { WriteReceipt(create: false); }
        catch { _receipt.NativeAttempt = previous; throw; } // Nothing native was launched.
        int code;
        try { code = Run(executable, arguments, _directory); }
        catch (Exception error)
        { throw new ExeInstallationMigration.PauseException("The native installer outcome could not be confirmed. Restart Windows before retrying installation recovery.", error); }
        _receipt.NativeAttempt.ExitCode = code;
        try { WriteReceipt(create: false); }
        catch (Exception error)
        {
            _receipt.NativeAttempt.ExitCode = null;
            throw new ExeInstallationMigration.PauseException("The native installer outcome could not be saved. Recovery is retained; restart Windows before retrying.", error);
        }
        EnsureRestartSatisfied();
        return code;
    }
    private static T Read<T>(string path, int limit)
    {
        Normal(path, false); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > limit) throw new IOException("The installation metadata has an invalid size.");
        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input)!;
    }
    private static ExeMigrationFile Snapshot(string file, string relative)
    { Normal(file, false); using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); using var sha = SHA256.Create(); return new ExeMigrationFile { Path = relative, Size = input.Length, Sha256 = Hex(sha.ComputeHash(input)) }; }
    private static void CopyVerified(string source, string destination, ExeMigrationFile record)
    {
        Normal(source, false); using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifyStream(input, record); input.Position = 0;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.WriteThrough);
        input.CopyTo(output); output.Flush(true); output.Position = 0; VerifyStream(output, record);
    }
    private static void VerifyFile(string path, ExeMigrationFile record)
    { Normal(path, false); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); VerifyStream(input, record); }
    private static void VerifyStream(Stream input, ExeMigrationFile record)
    {
        if (record.Size < 0 || record.Size > 1024L * 1024 * 1024 || !Regex.IsMatch(record.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || input.Length != record.Size)
            throw new IOException("An installation recovery file has changed size or identity.");
        using var sha = SHA256.Create();
        if (!Hex(sha.ComputeHash(input)).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("An installation recovery file failed its SHA-256 check.");
    }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('"') >= 0 || relative.Split('\\', '/').Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")))
            throw new IOException("An installation inventory path is invalid.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("An installation inventory path escaped its owner.");
        Normal(path, false); return path;
    }
    private static IEnumerable<string> Files(string directory)
    {
        Normal(directory, true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Installation migration cannot follow linked files.");
            if ((attributes & FileAttributes.Directory) != 0) { foreach (var file in Files(entry)) yield return file; } else yield return entry;
        }
    }
    private static void Normal(string path, bool directory) => UpdateCacheLocation.EnsureNormalPath(path, directory);
    private static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";

    private AncestorGuards LocationGuards() => new(Path.GetDirectoryName(_receipt.State.Source)!, Path.GetDirectoryName(_receipt.State.Destination)!);
    private static string DirectoryIdentity(string path)
    {
        Normal(path, true);
        using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0x10)
            throw new IOException("The installation directory identity could not be read.");
        return info.Volume.ToString("x8") + ":" + (((ulong)info.IndexHigh << 32) | info.IndexLow).ToString("x16");
    }
    private sealed class AncestorGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _held = new();
        internal AncestorGuards(params string[] locations)
        {
            try
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var location in locations)
                    for (var path = Path.GetFullPath(location); path != null; path = Path.GetDirectoryName(path)) paths.Add(path);
                foreach (var path in paths.OrderBy(x => x.Length))
                {
                    Normal(path, true);
                    // FILE_LIST_DIRECTORY + READ_ATTRIBUTES is necessary: an
                    // attribute-only handle does not prevent ancestor renames.
                    var handle = CreateFile(path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0x10)
                    { handle.Dispose(); throw new IOException("An installation recovery parent cannot be pinned safely."); }
                    _held.Add(handle);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = _held.Count - 1; i >= 0; i--) _held[i].Dispose(); _held.Clear(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
}

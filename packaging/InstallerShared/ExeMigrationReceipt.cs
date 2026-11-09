using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudInlet.Packaging;

// The UI reads this authenticated projection; it does not treat a plain status
// file or a process exit as permission to reinstall an application.
[DataContract]
internal sealed class ExeMigrationFile
{
    [DataMember(IsRequired = true)] public string Path { get; set; } = "";
    [DataMember(IsRequired = true)] public long Size { get; set; }
    [DataMember(IsRequired = true)] public string Sha256 { get; set; } = "";
}

[DataContract]
internal sealed class ExeNativeAttempt
{
    [DataMember(IsRequired = true)] public string Kind { get; set; } = "";
    [DataMember(IsRequired = true)] public string BootIdentity { get; set; } = "";
    [DataMember] public int BootIdentityVersion { get; set; } = InstallerMigrationRecovery.CurrentBootIdentityVersion;
    [DataMember] public int? ExitCode { get; set; }

    internal void Validate()
    {
        if ((Kind != "Update" && Kind != "Uninstall" && Kind != "Install") ||
            !Guid.TryParseExact(BootIdentity, "D", out var boot) || boot == Guid.Empty ||
            (BootIdentityVersion != 0 && BootIdentityVersion != InstallerMigrationRecovery.CurrentBootIdentityVersion))
            throw new IOException("The native installer recovery record is invalid.");
    }
    internal bool RequiresRestart(string currentBoot)
    {
        Validate();
        if (!Guid.TryParseExact(currentBoot, "D", out var boot) || boot == Guid.Empty)
            throw new IOException("Windows boot identity could not be verified.");
        if (ExitCode == null || ExitCode == 3010)
            InstallerMigrationRecovery.RequireCurrentBootIdentityVersion(BootIdentityVersion);
        return (ExitCode == null || ExitCode == 3010) && Guid.Parse(BootIdentity) == boot;
    }
    internal void EnsureRestartSatisfied(string currentBoot)
    {
        if (RequiresRestart(currentBoot)) throw new ExeInstallationMigration.PauseException(ExitCode == 3010
            ? "Restart Windows to finish the installer operation, then reopen installation recovery. Migration has not been marked complete."
            : "The last native installer operation has no confirmed outcome. Restart Windows before retrying so recovery cannot overlap an unfinished installer.");
    }
}

[DataContract]
internal sealed class ExeMigrationReceipt
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudInlet.ExeInstallationMigration.v1");
    [DataMember(IsRequired = true)] public int SchemaVersion { get; set; } = 1;
    [DataMember(IsRequired = true)] public string Id { get; set; } = "";
    [DataMember(IsRequired = true)] public string RequestHash { get; set; } = "";
    [DataMember(IsRequired = true)] public string Flavor { get; set; } = "";
    [DataMember(IsRequired = true)] public string Version { get; set; } = "";
    [DataMember] public string? OriginalVersion { get; set; }
    [DataMember] public ExeNativeAttempt? NativeAttempt { get; set; }
    [DataMember(IsRequired = true)] public bool Startup { get; set; }
    [DataMember(IsRequired = true)] public bool Desktop { get; set; }
    [DataMember(IsRequired = true)] public string OriginalBrand { get; set; } = "";
    [DataMember(IsRequired = true)] public string Uninstaller { get; set; } = "";
    [DataMember(IsRequired = true)] public ExeMigrationFile Package { get; set; } = new();
    [DataMember(IsRequired = true)] public ExeMigrationFile Helper { get; set; } = new();
    [DataMember] public ExeMigrationFile? HelperConfig { get; set; }
    [DataMember(IsRequired = true)] public string OriginalDirectoryIdentity { get; set; } = "";
    [DataMember] public string? DestinationDirectoryIdentity { get; set; }
    [DataMember] public string? ObserverDirectory { get; set; }
    [DataMember] public List<ExeMigrationFile>? ObserverFiles { get; set; }
    [DataMember] public string? Failure { get; set; }
    [DataMember] public bool FailureRequiresRestart { get; set; }
    [DataMember(IsRequired = true)] public List<ExeMigrationFile> OriginalFiles { get; set; } = new();
    [DataMember] public List<ExeMigrationFile>? RetainedOriginalFiles { get; set; }
    [DataMember(IsRequired = true)] public List<ExeMigrationFile> PayloadFiles { get; set; } = new();
    [DataMember(IsRequired = true)] public ExeInstallationMigration.State State { get; set; } = new();

    internal static string Root(string flavor)
    {
        if (flavor != "Debug" && flavor != "Release") throw new IOException("The EXE recovery build is invalid.");
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudInlet", "Migration", flavor, "ExeInstallation");
    }

    internal static void EnsureNoPending(string flavor)
    {
        foreach (var receipt in Pending(flavor))
            throw new IOException("An unfinished installation migration needs attention before another update or migration can start. Reopen installation recovery to retry or restore the original location.");
    }

    internal static ExeMigrationReceipt? FindPending(string flavor)
    {
        ExeMigrationReceipt? found = null;
        foreach (var receipt in Pending(flavor))
        {
            if (found != null) throw new IOException("More than one installation migration needs recovery. Their records were preserved for review.");
            found = receipt;
        }
        return found;
    }

    internal static bool ReferencesObserver(string directory, string flavor)
    {
        directory = System.IO.Path.GetFullPath(directory).TrimEnd('\\');
        // Enumerating every receipt also refuses corrupt recovery evidence. A
        // failed read can never be interpreted as permission to delete a runtime.
        var retained = false;
        foreach (var receipt in Pending(flavor))
            retained |= string.Equals(receipt.ObserverDirectory?.TrimEnd('\\'), directory, StringComparison.OrdinalIgnoreCase);
        return retained;
    }

    private static IEnumerable<ExeMigrationReceipt> Pending(string flavor)
    {
        var root = Root(flavor);
        UpdateCacheLocation.EnsureNormalPath(root, true);
        if (File.Exists(root)) throw new IOException("The installation recovery directory has been replaced by a file.");
        if (!Directory.Exists(root)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (!Regex.IsMatch(System.IO.Path.GetFileName(entry), "^[a-f0-9]{32}$")) continue;
            UpdateCacheLocation.EnsureNormalPath(entry, true);
            if (!Directory.Exists(entry)) throw new IOException("An installation recovery directory is invalid.");
            ExeMigrationReceipt receipt;
            try { receipt = Inspect(System.IO.Path.Combine(entry, "migration.dpapi")); }
            catch (Exception error) when (error is IOException || error is CryptographicException || error is SerializationException)
            { throw new IOException("Installation recovery evidence could not be read. Its files were retained for recovery.", error); }
            if (receipt.State.Phase != "Completed" && receipt.State.Phase != "Reverted") yield return receipt;
        }
    }

    internal static ExeMigrationReceipt Inspect(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        UpdateCacheLocation.EnsureNormalPath(path, false);
        var encrypted = ReadSnapshot(path, MaximumBytes);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var input = new MemoryStream(plain, false);
            var receipt = (ExeMigrationReceipt?)new DataContractJsonSerializer(typeof(ExeMigrationReceipt)).ReadObject(input)
                ?? throw new IOException("The EXE migration receipt is empty.");
            receipt.Validate(path);
            return receipt;
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    internal static byte[] ReadSnapshot(string path, int maximumBytes)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            UpdateCacheLocation.EnsureNormalPath(path, false);
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (input.Length < 1 || input.Length > maximumBytes) throw new IOException("The EXE migration receipt has an invalid size.");
                using var bytes = new MemoryStream(); input.CopyTo(bytes); return bytes.ToArray();
            }
            catch (IOException error) when (((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33) &&
                wait.Elapsed < TimeSpan.FromSeconds(1))
            {
                // Atomic publication can briefly deny a new reader. Keep observing
                // rather than permanently abandoning the recovery window. This
                // retries only a bounded read, never a native installer operation.
                System.Threading.Thread.Sleep(25);
            }
        }
    }

    internal void Validate(string path)
    {
        ExeInstallationMigration.Validate(State);
        NativeAttempt?.Validate();
        if (SchemaVersion != 1 || !Regex.IsMatch(Id ?? "", "^[a-f0-9]{32}$") ||
            !Regex.IsMatch(RequestHash ?? "", "^[a-f0-9]{64}$") || !System.Version.TryParse(Version, out _) ||
            OriginalVersion != null && (!System.Version.TryParse(OriginalVersion, out var originalVersion) || originalVersion > System.Version.Parse(Version)) ||
            State.Phase != "UpdatingOriginal" && OriginalVersion != null && OriginalVersion != Version ||
            OriginalBrand != "CloudBay" && OriginalBrand != "CloudInlet" ||
            State.RegistrationOnly && (OriginalBrand != "CloudBay" || DestinationDirectoryIdentity != OriginalDirectoryIdentity) ||
            Package == null || Helper == null || Package.Path != "installer.exe" || Helper.Path != "recovery.exe" ||
            HelperConfig != null && HelperConfig.Path != "recovery.exe.config" ||
            !Regex.IsMatch(OriginalDirectoryIdentity ?? "", "^[a-f0-9]{8}:[a-f0-9]{16}$") ||
            DestinationDirectoryIdentity != null && !Regex.IsMatch(DestinationDirectoryIdentity, "^[a-f0-9]{8}:[a-f0-9]{16}$") ||
            !Regex.IsMatch(Uninstaller ?? "", "^unins[0-9]{3}\\.exe$", RegexOptions.IgnoreCase) ||
            PayloadFiles == null || PayloadFiles.Count < 1 || PayloadFiles.Count > 10000 || OriginalFiles == null || OriginalFiles.Count > 20000 ||
            RetainedOriginalFiles != null && (!State.RegistrationOnly || RetainedOriginalFiles.Count > 20000) ||
            State.RegistrationOnly && RetainedOriginalFiles == null && (State.Phase == "InstallingDestination" || State.Phase == "Completed") ||
            Failure?.Length > 16384 || !string.Equals(InstallationMigrationPolicy.Target(State.Source, Flavor), State.Destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The authenticated EXE migration receipt is invalid.");
        if (!System.IO.Path.GetFullPath(path).Equals(System.IO.Path.Combine(Root(Flavor), Id!, "migration.dpapi"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The EXE migration receipt is outside its private location.");
        var all = PayloadFiles.Concat(OriginalFiles).Concat(new[] { Package, Helper }).Concat(HelperConfig == null ? Array.Empty<ExeMigrationFile>() : new[] { HelperConfig });
        foreach (var file in all) ValidateFile(file);
        if (PayloadFiles.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != PayloadFiles.Count ||
            OriginalFiles.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != OriginalFiles.Count)
            throw new IOException("The EXE migration inventory contains duplicate paths.");
        if (RetainedOriginalFiles != null)
        {
            var originals = OriginalFiles.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in RetainedOriginalFiles)
            {
                ValidateFile(file);
                if (!retained.Add(file.Path) || !originals.TryGetValue(file.Path, out var original) ||
                    original.Size != file.Size || !original.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The retained EXE files do not match the original verified inventory.");
            }
        }
        if ((ObserverDirectory == null) != (ObserverFiles == null)) throw new IOException("The EXE migration observer identity is incomplete.");
        if (ObserverDirectory != null)
        {
            var observerRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudInlet", "Migration", Flavor, "InstallerProgress", "host");
            var name = System.IO.Path.GetFileName(ObserverDirectory);
            if (!Regex.IsMatch(name, "^progress-[a-f0-9]{32}$") ||
                !ObserverDirectory.Equals(System.IO.Path.Combine(observerRoot, name), StringComparison.OrdinalIgnoreCase) ||
                ObserverFiles!.Count < 1 || ObserverFiles.Count > 10000 || !ObserverFiles.Any(x => x.Path == "CloudInlet.exe"))
                throw new IOException("The EXE migration observer is outside its verified runtime location.");
            foreach (var file in ObserverFiles!) ValidateFile(file);
        }
    }

    internal string HelperPath => System.IO.Path.Combine(Root(Flavor), Id, Helper.Path);

    internal bool IsActive()
    {
        var path = System.IO.Path.Combine(Root(Flavor), Id, "migration.dpapi.lock");
        UpdateCacheLocation.EnsureNormalPath(path, false);
        try { using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException error) when ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33) { return true; }
    }

    [DataContract] private sealed class ObserverReady
    {
        [DataMember(IsRequired = true)] public string Id { get; set; } = "";
        [DataMember(IsRequired = true)] public string Hash { get; set; } = "";
        [DataMember(IsRequired = true)] public int ProcessId { get; set; }
        [DataMember(IsRequired = true)] public long StartTicks { get; set; }
    }
    private static readonly byte[] ReadyEntropy = Encoding.UTF8.GetBytes("CloudInlet.ExeInstallationMigration.ObserverReady.v1");

    internal void PublishObserverReady()
    {
        using var process = Process.GetCurrentProcess();
        VerifyObserver(process.MainModule?.FileName ?? throw new IOException("The observer executable could not be identified."));
        var path = System.IO.Path.Combine(Root(Flavor), Id, "observer-ready.dpapi");
        UpdateCacheLocation.EnsureNormalPath(path, false);
        var ready = new ObserverReady { Id = Id, Hash = RequestHash, ProcessId = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks };
        byte[] encrypted;
        using (var memory = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(ObserverReady)).WriteObject(memory, ready);
            var bytes = memory.ToArray();
            try { encrypted = ProtectedData.Protect(bytes, ReadyEntropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
        UpdateCacheLocation.EnsureNormalPath(path, false);
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }

    internal bool IsObserverReady()
    {
        if (ObserverDirectory == null) return false;
        var path = System.IO.Path.Combine(Root(Flavor), Id, "observer-ready.dpapi");
        UpdateCacheLocation.EnsureNormalPath(path, false);
        byte[] encrypted;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length < 1 || input.Length > 16384) throw new IOException("The EXE migration window acknowledgement is invalid.");
            using var memory = new MemoryStream(); input.CopyTo(memory); encrypted = memory.ToArray();
        }
        catch (FileNotFoundException) { return false; }
        var plain = ProtectedData.Unprotect(encrypted, ReadyEntropy, DataProtectionScope.CurrentUser);
        try
        {
            using var memory = new MemoryStream(plain, false);
            var ready = (ObserverReady)new DataContractJsonSerializer(typeof(ObserverReady)).ReadObject(memory)!;
            if (ready.Id != Id || ready.Hash != RequestHash || ready.ProcessId <= 0 || ready.StartTicks <= 0) throw new IOException("The EXE migration window acknowledgement does not match this request.");
            try
            {
                using var process = Process.GetProcessById(ready.ProcessId);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ready.StartTicks &&
                    string.Equals(process.MainModule?.FileName, System.IO.Path.Combine(ObserverDirectory, "CloudInlet.exe"), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    internal void VerifyObserver(string executable)
    {
        if (ObserverDirectory == null || ObserverFiles == null || !System.IO.Path.GetFullPath(executable).Equals(System.IO.Path.Combine(ObserverDirectory, "CloudInlet.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("This window is not the authenticated migration observer.");
        foreach (var file in ObserverFiles) VerifyFile(System.IO.Path.Combine(ObserverDirectory, file.Path), file);
    }

    internal string VerifyCompletedApplication()
    {
        if (State.Phase != "Completed" && State.Phase != "Reverted") throw new IOException("The installation is not yet verified complete.");
        if (NativeAttempt != null) NativeAttempt.EnsureRestartSatisfied(InstallerMigrationRecovery.CurrentBootIdentity());
        var root = State.Phase == "Completed" ? State.Destination : State.Source;
        foreach (var file in PayloadFiles) VerifyFile(System.IO.Path.Combine(root, file.Path), file);
        return System.IO.Path.Combine(root, "CloudInlet.exe");
    }

    internal IDisposable OpenVerifiedHelper()
    {
        var pinned = new HeldFiles();
        try
        {
            if (HelperConfig != null) pinned.Add(System.IO.Path.Combine(Root(Flavor), Id, HelperConfig.Path), HelperConfig);
            else if (File.Exists(HelperPath + ".config")) throw new IOException("An unreviewed recovery configuration appeared.");
            pinned.Add(HelperPath, Helper);
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }
    private sealed class HeldFiles : IDisposable
    {
        private readonly List<FileStream> _files = new();
        internal void Add(string path, ExeMigrationFile file)
        {
            UpdateCacheLocation.EnsureNormalPath(path, false);
            var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { VerifyStream(input, file); _files.Add(input); }
            catch { input.Dispose(); throw; }
        }
        public void Dispose() { foreach (var file in _files) file.Dispose(); _files.Clear(); }
    }
    internal static void ValidateFile(ExeMigrationFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.Path) || System.IO.Path.IsPathRooted(file.Path) || file.Path.IndexOf(':') >= 0 || file.Path.IndexOf('"') >= 0 ||
            file.Path.Split('\\', '/').Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")) ||
            file.Size < 0 || file.Size > 1024L * 1024 * 1024 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new IOException("An EXE migration inventory file is invalid.");
    }
    internal static void VerifyFile(string path, ExeMigrationFile file)
    {
        UpdateCacheLocation.EnsureNormalPath(path, false);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); VerifyStream(input, file);
    }
    internal static void VerifyStream(Stream input, ExeMigrationFile file)
    {
        ValidateFile(file);
        if (input.Length != file.Size) throw new IOException("An EXE recovery file changed size.");
        using var sha = SHA256.Create();
        var actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("An EXE recovery file failed its SHA-256 check.");
    }
}

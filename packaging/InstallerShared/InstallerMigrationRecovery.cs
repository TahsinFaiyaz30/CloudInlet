using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CloudInlet.Packaging;

/// <summary>
/// Durable installer-owned migration evidence. This lives outside Client and the
/// download cache. A UI result is never authority to finish, discard or rerun it.
/// No method here deletes old files or runs an installer.
/// </summary>
internal sealed class InstallerMigrationRecovery : IDisposable
{
    internal const int CurrentBootIdentityVersion = 2;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudInlet.InstallationMigrationRecovery.v1");
    private const int MaximumReceiptBytes = 16 * 1024 * 1024;
    private readonly string _path;
    private readonly string _flavor;
    private readonly FileStream _lease;
    private readonly ParentGuards _guards;
    private readonly Func<string> _boot;
    private readonly Action<Receipt> _verifyInstalled;
    private readonly Action<Receipt> _verifyOriginal;

    [DataContract] internal sealed class FileProof
    {
        [DataMember(IsRequired = true)] public string Path { get; set; } = "";
        [DataMember(IsRequired = true)] public long Size { get; set; }
        [DataMember(IsRequired = true)] public string Sha256 { get; set; } = "";
    }

    [DataContract] internal sealed class Receipt
    {
        [DataMember(IsRequired = true)] public int SchemaVersion { get; set; } = 1;
        [DataMember(IsRequired = true)] public string RequestHash { get; set; } = "";
        [DataMember(IsRequired = true)] public string Source { get; set; } = "";
        [DataMember(IsRequired = true)] public string Destination { get; set; } = "";
        [DataMember(IsRequired = true)] public string SourceIdentity { get; set; } = "";
        [DataMember(IsRequired = true)] public string DestinationIdentity { get; set; } = "";
        [DataMember(IsRequired = true)] public string BuildFlavor { get; set; } = "";
        [DataMember(IsRequired = true)] public string InstalledVersion { get; set; } = "";
        [DataMember(IsRequired = true)] public string TargetVersion { get; set; } = "";
        [DataMember(IsRequired = true)] public string OriginalBrand { get; set; } = "";
        [DataMember(IsRequired = true)] public string PackageSha256 { get; set; } = "";
        [DataMember(IsRequired = true)] public long PackageSize { get; set; }
        [DataMember(IsRequired = true)] public string BootIdentity { get; set; } = "";
        [DataMember] public int BootIdentityVersion { get; set; } = CurrentBootIdentityVersion;
        [DataMember(IsRequired = true)] public string State { get; set; } = "Prepared";
        [DataMember(IsRequired = true)] public int ExitCode { get; set; } = -1;
        [DataMember(IsRequired = true)] public List<FileProof> OriginalFiles { get; set; } = new();
        [DataMember(IsRequired = true)] public List<FileProof> InstalledFiles { get; set; } = new();
        [DataMember(IsRequired = true)] public List<string> RetiredFiles { get; set; } = new();
    }

    internal sealed class Outcome
    {
        internal bool Pending { get; }
        internal bool Completed { get; }
        internal bool Reverted { get; }
        internal string? Message { get; }
        internal string? TargetVersion { get; }
        internal string? InstallDirectory { get; }
        internal string? OriginalDirectory { get; }
        internal Outcome(bool pending, bool completed, bool reverted, string? message, Receipt? receipt = null)
        { Pending = pending; Completed = completed; Reverted = reverted; Message = message; TargetVersion = receipt?.TargetVersion;
            InstallDirectory = reverted ? receipt?.Source : receipt?.Destination; OriginalDirectory = receipt?.Source; }
    }

    internal static string Root(string flavor, string? localAppData = null)
    {
        ValidateFlavor(flavor);
        return Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudInlet", "Migration", flavor, "InstallerRecovery");
    }

    internal InstallerMigrationRecovery(string flavor, string? root = null, Func<string>? bootIdentity = null,
        Action<Receipt>? verifyInstalled = null, Action<Receipt>? verifyOriginal = null)
    {
        ValidateFlavor(flavor);
        _flavor = flavor;
        var directory = InstallationMigrationPolicy.Normalize(root ?? Root(flavor));
        UpdateCacheLocation.EnsureNormalPath(directory, true);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "installation.dpapi");
        _guards = new ParentGuards(_path);
        try
        {
            UpdateCacheLocation.EnsureNormalPath(_path + ".lock", false);
            _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch { _guards.Dispose(); throw; }
        _boot = bootIdentity ?? CurrentBootIdentity;
        _verifyInstalled = verifyInstalled ?? VerifyInstalled;
        _verifyOriginal = verifyOriginal ?? VerifyOriginal;
    }

    internal void EnsureNoPending()
    {
        var receipt = Read();
        if (receipt != null && receipt.State != "Complete" && receipt.State != "RolledBack")
            throw new IOException("The previous installation migration still needs verification. Restart Windows and open CloudInlet before installing another update or deleting recovery files.");
    }

    internal void Begin(string requestHash, string source, string destination, string installedVersion,
        string targetVersion, string originalBrand, string packageSha256, long packageSize)
    {
        EnsureNoPending();
        var receipt = new Receipt
        {
            RequestHash = requestHash, Source = InstallationMigrationPolicy.Normalize(source),
            Destination = InstallationMigrationPolicy.Validate(source, destination, _flavor, "Msi", installedVersion, targetVersion),
            BuildFlavor = _flavor, InstalledVersion = installedVersion, TargetVersion = targetVersion,
            OriginalBrand = originalBrand, PackageSha256 = packageSha256.ToLowerInvariant(), PackageSize = packageSize,
            BootIdentity = _boot(), SourceIdentity = DirectoryIdentity(source), OriginalFiles = Snapshot(source)
        };
        _verifyOriginal(receipt);
        Validate(receipt);
        // Capture old packaged paths as well as new packaged paths later. Unknown
        // personal files are preserved and never treated as installer components.
        receipt.RetiredFiles = ReadOwnedPaths(source);
        Publish(receipt);
    }

    internal void RecordInstallerExit(string requestHash, int exitCode)
    {
        var receipt = Require(requestHash);
        if (receipt.State != "Prepared") throw new IOException("The installer outcome has already been recorded.");
        receipt.ExitCode = exitCode;
        receipt.State = "InstallerReturned";
        // This write precedes any optional progress or installed-version checks.
        // An unsuccessful check cannot erase the fact Windows requested reboot.
        Publish(receipt);
    }

    internal void RecordVerifiedInstallation(string requestHash)
    {
        var receipt = Require(requestHash);
        if (receipt.State != "InstallerReturned" || (receipt.ExitCode != 0 && receipt.ExitCode != 3010))
            throw new IOException("The installation has no successful recorded installer outcome.");
        _verifyInstalled(receipt);
        receipt.DestinationIdentity = DirectoryIdentity(receipt.Destination);
        receipt.InstalledFiles = Snapshot(receipt.Destination);
        receipt.RetiredFiles = receipt.RetiredFiles.Concat(receipt.InstalledFiles.Select(x => x.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        receipt.State = "VerifiedInstallation";
        Publish(receipt);
    }

    internal Outcome Resolve()
    {
        var receipt = Read();
        if (receipt == null) return new Outcome(false, false, false, null);
        if (receipt.State == "Complete") return new Outcome(false, true, false, null, receipt);
        if (receipt.State == "RolledBack") return new Outcome(false, false, true, null, receipt);
        try
        {
            if (receipt.State == "Prepared")
            {
                // A crash can happen before launching Windows Installer. Only
                // byte-identical old state with no destination can be released.
                VerifyUntouchedOriginal(receipt);
                receipt.State = "RolledBack";
                Publish(receipt);
                return new Outcome(false, false, true, "The interrupted installation made no verified changes. Your original installation is intact; retry the update when ready.", receipt);
            }
            if (receipt.ExitCode != 0 && receipt.ExitCode != 3010)
            {
                VerifyUntouchedOriginal(receipt);
                receipt.State = "RolledBack";
                Publish(receipt);
                return new Outcome(false, false, true, "Windows Installer restored the original installation. Retry the update when ready.", receipt);
            }
            if (receipt.ExitCode == 3010)
            {
                RequireCurrentBootIdentityVersion(receipt.BootIdentityVersion);
                var currentBoot = _boot();
                if (!Guid.TryParseExact(currentBoot, "D", out var currentId) || currentId == Guid.Empty) throw BootReadError();
                if (currentId == Guid.Parse(receipt.BootIdentity))
                    return new Outcome(true, false, false, "Restart Windows to finish installation migration. The selected change is still pending; completed migrations remain intact.", receipt);
            }
            _verifyInstalled(receipt);
            // If the worker was interrupted after Windows Installer returned,
            // collect evidence only after independently verifying its registration,
            // target version and descriptor. Never infer success from result JSON.
            if (receipt.State == "InstallerReturned") RecordVerifiedInstallation(receipt.RequestHash);
            receipt = Require(receipt.RequestHash);
            if (DirectoryIdentity(receipt.Destination) != receipt.DestinationIdentity)
                throw new IOException("The new installation directory was replaced.");
            VerifySnapshot(receipt.Destination, receipt.InstalledFiles, exact: true);
            VerifyRetirement(receipt);
            receipt.State = "Complete";
            Publish(receipt);
            return new Outcome(false, true, false, "Installation migration was verified successfully. The selected installation folder and registration are ready.", receipt);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is CryptographicException)
        {
            return new Outcome(true, false, false, "Installation migration needs attention: " + error.Message + " Recovery evidence was retained. Repair the installation and reopen CloudInlet to retry verification.", receipt);
        }
    }

    private void VerifyUntouchedOriginal(Receipt receipt)
    {
        if (DirectoryIdentity(receipt.Source) != receipt.SourceIdentity)
            throw new IOException("The original installation directory no longer matches the saved identity.");
        _verifyOriginal(receipt);
        VerifySnapshot(receipt.Source, receipt.OriginalFiles, exact: true);
        if (!SamePath(receipt.Source, receipt.Destination))
        {
            UpdateCacheLocation.EnsureNormalPath(receipt.Destination, true);
            if (Directory.Exists(receipt.Destination) || File.Exists(receipt.Destination))
                throw new IOException("A destination exists after the interrupted update; its outcome must be verified before continuing.");
        }
    }

    private static void VerifyRetirement(Receipt receipt)
    {
        if (SamePath(receipt.Source, receipt.Destination)) return;
        UpdateCacheLocation.EnsureNormalPath(receipt.Source, true);
        if (!Directory.Exists(receipt.Source)) return;
        if (DirectoryIdentity(receipt.Source) != receipt.SourceIdentity)
            throw new IOException("The former installation directory was replaced; it cannot be treated as retired.");
        foreach (var relative in receipt.RetiredFiles)
        {
            var path = Path.Combine(receipt.Source, relative);
            UpdateCacheLocation.EnsureNormalPath(path, false);
            if (File.Exists(path)) throw new IOException("An application component remains in the former installation folder: " + relative + ".");
        }
    }

    private static void VerifyInstalled(Receipt receipt) => VerifyDistribution(receipt.Destination, receipt.BuildFlavor, receipt.TargetVersion, "CloudInlet");
    private static void VerifyOriginal(Receipt receipt) => VerifyDistribution(receipt.Source, receipt.BuildFlavor, receipt.InstalledVersion, receipt.OriginalBrand);

    private static void VerifyDistribution(string directory, string flavor, string version, string brand)
    {
        UpdateCacheLocation.EnsureNormalPath(directory, true);
        var descriptorPath = Path.Combine(directory, "distribution.json");
        UpdateCacheLocation.EnsureNormalPath(descriptorPath, false);
        using var input = new FileStream(descriptorPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 32768) throw new IOException("The installed distribution descriptor is too large.");
        var descriptor = (Distribution?)new DataContractJsonSerializer(typeof(Distribution)).ReadObject(input);
        if (descriptor == null || descriptor.SchemaVersion != 1 || descriptor.Version != version || descriptor.BuildFlavor != flavor ||
            descriptor.InstallerKind != "Msi" || descriptor.Architecture != "x64" || descriptor.InstallScope != "perUser" || !SamePath(descriptor.InstallDirectory, directory))
            throw new IOException("The installed distribution does not match the recorded migration.");
        var image = Path.Combine(directory, "CloudInlet.exe");
        UpdateCacheLocation.EnsureNormalPath(image, false);
        if (FileVersionInfo.GetVersionInfo(image).ProductVersion?.Split('+')[0] != version)
            throw new IOException("The installed application version does not match the recorded migration.");
        var registration = DistributionRegistration.Read(flavor, "Msi", (path, name) =>
        { using var key = Registry.CurrentUser.OpenSubKey(path); return key?.GetValue(name) as string; });
        if (registration == null || registration.Brand != brand || registration.Version != version || !SamePath(registration.Directory, directory))
            throw new IOException("The installer registration does not match the recorded migration.");
        VerifyMsiRegistration(flavor, version);
    }

    internal sealed class MsiProduct
    {
        internal string ProductCode { get; }
        internal int State { get; }
        internal string Version { get; }
        internal int MainFeatureState { get; }
        internal MsiProduct(string productCode, int state, string version, int mainFeatureState)
        { ProductCode = productCode; State = state; Version = version; MainFeatureState = mainFeatureState; }
    }

    internal static void VerifyMsiRegistration(string flavor, string version,
        Func<string, IReadOnlyList<MsiProduct>>? readProducts = null)
    {
        ValidateFlavor(flavor);
        // A failed major upgrade can restore our files and distribution key while
        // leaving the old MSI merely advertised. Those bytes do not prove that
        // Windows Installer restored a serviceable installation for this user.
        var upgradeCode = flavor == "Debug" ? "{14BA2C55-3BED-4F8C-927B-2157E7852A96}" : "{7C2B596C-321A-4F07-9A96-C2E83136223D}";
        var products = (readProducts ?? ReadMsiProducts)(upgradeCode);
        if (products.Count != 1 || products[0].State != 5 || products[0].MainFeatureState != 3 ||
            products[0].Version != version || !Guid.TryParseExact(products[0].ProductCode, "B", out var productId) || productId == Guid.Empty)
            throw new IOException("Windows Installer has not verified the installed product and its main feature for this user. Repair the installation before continuing migration recovery.");
    }

    private static IReadOnlyList<MsiProduct> ReadMsiProducts(string upgradeCode)
    {
        var products = new List<MsiProduct>();
        // Enumerate and query on this thread. Never invoke a repair or configure
        // API while inspecting recovery; a query failure must retain the receipt.
        for (uint index = 0; index < 256; index++)
        {
            var code = new StringBuilder(39);
            var error = MsiEnumRelatedProductsW(upgradeCode, 0, index, code);
            if (error == 259) return products; // ERROR_NO_MORE_ITEMS
            if (error != 0) throw new IOException("Windows Installer product registration could not be read (code " + error + ").");
            var product = code.ToString();
            var state = MsiQueryProductStateW(product);
            var version = "";
            var feature = -1;
            if (state == 5) // INSTALLSTATE_DEFAULT: installed for the current user
            {
                var value = new StringBuilder(256); uint length = (uint)value.Capacity;
                error = MsiGetProductInfoW(product, "VersionString", value, ref length);
                if (error != 0) throw new IOException("Windows Installer product version could not be read (code " + error + ").");
                version = value.ToString();
                feature = MsiQueryFeatureStateW(product, "Main");
            }
            products.Add(new MsiProduct(product, state, version, feature));
        }
        throw new IOException("Windows Installer returned an unexpected number of related products.");
    }

    private static List<string> ReadOwnedPaths(string source)
    {
        var owned = new List<string> { "CloudInlet.exe", "distribution.json" };
        var path = Path.Combine(source, "installer-progress-payload.json");
        UpdateCacheLocation.EnsureNormalPath(path, false);
        if (!File.Exists(path)) return owned;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumReceiptBytes) throw new IOException("The installed payload inventory is too large.");
        var inventory = (PayloadInventory?)new DataContractJsonSerializer(typeof(PayloadInventory)).ReadObject(input);
        if (inventory == null || inventory.SchemaVersion != 1 || inventory.Files == null) throw new IOException("The installed payload inventory is invalid.");
        foreach (var file in inventory.Files) { ValidateRelative(file.Path); owned.Add(file.Path.Replace('/', '\\')); }
        return owned.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<FileProof> Snapshot(string root)
    {
        using var parents = new ParentGuards(Path.Combine(root, ".inventory-guard"));
        var result = new List<FileProof>();
        Walk(root);
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        void Walk(string directory)
        {
            UpdateCacheLocation.EnsureNormalPath(directory, true);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("The installation contains a linked file or directory.");
                if ((attrs & FileAttributes.Directory) != 0) Walk(path);
                else
                {
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var digest = SHA256.Create();
                    result.Add(new FileProof { Path = path.Substring(root.TrimEnd('\\').Length + 1), Size = input.Length, Sha256 = Hex(digest.ComputeHash(input)) });
                    if (result.Count > 30000) throw new IOException("The installation inventory exceeds the safe recovery limit.");
                }
            }
        }
    }

    private static void VerifySnapshot(string root, List<FileProof> expected, bool exact)
    {
        var actual = Snapshot(root);
        if (exact && actual.Count != expected.Count) throw new IOException("The installation file inventory changed after verification.");
        var lookup = actual.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in expected)
            if (!lookup.TryGetValue(file.Path, out var current) || current.Size != file.Size || current.Sha256 != file.Sha256)
                throw new IOException("An installation file changed after verification: " + file.Path + ".");
    }

    private Receipt Require(string hash)
    {
        var receipt = Read();
        if (receipt == null || receipt.RequestHash != hash) throw new IOException("The migration recovery record does not match this update request.");
        return receipt;
    }

    private Receipt? Read()
    {
        UpdateCacheLocation.EnsureNormalPath(_path, false);
        if (!File.Exists(_path)) return null;
        byte[] encrypted;
        using (var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length < 1 || input.Length > MaximumReceiptBytes) throw new IOException("The migration recovery record has an invalid size.");
            using var bytes = new MemoryStream(); input.CopyTo(bytes); encrypted = bytes.ToArray();
        }
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var bytes = new MemoryStream(plain, false);
            var receipt = (Receipt?)new DataContractJsonSerializer(typeof(Receipt)).ReadObject(bytes);
            if (receipt == null) throw new IOException("The migration recovery record is empty.");
            Validate(receipt);
            return receipt;
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    private void Publish(Receipt receipt)
    {
        Validate(receipt);
        byte[] plain;
        using (var bytes = new MemoryStream()) { new DataContractJsonSerializer(typeof(Receipt)).WriteObject(bytes, receipt); plain = bytes.ToArray(); }
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(plain, 0, plain.Length); }
        if (encrypted.Length > MaximumReceiptBytes) throw new IOException("The migration recovery record exceeds its safe size limit.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
            UpdateCacheLocation.EnsureNormalPath(_path, false);
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
        }
        finally { UpdateCacheLocation.EnsureNormalPath(temporary, false); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Validate(Receipt value)
    {
        if (value.SchemaVersion != 1 || value.BuildFlavor != _flavor || !IsHash(value.RequestHash) || !IsHash(value.PackageSha256) || value.PackageSize < 1 ||
            !Guid.TryParseExact(value.BootIdentity, "D", out var boot) || boot == Guid.Empty ||
            (value.BootIdentityVersion != 0 && value.BootIdentityVersion != CurrentBootIdentityVersion) || string.IsNullOrEmpty(value.SourceIdentity) ||
            (value.OriginalBrand != "CloudBay" && value.OriginalBrand != "CloudInlet") ||
            !new[] { "Prepared", "InstallerReturned", "VerifiedInstallation", "Complete", "RolledBack" }.Contains(value.State) ||
            value.OriginalFiles == null || value.InstalledFiles == null || value.RetiredFiles == null ||
            value.OriginalFiles.Count == 0 || value.OriginalFiles.Count > 30000 || value.InstalledFiles.Count > 30000 || value.RetiredFiles.Count > 60000)
            throw new IOException("The migration recovery identity is invalid.");
        InstallationMigrationPolicy.Validate(value.Source, value.Destination, value.BuildFlavor, "Msi", value.InstalledVersion, value.TargetVersion);
        foreach (var files in new[] { value.OriginalFiles, value.InstalledFiles })
        {
            if (files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new IOException("The recovery inventory contains duplicate files.");
            foreach (var file in files) { ValidateRelative(file.Path); if (file.Size < 0 || !IsHash(file.Sha256)) throw new IOException("The recovery file proof is invalid."); }
        }
        foreach (var relative in value.RetiredFiles) ValidateRelative(relative);
        if ((value.State == "VerifiedInstallation" || value.State == "Complete") && (value.InstalledFiles.Count == 0 || string.IsNullOrEmpty(value.DestinationIdentity)))
            throw new IOException("The migration has no verified installation inventory.");
    }

    private static void ValidateRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0 ||
            relative.Replace('/', '\\').Split('\\').Any(x => x.Length == 0 || x == "." || x == ".." || x.EndsWith(" ") || x.EndsWith(".")))
            throw new IOException("The recovery inventory contains an unsafe relative path.");
    }
    private static bool IsHash(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$");
    private static bool SamePath(string a, string b) => string.Equals(InstallationMigrationPolicy.Normalize(a), InstallationMigrationPolicy.Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static void ValidateFlavor(string flavor) { if (flavor != "Release" && flavor != "Debug") throw new IOException("The recovery build flavor is invalid."); }

    internal static string CurrentBootIdentity()
    {
        // SystemBootEnvironmentInformation identifies the boot entry and can stay
        // unchanged across real restarts. Use the kernel's immutable startup event.
        // No wall-clock/uptime subtraction, cache file, or editable receipt is proof.
        var query = EvtQuery(IntPtr.Zero, "System",
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]", 0x201);
        if (query == IntPtr.Zero) throw BootReadError();
        var events = new IntPtr[1];
        try
        {
            if (!EvtNext(query, 1, events, 3000, 0, out var count) || count != 1 || events[0] == IntPtr.Zero)
                throw BootReadError();
            if (EvtRender(IntPtr.Zero, events[0], 1, 0, IntPtr.Zero, out var required, out _) ||
                Marshal.GetLastWin32Error() != 122 || required < 2 || required > 64 * 1024 || required % 2 != 0)
                throw BootReadError();
            var buffer = Marshal.AllocHGlobal(required);
            try
            {
                if (!EvtRender(IntPtr.Zero, events[0], 1, required, buffer, out var used, out _) || used < 2 || used > required || used % 2 != 0)
                    throw BootReadError();
                var xml = Marshal.PtrToStringUni(buffer, used / 2)?.TrimEnd('\0') ?? throw BootReadError();
                return BootIdentityFromEventXml(xml);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            if (events[0] != IntPtr.Zero) EvtClose(events[0]);
            EvtClose(query);
        }
    }

    internal static void RequireCurrentBootIdentityVersion(int version)
    {
        if (version != CurrentBootIdentityVersion)
            throw new IOException("The saved installer record uses an older restart check and cannot prove that Windows restarted. Recovery evidence was retained; verify this pending installation before continuing.");
    }

    internal static string BootIdentityFromEventXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 32 * 1024) throw BootReadError();
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 });
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
            string One(string xpath)
            {
                var nodes = document.SelectNodes(xpath, ns);
                if (nodes == null || nodes.Count != 1) throw BootReadError();
                return nodes[0]!.InnerText;
            }
            if (One("/e:Event/e:System/e:Provider/@Name") != "Microsoft-Windows-Kernel-General" ||
                !Guid.TryParse(One("/e:Event/e:System/e:Provider/@Guid"), out var provider) || provider != new Guid("a68ca8b7-004f-d7b6-a698-07e2de0f1f5d") ||
                One("/e:Event/e:System/e:EventID") != "12" || One("/e:Event/e:System/e:Channel") != "System" ||
                !ulong.TryParse(One("/e:Event/e:System/e:EventRecordID"), NumberStyles.None, CultureInfo.InvariantCulture, out var record) || record == 0)
                throw BootReadError();
            var computer = One("/e:Event/e:System/e:Computer");
            var start = One("/e:Event/e:EventData/e:Data[@Name='StartTime']");
            if (string.IsNullOrWhiteSpace(computer) || computer.Length > 255 || !start.EndsWith("Z", StringComparison.Ordinal) ||
                !DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var boot) || boot.Year < 2000)
                throw BootReadError();
            // Record IDs and logging timestamps are deliberately excluded: log
            // maintenance cannot turn the same kernel StartTime into a new boot.
            var proof = Encoding.UTF8.GetBytes("CloudInlet.KernelStartup.v2\n" + computer.ToUpperInvariant() + "\n" + boot.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(proof);
            var id = new byte[16]; Array.Copy(hash, id, id.Length);
            return new Guid(id).ToString("D");
        }
        catch (XmlException error) { throw new IOException("Windows startup evidence is invalid; migration recovery remains pending.", error); }
    }

    private static IOException BootReadError() => new IOException("Windows startup evidence could not be verified; migration recovery remains pending.");

    private static string DirectoryIdentity(string path)
    {
        UpdateCacheLocation.EnsureNormalPath(path, true);
        using var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new IOException("The installation directory identity is unavailable.");
        if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException("The installation directory is linked or invalid.");
        return info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
    }

    private sealed class ParentGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        internal ParentGuards(string path)
        {
            var parents = new Stack<string>();
            for (var current = Path.GetDirectoryName(path); current != null; current = Path.GetDirectoryName(current)) parents.Push(current);
            try
            {
                foreach (var parent in parents)
                {
                    var handle = CreateFileW(@"\\?\" + parent, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The migration recovery directory is busy or unavailable."); }
                    _handles.Add(handle); UpdateCacheLocation.EnsureNormalPath(parent, true);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); }
    }

    public void Dispose() { _lease.Dispose(); _guards.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EvtNext(IntPtr result, int size, [Out] IntPtr[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int size, IntPtr buffer, out int used, out int count);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EvtClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MsiQueryProductStateW(string product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MsiQueryFeatureStateW(string product, string feature);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder value, ref uint length);

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
    [DataContract] private sealed class PayloadInventory
    {
        [DataMember(Name = "SchemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "Files")] public List<PayloadFile> Files { get; set; } = new();
    }
    [DataContract] private sealed class PayloadFile { [DataMember(Name = "Path")] public string Path { get; set; } = ""; }
}

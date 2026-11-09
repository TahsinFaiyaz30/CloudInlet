using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Updates;
using Microsoft.Win32;
using global::Windows.Storage.Provider;

namespace CloudInlet.Windows;

public sealed record LegacyDistributionRegistration(string RegistryPath, string InstallDirectory,
    UpdateBuildFlavor BuildFlavor, UpdateInstallerKind InstallerKind);
public sealed record LegacySyncRootRegistration(string Id, string RootPath, Guid ProviderId, string? AccountIdentity = null);
public sealed record LegacyMigrationPathState(bool Exists, bool HasReparsePoint = false, string? ReadError = null);

/// <summary>Read-only, injectable observations. Tests never need the user's registry, folders or providers.</summary>
public sealed record LegacyMigrationFacts
{
    public required string LocalAppDataDirectory { get; init; }
    public required string UserSid { get; init; }
    public required Func<string, LegacyMigrationPathState> InspectPath { get; init; }
    public IReadOnlyList<LegacyDistributionRegistration> Distributions { get; init; } = [];
    public IReadOnlyList<LegacySyncRootRegistration> SyncRoots { get; init; } = [];
    public IReadOnlyDictionary<string, string> KnownFolders { get; init; } = new Dictionary<string, string>();
    public string? StoreApplicationId { get; init; }
    public string? StoreStartupTaskId { get; init; }
}

/// <summary>
/// Inventories exact app-owned legacy locations without reading credentials, enumerating cloud files,
/// moving files or changing Windows state. A review is not an executable migration authorization.
/// </summary>
public static class LegacyMigrationDiscovery
{
    public const string ReviewOnlyReason = "This build can review this change; migration is not available yet.";
    private const string LegacyRegistry = @"Software\CloudBay\Distribution\";
    private const string CanonicalRegistry = @"Software\CloudInlet\Distribution\";

    public static MigrationPlan Discover(AppSettings settings, UpdateInstallation installation,
        string stateDirectory, LegacyMigrationFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(installation);
        facts ??= CaptureFacts(settings, installation);
        var items = new List<MigrationItem>();
        var folderMoves = new Dictionary<string, (string Id, string Target)>(StringComparer.OrdinalIgnoreCase);
        var installPath = Normalize(installation.Directory);
        var flavor = installation.Identity.BuildFlavor;
        var oldLeaf = flavor == UpdateBuildFlavor.Debug ? "CloudBay Debug" : "CloudBay";
        var newLeaf = flavor == UpdateBuildFlavor.Debug ? "CloudInlet Debug" : "CloudInlet";
        var registered = facts.Distributions.Where(item =>
            item.BuildFlavor == flavor && item.InstallerKind == installation.Identity.InstallerKind &&
            (string.Equals(item.RegistryPath, LegacyRegistry + flavor + "\\" + item.InstallerKind, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.RegistryPath, CanonicalRegistry + flavor + "\\" + item.InstallerKind, StringComparison.OrdinalIgnoreCase)) &&
            SamePath(item.InstallDirectory, installPath)).ToArray();
        var installerKind = installation.Identity.InstallerKind;
        if (installation.CanInstall && installation.Notice is null && installPath is not null && registered.Length != 0 &&
            (RenameLeaf(installPath, oldLeaf, newLeaf) is not null || registered.Any(value => value.RegistryPath.StartsWith(LegacyRegistry, StringComparison.OrdinalIgnoreCase))))
        {
            var target = RenameLeaf(installPath, oldLeaf, newLeaf) ?? installPath;
            var relocating = !SamePath(installPath, target);
            var key = registered[0].RegistryPath;
            var targetKey = CanonicalRegistry + flavor + "\\" + installerKind;
            items.Add(new("installation", MigrationArea.Installation,
                relocating ? "Application installation and registration" : "Installer registration",
                relocating ? installPath : "HKEY_CURRENT_USER\\" + key,
                relocating ? target : "HKEY_CURRENT_USER\\" + targetKey,
                $"The matching {installerKind.ToString().ToUpperInvariant()} registration owns this installation. " +
                "The installer must update its file ownership, repair and uninstall records together. " +
                (relocating ? "" : "The installation remains in " + installPath + ". ") +
                "Registry: HKEY_CURRENT_USER\\" + key + " → HKEY_CURRENT_USER\\" + targetKey + ". " +
                (installerKind == UpdateInstallerKind.Msi
                    ? "MSI component keys and installer-owned shortcuts are part of this same change. "
                    : "Installer-owned shortcuts are part of this same change. ") +
                "An older update worker must finish before migration starts.",
                registered.Length != 1 ? "Conflicting installer registrations must be repaired before migration." :
                    Blocked(facts, installPath, relocating ? target : null)));
        }

        var relativeState = flavor == UpdateBuildFlavor.Debug ? Path.Combine("Debug", "Client") : "Client";
        var expectedState = Normalize(Path.Combine(facts.LocalAppDataDirectory, "CloudBay", relativeState));
        var currentState = Normalize(stateDirectory);
        if (currentState is not null && SamePath(currentState, expectedState))
        {
            var target = Path.Combine(facts.LocalAppDataDirectory, "CloudInlet", relativeState);
            items.Add(new("application-state", MigrationArea.ApplicationState, "Application data", currentState, target,
                "This is this build's historical private settings, protected-account and recovery-journal directory. " +
                "A migration must preserve encrypted accounts and unfinished transfers, then verify the new state before removing the old copy.",
                Blocked(facts, currentState, target)));
        }

        var root = Normalize(settings.RootPath);
        if (root is not null && RenameLeaf(root, oldLeaf, newLeaf) is { } rootTarget)
        {
            items.Add(new("sync-folder", MigrationArea.SyncFolder, "Sync folder", root, rootTarget,
                "This exact folder is saved as CloudInlet's main sync root. Its placeholders, local changes, " +
                "saved exclusions and any Windows folder mappings must be verified and moved together.",
                Blocked(facts, root, rootTarget)));
            folderMoves[root] = ("sync-folder", rootTarget);
        }

        foreach (var backup in settings.CustomBackups.OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var source = Normalize(backup.SourcePath);
            if (source is null || folderMoves.ContainsKey(source) || RenameLeaf(source, oldLeaf, newLeaf) is not { } target) continue;
            // Only an explicitly saved backup source is considered. Other files containing the old name are never searched or renamed.
            var id = Id("backup-folder", source);
            items.Add(new(id, MigrationArea.BackupFolder, backup.Name + " backup folder", source, target,
                "This exact path is an explicitly saved custom backup source. Its contents belong to you; " +
                "changing this folder would also update the saved backup and recovery references.", Blocked(facts, source, target)));
            folderMoves[source] = (id, target);
        }

        foreach (var backup in settings.Backups.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var destination = Normalize(backup.DestinationPath);
            if (root is null || destination is null || !folderMoves.TryGetValue(root, out var rootMove) || !Within(root, destination)) continue;
            var target = Rebase(root, rootMove.Target, destination);
            var mappingMatches = facts.KnownFolders.TryGetValue(backup.Name, out var current) && SamePath(current, destination);
            var reason = mappingMatches ? ReviewOnlyReason :
                "The current Windows folder mapping does not match this saved backup. Review that mapping before migration. " + ReviewOnlyReason;
            AddCoupledDetail(items, rootMove.Id, backup.Name + " Windows mapping: " + destination + " → " + target + ". " +
                (mappingMatches ? "The current Windows mapping matches the saved destination." : "No matching current Windows mapping was verified."),
                mappingMatches ? null : reason);
        }

        var rootPaths = settings.CustomBackups.Select(item => Normalize(item.SourcePath)).Append(root)
            .Where(path => path is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providerPrefix = "CloudBay" + (flavor == UpdateBuildFlavor.Debug ? ".Debug" : "") + "!" + facts.UserSid + "!";
        foreach (var registration in facts.SyncRoots.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var registeredPath = Normalize(registration.RootPath);
            var prefix = SamePath(registeredPath, root) ? settings.Prefix :
                settings.CustomBackups.SingleOrDefault(value => SamePath(value.SourcePath, registeredPath))?.Prefix;
            var account = settings.AccountId + ":" + settings.BucketId + ":" + prefix;
            var hasAccountContext = prefix is not null && registration.AccountIdentity == account &&
                registration.Id == providerPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..24];
            if (registeredPath is null || !rootPaths.Contains(registeredPath) ||
                (registration.ProviderId != CloudFiles.WindowsPlaceholderService.ProviderId &&
                    !(registration.ProviderId == Guid.Empty && hasAccountContext)) ||
                !registration.Id.StartsWith(providerPrefix, StringComparison.Ordinal) ||
                registration.Id.Length != providerPrefix.Length + 24 ||
                !registration.Id[providerPrefix.Length..].All(Uri.IsHexDigit)) continue;
            var proposedId = "CloudInlet" + registration.Id["CloudBay".Length..];
            if (folderMoves.TryGetValue(registeredPath, out var move))
            {
                AddCoupledDetail(items, move.Id, "Files On-Demand registration: " + registration.Id + " → " + proposedId +
                    ". Its placeholder identity and recovery references are part of the same folder migration.");
                continue;
            }
            items.Add(new(Id("sync-registration", registration.Id), MigrationArea.WindowsIntegration,
                "Files On-Demand registration", registration.Id, proposedId,
                "Windows identifies this provider and this user's saved sync root at " + registeredPath +
                ". Re-registering requires verified placeholder identity and recovery; registry text must not be renamed directly.",
                ReviewOnlyReason));
        }

        var oldCloudPrefix = settings.Prefix == "CloudBay/";
        if (oldCloudPrefix)
            items.Add(new("cloud-prefix", MigrationArea.CloudPrefix, "B2 sync prefix", "CloudBay/", "CloudInlet/",
                "This is the exact saved legacy B2 sync namespace" + BucketDescription(settings) +
                ". Migration must verify every retained version, permissions, unfinished uploads and destination conflicts before switching sync.",
                ReviewOnlyReason));
        foreach (var backup in settings.CustomBackups.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var expected = settings.Prefix + ".cloudbay-backups/" + backup.Name + "/";
            if (backup.Prefix != expected) continue;
            var target = (oldCloudPrefix ? "CloudInlet/" : settings.Prefix) + ".cloudinlet-backups/" + backup.Name + "/";
            if (oldCloudPrefix)
            {
                AddCoupledDetail(items, "cloud-prefix", backup.Name + " backup namespace: " + backup.Prefix + " → " + target +
                    ". All versions and saved references belong to this same cloud migration.");
                var cloudIndex = items.FindIndex(value => value.Id == "cloud-prefix");
                items[cloudIndex] = items[cloudIndex] with { CloudMappings = (items[cloudIndex].CloudMappings ?? []).Append(new(backup.Prefix, target)).ToArray() };
                continue;
            }
            items.Add(new(Id("backup-prefix", backup.Prefix), MigrationArea.CloudPrefix, backup.Name + " B2 backup prefix",
                backup.Prefix, target,
                "This app-generated namespace belongs to the saved custom backup" + BucketDescription(settings) +
                ". Keep all cloud versions and local backup references consistent; no cloud objects were listed or changed during this review.",
                ReviewOnlyReason));
        }

        if (installerKind == UpdateInstallerKind.Store)
        {
            if (facts.StoreApplicationId == "CloudBay")
                items.Add(new("store-application-id", MigrationArea.WindowsIntegration, "Microsoft Store application identity",
                    "CloudBay", "CloudInlet", "This is the application ID inside the installed package, not its package family name. " +
                    "Changing it affects Windows activation, pins and notifications and requires a validated Store package update.", ReviewOnlyReason));
            if (facts.StoreStartupTaskId == "CloudBayStartup" && facts.StoreApplicationId == "CloudBay")
                AddCoupledDetail(items, "store-application-id", "Startup task: CloudBayStartup → CloudInletStartup. " +
                    "Its enabled, user-disabled or policy-controlled state must be preserved in the same package migration.");
            else if (facts.StoreStartupTaskId == "CloudBayStartup")
                items.Add(new("store-startup-id", MigrationArea.WindowsIntegration, "Microsoft Store startup task",
                    "CloudBayStartup", "CloudInletStartup", "The installed package declares this startup task. " +
                    "Its enabled, user-disabled or policy-controlled state must be preserved before retiring the old task.", ReviewOnlyReason));
        }
        return MigrationPlan.Create(items.DistinctBy(item => item.Id));
    }

    private static void AddCoupledDetail(List<MigrationItem> items, string id, string description, string? reason = null)
    {
        var index = items.FindIndex(item => item.Id == id);
        var item = items[index];
        items[index] = item with
        {
            Description = item.Description + " " + description,
            BlockedReason = reason is null ? item.BlockedReason : item.BlockedReason == ReviewOnlyReason ? reason : item.BlockedReason + " " + reason
        };
    }

    public static LegacyMigrationFacts CaptureFacts(AppSettings settings, UpdateInstallation installation)
    {
        var distributions = new List<LegacyDistributionRegistration>();
        var flavor = installation.Identity.BuildFlavor;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("Windows could not identify the current user for migration review.");
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        foreach (var kind in new[] { UpdateInstallerKind.Exe, UpdateInstallerKind.Msi })
        foreach (var registryRoot in new[] { LegacyRegistry, CanonicalRegistry })
        {
            var path = registryRoot + flavor + "\\" + kind;
            using var key = user.OpenSubKey(path);
            if (key?.GetValue("InstallDirectory") is string directory)
                distributions.Add(new(path, directory, flavor, kind));
        }
        var roots = StorageProviderSyncRootManager.IsSupported()
            ? StorageProviderSyncRootManager.GetCurrentSyncRoots().Select(item =>
                new LegacySyncRootRegistration(item.Id, item.Path?.Path ?? "", item.ProviderId, ReadAccountContext(item))).ToArray()
            : [];
        var knownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in settings.Backups.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            if (KnownFolderBackup.FolderIds.ContainsKey(name)) knownFolders[name] = KnownFolderBackup.GetPath(name);
        string? applicationId = null, startupId = null;
        if (installation.Identity.InstallerKind == UpdateInstallerKind.Store)
        {
            using var reader = XmlReader.Create(Path.Combine(installation.Directory, "AppxManifest.xml"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var manifest = new XmlDocument { XmlResolver = null };
            manifest.Load(reader);
            var app = manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application'][@Executable='CloudInlet.exe']") as XmlElement;
            applicationId = app?.GetAttribute("Id");
            startupId = (app?.SelectSingleNode(".//*[local-name()='StartupTask']") as XmlElement)?.GetAttribute("TaskId");
        }
        return new()
        {
            LocalAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UserSid = sid, InspectPath = InspectPath, Distributions = distributions, SyncRoots = roots,
            KnownFolders = knownFolders, StoreApplicationId = applicationId, StoreStartupTaskId = startupId
        };
    }

    private static string? ReadAccountContext(StorageProviderSyncRootInfo item)
    {
        if (item.Context is null || item.Context.Length is 0 or > 65536) return null;
        global::Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(item.Context, out var bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string Blocked(LegacyMigrationFacts facts, string source, string? target)
    {
        var reasons = new List<string>();
        AddPathReasons(source, false);
        if (target is not null) AddPathReasons(target, true);
        reasons.Add(ReviewOnlyReason);
        return string.Join(" ", reasons);

        void AddPathReasons(string path, bool destination)
        {
            var label = destination ? "The proposed destination" : "The current location";
            var state = facts.InspectPath(path);
            if (state.ReadError is not null) reasons.Add(label + " could not be inspected: " + state.ReadError + ".");
            if (state.HasReparsePoint) reasons.Add(label + " includes a linked or reparse-point path and needs a dedicated safety review.");
            if (destination && state.Exists) reasons.Add(label + " already exists. Its ownership and contents must be reviewed before merging or replacing anything.");
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) reasons.Add(label + " is a network location; a local-directory migration cannot move it safely.");
        }
    }

    private static LegacyMigrationPathState InspectPath(string path)
    {
        var exists = false;
        try
        {
            var first = true;
            for (var current = Normalize(path); current is not null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if (first) exists = true;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return new(exists, true);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                first = false;
            }
            return new(exists);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(exists, ReadError: error.Message); }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    private static bool SamePath(string? first, string? second) => Normalize(first) is { } left &&
        Normalize(second) is { } right && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static string? RenameLeaf(string path, string oldLeaf, string newLeaf) =>
        Path.GetFileName(path).Equals(oldLeaf, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(path) is { } parent
            ? Path.Combine(parent, newLeaf) : null;
    private static bool Within(string parent, string child) => SamePath(parent, child) ||
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Rebase(string source, string target, string child) =>
        SamePath(source, child) ? target : Path.Combine(target, Path.GetRelativePath(source, child));
    private static string Id(string prefix, string value) => prefix + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..16].ToLowerInvariant();
    private static string BucketDescription(AppSettings settings) => settings.BucketName.Length == 0 ? "" : " in bucket " + settings.BucketName;
}

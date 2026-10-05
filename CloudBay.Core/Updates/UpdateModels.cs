using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudBay.Core.Updates;

public enum UpdateBuildFlavor { Release, Debug }
public enum UpdateInstallerKind { Exe, Msi, Portable, Store }
public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Deferred, Error, StoreManaged, Unsupported }

/// <summary>Written by packaging, never inferred from an update candidate or changed by preferences.</summary>
public sealed record InstalledUpdateIdentity(string Version, UpdateBuildFlavor BuildFlavor,
    UpdateInstallerKind InstallerKind, string Architecture = "x64");

public sealed record UpdatePreferences(bool AutomaticChecks = true, bool AutomaticallyDownload = false,
    bool AutomaticallyInstall = false, int CheckIntervalHours = 24)
{
    public UpdatePreferences Normalize() => this with
    {
        CheckIntervalHours = Math.Clamp(CheckIntervalHours, 1, 168),
        AutomaticallyDownload = AutomaticallyDownload || AutomaticallyInstall
    };
}

public sealed record UpdateAsset(UpdateBuildFlavor BuildFlavor, UpdateInstallerKind InstallerKind,
    string Architecture, string FileName, long Size, string Sha256);
public sealed record UpdateManifest(int SchemaVersion, string Repository, string Version, string Tag,
    IReadOnlyList<UpdateAsset> Assets, DateTimeOffset? PublishedUtc = null);
public sealed record UpdateCandidate(string Version, string Tag, UpdateAsset Asset);
public sealed record UpdateSnapshot(UpdateState State, string Message, UpdateCandidate? Candidate = null,
    long DownloadedBytes = 0, long TotalBytes = 0, DateTimeOffset? LastCheckedUtc = null,
    DateTimeOffset? NextCheckUtc = null, string? ReadyPackagePath = null);
public sealed record VerifiedUpdatePackage(string Path, InstalledUpdateIdentity Identity,
    UpdateCandidate Candidate);

/// <summary>The platform runner must revalidate the file and preserve installation scope and settings.</summary>
public interface IUpdateInstaller
{
    Task InstallAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken);
}

public static class UpdateVersion
{
    // Release tags deliberately use three-component stable versions. Preview/debug is a build flavor,
    // not a prerelease feed, so both flavors advance from the same immutable release manifest.
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        return value is not null && Regex.IsMatch(value, @"^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$",
            RegexOptions.CultureInvariant) && Version.TryParse(value, out version!) &&
            version.Major <= 255 && version.Minor <= 255 && version.Build <= 65535;
    }
}

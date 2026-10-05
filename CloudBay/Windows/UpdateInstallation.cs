using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudBay.Core;
using CloudBay.Core.Updates;

namespace CloudBay.Windows;

/// <summary>Installation identity comes from the running build and its installer, never from update preferences.</summary>
public sealed record UpdateInstallation(InstalledUpdateIdentity Identity, string Directory, string? Notice = null)
{
    public bool CanInstall => Identity.InstallerKind is UpdateInstallerKind.Exe or UpdateInstallerKind.Msi;
    public static bool IsPackaged
    {
        get
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result == 0 || result == 122 && length > 0;
        }
    }

    public static UpdateInstallation Load() => LoadAt(AppContext.BaseDirectory, IsPackaged);

    internal static UpdateInstallation LoadAt(string installationDirectory, bool packaged)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
        var flavor = BuildInfo.Flavor == "Debug" ? UpdateBuildFlavor.Debug : UpdateBuildFlavor.Release;
        var identity = new InstalledUpdateIdentity(BuildInfo.Version, flavor,
            packaged ? UpdateInstallerKind.Store : UpdateInstallerKind.Portable);
        if (packaged) return new(identity, directory);
        var path = Path.Combine(directory, "distribution.json");
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new(identity, directory); }
            catch (DirectoryNotFoundException) { return new(identity, directory); }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Installation metadata cannot follow a linked or unexpected path.");
            if (new FileInfo(path).Length > 64 * 1024)
                throw new InvalidDataException("Installation metadata is too large.");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(path), options)
                ?? throw new InvalidDataException("Installation metadata is empty.");
            if (metadata.SchemaVersion != 1 || metadata.Version != identity.Version ||
                !string.Equals(metadata.BuildFlavor, flavor.ToString(), StringComparison.OrdinalIgnoreCase) ||
                metadata.Architecture != "x64" || !Enum.TryParse<UpdateInstallerKind>(metadata.InstallerKind, ignoreCase: true, out var kind) ||
                !string.Equals(metadata.InstallerKind, kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !Enum.IsDefined(kind) || kind == UpdateInstallerKind.Store)
                throw new InvalidDataException("Installation metadata does not match this build.");
            if (kind is UpdateInstallerKind.Exe or UpdateInstallerKind.Msi &&
                (metadata.InstallScope != "perUser" || string.IsNullOrWhiteSpace(metadata.InstallDirectory) ||
                 !Path.IsPathFullyQualified(metadata.InstallDirectory) ||
                 !Path.TrimEndingDirectorySeparator(Path.GetFullPath(metadata.InstallDirectory)).Equals(directory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Installation metadata does not match this installation directory.");
            return new(identity with { InstallerKind = kind }, directory);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            return new(identity, directory, "Installation information could not be verified. Install the matching EXE or MSI package to enable automatic installation.");
        }
    }

    private sealed record Metadata([property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Version, [property: JsonRequired] string BuildFlavor,
        [property: JsonRequired] string InstallerKind, [property: JsonRequired] string Architecture,
        string? InstallScope, string? InstallDirectory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}

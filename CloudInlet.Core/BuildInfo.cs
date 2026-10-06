namespace CloudInlet.Core;

/// <summary>Build identity is compiled into the payload; a downloaded update cannot select another flavor.</summary>
public static class BuildInfo
{
#if CLOUDINLET_DEBUG
    public const string Flavor = "Debug";
    public const string ProductName = "CloudInlet Debug";
    public const string PipeSuffix = ".Debug";
    public const string StartupRegistryName = "CloudInletDebug";
    public const string LegacyStartupRegistryName = "CloudBayDebug";
#else
    public const string Flavor = "Release";
    public const string ProductName = "CloudInlet";
    public const string PipeSuffix = "";
    public const string StartupRegistryName = "CloudInlet";
    public const string LegacyStartupRegistryName = "CloudBay";
#endif
    public const string IconFileName = "CloudInlet.ico";
    public const string NotificationIconFileName = "CloudInlet-32.png";
    // Published installations and Store registrations already own these identities.
    public const string StartupTaskId = "CloudBayStartup";
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    // Preserve secure accounts, recovery journals and cached old-updater handoffs in place.
    public static string DefaultDataDirectory => Flavor == "Debug"
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Debug", "Client")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Client");
    public static string DefaultRootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ProductName);
}

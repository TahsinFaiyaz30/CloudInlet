namespace CloudBay.Core;

/// <summary>Build identity is compiled into the payload; a downloaded update cannot select another flavor.</summary>
public static class BuildInfo
{
#if CLOUDBAY_DEBUG
    public const string Flavor = "Debug";
    public const string ProductName = "CloudBay Debug";
    public const string PipeSuffix = ".Debug";
    public const string StartupRegistryName = "CloudBayDebug";
#else
    public const string Flavor = "Release";
    public const string ProductName = "CloudBay";
    public const string PipeSuffix = "";
    public const string StartupRegistryName = "CloudBay";
#endif
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static string DefaultDataDirectory => Flavor == "Debug"
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Debug", "Client")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Client");
    public static string DefaultRootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ProductName);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using CloudInlet.Core;

namespace CloudInlet.Windows;

public static class SystemIntegration
{
    public static async Task<bool> IsStartupEnabledAsync()
    {
        if (UpdateInstallation.IsPackaged)
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(BuildInfo.StartupTaskId);
            return task.State is global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return HasStartupCommand(key, BuildInfo.StartupRegistryName) || HasStartupCommand(key, BuildInfo.LegacyStartupRegistryName);
    }

    private static bool HasStartupCommand(RegistryKey? key, string name) =>
        key?.GetValue(name) is string command && !string.IsNullOrWhiteSpace(command);

    /// <summary>Moves this installation's old startup entry without enabling a disabled or absent entry.</summary>
    public static void MigrateLegacyStartupRegistration()
    {
        if (UpdateInstallation.IsPackaged) return; // The published Store startup task keeps its original identity.
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (run is null) return;
        using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
        MigrateLegacyStartupRegistration(run, approved, StartupExecutable());
    }

    internal static void MigrateLegacyStartupRegistration(RegistryKey run, RegistryKey? approved, string executable)
    {
        var legacyName = BuildInfo.LegacyStartupRegistryName;
        var newName = BuildInfo.StartupRegistryName;
        if (run.GetValue(legacyName) is not string command || !OwnsLegacyStartupCommand(command, executable)) return;
        var hadNewCommand = run.GetValueNames().Contains(newName, StringComparer.OrdinalIgnoreCase);
        if (!hadNewCommand)
        {
            // StartupApproved contains Windows' enabled/disabled choice. Copy the opaque value verbatim;
            // changing only the Run name would otherwise re-enable startup disabled in Task Manager.
            if (approved is not null && !approved.GetValueNames().Contains(newName, StringComparer.OrdinalIgnoreCase) &&
                approved.GetValueNames().Contains(legacyName, StringComparer.OrdinalIgnoreCase))
                approved.SetValue(newName, approved.GetValue(legacyName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!,
                    approved.GetValueKind(legacyName));
            run.SetValue(newName, $"\"{executable}\" --background", RegistryValueKind.String);
        }
        // Keep the old command until the branded command and Windows approval are durable.
        run.DeleteValue(legacyName, throwOnMissingValue: false);
        approved?.DeleteValue(legacyName, throwOnMissingValue: false);
    }

    private static bool OwnsLegacyStartupCommand(string command, string executable)
    {
        var value = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end < 0) return false;
            path = value[1..end];
        }
        else
        {
            var end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return false;
            path = value[..(end + 4)];
        }
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            // Recover only an owned historical Windows startup record. Future
            // packages do not ship the old executable; disabled approval stays intact.
            return (Path.GetFileName(fullPath).Equals("CloudBay.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(fullPath).Equals("CloudInlet.exe", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(Path.GetDirectoryName(fullPath), Path.GetDirectoryName(Path.GetFullPath(executable)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static string StartupExecutable()
    {
        // Always register the canonical apphost, including test-hosted calls.
        var branded = Path.Combine(AppContext.BaseDirectory, "CloudInlet.exe");
        return File.Exists(branded) ? branded : Environment.ProcessPath ?? throw new IOException("Could not find the CloudInlet executable.");
    }
    public static async Task ConfigureStartupAsync(bool enabled, bool userRequested = false)
    {
        if (UpdateInstallation.IsPackaged)
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(BuildInfo.StartupTaskId);
            if (!enabled) { task.Disable(); return; }
            if (task.State is global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy) return;
            var state = await task.RequestEnableAsync();
            if (userRequested && state is not (global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
                throw new IOException("Windows has disabled CloudInlet startup. Enable CloudInlet in Settings > Apps > Startup.");
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = StartupExecutable();
            key.SetValue(BuildInfo.StartupRegistryName, $"\"{executable}\" --background");
        }
        else key.DeleteValue(BuildInfo.StartupRegistryName, throwOnMissingValue: false);
        key.DeleteValue(BuildInfo.LegacyStartupRegistryName, throwOnMissingValue: false);
    }
    public static string? GetPauseReason(AppSettings settings)
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
            return "Waiting for a network connection";
        if (settings.PauseOnMetered)
        {
            var cost = profile.GetConnectionCost();
            if (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit)
                return "Sync paused on a metered connection";
        }
        if (settings.PauseOnBatterySaver && GetSystemPowerStatus(out var power) && power.SystemStatusFlag == 1)
            return "Sync paused while Battery Saver is on";
        return null;
    }
    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The sync folder is not available yet.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

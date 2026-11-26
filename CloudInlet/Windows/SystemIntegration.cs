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

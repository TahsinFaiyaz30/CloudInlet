using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using CloudBay.Core;

namespace CloudBay.Windows;

public static class SystemIntegration
{
    public static async Task<bool> IsStartupEnabledAsync()
    {
        if (UpdateInstallation.IsPackaged)
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync("CloudBayStartup");
            return task.State is global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(BuildInfo.StartupRegistryName) is string command && !string.IsNullOrWhiteSpace(command);
    }
    public static async Task ConfigureStartupAsync(bool enabled, bool userRequested = false)
    {
        if (UpdateInstallation.IsPackaged)
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync("CloudBayStartup");
            if (!enabled) { task.Disable(); return; }
            if (task.State is global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy) return;
            var state = await task.RequestEnableAsync();
            if (userRequested && state is not (global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
                throw new IOException("Windows has disabled CloudBay startup. Enable CloudBay in Settings > Apps > Startup.");
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Could not find the CloudBay executable.");
            key.SetValue(BuildInfo.StartupRegistryName, $"\"{executable}\" --background");
        }
        else key.DeleteValue(BuildInfo.StartupRegistryName, throwOnMissingValue: false);
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

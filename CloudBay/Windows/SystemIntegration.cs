using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using CloudBay.Core;

namespace CloudBay.Windows;

public static class SystemIntegration
{
    public static void ConfigureStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Could not find the CloudBay executable.");
            key.SetValue("CloudBay", $"\"{executable}\" --background");
        }
        else key.DeleteValue("CloudBay", throwOnMissingValue: false);
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

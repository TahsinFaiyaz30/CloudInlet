using CloudBay.Core;

namespace CloudBay.Windows;

public enum TrayIconVisualState { Disconnected, Idle, Checking, Transferring, Paused, Offline, Attention }
internal enum TrayIconInteraction { None, ShowActivity, OpenApp, ContextMenu }

/// <summary>Shell event decoding and transfer presentation, independent of a live desktop.</summary>
internal static class TrayIconPresentation
{
    internal const int FrameCount = 12;
    internal const uint FrameIntervalMilliseconds = 250;

    internal static TrayIconVisualState VisualState(SyncSnapshot snapshot)
    {
        // A periodic scan is not a file transfer. It must never spin the tray
        // indefinitely, and pausing stops motion even if an older snapshot has
        // not yet cleared its transfer counters.
        if (snapshot.State == ClientState.Paused) return TrayIconVisualState.Paused;
        if (snapshot.ActiveTransfers > 0) return TrayIconVisualState.Transferring;
        return snapshot.State switch
        {
            ClientState.NotConnected => TrayIconVisualState.Disconnected,
            ClientState.Attention => TrayIconVisualState.Attention,
            ClientState.Offline => TrayIconVisualState.Offline,
            ClientState.Syncing or ClientState.Connecting => TrayIconVisualState.Checking,
            _ => TrayIconVisualState.Idle
        };
    }

    internal static TrayIconInteraction DecodeCallback(ulong wParam, long lParam, bool version4, uint iconId)
    {
        var eventCode = (uint)(lParam & 0xffff);
        if (version4)
        {
            if (((ulong)lParam >> 16 & 0xffff) != iconId) return TrayIconInteraction.None;
            // Version 4 provides the accessible semantic events. Handling the
            // accompanying mouse-up events too can open the UI twice.
            return eventCode switch
            {
                0x400 or 0x401 => TrayIconInteraction.ShowActivity, // NIN_SELECT / NIN_KEYSELECT
                0x203 => TrayIconInteraction.OpenApp, // WM_LBUTTONDBLCLK
                0x7b => TrayIconInteraction.ContextMenu, // WM_CONTEXTMENU
                _ => TrayIconInteraction.None
            };
        }
        if (wParam != iconId) return TrayIconInteraction.None;
        return eventCode switch
        {
            0x202 => TrayIconInteraction.ShowActivity,
            0x203 => TrayIconInteraction.OpenApp,
            0x205 or 0x7b => TrayIconInteraction.ContextMenu,
            _ => TrayIconInteraction.None
        };
    }

    internal static string Tooltip(string state)
    {
        var tip = "CloudBay – " + state.Replace('\r', ' ').Replace('\n', ' ');
        var length = Math.Min(127, tip.Length);
        if (length < tip.Length && length > 0 && char.IsHighSurrogate(tip[length - 1])) length--;
        return tip[..length];
    }
}

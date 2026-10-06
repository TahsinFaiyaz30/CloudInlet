namespace CloudInlet.Core;

/// <summary>Keeps configured clients in the tray until a window is explicitly requested.</summary>
public static class ClientLaunchPolicy
{
    public static bool ShouldShowWindow(AppSettings settings, bool hasOneDriveAccount, ClientState initialState,
        IReadOnlyCollection<string> commandLine, bool notificationActivation = false, bool isSecondaryLaunch = false) =>
        !notificationActivation && (IsInteractive(commandLine) || (!isSecondaryLaunch && initialState == ClientState.Attention) ||
            !(settings.IsConfigured || hasOneDriveAccount));

    public static string SecondaryCommand(IReadOnlyCollection<string> commandLine) =>
        commandLine.Contains("--ui-live") && commandLine.Contains("--show-tray") ? "tray" :
        IsInteractive(commandLine) ? "show" : "launch";

    private static bool IsInteractive(IReadOnlyCollection<string> commandLine) =>
        commandLine.Contains("--show") || commandLine.Contains("--ui-smoke") || commandLine.Contains("--ui-live");
}

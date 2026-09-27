namespace CloudBay.Core.Folders;

/// <summary>Pure path-state decisions used by the live service and safety tests.</summary>
public static class KnownFolderDecision
{
    public static KnownFolderState Classify(
        string currentPath, string defaultPath, bool exists,
        bool isManagedRedirect, bool isOneDrivePath)
    {
        if (!exists) return KnownFolderState.BrokenRedirect;
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        string localDefault = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultPath));
        if (string.Equals(current, localDefault, StringComparison.OrdinalIgnoreCase))
            return KnownFolderState.LocalDefault;
        if (isManagedRedirect) return KnownFolderState.CloudManaged;
        if (isOneDrivePath) return KnownFolderState.LegacyOneDrive;
        return KnownFolderState.RedirectedElsewhere;
    }

    public static bool RequiresExplicitRecovery(bool isNoOp, bool isManagedRedirect, bool allowUnmanaged) =>
        !isNoOp && !isManagedRedirect && !allowUnmanaged;
}

using CloudBay.Core;
using CloudBay.Core.Sync;

namespace CloudBay.Windows;

internal static class ActivityRowNavigation
{
    internal static Task<string> ResolveFolderAsync(ActivityActionTarget target, AppSettings settings) => Task.Run(() =>
    {
        if (!ActivityLocationResolver.MatchesCurrentRoot(target.Location, settings))
            throw new IOException("This activity belongs to a backup location that is no longer connected.");
        var folder = ActivityLocationResolver.FindExistingFolder(target, Directory.Exists)
            ?? throw new DirectoryNotFoundException("The activity's backup folder is no longer available.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Location.RootPath));
        if (!folder.Equals(root, StringComparison.OrdinalIgnoreCase))
            PathRules.FullPath(root, Path.GetRelativePath(root, folder).Replace('\\', '/'));
        // An Explorer action does not hydrate the file. Reject true links at
        // the root and destination too, without treating cloud placeholders
        // (whose LinkTarget is null) as links.
        if (new DirectoryInfo(root).LinkTarget is not null || new DirectoryInfo(folder).LinkTarget is not null)
            throw new IOException("This activity folder has changed to a linked location. Choose its backup folder in Files instead.");
        return folder;
    });
}

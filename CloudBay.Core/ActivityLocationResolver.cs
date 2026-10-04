using CloudBay.Core.Sync;

namespace CloudBay.Core;

/// <summary>A row action is bound to one currently configured root, never a guessed URL.</summary>
public sealed record ActivityActionTarget(ActivityLocation Location, bool IsFolder, bool CanViewCloud)
{
    public string LocalPath => IsFolder && Location.RelativePath.Length == 0
        ? ActivityLocationResolver.NormalizeRoot(Location.RootPath) : ActivityLocationResolver.FullPath(Location.RootPath, Location.RelativePath);
    public string ContainingFolder => IsFolder ? LocalPath : System.IO.Path.GetDirectoryName(LocalPath)!;
}

public static class ActivityLocationResolver
{
    public static ActivityActionTarget? ForActivity(ActivityEvent activity, AppSettings settings)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(activity.Path)) return null;
        try
        {
            var location = activity.Location;
            if (location is null)
            {
                // History is retained across account changes. Legacy paths
                // contain no bucket/root provenance, so even an ordinary
                // relative path can identify a different file today.
                return null;
            }
            if (!MatchesCurrentRoot(location, settings)) return null;
            var folder = activity.Kind == ActivityKind.Backup;
            if (!folder || location.RelativePath.Length > 0)
            {
                PathRules.ValidateRelative(location.RelativePath);
                if (PathRules.IsExcluded(location.RelativePath, Array.Empty<string>())) return null;
                FullPath(location.RootPath, location.RelativePath);
            }
            var cloud = !folder && activity.Completed && activity.Kind is
                ActivityKind.Upload or ActivityKind.Download or ActivityKind.Restore or ActivityKind.Delete or ActivityKind.Conflict;
            return new(location, folder, cloud);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException) { return null; }
    }

    public static ActivityActionTarget? ForTransfer(TransferSnapshot transfer, AppSettings settings)
    {
        if (!settings.IsConfigured) return null;
        try
        {
            if (transfer.RootName == "CloudBay" && settings.CustomBackups.Any(item => item.Name == "CloudBay")) return null;
            var folder = settings.CustomBackups.SingleOrDefault(item => item.Name.Equals(transfer.RootName, StringComparison.Ordinal));
            if (folder is null && transfer.RootName is not ("" or "CloudBay")) return null;
            var location = new ActivityLocation(folder?.SourcePath ?? settings.RootPath, folder?.Name,
                transfer.RelativePath, settings.BucketId, folder?.Prefix ?? settings.Prefix);
            PathRules.ValidateRelative(location.RelativePath);
            if (PathRules.IsExcluded(location.RelativePath, Array.Empty<string>())) return null;
            FullPath(location.RootPath, location.RelativePath);
            return new(location, false, transfer.Kind == ActivityKind.Download);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException) { return null; }
    }

    public static bool MatchesCurrentRoot(ActivityLocation location, AppSettings settings)
    {
        try
        {
            if (!settings.IsConfigured || location.BucketId is null || location.Prefix is null ||
                location.RootPath is null || location.RelativePath is null ||
                !location.BucketId.Equals(settings.BucketId, StringComparison.Ordinal)) return false;
            var folder = location.BackupName is null ? null : settings.CustomBackups.SingleOrDefault(item => item.Name.Equals(location.BackupName, StringComparison.Ordinal));
            if (location.BackupName is not null && folder is null) return false;
            return location.Prefix.Equals(folder?.Prefix ?? settings.Prefix, StringComparison.Ordinal) &&
                NormalizeRoot(location.RootPath).Equals(NormalizeRoot(folder?.SourcePath ?? settings.RootPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException) { return false; }
    }

    // Purely lexical: rendering action buttons must not read or hydrate a file.
    internal static string FullPath(string root, string relative)
    {
        PathRules.ValidateRelative(relative);
        root = NormalizeRoot(root);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The activity path is outside its backup root.");
        return path;
    }

    internal static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new InvalidDataException("The activity root is not a safe absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static string? FindExistingFolder(ActivityActionTarget target, Func<string, bool> directoryExists)
    {
        var root = NormalizeRoot(target.Location.RootPath);
        for (var path = target.ContainingFolder; path is not null; path = Path.GetDirectoryName(path))
        {
            if (directoryExists(path)) return path;
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }
}

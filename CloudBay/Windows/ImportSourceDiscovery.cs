using CloudBay.Core;
using global::Windows.Storage.Provider;

namespace CloudBay.Windows;

public sealed record ImportSourceCandidate(string Id, string DisplayName, string ProviderName, string Path, string Kind);

/// <summary>Read-only hints for a chooser. Presence never means an account is inactive or safe to move.</summary>
public static class ImportSourceDiscovery
{
    public static IReadOnlyList<ImportSourceCandidate> FindCandidates(AppSettings settings)
    {
        var found = new Dictionary<string, ImportSourceCandidate>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string provider, string kind, string? label = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return;
            try
            {
                path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
                if (!Directory.Exists(path) || IsWithin(settings.RootPath, path) || IsWithin(path, settings.RootPath) ||
                    settings.CustomBackups.Any(root => IsWithin(root.SourcePath, path) || IsWithin(path, root.SourcePath))) return;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && new DirectoryInfo(path).LinkTarget is not null) return;
                if (!found.ContainsKey(path)) found.Add(path, new(path, label ?? new DirectoryInfo(path).Name, provider, path, kind));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        }
        try
        {
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
            {
                var path = root.Path?.Path;
                var provider = root.Id?.Split('!')[0] ?? "Cloud app";
                Add(path, provider, "Registered cloud folder");
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException) { }
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            Add(Environment.GetEnvironmentVariable(variable), "Microsoft OneDrive", "Existing Windows folder");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            // A candidate can be old or current. The chooser never infers that it was abandoned.
            foreach (var path in Directory.EnumerateDirectories(profile, "OneDrive*", SearchOption.TopDirectoryOnly))
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
                {
                    Add(path, "Microsoft OneDrive", "Existing Windows folder");
                    foreach (var folder in KnownFolderBackup.FolderIds.Keys)
                        Add(System.IO.Path.Combine(path, folder), "Microsoft OneDrive", "Personal-folder candidate", name + " · " + folder);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return found.Values.OrderBy(item => item.ProviderName).ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static bool IsWithin(string parent, string child) => child.Equals(System.IO.Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(System.IO.Path.GetFullPath(parent).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

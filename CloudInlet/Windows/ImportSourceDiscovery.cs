using CloudInlet.Core;
using global::Windows.Storage.Provider;

namespace CloudInlet.Windows;

public sealed record ImportSourceCandidate(string Id, string DisplayName, string ProviderName, string Path, string Kind)
{
    /// <summary>The registered provider identity, when Windows supplies one.</summary>
    public Guid? ProviderId { get; init; }
}

/// <summary>Read-only hints for a chooser. Presence never means an account is inactive or safe to move.</summary>
public static class ImportSourceDiscovery
{
    // The stable provider GUID and old registration IDs also identify folders created before the rename.
    private static readonly Guid OwnProviderId = CloudFiles.WindowsPlaceholderService.ProviderId;

    /// <param name="knownFolderName">A Windows personal folder, or null to choose whole cloud account folders.</param>
    public static IReadOnlyList<ImportSourceCandidate> FindCandidates(AppSettings settings, string? knownFolderName = null)
    {
        var context = NormalizeContext(knownFolderName);
        var roots = new List<ImportSourceCandidate>();
        try
        {
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
            {
                try
                {
                    var path = NormalizePath(root.Path?.Path);
                    if (path is null) continue;
                    // Windows documents Id as provider!SID!account. Keep that identity
                    // so own roots cannot leak into discovery through a fallback hint.
                    var id = root.Id;
                    var provider = ProviderName(id?.Split('!')[0]);
                    roots.Add(new(id ?? path, AccountName(path), provider, path, "Registered Windows folder")
                    { ProviderId = root.ProviderId });
                }
                catch (Exception error) when (IsDiscoveryFailure(error)) { }
            }
        }
        catch (Exception error) when (IsDiscoveryFailure(error)) { }
        void AddOneDrive(string? rawPath)
        {
            if (NormalizePath(rawPath) is not { } path) return;
            roots.Add(new(path, AccountName(path), "Microsoft OneDrive", path, "Existing Windows folder"));
        }
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            AddOneDrive(Environment.GetEnvironmentVariable(variable));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            // A candidate can be old or current. The chooser never infers that it was abandoned.
            foreach (var path in Directory.EnumerateDirectories(profile, "OneDrive*", SearchOption.TopDirectoryOnly))
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
                    AddOneDrive(path);
            }
        }
        catch (Exception error) when (IsDiscoveryFailure(error)) { }
        string? currentPath = null;
        if (context is not null)
        {
            try { currentPath = KnownFolderBackup.GetPath(context); }
            catch (Exception error) when (IsDiscoveryFailure(error)) { }
        }
        return FilterCandidates(roots, settings, context, currentKnownFolderPath: currentPath);
    }

    /// <summary>Selects account roots or one matching personal folder per account without changing Windows registrations.</summary>
    public static IReadOnlyList<ImportSourceCandidate> FilterCandidates(IEnumerable<ImportSourceCandidate> accountRoots,
        AppSettings settings, string? knownFolderName = null, Func<string, bool>? directoryExists = null,
        string? currentKnownFolderPath = null)
    {
        ArgumentNullException.ThrowIfNull(accountRoots);
        ArgumentNullException.ThrowIfNull(settings);
        var context = NormalizeContext(knownFolderName);
        directoryExists ??= IsUsableDirectory;
        var roots = accountRoots.Select(item => (Candidate: item, Path: NormalizePath(item.Path)))
            .Where(item => item.Path is not null).ToArray();
        var excludedRoots = roots.Where(item => IsOwnProvider(item.Candidate)).Select(item => item.Path!)
            .Concat(settings.CustomBackups.Select(item => NormalizePath(item.SourcePath)))
            .Append(NormalizePath(settings.RootPath)).Where(path => path is not null).Cast<string>().ToArray();
        var currentPath = NormalizePath(currentKnownFolderPath);
        var found = new Dictionary<string, ImportSourceCandidate>(StringComparer.OrdinalIgnoreCase);
        // Registered metadata wins over environment and top-level folder hints.
        foreach (var item in roots.OrderBy(item => item.Candidate.Kind.StartsWith("Registered ", StringComparison.Ordinal) ? 0 : 1))
        {
            var root = item.Path!;
            if (IsOwnProvider(item.Candidate) || context is null && OverlapsExcluded(root)) continue;
            var path = context is null ? root : currentPath is not null && IsWithin(root, currentPath) &&
                !root.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ? currentPath : System.IO.Path.Combine(root, context);
            if (OverlapsExcluded(path) || !directoryExists(path) || found.ContainsKey(path)) continue;
            found.Add(path, item.Candidate with
            {
                Path = path,
                DisplayName = context is null ? item.Candidate.DisplayName : item.Candidate.DisplayName + " · " + context,
                Kind = context is null ? item.Candidate.Kind : context + " folder"
            });
        }
        return found.Values.OrderBy(item => item.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();

        bool OverlapsExcluded(string path) => excludedRoots.Any(excluded => IsWithin(excluded, path) || IsWithin(path, excluded));
    }

    private static bool IsOwnProvider(ImportSourceCandidate candidate) => candidate.ProviderId == OwnProviderId ||
        (candidate.Id.StartsWith("CloudBay!", StringComparison.OrdinalIgnoreCase) || candidate.Id.StartsWith("CloudBay.Debug!", StringComparison.OrdinalIgnoreCase)) ||
        (candidate.Id.StartsWith("CloudInlet!", StringComparison.OrdinalIgnoreCase) || candidate.Id.StartsWith("CloudInlet.Debug!", StringComparison.OrdinalIgnoreCase)) ||
        candidate.ProviderName.Equals("CloudBay", StringComparison.OrdinalIgnoreCase) ||
        candidate.ProviderName.Equals("CloudInlet", StringComparison.OrdinalIgnoreCase) ||
        candidate.ProviderName.Equals("CloudInlet Debug", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeContext(string? context)
    {
        if (context is null) return null;
        return KnownFolderBackup.FolderIds.Keys.FirstOrDefault(name => name.Equals(context, StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("Choose a supported Windows personal folder.", nameof(context));
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool IsUsableDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ||
                new DirectoryInfo(path).LinkTarget is null);
        }
        catch (Exception error) when (IsDiscoveryFailure(error)) { return false; }
    }

    private static string AccountName(string path) => System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
    private static string ProviderName(string? provider) => provider?.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) == true
        ? "Microsoft OneDrive" : string.IsNullOrWhiteSpace(provider) ? "Cloud app" : provider;
    private static bool IsDiscoveryFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or System.Runtime.InteropServices.COMException;
    private static bool IsWithin(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

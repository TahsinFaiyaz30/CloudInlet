using System;
using System.IO;

namespace CloudInlet.Packaging;

/// <summary>Recognizes only the two owned cache locations for one build flavor during opt-in state migration.</summary>
internal static class UpdateCacheLocation
{
    internal static string Validate(string flavor, string requestFile, string packagePath, string resultPath,
        string readinessPath, string localAppData)
    {
        if (flavor != "Release" && flavor != "Debug") throw new IOException("The update build flavor is invalid.");
        var home = AbsoluteLocalPath(localAppData);
        var request = AbsoluteLocalPath(requestFile);
        var relative = flavor == "Debug" ? @"Debug\Client\Updates" : @"Client\Updates";
        string? selected = null;
        foreach (var brand in new[] { "CloudBay", "CloudInlet" })
        {
            var candidate = Path.Combine(home, brand, relative);
            if (IsWithin(request, candidate)) selected = candidate;
        }
        if (selected == null) throw new IOException("The update request is outside this build's private update cache.");
        // Select from the request's exact location, never from whichever directory happens to exist.
        // A declined migration and a migrated installation can therefore both keep updating.
        EnsureNormalPath(selected, true);
        if (!Directory.Exists(selected)) throw new IOException("The update cache is unavailable.");
        foreach (var file in new[] { requestFile, packagePath, resultPath, readinessPath })
        {
            var absolute = AbsoluteLocalPath(file);
            if (!IsWithin(absolute, selected)) throw new IOException("All update files must use the same private update cache.");
            EnsureNormalPath(absolute, false);
        }
        // Preserve the existing ownership format until a separately verified format migration runs.
        var owner = Path.Combine(selected, ".cloudbay-update-cache-v1");
        EnsureNormalPath(owner, false);
        const string marker = "CloudBay update cache v1\n";
        using (var input = new FileStream(owner, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length != marker.Length) throw new IOException("The update cache owner is invalid.");
            using (var reader = new StreamReader(input))
                if (reader.ReadToEnd() != marker) throw new IOException("The update cache owner is invalid.");
        }
        return selected;
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string AbsoluteLocalPath(string path)
    {
        // net472 does not expose IsPathFullyQualified. A drive-relative or UNC path must not be resolved using the worker's CWD.
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0 || path.IndexOf('"') >= 0)
            throw new IOException("An absolute local update path is required.");
        return Path.GetFullPath(path).TrimEnd('\\');
    }

    internal static void EnsureNormalPath(string path, bool directory)
    {
        var absolute = Path.GetFullPath(path);
        for (var current = absolute; current != null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                current == absolute && ((attributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException("Update paths must be ordinary files and directories, without links.");
        }
    }
}

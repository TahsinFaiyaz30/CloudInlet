using System;
using System.IO;

namespace CloudInlet.Packaging
{
    // The installer owns relocation. The app never moves installed components or
    // rewrites their registration; consent is carried by the verified update request.
    internal static class InstallationMigrationPolicy
    {
        internal static string Target(string source, string flavor)
        {
            if (flavor != "Debug" && flavor != "Release") throw new IOException("The installation build is invalid.");
            var root = Normalize(source);
            var legacy = flavor == "Debug" ? "CloudBay Debug" : "CloudBay";
            var canonical = flavor == "Debug" ? "CloudInlet Debug" : "CloudInlet";
            if (string.Equals(Path.GetFileName(root), canonical, StringComparison.OrdinalIgnoreCase)) return root;
            if (!string.Equals(Path.GetFileName(root), legacy, StringComparison.OrdinalIgnoreCase))
                // An explicit registration migration still applies to custom
                // locations. The installer changes its owned registry key only;
                // a user-selected directory must never be renamed by branding.
                return root;
            return Path.Combine(Path.GetDirectoryName(root)!, canonical);
        }

        internal static string Validate(string source, string target, string flavor, string kind,
            string installedVersion, string targetVersion)
        {
            if (kind != "Msi" && kind != "Exe") throw new IOException("Installation migration requires a matching desktop installer.");
            if (!Version.TryParse(installedVersion, out var installed) || !Version.TryParse(targetVersion, out var update) || update <= installed)
                throw new IOException("Installation migration requires a newer package; repairs cannot relocate components.");
            var expected = Target(source, flavor);
            if (!string.Equals(expected, Normalize(target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The migration destination differs from the reviewed CloudInlet folder.");
            return expected;
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || value.StartsWith(@"\\", StringComparison.Ordinal) ||
                value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/') ||
                value.IndexOf(':', 2) >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\0') >= 0)
                throw new IOException("The installation path must be a normal local absolute path.");
            foreach (var part in value.Substring(3).Replace('/', '\\').Split('\\'))
                if (part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal))
                    throw new IOException("The installation path cannot contain Windows path aliases.");
            var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (path.Length < 8 || path == Path.GetPathRoot(path)?.TrimEnd('\\')) throw new IOException("The installation path is invalid.");
            return path;
        }
    }
}

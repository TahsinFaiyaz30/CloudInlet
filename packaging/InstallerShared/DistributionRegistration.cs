using System;
using System.IO;

namespace CloudInlet.Packaging
{
    internal sealed class DistributionRegistration
    {
        internal string Brand { get; }
        internal string Directory { get; }
        internal string Version { get; }
        internal DistributionRegistration(string brand, string directory, string version)
        { Brand = brand; Directory = InstallationMigrationPolicy.Normalize(directory); Version = version; }

        internal static string Key(string brand, string flavor, string kind)
        {
            if ((brand != "CloudBay" && brand != "CloudInlet") || (flavor != "Debug" && flavor != "Release") || (kind != "Exe" && kind != "Msi"))
                throw new IOException("The installer registration identity is invalid.");
            return @"Software\" + brand + @"\Distribution\" + flavor + @"\" + kind;
        }

        internal static DistributionRegistration? Read(string flavor, string kind, Func<string, string, string?> read)
        {
            DistributionRegistration? found = null;
            foreach (var brand in new[] { "CloudBay", "CloudInlet" })
            {
                var key = Key(brand, flavor, kind);
                var directory = read(key, "InstallDirectory");
                if (directory == null) continue;
                if (read(key, "BuildFlavor") != flavor || !System.Version.TryParse(read(key, "Version"), out _))
                    throw new IOException("The installation registration is incomplete. Repair it before updating.");
                if (found != null) throw new IOException("Both CloudBay and CloudInlet registrations exist for this installer. Repair the conflicting registrations before updating.");
                found = new DistributionRegistration(brand, directory, read(key, "Version")!);
            }
            return found;
        }
    }
}

namespace CloudBay.Core;

public sealed record ProductOption(string Id, string Name, string Description, string Group, bool Available = false)
{
    public string Status => Available ? "Available" : "Coming soon";
}

/// <summary>Product discovery only. Planned entries do not enable unsupported connections or modes.</summary>
public static class ProductCatalog
{
    public static IReadOnlyList<ProductOption> Modes { get; } = Array.AsReadOnly<ProductOption>([
        new("native", "Native backup", "Windows folder backup, Explorer integration, and Files On-Demand.", "Modes", true),
        new("virtual-drive", "Virtual drive", "Browse cloud storage through a mounted drive.", "Modes"),
        new("read-only", "Read-only access", "Browse and download cloud files without changing remote content.", "Modes")
    ]);

    public static IReadOnlyList<ProductOption> Providers { get; } = Array.AsReadOnly<ProductOption>([
        new("b2", "Backblaze B2", "Private cloud storage for native backup.", "Cloud storage", true),
        new("aws-s3", "Amazon S3", "AWS object storage.", "Cloud storage"),
        new("azure", "Microsoft Azure Blob Storage", "Azure object storage.", "Cloud storage"),
        new("gcs", "Google Cloud Storage", "Google Cloud buckets.", "Cloud storage"),
        new("google-drive", "Google Drive", "Personal and shared cloud files.", "Cloud files"),
        new("onedrive", "Microsoft OneDrive", "Personal and business cloud files.", "Cloud files"),
        new("sharepoint", "Microsoft SharePoint", "SharePoint sites and document libraries.", "Cloud files"),
        new("dropbox", "Dropbox", "Personal and team cloud files.", "Cloud files"),
        new("box", "Box", "Cloud file storage and collaboration.", "Cloud files"),
        new("nextcloud", "Nextcloud", "Self-hosted cloud files.", "Cloud files"),
        new("owncloud", "ownCloud", "Self-hosted cloud files.", "Cloud files"),
        new("opencloud", "OpenCloud", "Self-hosted cloud files.", "Cloud files"),
        new("pcloud", "pCloud", "Cloud files through a provider profile.", "Cloud files"),
        new("seafile", "Seafile", "Cloud libraries through a provider profile.", "Cloud files"),
        new("kdrive", "Infomaniak kDrive", "Cloud files through a provider profile.", "Cloud files"),
        new("cloudflare-r2", "Cloudflare R2", "S3-compatible object storage.", "S3-compatible storage"),
        new("wasabi", "Wasabi", "S3-compatible object storage.", "S3-compatible storage"),
        new("digitalocean", "DigitalOcean Spaces", "S3-compatible object storage.", "S3-compatible storage"),
        new("alibaba", "Alibaba Cloud OSS", "Object storage through a provider profile.", "S3-compatible storage"),
        new("ibm", "IBM Cloud Object Storage", "S3-compatible object storage.", "S3-compatible storage"),
        new("oracle", "Oracle Cloud Infrastructure", "Object storage through a provider profile.", "S3-compatible storage"),
        new("hetzner", "Hetzner Object Storage", "S3-compatible object storage.", "S3-compatible storage"),
        new("scaleway", "Scaleway Object Storage", "S3-compatible object storage.", "S3-compatible storage"),
        new("lyve", "Seagate Lyve Cloud", "S3-compatible object storage.", "S3-compatible storage"),
        new("exoscale", "Exoscale", "S3-compatible object storage.", "S3-compatible storage"),
        new("linode", "Linode Object Storage", "S3-compatible object storage.", "S3-compatible storage"),
        new("vultr", "Vultr Object Storage", "S3-compatible object storage.", "S3-compatible storage"),
        new("filebase", "Filebase", "S3-compatible object storage.", "S3-compatible storage"),
        new("storj", "Storj DCS", "Object storage through an S3 gateway.", "S3-compatible storage"),
        new("synology-c2", "Synology C2 Object Storage", "S3-compatible object storage.", "S3-compatible storage"),
        new("mega-s4", "MEGA S4", "S3-compatible object storage.", "S3-compatible storage"),
        new("minio", "MinIO", "Self-hosted S3-compatible storage.", "S3-compatible storage"),
        new("garage", "Garage", "Self-hosted S3-compatible storage.", "S3-compatible storage"),
        new("s3-compatible", "Other S3-compatible storage", "Custom endpoints and provider profiles.", "S3-compatible storage"),
        new("swift", "OpenStack Swift", "Object storage through Swift.", "Servers and enterprise"),
        new("rackspace", "Rackspace Cloud Files", "Cloud object storage through Swift.", "Servers and enterprise"),
        new("sftp", "SFTP", "Secure file access over SSH.", "Servers and enterprise"),
        new("ftp", "FTP / FTPS", "File transfer servers, including TLS connections.", "Servers and enterprise"),
        new("webdav", "WebDAV", "Compatible file servers and cloud profiles.", "Servers and enterprise"),
        new("smb", "SMB", "Windows and Samba file shares.", "Servers and enterprise"),
        new("files-com", "Files.com", "Enterprise cloud file storage.", "Servers and enterprise"),
        new("dracoon", "DRACOON", "Enterprise file storage.", "Servers and enterprise"),
        new("irods", "iRODS", "Research data storage.", "Servers and enterprise"),
        new("spectra", "Spectra BlackPearl", "Enterprise storage through a provider profile.", "Servers and enterprise")
    ]);
}

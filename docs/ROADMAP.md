# Providers and access modes

The Settings catalog presents the product's planned direction without enabling unfinished native connections. **Backblaze B2 with Native backup is available now.** **OneDrive direct transfers** are available in the separate transfer/import dialog, with actual cloud browsing and local/cloud destinations; OneDrive native background sync remains planned. Other unfinished providers and modes are marked **Coming soon**. No release dates or unsupported provider capabilities are implied.

| Mode | Availability | Intended experience |
| --- | --- | --- |
| Native backup | Available for Backblaze B2 | Windows personal-folder defaults, custom folder backup, Explorer status, background sync, and Files On-Demand |
| Direct transfers | Available for OneDrive and Backblaze B2 | Reviewed Copy/Move between accounts or explicit local folders, RAM-only cloud relay and durable restart progress |
| Virtual drive | Coming soon | Browse cloud storage through a mounted drive |
| Read-only access | Coming soon | Browse and download without modifying remote content |

Planned native providers include Amazon S3, Azure Blob Storage, Google Cloud Storage, Google Drive, OneDrive, SharePoint, Dropbox, Box, Nextcloud, ownCloud, OpenCloud, pCloud, Seafile, and Infomaniak kDrive.

Planned S3-compatible connections include Cloudflare R2, Wasabi, DigitalOcean Spaces, Alibaba Cloud OSS, IBM Cloud Object Storage, Oracle Cloud Infrastructure, Hetzner, Scaleway, Seagate Lyve Cloud, Exoscale, Linode, Vultr, Filebase, Storj DCS, Synology C2, MEGA S4, MinIO, Garage, and other compatible endpoints and provider profiles.

Planned server and enterprise connections include OpenStack Swift, Rackspace Cloud Files, SFTP, FTP/FTPS, WebDAV, SMB, Files.com, DRACOON, iRODS, and Spectra BlackPearl. Each integration will need its own authentication, capabilities, error handling, and acceptance tests. Features such as native version history and metadata retention vary by provider; the catalog does not promise identical behavior across protocols.

The catalog follows the families and profiles in [Mountain Duck's official protocol documentation](https://docs.mountainduck.io/protocols/) and its [S3 provider documentation](https://docs.mountainduck.io/protocols/s3/providers/), checked on 2026-10-03. It is a CloudInlet roadmap, not a claim that those integrations are implemented or affiliated with their providers.

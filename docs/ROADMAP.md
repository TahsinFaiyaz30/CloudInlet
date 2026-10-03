# Providers and access modes

The Settings catalog presents the product's planned direction without enabling unfinished connections. **Backblaze B2 with Native backup is available now.** Every other provider and mode is marked **Coming soon**. No release dates or unsupported provider capabilities are implied.

| Mode | Availability | Intended experience |
| --- | --- | --- |
| Native backup | Available for Backblaze B2 | Windows personal-folder defaults, custom folder backup, Explorer status, background sync, and Files On-Demand |
| Virtual drive | Coming soon | Browse cloud storage through a mounted drive |
| Read-only access | Coming soon | Browse and download without modifying remote content |

Planned providers include Amazon S3, Azure Blob Storage, Google Cloud Storage, Google Drive, OneDrive, SharePoint, Dropbox, Box, Nextcloud, ownCloud, OpenCloud, pCloud, Seafile, and Infomaniak kDrive.

Planned S3-compatible connections include Cloudflare R2, Wasabi, DigitalOcean Spaces, Alibaba Cloud OSS, IBM Cloud Object Storage, Oracle Cloud Infrastructure, Hetzner, Scaleway, Seagate Lyve Cloud, Exoscale, Linode, Vultr, Filebase, Storj DCS, Synology C2, MEGA S4, MinIO, Garage, and other compatible endpoints and provider profiles.

Planned server and enterprise connections include OpenStack Swift, Rackspace Cloud Files, SFTP, FTP/FTPS, WebDAV, SMB, Files.com, DRACOON, iRODS, and Spectra BlackPearl. Each integration will need its own authentication, capabilities, error handling, and acceptance tests. Features such as native version history and metadata retention vary by provider; the catalog does not promise identical behavior across protocols.

The catalog follows the families and profiles in [Mountain Duck's official protocol documentation](https://docs.mountainduck.io/protocols/) and its [S3 provider documentation](https://docs.mountainduck.io/protocols/s3/providers/), checked on 2026-10-03. It is a CloudBay roadmap, not a claim that those integrations are implemented or affiliated with their providers.

# CloudBay 2 architecture and acceptance criteria

CloudBay is a Windows desktop sync provider for Backblaze B2. The `legacy` branch preserves the mounted-drive folder migration application at `03656fb`. Main contains the new application, with independent cloud transport, reconciliation, Windows integration, and UI layers.

## Supported product behavior

- A private B2 bucket and a dedicated prefix hold cloud files and B2 version history.
- Windows Cloud Files registration provides Explorer navigation, status icons, download progress, pinned files, online-only files, and built-in availability commands.
- The process runs in the user's background session, survives closing its main window, starts at sign-in when selected, and provides a notification area activity flyout. A per-user mutex and restricted named pipe coordinate repeat launches and graceful shutdown.
- The UI uses WinUI 3 Mica Alt with Windows theme resources and keyboard-accessible settings.
- Backup supports Windows personal folders through documented Known Folder IDs and current policy capabilities, plus arbitrary personal data folders in their existing locations. Each custom folder is a separate native sync root, with its own durable baseline and cloud prefix.
- Windows personal folder backup copies and verifies the initial data, preserves conflicts and originals, then changes the current user's real Windows folder mapping. The app checks the live path, permissions, policy, and other provider ownership before redirection. Interrupted mapping operations are recovered from an atomic intent journal.
- B2 transport keeps one HTTP pool alive. Reusable upload URLs are leased exclusively to workers and retained after successful uploads. Multipart files use streaming bounded memory and ordered, verified SHA1 parts. Aggregate concurrency and speed limits include multipart work.
- The sync engine obtains complete cloud and local snapshots before inferring deletions. It compares them to a durable SQLite baseline, preserves conflicts with atomic renames, retries failures, and leaves unsynced edits dirty. Large deletion batches require review of the exact pending set.
- Routine B2 reconciliation pages through current file names rather than retained version history. Every page and continuation cursor is validated, and an incomplete listing prevents reconciliation. A hidden name is absent from the complete current snapshot; explicit version browsing still uses the historical endpoint.
- Deletion uses B2 hide markers, preserving versions subject to the bucket owner's lifecycle policies. Locally removed cloud content is retained in Recovery. Empty directories have versioned zero-byte trailing-slash markers.
- Credentials are protected by current-user Windows DPAPI outside the repository. Diagnostics never contain application keys or authorization tokens.
- Account disconnect first hydrates and explicitly reverts cloud placeholders, restores backed-up Windows folder paths, and removes only the provider registrations. Local files and B2 versions remain.

## Windows integration decisions

Cloud Files uses `StorageProviderSyncRootManager`, `CfConnectSyncRoot`, `CfCreatePlaceholders`, `CfExecute`, `CfUpdatePlaceholder`, `CfConvertToPlaceholder`, pin/in-sync state APIs, and explicit placeholder reversion before unregistering. Native x64 layouts are verified against the installed Windows SDK `cfapi.h`. Hydration uses the full policy and `AutoDehydrationAllowed`, so Windows Storage Sense owns eviction of eligible clean, unpinned content. The app exposes the Windows Storage Sense settings page; it does not invent a competing cache quota or retention policy.

Storage Sense cloud-content eviction must be enabled by the user and is supported on the Windows system drive. Setting the provider's auto-dehydration modifier makes content eligible; it does not turn on Windows policy or override pinned files. On other supported NTFS drives, native on-demand hydration and explicit availability/free-space commands still work.

Personal folders use `SHGetKnownFolderPath`, `SHSetKnownFolderPath`, `IKnownFolder::GetRedirectionCapabilities`, and Explorer change notification. The original mapping is recorded before mutation. Managed or existing OneDrive locations are protected from silent takeover. The user must stop the current provider's folder backup first.

Windows Backup's own Microsoft-account backup UI is a Microsoft/OneDrive feature. CloudBay supplies real Windows folder locations and native Explorer integration; it does not claim to replace Microsoft's Windows Backup service, image an operating system, or back up locked live registry/application profile state. Windows, installed program locations, application profiles, volume roots, linked directory trees, nested provider roots, and incompatible volumes are rejected as sync roots. Supported personal Known Folders are queried from Windows; unavailable or policy-controlled folders are disabled with their reason.

## Storage and recovery

Per-user app data is `%LOCALAPPDATA%\CloudBay\Client`: atomic settings with previous copies, DPAPI credentials, bounded activity history, account/root-specific SQLite databases, backup operation intent, and Recovery. Unsynced or online-only files requiring atomic recovery on their own volume remain under the root's excluded `.cloudbay\Recovery`. Custom remote namespaces are reserved under `.cloudbay-backups/` beneath the account prefix and excluded from the main root so they are not downloaded twice.

The reconciliation baseline survives restarts. Watcher events wake the engine; a complete periodic scan recovers missed events. Files are held against concurrent writes during hash/upload; late local edits during download are preserved before the verified remote data is installed. Placeholder identity contains the immutable B2 file version, so hydration fetches that exact object rather than a mutable filename.

An attribute-only native metadata probe checks dirty ranges without downloading cloud content. It detects local writes even when applications preserve file size and timestamps. Before marking an uploaded file clean, the provider verifies its SHA1 again under a referenced exclusive native handle, protecting a save that arrives between upload completion and native marking. Manual pause is inherited by new roots, and maintenance quiescence is retained while folder copying or unregister preparation runs.

## Release gates

1. Complete solution Release build with zero warnings and errors.
2. Transport tests with fake HTTP for session reuse, exclusivity, retries, auth renewal, range/hash integrity, multipart ordering/cancellation/finish recovery, and large legacy objects.
3. Reconciliation tests for complete-snapshot safety, dirty edits, concurrent saves, crash adoption, versioned deletion, directory markers, collisions, filters, pause/quiescence, and exact deletion review.
4. Real Windows Cloud Files tests in GUID-isolated user-profile roots: shell registration, native I/O hydration, pin/evict/rehydrate, dirty data protection, failed/canceled hydration, upload conversion, recovery, and data-preserving unregister.
5. Live tests against a bucket-restricted key, in a generated prefix only: small-file sessions, concurrent uploads, pagination, versions/restore/hide, full/ranged download, a >200 MB multipart file, canceled upload cleanup, native Explorer hydration, and controller multi-root lifecycle.
6. UI rendering across narrow/wide windows and light/dark themes; source uses actual native Mica Alt, which `RenderTargetBitmap` cannot capture from the desktop compositor.
7. Self-contained win-x64 publishing, installer/uninstaller path checks, Git whitespace verification, secret scan, and archive SHA256.

No finite test run proves correctness for every Windows installation, network interruption, or application workload. Public distribution additionally requires the publisher's signing certificate and testing on supported Windows release builds; the local artifact's actual verification scope is recorded in `VALIDATION.md`.

## Primary references

- [Microsoft: Cloud Files sync engine and Explorer integration](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine)
- [Microsoft: Known Folder APIs and redirection](https://learn.microsoft.com/en-us/windows/win32/shell/working-with-known-folders)
- [Microsoft: Known Folder IDs](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)
- [Microsoft: SHSetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shsetknownfolderpath)
- [Microsoft: redirection policy capabilities](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iknownfolder-getredirectioncapabilities)
- [Microsoft: Storage Sense and cloud providers](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/manage-drive-space-with-storage-sense)
- [Microsoft: Windows Backup](https://support.microsoft.com/en-us/windows/experience/backup-recovery/back-up-and-restore-with-windows-backup)
- [Backblaze: reusable upload URLs](https://www.backblaze.com/apidocs/b2-get-upload-url)
- [Backblaze: current file names and pagination](https://www.backblaze.com/apidocs/b2-list-file-names)
- [Backblaze: uploading large files](https://www.backblaze.com/docs/cloud-storage-create-large-files-with-the-native-api)
- [Microsoft: unregistering a Cloud Files root](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot)

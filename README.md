# CloudBay 2

CloudBay is a native Windows backup and sync client for **Backblaze B2**. It runs in the background, uses **WinUI 3 Mica Alt**, and integrates with File Explorer through the Windows Cloud Files API. Mountain Duck is not required.

The previous mounted-drive migration helper is preserved on the **`legacy` branch** at `03656fb`. This branch contains the new client.

## Features

- Notification area icon and activity flyout with upload/download history, pause/resume, quick settings, and full settings.
- Files On-Demand, native Explorer status, download progress, always-available files, and freeing local space.
- Windows Storage Sense integration for eligible clean, unpinned cloud content. Windows owns cache retention; configure it through **Settings → Sync → Windows Storage Sense**.
- Backup for Desktop, Documents, Pictures, Music, Videos, Downloads, Favorites, Contacts, Saved Games, Links, Searches, and 3D Objects where Windows makes them available. Turning on a personal folder backup changes its actual Windows default location after a verified copy. Apps using that Windows folder then save to CloudBay automatically.
- Custom personal folder backup in its existing location, with its own native sync root. No symlinks or mounted drives.
- Persistent B2 connections and reusable exclusive upload sessions for small files, streaming multipart uploads, concurrency controls, and shared upload/download speed caps.
- Durable sync state, conflict copies, exact-set review of large deletion batches, version-preserving B2 deletion, empty-folder sync, and previous-version restore.
- Optional pause on metered connections or Battery Saver, sign-in startup, exclusions, and Windows light/dark/system themes.
- Application keys encrypted with current-user Windows DPAPI. Account disconnect downloads and converts cloud files into normal local files before removing provider registrations.

## Run

Use the complete self-contained x64 release folder, or build from source:

```powershell
dotnet build CloudBay.sln -c Release
& '.\CloudBay\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\CloudBay.exe'
```

Windows 11 on x64 is recommended for Mica Alt. The project minimum is Windows 10 build 19041; Files On-Demand requires a **local fixed NTFS drive**. The release includes its .NET and Windows App SDK runtime files. Keep all files in the release's `App` folder together.

For a per-user installation, run **`Install.ps1` from the extracted release folder**. It adds a Start menu shortcut and a Windows Installed apps entry, without elevation. Before uninstalling, disconnect the account in CloudBay Settings so all local data becomes independent of the provider.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

## Connect B2

1. Create or select a private B2 bucket. Use a bucket-scoped application key with `listFiles`, `readFiles`, and `writeFiles` access and **Allow List All Bucket Names** enabled. Previous-version server-side restore also needs access to the version being restored.
2. Open **Settings → Account**, enter the bucket name, key ID, and application key. Advanced connection options let you choose a dedicated local CloudBay folder and a cloud prefix such as `CloudBay/`.
3. Connect. CloudBay discovers the account and bucket, registers its native Explorer folder, and starts syncing. Keep keys out of screenshots and diagnostic attachments.
4. Open **Folder backup** to choose Windows personal folders or add custom folders. If OneDrive or Windows policy already controls a folder, stop that provider's backup or resolve the policy first. CloudBay does not silently take over those mappings.
5. In Explorer, use availability commands for offline access. The **Files** page can pin/free files and folders, browse B2 versions, and restore a selected version. Its folder selector includes custom backup roots.

Closing the main window keeps CloudBay running. Use the tray menu's **Quit CloudBay** to exit. A second launch opens the running app.

## Data and recovery

State lives in `%LOCALAPPDATA%\CloudBay\Client`: settings and prior copies, DPAPI-protected credentials, root-specific SQLite sync baselines, backup intent, activity history, and Recovery. Some atomic recovery copies live under the sync root's excluded `.cloudbay\Recovery`. These copies are retained for review; CloudBay does not delete them automatically.

Deleting a synced file hides the current B2 name while retaining older versions. The bucket owner's B2 lifecycle policies determine how long those versions remain. Changes made on multiple computers are reconciled periodically; conflicting edits are preserved as separate files. CloudBay is a personal-folder backup/sync client, not a Windows disk image or a consistent snapshot of locked application/registry state.

Microsoft's **Windows Backup** app remains tied to Microsoft accounts and OneDrive. CloudBay uses the documented Windows folder defaults and Cloud Files integration; it does not replace Microsoft's Windows Backup service. Files On-Demand supports eligible NTFS folders, not a virtual-drive or read-only mode.

## Build and verify

```powershell
dotnet build CloudBay.sln -c Release -v:minimal
dotnet test CloudBay.Tests\CloudBay.Tests.csproj -c Release --no-build -v:minimal
.\scripts\build-release.ps1
```

The release script creates `artifacts\release\CloudBay-2.0.0-win-x64.zip`, an extracted package, and its SHA256 checksum. `CloudBay.exe --ui-smoke` captures the UI into `artifacts\ui-smoke` using fresh processes for both themes, isolated storage, and presentation fixtures for all seven client states. It checks focused Settings routes, retained drafts, keyboard focus, activity scrolling, provider discovery, and tray action reachability. These fixtures do not connect to a provider or modify the controller's account. The [live validation CLI](tools/CloudBay.Validation/README.md) uses a separately provisioned bucket-restricted key and a generated test prefix; it never redirects real Windows personal folders.

See [architecture and primary documentation](docs/ARCHITECTURE.md), [Windows 11 interface design](docs/UI-DESIGN.md), and [verified release scope](docs/VALIDATION.md). Public distribution needs the publisher's code-signing certificate; this local release is unsigned.

The Settings catalog also shows [planned providers and modes](docs/ROADMAP.md). Amazon S3, other cloud services and server protocols, virtual-drive access, and read-only access are marked **Coming soon**. Backblaze B2 with native backup is available in this release.

## Live development window

`scripts/run-dev-live.ps1 -Watch` keeps an isolated development copy open. While the watcher is enabled, every successful app build automatically writes `artifacts/live/build-ready.json` and refreshes the window. It copies completed binaries away from the build folder, so the open app does not lock the next build. A refresh gracefully restarts the window and restores its selected page, including a focused Settings detail page. Failed builds leave the current copy open; the last working build is retained for rollback.

Start the watcher in one PowerShell session, then build normally in another:

```powershell
.\scripts\run-dev-live.ps1 -Watch
dotnet build CloudBay.sln -c Release -v:minimal
```

The development copy uses `--ui-live`, separate per-user state, and a separate instance identity. It does not modify the regular account's state or the Windows sign-in startup entry. UI smoke captures run in a separate instance as well. This developer workflow is independent of public release updates.

# CloudInlet

Previously CloudBay. **Version 1.1.2** updates existing installations to the new name while preserving accounts, backups and transfer recovery. See [upgrade compatibility](docs/REBRANDING.md).

CloudInlet is a native Windows backup and sync client for **Backblaze B2**. It runs in the background, uses **WinUI 3 Mica Alt**, and integrates with File Explorer through the Windows Cloud Files API. Mountain Duck is not required.

The local `legacy` branch retains the previous mounted-drive migration helper at `03656fb`. The published repository contains the current native client.

## Features

- Notification area icon and activity flyout with live per-file uploads/downloads, waiting counts, recent history, pause/resume, quick settings, and full settings. Activity includes All activity, In progress, Queued, and History filters.
- Native Windows notifications with action buttons for reviewing problems, retrying sync, opening folders, and downloading or installing the matching update. Notification categories, completion summaries, and sound are configurable under Settings â†’ Notifications.
- Files On-Demand, native Explorer status, download progress, always-available files, and freeing local space.
- Windows Storage Sense integration for eligible clean, unpinned cloud content. Windows owns cache retention; configure it through **Settings â†’ Files and storage â†’ Windows Storage Sense**.
- Backup for Desktop, Documents, Pictures, Music, Videos, Downloads, Favorites, Contacts, Saved Games, Links, Searches, and 3D Objects where Windows makes them available. Choose the current Windows folder, its matching folder in another cloud account, or a browsed folder. Copy, verified move, and turning on without importing are explicit choices before changing the real Windows default location.
- Custom personal folder backup in its existing location, with its own native sync root. No symlinks or mounted drives.
- Reviewed imports from local folders, external drives, mounted drives, and existing Windows cloud folders. Whole-account shortcuts appear on Folder backup; personal-folder setup shows only matching folders from each account. Independent folder choices stay available while another change is queued or applying.
- Stopping personal-folder backup offers a selected local or existing cloud location with Copy or Move, or stopping without restoring files. Optional freeing of downloaded CloudInlet copies keeps B2 files; unsynced local contents remain on this PC. Verified moves retain changed or blocked originals and report them. Moving out of CloudInlet removes its current B2 copies through normal deletion sync; older versions follow bucket policy.
- Direct server-side import from Backblaze B2 buckets accessible to the connected account, with immutable source versions, explicit destination review, and interrupted-import history. OneDrive accounts are available through the direct transfer dialog; other planned connectors retain their Coming soon status.
- Direct OneDrive â†” Backblaze B2 transfers through CloudInlet, with real cloud accounts and folder browsing, Copy/Move, exclusions, conflict review, durable progress and bounded RAM streaming. The same endpoint adapters also support explicit local sources and destinations. See [cloud transfer behavior and validation](docs/CLOUD_TRANSFERS.md).
- Persistent B2 connections, reusable exclusive upload sessions for small files, resumable multipart uploads, parallel resumable downloads, separate upload/download concurrency, and shared speed caps.
- Original Windows and custom folder icons are retained when personal folders are backed up. Standard Windows compatibility junctions inside Documents are skipped without following their targets.
- Durable sync state, conflict copies, exact-set review of large deletion batches, version-preserving B2 deletion, empty-folder sync, and previous-version restore.
- Visual exclusions: browse for files or folders, choose file types, match names, or assemble advanced path patterns from named parts. Rules can apply to all backups, one backup, or a specific folder, and can be edited, disabled, or removed.
- Optional pause on metered connections or Battery Saver, sign-in startup, and Windows light/dark/system themes.
- Application keys, OneDrive tokens and upload-session checkpoints protected with current-user Windows DPAPI. Account disconnect offers **Disconnect only**, **Download files, then disconnect**, and **Remove local cloud copies, then disconnect**. Disconnect only is the default; local cleanup removes only verified unchanged copies and retains other files.
- GitHub updates retain the installed Debug/Release flavor and EXE/MSI installer. Settings â†’ About CloudInlet offers manual checks, an automatic check interval, optional automatic download, and optional silent installation. Microsoft Store packages use Store updates.

## Run

Use the complete self-contained x64 release folder, or build from source:

```powershell
dotnet build CloudInlet.sln -c Release
& '.\CloudInlet\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\CloudInlet.exe'
```

Windows 11 on x64 is recommended for Mica Alt. The project minimum is Windows 10 build 19041; Files On-Demand requires a **local fixed NTFS drive**. The release includes its .NET and Windows App SDK runtime files. Keep all files in the release's `App` folder together.

For a per-user installation, run **`Install.ps1` from the extracted release folder**. It adds a Start menu shortcut and a Windows Installed apps entry, without elevation. Before uninstalling, choose **Download files, then disconnect** if online-only files need to become independent local files.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

## Connect B2

1. Create or select a private B2 bucket. Use a bucket-scoped application key with `listFiles`, `readFiles`, and `writeFiles` access and **Allow List All Bucket Names** enabled. Previous-version server-side restore also needs access to the version being restored.
2. Open **Settings â†’ Account**, enter the bucket name, key ID, and application key. Advanced connection options let you choose a dedicated local CloudInlet folder and a cloud prefix such as `CloudInlet/`.
3. Connect. CloudInlet discovers the account and bucket, registers its native Explorer folder, and starts syncing. Keep keys out of screenshots and diagnostic attachments.
4. Open **Folder backup** to choose Windows personal folders or add custom folders. Review the current Windows location before enabling backup; you can also select an additional folder to copy. Redirected cloud folders require explicit review, and Windows policy restrictions remain enforced. Use **Import files** to copy an existing folder without changing its Windows location, or to import directly from an accessible B2 bucket.
5. In Explorer, use availability commands for offline access. The **Files** page can pin/free files and folders, browse B2 versions, and restore a selected version. Its folder selector includes custom backup roots.

Closing the main window keeps CloudInlet running. After an account is configured, normal launches, Windows sign-in startup and ordinary duplicate launches keep it in the tray. First-time or incomplete setup shows the window. Use the tray menu's **Open CloudInlet** to show it and **Quit CloudInlet** to exit; explicit `--show` also opens the window.

For OneDrive, open **Folder backup â†’ Transfer between clouds** or **Import files â†’ Cloud storage â†’ OneDrive â†” Backblaze B2**. Choose **Sign in with Microsoft**, then browse the actual account and cloud folder. CloudInlet supplies its public application registration; users do not need to configure Entra. Review both locations, Copy/Move, conflicts and exclusions before starting. The OneDrive sync client is optional. Direct cloud transfers retain metadata-only restart checkpoints and stream their payloads through bounded RAM. See [provider behavior, recovery and measured validation](docs/CLOUD_TRANSFERS.md).

Right-click the notification icon for a Fluent menu with **Open CloudInlet**, **Open folder**, **Pause/Resume syncing**, **Settings**, and **Quit CloudInlet**. The activity window and menu request foreground input when opened by the user, support keyboard focus, and dismiss when another window becomes active. The icon moves during active file work; periodic checking, up-to-date, paused, offline, and attention states have distinct still badges. Windows' reduced-motion and high-contrast preferences suppress animation.

Activity rows offer **Open folder** when their local folder is available. **View in cloud** opens that exact file's retained B2 versions in CloudInlet's Files page. Actions retain the event's backup root, bucket, and prefix and are hidden when those no longer match. Old history without saved location identity remains readable without guessing a destination.

## Choose exclusions

Open **Folder backup â†’ Exclusions** or **Settings â†’ Files and storage â†’ Exclusions**, then select **Add exclusion**. File and folder selections use the Windows picker and stay tied to the backup containing the selected item. File-type and name rules use ordinary text and matching choices. The advanced builder offers literal text, any text within a name, one character, folder separators, and any number of subfolders; parts can be moved or removed. Its preview and optional example check show how a rule behaves before saving.

Choose whether a rule matches files, folders and their contents, or both, then choose its scope. Exclusions skip backup and sync; existing local files and cloud copies are retained. Removing or disabling a rule resumes normal reconciliation. Previously saved patterns keep their original behavior until explicitly edited; disabling them preserves their exact pattern.

**Choose an example file** opens the native Windows chooser and fills in its extension without opening the file. Cancelling keeps the current draft. For a file without an extension, choose that individual file or use a Name rule. Selecting an entire backup folder as the scope applies the rule to that whole backup, including its subfolders.

## Data and recovery

State lives in `%LOCALAPPDATA%\CloudBay\Client`: settings and prior copies, DPAPI-protected credentials, root-specific SQLite sync baselines, backup intent, activity history, and Recovery. Some atomic recovery copies live under the sync root's excluded `.cloudbay\Recovery`. These copies are retained for review; CloudInlet does not delete them automatically.

Uploads of **64 MiB or larger** use streaming multipart transfer. Confirmed B2 parts survive an interruption and app restart when the source is unchanged; smaller uploads are atomic and retry that file from its beginning. Download staging saves confirmed chunks under the excluded `.cloudbay\transfers` directory, verifies saved bytes before reuse, and verifies the completed file before installation. Native on-demand reads can resume from Windows' retained cache after provider restart. Before releasing clean cloud data to an app, CloudInlet checks the assembled native cache, including the retained prefix, against the cloud version's whole-file checksum. A failed check gets one automatic fresh download; another failure stops the read. Unsent local edits are preserved. Foreign legacy versions without a whole-file checksum receive transport, identity, and length checks but cannot receive the same checksum guarantee.

Open **Settings â†’ Transfers and power â†’ Transfer performance** to choose Intelligent, Maximum throughput, or Manual. These preferences apply to local â†’ cloud, cloud â†’ local and cloud â†’ cloud transfers. Intelligent uses a bounded CPU/memory-aware budget; Manual exposes separate upload and download request limits shared across connected accounts, files and large-file parts. Existing aggregate speed caps still apply. Hashing and verification have their own shared limits, so increasing network slots does not create the same number of disk-hashing workers. Bounded preparation supplies tiny files while large sources prepare, and upload/download workers can start another file while earlier transfers are verified or installed. HTTP/TLS connections and upload sessions are reused across files. Each cloud file still requires provider requests, so tiny-file throughput depends on network latency as well as bandwidth. Live Activity and the tray show measured per-file and total upload/download speeds, with smoothing and idle expiry. Checkpoint bytes do not inflate rates, and verification shows its own phase before a native download enters completed history.

Private unfinished download parts older than seven days are removed during periodic maintenance when unlocked. Managed unfinished uploads are cancelled when their source disappears or their checkpoint has been inactive for seven days. These scratch checkpoints are separate from Windows' downloaded-file cache and from retained Recovery copies.

Deleting a synced file hides the current B2 name while retaining older versions. The bucket owner's B2 lifecycle policies determine how long those versions remain. Changes made on multiple computers are reconciled periodically; conflicting edits are preserved as separate files. CloudInlet is a personal-folder backup/sync client, not a Windows disk image or a consistent snapshot of locked application/registry state.

Microsoft's **Windows Backup** app remains tied to Microsoft accounts and OneDrive. CloudInlet uses the documented Windows folder defaults and Cloud Files integration; it does not replace Microsoft's Windows Backup service. Files On-Demand supports eligible NTFS folders, not a virtual-drive or read-only mode.

## Build and verify

```powershell
dotnet build CloudInlet.sln -c Release -v:minimal
dotnet test CloudInlet.Tests\CloudInlet.Tests.csproj -c Release --no-build -v:minimal
.\scripts\build-release.ps1
```

The central product version is in `version.json`. The release script creates `artifacts\release\CloudInlet-<version>-win-x64.zip`, an extracted portable package, and its SHA256 checksum. `CloudInlet.exe --ui-smoke` captures the UI into `artifacts\ui-smoke` using fresh processes for both themes, isolated storage, and presentation fixtures for all seven client states. It checks focused Settings routes, retained drafts, keyboard focus, activity scrolling, provider discovery, and tray action reachability. These fixtures do not connect to a provider or modify the controller's account. The [live validation CLI](tools/CloudInlet.Validation/README.md) uses a separately provisioned bucket-restricted key and a generated test prefix; it never redirects real Windows personal folders.

See [architecture and primary documentation](docs/ARCHITECTURE.md), [Windows 11 interface design](docs/UI-DESIGN.md), and [verified release scope](docs/VALIDATION.md). Public distribution needs the publisher's code-signing certificate; this local release is unsigned.

The Settings catalog also shows [planned providers and modes](docs/ROADMAP.md). Amazon S3, other cloud services and server protocols, virtual-drive access, and read-only access are marked **Coming soon**. Backblaze B2 with native backup is available in this release.

## Live development window

`scripts/run-dev-live.ps1 -Watch` keeps an isolated development copy open. While the watcher is enabled, every successful app build automatically writes `artifacts/live/build-ready.json` and refreshes the window. It copies completed binaries away from the build folder, so the open app does not lock the next build. A refresh gracefully restarts the window and restores its selected page, including a focused Settings detail page. Failed builds leave the current copy open; the last working build is retained for rollback.

Start the watcher in one PowerShell session, then build normally in another:

```powershell
.\scripts\run-dev-live.ps1 -Watch
dotnet build CloudInlet.sln -c Release -v:minimal
```

The development copy uses `--ui-live`, separate per-user state, and a separate instance identity. It does not modify the regular account's state or the Windows sign-in startup entry. UI smoke captures run in a separate instance as well. This developer workflow is independent of public release updates.

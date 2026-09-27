# CloudBay

CloudBay is an unpackaged WinUI 3 app for connecting Windows personal folders to an existing mounted cloud drive or sync folder. Its Windows 11 interface uses Mica Alt, a navigation shell, and system light or dark theme. It uses Windows Known Folder APIs for Desktop, Documents, Pictures, Music, Videos, and optional Downloads redirection. CloudBay keeps the original data when it copies files or changes a folder mapping.

## Requirements

- Windows 11 (build 22000 or later) recommended; Windows 10 build 19041 or later is the project minimum.
- .NET 8 SDK and the Windows SDK for building. Visual Studio 2022 with Windows App SDK C# support is recommended for XAML editing and debugging.
- An existing, writable mounted drive, network share, or local sync root. CloudBay does not create or authenticate a cloud mount.
- For project links, the local source must be on NTFS or ReFS. Creating a directory symbolic link requires Windows Developer Mode or an account with the symbolic link privilege. CloudBay reports a permission error if Windows denies creation.

## Build and run

From the repository root:

```powershell
dotnet restore CloudBay.sln
dotnet build CloudBay.sln -c Release
& '.\CloudBay\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\CloudBay.exe'
```

Open `CloudBay.sln` in Visual Studio to run and debug the x64 unpackaged profile. The project sets `WindowsPackageType=None` and `WindowsAppSDKSelfContained=true`; it runs with the current user's desktop permissions.

To create a self-contained x64 folder for distribution, run:

```powershell
dotnet publish CloudBay\CloudBay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false
```

The output is in `CloudBay\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish`. Keep the whole published folder together when copying it to another Windows PC.

## Using CloudBay

1. On **Dashboard**, choose an existing mounted folder or drive. CloudBay checks that it can read and write a probe file and shows available capacity when the provider reports it.
2. On **System Folders**, review each live Windows path and redirect the folders you want. Downloads stays local until you enable it in Settings. CloudBay scans for collisions, copies and verifies files, writes a journal, changes the shell path through `SHSetKnownFolderPath`, and tells Explorer to refresh. Original files remain in place.
3. On **Custom Links**, choose a local project directory and a cloud folder name. CloudBay copies and verifies the tree, retains a local backup beside the original, and makes the original path a directory symbolic link to the cloud copy. Unlinking reconciles cloud changes into the local backup with conflict copies, removes only the link, and restores the local folder. The cloud copy remains.
4. On **Developer Filters**, select preset or custom exclusion names and apply the provider rules. Git reads `.gitignore` within a repository. Rclone requires `--filter-from` pointing to the generated rule file. Mountain Duck and Cyberduck use their shared `%APPDATA%\Cyberduck\default.properties` preferences and must be restarted. A generated `.cyberduckignore` is an export only; Cyberduck does not automatically read it.
5. On **Settings**, choose System, Light, or Dark theme and review recent operation journals. Rollbacks change mappings or restore prior rule content only when their recorded state is still safe to restore. Copied user files are never purged by rollback.

On **System Folders**, use **Scan folders** to look for files left in inactive OneDrive locations. CloudBay keeps this potentially lengthy scan off the startup path. Review the recovery preview before copying those files to a local profile folder or the configured cloud root.

Operation journals are stored in `%LOCALAPPDATA%\CloudBay\Journals`; rule backups and app settings live under `%LOCALAPPDATA%\CloudBay`. Keep these files if you may need to investigate or roll back an operation.

## Safety and limitations

- CloudBay stops on file collisions, inaccessible paths, links inside migrated trees, and unsupported storage operations. It leaves copies in place for review after a partial failure.
- Windows may report space for a mount or its local cache rather than the cloud account quota. CloudBay hides capacity when a provider mount cannot expose a trustworthy volume value. A copy may still run with an explicit unknown-capacity warning; the original data is retained and each copied file is verified. Check the provider's own quota and sync status before removing any external backup.
- Rule files are provider specific. CloudBay does not start Rclone commands or force Mountain Duck/Cyberduck to reload their settings.
- Windows policy or another sync client may control a known folder. CloudBay detects external and OneDrive redirects and requires a separate recovery action before changing them.
- CloudBay does not delete local or cloud data to clean up after redirects, restores, or unlinks. You decide when retained copies can be archived or removed.

## Development

`CloudBay/Application` contains the application facade and per-user settings. `CloudBay/Core/Folders` contains Windows shell integration and data transfer; `CloudBay/Core/Links` contains local directory links; `CloudBay/Core/Filters` contains provider rules; `CloudBay/Core/Safety` contains durable journals. `CloudBay/ViewModels` and `CloudBay/Views` provide the MVVM interface.

Run `dotnet build CloudBay.sln` for the build gate and `dotnet test CloudBay.sln` for the test suite.

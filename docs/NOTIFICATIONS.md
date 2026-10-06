# Windows notifications

CloudInlet uses Windows App SDK app notifications. Windows controls banners, Notification Center, sound, and Do not disturb; notification failures do not pause backup or prevent updates. The live development preview and bitmap capture processes do not register or send real notifications.

Settings → Notifications has a master switch and separate choices for backup problems, folder backup changes, sync completion, app updates, and sound. Problems, folder changes, and updates are enabled by default. Completion summaries and sound are off by default. The Windows settings button opens the system notification controls.

Related actions are available directly in the notification:

| Notification | Actions |
|---|---|
| Backup needs attention | View activity; retry sync |
| Folder backup changed | Manage backup; open the configured CloudInlet folder |
| Transfers completed, when enabled | View activity; open the configured CloudInlet folder |
| Installed EXE/MSI update available | Download update; view update |
| Verified EXE/MSI update downloaded | Install update; view update |
| Portable update available | View update |

Notification actions use the current client state. An unconfigured client opens folder setup instead of attempting to open an unknown folder. Update buttons carry the specific release version; a stale button opens About CloudInlet and cannot authorize a different release. Download and install actions retain the updater's installation identity, package verification, and graceful shutdown rules.

New activity is combined into short summaries. Existing activity history is used as the startup baseline, and unchanged attention or offline state does not repeatedly produce banners. Optional completion summaries wait until transfers are up to date and a three-second quiet window has elapsed, including tiny transfers that finish between UI observations. Transfers completed while notifications are disabled do not replay when enabled. Notification Center groups replace earlier summaries. Disabling a category removes that category's owned entries, including entries retained from a previous app session. Only fixed commands and an update version appear in activation arguments; notifications do not expose private file paths, account keys, or raw exception text.

The activation handler is registered before Windows notification registration and lifecycle inspection. Cold activation is buffered until the client is ready; subsequent launches forward a bounded command through the same-user, same-build activation pipe. Unknown or malformed commands are rejected. Closing the app unregisters its active COM server while retaining Notification Center activation. Permanent EXE/MSI uninstall invokes the installed application's isolated registration cleanup after graceful shutdown; upgrades retain registration. Store packaging declares its notification COM activation extension.

Implementation follows Microsoft's [Windows App SDK notification registration and activation guidance](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet). These are local Windows notifications; a remote website push service is separate work.

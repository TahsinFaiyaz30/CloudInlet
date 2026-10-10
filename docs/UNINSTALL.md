# Uninstall, disconnect and retained data

CloudInlet uses the same backup, transfer and recovery features in its Microsoft
Store, EXE and MSI editions. Uninstalling the application is separate from
disconnecting a cloud account. Uninstall does not delete your cloud files, download
all online-only files, or automatically restore Windows personal-folder locations.

## Before uninstalling

If you want backed-up files available without CloudInlet, open the B2 account's
disconnect dialog and choose **Download files, then disconnect**. Keep the app
running until the operation completes. This requires access to the connected B2
account and enough local disk space. It downloads online-only files, restores the
backed-up Windows folder locations, converts the prepared placeholders into local
files and unregisters the prepared sync roots. If downloading fails, resolve the
failure and retry before uninstalling.

**Disconnect only** does not download files. It restores the backed-up Windows
folder locations without moving the files, and leaves downloaded files and
online-only placeholders in the CloudInlet roots. Reconnect the same account to
open the online-only content. **Remove local cloud copies, then disconnect** is a
separate explicit choice: it removes only copies whose cloud versions and local
state can be verified, retaining changed, unsynced or unverified files.

These choices concern the B2 backup account. They do not sign out separately
connected OneDrive accounts or erase their saved tokens. Pause any independent
transfers before leaving the app. Cancelling or disconnecting does not erase
files already transferred to a cloud provider.

## What uninstall retains

Windows removes the installed application and its package/installer-owned
components. CloudInlet does not delete cloud content or ordinary local files
during uninstall. It retains Windows folder mappings and its shared Release state at
`%LOCALAPPDATA%\CloudBay\Client`. The old directory name is a stable data identity,
not an additional installed CloudBay application. Debug uses its separate
`%LOCALAPPDATA%\CloudBay\Debug\Client` directory.

Retained state includes settings, encrypted B2 keys and OneDrive tokens (and any
backup copies), activity/search history, sync databases, transfer plans,
checkpoints and recovery records. B2 disconnect clears the B2 credential vault;
ordinary uninstall does not clear either account vault. Keys, tokens and protected
session checkpoints use Windows DPAPI for the current Windows user. This is not
portable backup of credentials to a different Windows account.

Online-only placeholders require CloudInlet and access to the corresponding cloud
account; uninstall does not turn them into independent local files. During Store
uninstall, Windows unregisters the packaged sync provider and can remove online-only
placeholder entries from its roots. The cloud files remain. EXE/MSI removal does
not perform that package-managed cleanup. Starting with 1.2.0, CloudInlet detects a
missing sync-root registration and uses the retained sync baseline to rebuild
missing placeholders after reconnecting, rather than propagate their absence as
cloud deletions. Recovery needs access to the cloud account; it cannot complete
offline.

To resume, reinstall CloudInlet under the same Windows user and use the existing
account and root. If the provider requires renewed authentication, reconnect it. Keep recovery
data until pending operations and any retained local recovery files are resolved.
Do not move or manually strip placeholder metadata to make an online-only file
appear downloaded.

## Removing saved account data

First finish the explicit download/disconnect workflow for every backed-up root,
verify the local files you need, and resolve pending transfers or local recovery
copies. Then quit CloudInlet and uninstall it. If you no longer want saved accounts,
history or restart recovery, you can explicitly delete the corresponding Client
directory above using File Explorer. This permanently discards the saved vaults,
their backups and recovery state; do not do it while any installed edition is
using that directory or if you still need its recovery contents. It does not
delete cloud data or files in your chosen backup roots. You can also revoke
CloudInlet's authorization directly with the relevant cloud provider.

For help, use [CloudInlet support](https://github.com/TahsinFaiyaz30/CloudInlet/issues).
Do not include application keys, tokens, private file contents or unredacted
account/recovery files in a public issue.

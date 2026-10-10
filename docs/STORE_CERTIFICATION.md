# Store certification response for 1.2.0

Draft text for product `9N63JNRR5VB4`, submission `1152921505702060020`.
Keep verification claims tied to the recorded evidence in `ASTRA_HANDOFF.md`.
Microsoft approval remains a separate external decision.

## runFullTrust (445 / 500 characters)

```text
CloudInlet 1.2.0 is a per-user WinUI 3 desktop backup and transfer app. Native Cloud Files callbacks, Explorer placeholders, user-selected folder monitoring, Windows known-folder backup and background/tray transfers require full-trust desktop execution. It runs as the signed-in user, without elevation or a system service. This is the same application engine as EXE/MSI. See Additional Testing Info for scope, safeguards and uninstall/recovery.
```

## unvirtualizedResources (498 / 500 characters)

```text
Required for user-approved Windows folder backup and shared recovery state. Windows 11 exclusions: LocalAppData\CloudBay\Client and HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\{User Shell Folders,Shell Folders}; other HKCU writes stay virtualized. Windows 10 uses the legacy broad switches. State survives uninstall; explicit disconnect restores folder mappings. Version 1.2.0 safely recovers OS-removed placeholders. See Additional Testing Info for necessity, safeguards and retention.
```

## Additional Testing Info

```text
CloudInlet 1.2.0 (x64) resubmission: policy 10.6.3, unvirtualizedResources.

PURPOSE AND NECESSITY
CloudInlet provides Backblaze B2 backup with Windows Files On-Demand and B2/OneDrive/local transfers. Store, EXE and MSI use the same application engine. Windows personal-folder backup is an explicit user choice: the reviewed setup changes the selected folder's real Windows location with SHSetKnownFolderPath, so Explorer and other applications use the chosen backup root. Package-private mapping writes cannot provide that system-visible behavior. The shared state directory preserves the existing user's accounts, sync baselines and unfinished transfer recovery across an explicit change between Store and EXE/MSI and after reinstall. Ordinary MSIX version updates alone are not the justification for external state.

PRECISE SCOPE
On Windows 11 the manifest uses flexible virtualization exclusions only for:
- $(KnownFolder:LocalAppData)\CloudBay\Client
- HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders
- HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders
CloudBay in this path is the stable historical data identity, not a second app. Other HKCU writes remain virtualized on Windows 11. Windows 10 build 19041 remains supported and uses desktop6 broad registry/file-system virtualization switches because it lacks the Windows 11 scoped schema.

ALTERNATIVES AND SAFEGUARDS
Default MSIX virtualization was tested: isolated writes beneath the two mapping keys were invisible outside the package. The scoped Windows 11 policy exposed those keys while an unrelated registry control stayed virtualized. Keeping state only inside the package would not meet cross-distribution/reinstallation recovery. Removing known-folder backup from Store would remove an existing product feature.
The app runs per user without elevation or a system service. Folder setup shows a review and rechecks the original mapping before applying; a changed mapping stops the operation. It validates local paths and rejects unsafe linked/conflicting roots. Windows folder policy restrictions are respected. Keys, tokens and protected session checkpoints use current-user DPAPI. Cloud-to-cloud payloads pass through bounded RAM; only recovery metadata is persisted. Normal file operations use destinations explicitly selected for backup or transfer.
We request the capability solely for the documented folder mappings and shared client state, not unrelated registry modification, machine-wide configuration or access to other users' data.

UNINSTALL AND DATA
Windows removes the installed app/package-owned components. Shared state remains at %LOCALAPPDATA%\CloudBay\Client: settings, B2/OneDrive credential vaults and backups, activity/search history, sync databases, transfer plans and recovery checkpoints. Ordinary uninstall does not clear credentials, delete cloud files, silently download all cloud data or restore user folder mappings. Retention allows recovery and deliberate installer switching.
Store removal unregisters the packaged sync provider and can remove online-only placeholder entries. Version 1.2.0 durably guards the prior sync baseline before registering a missing root, then restores missing placeholders from the cloud instead of treating their disappearance as cloud deletions. Recovery requires the same Windows user, retained state and cloud access.
Before uninstall, B2 account disconnect offers Download files, then disconnect (hydrate, restore mappings and detach prepared placeholders), Disconnect only (no download; restore mappings without moving files), or Remove local cloud copies, then disconnect (explicit verified-copy cleanup preserving changed/unsynced/unverified files). Failed downloads must be resolved before uninstall if independent local files are required. B2 disconnect clears the B2 vault; separately connected OneDrive accounts/tokens are unaffected. Users can remove saved local state after exiting the app and resolving recovery. UNINSTALL.md is included in every 1.2.0 application payload.

CHANGES AND LOCAL EVIDENCE
The package now declares windows.cloudFiles, required for its packaged native sync-root registration. Isolated native testing exercised registration, online-only hydration, restart, actual package removal/reinstallation, baseline recovery without cloud deletions, DPAPI reopening, retained journals and explicit placeholder detachment. Default/broad/scoped virtualization and unpackaged runtime were compared. Downloaded and pinned file bytes survived removal and were verified before reconnect. Real user settings, credential bytes and folder mappings remained unchanged. Fixtures used generated data and an in-memory provider; live B2/OneDrive and actual personal-folder redirection were not tested locally.

REVIEWER TESTING
Windows 10 build 19041+ or Windows 11 x64; Files On-Demand needs a fixed local NTFS drive. Reviewer B2 bucket/key and Microsoft test account entries are in the Credentials section. Use those isolated accounts and disposable folders; do not use personal data.
Connect B2 in Cloud services/settings, add a small disposable backup folder, then check Activity and Explorer availability actions. In a disposable Windows profile, review personal-folder backup and verify the Windows location after enable/stop. Test Copy in both B2/OneDrive directions, pause/resume and quit/reopen recovery using isolated destinations. Test each disconnect choice, offline/retry behavior, and removal/reinstallation with online-only and local files. Closing the window leaves the tray app running; use Quit CloudInlet to exit.

Support: https://github.com/TahsinFaiyaz30/CloudInlet/issues
Contact: tahsinfaiyaz30@gmail.com
Please do not include credentials or private recovery files in public issues.
```

## References

- [Microsoft flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
- [Microsoft restricted capabilities](https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations#restricted-capabilities)
- [Microsoft Cloud Files manifest extension](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-cloudfiles)
- Local [uninstall guide](UNINSTALL.md) and [distribution parity contract](INSTALLERS.md#store-exe-and-msi-feature-parity).

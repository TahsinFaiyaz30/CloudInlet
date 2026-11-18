# Store certification response for 1.2.1

Prepared on 2026-11-18 for product `9N63JNRR5VB4`. This is the full technical
reference, not a certification claim. The saved reviewer field uses a condensed
5,538-character version to fit Partner Center's limit. See
[publication evidence and current status](RELEASE_1.2.1_PUBLICATION.md) for the
verified 1.2.1.0 package and saved fields. The original 1.2.0 text
and rejection analysis remain in `STORE_CERTIFICATION.md` and
`STORE_CERTIFICATION_FOLLOWUP.md`.

## runFullTrust

```text
CloudInlet 1.2.1 is a per-user WinUI 3 desktop backup and transfer app. Native Cloud Files callbacks, Explorer placeholders, user-selected folder monitoring, Windows known-folder backup and background/tray transfers require full-trust desktop execution. It runs as the signed-in user, without elevation or a system service. This is the same application engine as EXE/MSI. See Additional Testing Info for scope, safeguards and uninstall/recovery.
```

## unvirtualizedResources

```text
Support: tahsinfaiyaz30@gmail.com (publisher Tahsin Faiyaz). Reconsideration of 10.6.3: user-approved Windows folder backup must update real Explorer mappings; package-private mappings cannot provide it. Shared per-user state enables recovery after reinstall or switching installers. Windows 11 uses scoped state-directory and two Shell-key exclusions; Windows 10 uses broad fallback switches. Exact paths, alternatives, safeguards and uninstall retention are in Additional Testing Information.
```

## Additional Testing Information

```text
RECONSIDERATION: POLICY 10.6.3 - UNVIRTUALIZEDRESOURCES
CloudInlet 1.2.1 (package 1.2.1.0, x64); product 9N63JNRR5VB4.
Publisher and direct support: Tahsin Faiyaz, tahsinfaiyaz30@gmail.com.

This response addresses the certification report completed on 15 October 2026. Please use the direct email above for publisher questions and customer support; no GitHub account or issue is required. The previous response identified GitHub Issues as Support. This response identifies the publisher's direct contact prominently and supplies the requested capability and uninstall details. Approval remains Microsoft's decision.

PURPOSE AND NECESSITY
CloudInlet provides Backblaze B2 backup with Windows Files On-Demand and local/B2/OneDrive transfers. Store, EXE and MSI share the application engine. The restricted capability supports two user-visible behaviors: Windows personal-folder backup that Explorer and other applications can see, and recovery of the same user's accounts and unfinished work after reinstalling or explicitly switching installer distribution. It is not requested merely to run a desktop app or preserve data during an ordinary Store update.
Personal-folder backup is an explicit reviewed choice. SHSetKnownFolderPath must change the real Windows folder location so other applications use the chosen backup root. Package-private mappings cannot provide this behavior. State shared with the desktop edition preserves the user's accounts, sync baselines and recovery journals through a deliberate distribution change or reinstall.

PRECISE SCOPE
Windows 11 uses flexible virtualization exclusions only for these three directories and two registry keys:
- $(KnownFolder:LocalAppData)\CloudBay\Client
- $(KnownFolder:LocalAppData)\CloudInlet\Client
- $(KnownFolder:LocalAppData)\CloudInlet\Migration\Release
- HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders
- HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders
CloudBay is the historical data identity, not a second application. Existing profiles keep their original directory unless the user explicitly approves migration. Fresh profiles use CloudInlet\Client; protected routing/recovery records identify the selected location. The two newer CloudInlet directories are additions since the original 1.2.0 submission. Other HKCU writes remain virtualized on Windows 11.
Windows 10 build 19041 is the package minimum. It lacks the Windows 11 scoped schema, so the manifest retains desktop6 broad file-system and registry virtualization-disable switches on Windows 10. We disclose that broader fallback rather than claiming narrow exclusions on all versions.

ALTERNATIVES AND SAFEGUARDS
Earlier isolated Windows 11 tests compared default virtualization, broad switches and scoped exclusions: isolated writes beneath the two mapping keys were invisible outside the default package; the scoped policy exposed those keys while an unrelated registry control remained virtualized. Package-private state alone does not provide cross-distribution/reinstallation recovery. Removing personal-folder backup only from Store would remove an existing feature.
The app runs as the signed-in user without elevation or a system service. It reviews the source and destination before changing a folder mapping, rechecks the original mapping, respects Windows folder policy and rejects unsafe links or conflicting roots. A changed external mapping stops the operation. Migration requires selection and confirmation; existing paths keep working if declined. Protected routing/journals pause normal work while unresolved migration needs recovery.
Credentials and protected session checkpoints use current-user Windows DPAPI. Cloud-to-cloud payloads pass through bounded RAM; only small recovery metadata persists. Normal file operations use destinations selected for backup or transfer. The capability is not requested for machine-wide configuration or access to other users' data.

UNINSTALL AND RECOVERY
Windows removes application/package-owned components. The three shared directories listed above are retained. Client state includes settings, B2/OneDrive credential vaults and backups, activity/search history, sync databases, transfer plans and recovery checkpoints. Migration routing/recovery records also remain so reinstall can find the selected client location.
Ordinary uninstall does not clear credentials, erase cloud files, download all online-only files, or restore user-selected Windows folder mappings. Before uninstall, B2 disconnect offers Download files, then disconnect; Disconnect only; or Remove local cloud copies, then disconnect. The first hydrates content before restoring mappings and detaching prepared placeholders. Disconnect only restores mappings without downloading or moving files. The cleanup choice removes only verified cloud copies, preserving changed, unsynced or unverified local files. Failed downloads must be resolved if independent local files are required. B2 disconnect clears its vault; separately connected OneDrive accounts are unaffected.
Store removal unregisters the packaged sync provider and can remove online-only placeholder entries. Reinstall under the same Windows user, retaining state and cloud access, allows the app to register the missing root and recover placeholders instead of propagating their absence as cloud deletions. This is not offline recovery or a portable credential backup. Users can explicitly remove saved state after quitting, uninstalling and resolving recovery. UNINSTALL.md is included in each application payload and identifies the retained locations and direct support contact.

VERSION CHANGES AND EVIDENCE LIMITS
1.2.1 adds reviewed migration and recovery of legacy app data/local folders/Explorer registration, installer recovery safeguards, tray positioning, page-entry icon animation and clearer taskbar/tray icons. Store updates remain managed by Microsoft Store; EXE/MSI updater behavior is not a Store installation requirement.
Prior isolated Windows 11 acceptance exercised packaged registration, hydration, removal/re-registration, retained DPAPI state and baseline protection using generated data and an in-memory provider. Later desktop migration checks exercised actual folder movement, Explorer location, external mapping conflict and restoration. Installer checks separately covered real Windows restart with synthetic full-app restart requirements and a native MSI 3010 deferred-cleanup fixture. The latter does not establish full-app UI recovery driven by naturally triggered 3010. These are recorded development results, not certification of this new Store binary. Windows 10 execution and production Store delivery remain unverified. No blanket claim of all live-provider acceptance is made.

REVIEWER TESTING
Use Windows 10 build 19041+ or Windows 11 x64 and a local fixed NTFS drive for Files On-Demand. Existing isolated B2/Microsoft test-account entries are supplied separately in the Credentials section; do not use personal data.
Connect B2 in Cloud services/settings, add a disposable folder, and inspect Activity and Explorer availability actions. In a disposable Windows profile, review personal-folder backup and verify the Windows location after enable/stop. Test Copy in both B2/OneDrive directions, pause/resume and quit/reopen with isolated destinations. Exercise the disconnect choices, offline/retry behavior and removal/reinstallation with online-only and local files. Existing legacy profiles can review migration in Settings > Updates; nothing is selected by default. Closing the main window leaves the tray app running; use Quit CloudInlet to exit.

Please reconsider unvirtualizedResources for these documented needs. If the scenario is ineligible, please identify the unsupported requirement so a supported implementation/distribution can be evaluated without silently removing backup or recovery features.
Direct publisher and customer support: tahsinfaiyaz30@gmail.com. Do not send credentials or private recovery files.
```

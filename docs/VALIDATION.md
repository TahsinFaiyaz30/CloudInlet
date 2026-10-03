# CloudBay 2 unsigned local release validation

Acceptance was performed on 2026-10-03 using Windows 11 Pro Insider Preview build 26340, x64, and .NET SDK 8.0.425. This is the unsigned local release requested by the owner. The previous migration helper remains on the local `legacy` branch at `03656fb`.

## Automated and native checks

The Release suite passed **140/140 tests, with zero skips** after atomic preference updates were introduced. The release script gates packaging on a complete Release solution build and the full MSTest suite, including actual Windows Cloud Files tests. Transport tests use deterministic HTTP responses to exercise reusable exclusive upload sessions, retries, authorization renewal, shared transfer caps, stalled sockets/responses, multipart ordering and cancellation, and acknowledgment recovery. Current-name pagination adds 26 cases covering malformed metadata, repeated or out-of-prefix cursors, duplicate names, restricted prefixes, nested directory markers, and interrupted snapshots preventing both local and remote deletion. Sync tests exercise persistent baselines, conflicts, deletion review, incomplete snapshots, concurrent saves, directory markers, path collisions, exclusions, and pause/quiescence. Preference tests verify concurrent field updates preserve account and backup ownership, queued updates snapshot caller collections, invalid or cancelled updates do not write, unchanged values do not rotate settings, isolated previews do not change Windows startup, and failed persistence restores the exact previous startup value and registry type.

Native tests register GUID-isolated roots in the current user's profile. They verify ordinary Windows file reads hydrate the selected immutable B2 version, clean files can be evicted and rehydrated, scans do not hydrate online-only files, dirty bytes are protected, same-size edits with preserved timestamps are uploaded, recovery survives removal, and explicit placeholder reversion retains ordinary local files after unregistering. Registration and restart tests also passed five consecutive repetitions. Verified folder-copy tests include real destination junctions at the root, ancestor, and nested levels; rejected copies leave source and outside data unchanged.

The local build and test output is available under `artifacts/validation/` and `artifacts/tests/`; the preference suite report is `artifacts/validation/tests/preferences-backend-release-final.trx`. Generated outputs and credentials are excluded from Git.

## Live Backblaze B2 acceptance

A bucket-restricted application key was used only with generated `CloudBayValidation/<GUID>/` prefixes. The product controller acceptance covers connection, DPAPI settings, multiple native roots, B2 version retention, pin/free/hydration, timestamp-preserving edits, pause inherited by a newly added root, durable restart, custom-folder removal, safe full disconnect, and immediate retries after injected Windows unregister failures. It confirms the exact previous startup registration is restored and the real Windows personal-folder mappings are unchanged.

Full transport acceptance passed **11/11 checks**: it streams a 205 MiB multipart file, verifies the complete download and precise byte ranges, cancels another multipart upload and checks B2 acknowledged its cleanup, and exercises native on-demand hydration. Eighteen small-file uploads reused four upload endpoints, with zero token-sharing violations. This full run took 246.84 seconds, with a process peak working set of 104.1 MiB; it is an observed run rather than a general performance guarantee. Reports contain safe operation counts and results, with no credentials, authorization headers, or upload URLs. Test cleanup hides only the generated objects; their older B2 versions remain subject to the bucket's lifecycle policy.

Use the [validation CLI](../tools/CloudBay.Validation/README.md) for reproducible commands. Fresh acceptance reports remain in `artifacts/validation/b2-<GUID>.json`.

After the current-name listing change, the smaller native/B2 run passed **10/10 checks**. It includes real current-name pagination at three objects per page and verifies hidden names disappear from the current snapshot while their versions remain available. After the preference update change, fresh controller acceptance passed **15/15 checks**, including concurrent updates while paused: account and backup ownership, native registration IDs, and global pause remain intact, with no reconnect. `artifacts/validation/backend-release-summary.json` links the successful reports, separating them from retained reports of earlier failures.

## UI and distribution

The native WinUI application rendered 59 screenshots, including light/dark tray windows and all five pages at widths of 800, 1100, and 1300 pixels. Thirty page-bound assertions passed. Backup and settings pages were inspected at their middle and bottom scroll positions. A smoke assertion verifies that background settings refreshes preserve unsaved form values. Mica Alt is configured on the actual windows; bitmap capture records the XAML content rather than the desktop compositor backdrop.

The release is self-contained for Windows x64 and includes its .NET and Windows App SDK runtimes. The package contains `App/CloudBay.exe`, per-user installer and uninstaller scripts, documentation, license, and an external SHA256 checksum. Publishing and same-version installation use fresh staging folders so obsolete files do not accumulate in the active package. Previous package/install folders are retained for rollback until explicitly removed or uninstalled.

Installer acceptance passed a real per-user install, Start menu target and Installed apps checks, same-version reinstallation with an obsolete-file sentinel, preservation of the previous installation, refusal to uninstall while an injected current-user provider registration exists, and complete removal of binary folders and app entries. The previous startup registry value was restored exactly. The test left no installed application or provider registration; see `artifacts/validation/installer-roundtrip.json`.

The published runtime was verified from both dependency and runtime configuration files: .NET 8.0.31 and Windows App SDK 1.8.260921001. Keep the runtime patched and migrate before [.NET 8 support ends on November 10, 2026](https://dotnet.microsoft.com/en-us/platform/support/policy).

The GitHub Windows build workflow is included but has not been run remotely. Hosted Windows Server runners run transport/reconciliation tests; native desktop acceptance is performed locally.

## Verified scope and remaining release requirements

- All 12 supported Windows personal folders were queried through their real Known Folder IDs, default/current paths, and redirection policy capabilities. Their actual default locations were not changed during acceptance. Initial copy, collision preservation, source-change checks, and journal recovery are covered independently; real personal-folder enable/restore should be verified in a disposable Windows user profile before public distribution.
- Acceptance used one Windows Insider build and x64. Windows 10, stable Windows 11 builds, other NTFS drives, and managed enterprise policies need a compatibility matrix before public distribution.
- Windows owns eligible cloud-content eviction. Storage Sense must be configured by the user and automatic cloud-content cleanup applies on the system drive. Pinning and explicit free-space commands remain available on other supported local fixed NTFS drives.
- Native provider disconnect cancellation is tested. An explicit synchronous Windows hydration request checks caller cancellation between files; an individual request completes or is bounded by the provider's network inactivity timeout.
- This artifact is unsigned. Public distribution requires a publisher code-signing certificate and the compatibility acceptance above.
- Automatic approval review rejected cleanup of 78,096 bytes of state from an earlier isolated test run, with no additional reason. Those three temporary SQLite files were retained. Later run cleanup is reported separately and does not alter that retained state.

See [architecture and primary references](ARCHITECTURE.md) for the Windows and B2 contracts used by the implementation.

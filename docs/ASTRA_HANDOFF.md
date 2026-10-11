# CloudInlet configuration and verification handoff

## Current audit addendum — 2026-10-11

**1.2.0 is published:** immutable tag `v1.2.0` points to `7c1b42f6edd3c1c7b0b1a8a89b92cb24916fe478`. Windows CI `38114892013` and release workflow `38115258247` succeeded. Each hosted Debug/Release build passed 828 tests with zero failures/skips (Native and LiveProvider excluded); the unchanged application source also has the 959-test local desktop gate below. All sixteen release assets are present, and schema 2 exposes six desktop variants. The downloaded Store upload, its embedded MSIX, metadata, updater manifest and validation/checksum files were checked against GitHub SHA-256 digests and lengths. Production identity, manifest capability scope, stable Windows IDs and required notices passed inspection; no obsolete `CloudBay.exe` launcher is present. Evidence is under `artifacts/store-certification/release-v1.2.0-7c1b42f/`, particularly `verified-release-summary.json`, `manifest-verification.json`, `payload-verification.json` and `workflow-success.json`. Do not replace any published release bytes.

**Store resubmission completed on 2026-10-11:** the verified `CloudInlet-1.2.0-win-x64-release-store.msixupload` replaced 1.1.2 in Partner Center submission `1152921505702060020` for product `9N63JNRR5VB4`. The overview confirmed the package as Validated before resubmission, then showed **In certification**, with **Pre-processing** in progress and automatic publishing after approval unchanged. Microsoft certification is pending; this is not an approval claim. The 5,922-character reviewer notes were saved and reloaded, preserving the five masked reviewer credential entries. Proof: `artifacts/store-certification/submitted-1.2.0.jpg` and `reviewer-notes-final-saved.jpg`. The upload page's progress display had remained stale at 99%; a fresh package-page read confirmed the server had finished, and Save succeeded there. No duplicate upload was necessary. The prior failed release attempts below are historical and must not be resumed or used for replacement assets.

The second publication attempt (`9e7bdc1`, Windows CI `38084530945`, release `38084981643`) again passed CI and all Debug/Release/Store packaging jobs. Its merged workflow inventory also contains the four per-installer `.sha256` sidecars emitted by `build-installers.ps1`, which the earlier nine/twelve-file fixtures omitted. Publication now accepts only their complete exact-name group and verifies every digest and referenced filename against the final installer bytes. Signing refreshes those sidecars after successful Authenticode verification. Per-flavor evidence now uses `Debug-build-validation.json` and `Release-build-validation.json`, avoiding the case-insensitive Windows collision between the old `Release-validation.json` and combined `release-validation.json`. A Store-enabled workflow therefore publishes sixteen files; the updater still offers six desktop variants. Partial, stale and unexpected inventories remain rejected before GitHub mutation. The expanded regression suite passed 116 assertions without network access (`artifacts/store-certification/release-scripts-workflow-sidecars-final.log`). The certification notes now explicitly identify local validation-package removal/re-registration and distinguish it from untested production Store delivery and Windows 10 execution.

The first 1.2.0 source push (`b6acd76`) passed Windows CI, but release run `38014788075` failed its Debug stale-import regression before publishing a release. Equal-size writes with restored modification timestamps can also retain the same native change timestamp; the metadata-only review was insufficient. The follow-up uses guarded file USNs (content hashes for available files without a journal), rejects active writers including named streams, binds the reviewed plan to the copy's initial snapshot, and checks versions under the copy guard. Native tests also cover the no-hydration preview requirement: read-data access can hydrate a placeholder even with no-recall flags, so online-only guards use DELETE access solely for sharing protection, never to delete anything. If that access is unavailable, download-first guidance avoids weakening validation. All earlier local 1.2.0 packages predate this fix and must not be uploaded. Use the final verification entry below when available.

Final import-fix verification passed: Release solution build, zero warnings/errors (`artifacts/store-certification/build-import-verified.log`), then **959/959 desktop tests, zero failures/skips**, excluding LiveProvider (`tests-import-verified.log`, `tests/release-import-verified.trx`). The focused 64-case suite includes deterministic native timestamp restoration, file and directory stream writers, readonly ACLs, native online-only preview/copy, named streams and collision preservation (`import-ads-acl-final.log`). A preceding 44-case Debug copy/import suite passed; hosted Debug/Release gates run again from the final committed source. Earlier failed experiments and the corrected ACL-test cleanup failure are retained. The failed ACL fixture was removed after exact path/content/rule checks; its cleanup evidence is `acl-fixture-cleanup-5471d5e39743443090faa3414481d688.json`. Evidence names without an explicit prefix in this paragraph are beneath `artifacts/store-certification/`.

Source `4fc7ca4` passed Windows CI `38064293477` and the Debug, Release and Store packaging jobs of release run `38064720751`. Publication alone failed before tag creation: the retired-compatibility inventory check allowed nine desktop release files while the configured Store adds three canonical files. The publishing fix explicitly allows the complete matching three-file Store group, checks its version/revision/production identity flag, and still rejects partial or unexpected inventories. The updater manifest retains exactly six desktop variants. No 1.2.0 tag or release existed when this follow-up began. The new local app is `artifacts/store-certification/app-1.2.0-verified/CloudInlet.exe`; its isolated unpackaged/candidate-Store removal-and-reinstall acceptance passed at `parity/a6058b624b044716bd5e000f0a487090/summary.json` beneath that evidence directory. The follow-up release-script suite passed all 94 assertions without network access (`artifacts/store-certification/release-scripts-store-group.log`), covering both nine-file desktop and twelve-file Store releases, incomplete/invalid groups and immutable remote bytes. Use the eventual successful release assets for Partner Center upload.

Start with [FULL_AUDIT.md](FULL_AUDIT.md) for the current fixes, evidence and remaining acceptance boundaries. The working tree now contains the continuing UI redesign, backend fixes, small-window layout work and compatibility retirement; the original three-file setup scope below is historical. Final Release build/publish passed without warnings/errors; 947 desktop tests and the later 58-test updater rerun passed. Final light/dark UI suites passed with 452 full-sweep and 104 focused-layout captures. Live provider acceptance remains unrun. The latest local app is `artifacts/full-audit/audit-final-app/CloudInlet.exe`; validation metadata is `artifacts/full-audit/audit-final-summary.json`.

The user approved retiring obsolete compatibility code **after the frozen 1.1.2 bridge**. Future releases use canonical CloudInlet schema 2 and packages only: no newly generated CloudBay assets, schema-1 manifest or launcher. Preserve existing registered install directories and durable data/Windows identities, as specified by `AGENTS.md` and [REBRANDING.md](REBRANDING.md). This policy supersedes the old launcher-preservation wording. The user selected **1.2.0** for the next release; locally compiled acceptance packages must not replace published 1.1.2 bytes.

The Store certification report (completed 2026-10-09, product `9N63JNRR5VB4`) denied `unvirtualizedResources` under 10.6.3 and requested uninstall details. The user requires Store, EXE and MSI feature/data-safety parity. See [the distribution parity contract and remaining acceptance](INSTALLERS.md#store-exe-and-msi-feature-parity).

The continuing 1.2.0 remediation adds the missing `windows.cloudFiles` extension (packaged native registration previously failed with 0x80070490), narrows Windows 11 virtualization exclusions, and fixes recovery after package removal deletes online-only placeholders. A durable baseline guard is committed before registering a missing root; sync restores OS-removed entries instead of hiding cloud files. Initial regression tests reproduced the remote-deletion bug before the fix. Real isolated removal/reinstallation passes for all four tested policies in `artifacts/store-certification/parity/7d2bdc8e9a744079a2f7c98a1b56ad8d/summary.json`, including online-only, downloaded and pinned placeholders. Downloaded/pinned bytes were verified before reconnect, so rehydration could not mask their loss. Cleanup preserved real user state. No live B2/OneDrive acceptance or real personal-folder redirection was performed. Earlier activation-only evidence must not be presented as lifecycle coverage.

The final 1.2.0 Release build passed with zero warnings/errors (`build-1.2.0-final.log`) and self-contained publish completed (`publish-1.2.0-final.log`). The 951-test run passed 950 and exposed a missing fixture-prefix whitelist entry in the new test's cleanup; the new native test had reached its cleanup. After fixing that test-only whitelist, all six `NativeRegistrationTests` passed (`native-registration-final.log`, TRX in `tests/`). Preserve the original failure log rather than claiming a single clean 951-test run. Release automation previously passed all 73 assertions (`release-scripts.log`); product recovery changes did not alter those release rules. Final app: `artifacts/store-certification/app-1.2.0-final/CloudInlet.exe`. All paths in this paragraph are under `artifacts/store-certification/` unless fully stated.

The active Partner Center draft is submission `1152921505702060020`. On 2026-10-10 its Additional Testing Info page contained five masked reviewer credential entries (B2 bucket/key ID/key and Microsoft email/password); their values were not read or tested locally. The published 1.1.2 release remains untouched. Check current browser/package state before continuing an upload or submission.

## Historical configuration snapshot — 2026-10-07

Checked on **2026-10-07** in `C:\Users\tahsi\Documents\CloudInlet`.
Baseline source: `ebd61f65898a9909009d55722c3268ba429cba0d`, `main`, version **1.1.2**. Start with `git status` and `git diff`; setup edits are uncommitted.

## Completed configuration audit

CloudInlet retains the original source repository (GitHub ID `1389995455`, created September 27). The current CloudBay repository is the recreated, archived static upgrade bridge (ID `1407896272`, created October 6). Do not copy its archived state or disabled issue/wiki features into CloudInlet.

Local origin and `main` tracking already point to `TahsinFaiyaz30/CloudInlet`. All projects, canonical release names, four active workflows and schema-2 updater trust use CloudInlet. Actions permissions/settings, default branch and merge settings require no parity repair. All four `CLOUDINLET_STORE_*` identity/application variables are present. `github-release` exists; signing secrets are optional and absent.

Automated Partner Center submission still lacks the `microsoft-store` environment and its `CLOUDINLET_STORE_TENANT_ID`, `CLOUDINLET_STORE_CLIENT_ID`, `CLOUDINLET_STORE_CLIENT_SECRET` credentials. Neither repository has credentials to copy. Store package-only builds already work; submission needs separately supplied owner credentials.

Already successful at the baseline revision:

- [Windows CI](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/37530626936): solution build/tests and silent EXE/MSI install/update fixtures.
- [GitHub release](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/37531584421): Debug/Release packages and canonical 1.1.2 publication.
- [Microsoft Store](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/37538745268): packaging passed; submission skipped.

Canonical 1.1.2 has 20 assets; bridge 1.1.2 has seven. Both manifests list six update variants. All 12 manifest lengths/SHA-256 values match GitHub asset metadata; all six legacy aliases match across repositories. Existing published bytes/tags were not changed. GitHub server-side release immutability is disabled; source release scripts enforce their own existing-release checks.

## Setup changes

- `scripts/test-store-runtime.ps1`: accept the intentionally stable manifest application ID `CloudBay` and derive the activation AUMID from that validated ID. The migrated test previously expected `CloudInlet` and could not activate the generated package. Executable stays `CloudInlet.exe`; isolated package stays `CloudInlet.LocalValidation`.
- `AGENTS.md`: compact project map, commands, validation boundaries and preserved identities for subsequent work.
- This handoff: configuration facts, evidence pointers and remaining full acceptance.

## Fresh local verification

Evidence root: `artifacts/repository-setup/` (ignored, available in this checkout).
GitHub settings and bridge checks are saved in `github-configuration.json` and `github-release-bridge.json`.

| Gate | Result | Evidence |
|---|---|---|
| Release solution build | Pass, 0 warnings/errors | `release-build.json`, `release-build.log` |
| Final desktop tests, excluding LiveProvider | Pass, 938/938, 0 skipped | `release-tests-final.trx`, `release-tests-final.json` |
| Isolated release automation | Pass, 82 assertions | `release-script-tests-result.json`, `release-script-tests.log` |
| PowerShell parser | 19 scripts, 0 errors | `powershell-syntax-result.json` |
| Workflow actionlint 1.7.12 | All 4 workflows pass, 0 diagnostics | `workflow-actionlint-result.json` |
| Initial desktop tests, excluding LiveProvider | 937 passed, 1 failed, 0 skipped | `release-tests.trx`, `release-tests.log` |
| Native failure follow-up | Target passed in 3 fresh processes; class passed 5/5 | `native-recheck/summary.json` |
| Actual Store publish/package/activation | Pass, version 1.1.2, process exit 0 | `store-runtime-summary.json` |

The initial native failure was `SameMetadataSaveAfterUploadCannotBeMarkedCleanOrEvicted`, final `FreeSpaceAsync`, with Windows oplock contention `0x80070322` at `CloudInlet/Windows/CloudFiles/WindowsPlaceholderService.cs:538`. The owning process/cause is unproven; the product implementation is unchanged from before the rename. Three fresh-process follow-ups and the final full 938-test rerun passed without product/test changes. Original failed evidence is retained; investigate if it recurs.

The corrected Store test actually registered the isolated package and activated `!CloudBay`; it confirmed the Store channel, notifications and disabled `CloudBayStartup`. Cleanup removed the test registration, preserved client state and folder mappings, and changed neither certificate trust nor Developer Mode. This verifies local runtime acceptance, not Store certification/submission.
`store-script-syntax-final.json` records the changed script's successful parse and hash; the Store runtime summary records the tested script hashes. Portable actionlint was downloaded from the official release and its SHA-256 verified before execution.

## Astra full check

Read this file and `AGENTS.md`, inspect the three-file diff, and use the compact JSON evidence before opening large logs. Recheck only uncertain or affected setup facts; complete the requested broader source review and acceptance. `docs/VALIDATION.md` contains historical results, not fresh evidence for this checkout.

Use .NET 8 and PowerShell 7. On this machine the available PowerShell 7 executable is `C:\Users\tahsi\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe`; use it when `pwsh` is absent from PATH.

```powershell
dotnet build CloudInlet.sln -c Release -v:minimal
dotnet test CloudInlet.Tests/CloudInlet.Tests.csproj -c Release --no-build --filter 'TestCategory!=LiveProvider' -v:minimal
pwsh -NoProfile -File scripts/test-release-scripts.ps1
pwsh -NoProfile -File scripts/test-installers.ps1
```

For full UI acceptance, publish the app and run its isolated `--ui-smoke` / tray-focus probes; save fresh evidence. Installer acceptance above is hosted baseline evidence and was not rerun locally during setup. Live B2/OneDrive tests need explicitly isolated provider accounts/prefixes and opt-in credentials. Keep cloud payloads in bounded RAM and preserve restart checkpoints.

Preserve the durable CloudBay identities documented in `docs/REBRANDING.md`, especially state/DPAPI, native registrations, installer IDs and Store application/startup IDs. Preserve the already-published 1.1.2 launcher/schema-1 bridge bytes and historical tags, but do not ship a launcher or generate schema 1 in future releases. A remaining old name is not by itself a migration bug. The current audit addendum above supersedes this snapshot where the upgrade policy or validation state has changed.

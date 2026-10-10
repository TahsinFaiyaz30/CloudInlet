# CloudInlet audit and acceptance — 2026-10-09–10

This audit continues the existing frontend redesign and checks the backend, updater, packaging and Windows integration around it. The working tree contains both earlier UI work and audit fixes. Review the actual diff before attributing or changing those edits. The verification below is the completed UI/backend phase; subsequent Store remediation is tracked in [ASTRA_HANDOFF.md](ASTRA_HANDOFF.md).

That phase used **1.1.2**. The user subsequently selected **1.2.0** for the Store remediation release. Packages built under `artifacts/full-audit/` remain local acceptance artifacts, including synthetic newer installer versions. **Do not publish these artifacts over 1.1.2.** Build 1.2.0 through the normal release gates.

## Fixed defects and source review

| Area | Change and reason | Source / regression coverage |
|---|---|---|
| Local directory transfers | Directory last-write changes caused by deleting verified child files no longer invalidate the source directory. Creation identity and path validation remain; files still require their version and integrity checks. | `CloudInlet.Core/Transfers/LocalTransferEndpoint.cs`; `CloudInlet.Tests/LocalTransferDirectoryTests.cs` |
| Diagnostic recovery | Locked, unreadable or malformed optional diagnostics no longer prevent client startup. Invalid event entries are skipped; the diagnostic file is preserved. | `CloudInlet/Application/ClientStorage.cs`; `CloudInlet.Tests/ClientStorageTests.cs` |
| Independent cloud jobs | Pause/resume is available for independent transfer jobs without requiring a configured B2 backup. An account-attention aggregate state no longer hides an active manual pause or makes Resume unreachable. | `CloudInlet/Application/ClientController.cs`, `ClientController.CloudTransfers.cs`; `CloudInlet.Tests/CloudTransferControllerTests.cs`; main-window, view-model and tray consumers |
| SQLite dependency | The native SQLite bundle was updated to `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13. The loaded engine was verified as 3.53.3; a runtime regression test enforces the required minimum. | `CloudInlet.Core/CloudInlet.Core.csproj`; `CloudInlet.Tests/SqliteRuntimeTests.cs` |
| UI state transitions | Corrected selected-section navigation, OneDrive-only startup/pause state, import connection handoff and folder-icon refresh after display-scale changes. A native selection reset now restores the current section instead of navigating Home or losing a nested settings route. | `CloudInlet/MainWindow.xaml.cs`, `MainWindow.FolderPresentation.cs`; `MainWindow.AuditValidation.cs`, `MainWindow.ResponsiveValidation.cs` |
| Recycled transfer rows | Reproduced a native `E_INVALIDARG` crash when dynamic bitmap `IconSourceElement` content was recycled in transfer rows. Dynamic row glyphs now bind directly to `FontIcon`; static branded artwork remains. This also fixes the corresponding tray measurement crash. | `CloudInlet/MainWindow.xaml`, `CloudInlet/App.xaml`, `CloudInlet/Views/TrayWindow.xaml` |
| Small-window layouts | Action groups use measured wrapping instead of switching all actions to a column at a broad breakpoint. Home, Transfers, Updates, Appearance, Backup, settings and transfer-review layouts make better use of the available width. Appearance supports intermediate column counts; compact numeric settings retain usable inputs. Transfer actions reflow when availability changes, even if the panel's size stays the same. Each file combines status, speed, percentage and transferred size on one line above its progress bar; aggregate speed shares the activity count line. These lines wrap only when necessary. | `CloudInlet/Views/ActionWrapPanel.cs`, `MainWindow.Fluent.cs`, `MainWindow.SettingsLayout.cs`, `MainWindow.Appearance.cs`, `MainWindow.xaml.cs`, `Views/SourceImportDialog.cs`, `ViewModels/ClientViewModel.cs`; `MainWindow.ResponsiveValidation.cs`, `MainWindow.UpdateValidation.cs` |
| Upgrade compatibility retirement | Future update selection and downloads trust canonical CloudInlet schema 2 only. Future packages no longer build the CloudBay launcher or aliases; release generation rejects schema-1 leftovers. Installer paths remain stable so the frozen 1.1.2 worker can reopen the updated app. | `CloudInlet.Core/Updates/`, `packaging/`, `scripts/generate-update-manifest.ps1`, `scripts/publish-github-release.ps1`; `CloudInlet.Tests/UpdateCoordinatorTests.cs`, release and installer fixtures |

The directory-transfer regression was reproduced before its fix (two failures) and passed afterward (four tests). Those records are retained as `artifacts/full-audit/local-folder-red.trx` and `local-folder-green.trx`. Earlier failed or interrupted UI/installer runs are also retained; use the specific successful runs below rather than assuming every artifact directory is a passing result.

The final UI review also reproduced a native selection reset sending Activity to Home (`navigation-reset-red/artifacts/ui-smoke/responsive-layout.txt`). Selection recovery now preserves the current page without reloading its contents; focused validation covers both Activity and Updates. The preceding broad navigation assertion failure remains under `ui-complete-final/`; the diagnostic sweep passed before the explicit reset regression was added, so it is not proof of the original failure's exact trigger.

The Updates card now uses short messages such as "CloudInlet 1.1.3 is available" and a single wrapping group containing download/install, check, cleanup and release-note actions. Installer matching remains internal. This removes the redundant "installed package type" heading and the separately indented Check button.

## CloudBay bridge contract

Original CloudBay clients take the existing, immutable **1.1.2** bridge first. Its updater then uses `TahsinFaiyaz30/CloudInlet` and `updates-v2.json`. The archived CloudBay repository keeps its existing seven bridge assets and needs no future synchronization.

New releases contain six canonical packages, schema 2, checksums and release validation. They must not contain newly generated CloudBay aliases, `updates-v1.json` or the compatibility launcher. Existing registered install directories are retained, including CloudBay-named directories. An already-installed EXE launcher can remain as an old installer-owned file until normal uninstallation; future packages do not supply it.

Stable user-data and Windows identities remain intentional: state paths, DPAPI entropy, journals and `.cloudbay` recovery metadata, sync roots, pipes/mutexes, installer identities and distribution registry keys, Store application ID `CloudBay`, startup task `CloudBayStartup`, and existing OneDrive identity/environment fallbacks. The owned startup-record migration preserves Windows' disabled-startup choice. Cloud payloads remain bounded-RAM transfers, with only small recovery metadata persisted.

See [REBRANDING.md](REBRANDING.md) for the full contract. The discarded, unreleased directory-relocation patch is preserved locally at `artifacts/full-audit/pre-retirement-packaging.patch`.

## Verified gates

All evidence paths below are relative to `artifacts/full-audit/`, which is ignored by Git and available in this checkout. Prior successful gates remain useful evidence, but are distinguished from the final current-source rerun after the responsive changes.

Installer, Store-runtime and native tray/chooser acceptance preceded the final layout refinements. Their packaging and Windows-integration code did not change afterward; those artifacts are not rebuilt public releases. `verified-source-manifest.json` records the final source hashes.

| Gate | Result / scope | Evidence |
|---|---|---|
| Prior Release solution build | Pass, 0 warnings and 0 errors, before the latest responsive edits | `release-build-final-ui.log` |
| Prior desktop tests | **947 passed, 0 failed, 0 skipped**; excludes `LiveProvider`; includes 56 test names containing `Native` | `release-tests-final.log`, `release-tests-final.trx` |
| Release automation | **73 assertions passed**, isolated repository/package fixtures | `release-scripts-final.log` |
| PowerShell syntax | 19 scripts, 0 parser errors | `powershell-parser-final.json` |
| Workflow lint | All four workflows passed actionlint, exit 0, no diagnostics | `workflow-lint-final.json`, `workflow-lint-final.log` |
| NuGet vulnerability audit | No known vulnerable packages reported for the three projects against the configured NuGet source at audit time | `dependency-audit-after.log` |
| EXE/MSI runtime upgrade fixtures | Pass using isolated synthetic Debug client payloads, the archived 1.1.2 installer/worker, and synthetic 1.1.3 then 1.1.4 upgrades. Verified both worker generations, checksum recheck, readiness, graceful shutdown, startup/shortcut choices, original/custom paths, cross-kind/fresh-update refusal and private-state preservation. | `installer-acceptance/installer-host-bd6a7e365c634816913177c2954354b4/result.json`; `installer-acceptance/installer-fixture-39b5de1c77264549a6786d3bb3d708a5/installer-validation.json` |
| Full Release installer compilation | Real self-contained Release app payload compiled into EXE and MSI; this gate did **not** install those Release packages | `installer-full-ready.log`; `installer-full/Packages/` |
| Store package/runtime | Isolated `CloudInlet.LocalValidation` package registered and activated successfully; Store channel, notification registration and disabled `CloudBayStartup` verified. Cleanup removed registration, preserved client state and changed neither certificate trust nor Developer Mode. | `store-runtime/deae38b470c34c1f921cfad7853a89eb/Result/complete.json`, with `activation-validation.json` and `cleanup-validation.json` in its parent directory |
| Prior complete UI suite | Both themes completed, with 446 PNG captures and assertions covering navigation, transfer rows/progress, dialogs/imports/exclusions and tray behavior; predates the latest responsive edits | `ui-final/artifacts/ui-smoke/complete.txt`, `complete-Dark.txt`, `complete-Light.txt`, and adjacent assertion files |
| Native tray focus/lifecycle | 36/36 checks in the normal order; 40/40 with the menu opened first. Verifies own-process HWND foreground ownership, XAML focus, dismissal and reopening. | `native-tray-focus/artifacts/tray-focus-smoke/native-tray-focus-validation.json`; `native-tray-menu-first/artifacts/tray-focus-smoke/native-tray-focus-validation.json` |
| Native chooser lifecycle | Single-file, multi-file and folder dialogs actually opened with correct ownership; concurrent requests were rejected; cancellation and owner-close completed | `native-picker/artifacts/picker-smoke/native-picker-validation.json` |
| Intermediate responsive suite | Both themes completed after the first small-window fixes; later responsive refinements still require the final gate below | `responsive-run-2/artifacts/ui-smoke/complete.txt`, `complete-Dark.txt`, `complete-Light.txt`, `responsive-layout.txt` |
| Final current-source Release build and publish | Pass, 0 warnings/errors; self-contained x64 publish completed after the compact transfer statistics, Updates and navigation recovery changes | `audit-final-build.log`, `audit-final-publish.log`; runnable local output `audit-final-app/CloudInlet.exe` |
| Final desktop tests | **947 passed, 0 failed, 0 skipped**, including native tests, excluding `LiveProvider`. After the updater status text changed, all **58 updater tests** passed again. Other subsequent edits concern WinUI files outside the managed test project's compilation inputs. | `release-tests-responsive-final.log`, `release-tests-responsive-final.trx`; `update-tests-final.log`, `update-tests-final.trx` |
| Final current-source focused responsive UI | Both themes passed at 760×600, 800×600, 980×600 and 1100×600, including navigation recovery, repeated resizing, compact transfer statistics, action availability changes, card geometry, transfer review and larger-font wrapping. Updates also passed at 760×600, 800×840 and 1300×840. | `audit-final-layout/artifacts/ui-smoke/complete.txt`, `complete-Dark.txt`, `complete-Light.txt`, `responsive-layout.txt`; 104 PNGs |
| Final current-source complete UI suite | Both themes passed, **452 PNG captures**, including navigation, transfer states and actions, imports, dialogs, exclusions, settings, updates and tray behavior | `audit-final-ui/artifacts/ui-smoke/complete.txt`, `complete-Dark.txt`, `complete-Light.txt` and adjacent assertion logs |

## Remaining acceptance boundaries

- Live OneDrive/B2 network acceptance was not run. `live-provider-readiness.json` records that the isolated OneDrive opt-in settings/destination were not provisioned. The desktop test command excludes `LiveProvider`; these cases are not counted as passed. Real provider throughput, interruption/restart behavior across every transfer path and account-specific behavior remain external acceptance work.
- Native tray tests did not click the real Explorer notification icon. Native chooser tests selected zero items; they prove activation/cancellation and ownership, not actual user selection or multiselection.
- Local Store runtime is not Microsoft Store certification or Partner Center submission. Existing submission credentials were not supplied or changed.
- Layout captures and assertions cover this machine and the exercised window sizes/themes. They do not establish an exhaustive Windows version, monitor/DPI, text-scale or accessibility matrix.
- A passing audit establishes the checks listed here; it does not prove that every possible execution is bug-free. Preserve the failing evidence and report any reproduced issue with its exact state and environment.

## Resume safely

Read this report, `AGENTS.md` and the actual Git diff first. Use .NET 8 and PowerShell 7. Keep all further acceptance data under isolated `artifacts/` fixtures; do not repurpose the user's configured cloud accounts, installed Release application or backup roots. The local final gates above passed; external acceptance boundaries remain as listed. A future release must use a new version and preserve the already-published bridge bytes.

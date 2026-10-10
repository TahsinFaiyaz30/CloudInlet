# CloudBay to CloudInlet: one-time upgrade bridge

CloudBay users upgrade through the immutable **1.1.2** bridge before taking later CloudInlet releases. Published releases and tags are never rebuilt or replaced.

## Upgrade sequence

1. An original CloudBay installation uses its existing `TahsinFaiyaz30/CloudBay` schema-1 feed to install the frozen 1.1.2 package.
2. That package changes the product/executable to CloudInlet and retains the user's accounts, backup roots, Windows folder mappings, checkpoints, installer kind and build flavor.
3. Its CloudInlet updater reads `TahsinFaiyaz30/CloudInlet` and `updates-v2.json`. Subsequent releases use only canonical `CloudInlet-*` assets and schema 2.

The archived CloudBay repository keeps its seven existing bridge assets, including the byte-identical 1.1.2 aliases and schema-1 manifest. It needs no new releases or synchronization. The original source history remains in the canonical CloudInlet repository.

## Compatibility retirement in the next release

The working tree removes schema-1 update selection/downloads, legacy release-alias generation, the `CloudBay.exe` compatibility-launcher project, and obsolete executable/helper fallbacks from future installer payloads. Packaging rejects stale CloudBay binaries and legacy feed assets instead of silently shipping them. The bridge remains available as already-published bytes; it is not built by the new release pipeline.

Existing EXE/MSI installations are upgraded **in their registered directory**, including a directory historically named `CloudBay` or `CloudBay Debug`. Fresh installs use `Programs\CloudInlet` or `Programs\CloudInlet Debug`. The frozen CloudInlet 1.1.2 worker verifies and reopens `CloudInlet.exe` at that original directory; relocating it would require keeping forwarding executables indefinitely for clients that skip intermediate versions. The unreleased directory-relocation implementation has therefore been retired. Its prior working-tree patch is retained locally at `artifacts/full-audit/pre-retirement-packaging.patch`.

Portable script installations also retain their validated registered directory. They refuse to take over managed EXE/MSI installations. An original CloudBay installation must take the published bridge first. Installers do not recursively remove historical directories or unknown files. An old EXE installation can retain an already-installed historical launcher until its normal uninstaller removes its owned files; future packages contain no such launcher.

These are unreleased source changes. `version.json` has not been bumped and published 1.1.2 bytes have not changed. Publishing requires a new version and successful installer acceptance. `scripts/test-installers.ps1` exercises the frozen 1.1.2 installer/worker to a synthetic newer version, then a second update using the current worker, for both EXE and MSI.

## Durable identity and user data

The following remain stable data/Windows identities, rather than a second product implementation: `%LOCALAPPDATA%\CloudBay`, DPAPI entropy, SQLite journals, `.cloudbay` recovery metadata, Cloud Files root IDs, pipes/mutexes, EXE AppIds, MSI UpgradeCodes, `Software\CloudBay\Distribution`, Store application ID `CloudBay` and startup task `CloudBayStartup`.

Accounts, backup paths and cloud prefixes are not renamed. These identities cannot be changed cosmetically without risking encrypted accounts, online-only files, Windows registrations or restart recovery. Any future identity migration needs its own transactional migration and recovery acceptance. Existing deterministic keep-both receipts remain readable; new names use CloudInlet. Updater-cache ownership and safe cleanup of historical cached packages also remain stable.

The OneDrive public application registration and legacy environment fallbacks remain unchanged so existing deployments and saved refresh tokens continue working. The small startup-record recovery routine also remains part of Windows-state preservation: it recognizes only this installation's old Run entry and carries Windows' disabled-startup choice forward without shipping an old executable. The rename does not change transfer scheduling, integrity checks or bounded-RAM cloud-to-cloud payload handling.

Live provider tests require explicitly provisioned isolated credentials and destinations. Ordinary release validation excludes that category and does not count it as passed.

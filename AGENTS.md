# CloudInlet repository guide

Start with `docs/ASTRA_HANDOFF.md` for the repository configuration audit, current verification, and remaining acceptance work. Check `git status` and the actual diff before changing anything; the handoff is a dated snapshot.

## Project map

- `CloudInlet/`: unpackaged x64 WinUI 3 app, Windows integration, UI and controller.
- `CloudInlet.Core/`: sync, B2/OneDrive adapters, transfer recovery and updater rules.
- `CloudInlet.Tests/`: managed and Windows desktop integration tests.
- `scripts/`, `packaging/`, `.github/workflows/`: versioning, installers, Store packaging and releases.
- `version.json`: shared product version; `docs/REBRANDING.md`: upgrade compatibility contract.

## Build and validation

Use .NET 8 and PowerShell 7 for the scripts (Windows PowerShell 5.1 cannot handle their `utf8NoBOM` encoding).

```powershell
dotnet build CloudInlet.sln -c Release -v:minimal
dotnet test CloudInlet.Tests/CloudInlet.Tests.csproj -c Release --no-build --filter 'TestCategory!=LiveProvider' -v:minimal
pwsh -NoProfile -File scripts/test-release-scripts.ps1
```

Hosted CI additionally excludes test names containing `Native`; desktop native acceptance remains a separate gate. `LiveProvider` requires explicitly provisioned test credentials and destinations. Save logs/TRX/screenshots in ignored `artifacts/`; report failed or unrun gates accurately.

## Release presentation and publication

For any release task, follow `docs/RELEASE_STYLE.md` and `docs/RELEASING.md`. Prepare `docs/releases/<version>.json` from the complete previous-tag-to-source diff before bumping that version. Choose meaningful changelog categories for the actual release, cover all material changes, and link each item to its implementing commit(s); do not dump commit subjects or force a fixed category list. Make routine editorial decisions autonomously within this format.

Use the release-note generator for download buttons, vertically stacked **Release (x64)** / **Debug (x64)** tables, metadata and full SHA-256 values with native Copy buttons. The File column contains full clickable filenames; Type contains plain file formats (EXE, MSI, ZIP, MSIX, JSON or TXT). GitHub supplies the title, so do not repeat it inside the notes. Architecture must match the actual artifacts. Keep all six Release/Debug desktop packages, `updates-v2.json`, `SHA256SUMS.txt`, and the current Store MSIX when built. Keep MSIXUPLOAD, Store identity records, validation reports and duplicate checksum sidecars in CI artifacts, outside the public asset list. Never omit Debug or invent certification, signing, platform support or test claims. A later verified Microsoft Store installer is a deliberate distribution-metadata update; follow the Store transition checks in `docs/RELEASE_STYLE.md`.

Do not publish generic fallback notes when curated notes are missing. Preserve normal immutable tag/package guards. The user-authorized recreation of the 1.2.0 release object is a one-time presentation/inventory cleanup, not permission for future agents to recreate releases or replace app bytes.

## Preserve compatibility and user data

Do not globally replace `CloudBay`. Existing state paths, DPAPI entropy, sync-root IDs, pipes/mutexes, installer identities, `Software\CloudBay\Distribution`, `.cloudbay` recovery metadata, Store application ID `CloudBay`, and startup task `CloudBayStartup` intentionally remain stable. Keep existing OneDrive application identity and legacy environment fallbacks. The legacy executable launcher belongs only to the frozen 1.1.2 bridge.

Canonical source/updates: `TahsinFaiyaz30/CloudInlet`, schema 2, `updates-v2.json`. `TahsinFaiyaz30/CloudBay` is the archived static 1.1.2 upgrade bridge. Preserve that published schema-1 bridge and its byte-identical aliases, but do not generate schema 1, CloudBay assets, or compatibility launchers in future releases. CloudBay clients upgrade through 1.1.2 first. Preserve registered installation directories so the frozen 1.1.2 updater can reopen CloudInlet at its original path; fresh installs use CloudInlet. Never move historical tags or replace published release bytes.

Cloud-to-cloud payloads pass through bounded RAM; persist only small recovery metadata. Keep account credentials, backup roots, Windows folder mappings, and existing checkpoints intact. Use isolated test storage and destinations for acceptance.

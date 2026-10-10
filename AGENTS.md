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

## Preserve compatibility and user data

Do not globally replace `CloudBay`. Existing state paths, DPAPI entropy, sync-root IDs, pipes/mutexes, installer identities, `Software\CloudBay\Distribution`, `.cloudbay` recovery metadata, Store application ID `CloudBay`, and startup task `CloudBayStartup` intentionally remain stable. Keep existing OneDrive application identity and legacy environment fallbacks. The legacy executable launcher belongs only to the frozen 1.1.2 bridge.

Canonical source/updates: `TahsinFaiyaz30/CloudInlet`, schema 2, `updates-v2.json`. `TahsinFaiyaz30/CloudBay` is the archived static 1.1.2 upgrade bridge. Preserve that published schema-1 bridge and its byte-identical aliases, but do not generate schema 1, CloudBay assets, or compatibility launchers in future releases. CloudBay clients upgrade through 1.1.2 first. Preserve registered installation directories so the frozen 1.1.2 updater can reopen CloudInlet at its original path; fresh installs use CloudInlet. Never move historical tags or replace published release bytes.

Cloud-to-cloud payloads pass through bounded RAM; persist only small recovery metadata. Keep account credentials, backup roots, Windows folder mappings, and existing checkpoints intact. Use isolated test storage and destinations for acceptance.

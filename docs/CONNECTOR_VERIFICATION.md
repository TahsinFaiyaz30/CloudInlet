# Connector verification

The following 24 November measurements and fixture results describe the intermediate implementation. The MSIX described in this historical comparison contained both packs. The current source downloads them separately in all editions. [Current distribution procedure](CONNECTOR_RELEASES.md)

Verified on Windows x64 on 24 November 2026 using .NET SDK 8.0.426 and PowerShell 7. The baseline was the clean checkout at `67d5a6b`. Both builds use product version 1.2.1. The version of the connector contract and both first-party packs is 1.0.0.

## Download and installed payload sizes

These are actual byte lengths from locally built Release artifacts. Both sides use the repository's x64, self-contained, untrimmed WinUI publish, identical installer tools, helper, notices and portable packaging settings. The desktop modular column uses the core with an embedded bootstrap for the local signed test catalog. The expanded payload is the sum of the app's release files before installation. NTFS allocation, the uninstaller and user profiles are outside this measure. The Store comparison uses unsigned, local-validation MSIX packages and includes both official provider packs in the modular package.

| Artifact | Baseline bytes | Modular bytes | Change in bytes |
| --- | ---: | ---: | ---: |
| Desktop EXE installer | 64,337,232 | 64,272,256 | −64,976 |
| Desktop MSI installer | 74,481,646 | 74,486,246 | +4,600 |
| Portable ZIP | 91,258,269 | 91,171,049 | −87,220 |
| Expanded desktop payload | 226,726,391 | 226,366,324 | −360,067 |
| Store MSIX with both providers | 90,972,027 | 91,063,576 | +91,549 |

| Optional desktop download | Download and installed archive bytes | Expanded inventory bytes |
| --- | ---: | ---: |
| Backblaze B2 | 132,438 | 351,305 |
| OneDrive | 50,533 | 121,942 |
| Read-only example | 7,052 | 14,299 |

Installed packs remain compressed and add a small registry record. Each measured pack contains its manifest, signature, provider DLL and license. The 102,400-byte SDK DLL is shared once in the desktop payload. The original providers used direct HTTP clients, so extracting them leaves the shared Windows App SDK and .NET runtimes as the largest part of the download. The modular desktop payload contains 667 files, compared with 664 before.

## Startup and memory

Three launches per build opened the same isolated WinUI Overview/Updates fixture. Each launched a new process and account profile on the same machine. The measurement sampled the process working set and private bytes when Windows first reported its main window. The modular build had zero installed connectors. The runs were consecutive without flushing the Windows file cache.

| Measurement | Baseline | Modular |
| --- | ---: | ---: |
| First window, all three runs | 1,852 / 1,417 / 1,007 ms | 1,222 / 970 / 1,100 ms |
| Median time to first window | 1,417 ms | 1,100 ms |
| Median working set | 147.05 MiB | 149.02 MiB |
| Median private bytes | 55.24 MiB | 56.25 MiB |

Working set rose about 2 MiB in this sample. These observations cover first-window startup on one machine. A longer cold-start study and resident-memory sampling under a production workload would be needed to generalize them.

## Checks and evidence

- The Release and Debug solution builds passed with zero warnings and errors. All 16 connector cases also passed in Debug. The full Release non-live suite passed **1,640 of 1,640** including the desktop native tests. Sixteen cases in `ConnectorSystemTests` exercise package loading, dependency plans, SDK compatibility, signatures, source and publisher continuity, and update and transfer recovery. The repository release-script suite passed **141 assertions**.
- A job using the separately loaded production OneDrive and B2 assemblies issued range reads against a simulated Graph server, uploaded to a simulated B2 destination, verified the destination digest and completed through the shared journaled transfer engine. Existing provider suites cover authorization, upload sessions, multipart retries, verification and recovery.
- Isolated profile tests verified that a failed multi-package installation retained the original version, a running provider survived a staged replacement, a restart activated the new version, and rollback restored the previous one. They also covered optional and required dependencies, a compiled component dependency, cycle and publisher rejection, reinstall after disable, protected custom accounts, and byte preservation of existing credentials and checkpoints.
- A WinUI smoke run searched for B2, reviewed the publisher and source, installed, disabled and uninstalled it, and inspected installed/latest versions in Settings. A transfer draft with both endpoints missing opened a single reviewed plan that installed B2 and OneDrive. The existing full Light-theme WinUI regression run also completed, including its connection-draft restoration checks and 226 captures. The connector flow used an isolated profile and a signed local catalog.
- The private tutorial generated a standalone project, compiled the starter, signed and validated the archive using a DPAPI-protected personal key, and signed a private catalog. The published manifest schema validated both official archives and private examples. A starter built outside the repository was imported as its own signed pack and copied and verified its file through the real engine.
- A loopback HTTPS server exercised GitHub-style catalog and asset redirects, a failed primary download, selection of a signed mirror and a staged update from 1.0.0 to 1.1.0 with the previous version retained. The fixture trusted an ephemeral CA in the test process only.
- The portable archive and MSI file table contain the SDK and desktop bootstrap. Neither contains provider packs. Both official archives inside the local MSIX were byte-identical to the independently downloadable desktop packs. The Store image excluded the desktop feed bootstrap and private signing keys. The Store manifest check passed for the durable application and startup identities, Cloud Files registration and virtualization scope.
- One intermediate run hit a sharing violation in the existing `NativeConcurrentReadersNeverSeePartialWrites` installer-journal test. It passed three targeted retries and the final full run.

Local evidence is under `artifacts/connector-dev/`. The Release test result is `final-acceptance.trx`. The signed preview and catalog are under `verified/`, and the four connector UI captures are under `ui-final/artifacts/ui-smoke-connectors/`. The comparable installer trees are `distribution/before/` and `distribution/final/`, with the final MSIX under `distribution/final/store-validated/`. The first-window samples are in `baseline-window.json` and `modular-window.json`.

# Connector verification

The connector-enabled app was built and checked on Windows x64 on 25 November 2026. The app version is 1.2.1, the SDK is 1.0.0, and the first two connector releases are independently versioned 1.0.0. The tested host is on the source repository's `codex/separate-provider-packs` branch. The published app release and live Store submission were outside this change. [Tested host source](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b55ef1d2bec684ecf521308f72d206acc8c7e940) · [Source signed into both first releases](https://github.com/TahsinFaiyaz30/CloudInlet/commit/8f0e1f465da739b3235fb8590849ee50beb52f77)

## Published downloads

The production catalog is 3,672 bytes. It authenticates both versioned asset URLs and archive digests. The app pins the catalog URL and the CloudInlet publisher fingerprint `D3E879A5EA4B3F3316472C9A9CFAC000DFDE296E7980D71AB0AD4C358741388A`. Both releases are immutable on GitHub and contain their pack, release notes and checksums. [Signed catalog](https://raw.githubusercontent.com/TahsinFaiyaz30/CloudInlet-Connectors/main/catalog.json)

| Connector | Download and installed archive bytes | Expanded inventory bytes | SHA-256 of downloaded archive |
| --- | ---: | ---: | --- |
| [Backblaze B2 1.0.0](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/releases/download/b2-v1.0.0/b2-1.0.0.cipack) | 132,453 | 351,391 | `0981A72D6D321135F9702A974D01ADF1CC4FCC0EA17247456E055E93C9C5C692` |
| [OneDrive 1.0.0](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/releases/download/onedrive-v1.0.0/onedrive-1.0.0.cipack) | 50,548 | 122,028 | `23811B2777D4FF1564082AC6B235DEDC8AB8DE653C4893D331B5BF9E5654DC85` |

Each publishing job ran 357 focused non-live tests. The signer used the production environment, validated each package against its exact build inputs, downloaded the public assets, and authenticated the resulting catalog. A B2 release replay preserved the signed bytes and the cumulative two-provider catalog. The initial 1.0.0 DLLs retain build-environment-specific debug identifiers. Their signed payloads matched the hosted inputs. The newer unsigned build checks compared both DLLs, licenses and manifest templates byte-for-byte between a clean independent workstation clone and Windows Actions. The signing jobs were skipped on those checks. [B2 publication, first attempt](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/actions/runs/45456567363/attempts/1) · [OneDrive publication](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/actions/runs/45457169345) · [Successful B2 replay](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/actions/runs/45532753152) · [B2 build comparison](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/actions/runs/45533441607) · [OneDrive build comparison](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/actions/runs/45533446941)

## Base package sizes

Actual Release byte lengths for the original `67d5a6b` host and the tested connector-enabled host are below. Both use x64 self-contained, untrimmed WinUI/.NET publishing, the installer helper, release notices and the project's compression settings. The MSIX comparison uses unsigned local-validation packages. Expanded sizes sum the app files before installation and exclude NTFS allocation, user data and the Windows uninstaller.

| Artifact | Original bytes | Separate-download bytes | Change |
| --- | ---: | ---: | ---: |
| EXE installer | 64,337,232 | 64,260,278 | −76,954 |
| MSI installer | 74,481,646 | 74,437,094 | −44,552 |
| Portable ZIP | 91,258,269 | 91,165,427 | −92,842 |
| MSIX | 90,972,027 | 90,874,292 | −97,735 |
| Expanded desktop payload | 226,726,391 | 226,362,123 | −364,268 |

The intermediate bundled MSIX measured 91,063,576 bytes. The final MSIX saves 189,284 bytes against that build and contains zero providers. The 102,400-byte SDK ships once. B2 and OneDrive each contain one managed provider DLL and a license, alongside the signed manifest. Installing both adds 183,001 compressed bytes and small registry records. Shared Windows App SDK and .NET runtime files account for most of the base footprint.

The base scanner inspected archives, assembly metadata and managed host resources. An independent comparison decoded all 226,362,117 bytes in the EXE's solid application payload and checked 667 source files, including its installer-specific identity. It compared all 667 decoded MSI cabinet members, all 667 portable ZIP entries, and all 674 local MSIX entries against the publish tree and expected package metadata. EXE and MSI verification used offline decoding. The package audit also rejects a renamed connector DLL or a renamed signed archive. [Package verification commands](CONNECTOR_RELEASES.md#verify-the-base-packages)

## Runtime and recovery

- The Release solution built with zero warnings and errors. All **1,640 non-live tests** passed with zero skips, including the desktop native tests. An additional focused controller test passed for rejecting both disable and uninstall while a saved sync-root binding uses the connector. The separate release automation suite passed **141 assertions**. Connector tests cover required and optional components, compatible version fallback, disabled or missing dependencies, conflicts and cycles, package integrity, stale plans, and staged update, interruption and rollback. Packaging fixtures also cover a duplicate immutable version, incorrect signing identity, incorrect source commit and a forged prior catalog.
- Real OneDrive and B2 connector assemblies completed a verified streaming transfer against simulated Graph and B2 HTTP transports. Both endpoints used the shared job engine. Account-vault, saved-root, missing-provider and journal cases used isolated data. A failed or removed installation retained the existing credential and checkpoint bytes. Live provider accounts and customer upgrade data were outside these checks.
- The production desktop acquisition check fetched the real signed catalog with zero installed packs, installed and loaded one provider, disabled, re-enabled and removed it, then repeated the sequence for the other. The WinUI test searched, reviewed the publisher and download source, installed and disabled B2, inspected Settings, removed it, then installed both missing endpoints from a single transfer recovery action. The UI fixture used a local copy of the production signed catalog and the real public asset URLs.
- Windows registered the separate `CloudInlet.LocalValidation` MSIX and activated its packaged host. The host fetched the production HTTPS catalog and exercised the same selective acquisition, activation and removal for each provider under the package's writable LocalState. Windows reported the Store update channel, the existing application ID and startup task. Cleanup removed the test registration and confirmed preservation of account settings, credential bytes and Windows folder mappings. The test used the machine's existing Developer Mode and certificate trust.
- A developer generated a standalone sample outside the source checkout using the scaffolder. It built against the SDK, signed and validated with a personal DPAPI-protected key and produced a private catalog. Earlier isolated acceptance also imported a standalone pack and verified its sample file through the transfer engine, covered HTTPS mirror selection and a 1.0.0 → 1.1.0 update, and captured 226 screens in the Light WinUI regression. No sample was published as an official connector.

Three isolated Overview launches per build measured the first top-level window on the same machine. Baseline times were 1,852, 1,417 and 1,007 ms. The final host with zero installed providers took 2,307, 1,038 and 1,009 ms. Median working set was 147.05 MiB before and 149.12 MiB after. Median private bytes were 55.24 MiB before and 55.38 MiB after. These small samples used the normal Windows file cache and cover first-window startup only.

## Evidence and acceptance

Local records are under `artifacts/connector-delivery/`. See `verified-final.trx`, `final-build.log`, `release-scripts.log`, `public-downloads/`, and `final/` for package sizes and hashes, decoded inventories, private publication checks, startup samples, WinUI captures, packaged activation and cleanup. Baseline evidence and the earlier isolated transport, WinUI and upgrade fixtures are under `artifacts/connector-dev/`.

The connector-enabled host is available as source and local builds. First-party packs are publicly downloadable. Microsoft certification of this extension model, live cloud-account acceptance and a real customer upgrade remain external acceptance work. The private source protocol supports local catalogs and reachable HTTPS endpoints. An authenticated network or VPN can serve a private feed. Interactive HTTP credentials and private GitHub API tokens are outside v1. [Store policy assessment and certification material](STORE_CONNECTORS.md) · [Private connector tutorial](CONNECTORS.md)

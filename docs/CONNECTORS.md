# Provider connectors

The x64 EXE, MSI, portable and MSIX builds use the same in-app catalog and download provider code on demand. Open **Connectors**, choose **Backblaze B2** or **OneDrive**, select **Install**, and review its publisher and source. A new profile contains zero providers. Opening this page fetches signed metadata. Installing the reviewed plan fetches the selected provider and its required dependencies. Backblaze B2 and OneDrive use the same signed package format and transfer SDK. This PC is part of the host. S3, Azure Blob Storage, Google Cloud Storage and Google Drive are prospective connectors.

## Try the desktop development build

From the repository root, run PowerShell 7 on Windows with the .NET 8 SDK.

```powershell
pwsh -File scripts/build-connectors.ps1 -OutputDirectory artifacts/connector-preview -Preview
& .\artifacts\connector-preview\App\CloudInlet.exe --connector-lab --page=connectors
```

Choose a new output directory for each build. The command builds the app and separately signs the two provider test packs, a local catalog and a private sample. The preview trusts the local catalog key embedded in its distribution configuration. Local development keys are unique to the build unless you pass an existing `-KeyFile`. Keep the preview and its catalog together. The lab uses its own per-build profile, instance mutex and pipe. Windows startup management is disabled in this mode. In the lab, use **Settings → Updates and about → Check connectors** to refresh the test catalog. The normal installed desktop edition also checks for app updates.

- **Connectors → Install** opens the full plan, including the publisher fingerprint, actual download location, version, dependencies and release notes. One approval installs the group. When a publisher supplies mirrors, select a location in this dialog. An alternative publisher or subscription requires separate review.
- To try a transfer without cloud credentials, use **Import packages** and select `example.cipack` from the build output. Review the displayed developer fingerprint against the build log. Open **Cloud transfer**, choose **Private sample files → Sample files** as source, **This PC** and a new empty folder as destination, then choose **Copy**. The engine verifies and writes `welcome.txt`.
- A saved job or account with missing providers has an **Install required connectors** action. Recovery preserves the saved endpoint IDs and checkpoints. Reopen the connection flow after installing if authentication is still required.
- Search by name, provider ID, feature or publisher. A connector can be disabled while its saved accounts remain on disk. Pause its active transfers before disabling or uninstalling. CloudInlet blocks removal while the connector owns a mounted backup root or another installed package requires it. Removing a subscription leaves installed code and accounts in place.
- **Settings → Updates and about** checks the app and connector sources. It shows installed and catalog versions, release notes, individual and combined updates, and automatic preferences. Local imports receive updates by importing a newer package signed with the same key. The Connectors page offers rollback while the prior connector package is retained.

## Engine and SDK

| Owner | Responsibilities |
| --- | --- |
| WinUI host and CloudInlet.Core | Account selection, protected credentials, configuration, queues, SQLite jobs, conflict decisions, progress, cancellation, bandwidth budgets, verified moves, Windows integration and migration. |
| CloudInlet.ConnectorSdk | Shared endpoint types, versioned provider and session interfaces, optional account setup, and optional contracts for existing backup and OneDrive account workflows. |
| B2 pack | Application-key authorization, bucket and version listing, API errors, single/multipart transfers, checksums, upload recovery, server-side version-preserving copy and B2 folder backup transport. |
| OneDrive pack | Existing delegated device-code and refresh flows, Microsoft Graph requests, drives and item identities, resumable upload sessions, version checks and exact deletion. |
| Custom connector or component | Its declared provider operations or reusable managed libraries. Each depends on the public SDK and its declared components. |

Each transfer has a source location and a destination location. The controller opens the account session for each provider and supplies both endpoints to the same transfer engine. A OneDrive-to-B2 job streams independent, version-bound ranges from Graph into B2. Verification finishes before a move deletes its exact source version. The database contains metadata, receipts and protected checkpoints. Cloud payload stays in bounded memory on this path. Native Downloads and Files On-Demand retain their existing staging and validation behavior.

Desktop connector code, source pins and servicing preferences live under `%LOCALAPPDATA%\CloudInlet\ProviderPacks\<flavor>`. MSIX uses the package's writable LocalState directory, under `ProviderPacks\<flavor>`. The legacy and canonical account profiles share the connector location for their edition and build flavor. A pending profile migration can install B2 before resuming without modifying the reviewed folder inventory. Private connector account vaults remain inside the active protected account profile. Lab and test profiles have separate connector stores.

The SDK assembly name is `CloudInlet.ConnectorSdk`. Some type namespaces retain `CloudInlet.Core` for source compatibility. The engine assembly is a separate artifact. Provider DLLs reference the SDK. SQLite, the application engine, WinUI and the runtime remain with the host.

## Contract version 1

Implement a public, concrete `IProviderConnector` with a public parameterless constructor. Its ID and capability flags must match the signed manifest. `Open(ConnectorContext)` receives an account ID, shared bandwidth budget, optional provider options and an opaque credential. A refresh calls the supplied credential-save callback. `IConnectorSession.GetRootsAsync` returns the actual containers accessible to that account. `OpenEndpoint` binds one reviewed location to an `ITransferEndpoint`. Container, folder and immutable item IDs are opaque strings. Relative paths name the file presented to the transfer engine.

| Capability | Contract |
| --- | --- |
| Browse | Expose roots and paged child folders. An object store can synthesize folders from prefixes. |
| Read | Enumerate stable entries and open a replayable source range. Revalidate the source identity and version. |
| Write and Verify | Reconcile prior intent, upload or resume, return a version-bound receipt and independently verify the destination. The host requires both for a copy destination. |
| DeleteExactVersion | Delete only the reviewed unchanged version. Moves require this capability on the source. |
| ResumableUpload | Persist acknowledged offsets and opaque session metadata through the engine callback. |
| ServerCopy | Provider has a direct copy operation. B2 exposes this through its optional native backup contract. |
| Checksums | The provider exposes content digests. The SHA-1 fields in existing transfer records are optional. A provider without a suitable digest must supply equally sound destination verification to advertise Verify. |
| ObjectKeys / HierarchicalFolders | Describe object namespaces or hierarchical drives. They do not imply a provider supports all copy policies or Windows backup. |
| FolderBackup | Optional Windows backup transport. The v1 built-in workflow uses B2's published versioned-store semantics. |
| AccountSetup | Implement `IConnectorAccountSetup`. The host renders the requested text or password fields. Return a stable ID, display name and opaque credential, which the host protects with current-user DPAPI. |

Providers implement supported endpoint methods and throw `NotSupportedException` for unavailable operations. The host checks required capabilities before starting or resuming a job. Respect cancellation, use the shared directional bandwidth budget for network payload, and retain immutable identities across retries. Return translated errors without secrets. Before any destination mutation, validate the source. After losing an acknowledgement, reconcile the operation ID before attempting creation again. Respect the engine's deterministic keep-both filename rules. `HasContentBoundVersion` is a positive guarantee of immutable content binding.

Optional native backup and device-code contracts preserve the original B2 workflows and OneDrive registration, legacy environment variables, drive/account IDs, entropy and checkpoint formats. A new provider can implement ordinary file transfers without implementing either extension.

## Package, dependencies and trust

A `.cipack` is a ZIP containing `manifest.json`, a 64-byte binary ECDSA P-256/SHA-256 signature in `manifest.sig`, and its signed inventory of flat managed DLLs and optional `LICENSE.txt` or `README.md`. The signature covers the exact UTF-8 manifest bytes and uses fixed-width IEEE P1363 encoding. The manifest includes a SHA-256 digest for every file. The SDK, runtime and engine are shared. The loader validates assembly identities, SDK compatibility, platform, architecture, entry point and file hashes before activation. It reads signed DLL bytes directly from the package. Paths, ZIP links, duplicate entries, undeclared files and incomplete inventories are rejected.

The [manifest schema](../connectors/manifest.schema.json), [B2 template](../connectors/BackblazeB2/connector.json), [OneDrive template](../connectors/OneDrive/connector.json) and [SDK interfaces](../CloudInlet.ConnectorSdk/Connector.cs) define the fields. Build templates leave the public key and hashes empty. Packaging fills these fields. The signed catalog supplies the full-archive hash, release metadata, download locations and catalog subscription used for updates.

A library component sets `kind` to `component` and implements `IConnectorComponent`. A consumer can reference its managed library when compiling. Package the library only with the component. Both of the following dependency examples use an illustrative publisher fingerprint.

```json
"dependencies": [
  { "id": "example.shared", "publisherKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "versions": { "min": "1.2.0", "max": "2.0.0" }, "optional": false },
  { "id": "example.metadata", "publisherKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "versions": { "min": "1.0.0", "max": "1.5.0" }, "optional": true }
]
```

Ranges include the minimum and exclude the maximum. The planner considers constraints from installed dependents, resolves required components in dependency order, lists disabled components to enable, and explains unavailable optional functionality. Optional code must tolerate an absent dependency. Cycles, ambiguous publishers, conflicting assembly names and conflicting bytes for one version fail validation or activation. Components with the same ID are shared only when publisher identity and version satisfy the dependency.

A public key is canonical Base64 DER SubjectPublicKeyInfo. Its fingerprint is uppercase SHA-256 of those bytes. The publisher name is a label. The key proves continuity. Compare personal or community fingerprints using a separate trusted channel. For local imports the approval applies to the displayed package and publisher ID. Catalog subscription pins the key. Official IDs are reserved to the distribution's pinned publisher. Installed IDs retain publisher ownership even after uninstall. A local pack cannot silently replace a catalog subscription. Production key replacement requires an explicit distribution and ownership migration.

**Executable connectors run inside CloudInlet with the user's Windows access.** They can access files, network resources and secrets available to that process. Assembly load contexts resolve dependencies and versions. They provide no security sandbox. Use only publishers whose code you trust.

## Build a private connector

The scaffolder makes a standalone working project with a copy of the SDK for compilation. It can live outside this repository and needs no public listing or commercial certificate.

```powershell
$project = Join-Path $PWD 'artifacts/my-personal-provider'
pwsh -File scripts/new-connector.ps1 -Id personal.samples -Publisher 'My private publisher' -Destination $project
dotnet build "$project/personal.samples.csproj" -c Release
$tool = 'tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll'
dotnet build tools/CloudInlet.ConnectorTool/CloudInlet.ConnectorTool.csproj -c Release
dotnet $tool keygen "$project/publisher.dpapi"
dotnet $tool pack "$project/connector.json" "$project/bin/Release/net8.0" "$project/publisher.dpapi" "$project/personal-1.0.0.cipack"
dotnet $tool validate "$project/personal-1.0.0.cipack"
```

Import the resulting file from **Connectors → Import packages**. The starter exposes a single read-only file. Replace its session, endpoints, ID, features, license, documentation and release notes as appropriate. Add provider-specific managed DLLs to the manifest inventory and build output. A standalone pack does not need to include a self-contained .NET publish. Preserve the key to update the connector. For a new local version, increase the connector's three-part version, rebuild and import the new signed package. The CLI refuses to overwrite an existing pack.

The example key is protected for the current Windows user using DPAPI. Keep private keys outside source control, catalogs, releases and the app. The normal desktop app does not need the signing tool or a private key.

## Publish a community or private catalog

1. Build and validate each pack. Select a publisher-controlled HTTPS location, a GitHub repository of your own, or a local catalog file shared privately with its sibling packs.
2. Use the same publisher key to sign a catalog. For example, run `dotnet $tool catalog "$project/publisher.dpapi" "$project/catalog.json" "$project/personal-1.0.0.cipack"`. Include all packages and versions needed by the intended install plans. A publisher may depend on a separately subscribed publisher's pinned component.
3. To specify alternatives, add `--locations map.json` before the package filenames. The map associates each connector ID with an ordered array of package URLs or sibling filenames. The signature covers that list. First-party and community catalogs follow the same format.
4. For GitHub, attach packs to immutable versioned releases and serve the cumulative signed catalog at a dedicated stable URL, for example `https://raw.githubusercontent.com/OWNER/CONNECTORS/main/catalog.json`. Each package location in a hosted catalog must resolve to its own versioned asset. Publish the public fingerprint separately. Create a new version and new bytes for each update. [GitHub release documentation](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)
5. Subscribers choose **Add source**, enter the catalog URL or select a local catalog file, and verify its publisher. Self-hosted HTTPS can be private to a network or VPN. V1 does not forward GitHub tokens, browser cookies or custom HTTP credentials. Local import also works entirely offline.

CloudInlet pins the official discovery URL and ECDSA public key in `connectors/official-source.json`. Every release package carries this small public file. Official manifests also sign the full source commit. Connector version numbers belong to the individual providers. [Official connector release procedure](CONNECTOR_RELEASES.md)

## Servicing and compatibility

The entire plan downloads into the connector-owned package directory for this installation or test profile. The installer verifies each signed archive and its expected digest, then commits a single registry replacement. A failed or interrupted plan leaves the previous registry authoritative. Later maintenance reclaims abandoned partials and unreferenced archives. One writer owns the cross-process installation lock. The registry retains one previous version for rollback. Loaded code keeps its original package and dependency context through process exit. Activation of its replacement waits for restart, so active streams keep their transport and checkpoints.

Automatic servicing is opt-in globally and can be disabled per connector. It checks subscribed enabled connectors on the existing update scheduler, with a six-hour interval. A plan requiring new dependencies or re-enabling a package needs manual review. A removed or offline source can be restored at its original location with the pinned key. The Microsoft Store services the host MSIX. CloudInlet services the reviewed connector subscriptions. [Store policy and certification notes](STORE_CONNECTORS.md)

SDK v1 uses assembly identity 1.0.0.0. Compatible additions use new optional interfaces, capability flags or default interface methods. A mandatory signature or behavioral change requires a new contract major and an explicit supported range. Deprecations must document their replacement and last supported contract. Saved provider IDs, the interpretation of saved immutable item identities, and checkpoint upgrade readers have their own compatibility obligations. An older app explains the need for an upgrade when a package declares an unknown feature and can still install compatible entries from that catalog. A newer app may reject a connector whose range excludes its SDK and show the required versions.

The automated connector tests cover package integrity, real shared-library dependencies, interrupted installs and upgrades, source and publisher pins, edition-independent acquisition, recovery across process restarts, private imports and a OneDrive-to-B2 job using both loaded packs with mock HTTP transports. The verification report identifies the builds, public downloads, measured sizes and external acceptance work. [Connector verification](CONNECTOR_VERIFICATION.md)

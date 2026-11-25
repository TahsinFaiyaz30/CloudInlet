# Releasing an official connector

| Repository | Responsibility |
| --- | --- |
| [CloudInlet](https://github.com/TahsinFaiyaz30/CloudInlet) | Application releases, SDK, provider source and the reviewed publishing tools. |
| [CloudInlet-Connectors](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors) | Protected publisher, signed catalog, generated directory and both historical 1.0.0 releases. |
| [CloudInlet-Connector-B2](https://github.com/TahsinFaiyaz30/CloudInlet-Connector-B2) | Backblaze B2 package and release history. Source lives in `connectors/BackblazeB2` in CloudInlet. |
| [CloudInlet-Connector-OneDrive](https://github.com/TahsinFaiyaz30/CloudInlet-Connector-OneDrive) | OneDrive package and release history. Source lives in `connectors/OneDrive` in CloudInlet. |

The stable discovery URL and public signing identity apply across all four app distributions. A personal or community publisher can use its own key, repository and catalog. [Signed catalog](https://raw.githubusercontent.com/TahsinFaiyaz30/CloudInlet-Connectors/main/catalog.json) · [Private and community development](CONNECTORS.md)

Register a new official provider in `providers.json` in the central distribution repository. Specify its display name, release repository, reviewed source folder, assembly and entry point. Grant the existing publisher GitHub App access to that provider's repository and dispatch the normal publishing workflow. The host discovers compatible providers, versions, download URLs and optional Microsoft Store locations from the signed catalog on refresh. A provider using the existing SDK can ship independently of the app.

## Prepare a version

1. Change only the affected provider's independent version, notes and supported SDK range in `connectors/BackblazeB2/connector.json` or `connectors/OneDrive/connector.json`. Versions have three numeric components. The existing first-party ID, publisher key and previous assets identify upgrades of saved connections. Review the provider changes and their dependencies together.
2. Check out the full reviewed commit in a clean Windows x64 checkout with PowerShell 7 and the .NET 8.0.426 SDK. Run the build and non-live tests described in AGENTS.md. For a new version, use the commands below to build one provider's release input. It uses deterministic IL, omits developer-machine PDB identifiers, and normalizes packaged text to LF. The signed manifest identifies the source commit. For historical 1.0.0 provenance checks use the original public pack and signed catalog. The original hosted build inputs also support a byte-for-byte inventory comparison with `release-verify`.

   ```powershell
   $revision = git rev-parse HEAD
   pwsh -File scripts/build-connector-input.ps1 -Connector b2 -SourceCommit $revision -OutputDirectory artifacts/release-input-b2
   ```

3. Make the commit reachable in the public source repository. A pushed reviewed branch is sufficient. An app version bump, tag or published app package is unnecessary for a contract-compatible connector change.
4. Dispatch the workflow on distribution `main`, selecting one provider. The workflow runs checks without signing credentials, then builds only the selected release payload. A separate job signs, publishes, downloads and authenticates the result, and merges it into the catalog. The signing job runs the maintainer-pinned tooling commit from `.github/publisher.json`. User-selected revisions supply package input only.

   ```powershell
   gh workflow run publish.yml --repo TahsinFaiyaz30/CloudInlet-Connectors --ref main -f connector=b2 -f source_commit=$revision
   gh run list --repo TahsinFaiyaz30/CloudInlet-Connectors --workflow publish.yml --limit 4
   ```

   For a new version, add `-f validate_only=true` for an unsigned build and test run. Its `unsigned-input` artifact contains the reviewed package input. For version 1.0.0, validation authenticates the original public download against the signed catalog and source commit. Publication copies all three original immutable assets byte for byte into the selected provider repository and signs the catalog location change. The original URL remains an explicit fallback. Use `8f0e1f465da739b3235fb8590849ee50beb52f77` to verify either original. Add `-f verify_access=true` to check the protected installation token for the selected provider. Combine it with `validate_only=true` for an access check without publication. [GitHub Actions concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)

## Signing and repository configuration

Use the existing `connector-production` environment in CloudInlet-Connectors, restricted to branch `main`. Limit write access to trusted release operators. Both provider repositories require immutable releases. The app pins the production signing identity in `connectors/official-source.json`. [GitHub deployment environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments) · [Immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)

The one-time operator command `pwsh -File scripts/initialize-connector-publisher.ps1` creates an ECDSA P-256 signing identity on Windows, writes its current-user DPAPI recovery copy under `%LOCALAPPDATA%\CloudInlet-Publishing\connector-production\publisher-v1.dpapi`, and sends the key over standard input to GitHub's encrypted environment secret `CONNECTOR_PUBLISHER_PKCS8`. A new identity is refused if a pin or secret already exists. Run it for recovery only with the original protected key and Windows profile. Preserve that Windows profile and its DPAPI recovery material using the operator's protected backup process. When a credential is lost or compromised, stop publication and distribute a reviewed host trust transition before using a new key.

The private **CloudInlet Connector Publisher** GitHub App grants repository Contents read/write and the implicit Metadata read permission. Install it on **Only select repositories** and choose the two provider repositories in the table. Keep its client ID in the environment variable `CONNECTOR_RELEASE_APP_CLIENT_ID` and its PEM in the encrypted environment secret `CONNECTOR_RELEASE_APP_PRIVATE_KEY`. To rotate, add a new key to the same App, pipe it to `gh secret set CONNECTOR_RELEASE_APP_PRIVATE_KEY --repo TahsinFaiyaz30/CloudInlet-Connectors --env connector-production` via standard input, verify access for both connectors, then revoke the superseded key. This installation controls release uploads. The separate ECDSA key authenticates connector code and catalog bytes. [GitHub App installation](https://docs.github.com/en/apps/using-github-apps/installing-your-own-github-app) · [Installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app)

Maintain the workflow templates under `packaging/connector-distribution/`. Copy the YAML to central `.github/workflows/publish.yml` and the script to `.github/publish.ps1`. Set central `.github/publisher.json` to `{"toolRevision":"<40-character reviewed CloudInlet commit>"}`. Review these three files as signing-authority changes. The build job has read-only source access. The protected job uses SHA-pinned actions, trusted tooling, a short-lived installation token limited to the selected provider repository and a separate central `GITHUB_TOKEN` for the catalog. Only the final trusted step receives the connector signing key. A contributor pull request cannot trigger this deployment.

## Public inventory and retry

The two immutable historical tags are [b2-v1.0.0](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/releases/tag/b2-v1.0.0) and [onedrive-v1.0.0](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors/releases/tag/onedrive-v1.0.0). Their original URLs, hashes and signed provenance are retained in the catalog. Every version, including 1.0.0, uses `v<version>` in its own provider repository. Its title names the provider and version. The highest stable version for that provider receives the GitHub Latest designation, including when an older supported line is serviced later. Each release has three assets, `<id>-<version>.cipack`, `SHA256SUMS.txt` and `release-notes.md`. All discovery uses central `main/catalog.json`. The signed entry authenticates the complete package hash and immutable URL. The package authenticates its manifest, provenance and payload.

The job fills a draft, verifies existing assets on retry, downloads all three published files and checks their digests. It verifies the package against the exact source build. The central catalog and generated README advance together in a commit conditioned on the previous branch tip. The merge preserves entries for other connectors and older clients. The provider README is regenerated from the authenticated catalog after that commit. A retry with the same inputs finishes an interrupted release or directory refresh. A digest or provenance conflict stops the run. Correct immutable assets by releasing a new connector version.

Run `pwsh -File scripts/test-connector-release.ps1` for isolated routing, history, directory, integrity and replay checks. To reproduce directory Markdown, run `dotnet $tool release-readme connectors/official-source.json PATH_TO_SIGNED_CATALOG central $reviewedToolCommit README.md` with the release tool below. Use `b2` or `onedrive` in place of `central` for a provider README.

For an independent end-to-end check from a clean profile, run the verification command below. It discovers the public catalog and installs, loads, disables, re-enables and removes each provider in isolation. It needs no cloud account. For private development and self-hosting use the connector guide. [Private connector tutorial](CONNECTORS.md)

```powershell
dotnet build tools/CloudInlet.ConnectorTool -c Release
dotnet tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll test-acquisition connectors/official-source.json artifacts/new-acquisition-check
```

## Verify the base packages

The release scripts scan the publish directory for provider implementations and embedded packs. To compare the actual EXE, MSI, portable ZIP and local-validation MSIX against that tree, run the independent inventory check below in PowerShell 7. It uses Python 3.9+ and the repository's pinned WiX compiler. It reads the self-contained installer data without installing either desktop product. Set the first three variables to your freshly built candidate directories. The paths below are the recorded verification build.

```powershell
$app = 'artifacts/connector-delivery/final/App'
$packages = 'artifacts/connector-delivery/final/packages'
$store = 'artifacts/connector-delivery/final/store'
$exe = (Get-ChildItem $packages -Filter '*.exe').FullName
$msi = (Get-ChildItem $packages -Filter '*.msi').FullName
$zip = (Get-ChildItem $packages -Filter '*.zip').FullName
$msix = (Get-ChildItem $store -Filter '*.msix').FullName
$staging = (Get-ChildItem $packages -Directory -Force -Filter '.installer-*').FullName
$inspection = Join-Path $PWD ('artifacts/msi-inspect-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $inspection | Out-Null
& ./scripts/assert-core-payload.ps1 -AppFolder $app
& ./artifacts/tools/installers/wix/wix.exe msi decompile $msi -x "$inspection/files" -o "$inspection/installer.wxs"
python scripts/test-connector-distribution-payloads.py --app $app --exe $exe --portable $zip --msix $msix --installer-staging $staging --msi-files "$inspection/files/File"
& ./scripts/test-store-manifest.ps1 -PackagePath $msix
```

On a Windows desktop with Developer Mode already enabled, `pwsh -File scripts/test-store-runtime.ps1 -PackagePath $msix -VerifyConnectors` registers only the separate local-validation identity, checks actual public acquisition in package-local storage, then removes that registration. The helper refuses to replace an existing local-validation registration. Keep the official catalog reachable for this test. The EXE audit follows the pinned Inno compiler's solid LZMA2 format and call-instruction transform. [Inno Setup format implementation](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Projects/Src/Compression.Base.pas)

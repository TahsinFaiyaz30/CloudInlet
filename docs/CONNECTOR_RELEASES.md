# Releasing an official connector

The CloudInlet repository owns the SDK, host, provider implementation, build tool and source templates for distribution automation. The public CloudInlet-Connectors repository owns the signing environment, immutable connector releases and signed catalog. It publishes Windows x64 B2 and OneDrive packs. The private starter uses its author's own key and feed. [Source](https://github.com/TahsinFaiyaz30/CloudInlet) · [Distribution](https://github.com/TahsinFaiyaz30/CloudInlet-Connectors) · [Signed catalog](https://raw.githubusercontent.com/TahsinFaiyaz30/CloudInlet-Connectors/main/catalog.json)

## Prepare a version

1. Change only the affected provider's independent version, notes and supported SDK range in `connectors/BackblazeB2/connector.json` or `connectors/OneDrive/connector.json`. Versions have three numeric components. The existing first-party ID, publisher key and previous assets identify upgrades of saved connections. Review the provider changes and their dependencies together.
2. Check out the full reviewed commit in a clean Windows x64 checkout with PowerShell 7 and the .NET 8.0.426 SDK. Run the build and non-live tests described in AGENTS.md. To reproduce the unsigned input for one provider, use the following commands. The build includes the connector DLL and declared license. The source commit goes in the signed manifest and its documentation link.

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

## Signing and repository configuration

Create an environment named `connector-production` in CloudInlet-Connectors, restricted to deployments from branch `main`. Limit write access to trusted release operators. Enable GitHub immutable releases for the distribution repository before the first publication. The public signing identity is pinned in `connectors/official-source.json`. Preserve that pin across compatible app versions. [GitHub deployment environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments) · [Immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)

The one-time operator command `pwsh -File scripts/initialize-connector-publisher.ps1` creates an ECDSA P-256 signing identity on Windows, writes its current-user DPAPI recovery copy under `%LOCALAPPDATA%\CloudInlet-Publishing\connector-production\publisher-v1.dpapi`, and sends the key over standard input to GitHub's encrypted environment secret `CONNECTOR_PUBLISHER_PKCS8`. A new identity is refused if a pin or secret already exists. Run it for recovery only with the original protected key and Windows profile. Preserve that Windows profile and its DPAPI recovery material using the operator's protected backup process. When a credential is lost or compromised, stop publication and distribute a reviewed host trust transition before using a new key.

Maintain the two workflow files in the source repository under `packaging/connector-distribution/`. Copy the reviewed YAML to distribution `.github/workflows/publish.yml` and the script to `.github/publish.ps1`. Set `.github/publisher.json` to `{"toolRevision":"<40-character reviewed source commit>"}` when upgrading signing tooling. Review changes to those three distribution files as signing-authority changes. The workflow uses SHA-pinned actions, read-only public source checkout, and a distribution-repository token with contents-write only in the publishing job. The signing key is exposed only to the final trusted signing step. No cross-repository publisher PAT is required. A contributor pull request cannot trigger this deployment.

## Public inventory and retry

Every published tag has the form `<id>-v<version>`. Its downloadable assets are `<id>-<version>.cipack`, `SHA256SUMS.txt` and `release-notes.md`. The release asset URL includes the tag. The catalog URL always points to `main/catalog.json` in the distribution repository, independently of the latest release. The catalog authenticates the full-archive SHA-256 hash and download URL. The package authenticates its manifest, provenance and payload hashes.

The publishing job fills a draft, verifies existing assets on retry, and publishes it under GitHub release immutability. It downloads all three public assets, compares their digests and verifies the signed package against the exact build inputs before updating discovery. It merges with the verified previous catalog, keeps historical entries and commits with a compare-and-swap check. A retry after interruption finishes the same release. A digest or provenance conflict stops publication and requires a new connector version. Corrections to immutable assets ship under a new version. Catalog release history remains in Git.

For an independent end-to-end check from a clean profile, run the verification command below. It discovers the public catalog and installs, loads, disables, re-enables and removes each provider in isolation. It needs no cloud account. For private development and self-hosting use the connector guide. [Private connector tutorial](CONNECTORS.md)

```powershell
dotnet build tools/CloudInlet.ConnectorTool -c Release
dotnet tools/CloudInlet.ConnectorTool/bin/Release/net8.0-windows/CloudInlet.ConnectorTool.dll test-acquisition connectors/official-source.json artifacts/new-acquisition-check
```

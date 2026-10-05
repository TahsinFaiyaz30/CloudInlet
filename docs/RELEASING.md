# Versions, releases, and updates

`version.json` is the version source for CloudBay, its libraries, and the Windows installers. The public baseline is **1.0.0**. Assembly and MSIX versions append a fourth zero; MSI versions use the three central components. Major and minor must be at most 255, and patch at most 65535, so every distribution can represent the same version.

Both normal builds and packaging validate the central document. A malformed JSON document, duplicate version property, leading-zero component, prerelease suffix, or out-of-range component fails before producing a release.

## Publish a new version

1. Commit normal changes to `main`. Windows CI builds the complete solution and runs the hosted-compatible tests on pushes, pull requests, and manual dispatches. Native Cloud Files desktop acceptance tests remain a separate local validation gate because hosted Windows Server is not the target desktop environment.
2. Run the **Bump version** workflow on `main`, selecting Patch, Minor, or Major. It commits only `version.json`, includes a descriptive commit body, and explicitly starts CI for the new commit. The workflow does not bump versions for every source commit.
3. After Windows CI succeeds, **GitHub release** selects its exact source revision. It proceeds only while that revision is the latest `main` and the version has no immutable release tag. Both Debug and Release are built and tested again before their installer payloads are retained.
4. The publish job creates an annotated `vX.Y.Z` tag and a draft release, uploads the full inventory, verifies GitHub's actual asset lengths and SHA-256 digests, and then publishes the draft as latest. No updater sees the draft as a finished release.

The initial 1.0.0 release follows the same pipeline. The **GitHub release** workflow's manual `verify` action builds packages without creating a tag or publishing. Its `release` action can publish an unreleased central version after all build and test gates pass.

Local version changes use the same validator:

```powershell
.\scripts\get-release-version.ps1
.\scripts\bump-version.ps1 -Component Patch
# Optional: commit and push just the version bump from a clean tracked tree.
.\scripts\bump-version.ps1 -Component Patch -Commit -Push
```

The release scripts refuse to overwrite published bytes or move a tag to another commit. If uploading fails, re-run the failed **publish job** while its original build artifacts remain available. A rebuilt installer may contain different bytes and cannot replace an existing asset for the same version. If `main` changes after a tag was created, or an immutable tag belongs to a different source revision, advance the central version and publish that new version. Do not retarget the old tag. Stale completed CI runs exit without starting another build; a source that becomes stale while packaging or uploading leaves its draft unpublished.

Repository administrators can additionally enable GitHub's **immutable releases** setting to enforce these protections on the server. The pipeline uses a complete draft before publication, as recommended in [GitHub's immutable release documentation](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).

## Download inventory

Every GitHub release contains these six packages:

| Build | EXE installer | MSI installer | Portable app |
|---|---|---|---|
| Release | `CloudBay-X.Y.Z-win-x64-release-setup.exe` | `CloudBay-X.Y.Z-win-x64-release-setup.msi` | `CloudBay-X.Y.Z-win-x64-release-portable.zip` |
| Debug | `CloudBay-X.Y.Z-win-x64-debug-setup.exe` | `CloudBay-X.Y.Z-win-x64-debug-setup.msi` | `CloudBay-X.Y.Z-win-x64-debug-portable.zip` |

`updates-v1.json` records the repository, version/tag, build flavor, installer kind, architecture, filename, byte count, and SHA-256 for each variant. `SHA256SUMS.txt` provides a conventional checksum list. `release-validation.json` records the source commit, build/test results for both configurations, and signing status. Each manifest installer is bounded to 1 GiB, matching the app updater's validation limit.

The installer products are per-user and do not require administrator rights. Release and Debug have separate installation directories, startup registrations, instance identities, backup roots, and client state. Updates preserve the actual installed startup and desktop-shortcut choices and leave account credentials, backup configuration, and Windows folder mappings intact. Changing between EXE and MSI for the same build flavor requires an explicit installation change; silent updates never switch that identity.

Portable ZIPs contain the complete self-contained app and do not silently install themselves. Older development packages without installer distribution metadata also use the portable update path. Their account settings remain in the established per-user data directory when moving to a real installer.

Moving between Microsoft Store and EXE/MSI is a manual channel change. Uninstall the former installer before installing the other distribution so its old startup registration does not compete with the new one. Keep the client data and backed-up folders; changing the package format does not require deleting the cloud account, credentials, or backup files. Automatic updates stay within the installed channel.

To build complete packages locally from a fresh `artifacts/release-build` directory:

```powershell
$version = (.\scripts\get-release-version.ps1).version
.\scripts\build-release-artifacts.ps1 -Configuration Release -Version $version -OutputDirectory artifacts/packages
.\scripts\build-release-artifacts.ps1 -Configuration Debug -Version $version -OutputDirectory artifacts/packages
.\scripts\generate-update-manifest.ps1 -Version $version -AssetDirectory artifacts/packages
```

`build-release-artifacts.ps1` performs the build, tests, self-contained publish, license copying, update-worker build, optional signing, EXE/MSI compilation, and portable ZIP creation in that order. Installer tool versions are pinned by `get-installer-tools.ps1`. CI uses `-HostedTests`; local desktop release acceptance should run the complete test suite. `test-release-scripts.ps1` exercises version bounds, missing payload rejection, all update variants, draft publication, immutable reruns, digest mismatch, stale main, and Store identity gates using an isolated local Git remote and a fake GitHub transport. It never publishes test assets to GitHub.

## GitHub signing configuration

The `github-release` environment supports these optional secrets:

| Secret | Value |
|---|---|
| `CLOUDBAY_SIGNING_PFX_BASE64` | Base64 of the publisher's code-signing PFX |
| `CLOUDBAY_SIGNING_PFX_PASSWORD` | PFX password |

When configured, app-owned binaries, the external update worker, EXE, and MSI are signed with SHA-256 and an RFC 3161 timestamp and verified before publication. Secrets are read from environment values rather than command arguments. Temporary private-key files and certificates imported by the build are removed afterward. With no certificate configured, the pipeline deliberately produces unsigned packages and records that status. Unsigned local packaging is supported; Windows can display its publisher/security warning when first installing those packages.

The GitHub workflows use their scoped `GITHUB_TOKEN`: normal CI has read access, the publish job has `contents: write`, and the version workflow has `contents: write` plus `actions: write` to dispatch CI. A personal access token is not required. Actions are pinned to verified full commit SHAs. Optional environment protection can be configured in repository Settings without changing the workflow files.

## In-app update behavior

EXE and MSI installations select an update using their immutable distribution identity together with the compiled build flavor. Debug receives Debug, Release receives Release, and the installer kind and architecture stay the same. The update worker runs outside the installed application directory, waits for graceful shutdown, applies the matching installer silently, and restarts the app. It never terminates a foreign process to make room for an update.

Update preferences support automatic checks, a configurable check interval, automatic download, and automatic installation. Automatic checks default to daily; automatic download and installation require the user's selected preference. Manual checks use the same feed and backoff rules. If a newer update replaces a downloaded but uninstalled version, the old package is removed before the new package downloads. Streamed downloads validate the complete byte count and SHA-256, and installation validates the saved package again. Failed downloads never become ready-to-install packages.

**Delete downloaded updates** removes current packages, older packages, partial downloads, and abandoned installer handoff files from the owned update cache. Account settings and update preferences remain intact. A package already being installed stays protected until the installer finishes; a locked or linked item is reported rather than silently treated as deleted.

The feed uses GitHub's public release-asset endpoint rather than making a REST API request for every check. Conditional requests retain ETags, manual requests have a minimum cooldown, and rate-limit/Retry-After responses persist their next permitted check time across restarts. Errors also back off. This avoids consuming the unauthenticated REST quota for routine checks; see [GitHub's rate-limit rules](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api). Portable builds offer a manual release download, and Microsoft Store installations use the Store's update channel.

## Prepare Microsoft Store

The Store workflow is prepared; production publishing requires a reserved Partner Center app identity. No example identity is submitted automatically. Without the identity variables below, normal GitHub releases still ship EXE/MSI/portable assets and skip Store packaging. Manually requesting production Store packaging without configuration fails with an actionable configuration error.

Set these repository variables from the reserved app's **Product identity**:

| Variable | Partner Center value |
|---|---|
| `CLOUDBAY_STORE_IDENTITY_NAME` | Package/Identity/Name |
| `CLOUDBAY_STORE_PUBLISHER` | Package/Identity/Publisher, including the full `CN=` value |
| `CLOUDBAY_STORE_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName |
| `CLOUDBAY_STORE_APPLICATION_ID` | Store application ID used by the submission API |

After these are present, GitHub release packaging can also attach the production `.msix`, `.msixupload`, and package identity record. The **Microsoft Store** workflow can independently package any verified published tag. It downloads the matching Release portable ZIP and checks its bytes against that release's manifest before repackaging. Use `submit: false` to retain upload artifacts without contacting Partner Center.

For API submission, associate an Entra application with Partner Center and configure these secrets in the `microsoft-store` environment:

| Secret | Value |
|---|---|
| `CLOUDBAY_STORE_TENANT_ID` | Entra tenant GUID |
| `CLOUDBAY_STORE_CLIENT_ID` | Entra application/client GUID |
| `CLOUDBAY_STORE_CLIENT_SECRET` | Associated application's client secret |

Microsoft requires reserving the app and completing an initial Partner Center submission, including age ratings, before the API can create later submissions. The workflow preserves the existing listing, uploads only the new package, refuses to replace an already-pending draft, and commits only when `submit: true` is explicitly selected. Its default publication mode is Manual; Immediate must be selected intentionally. Credential values, bearer tokens, and signed Azure upload URLs are omitted from errors. See [Microsoft's submission prerequisites](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services) and [submission API process](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions).

The manifest contains the full-trust startup task `CloudBayStartup` and requests `runFullTrust` plus `unvirtualizedResources`. It disables registry/file-system write virtualization so native backup can update the real Windows known-folder mappings and preserve the same client data on updates. These restricted capabilities need a justification during Store review; packaging success is not certification approval. See [Microsoft's virtualization documentation](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization).

Store packaging checks the compiled Release identity and generates a separate `resources.pri` for the exact manifest package name, preserving the unpackaged application's resource index in the GitHub installer payload. MakePri inspects the package's primary map and compiled application XAML before MakeAppx runs. This follows [Microsoft's package resource index guidance](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion#generate-a-package-resource-index-pri-file-using-makepri); merely copying the unpackaged executable into an MSIX is insufficient for its WinUI resource lookup.

Local manifest/schema validation can run before any reservation:

```powershell
.\scripts\build-store-package.ps1 -AppFolder path\to\published\Release\App `
    -Version 1.0.0 -OutputDirectory artifacts/store-validation -LocalValidationIdentity
```

The separate `CloudBay.LocalValidation` identity produces unsigned test MSIX/MSIXUPLOAD files. It is not installable as a trusted public Store package without a suitable local test signature, and the submission script rejects it by reading the embedded manifest, even if its file is renamed. Production packages use the exact reserved identity; Microsoft manages Store signing and updates after a successful submission.

On a desktop with Windows Developer Mode already enabled, `test-store-runtime.ps1 -PackagePath path\to\local-validation.msix` can development-register only this test identity and activate its isolated runtime probe. The probe checks real package identity, the Store update channel, package location, compiled version, and the declared startup task. Validation records process exit evidence, stops on an early crash, and caps an unresponsive probe at 60 seconds. The script removes that exact test registration afterward and verifies that regular account settings, credential bytes, and Windows folder mappings are unchanged. It does not enable Developer Mode, install a certificate, change machine trust, connect a backup account, or turn on the startup task.

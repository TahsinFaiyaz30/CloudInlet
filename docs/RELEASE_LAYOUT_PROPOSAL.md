# Approved GitHub release design

**Approved design record — 11 October 2026.** The user approved this structure and the one-time recreation of the 1.2.0 release object. The user-facing example is [CloudInlet 1.2.0 release preview](RELEASE_PREVIEW_1.2.0.md). [RELEASE_STYLE.md](RELEASE_STYLE.md) is the governing contract for future releases; [RELEASING.md](RELEASING.md) describes its implementation. This document records the design decisions and authorized migration, not the live publication status.

## Page layout for future releases

1. Use the GitHub release title for the version. Start the body with a short description of the main changes; do not repeat the release title inside the notes.
2. Prominent download buttons for Release EXE, MSI, portable ZIP and the available Store download.
3. Separate tables stacked vertically: **Release (x64)**, then **Debug (x64)**, then **Metadata**. Use **File | Type | SHA-256** for each table. Put the shared architecture in the app-table heading instead of repeating it in every row. If a future release adds another architecture, give it a clearly labeled section. The File cell shows the complete actual filename as a clickable download link; Type shows only the file format, such as EXE, MSI, ZIP, MSIX, JSON or TXT. Display each file's full, selectable 64-character hash without truncation or inserted whitespace, with a GitHub-native Copy control for each checksum. Keep PowerShell verification instructions collapsed below the tables.
4. Choose changelog categories from the actual changes in each release, rather than a fixed template. For 1.2.0, use **Features**, **Bug fixes**, **Improvements**, **Compatibility**, and collapsed **Dependencies and release tooling**. Other releases may need Performance, Security, Breaking changes, Deprecations, or another specific heading. Include a category only when supported by real changes; omit empty or redundant categories.
5. An implementing commit link beside every change, plus a comparison link covering the complete release.
6. Keep installation requirements, upgrade guidance and a short, evidence-backed validation summary in collapsed sections. Put important compatibility or breaking changes in the visible changelog. Link the full comparison and supporting documentation; keep publishing instructions in this plan.

Describe each change concisely, followed by its commit link. Use a second sentence when needed to explain an important impact or migration requirement; completeness takes priority over an arbitrary sentence limit. Avoid repeating bold labels inside every bullet, explanatory paragraphs above categories, and internal publishing commentary in the release body. A commit containing several distinct changes produces several appropriately categorized bullets; changes spread across commits receive multiple links. Do not list commit subjects as the changelog or claim existing functionality as newly added. Review the complete previous-tag-to-release-tag diff when preparing the notes.

GitHub Markdown supports linked image badges for the prominent downloads. The table also provides ordinary links, so downloads remain accessible if badge images do not load.

## Approved public inventory: nine files

| Keep | Why |
| --- | --- |
| Release EXE, MSI and portable ZIP | Everyday use, deployment and portable installation. |
| Debug EXE, MSI and portable ZIP | Development/troubleshooting and updates for existing Debug installations. |
| Store MSIX | The current Store package, explicitly requested for inclusion while approval is pending. |
| updates-v2.json | Required for the app updater; retain all six desktop variants. |
| SHA256SUMS.txt | One checksum list for the public packages and update manifest. |

The original 1.2.0 release had sixteen uploaded files. The approved inventory removes these seven public assets while preserving their internal build/certification evidence:

| Remove from public release | Retain internally for |
| --- | --- |
| CloudInlet-1.2.0-win-x64-release-store.msixupload | Partner Center submission. |
| CloudInlet-1.2.0-win-x64-release-store-package-info.json | Store identity, version and source validation. |
| release-validation.json | Required publication gate and CI evidence. |
| Four EXE/MSI .sha256 sidecars | Packaging/signing checks, if needed internally. Public checksums are consolidated in SHA256SUMS.txt. |

This removes submission files, internal reports and duplicate checksum files, while retaining every desktop distribution and the Store MSIX.

The current MSIX metadata records **signed: false**, and the package has no AppxSignature.p7x. Its download must therefore be labeled as an unsigned package pending certification, not an approved, ready-to-install Store release.

GitHub also supplies source ZIP/tar.gz archives from the tag. Identify them briefly in the collapsed installation notes; they are not manually uploaded assets or runnable installers. See [GitHub release documentation](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

## Store download after approval

Once Microsoft approves the submission **and the approved app is publicly available**:

1. Obtain the official small Store Web Installer for product **9N63JNRR5VB4** from Microsoft's public listing/badge flow.
2. Verify the Microsoft signature and correct CloudInlet product before attaching it. Use the actual verified filename in the download table and button.
3. Add that installer to the release and replace the temporary unsigned MSIX download. The public inventory remains nine files.
4. Update the Store button, file-table description and checksum listing to match. Preserve the six desktop package bytes and updates-v2.json.
5. Label the Store installer as installing the **current Store version**, not as a permanently pinned 1.2.0 binary.

After verifying the actual Store installer filename, replace the MSIX row with:

| File | Type | SHA-256 |
| --- | --- | --- |
| Complete verified installer filename, linked to its download | EXE | Full verified hash with a GitHub-native Copy control |

The File cell in the Release table will show the verified Microsoft Store installer's complete filename and link to its download. Inspect that installer's actual architecture and delivery behavior before placing it under an architecture-specific heading; use a separate Store section if needed. Keep the note concise: **Store installer requires internet and installs the current Store version.**

The prominent button will read **Get from Microsoft Store**. Until the verified installer exists, keep the MSIX button and its pending-certification label; do not publish a broken placeholder link or an approval claim.

Microsoft documents this lightweight EXE and the Direct badge installation flow in [Store Web Installer guidance](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution). The full MSIXUPLOAD remains an internal submission artifact.

This is the approved approach for a later distribution-metadata update, not a scheduled approval monitor. Perform the verification above before changing the Store download.

## Future release automation

- Stage the public inventory explicitly; do not upload everything matching a broad filename wildcard.
- Continue building Release, Debug and Store artifacts and validating their source/version/identity before publication.
- Keep submission packages and validation reports in CI artifacts, with workflow links in the collapsed release validation section and repository documentation.
- Generate SHA256SUMS.txt from the public payloads and update feed. Exclude the checksum file itself and internal-only artifacts. Then hash SHA256SUMS.txt for its release-table row; the checksum list cannot contain its own hash.
- Generate every displayed table hash from the final asset bytes and cross-check it against GitHub's uploaded asset digest. Regenerate the preview from the cleaned public inventory so its checksum-index row matches the final SHA256SUMS.txt bytes.
- Provide a GitHub-native Copy control for each checksum without custom scripts. Verify the live rendering and exact 64-character copied value before claiming that the control works; selectable text or a decorative button alone does not meet the requirement.
- Generate the verification example with the current Release EXE filename. Show the complete hash with `(Get-FileHash -LiteralPath '<filename>' -Algorithm SHA256).Hash`; tell users to compare it with the same file and build in the table or checksum list, ignoring letter case. Keep these instructions collapsed beneath the download table.
- Preserve exact installer/flavor matching, canonical schema 2 and the frozen CloudBay 1.1.2 bridge.
- Generate download links, architecture headings and table hashes from the actual version and final files. Current 1.2.0 app packages are x64, confirmed by the updater manifest, publish settings and MSIX identity. Never advertise x86 or ARM64 builds unless those artifacts exist and are validated.
- Prepare and review categorized notes against the complete release diff; include commit links beside every item.
- Verify expected assets, hashes, sizes and download links after publication.
- Treat the later MSIX-to-Store-installer change as an explicit distribution-metadata update. Update the public checksum index to reflect that change; never silently replace desktop installers or the update feed.

## Authorized one-time recreation of 1.2.0

The user approved removal and recreation of the 1.2.0 GitHub release object after reviewing this structure. This authorization applies only to this presentation and inventory cleanup; future releases retain the normal immutable-tag and package guards.

1. Save the existing release metadata and all sixteen assets, and verify their hashes.
2. Keep tag **v1.2.0** at **7c1b42f6edd3c1c7b0b1a8a89b92cb24916fe478**. Do not rebuild the app or move/delete the tag.
3. Prepare the approved release text, nine-file inventory and regenerated public checksum index locally.
4. Delete and recreate the release object against the existing tag, using the exact existing bytes and names for all six desktop packages, the Store MSIX and updates-v2.json.
5. Upload the cleaned inventory, verify all downloads and updater selections, and restore its intended latest-release status.

Deleting/recreating the release briefly interrupts its download/update URLs. Preserve a complete backup before the operation. The current ordinary release workflow rejects an already-existing tag, so this requires an explicit one-time operation; do not weaken normal version and immutable-binary checks.

Only the release presentation, selected public inventory and checksum index change. Existing application package bytes, historical tags, the CloudBay bridge and the already-uploaded Partner Center submission remain intact. Any later automation changes belong to a new source commit, not a moved 1.2.0 tag.

## Approval status

The design is approved, including the later clarification to show complete filenames, plain file-format types and a Copy control for each checksum. The release-object recreation and implementation verification are separate operations with backup, upload and rendering evidence; consult the current repository handoff and GitHub release for their completion status. Do not interpret this design record as evidence that Copy controls work, publication has completed or Store certification has passed.

## Recorded verification (supporting detail)

The recorded 1.2.0 checks include 959 local desktop tests, 828 hosted tests in each of Debug and Release, and 116 release-script assertions. UI checks produced 452 full-suite and 104 focused layout captures in both themes. These are the existing package-verification results, separate from validation of the later release-presentation automation.

[Windows CI](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/38114892013) · [Release build](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/38115258247)

Hosted tests exclude Native and LiveProvider cases. Live B2/OneDrive acceptance, Windows 10 execution and real personal-folder redirection remain unverified. Store certification is separate. Before publication, refresh Store availability; the preview currently reflects the recorded pending submission.

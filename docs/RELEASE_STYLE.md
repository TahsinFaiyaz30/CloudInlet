# GitHub release style

This is the standing release-page contract for contributors and agents. Apply it without asking the user to choose headings or repeat presentation preferences. Use the best categories for the actual changes, and keep the page complete, concise and useful to someone deciding what to download or whether to update. [RELEASING.md](RELEASING.md) covers the publication pipeline.

## Page structure

1. Set the GitHub release title to **CloudInlet X.Y.Z**. Start the notes body with one short summary of the release's main benefit; do not repeat the title as a heading inside the body.
2. Prominent linked download badges for the Release EXE, MSI, ZIP and the available Store package. Provide ordinary table links as well.
3. **Release (x64)** and then **Debug (x64)**, in separate tables stacked vertically. Follow with a **Metadata** table. Each uses **File | Type | SHA-256**. The File cell shows the complete actual filename as a clickable download link. The Type cell shows only the file format: EXE, MSI, ZIP, MSIX, JSON or TXT, as appropriate. Put the architecture in each app-table heading; add another clearly labeled section only when actual validated packages support it.
4. A collapsed **Verify download (SHA-256)** section with a copyable PowerShell command using the current Release EXE filename. Tell readers to match all 64 characters for the same file and build; hash letter case does not matter.
5. A categorized changelog, then collapsed installation and validation details. Finish with the complete previous-tag-to-release-tag comparison link.

Keep filenames on one line with the supported `nowrap` table-cell attribute; left-align File and SHA-256 headers and center Type. Let GitHub scroll the table horizontally in narrow windows rather than squeezing filenames into tall stacks. Full filenames, unbroken 64-character hashes and native Copy controls require more width than a narrow sidebar; do not truncate values or insert whitespace into a checksum to force them to fit.

Show every full, selectable SHA-256 value without truncation or inserted whitespace, and provide a GitHub-native Copy control for each checksum. The generator uses fenced code blocks inside HTML table cells so GitHub supplies its native clipboard buttons. Use GitHub-supported rendering without custom scripts or decorative buttons that cannot copy. Verify the rendered release page and each control's exact 64-character value, and exercise the native Copy action; selectable text alone does not satisfy the Copy-control requirement. Compute every displayed hash from the final public file bytes, including the checksum index itself. `SHA256SUMS.txt` covers all other public assets but cannot contain its own hash. Keep the complete filenames visible in the tables so readers can match downloads and checksums unambiguously.

## Changelog judgment and completeness

Read the complete release diff, commit bodies and relevant validation evidence. Organize changes by their meaning, not by commit order. Features, Bug fixes, Improvements, Performance, Compatibility, Security, Dependencies, Breaking changes and Deprecations are possible categories, not a required list. Use only relevant headings; combine overlapping categories and omit empty ones. Collapse low-interest dependency or release-tooling details when that keeps the main changelog readable. Keep breaking changes and required migration steps visible.

Cover every material change. One commit may need multiple bullets; one change may need links to several commits. Each bullet describes the concrete new behavior or corrected problem and links to the implementing commit(s). Do not substitute raw commit subjects for release notes, claim existing behavior as new, count documentation corrections as product features, or add after-tag work to a historical release. Use a second sentence where it explains a meaningful impact; avoid duplicating the same change in several categories.

Validation notes must state what passed and what remained unrun or unavailable. Refer to exact workflow/evidence links and relevant environment boundaries. Never turn a build success into a claim of complete functional validation, Store approval, Windows-version coverage or live-provider acceptance. The generator checks source relationships and files; it cannot prove the semantic completeness or truth of prose. The author remains responsible for reviewing those claims.

## Curated source and generated output

Before advancing `version.json`, commit curated notes for the next version at `docs/releases/<version>.json`. The generator requires those notes and refuses to substitute a generic paragraph or an automatic commit log. This outline shows the JSON fields; replace illustrative values with reviewed release data:

```json
{
  "schemaVersion": 1,
  "version": "1.2.0",
  "previousTag": "v1.1.2",
  "summary": "A short summary of the release's main benefit.",
  "categories": [
    {
      "title": "Bug fixes",
      "collapsed": false,
      "items": [
        {
          "text": "Describe a verified user-facing correction.",
          "commits": ["<full 40-character implementing commit SHA>"]
        }
      ]
    }
  ],
  "installationNotes": ["A verified installation or upgrade requirement."],
  "validation": {
    "items": ["The relevant validation result and its practical limits."],
    "links": [{"label": "Validation evidence", "url": "https://github.com/TahsinFaiyaz30/CloudInlet/actions"}]
  },
  "storeStatus": "Certification pending."
}
```

Use single-line text values and nonempty category, item, installation-note and validation-item lists. Category titles are free-form and unique; `collapsed` is optional. Commit references must be full SHAs inside `previousTag..ExpectedRevision`; the previous tag must be a canonical earlier version and an ancestor of the release source. `validation.links` and `storeStatus` are optional. Omit the Store status when no Store package is included; otherwise verify the current status before authoring it. Signing status, file hashes, architecture headings and download URLs come from validated build artifacts, not manually copied release prose.

After packages, internal validation evidence, the update manifest and public checksum index are prepared:

```powershell
$version = (.\scripts\get-release-version.ps1).version
$revision = (git rev-parse HEAD).Trim()
pwsh -NoProfile -File scripts/generate-release-notes.ps1 `
    -Version $version -AssetDirectory artifacts/packages `
    -RepositoryRoot . -ExpectedRevision $revision `
    -OutputPath artifacts/packages/release-notes.md
```

`-NotesPath` can select another curated JSON file for an explicitly controlled operation; ordinary releases use the default versioned path. Review the generated Markdown and run `scripts/test-release-scripts.ps1`. The publisher regenerates and validates notes before creating a tag or changing GitHub. Missing, invalid or out-of-range notes are a publication failure, not a reason to bypass the format.

## Public assets and internal evidence

Publish exactly these eight desktop files, plus the current Store MSIX when production Store packaging is configured:

- Release EXE, MSI and portable ZIP.
- Debug EXE, MSI and portable ZIP. Debug is required: existing Debug installations select matching Debug updates.
- `updates-v2.json`, retaining all six desktop installer/flavor variants.
- `SHA256SUMS.txt` for the public payloads and update feed.
- The production Store MSIX, when built, clearly labeled with its actual signing and certification status.

Keep the MSIXUPLOAD, Store package-identity JSON, combined/per-build validation reports and four installer checksum sidecars in retained CI artifacts. They remain validation/submission inputs; they are not public download choices. Do not remove these checks merely because the files are no longer uploaded as release assets. GitHub also generates source archives from the tag; identify them as source, not installers, in installation notes.

## Store download transition

Until an approved app is publicly available, retain the requested MSIX and describe unsigned/pending status accurately. Do not advertise it as an approved, trusted Store installation.

Once Microsoft approval and public availability are verified, obtain the official small Store Web Installer for product **9N63JNRR5VB4** from Microsoft's public flow. Verify its Microsoft signature, product identity and actual filename before proposing or performing the already-authorized distribution update. Check its architecture/delivery behavior; use a separate Store section if it cannot accurately sit under the app's architecture heading. Replace the temporary MSIX public asset with that verified installer, update the Store badge/table and regenerate checksums. The installer must be described as requiring internet and installing the **current Store version**, not a pinned historical app version. Never invent a download URL, infer approval from a local package build, or reuse an unverified EXE.

This is a deliberate distribution-metadata update, not permission to replace desktop application bytes or the updater feed. Normal publication guards stay in force. If release immutability prevents later edits, leave the existing release intact and add the verified Store distribution to the next release; do not disable protections or recreate releases on the agent's initiative. See [Microsoft's Store Web Installer documentation](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution).

## Historical release protection

Never move a published tag or rebuild installers to replace published bytes. The approved one-time cleanup of the **1.2.0 release object** keeps tag `v1.2.0` at `7c1b42f6edd3c1c7b0b1a8a89b92cb24916fe478`, preserves the six desktop packages, Store MSIX and update feed byte-for-byte, and changes only the release page, public inventory and checksum index. It is not a reusable exception for future releases. Preserve a verified backup and check every public asset and updater variant after that operation.

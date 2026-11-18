# CloudInlet 1.2.1 publication — 2026-11-18

[GitHub 1.2.1](https://github.com/TahsinFaiyaz30/CloudInlet/releases/tag/v1.2.1)
is published from `a09f34cbd2e4576c419d4da9fa7a3844cea6acd3`. All nine public
assets are present: six Release/Debug desktop packages, the Store MSIX, schema-2
update feed and checksum index. The six updater variants and public SHA-256
values were checked. The rendered page has separate Release/Debug tables and
nine full checksum Copy controls; the Release EXE control returned GitHub's
`Copied!` confirmation. This was UI feedback, not an independent clipboard read.
The existing 1.2.0 tag and application bytes were not replaced.

## Build evidence

- [Windows CI](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/44266566742)
  passed for the release source.
- [Package builds](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/44268204469)
  passed for Release, Debug and Store. Release and Debug each passed 1,278 hosted
  tests with no failures or skips. The initial publication job correctly stopped
  because two Store build logs were outside the permitted public inventory.
- [Publication completion](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/44285058424)
  retained those logs separately, verified the original build artifacts, and
  published those bytes through the existing publication checks. No application
  rebuild or tag replacement was used. Main now keeps the logs in a separate CI
  artifact for future releases.

Hosted tests exclude Native and LiveProvider cases. Earlier isolated Windows 11
acceptance remains development evidence; the combined full-app UI recovery path
with naturally triggered MSI 3010, Windows 10 execution, production Store
delivery and complete live-provider acceptance remain unverified.

## Microsoft Store

Product `9N63JNRR5VB4`, submission `1152921505702060020`, now has package
`CloudInlet-1.2.1-win-x64-release-store.msixupload` saved as **Validated**.
Resubmission completed on **2026-11-18**. Partner Center confirms **In
certification**, with **Pre-processing** in progress. Microsoft approval remains
pending; no further owner action or local test is queued for this submission.

The support field uses `tahsinfaiyaz30@gmail.com`; the optional GitHub developer
website was removed. Both capability justifications and the 5,538-character
Additional Testing Information were saved and re-read. The notes describe the
three Windows 11 directory exclusions, two Shell mapping keys, broader Windows
10 fallback, user choices, retained state and evidence limits. Existing reviewer
credential entries were retained. Publishing remains automatic after Microsoft
approval. Validation and submission are not certification approval.

| Artifact | SHA-256 |
| --- | --- |
| Store submission archive | `b204ca2dd4008e325d3eb16b8ff82fa9f8579dfa66243fa0ae9077243ee15b4c` |
| Embedded/public Store MSIX | `d7cc8c008e6770e8507a3ff3d71b06be673d32bac3bc7f353d913a65a8b0ba59` |

Local evidence is retained under `artifacts/release-1.2.1/`, including
`completion-record.json`, `rendered-release-check.json`,
`store-reviewer-notes-verified.json`, `store-capabilities-verified.json`, the
exact `store-additional-testing.txt`, `store-submitted-state.txt`,
`store-submitted.png`, and `store-submission/`.

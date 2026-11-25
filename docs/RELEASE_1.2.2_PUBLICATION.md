# CloudInlet 1.2.2 publication

Verified 2026-11-25. This maintenance release uses the non-modular app; it does
not include the separately developed connector-pack system.

## GitHub

[CloudInlet 1.2.2](https://github.com/TahsinFaiyaz30/CloudInlet/releases/tag/v1.2.2)
was published at 13:01:57 UTC (19:01:57 Dhaka). Annotated tag `v1.2.2` resolves
to `337c399d35b43d3a7571e2d6b631dbb87f29e59d`.

- [Windows CI 45615978192](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/45615978192)
  passed the solution build, 1,299 hosted-compatible tests, 141 release-script
  assertions, and silent EXE/MSI installation and update fixtures.
- [Release run 45617669175](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/45617669175)
  passed all five jobs. Both Release and Debug passed 1,299 hosted-compatible
  tests before packaging. Store packaging and publication validation passed.
- The nine public assets comprise all six Release/Debug desktop packages,
  `updates-v2.json`, `SHA256SUMS.txt`, and the Store MSIX. All six updater entries
  match GitHub's asset lengths and digests; all eight checksum-index entries
  match their asset digests. Downloaded metadata hashes also match.
- The rendered page has full linked filenames, separate Release/Debug tables,
  and nine exact 64-character SHA-256 Copy controls. The native Copy action
  displayed `Copied!`; an independent OS clipboard read was not performed.

GitHub packages remain unsigned and are labeled accordingly. Published app
bytes and previous release tags were not replaced.

## Microsoft Store

Product `9N63JNRR5VB4`, submission `1152921505702504775` (Submission 2), received
`CloudInlet-1.2.2-win-x64-release-store.msix`, version **1.2.2.0**, x64.
The upload used the exact GitHub release asset, not a local rebuild:

```text
ee4c9bb81bbdb311f99668b7a38d6fba4294d886e845bc4335dff35126214c06
```

Its manifest was checked against the existing `TahsinFaiyaz.CloudInlet` identity
and publisher `CN=CC23EA69-42B2-45CB-B62F-4451A90083B3`. Partner Center validated
the package and automatically superseded the older package in this draft when
the page was saved. The approved production listing, device families,
capabilities, and existing reviewer credentials were preserved. The English
What's new field and reviewer notes describe the maintenance fix and its limits.

The [application overview](https://partner.microsoft.com/en-us/dashboard/products/9N63JNRR5VB4/overview)
confirmed **Update in certification**, **Submission: Complete**, and
**Pre-processing: In progress** at approximately 21:37 Dhaka. It also confirmed
that publishing starts as soon as certification passes. This is submission
confirmation, not Microsoft approval or proof that customers have received 1.2.2.

## Evidence and boundaries

Ignored records are in `artifacts/startup-1.2.2/` in the hotfix worktree:
`windows-ci-result.json`, `windows-ci-full.log`, `release-build-full.log`,
`github-release.json`, `public-metadata/`, `store-package-verification.json`,
and `publication-verification.json`. The matching Store MSIX is in
`store-package/`; the full upload/identity artifacts remain in the successful CI run.

See [startup recovery evidence](STARTUP_RECOVERY_1.2.2.md) for the reproduced
failure, local native checks and preservation rules. The customer's exact
profile contents, live providers, and delivery of this new Store update were
not tested. No additional VM acceptance batch is queued. The modular working
branch was left unchanged; carrying this fix into it is separate work.

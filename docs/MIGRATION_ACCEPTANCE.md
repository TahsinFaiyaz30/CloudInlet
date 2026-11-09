# Migration acceptance

Migration remains unreleased. Deterministic and desktop tests do not establish live cloud, real reboot or personal Windows folder-redirection acceptance. Keep cloud-prefix discovery unavailable until all required live cases pass. Never replace published packages to include this work.

## Local checks

Run the normal build, desktop suite and release-script checks from `AGENTS.md`. `ClientFolderMigrationTests` includes actual temporary-directory moves, protected receipts and saved settings; it injects Windows folder mapping writes. Its partial-write cases cover failure before/after each mapping, Retry, Revert, unrelated settings and external mapping changes. `NativeKnownFolderTests` reads actual Shell policy and paths without changing personal mappings.

Installer acceptance uses isolated Debug installations. The current native suite simulates boot-boundary changes and labels them accordingly. Actual reboot and native Shell redirection require a dedicated Windows guest and disposable account; never use the developer's personal folders or restart the host as an acceptance fixture.

## Opt-in B2 prefix test

`CloudPrefixProviderAcceptanceTests.RealB2PrefixMigrationPreservesHistoryMetadataAndContent` exercises initial completion and recovery through the production migration engine, provider transport and protected journals. It uses an isolated test binding, not the user's application settings. Its small in-memory fixture contains retained upload versions, a hide marker and a nested visible file. Assertions check unchanged original history and order, copied content and metadata, encryption settings, timestamps, current visibility and completed-state immutability.

The five scenarios are:

- Initial completion with exactly one destination version for every source version.
- Cancellation after two durable destination acknowledgments, followed by closing and reopening both journals. The same destination IDs must survive without duplicate copies.
- One successful copy response deliberately withheld from the production engine after the test records its creation acknowledgment. Both production journals must remain pending until retry recovers the exact committed version from its validated receipt. The test ownership ledger is never given to the production engine.
- Revert after two durable destination acknowledgments and journal reopen, retaining every original version and an independently completed selection.
- A committed deletion response withheld during Revert, followed by journal reopen. Production verifies that exact version is already absent and removes only the remaining acknowledged destination version.

These scenarios reopen journals within one process. They do not establish recovery across a process crash or Windows reboot, and withheld responses are controlled test faults, not actual network outages.

The test loads only the existing Windows DPAPI credential store at `%LOCALAPPDATA%\CloudBay\Validation\credentials.dpapi`. It requires a key restricted to exactly one explicitly named test bucket and these capabilities:

- `listBuckets`, `listFiles`, `readFiles`, `writeFiles`, `deleteFiles`
- `readBucketEncryption`, `readBucketRetentions`, `readFileRetentions`, `readFileLegalHolds`, `readBucketReplications`

Unknown protection, retention, legal hold, overlapping lifecycle rules, replication, unsupported encryption or pre-existing fixture history prevents writes. Do not weaken these checks, alter bucket policies, broaden an existing production key or use existing `CloudBay/` history as a fixture.

Use PowerShell 7 in a dedicated test process. Set the non-secret expected bucket and an existing absolute directory under ignored `artifacts/`. Start with read-only preflight:

```powershell
$env:CLOUDINLET_RUN_B2_MIGRATION_ACCEPTANCE = '1'
$env:CLOUDINLET_ACCEPTANCE_B2_BUCKET = '<reviewed test bucket name>'
$env:CLOUDINLET_B2_MIGRATION_ARTIFACTS = (Resolve-Path artifacts/migration-review).Path
$env:CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY = '1'
dotnet test CloudInlet.Tests/CloudInlet.Tests.csproj -c Release --filter 'FullyQualifiedName~CloudPrefixProviderAcceptanceTests' --logger 'trx;LogFileName=cloud-prefix-preflight.trx' --results-directory artifacts/migration-review
```

Read-only mode intentionally ends inconclusive; inspect the TRX reason and durable `cloud-prefix-acceptance-<guid>/report.json`. A successful test command with a skipped/inconclusive case is **not** live migration acceptance. Missing capabilities stop before inventory creation or mutation. With complete access, read-only mode inspects the fresh prefixes and policy but still does not enable writes.

Only after the bucket and its permissions are provisioned for this acceptance workflow, remove `CLOUDINLET_B2_MIGRATION_PREFLIGHT_ONLY` in that dedicated process and run the same filtered test. This creates small fixture objects under `<allowed prefix>CloudInletMigrationAcceptance/<guid>/CloudBay/` and its sibling `CloudInlet/`, then permanently removes only the acknowledged immutable fixture versions after successful verification. Requests outside the exact fresh scope, unknown operations, wrong methods and unacknowledged cleanup IDs are rejected by the test transport. Upload requests use the exact URL returned by B2; these URLs can contain additional path segments and use a different provider upload host ([B2 upload documentation](https://www.backblaze.com/apidocs/b2-upload-file)).

On failure, the test stops and retains metadata journals and an acknowledgment ledger in the explicit evidence directory. Before each mutation it also flushes `mutation-attempts.jsonl`, containing only the endpoint, scoped filenames, relevant immutable IDs, operation receipt, counter and timestamp. A logged attempt does not prove a provider mutation completed; the separate creation acknowledgment establishes test ownership. An unwritable attempt log prevents dispatch. It does not broadly clean cloud prefixes or infer ownership from a listing. Do not blindly rerun cleanup after an interrupted request or loss of its acknowledgment. Review the exact owned IDs and protection state first. No payload files or authentication tokens belong in evidence. Keep the evidence directory outside test-runner deployment folders, which can be removed on skipped runs.

Failure reports include `providerFailure` when the exception chain contains a B2 API rejection. It contains only the HTTP status and the production parser's allowlisted error code; provider messages, response bodies, URLs and credentials are omitted. An unknown upload outcome is retained as a failure even when a later independent fixture passes. Starting a fresh isolated fixture does not resolve or authorize cleanup of the earlier uncertain upload.

## Additional live gates

- `CloudPrefixProcessRecoveryAcceptanceTests`: `prepare` pauses after durable acknowledgments and authenticates its exact fixture evidence; `resume` runs in a separate process after the first process has exited. It must preserve previous destination IDs and copy only the remaining version. Set `CLOUDINLET_B2_PROCESS_ACCEPTANCE_PHASE` and the same explicit `CLOUDINLET_B2_PROCESS_ACCEPTANCE_RUN` GUID for both phases. This is a real process boundary after a clean pause, not an abrupt kill or reboot.
- `CloudPrefixExternalWriterAcceptanceTests`: a separately authenticated client writes a new source version during a pause. Retry must stop visibly without merging histories, deleting versions or switching references. The explicit keep-original recovery must preserve that changed history.
- `CloudPrefixLostHideAcceptanceTests`: the guard durably records a real hide acknowledgment but withholds it from production. Retry must refuse to guess ownership or repeat the hide. Explicitly keeping the original location retains all partial copies and authenticated journals, without claiming completion or successful Revert.
- `CloudPrefixDesktopAcceptanceTests`: opt in with `CLOUDINLET_RUN_B2_DESKTOP_MIGRATION_ACCEPTANCE=1`. This uses a fresh ordinary profile subdirectory, isolated settings and credentials, actual Windows placeholder registration and SQLite manifests. It verifies dirty local bytes, completed-binding reopen and hydration using the new immutable file ID. It does not redirect personal Windows folders.
- `CloudPrefixMultipartProviderAcceptanceTests.RealB2MultipartMigrationResumesOwnedPartsAfterJournalReopen`: opt in with `CLOUDINLET_RUN_B2_MULTIPART_ACCEPTANCE=1`. A 50 MB RAM seed is copied server-side into a real 5 GB source, then migrated through the unchanged production multipart threshold. Independently calculated part and whole-file hashes verify content without payload disk staging. Allow roughly 10.1 GB transient bucket storage. Only an identical unconfirmed part on the same authenticated unfinished ID, immutable source and range may retry; acknowledged parts and creating requests cannot replay.
- `RealB2MultipartResumesPreviouslyAuthenticatedFixture`: explicitly set `CLOUDINLET_B2_MULTIPART_RESUME_RUN` to an existing interrupted fixture. The protected production journals must authenticate every source, unfinished destination and completed part before any mutation. Live inventory can verify those receipts, never establish ownership. This refuses changed source/protection, foreign unfinished IDs, completed bindings and missing evidence. Select this method alone when recovering an existing fixture; do not run the entire multipart class inadvertently.

All fixture cleanup remains separate from product recovery and requires exact test-owned acknowledgments plus current protection checks. A withheld response is known to the test guard but deliberately unknown to production; never pass the guard's ledger to the product to manufacture recovery.

Multiple versions claiming one operation receipt, or a hide with no protected acknowledgment, remain ambiguous: preserve the evidence instead of adopting or deleting a guessed version. **Keep original location** is a separately confirmed exit available only before references begin switching and after independently verifying the original local binding. It persists `OriginalKept`, retains uncertain versions and recovery records, preserves completed selections and blocks dependent changes. It never labels that selection migrated or reverted. A saved session display state alone is insufficient: startup revalidates the authenticated adapter receipt.

Real Windows reboot, personal Shell mapping changes, native restart-required EXE/MSI outcomes and WinUI sign-in recovery remain separate acceptance gates. Prepare the isolated environment using [the VM setup](MIGRATION_VM_SETUP.md) and [guest acceptance stages](MIGRATION_GUEST_ACCEPTANCE.md). Tooling preparation or a passing ordinary desktop suite does not establish that those guest cases ran.

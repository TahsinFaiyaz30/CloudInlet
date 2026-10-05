# Direct cloud transfers

CloudBay is the middleman for OneDrive → Backblaze B2 and B2 → OneDrive. The transfer dialog also offers explicit local → cloud and cloud → local destinations. OneDrive accounts use delegated Microsoft Graph authentication and actual drive/folder identities; the OneDrive Windows sync client is not required.

## Product flows

- Open **Folder backup → Transfer between clouds**, or **Import files → Cloud storage → OneDrive ↔ Backblaze B2**.
- Connect OneDrive with a registered public-client application ID. Enable **Allow public client flows**; use the tenant ID/domain for a work account or the appropriate Microsoft common/consumer authority. Tokens and refresh-token rotations are stored with current-user Windows DPAPI.
- Browse the source account/folder and destination account/folder. Review Copy or Move, exclusions and Fail/Skip/Replace/Rename conflicts before starting.
- A cloud destination keeps its provider identity. Only an explicit **This PC** destination creates local files.
- Personal-folder enable can import directly from OneDrive into the selected B2 backup before changing the Windows folder mapping. Stop can transfer the former B2 backup to OneDrive after separately reviewing the Windows local mapping. Native sync relinquishes that cloud source durably before a Move.
- Activity shows discovery, verified files, queued work, transferred/remaining bytes, measured speeds and per-file states. Pause/Resume/Cancel preserve completed copies and recoverable checkpoints. Existing tray, aggregate Activity and notification systems display these jobs.

**Settings → Account → Disconnect** has three visible choices. **Disconnect only** is the default and keeps local files without hydration. **Download files, then disconnect** materializes cloud files. **Remove local cloud copies, then disconnect** removes only unchanged verified local copies and exact online-only placeholders; personal folders, changed/unsynced files and unknown files stay. All choices retain B2 content and versions. Disconnect intent is durable so a crash cannot turn intentional local cleanup into B2 deletion sync.

## Shared transfer primitives and memory

`ITransferEndpoint` and replayable `ITransferSourceFile` power the durable local/B2/OneDrive job pipeline. Windows native sync retains its placeholder registration, staging and installation workflows while using `NativeTransferAdapters` for prepared local upload, cloud download and verification. The original locked local FileStream is preserved so B2 reuses its prepared multipart hashes and upload resume journal.

Cloud → cloud adapters do not create payload files or disk caches. Large cloud reads use bounded 256 KiB read-ahead blocks; Graph uploads use at most a 5 MiB fragment per active file. B2 uploads stream independent ranges through bounded buffers with provider-verified SHA1 trailers. Small Graph files are bounded to 1 MiB. The engine bounds upload admission, read-ahead, the verification queue and shared verification work. HTTP clients, refreshed authorization and exclusive B2 upload URLs are reused across files.

Upload/download bandwidth budgets are shared across the application's native roots, relay transport and connected OneDrive accounts. Separate provider upload/download concurrency preferences remain effective. Increasing the worker limit changes running admission without discarding jobs. Requesting HTTP/2 permits fallback; the real B2 test negotiated HTTP/1.1.

Explicit local destinations may use resumable local partials. Replacing a local file first preserves that exact unchanged original through a Windows DELETE handle, then installs the partial without overwriting a late new target. Previous local files remain at the recorded `.CloudBay-transfer-<operation>.original` path; Activity reports that path. Owned `.part`/`.original` records are excluded from discovery and native sync. These files are never used for cloud destinations.

## Restart, integrity and Move

The metadata-only SQLite journal durably stores the plan, source identity/version, discovery cursor and folder queue, acknowledged bytes/checkpoints, receipts, verification/deletion intent and counters. Upload-session secrets are protected with DPAPI. Completed files and committed discovery pages are not retransferred or recounted after restart. Policy pauses survive restart separately from explicit user Pause/Cancel.

Graph expiration restarts only its unfinished file. Expired folder pagination revisits only the incomplete folder, with unchanged entries deduplicated by the journal. Changed versions, duplicate paths, damaged local ranges, unexpected provider acknowledgments and uncertain small-upload outcomes stop safely for review. B2 reconciles acknowledged multipart hashes against provider parts and operation metadata before replaying ranges. An unfinished B2 `start` listing is never treated as a completed receipt.

Small B2 provider SHA1 and independent receipt metadata establish completion. Multipart B2 whole-file hashes are client metadata, so verification streams the destination back. Graph SHA1 is used when available; otherwise independent destination streaming checks the upload digest. QuickXorHash is not a cryptographic integrity guarantee. Missing source digests can require one independent source hash pass for that unfinished file.

Move deletes after verification and final source validation. Graph deletes the saved immutable item ID with `If-Match`; B2 deletes only the exact unchanged version; Windows local Move uses identity/content checks and a writer/rename-denying handle. Changed sources, extra Windows data streams and blocked deletes remain for review. A lost delete acknowledgment is reconciled against the saved exact identity. Source folders remain.

Graph guarantees `If-Match` at upload-session creation. CloudBay revalidates the destination before its final fragment; Microsoft does not document an atomic final-fragment conditional commit against later edits. That provider race is not presented as a stronger guarantee. B2 name creation similarly has no atomic create-if-absent primitive; version identities and operation receipts prevent blindly replaying uncertain uploads.

## Provider evidence and remaining acceptance

Tests use isolated generated namespaces and existing restricted test credentials. No user payload files are read for fixture generation. Safe reports are in `artifacts/validation`; credentials stay in Windows DPAPI.

- Real B2 64 MiB relay: fresh-process interruption acknowledged 32 MiB; another process resumed at byte 33,554,432 using the same unfinished destination and streamed the completed content for verification. The acknowledged source range was not retransmitted. A dropped real successful small-upload response recovered without a duplicate payload upload.
- Latest complete durable-engine tiny benchmark: 24 × 1 KiB files, four workers, **10.036 seconds / 2.391 files per second**. It made 24 uploads, 24 source GETs, 48 immutable metadata checks, 25 name/discovery listings, one authorization and four reusable upload endpoints. Aggregate upload gaps totaled 398.7 ms; the longest was 250.0 ms. Separate runs varied with the network; these differences are not attributed solely to code changes.
- Observed request send-to-header means were 913.6 ms for uploads (including streaming the source), 282.2 ms for source GETs, 206.1 ms for immutable metadata and 284.9 ms for name listings. Concurrent request totals overlap and cannot be added as elapsed time. Provider/network round trips dominate these tiny-file transfers; the implementation does not claim constant line-rate speed.
- Cloud adapter source audit found no payload file-writing paths. Live relay state-directory audits and payload-marker/encrypted-session tests passed. These are application/source and scoped-directory checks; system-wide filesystem tracing and OS paging exclusion were not performed.
- yxrcz tenant public-client authentication and real OneDrive drive discovery succeeded. The actual OneDrive/B2 write acceptance stopped **before creating its first test folder** with **HTTP 507 `quotaLimitReached`**. No OneDrive test payload was uploaded. Resolve that drive's storage quota, then rerun the opt-in test in [the OneDrive transport instructions](../CloudBay.Core/OneDrive/README.md). Successful sign-in is not reported as successful transfer acceptance.

The next release is prepared through the central version process. The published **1.0.0** tag and assets remain unchanged. Live OneDrive write/recovery acceptance is an outstanding release gate; do not publish a verified cloud-transfer release while it remains blocked.

## Official API references

- [Microsoft device authorization](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Public-client registration error guidance](https://learn.microsoft.com/en-us/troubleshoot/entra/entra-id/app-integration/confidential-client-application-authentication-error-aadsts7000218)
- [Graph ranges and preauthenticated download URLs](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0)
- [Graph upload sessions and acknowledgments](https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0)
- [Graph hashes](https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0)
- [Graph conditional deletion](https://learn.microsoft.com/en-us/graph/api/driveitem-delete?view=graph-rest-1.0)
- [B2 upload checksum trailers](https://www.backblaze.com/apidocs/b2-upload-file)
- [B2 parts and resumable upload](https://www.backblaze.com/apidocs/b2-upload-part)

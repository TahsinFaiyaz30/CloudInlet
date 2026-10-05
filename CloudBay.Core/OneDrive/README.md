# Direct OneDrive transport

CloudBay uses delegated Microsoft Graph access to actual drive/folder identities. It does not depend on the OneDrive sync client or a locally synchronized folder. `OneDriveAuthClient` supports browser authorization with PKCE and device sign-in, requests `Files.ReadWrite`, `User.Read`, and `offline_access`, rotates refresh tokens through the host's DPAPI persistence callback, and shares refreshed authorization across file workers.

CloudBay supplies the registered Microsoft public-client application ID. Normal users choose **Connect OneDrive → Sign in with Microsoft**, select their personal or work account, and approve access. They do not register an Entra application or enter an application ID/tenant. The normal `common` authority supports both account types when enabled by the registration. The provided CloudBay registration is built in; its public application ID is configuration, not a credential.

**Advanced connection settings** allow a custom application or organization-specific authority; **Use yxrcz organization** selects the provided yxrcz tenant. Deployments can override the defaults with `CLOUDBAY_ONEDRIVE_CLIENT_ID` and `CLOUDBAY_ONEDRIVE_TENANT_ID`. The app owner maintains supported account types, public-client/device authentication and delegated Graph permissions. Browser PKCE also needs the registered HTTP loopback redirect. No client secret belongs in this desktop application. Options are captured before device authorization and held unchanged through token completion.

`OneDriveClient` reuses HTTP connection pools. Signed download/upload URLs receive no bearer token. Range reads validate saved item identity, eTag, and length before opening the primary stream; destination verification and the engine's final source checks reject changes after reading. Graph does not promise that a Graph item eTag is accepted as a conditional header by its signed CDN URL, so the transport does not rely on that unsupported behavior. HTTPS redirects preserve ranges without forwarding credentials. A response that ignores a partial range is rejected.

Real personal-account testing found that selected item metadata can omit `@microsoft.graph.downloadUrl` even when the annotation is explicitly selected. Fresh per-item transfer metadata therefore uses one full item request, which returns the signed URL without a second fallback round trip. Folder pages still select only the discovery fields.

`OneDriveTransferEndpoint` uses RAM-only payload buffers: small files up to 1 MiB use one PUT, while larger files use sequential 5 MiB fragments and one continuous source connection. The engine's bounded read-ahead overlaps downloading with fragment uploads. Sessions, exact destination intents, server acknowledgments, completed identities and content digests are persisted through the protected checkpoint callback. Session disappearance restarts only the unfinished file. A lost final response is reconciled by exact destination identity/path plus cryptographic content verification before a new mutation is allowed.

The upload computes a whole-file SHA-1 while reading. It persists that digest before the final request and returns it in the receipt, including when Graph does not supply SHA-1. A resumed unfinished file without a saved/source digest needs one streaming hash pass over that file. Provider SHA-1 is compared when available; otherwise the destination is independently streamed and hashed. Graph's `sha256Hash` property is unsupported, and QuickXorHash is not treated as a cryptographic checksum. The legacy SHA-256 fallback compares independent source/destination streams when no trusted upload/source SHA-1 exists. No transfer payload is written to local staging, temporary files, or disk cache by these adapters.

Discovery checkpoints contain the current folder, its next Graph page, and pending folder identities. Normal restart continues that page. If Graph expires pagination state, only the unfinished folder is revisited and the durable journal deduplicates identical entries; changed identities and path collisions need review. Repeated page expiry stops safely. Move uses the saved item ID and `If-Match` eTag to delete only the unchanged file after verification; source directories remain.

## Provider protocol verification

Run deterministic HTTP protocol tests:

```powershell
dotnet test CloudBay.Tests/CloudBay.Tests.csproj --filter FullyQualifiedName~OneDriveTransferTests
```

The tests exercise connection/auth reuse, PKCE state, persisted folder pages, signed range requests, source changes, acknowledged fragment restart, expired sessions, lost final responses, duplicate prevention, checksum failures, tiny-file request reduction, continuous large-file source reads, independent verification, and exact-source Move deletion.

## Opt-in real OneDrive and B2 acceptance

Connect the reviewed OneDrive account in CloudBay first. Put a B2 application key restricted to exactly one test bucket into the existing Windows DPAPI validation store at `%LOCALAPPDATA%\CloudBay\Validation\credentials.dpapi`, using the existing CloudBay validation credential workflow. The test refuses unrestricted B2 keys. No access/refresh tokens or application-key values belong in command-line arguments or reports.

Set only non-secret selection/configuration values:

```powershell
$env:CLOUDBAY_RUN_ONEDRIVE_ACCEPTANCE = '1'
$env:CLOUDBAY_ACCEPTANCE_ONEDRIVE_DRIVE_ID = '<reviewed Graph drive ID>'
$env:CLOUDBAY_ACCEPTANCE_ONEDRIVE_FOLDER_ID = '<reviewed parent folder ID>'
$env:CLOUDBAY_ACCEPTANCE_CLIENT_DATA = '<directory containing CloudBay account DPAPI storage>'
# Only needed when several OneDrive accounts are saved:
$env:CLOUDBAY_ACCEPTANCE_ONEDRIVE_ACCOUNT_ID = '<saved CloudBay account ID>'
dotnet test CloudBay.Tests/CloudBay.Tests.csproj --filter FullyQualifiedName~OneDriveProviderAcceptanceTests --logger 'console;verbosity=normal'
```

The parent folder selection is optional and defaults to the reviewed drive root. The test creates unique cloud namespaces containing 64 tiny files, four medium files and one 12 MiB file. It runs both transfer directions, reloads completed durable progress without provider calls, interrupts a large upload after a real 5 MiB acknowledgment, restores an encrypted checkpoint, drops its final HTTPS response after service completion, and verifies recovery without another destination. It also revokes a provider upload session to exercise disappearance/restart, tests destination conflicts and checksum rejection, and verifies a changed source survives Move deletion.

The generated metadata-only `report.json` records sustained verified throughput, small-file payload requests per second, and aggregate gaps between overlapping payload requests. These measurements include provider RTT, throttling, discovery, and cryptographic readback overhead; they are not claims of constant line-rate throughput. A unique payload marker and signed-session URLs are checked against all local job/checkpoint files. This storage audit is scoped to CloudBay's acceptance metadata directory, and is not a system-wide OS paging or filesystem trace.

After success, only exact file identities from the unique acceptance namespaces are deleted. Empty OneDrive test folders remain as reviewable run markers. On failure, test content and progress remain for diagnosis. Without explicit opt-in and configured accounts, this test is inconclusive/skipped and makes no provider calls; that result must not be reported as real-provider verification.

## Official API references reviewed

- [Microsoft identity authorization code and PKCE](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
- [Microsoft identity device authorization](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Graph folder children and paging](https://learn.microsoft.com/en-us/graph/api/driveitem-list-children?view=graph-rest-1.0)
- [Graph download URLs and partial ranges](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0)
- [Graph upload sessions, aligned fragments, acknowledgments and expiration](https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0)
- [Graph single-request uploads](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0)
- [Graph driveItem conflict behavior and eTags](https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0)
- [Graph hashes and unavailable SHA-256 metadata](https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0)
- [Graph exact-item conditional deletion](https://learn.microsoft.com/en-us/graph/api/driveitem-delete?view=graph-rest-1.0)

# Live Windows and B2 acceptance validation

Run these commands from a CloudInlet source checkout. The application release includes this guide for reference; the developer validation console is built from source and is separate from the installed app.

This console uses a DPAPI-encrypted application key at `%LOCALAPPDATA%\CloudBay\Validation\credentials.dpapi`, protected for the current Windows user with the same `CloudBay.B2.v1` entropy as the app. Credentials are never placed in the repository, printed, or included in a report.

```powershell
dotnet run --project tools/CloudInlet.Validation/CloudInlet.Validation.csproj -c Release -- --list
dotnet run --project tools/CloudInlet.Validation/CloudInlet.Validation.csproj -c Release -- --b2 --native
dotnet run --project tools/CloudInlet.Validation/CloudInlet.Validation.csproj -c Release -- --b2 --controller
dotnet run --project tools/CloudInlet.Validation/CloudInlet.Validation.csproj -c Release -- --b2 --transfers
dotnet run --project tools/CloudInlet.Validation/CloudInlet.Validation.csproj -c Release -- --b2 --cloud-relay
```

`--list` only checks authorization and prints accessible bucket names. Live writes require a key restricted to exactly one private or empty bucket. Every run uses an isolated `CloudInletValidation/<GUID>/` cloud prefix, appending it to any key prefix restriction. It checks reusable small-file sessions, concurrent session exclusivity, pagination of both current names and historical versions, version history, restore/hide behavior, a streamed 205 MiB multipart upload and verified download, precise byte ranges, and canceled multipart cleanup. It hides the run's objects afterward while retaining their B2 versions.

`--native` adds a unique `CloudInletValidation-<GUID>` folder directly in the user profile. It verifies native Cloud Files/Explorer registration, checksum-validated on-demand B2 hydration, and SyncEngine upload/edit/delete behavior with a durable SQLite manifest and activity history. The console disconnects and unregisters only that run's root and removes only its own local test folder. The separate Windows test suite covers retained-prefix resume, assembled-cache corruption recovery, bounded repeated failure, and dirty-edit protection.

Add `--quick` to rerun the smaller B2 and native checks without repeating multipart transfers. Reports explicitly identify this reduced scope.

`--transfers` runs a separate restart acceptance flow against a bucket-restricted private bucket. It creates an artificial 205 MiB file, interrupts an upload after one confirmed part, and starts a fresh process that must reuse the same unfinished B2 version without resending that part. Two more fresh processes interrupt and resume a staged download, skip the saved 8 MiB chunk, download the remaining ranges within a four-request shared budget, and independently verify the completed disk checksum. This mode does not repeat the ordinary small-file/multipart sequence. No user files are read, and no credentials are passed on a command line or written into worker plans.

`--controller` tests the product's account and multi-root lifecycle with separate temporary settings and native roots. It includes saved DPAPI credentials, preserved B2 versions, timestamp-preserving edits, pin/free/hydration, and a held native-validation worker that checks whether user I/O and completed history wait for cache validation while the live row remains Verifying with no old transfer rate. It also covers concurrent preference changes that preserve registered roots and global pause, pause inherited by a new custom backup, restart, custom removal, full disconnect, and immediate retry after an injected Windows unregister failure. It restores the exact prior startup registry value and never redirects real Windows personal folders.

Safe acceptance reports go to `artifacts/validation/b2-<GUID>.json`. They contain test results, timings, API operation counters, token-exclusivity violation counts, and the process peak working set. Operation counters never retain request URLs or headers. The console uses temporary disk files for multipart validation and deletes them on completion. Multipart validation transfers approximately 410 MiB plus the beginning of one canceled upload.

Restart reports also include `transfer-processes-<GUID>.json` and one safe report per worker. They record process IDs, confirmed part numbers, range offsets and peak active downloads. Cleanup is confined to the generated GUID cloud prefix and local validation directory.

`--cloud-relay` requires the restricted private test bucket and exercises the shared RAM-only adapter against live B2. It generates a 64 MiB source directly in RAM streams, uploads with provider-verified SHA1 trailers, then relays actual immutable B2 range downloads through the middleman into a second B2 object. Separate worker processes interrupt after one acknowledged part and resume only the remaining range; the destination is streamed back for content verification. A lost successful small-upload acknowledgment is injected on the actual provider response and must reconcile its operation receipt without another payload upload. A 24-file, four-worker tiny-file benchmark reports elapsed files/second, payload throughput, reused authorization/upload endpoints, and aggregate upload idle gaps. The generated state directory is checked for only small JSON records. This checks the implementation and its scoped directory; it does not claim OS-level tracing of every disk write. Reports go to `cloud-relay-<GUID>.json`, and ordinary cleanup hides only generated test objects. The fixture and relay use about 256 MiB of payload traffic; no payload file is generated on disk.

Use `--b2 --cloud-relay-tiny` to repeat only the RAM seed, tiny-file benchmark, storage audit and scoped cleanup. The report records the actual negotiated HTTP versions; requesting HTTP/2 does not guarantee that a provider supports it.

Add `--cloud-relay-cold-source` for a controlled tiny benchmark that requires a fresh metadata GET for every source instead of reusing exact immutable version evidence from the source discovery response. It preserves destination verification, range identity checks and every other protocol step; compare API counts and elapsed timings without attributing network variation to the change.

For isolated OneDrive sign-in, run `--onedrive-signin --client-id <public-client-ID> --tenant-id <tenant-ID-or-domain>`. Enable **Allow public client flows** in the Microsoft app registration. The helper prints a short-lived Microsoft device code, saves tokens only in Windows DPAPI at `%LOCALAPPDATA%\CloudBay\Validation\OneDrive`, and returns safe account/drive selections for the opt-in acceptance test. Run the helper as a hidden background process when sign-in must survive chat or terminal interruptions. No client secret is needed.

To repair icons on personal folders already backed up by this installation, run:

```powershell
dotnet run --project tools/CloudInlet.Validation/CloudInlet.Validation.csproj -c Release -- --repair-folder-icons
```

This separate maintenance command validates saved ownership against the current Windows folder mapping before changing only folder appearance metadata. It preserves existing custom icons, uses the original source customization when available, and otherwise restores the corresponding Windows icon. It does not restart the provider, redirect folders, or contact B2. Sharing or permission failures are reported per folder so active work can continue. The safe report is `artifacts/validation/owned-folder-icon-repair.json`.

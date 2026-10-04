# Live Windows and B2 acceptance validation

Run these commands from a CloudBay source checkout. The application release includes this guide for reference; the developer validation console is built from source and is separate from the installed app.

This console uses a DPAPI-encrypted application key at `%LOCALAPPDATA%\CloudBay\Validation\credentials.dpapi`, protected for the current Windows user with the same `CloudBay.B2.v1` entropy as the app. Credentials are never placed in the repository, printed, or included in a report.

```powershell
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --list
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --b2 --native
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --b2 --controller
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --b2 --transfers
```

`--list` only checks authorization and prints accessible bucket names. Live writes require a key restricted to exactly one private or empty bucket. Every run uses an isolated `CloudBayValidation/<GUID>/` cloud prefix, appending it to any key prefix restriction. It checks reusable small-file sessions, concurrent session exclusivity, pagination of both current names and historical versions, version history, restore/hide behavior, a streamed 205 MiB multipart upload and verified download, precise byte ranges, and canceled multipart cleanup. It hides the run's objects afterward while retaining their B2 versions.

`--native` adds a unique `CloudBayValidation-<GUID>` folder directly in the user profile. It verifies native Cloud Files/Explorer registration, checksum-validated on-demand B2 hydration, and SyncEngine upload/edit/delete behavior with a durable SQLite manifest and activity history. The console disconnects and unregisters only that run's root and removes only its own local test folder. The separate Windows test suite covers retained-prefix resume, assembled-cache corruption recovery, bounded repeated failure, and dirty-edit protection.

Add `--quick` to rerun the smaller B2 and native checks without repeating multipart transfers. Reports explicitly identify this reduced scope.

`--transfers` runs a separate restart acceptance flow against a bucket-restricted private bucket. It creates an artificial 205 MiB file, interrupts an upload after one confirmed part, and starts a fresh process that must reuse the same unfinished B2 version without resending that part. Two more fresh processes interrupt and resume a staged download, skip the saved 8 MiB chunk, download the remaining ranges within a four-request shared budget, and independently verify the completed disk checksum. This mode does not repeat the ordinary small-file/multipart sequence. No user files are read, and no credentials are passed on a command line or written into worker plans.

`--controller` tests the product's account and multi-root lifecycle with separate temporary settings and native roots. It includes saved DPAPI credentials, preserved B2 versions, timestamp-preserving edits, pin/free/hydration, and a held native-validation worker that checks whether user I/O and completed history wait for cache validation while the live row remains Verifying with no old transfer rate. It also covers concurrent preference changes that preserve registered roots and global pause, pause inherited by a new custom backup, restart, custom removal, full disconnect, and immediate retry after an injected Windows unregister failure. It restores the exact prior startup registry value and never redirects real Windows personal folders.

Safe acceptance reports go to `artifacts/validation/b2-<GUID>.json`. They contain test results, timings, API operation counters, token-exclusivity violation counts, and the process peak working set. Operation counters never retain request URLs or headers. The console uses temporary disk files for multipart validation and deletes them on completion. Multipart validation transfers approximately 410 MiB plus the beginning of one canceled upload.

Restart reports also include `transfer-processes-<GUID>.json` and one safe report per worker. They record process IDs, confirmed part numbers, range offsets and peak active downloads. Cleanup is confined to the generated GUID cloud prefix and local validation directory.

To repair icons on personal folders already backed up by this installation, run:

```powershell
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --repair-folder-icons
```

This separate maintenance command validates saved ownership against the current Windows folder mapping before changing only folder appearance metadata. It preserves existing custom icons, uses the original source customization when available, and otherwise restores the corresponding Windows icon. It does not restart the provider, redirect folders, or contact B2. Sharing or permission failures are reported per folder so active work can continue. The safe report is `artifacts/validation/owned-folder-icon-repair.json`.

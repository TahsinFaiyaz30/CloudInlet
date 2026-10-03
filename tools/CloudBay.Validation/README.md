# Live Windows and B2 acceptance validation

This console uses a DPAPI-encrypted application key at `%LOCALAPPDATA%\CloudBay\Validation\credentials.dpapi`, protected for the current Windows user with the same `CloudBay.B2.v1` entropy as the app. Credentials are never placed in the repository, printed, or included in a report.

```powershell
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --list
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --b2 --native
dotnet run --project tools/CloudBay.Validation/CloudBay.Validation.csproj -c Release -- --b2 --controller
```

`--list` only checks authorization and prints accessible bucket names. Live writes require a key restricted to exactly one private or empty bucket. Every run uses an isolated `CloudBayValidation/<GUID>/` cloud prefix, appending it to any key prefix restriction. It checks reusable small-file sessions, concurrent session exclusivity, paginated listing, version history, restore/hide behavior, a streamed 205 MiB multipart upload and verified download, precise byte ranges, and canceled multipart cleanup. It hides the run's objects afterward while retaining their B2 versions.

`--native` adds a unique `CloudBayValidation-<GUID>` folder directly in the user profile. It verifies native Cloud Files/Explorer registration, on-demand B2 hydration, and SyncEngine upload/edit/delete behavior with a durable SQLite manifest and activity history. The console disconnects and unregisters only that run's root and removes only its own local test folder.

Add `--quick` to rerun the smaller B2 and native checks without repeating multipart transfers. Reports explicitly identify this reduced scope.

`--controller` tests the finished product's account and multi-root lifecycle with separate temporary settings and native roots. It includes saved DPAPI credentials, preserved B2 versions, timestamp-preserving edits, pin/free/hydration, pause inherited by a new custom backup, restart, custom removal, full disconnect, and immediate retry after an injected Windows unregister failure. It restores the exact prior startup registry value and never redirects real Windows personal folders.

Safe acceptance reports go to `artifacts/validation/b2-<GUID>.json`. They contain test results, timings, API operation counters, token-exclusivity violation counts, and the process peak working set. Operation counters never retain request URLs or headers. The console uses temporary disk files for multipart validation and deletes them on completion. Multipart validation transfers approximately 410 MiB plus the beginning of one canceled upload.

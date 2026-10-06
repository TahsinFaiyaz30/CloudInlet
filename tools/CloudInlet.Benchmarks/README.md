# Adapter throughput measurements

This standalone harness measures the shared transfer engine and real local, OneDrive and B2 adapters. It does not configure production accounts or change transfer preferences. Run provider experiments sequentially; simultaneous jobs make before/after measurements difficult to interpret.

```powershell
dotnet build tools/CloudInlet.Benchmarks/CloudInlet.Benchmarks.csproj -c Release
tools/CloudInlet.Benchmarks/bin/Release/net8.0-windows/CloudInlet.Benchmarks.exe --mode self-test
tools/CloudInlet.Benchmarks/bin/Release/net8.0-windows/CloudInlet.Benchmarks.exe --mode scheduler --workload tiny --workers 4 --label current
```

Scheduler mode uses generated bounded streams and deterministic preparation, payload and verification delays. It isolates changes in overlap/admission; it does not represent provider throughput. Workloads are `tiny` (24 Ã— 4KiB), `large` (12MiB +123B), and `mixed` (16 Ã— 4KiB, two Ã—256KiB and 8MiB +123B). Override the tiny-file count with `--tiny-files 12` (1â€“64). Untimed cloud seeding and exact verified cleanup use the configured worker limit, with encrypted seed checkpoints retained on failures.

Live mode requires the existing Windows DPAPI validation B2 credentials restricted to one test bucket and, for OneDrive, an explicitly reviewed account and primary drive from the validation account store:

```powershell
tools/CloudInlet.Benchmarks/bin/Release/net8.0-windows/CloudInlet.Benchmarks.exe --mode live --direction local-b2 --workload tiny --workers 4 --label current
tools/CloudInlet.Benchmarks/bin/Release/net8.0-windows/CloudInlet.Benchmarks.exe --mode live --direction b2-onedrive --workload mixed --workers 4 --label current --onedrive-account ACCOUNT_ID --onedrive-drive DRIVE_ID
```

Supported directions are `local-b2`, `b2-local`, `local-onedrive`, `onedrive-local`, `onedrive-b2`, and `b2-onedrive`. Each run uses new GUID-owned cloud folders/prefixes. Seeds are created before timing. Cloud-to-cloud seeds and transfer contents remain in bounded RAM. Local directions intentionally create local fixture or destination files. Completed exact source/destination identities are independently verified before cleanup; failures retain fixtures and recovery metadata for review. Empty OneDrive run folders remain as markers.

The two provider clients share one `TransferBandwidthBudget`, matching the app. `--workers` controls engine admission; `--payload-workers` separately controls each direction's payload limit and defaults to the engine count. Use `--workers 3 --payload-workers 1` to exercise relays with one upload and one download slot. The report captures both settings and the exact loaded Core hash.

Reports under `artifacts/validation/throughput-LABEL-GUID/` contain the loaded Core hash/version, monotonic per-file boundary timestamps, actual range-open counts, source reads/validations, request RTTs, upload receipts, verification waits and completion times. Body-lifetime and stream-call intervals are unioned separately. Body lifetimes include source/consumer backpressure and are not a measurement of the provider's line utilization; request-duration totals overlap when requests run concurrently. Local filesystem writes are inferred from source ranges and upload progress. No raw request URLs, authorization headers or checkpoint secrets are logged.

`ClaimToUploadMs` includes admission, destination reconciliation and source preparation after a worker claims a file. `ReceiptToVerifyMs` measures the delay between the last acknowledged upload receipt and the start of verification. Time outside the union of payload body lifetimes can include provider metadata requests, connection setup, source preparation, verification or application scheduling; it cannot be labelled avoidable idle time without examining the accompanying events. Empty OneDrive reservation requests do not count as payload bodies. Stream-call intervals measure time inside read/write calls, including waits, rather than packet activity.

An incomplete or uncertain provider result is a failed benchmark, even when a remote name exists. The harness retains that job's checkpoint and owned fixtures instead of replaying an upload with an unproven result or cleaning it by name. Small metadata checks for fixture byte patterns establish only that recorded recovery files contain no generated payload pattern; they are not an operating-system disk trace.

For reproducible baseline/current comparisons, copy the complete built harness output to separate artifact directories and replace only the isolated harness's `CloudInlet.Core.dll` with the desired captured build. The report records that actual DLL's SHA256. Keep identical workloads/workers, run sequentially, repeat to expose variance, and distinguish provider RTT/throttling/readback from application scheduling gaps. Never replace DLLs under a running production app or provider test.

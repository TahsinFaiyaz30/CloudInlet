# Disposable Windows migration lab

The migration acceptance that changes Windows personal-folder mappings or crosses a real reboot must run in a dedicated Windows 11 x64 guest with a disposable local account. `scripts/setup-migration-lab.ps1` prepares that environment. It does not install Windows, run acceptance, start a VM or restart the host.

The default **Plan** operation only prints a plan. `-WritePlan` saves a new plan below ignored `artifacts/migration-vm-plans/`. **Create** is a separate explicit operation requiring that exact plan path and its reviewed SHA-256. It creates only a fresh VM and leaves it powered off. The script never changes existing VMs, virtual switches, Windows features or group membership, and never downloads an OS or supplies credentials.

## Prepare the plan

Use PowerShell 7 from this repository. Without an ISO, this command produces a reviewable incomplete plan and does not require Hyper-V permissions:

```powershell
pwsh -NoProfile -File scripts/setup-migration-lab.ps1
```

Supply a genuine Windows 11 x64 installation ISO obtained from Microsoft or another authorized source, with a license suitable for this use. The setup does not establish the ISO's provenance or licensing. If its edition requires an internet connection or account during installation, satisfy those requirements through the normal supported setup process; the lab does not bypass them or silently connect a network. Any network connection is a separate deliberate action and must be removed before migration acceptance.

Microsoft's [Windows 11 download page](https://www.microsoft.com/en-us/software-download/windows11) provides the **Download Windows 11 Disk Image (ISO) for x64 devices** option and a **Verify your download** section. Select the image and language, then compare `Get-FileHash -Algorithm SHA256 <downloaded ISO>` with Microsoft's corresponding published value before preparing the plan. The plan's saved hash binds the selected file; it does not replace this provenance check.

Create a saved plan using the actual ISO path:

```powershell
$review = ./scripts/setup-migration-lab.ps1 -IsoPath 'D:\Images\Windows11-x64.iso' -WritePlan
Get-Content -LiteralPath $review.planPath
$review | Format-List
```

The plan uses a new GUID for both its name and directory. Its fixed configuration is:

| Setting | Value |
|---|---|
| Guest | Generation 2, Windows Secure Boot template, local virtual TPM |
| Resources | 2 processors, 4 GiB fixed memory, 80 GiB dynamic VHDX |
| Location | `artifacts/migration-vm/<guid>/` under this checkout |
| Network | Disconnected; no new virtual switch |
| Startup | Powered off; automatic startup disabled |
| Checkpoints | Disabled by setup; establish a clean baseline deliberately after Windows setup |

Creation requires a local fixed NTFS drive with at least 88 GiB free. It records current free physical memory and warns when startup is not ready, while still leaving the new VM powered off. The guest keeps its fixed 4 GiB memory configuration. Starting it later requires a fresh check for at least **6 GiB free physical memory**: 4 GiB for the guest and the existing 2 GiB host reserve. A successful creation or an earlier memory observation is not startup permission. The ISO, workspace and lab paths must be ordinary local paths without junctions, symlinks, alternate streams or UNC traversal. An occupied lab path or VM name is an error. The saved plan also binds the current setup script hashes; changing the scripts requires a new reviewed plan.

## Create the dedicated VM

Open an **administrator PowerShell 7 terminal** in this checkout. Use the `planPath` and `planSha256` printed above, with their actual values:

```powershell
./scripts/setup-migration-lab.ps1 -Mode Create `
    -PlanPath '<printed absolute planPath>' `
    -PlanSha256 '<printed 64-character planSha256>'
```

No automatic elevation is attempted. Hyper-V management must already be installed and accessible. The script checks enumeration, storage, plan contents and the exact ISO hash before creating the VM, and records timestamped memory readiness. It flushes creation intent and subsequent outcomes to `setup-evidence.jsonl` in the lab directory. The result identifies the new VM by `vmId`, its distinct `firmwareUuid` when Hyper-V exposes it, and the fixture ID. Save those values for guest enrollment.

A failure retains the new VM and files for diagnosis; it does not guess which partial resources to remove. Do not rerun Create with the same plan or rename an existing VM to satisfy it. Review the exact failure and VM ID first. There is deliberately no automatic recursive cleanup command.

## Install Windows and enroll the test account

Open only the VM named in the successful setup result in Hyper-V Manager. Recheck the attached ISO's hash before starting it if the ISO has changed or moved since creation. Immediately before every deliberate start, check current host memory:

```powershell
$available = [long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB
if ($available -lt 6GB) { throw 'Keep the VM off: at least 6 GiB free physical memory is required before startup.' }
```

This command is read-only and does not start anything. If it fails, close unneeded applications and recheck; do not lower the guest memory or the startup threshold. Microsoft documents [VM creation](https://learn.microsoft.com/en-us/powershell/module/hyper-v/new-vm) and [VM startup](https://learn.microsoft.com/en-us/powershell/module/hyper-v/start-vm) as separate operations; this setup performs only creation and verifies that the VM remains off.

Install Windows interactively, then sign in to a new disposable **local** account used only for migration acceptance. Do not connect that account to OneDrive, restore an existing profile, share host personal folders, import host app state, or copy host DPAPI or B2 credentials.

After installation, shut down the guest and establish a clean baseline deliberately before running tests. A checkpoint restore is useful for a fresh fixture but never counts as a real reboot. Keep the guest network disconnected during the local installer and Windows-folder cases. Transfer only the reviewed source/harness, verified Debug packages and a SHA-256 inventory into a guest-only directory; do not expose the entire host checkout as a writable share.

`scripts/initialize-migration-guest.ps1` enrolls the current disposable guest account using the host-recorded fixture ID, VM ID and firmware UUID. It must verify the guest's actual firmware UUID and bind its computer name, account SID and fixed lab path into a DPAPI-protected identity. VM ID and firmware UUID are different identifiers; never substitute one for the other. If setup reports no firmware UUID, obtain and verify it for this exact VM before enrollment. A manually edited JSON identity file is not acceptance authority.

Inside the guest, sign in to the disposable local account named `CloudInletLab` (or another `CloudInletLab`-prefixed account), then run the copied enrollment script with the actual values from the host setup result:

```powershell
./scripts/initialize-migration-guest.ps1 `
    -FixtureId '<host fixtureId>' -VmId '<host vmId>' `
    -ExpectedFirmwareUuid '<host firmwareUuid>' `
    -ExpectedAccountName 'CloudInletLab' -ConfirmDisposableGuest
```

The guest-only folder runner takes the complete verified test build, including its dependencies:

```powershell
./scripts/test-migration-guest-folders.ps1 -Stage Prepare `
    -TestAssembly '<guest build directory>\CloudInlet.Tests.dll' -ConfirmDisposableGuest
```

Run `VerifyBeforeRestart` on that same fixture, then restart Windows gracefully **inside this dedicated guest** and sign in to the same test account before `VerifyAfterRestart`. The runner does not issue a reboot command. Use its separate `Revert` and `RestoreMappings` stages as documented by the runner, preserving evidence. These stages exercise actual Shell-folder mapping and a real guest boot boundary; they do not exercise the WinUI installer observer or prove EXE/MSI native return code 3010 recovery. Those installer cases remain separate required acceptance.

The guest-side acceptance harness must verify that protected identity on every stage, generate its own fixtures and receipts inside the guest, and use the production boot-identity reader. Follow the separate case requirements in [migration acceptance](MIGRATION_ACCEPTANCE.md) and the current ignored `artifacts/migration-review/REBOOT-LAB-PLAN-2026-10-17.md`. Keep each outcome separate: native restart-required installation, seeded restart-required receipt, actual guest reboot, process restart and actual Shell-folder mapping are different evidence.

## Validation and remaining boundary

Run the isolated plan and path tests without administrative rights:

```powershell
pwsh -NoProfile -File scripts/test-migration-lab-setup.ps1
```

These tests validate fresh identity/path binding, plan digests, stale scripts, pre-existing lab preservation, junction rejection, read-only default behavior, the unchanged 6 GiB startup threshold and the requirement to recheck it. They also check that the setup contains no VM start/resume operation. They use a clearly named nonbootable file as an ISO-path fixture and never call Hyper-V or create a VM. Results are saved beneath ignored `artifacts/migration-lab-script-tests/`.

Passing these checks means the setup safeguards were tested. It does not mean that a guest was created, Windows was installed, the migration rebooted successfully, or personal-folder migration passed. Those claims require the actual dedicated guest and retained runtime evidence. No real guest is ready until a Windows ISO is supplied, an administrator runs Create, and Windows/test-account setup is completed.

Microsoft references: [creating a Hyper-V VM](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/create-a-virtual-machine-in-hyper-v), [Windows 11 on Hyper-V](https://techcommunity.microsoft.com/blog/itopstalkblog/how-to-run-a-windows-11-vm-on-hyper-v/3713948), and [local VM key protection](https://learn.microsoft.com/en-us/powershell/module/hyper-v/set-vmkeyprotector).

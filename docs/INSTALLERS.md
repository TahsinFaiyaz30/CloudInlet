# Windows installers and updates

CloudInlet ships per-user x64 installers for Windows 11. Each version has separate
Release and Debug EXE, MSI, and portable ZIP assets. `version.json` is the version
source; installers accept stable `major.minor.patch` versions within MSI limits
(major/minor up to 255, patch up to 65535).

The EXE installer uses pinned Inno Setup 6.7.3. The MSI uses WiX 5.0.2 and native
x64 DTF custom actions with a .NET Framework 4.7.2 helper. The build downloads
the Inno compiler from its official release, verifies SHA-256 and its publisher
signature, and checks the documented compiler version constant. WiX and its
extensions are pinned. No installer downloads the application at installation time.

| Distribution | Default application directory | Startup value | Client settings |
| --- | --- | --- | --- |
| Release | `%LOCALAPPDATA%\Programs\CloudInlet` | `CloudInlet` | `%LOCALAPPDATA%\CloudBay\Client` |
| Debug | `%LOCALAPPDATA%\Programs\CloudInlet Debug` | `CloudInletDebug` | `%LOCALAPPDATA%\CloudBay\Debug\Client` |

Debug has its own activation pipe, backup root, settings and credentials. EXE
and MSI are alternative installers for a flavor. An installer refuses to replace
an installation owned by the other kind; uninstall the old kind before switching.
Uninstalling removes installed application files and shortcuts, while preserving
client settings, credentials, activity, backup data and Windows folder mappings.
Use the app to stop backup or disconnect an account before removing it if desired.

## Build

Publish the full self-contained application and copy third-party notices into its
payload. Then run:

```powershell
./scripts/build-installer-helper.ps1 -AppFolder ./artifacts/release-build/Release/App
# Sign application binaries, including CloudInlet.SetupHelper.exe, here if configured.
./scripts/build-installers.ps1 `
  -AppFolder ./artifacts/release-build/Release/App `
  -Version 1.0.0 -Configuration Release `
  -OutputDirectory ./artifacts/release `
  -HelperPrepared
```

Without `-HelperPrepared`, `build-installers.ps1` builds and bundles the helper
before packaging. `build-release-artifacts.ps1` handles the complete build, test,
publish, helper, signing and packaging sequence. An unsigned local build is
supported. Public signing is optional configuration; unsigned installers can
produce Windows reputation prompts.

Each installed app contains `distribution.json` with version, build flavor,
installer kind, architecture, scope, actual installation directory and current
startup/desktop shortcut choices. The installer writes the descriptor after
installation. Automatic updates select a package matching that identity.

## Silent updates

The updater copies `CloudInlet.SetupHelper.exe` into its private update cache before
starting it. The helper has no cloud credentials or network access. It validates
the installed descriptor, registration, requesting process identity, update-cache
ownership, package name, SHA-256 and size. It acknowledges readiness before the
app gracefully exits, then waits for that exact process to exit. An active client
is never forcibly terminated. If shutdown takes too long, installation stops.

The EXE update uses `/UPDATE /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` with
the current installation directory. MSI uses `/qn /norestart UPDATE=1`. Both require
an existing installation of the same kind and flavor and read current startup
and desktop shortcut choices at update time. They do not reset those choices to
the defaults stored by the first installer. Client data stays outside the application
directory and is not an installer component.

Installer licenses, exact upstream source locations and the custom-action/helper
sources used by the payload are retained in `Licenses/Installers`.

The helper keeps the verified package locked against changes while the installer
runs, saves a result in the owned cache, and reopens the installed application
after success or after an installer failure. It never interrupts Windows Installer
rollback. Result code 3010 records Windows' restart request without forcing a reboot.
Temporary helper/request files are maintained separately from the update package.

Portable distributions need manual installation. Microsoft Store distributions
use Store updates, rather than GitHub installers; see `RELEASING.md` for Store
identity, signing and submission configuration.

## Installer safety

Installation rejects network paths, linked directory ancestors, the Windows
directory and the private CloudInlet settings directory. Silent EXE/MSI update
commands cannot be used as fresh installation commands. The installer asks the
matching current-user activation pipe to quit, validates its process image, and
waits for exit. It does not use a command interpreter, PowerShell custom actions,
process-name termination or elevation. Installer tests should use isolated Debug
fixture payloads and must not overwrite a real Debug or Release installation.

Run `./scripts/test-installers.ps1` for real, isolated Debug fixture acceptance.
It verifies EXE/MSI upgrades through the external updater, checksum rejection after
graceful shutdown, current startup and desktop choices, custom installation paths,
matching kind, update-only guards and uninstall. It compares Release configuration,
credentials and Windows folder mappings before and after and preserves client data.

The script creates a hidden same-user Windows process through WMI and checks its SID
before touching fixtures. This gives the EXE installer and Windows Installer service
the same physical registry view; some development hosts have a caller-specific
registry overlay that would otherwise invalidate that comparison. An existing Debug
installation, shortcut, startup value or update cache causes an immediate refusal.
Evidence is saved beneath `artifacts/validation`; Windows CI gates releases on this
acceptance and retains its result and installer logs without private client data.

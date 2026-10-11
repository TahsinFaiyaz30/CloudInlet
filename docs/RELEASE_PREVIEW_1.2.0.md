A refreshed interface, compact transfer views, and safer imports and recovery.

## Downloads

**Windows x64** · EXE recommended

[![Download EXE](https://img.shields.io/badge/Download-EXE-0078D4?style=for-the-badge)][release-exe] [![Download MSI](https://img.shields.io/badge/Download-MSI-0078D4?style=for-the-badge)][release-msi] [![Download ZIP](https://img.shields.io/badge/Download-ZIP-0078D4?style=for-the-badge)][release-zip] [![Download MSIX](https://img.shields.io/badge/Download-MSIX-666666?style=for-the-badge)][store-msix]

### Release (x64)

Recommended for everyday use.

<table>
<thead><tr><th align="left">File</th><th align="center">Type</th><th align="left">SHA-256</th></tr></thead>
<tbody>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-setup.exe">CloudInlet-1.2.0-win-x64-release-setup.exe</a></td><td align="center">EXE</td><td>

```
dd3445d68737f02241881df2c212abf6416425ed14fe97a407907371a60852fe
```

</td></tr>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-setup.msi">CloudInlet-1.2.0-win-x64-release-setup.msi</a></td><td align="center">MSI</td><td>

```
10dd5a88b0b9c4d674268f57819b37b54e899c381242434c57eb5735b722514b
```

</td></tr>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-portable.zip">CloudInlet-1.2.0-win-x64-release-portable.zip</a></td><td align="center">ZIP</td><td>

```
3d82fce0a33ca3510affcf6f02367de5e6769a64ca0310e8d3718ac00d393f5c
```

</td></tr>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-store.msix">CloudInlet-1.2.0-win-x64-release-store.msix</a></td><td align="center">MSIX</td><td>

```
963e29bbc66755cea5979d490af8409195e8d3b126e721ad180747ed10316654
```

</td></tr>
</tbody>
</table>

*Store package: unsigned; Certification pending.*

### Debug (x64)

Diagnostic builds with separate app state.

<table>
<thead><tr><th align="left">File</th><th align="center">Type</th><th align="left">SHA-256</th></tr></thead>
<tbody>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-debug-setup.exe">CloudInlet-1.2.0-win-x64-debug-setup.exe</a></td><td align="center">EXE</td><td>

```
f90e59a63350d746637a0445a26e284d59ccd488636ef8c8486343e61bddc55e
```

</td></tr>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-debug-setup.msi">CloudInlet-1.2.0-win-x64-debug-setup.msi</a></td><td align="center">MSI</td><td>

```
1b68bb778c91bf09e2256f500c1e91db52fc80db1b15fc23700bfa14636157bc
```

</td></tr>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-debug-portable.zip">CloudInlet-1.2.0-win-x64-debug-portable.zip</a></td><td align="center">ZIP</td><td>

```
792d563db50076209befb619c72dca88b666ee087c87e4c99c52b4ac0c96c261
```

</td></tr>
</tbody>
</table>

### Metadata

<table>
<thead><tr><th align="left">File</th><th align="center">Type</th><th align="left">SHA-256</th></tr></thead>
<tbody>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/updates-v2.json">updates-v2.json</a></td><td align="center">JSON</td><td>

```
a17eb3cf5339b7a844cffac8530af6b40bf412cdad4758eab245b01acba54d76
```

</td></tr>
<tr><td nowrap><a href="https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/SHA256SUMS.txt">SHA256SUMS.txt</a></td><td align="center">TXT</td><td>

```
0117a6eedb95374d98e5f394564c5b346e21f04f0a02bfd7d406bc1b51fd46c2
```

</td></tr>
</tbody>
</table>

<details>
<summary>Verify download (SHA-256)</summary>

1. Copy the SHA-256 from the table for your file, or download [SHA256SUMS.txt][checksums].
2. Open PowerShell in the folder containing your download and run:

   ```powershell
   (Get-FileHash -LiteralPath '.\CloudInlet-1.2.0-win-x64-release-setup.exe' -Algorithm SHA256).Hash
   ```

3. Compare the result with the hash for the **same file and Release/Debug build**. All 64 characters must match; uppercase/lowercase does not matter.

For another package, replace the filename in the command. If the hashes differ, do not run the file; download it again.

</details>

## Changelog

### Features

- Search app pages and settings with recent queries and clear history. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Cloud services dashboard for B2 and OneDrive accounts. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Searchable source and destination pickers for local and cloud transfers. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Guided transfer exclusions with editable rules and example matching. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- System, Light and Dark theme previews with Windows appearance shortcuts. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))

### Bug fixes

- Fixed unnecessary stacking and cramped controls in smaller windows. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Fixed navigation unexpectedly returning Home or losing the current settings page. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Fixed transfer-row and tray crashes caused by icon recycling. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Prevented damaged or inaccessible diagnostic files from blocking startup. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Restored independent cloud-job pause/resume without a configured B2 backup. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Corrected startup controls and pause state for OneDrive-only accounts. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Preserved transfer drafts when connecting an account during setup. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Fixed folder moves failing after verified child files were removed. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Rejected stale import reviews and guarded source files against concurrent changes. ([4fc7ca4](https://github.com/TahsinFaiyaz30/CloudInlet/commit/4fc7ca4e5bd3f1313d469faf59417cf80f66e758))
- Preserved read-only imports and destination conflicts; kept online-only previews from downloading file contents. ([4fc7ca4](https://github.com/TahsinFaiyaz30/CloudInlet/commit/4fc7ca4e5bd3f1313d469faf59417cf80f66e758))
- Fixed native sync-root registration in the Store package. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Prevented missing placeholders after Store removal from propagating cloud deletions during recovery. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Corrected folder-icon scaling and caption colors when Windows and app themes differ. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))

### Improvements

- Refreshed Fluent pages, navigation, icons, dialogs and tray. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Combined transfer status, speed, progress and size into compact rows. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Simplified Updates messages and grouped actions into a wrapping toolbar. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Adapted theme previews to available width and text scaling. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Improved OneDrive sign-in with code, expiry, progress and copy controls. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Adapted minimum window sizing to display scale and available screen space. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))

### Compatibility

- Future releases use CloudInlet packages and schema-2 updates; original CloudBay clients upgrade through the frozen 1.1.2 bridge. Existing installation paths and saved data are preserved. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Windows 11 Store virtualization exclusions now cover only shared app state and required Explorer settings; data-retention and uninstall guidance are documented. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))

<details>
<summary>Dependencies and release tooling</summary>

- Updated SQLite bundle to 2.1.13, with SQLite engine 3.53.3. ([b6acd76](https://github.com/TahsinFaiyaz30/CloudInlet/commit/b6acd76697f22da7a51c770afd6a9f9cdfd23ce3))
- Strengthened package validation, final checksums and release-file handling. ([9e7bdc1](https://github.com/TahsinFaiyaz30/CloudInlet/commit/9e7bdc1b7dc8459a723548bdea4f4fdf8c9144af), [7c1b42f](https://github.com/TahsinFaiyaz30/CloudInlet/commit/7c1b42f6edd3c1c7b0b1a8a89b92cb24916fe478))

</details>

<details>
<summary>Installation notes</summary>

- **Platform:** x64. The package minimum is Windows 10 build 19041; local native validation covered Windows 11. Windows 10 execution remains unverified.
- **Runtime:** bundled with the app; no separate .NET installation is required.
- Keep the same installer type and Release/Debug build when updating.
- Original CloudBay installations must upgrade through the **1.1.2 bridge** first.
- Portable builds update manually; Store installations update through Microsoft Store.
- GitHub's source archives are for building the app.
- **Signing:** Release EXE/MSI: unsigned; Debug EXE/MSI: unsigned.

[Installation guide](https://github.com/TahsinFaiyaz30/CloudInlet/blob/v1.2.0/docs/INSTALLERS.md) · [Data retention](https://github.com/TahsinFaiyaz30/CloudInlet/blob/v1.2.0/docs/UNINSTALL.md)

</details>

<details>
<summary>Validation</summary>

- Build validation: Release 828 tests passed; Debug 828 tests passed; zero failures or skips.
- 959 local desktop tests and 116 release-script assertions passed for this release.
- Light and dark UI checks, including compact-window layouts; isolated Store-package removal and recovery checks.
- Hosted tests exclude Native and LiveProvider cases. Live B2/OneDrive acceptance and real personal-folder redirection remain unverified. Store certification is separate.

[Windows CI](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/38114892013) · [Release build](https://github.com/TahsinFaiyaz30/CloudInlet/actions/runs/38115258247)

</details>

---

[Full changelog: v1.1.2 → v1.2.0](https://github.com/TahsinFaiyaz30/CloudInlet/compare/v1.1.2...v1.2.0)

[release-exe]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-setup.exe
[release-msi]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-setup.msi
[release-zip]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-portable.zip
[debug-exe]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-debug-setup.exe
[debug-msi]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-debug-setup.msi
[debug-zip]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-debug-portable.zip
[store-msix]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/CloudInlet-1.2.0-win-x64-release-store.msix
[update-feed]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/updates-v2.json
[checksums]: https://github.com/TahsinFaiyaz30/CloudInlet/releases/download/v1.2.0/SHA256SUMS.txt

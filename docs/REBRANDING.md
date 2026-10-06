# CloudInlet 1.1.2 upgrade compatibility

CloudInlet is the new product name for CloudBay. Version 1.1.2 is a patch release for the rename. The published 1.0.0, 1.1.0 and 1.1.1 releases and their tags remain unchanged in the renamed [CloudInlet repository](https://github.com/TahsinFaiyaz30/CloudInlet).

## Existing installations

Use **Settings → About → Check for updates** in the existing app. The 1.1.2 update retains the installed EXE/MSI format and Debug/Release flavor. It upgrades the same Windows product, changes its display name and shortcuts to CloudInlet, and reopens CloudInlet. Existing installations retain their installation directory; fresh installations default to `Programs\CloudInlet` or `Programs\CloudInlet Debug`.

Accounts, backup locations, exclusion rules, transfer plans, acknowledged checkpoints, completion receipts and update preferences remain in place. Existing local backup directories and B2 prefixes are not renamed or moved: renaming a configured directory would invalidate Windows folder mappings and source identities. Fresh defaults use the CloudInlet name.

The established `%LOCALAPPDATA%\CloudBay` private state directory, Windows DPAPI entropy, Cloud Files registration IDs, instance pipe, EXE AppIds, MSI UpgradeCodes and distribution registry keys remain stable compatibility identifiers. They are not product display names. Renaming these would strand encrypted credentials, recovery records, native placeholders or old updater handoffs. The Store startup task and manifest application ID also remain stable, preserving the Windows application identity; Store production package identity continues to come from the existing reserved Partner Center identity.

Startup registration changes to CloudInlet while preserving the user's enabled or disabled Windows startup choice. The migration applies only to an owned legacy command in the current installation directory. A small legacy `CloudBay.exe` compatibility launcher remains in the installed payload because published update workers verify and reopen that exact executable. It launches the actual `CloudInlet.exe`, preserving the new Windows process name and WinUI resource lookup. CloudInlet shares the existing instance identity.

## Legacy update bridge

Published CloudBay updaters strictly trust `TahsinFaiyaz30/CloudBay`, `updates-v1.json`, and `CloudBay-X.Y.Z-...` package filenames. They reject a redirect to a differently named GitHub repository. A repository rename alone therefore cannot deliver this upgrade.

The main source repository is renamed to **CloudInlet**. The old **CloudBay** repository name hosts a small, static update bridge containing the verified 1.1.2 legacy package aliases and schema-1 manifest. Those aliases contain the identical CloudInlet installers and portable payloads. After upgrading to 1.1.2, the new updater uses the canonical CloudInlet `updates-v2.json` feed and CloudInlet package names. The bridge must remain available for installations that have not yet upgraded; it needs no ongoing synchronization to later releases.

Reusing the old repository name deliberately replaces GitHub's repository-name redirect. The bridge README points to the canonical source, issues and historical releases. Canonical release packaging retains both manifests and verifies matching alias lengths and SHA-256 digests; existing published release bytes are never replaced.

## Provider and recovery compatibility

The registered OneDrive public client ID is unchanged, so saved personal/work accounts retain their refresh tokens. New environment overrides use `CLOUDINLET_ONEDRIVE_CLIENT_ID` and `CLOUDINLET_ONEDRIVE_TENANT_ID`; legacy `CLOUDBAY_...` overrides remain supported when the new values are absent. Microsoft account-consent screens may show the app registration's independently managed display name until its owner updates it in Entra.

B2 upload intent metadata, existing internal backup namespaces, local recovery partials and acknowledged keep-both filenames retain their original identities. New keep-both filenames use CloudInlet; recovery accepts only the exact deterministic legacy alternate name as well. Completed files are not copied again merely because the product name changed.

The rename does not change transfer scheduling, integrity checks or RAM-only cloud payload handling. Live provider tests remain an explicit opt-in acceptance gate; ordinary release builds exclude that category rather than reporting a skipped test as passed.

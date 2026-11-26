# Upgrading from CloudBay

**CloudInlet 1.2.2 is the last release that can convert a CloudBay installation.** New users can install the current CloudInlet release directly.

1. For an original CloudBay installation, update to the published 1.1.2 bridge. It keeps the original installer format, build flavor and installation directory. [CloudBay bridge release](https://github.com/TahsinFaiyaz30/CloudBay/releases/tag/v1.1.2)
2. Install and open CloudInlet 1.2.2. Finish any pending conversion or choose to keep your existing location. Open the app and check that it shows your accounts and backups. [CloudInlet 1.2.2 and matching installers](https://github.com/TahsinFaiyaz30/CloudInlet/releases/tag/v1.2.2)
3. Update to 1.2.5 using the same installer format. Open **Connectors** and install the providers used by your accounts. Saved jobs can resume after their connectors are available.

## Saved locations

The EXE and MSI update the registered installation in place. Fresh profiles use `%LOCALAPPDATA%\CloudInlet\Client`. An existing profile may still use `%LOCALAPPDATA%\CloudBay\Client`. Debug has a separate `Debug\Client` directory under each name.

The app reads the protected location and completion records that 1.2.2 left behind. It can also recognize a single retained CloudInlet profile from its saved settings. Two populated profiles or an unfinished conversion require attention in 1.2.2. Keep both directories and their records intact. Account encryption, Explorer sync-root identities, Windows folder mappings, update ownership and transfer checkpoints retain their original formats.

Registered desktop updaters check the installed version against the 1.2.2 minimum. The Store controls MSIX version selection and can skip versions. When 1.2.5 first opens an existing Store profile, it checks this Windows user's successful 1.2.2 package deployment. If Windows has discarded that history, install and open the 1.2.2 desktop EXE or MSI under the same account, then reopen the Store app. This also provides a recovery path for an unregistered portable copy. A new profile opens immediately on every edition.

The archived CloudBay repository continues to serve the original schema-1 bridge. CloudInlet publishes schema-2 updates. Historical conversion instructions and test tools remain available at the 1.2.2 tag. [1.2.2 upgrade guide](https://github.com/TahsinFaiyaz30/CloudInlet/blob/v1.2.2/docs/REBRANDING.md)

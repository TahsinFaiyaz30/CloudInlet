# Startup recovery maintenance update

Prepared 2026-11-25 from the non-modular `main` after v1.2.1. The separate
connector implementation is not included in this update.

The reported message, "An unrecognized CloudInlet profile already exists",
came from profile routing before account connections or transfers started.
The photo alone does not establish what created that customer's directory,
or that OneDrive caused it. The old condition rejected every existing canonical
directory without a routing record, even an empty directory tree.

The change preserves empty scaffolding and selects existing legacy data when
appropriate. A retained canonical profile can recover its absent route only
when settings identify the supported format, accounts and databases verify,
the other location has no saved data, no installer is using it, and no orphaned
migration records are present. Existing unreadable routes, links, populated
conflicts and incomplete migrations still stop startup. No profile directory
is deleted, merged or moved by this recovery. Directory guards and the existing
process/profile leases remain in force.

Startup errors now identify the failure without assuming that a migration was
underway. Users can copy the message/version and affected locations for support.
They are told to keep the saved-data folders intact.

## Local evidence

Ignored evidence is under `artifacts/startup-1.2.2/` in the hotfix worktree:

- `before-fix.trx`: five failures reproduce the original message.
- `build-release.log`: complete Release solution build, zero warnings/errors.
- `routing-final.trx`: 52 focused routing, startup and Store-policy cases passed.
- `release-regression.trx`: 1,299 hosted-compatible cases passed, excluding
  LiveProvider and other Native-named cases.
- `release-scripts.log`: 141 isolated release-script assertions passed.

These results do not establish the customer's exact file layout, live-cloud
behavior, new Store delivery or full desktop/Windows-version acceptance.
The release workflow separately validates both build configurations and final
installer artifacts before publication.

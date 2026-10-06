# Nova Manager changelog

## 1.0.10 — Hotfix

- Redesigned Settings with a clearer release-notes header and distinct appearance and update sections.
- Fixed successful updates leaving the staged executable behind and triggering a temporary-folder cleanup warning.

## 1.0.9 — Hotfix

- Polished the Settings layout with clearer release-note, appearance, and application-update sections.
- Improved spacing, version/status hierarchy, and responsive update actions while preserving existing Settings behavior.

## 1.0.8 — Hotfix

- Fixed changelog errors after updating from Nova 1.0.7 by accepting its older updater handoff and always showing release notes in Settings.
- Settings now loads the latest published changelog and version from GitHub, with the changelog for the installed version bundled in the single-file EXE for offline fallback.
- Once installed, this updater stores downloads and rollback backups for future updates in Local AppData instead of the Nova installation or Downloads folder.

## 1.0.7 — Feature release

- Added a release changelog to Nova's Updates section. After an in-app update, Nova reopens directly to the new release notes.
- Added saved Light and Dark appearance settings.
- Fixed update downloads keeping the staged executable locked during verification, and retry brief Windows file locks during installation.

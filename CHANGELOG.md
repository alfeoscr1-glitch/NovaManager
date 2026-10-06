# Nova Manager changelog

## 1.2.2 — Hotfix

- Feedback and bug reports are now submitted directly as GitHub Issues after one-time GitHub authorization; no browser-based issue submission or embedded access token is used.
- The user's GitHub authorization token is protected with Windows DPAPI for the signed-in Windows account.
- Saved authorization is reused for subsequent submissions, and expiring GitHub access tokens are renewed with the saved refresh token.
- Fixed authorization storage compatibility so existing saved tokens are found and migrated rather than starting a new Device Flow for each submission.
- Kept automatic background update-check failures quiet; Nova retries on its regular interval without repeatedly surfacing transient GitHub errors.
- Kept Nova open throughout GitHub authorization and report submission, and surfaced authorization failures in Settings instead of allowing them to terminate the application.

## 1.2.2 — Feature release

- Added Settings feature-suggestion and bug-report forms; bug reports include only Nova and Windows version diagnostics.
- Added hourly, per-user update checks while Nova is closed, with a Windows notification that opens Settings and never installs automatically.
- Reduced GitHub API traffic with conditional requests and a persistent Local AppData cache; rate limiting now preserves the last known release details and explains the status in Settings.

## 1.2.1 — Hotfix

- Added a bridge patch so existing 1.2.0 installations can receive hotfixes even when the release keeps the same version tag.
- Stable hotfix updates with the same version are detected by comparing the installed EXE with the published release asset SHA-256 digest.

## 1.2.0 — Hotfix

- Fixed the Settings appearance selector so the selected Light or Dark theme is always visible.
- Re-published the existing 1.2.0 executable as a same-version hotfix; update detection compares the executable digest when versions match.

## 1.2.0 — Feature release

- Added live checks for Nova updates while the app is running, with a Windows notification when a new release is detected.
- Added a numbered badge to Settings showing how many stable Nova releases are newer than the installed version.
- Update notifications are suppressed after the same release has already been announced; open Settings to download and install an available update.

## 1.1.1 — Hotfix

- Replaced the Safe, Standard, and Advanced cleanup mode previews with one Cleanup files action.
- Added user and Windows Temp, Windows web and thumbnail caches, DirectX and NVIDIA caches, and Edge/Chrome browser caches to the reviewed cleanup scan.
- Made cleanup rows and checkboxes easier to read in Dark mode and made the Light/Dark selection text visible.
- Cleanup scans include all found files by default; review the checked locations and confirm before removal. Thumbnail cleanup targets only `thumbcache_*.db` files.

## 1.1.0 — Feature release

- Redesigned the whole application with a new top navigation bar, a cleaner page header, and a full-width content layout.
- Moved the last-scan status into the page header and refreshed navigation, card, and button styling in both Light and Dark modes.

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

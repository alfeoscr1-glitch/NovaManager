# Nova Manager changelog

## 1.2.3 — Hotfix

- The Updates scan now checks Nova Manager releases, winget updates, Microsoft Store updates available from its Store source, and Windows Update availability. Windows updates are not installed; Nova offers a button to open Windows Update Settings.
- Nova's existing update download now displays percentage progress and a reliable remaining-time estimate when GitHub provides the asset size. The existing updater helper runs without showing a command window.

## 1.2.3 — Feature release

- Nova now remembers your last scans. The installed software list, winget updates, temporary-file estimate, last scan time and last cleanup result are saved locally and shown on the Overview right away when Nova starts, including after an update.
- Roomier layout: cards, the Overview tiles and especially the Temporary files cleanup section now have more padding and row height, so everything has room to breathe while keeping the same design.
- Fixed GitHub authorization storage on Windows, so Send Suggestion and Send Bug Report can save and reuse your authorization.
- Update notifications are more reliable: the PC notification and the in-app update check now both run every 15 minutes, failures are silent, and the notification stays clickable to open Nova's update settings.

# Nova Manager changelog

## 1.2.3 — Feature release

- Nova now remembers your last scans. The installed software list, winget updates, temporary-file estimate, last scan time and last cleanup result are saved locally and shown on the Overview right away when Nova starts, including after an update.
- Roomier layout: cards, the Overview tiles and especially the Temporary files cleanup section now have more padding and row height, so everything has room to breathe while keeping the same design.
- Fixed GitHub authorization storage on Windows, so Send Suggestion and Send Bug Report can save and reuse your authorization.
- Update notifications are more reliable: the PC notification and the in-app update check now both run every 15 minutes, failures are silent, and the notification stays clickable to open Nova's update settings.

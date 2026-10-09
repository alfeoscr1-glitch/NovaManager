# Nova Manager changelog

## 1.2.5 — Feature release

- Replaced the Overview page with a Dashboard, using a left-side navigation rail, a compact welcome and scan panel, and live-data summary cards for installed apps, available winget updates, and scanned temporary files.
- Added live CPU, GPU, RAM, and VRAM usage with a rolling in-session graph and 1H, 6H, and 24H ranges. Monitoring runs only while the Dashboard is selected, samples on a background loop, and keeps chart redraws and history sampling bounded.
- Added PC specification, Windows, boot-time, graphics-driver, and detected local-drive details. CPU usage uses Windows performance counters, memory uses the native Windows API, and NVIDIA telemetry uses NVML when available with Windows GPU performance counters as the utilization fallback.
- Redesigned Settings as a compact six-card layout with a release-aware version summary, an in-app multi-release change log, and direct links to Nova feedback forms.
- Overhauled the Settings presentation with dark glass-effect cards, neon-blue borders, colorful icon plates, and a responsive sunset-art header while retaining the three-column layout and minimum window constraints.
- Enlarged and strengthened Settings typography, controls, card insets, and action tiles; expanded the update card and simplified the appearance card while keeping the compact Developer version badge readable at different window sizes.
- Refined the Settings cards to use the available layout height more consistently, expanded update and support actions, added architecture details, and replaced the raster header with crisp vector artwork.
- Refreshed the full application experience with larger, clearer type, consistent rounded surfaces and controls, more comfortable spacing, and a dark visual theme by default for new installations; existing saved Light or Dark preferences are preserved.
- Reworked the shared application controls, including accessible keyboard-focus states, consistent rounded buttons and fields, and roomier data tables across software, updates, and storage.
- Tuned the dark palette toward layered navy surfaces and improved cleanup selection contrast, while keeping each settings card balanced as the window grows.
- Added persisted behaviour preferences for minimizing to the system tray, starting with Windows, and showing a startup notification; retained the existing update workflow, theme selection, and password-gated Developer Mode.

- Redesigned the Temporary Files cleanup panel to follow the supplied two-column layout, with a compact selectable category list, a selected-size summary, safety guidance, and clearer cleanup/scan actions.
- Added a master checkbox to select or clear all scanned cleanup files while preserving individual file review and the existing confirmation and safe-delete checks.

## 1.2.3 — Hotfix

- Automatic GitHub release checks now run every 10 minutes while Nova is open and through the existing scheduled task while it is closed. Only automatic checks trigger the Windows update notification; manual checks continue to report update availability without sending that notification.
- Serialized notification deduplication across Nova's open and scheduled processes so a release is only notified once when both checks run at the same time.
- Reduced the shared release API cache lifetime below the check interval so scheduled and in-app checks do not reuse stale release data for an entire polling interval.
- Added a password-gated Developer Mode area in Settings with a local Windows notification test. Its remembered access marker is protected with DPAPI for the current Windows account; notification tests are local only.
- The Updates scan now checks Nova Manager releases, winget updates, Microsoft Store updates available from its Store source, and Windows Update availability. Windows updates are not installed; Nova offers a button to open Windows Update Settings.
- Nova's existing update download now displays percentage progress and a reliable remaining-time estimate when GitHub provides the asset size. The existing updater helper runs without showing a command window.

## 1.2.3 — Feature release

- Nova now remembers your last scans. The installed software list, winget updates, temporary-file estimate, last scan time and last cleanup result are saved locally and shown on the Overview right away when Nova starts, including after an update.
- Roomier layout: cards, the Overview tiles and especially the Temporary files cleanup section now have more padding and row height, so everything has room to breathe while keeping the same design.
- Fixed GitHub authorization storage on Windows, so Send Suggestion and Send Bug Report can save and reuse your authorization.
- Update notifications are more reliable: the PC notification and the in-app update check now both run every 15 minutes, failures are silent, and the notification stays clickable to open Nova's update settings.

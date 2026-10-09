# Nova Software Manager

A Windows desktop app for reviewing installed software, checking third-party update candidates, and managing temporary-file cleanup.

## Features

- Scans Windows' registered installed-program lists (64-bit and 32-bit, current-user and machine) and current-user Windows app packages.
- The Updates section checks Nova Manager releases, the `winget` and Microsoft Store (`msstore`) package sources, and Windows Update availability. Windows updates are never installed by Nova; a button opens the standard Windows Update Settings page. Store results are limited to packages discoverable by the configured Store source.
- Starts an individual, user-confirmed update in a visible `winget` process; it does not request silent installation or automatically accept package agreements.
- The Dashboard provides a welcome and scan panel, live summary cards, PC specifications, and CPU/GPU/RAM/VRAM monitoring with 1H, 6H, and 24H graph ranges. The left navigation rail opens Dashboard, My software, Updates, Storage & cleanup, and Settings. The scan button scans the whole app from Dashboard, or only the active section elsewhere.
- Scans the current-user and Windows Temp folders, Windows web and thumbnail caches, DirectX and NVIDIA shader caches, and the default Edge and Chrome browser caches.
- Cleanup shows approved locations, file counts, estimated sizes, and a Browse action. All files found in available locations start checked; use the master checkbox or review individual categories/files before using the single **Cleanup files** action. Cleanup requires confirmation and removes only regular files enumerated in the scan. Changed files, reparse points, locked files, and inaccessible files are skipped or reported; folders are never removed and no administrator elevation is requested.
- Thumbnail cleanup is limited to `thumbcache_*.db` files. Close Edge and Chrome before cleanup to allow their cache files to be removed.
- Storage & cleanup includes a read-only folder navigator with quick locations for Home, Desktop, Downloads, Documents, Pictures, AppData, Program Files, ProgramData, Windows, This PC, and available drives. It lists direct children, supports typed paths and recursive name search, skips linked folders, and caps search at 5,000 results.
- A shortcuts view can remember a chosen folder and list `.lnk` names and targets. It never moves or deletes shortcuts.
- Settings includes a Light/Dark appearance selector. The selection applies immediately and is saved for the next launch; Dark is the default for new installations.
- Settings includes a release-aware version summary and in-app multi-release changelog, feedback links, and preferences for minimizing to the system tray, starting with Windows, and showing a startup notification.
- The gear beside Nova Software Manager opens Settings. Check for updates queries the latest stable release from `alfeoscr1-glitch/NovaManager` on GitHub. Nova downloads only the `NovaManager.exe` release asset, verifies it against GitHub's SHA-256 asset digest, then uses a separate helper process to replace and restart the app. The prior executable is retained as a timestamped `.previous-...` backup.
- Settings always shows the latest published release version and changelog. It loads the notes from GitHub and includes the installed version's changelog in the single-file EXE for offline use.
- Nova checks for newer stable releases at startup and every 10 minutes while it is running. On first launch, it registers a non-elevated, per-user Windows Scheduled Task that checks every 10 minutes while the user is signed in, even when Nova is closed. Only automatic checks trigger a newly detected update or hotfix notification; manual checks remain available without triggering that notification. Selecting a notification opens Nova's Settings, where the user can choose whether to install. Updates are never installed automatically. Windows notifications must be enabled, and the scheduled task is refreshed each time Nova starts.
- Nova update downloads report measured percentage progress and an estimated time remaining when the release asset size is available. The existing helper process that replaces and restarts Nova runs without a command window.
- Release API responses are cached in Local AppData and use GitHub conditional requests to limit traffic. When GitHub rate-limits a request or is temporarily unreachable, Settings shows the last saved release data and an explanatory status instead of repeatedly failing.
- Settings includes feature-suggestion and bug-report forms that submit issues directly to `alfeoscr1-glitch/NovaManager`, with the existing `enhancement` and `bug` labels. On first use, each Windows account authorizes Nova through GitHub Device Flow; the saved authorization is protected with Windows DPAPI and reused for later submissions. Expiring access tokens are renewed with GitHub's refresh-token flow; no client secret or personal access token is embedded in Nova. Bug reports include only the Nova and Windows versions, not usernames, local paths, or machine inventories. The submitted issue mentions the repository owner so GitHub can deliver its normal mention notification.
- The Settings gear shows the count of stable releases newer than the installed version. Nova 1.2.1 and later can detect a republished same-version hotfix by comparing the installed EXE's SHA-256 digest with the release asset. Older installs require a higher-version bridge release once before they can receive same-version hotfixes.
- The Settings page groups release notes, appearance, and application update controls into clearer, responsive sections.
- Nova removes the staged update executable after installation before cleaning up the temporary update folder.
- A GitHub Actions workflow builds and publishes a new self-contained EXE release on each push to `main`. It selects the higher of the project version and next patch version, and requires a matching changelog entry, so no manual binary upload is needed.

## Build and run

Requires the .NET 8 SDK and Windows Desktop Runtime/targeting pack. Windows App Installer provides `winget`.

```powershell
dotnet build .\NovaManager.csproj
dotnet run --project .\NovaManager.csproj
```

Create a self-contained single-file Windows executable:

```powershell
dotnet publish .\NovaManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The publish output is under `bin\Release\net8.0-windows\win-x64\publish`. The executable is also published at the workspace root as `NovaManager.exe`.

## Automatic updates through GitHub

The updater and workflow use the public repository `alfeoscr1-glitch/NovaManager`. Keep it public so Nova can check releases without a sign-in.

For the initial setup, connect this project to the repository and push its source to the `main` branch. One straightforward way is to use GitHub Desktop: clone `alfeoscr1-glitch/NovaManager`, copy the project source files into the cloned repository, then commit and push them. Include `NovaManager.csproj`, all `.cs` and `.xaml` source files, `CHANGELOG.md`, `.gitignore`, and `.github\workflows\release.yml`. Do not copy `bin`, `obj`, or the built EXE as source files. On GitHub, open **Settings → Actions → General** and make sure Actions are allowed; under **Workflow permissions**, allow read and write access so the workflow can publish releases. The workflow requests `contents: write` using GitHub's built-in token.

Once the source files and workflow are on the `main` branch, a push to `main` starts a Windows build and publishes a release with the asset named `NovaManager.exe`. For each release, add a `CHANGELOG.md` section for its three-part version. Use `## 1.0.9 — Feature release` for a feature update or `## 1.0.9 — Hotfix` for a small fix. A hotfix is still delivered as a normal versioned patch and self-contained update; its release is clearly labeled as a hotfix. The workflow uses a project version that is ahead of the latest release; otherwise, it increments the latest stable patch automatically. Friends with an earlier Nova build use **Settings → Check for updates** to install the release. **Settings** shows the latest changelog and version.

If the repository has no stable semantic-version release, the workflow uses the three-part `<Version>` in `NovaManager.csproj`. Subsequent releases use a higher project version when supplied, or increment the latest stable patch number.

## Limitations

Inventory coverage is best-effort: portable apps, apps registered only for another Windows account, and applications without uninstall registration may not appear. `winget` only reports apps it can match in its configured source; this app does not provide a universal updater or check Store update availability per app.

Temporary-file cleanup is irreversible. Review paths and counts in the app (or open each in Explorer) before confirming. Only checked file paths recorded during that scan are eligible; parent paths are revalidated against the standard temp locations. Files detected as changed, active, or protected may remain and access failures are reported.

The folder navigator is read-only. Windows Gallery is virtual and may display images from beyond the user's Pictures folder. Searching the entire PC can take time, but can be canceled and does not follow linked folders.

The automatic updater requires the configured public GitHub repository to exist and have a stable release containing `NovaManager.exe` with a SHA-256 asset digest. It checks for newer three-part version tags and, in versions that support it, detects a republished same-version hotfix by digest. Downloads use HTTPS; Nova verifies the digest and Windows executable signature header and retains a backup copy in Local AppData. After the first update from older Nova versions, the compatibility updater's temporary staging folder is removed and its rollback backup is moved from beside Nova into Local AppData; later updates stage both files there directly. The release itself remains a single self-contained EXE. Replacing the app requires write access to its installation folder; if that is unavailable, download and run the release manually. If GitHub reports 404, confirm the repository spelling and owner, that the repository is public, and that at least one non-draft stable release has been published. A GitHub Actions workflow cannot see edits that exist only on a developer's computer: source changes must first be committed/uploaded to the repository's `main` branch before GitHub can build and release them.

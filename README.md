# Nova Software Manager

A Windows desktop app for reviewing installed software, checking third-party update candidates, and managing temporary-file cleanup.

## Features

- Scans Windows' registered installed-program lists (64-bit and 32-bit, current-user and machine) and current-user Windows app packages.
- Checks the `winget` source for update candidates. It does not query the Microsoft Store catalog or claim every installed app can be matched and updated.
- Starts an individual, user-confirmed update in a visible `winget` process; it does not request silent installation or automatically accept package agreements.
- The left navigation selects Overview, My software, Updates, or Storage & cleanup. The scan button scans the whole app from Overview, or only the active section elsewhere.
- Scans the standard current-user `LocalAppData\Temp` folder and `Windows\Temp` for a preview. `%TEMP%`, `%TMP%`, and the .NET temporary path are checked against those standard locations; custom locations outside them are not scanned.
- Shows approved temp-folder paths, file counts, estimated size, and a Browse action. Cleanup requires confirmation and removes only regular files enumerated in that scan. Changed files, reparse points, and inaccessible files are skipped and reported; folders are left in place and no administrator elevation is requested.
- Temp cleanup presents Safe, Standard, and Advanced mode buttons, category cards, a selected-cleanup summary, and detailed file results. Only user Temp and Windows Temp are scanned/selectable. The other mode buttons are visual only and do not widen scan or deletion scope. Nothing is selected initially.
- Storage & cleanup includes a read-only folder navigator with quick locations for Home, Desktop, Downloads, Documents, Pictures, AppData, Program Files, ProgramData, Windows, This PC, and available drives. It lists direct children, supports typed paths and recursive name search, skips linked folders, and caps search at 5,000 results.
- A shortcuts view can remember a chosen folder and list `.lnk` names and targets. It never moves or deletes shortcuts.
- Settings includes a Light/Dark appearance selector. The selection applies immediately and is saved for the next launch.
- The gear beside Nova Software Manager opens Settings. Check for updates queries the latest stable release from `alfeoscr1-glitch/NovaManager` on GitHub. Nova downloads only the `NovaManager.exe` release asset, verifies it against GitHub's SHA-256 asset digest, then uses a separate helper process to replace and restart the app. The prior executable is retained as a timestamped `.previous-...` backup.
- A GitHub Actions workflow builds and publishes a new self-contained EXE release on each push to `main`. It increments the patch version automatically from the latest stable release, so no release version edit or manual binary upload is needed for routine updates.

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

For the initial setup, connect this project to the repository and push its source to the `main` branch. One straightforward way is to use GitHub Desktop: clone `alfeoscr1-glitch/NovaManager`, copy the project source files into the cloned repository, then commit and push them. Include `NovaManager.csproj`, the `.cs` and `.xaml` source files, `AppUpdateService.cs`, `.gitignore`, and `.github\workflows\release.yml`. Do not copy `bin`, `obj`, or the built EXE as source files. On GitHub, open **Settings → Actions → General** and make sure Actions are allowed; under **Workflow permissions**, allow read and write access so the workflow can publish releases. The workflow requests `contents: write` using GitHub's built-in token.

Once the source files and workflow are on the `main` branch, a push to `main` starts a Windows build and publishes the next patch release automatically, based on the latest stable release, with the asset named `NovaManager.exe`. For each future change, commit or upload the changed source files to `main`; GitHub Actions builds and releases the next patch version. Friends with an earlier Nova build use **Settings → Check for updates** to download and install the release. You do not have to send them each new EXE.

If the repository has no stable semantic-version release, the workflow starts from the three-part `<Version>` in `NovaManager.csproj` and increments the patch component. Subsequent releases use the highest stable `vMAJOR.MINOR.PATCH` tag and increment its patch number.

## Limitations

Inventory coverage is best-effort: portable apps, apps registered only for another Windows account, and applications without uninstall registration may not appear. `winget` only reports apps it can match in its configured source; this app does not provide a universal updater or check Store update availability per app.

Temporary-file cleanup is irreversible. Review paths and counts in the app (or open each in Explorer) before confirming. Only checked file paths recorded during that scan are eligible; parent paths are revalidated against the standard temp locations. Files detected as changed, active, or protected may remain and access failures are reported.

The folder navigator is read-only. Windows Gallery is virtual and may display images from beyond the user's Pictures folder. Searching the entire PC can take time, but can be canceled and does not follow linked folders.

The automatic updater requires the configured public GitHub repository to exist and have a stable release containing `NovaManager.exe` with a SHA-256 asset digest. It checks for newer three-part version tags, downloads over HTTPS, verifies the digest and Windows executable signature header, and retains a backup copy of the replaced executable. Replacing the app requires write access to its installation folder; if that is unavailable, download and run the release manually. If GitHub reports 404, confirm the repository spelling and owner, that the repository is public, and that at least one non-draft stable release has been published. A GitHub Actions workflow cannot see edits that exist only on a developer's computer: source changes must first be committed/uploaded to the repository's `main` branch before GitHub can build and release them.

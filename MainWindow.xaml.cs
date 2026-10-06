using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace NovaManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<InstalledSoftware> software = new();
    private readonly ObservableCollection<UpdateCandidate> updates = new();
    private readonly ObservableCollection<TempFolderResult> tempFolders = new();
    private readonly ObservableCollection<TempFileResult> tempFiles = new();
    private readonly ObservableCollection<TempCleanupCategory> cleanupCategories = new();
    private readonly ObservableCollection<TempCleanupCategory> selectedCleanupCategories = new();
    private readonly ObservableCollection<StorageEntryInfo> storageFolders = new();
    private readonly ObservableCollection<ShortcutInfo> shortcuts = new();
    private readonly UpdateNotificationService updateNotificationService = new();
    private readonly DispatcherTimer updateCheckTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private AppUpdateRelease? availableAppUpdate;
    private TempScanResult? lastTempScan;
    private bool isBusy;
    private bool isLoadingLatestReleaseNotes;
    private bool isUpdatingTempSelection;
    private bool showTempFileDetails;
    private string? shortcutsFolder;
    private readonly Stack<string> folderMapHistory = new();
    private CancellationTokenSource? folderSearchCancellation;
    private string currentFolderMapPath = StorageExplorer.ThisPcPath;

    public MainWindow(bool openSettings = false)
    {
        InitializeComponent();
        if (openSettings)
        {
            MainTabs.SelectedIndex = 4;
        }

        DataContext = new
        {
            Software = software,
            Updates = updates,
            TempFolders = tempFolders,
            TempFiles = tempFiles,
            CleanupCategories = cleanupCategories,
            SelectedCleanupCategories = selectedCleanupCategories,
            StorageFolders = storageFolders,
            Shortcuts = shortcuts
        };
        AddFolderRootOptions();
        LoadShortcutsFolder();
        AppearanceComboBox.SelectedIndex = ThemeManager.CurrentTheme == "Dark" ? 1 : 0;
        AppearanceSelectionText.Text = ThemeManager.CurrentTheme;
        AppearanceComboBox.SelectionChanged += AppearanceComboBox_SelectionChanged;
        AppVersionText.Text = $"Installed version: {AppUpdateService.CurrentVersion}";
        var bundledNotes = AppUpdateService.GetBundledReleaseNotes(AppUpdateService.CurrentVersion);
        ShowLatestReleaseNotes(bundledNotes);
        LatestReleaseNotesStatusText.Text = "Showing changelog bundled with this version. Checking GitHub for the latest release…";
        Loaded += async (_, _) => await RefreshLatestReleaseNotesAsync();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            updateCheckTimer.Stop();
            updateNotificationService.Dispose();
        };
        updateNotificationService.NotificationClicked += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            WindowState = WindowState.Normal;
            Activate();
            MainTabs.SelectedIndex = 4;
        });
        updateCheckTimer.Tick += UpdateCheckTimer_Tick;
        ShowStoragePanel(TempCleanupPanel);
        UpdateSectionChrome();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        string? notificationSetupError = null;
        try
        {
            await UpdateNotificationService.RegisterScheduledCheckAsync();
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            notificationSetupError = exception.Message;
        }

        updateCheckTimer.Start();
        await CheckLiveUpdateAvailabilityAsync();
        if (notificationSetupError is not null)
        {
            AppUpdateStatusText.Text += $" Background notifications could not be enabled: {notificationSetupError}";
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            return;
        }

        switch (MainTabs.SelectedIndex)
        {
            case 0:
                await RunScanAsync(ScanOverviewAsync);
                break;
            case 1:
                await RunScanAsync(ScanSoftwareAsync);
                break;
            case 2:
                await RunScanAsync(ScanUpdatesAsync);
                break;
            case 3:
                if (FolderMapPanel.Visibility == Visibility.Visible)
                {
                    await RunScanAsync(ScanFolderMapAsync);
                }
                else if (ShortcutsPanel.Visibility == Visibility.Visible)
                {
                    await RunScanAsync(ScanShortcutsAsync);
                }
                else
                {
                    await RunScanAsync(ScanTemporaryFoldersAsync);
                }

                break;
            case 4:
                await CheckForAppUpdatesAsync();
                break;
        }
    }

    private async Task RunScanAsync(Func<Task> scan)
    {
        if (isBusy)
        {
            return;
        }

        SetBusy(true, $"Scanning {GetSectionName()}…");
        try
        {
            await scan();
            LastScanText.Text = DateTime.Now.ToString("g");
            StatusText.Text = $"{GetSectionName()} scan complete.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"{GetSectionName()} scan failed.";
            MessageBox.Show(this, exception.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task ScanOverviewAsync()
    {
        software.Clear();
        updates.Clear();
        tempFolders.Clear();
        tempFiles.Clear();
        lastTempScan = null;
        CleanupStatusText.Text = string.Empty;
        RefreshCounts();

        var inventoryTask = SoftwareScanner.FindInstalledAsync(CancellationToken.None);
        var updateTask = SoftwareScanner.FindUpdatesAsync(CancellationToken.None);
        var tempTask = Task.Run(() => TempCleaner.Scan(CancellationToken.None));
        await ObserveScanTasksAsync(inventoryTask, updateTask, tempTask);
        var errors = new List<string>();

        if (inventoryTask.IsCompletedSuccessfully)
        {
            SetSoftware(inventoryTask.Result.Items);
            errors.AddRange(inventoryTask.Result.Warnings);
        }
        else
        {
            AddTaskError(errors, inventoryTask, "Installed software scan failed.");
        }

        if (updateTask.IsCompletedSuccessfully)
        {
            SetUpdates(updateTask.Result);
        }
        else
        {
            AddTaskError(errors, updateTask, "winget update scan failed.");
        }

        if (tempTask.IsCompletedSuccessfully)
        {
            SetTempScan(tempTask.Result);
        }
        else
        {
            AddTaskError(errors, tempTask, "Temporary-folder scan failed.");
        }

        RefreshCounts();
        ThrowScanWarnings(errors);
    }

    private async Task ScanSoftwareAsync()
    {
        software.Clear();
        RefreshCounts();
        var result = await SoftwareScanner.FindInstalledAsync(CancellationToken.None);
        SetSoftware(result.Items);
        RefreshCounts();
        ThrowScanWarnings(result.Warnings);
    }

    private async Task ScanUpdatesAsync()
    {
        updates.Clear();
        RefreshCounts();
        var result = await SoftwareScanner.FindUpdatesAsync(CancellationToken.None);
        SetUpdates(result);
        RefreshCounts();
    }

    private async Task ScanTemporaryFoldersAsync()
    {
        tempFolders.Clear();
        tempFiles.Clear();
        lastTempScan = null;
        CleanupStatusText.Text = string.Empty;
        RefreshCounts();
        var result = await Task.Run(() => TempCleaner.Scan(CancellationToken.None));
        SetTempScan(result);
        RefreshCounts();
        ThrowScanWarnings(result.Errors);
    }

    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForAppUpdatesAsync(forceRefresh: true);

    private async void SendFeatureSuggestion_Click(object sender, RoutedEventArgs e) =>
        await SubmitGitHubFeedbackAsync(
            FeatureSuggestionTextBox,
            "Feature suggestion",
            "enhancement",
            includeDiagnostics: false);

    private async void SendBugReport_Click(object sender, RoutedEventArgs e) =>
        await SubmitGitHubFeedbackAsync(
            BugReportTextBox,
            "Bug report",
            "bug",
            includeDiagnostics: true);

    private async Task SubmitGitHubFeedbackAsync(
        System.Windows.Controls.TextBox input,
        string issueType,
        string label,
        bool includeDiagnostics)
    {
        var description = input.Text.Trim();
        if (description.Length == 0)
        {
            FeedbackStatusText.Text = $"Enter a description before sending a {issueType.ToLowerInvariant()}.";
            input.Focus();
            return;
        }

        var body = $"### {issueType}\n\n{description}";
        if (includeDiagnostics)
        {
            body +=
                $"\n\n### Diagnostics\n- Nova version: {AppUpdateService.CurrentVersion}\n- Windows version: {Environment.OSVersion.VersionString}";
        }

        body += "\n\nSubmitted from Nova Manager Feedback & Support. @alfeoscr1-glitch";
        var title = $"[{issueType}] Nova Manager feedback";
        FeatureSuggestionButton.IsEnabled = false;
        BugReportButton.IsEnabled = false;
        isFeedbackSubmissionInProgress = true;
        FeedbackStatusText.Text = "Sending your report to GitHub…";
        try
        {
            var issueUrl = await GitHubFeedbackService.SubmitIssueAsync(
                title,
                body,
                label,
                AuthorizeGitHubFeedbackAsync,
                CancellationToken.None);
            input.Clear();
            FeedbackStatusText.Text = $"Submitted successfully. GitHub created the issue: {issueUrl}";
        }
        catch (Exception exception)
        {
            FeedbackStatusText.Text = $"Could not submit the report. No success was reported. {exception.Message}";
        }
        finally
        {
            isFeedbackSubmissionInProgress = false;
            FeatureSuggestionButton.IsEnabled = true;
            BugReportButton.IsEnabled = true;
        }
    }

    private bool isFeedbackSubmissionInProgress;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!isFeedbackSubmissionInProgress)
        {
            return;
        }

        e.Cancel = true;
        FeedbackStatusText.Text = "Nova will remain open until GitHub authorization and submission finish.";
    }

    private Task AuthorizeGitHubFeedbackAsync(string userCode, string verificationUri)
    {
        _ = Process.Start(new ProcessStartInfo(verificationUri) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Windows could not open GitHub's one-time sign-in page.");

        var result = MessageBox.Show(
            this,
            $"Nova needs one-time permission to submit feedback to the public Nova Manager repository.{Environment.NewLine}{Environment.NewLine}" +
            $"GitHub's authorization page has been opened. Enter this code there:{Environment.NewLine}{Environment.NewLine}" +
            $"{userCode}{Environment.NewLine}{Environment.NewLine}" +
            "After approving Nova in GitHub, select OK. Nova will then submit this report automatically.",
            "Authorize Nova feedback",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK)
        {
            throw new InvalidOperationException("GitHub sign-in was canceled; no report was submitted.");
        }

        FeedbackStatusText.Text = "Waiting for GitHub authorization…";
        return Task.CompletedTask;
    }

    private void AppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppearanceComboBox.SelectedItem is not ComboBoxItem item || item.Content is not string theme)
        {
            return;
        }

        try
        {
            ThemeManager.SetTheme(Application.Current, theme);
            AppearanceSelectionText.Text = theme;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Nova could not save the appearance setting.{Environment.NewLine}{exception.Message}",
                "Appearance setting could not be saved", MessageBoxButton.OK, MessageBoxImage.Error);
            AppearanceComboBox.SelectedIndex = ThemeManager.CurrentTheme == "Dark" ? 1 : 0;
            AppearanceSelectionText.Text = ThemeManager.CurrentTheme;
        }
    }

    public void ShowUpdateReleaseNotes(string releaseName, string version, string releaseNotes)
    {
        if (Version.TryParse(version, out var parsedVersion))
        {
            ShowLatestReleaseNotes(new AppReleaseNotes(parsedVersion, $"v{version}", releaseName, releaseNotes));
        }

        MainTabs.SelectedIndex = 4;
        StatusText.Text = $"Updated to Nova {version}. The latest changelog is shown in Settings.";
        _ = RefreshLatestReleaseNotesAsync();
    }

    public async Task RefreshLatestReleaseNotesAsync()
    {
        if (isLoadingLatestReleaseNotes)
        {
            return;
        }

        isLoadingLatestReleaseNotes = true;
        LatestReleaseNotesStatusText.Text = "Checking GitHub for the latest release notes…";
        try
        {
            var releaseNotes = await AppUpdateService.GetLatestReleaseNotesAsync(CancellationToken.None);
            ShowLatestReleaseNotes(releaseNotes);
            var warning = AppUpdateService.ApiWarningMessage;
            LatestReleaseNotesStatusText.Text = string.IsNullOrWhiteSpace(warning)
                ? $"Latest published release: {releaseNotes.Tag}."
                : $"Latest published release: {releaseNotes.Tag}. {warning}";
        }
        catch (Exception exception)
        {
            LatestReleaseNotesStatusText.Text =
                $"Could not refresh the changelog from GitHub. Showing bundled Nova {AppUpdateService.CurrentVersion} notes instead. {exception.Message}";
        }
        finally
        {
            isLoadingLatestReleaseNotes = false;
        }
    }

    private void ShowLatestReleaseNotes(AppReleaseNotes releaseNotes)
    {
        LatestReleaseNotesTitleText.Text = releaseNotes.ReleaseName;
        LatestReleaseNotesText.Text = releaseNotes.Notes;
        LatestReleaseNotesPanel.Visibility = Visibility.Visible;
    }

    private async Task CheckForAppUpdatesAsync(bool forceRefresh = false)
    {
        if (isBusy)
        {
            return;
        }

        availableAppUpdate = null;
        InstallAppUpdateButton.IsEnabled = false;
        AppUpdateStatusText.Text = "Checking the public GitHub release…";
        SetBusy(true, "Checking Nova updates…");
        try
        {
            await RefreshUpdateAvailabilityAsync(forceRefresh);
        }
        catch (Exception exception)
        {
            AppUpdateStatusText.Text = $"Could not check for updates: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Could not check for Nova updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, AppUpdateStatusText.Text);
        }
    }

    private async void UpdateCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (isBusy)
        {
            return;
        }

        await CheckLiveUpdateAvailabilityAsync();
    }

    private async Task CheckLiveUpdateAvailabilityAsync()
    {
        try
        {
            await RefreshUpdateAvailabilityAsync();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or
                InvalidOperationException or InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            StatusText.Text = "GitHub is temporarily unavailable. Nova will retry automatically.";
        }
    }

    private async Task RefreshUpdateAvailabilityAsync(bool forceRefresh = false)
    {
        var updateTask = AppUpdateService.CheckAsync(CancellationToken.None, forceRefresh);
        var missedCountTask = AppUpdateService.GetMissedReleaseCountAsync(CancellationToken.None, forceRefresh);
        await Task.WhenAll(updateTask, missedCountTask);

        availableAppUpdate = await updateTask;
        var missedCount = await missedCountTask;
        UpdateBadgeText.Text = missedCount > 99 ? "+99" : $"+{missedCount}";
        UpdateBadgeBorder.Visibility = missedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsGearButton.ToolTip = missedCount == 0
            ? "Settings"
            : $"Settings — {missedCount} Nova update{(missedCount == 1 ? string.Empty : "s")} available";
        InstallAppUpdateButton.IsEnabled = availableAppUpdate is not null;
        var status = availableAppUpdate is null
            ? $"You’re up to date. Installed version: {AppUpdateService.CurrentVersion}."
            : availableAppUpdate.Version == AppUpdateService.CurrentVersion
                ? $"A hotfix for Nova {availableAppUpdate.Version} is available (release {availableAppUpdate.Tag}). Download it when you’re ready."
                : $"Version {availableAppUpdate.Version} is available (release {availableAppUpdate.Tag}). Download it when you’re ready.";
        var apiWarning = AppUpdateService.ApiWarningMessage;
        AppUpdateStatusText.Text = string.IsNullOrWhiteSpace(apiWarning) ? status : $"{status} {apiWarning}";

        if (availableAppUpdate is not null)
        {
            updateNotificationService.NotifyIfNew(availableAppUpdate);
        }
    }

    private async void InstallAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy || availableAppUpdate is null)
        {
            return;
        }

        SetBusy(true, $"Downloading Nova {availableAppUpdate.Version}…");
        try
        {
            var release = availableAppUpdate;
            AppUpdateStatusText.Text = $"Downloading and verifying Nova {release.Version}…";
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Nova could not determine its executable path.");
            var stagePath = await AppUpdateService.DownloadAndVerifyAsync(release, CancellationToken.None);
            AppUpdateInstaller.StartUpdater(stagePath, executablePath, Environment.ProcessId, release);
            AppUpdateStatusText.Text = "Update verified. Nova is closing to install it, then will restart.";
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            AppUpdateStatusText.Text = $"Update could not be installed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Could not install Nova update",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, AppUpdateStatusText.Text);
        }
    }

    private static async Task ObserveScanTasksAsync(
        Task<InstalledScanResult> inventoryTask,
        Task<IReadOnlyList<UpdateCandidate>> updateTask,
        Task<TempScanResult> tempTask)
    {
        try
        {
            await Task.WhenAll(inventoryTask, updateTask, tempTask);
        }
        catch
        {
        }
    }

    private static void AddTaskError<T>(List<string> errors, Task<T> task, string fallback)
    {
        if (task.Exception is not null)
        {
            errors.Add(task.Exception.GetBaseException().Message);
        }
        else if (task.IsCanceled)
        {
            errors.Add($"{fallback} The scan was canceled.");
        }
        else
        {
            errors.Add(fallback);
        }
    }

    private static void ThrowScanWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, warnings));
        }
    }

    private void SetSoftware(IEnumerable<InstalledSoftware> items)
    {
        software.Clear();
        foreach (var item in items)
        {
            software.Add(item);
        }
    }

    private void SetUpdates(IEnumerable<UpdateCandidate> items)
    {
        updates.Clear();
        foreach (var item in items)
        {
            updates.Add(item);
        }
    }

    private void SetTempScan(TempScanResult result)
    {
        lastTempScan = result;
        tempFolders.Clear();
        tempFiles.Clear();
        cleanupCategories.Clear();
        selectedCleanupCategories.Clear();
        foreach (var folder in result.Folders)
        {
            tempFolders.Add(folder);
            foreach (var file in folder.ScannedFiles)
            {
                tempFiles.Add(file);
            }
        }

        BuildCleanupCategories(result.Folders);
        CleanupStatusText.Text = result.Errors.Count == 0
            ? string.Empty
            : "Scan warnings:" + Environment.NewLine + string.Join(Environment.NewLine, result.Errors);
        UpdateTempSelectionState();
    }

    private void BuildCleanupCategories(IReadOnlyList<TempFolderResult> scannedFolders)
    {
        foreach (var location in TempCleaner.GetCleanupLocations())
        {
            var folder = scannedFolders.FirstOrDefault(scanned =>
                scanned.Path.Equals(location.Path, StringComparison.OrdinalIgnoreCase));
            AddScannedCategory(location.Name, location.Description, location.Icon, location.Path, folder);
        }
    }

    private void AddScannedCategory(
        string name,
        string description,
        string icon,
        string approvedPath,
        TempFolderResult? folder)
    {
        var available = folder is not null;
        var path = folder?.Path ?? approvedPath;
        var files = folder?.ScannedFiles ?? Array.Empty<TempFileResult>();
        foreach (var file in files)
        {
            file.IsSelected = true;
        }

        cleanupCategories.Add(new TempCleanupCategory(
            name,
            icon,
            description,
            path,
            files,
            available ? (folder!.Files == 0 ? "No files found" : "Scanned") : "Location not found",
            available));
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: UpdateCandidate update } || isBusy)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Start the winget update for {update.Name} ({update.CurrentVersion} → {update.AvailableVersion})?{Environment.NewLine}{Environment.NewLine}" +
            "The installer may ask questions or request administrator permission. No silent install is requested.",
            "Confirm update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, $"Starting update for {update.Name}…");
        try
        {
            var exitCode = await SoftwareScanner.RunUpdateAsync(update, CancellationToken.None);
            if (exitCode == 0)
            {
                MessageBox.Show(this, $"{update.Name} update process finished successfully.",
                    "Update finished", MessageBoxButton.OK, MessageBoxImage.Information);
                await ScanAfterUpdateAsync();
            }
            else
            {
                MessageBox.Show(this,
                    $"The update process for {update.Name} exited with code {exitCode}. Check the winget window for details.",
                    "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = $"Update failed for {update.Name} (exit {exitCode}).";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not update {update.Name}.";
            MessageBox.Show(this, exception.Message, "Update error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task ScanAfterUpdateAsync()
    {
        var results = await SoftwareScanner.FindUpdatesAsync(CancellationToken.None);
        SetUpdates(results);
        RefreshCounts();
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = tempFiles.Where(file => file.IsSelected).ToArray();
        if (isBusy || lastTempScan is null || selectedFiles.Length == 0)
        {
            return;
        }

        var scanned = lastTempScan;
        var selectedByFolder = scanned.Folders
            .Select(folder => new
            {
                Folder = folder,
                Files = folder.ScannedFiles.Where(file => file.IsSelected).ToArray()
            })
            .Where(entry => entry.Files.Length > 0)
            .ToArray();
        var folderDetails = string.Join(Environment.NewLine, selectedByFolder.Select(entry =>
            $"- {entry.Folder.Path}: {entry.Files.Length:N0} selected file(s), {FormatBytes(entry.Files.Sum(file => file.Bytes))}"));
        var filePreview = string.Join(Environment.NewLine, selectedFiles.Take(12).Select(file => $"  {file.Path}"));
        if (selectedFiles.Length > 12)
        {
            filePreview += Environment.NewLine + $"  …and {selectedFiles.Length - 12:N0} more selected file(s).";
        }

        var selectedBytes = selectedFiles.Sum(file => file.Bytes);
        var answer = MessageBox.Show(this,
            $"Clean up the {selectedFiles.Length:N0} checked file(s) ({FormatBytes(selectedBytes)})?{Environment.NewLine}{Environment.NewLine}" +
            $"{folderDetails}{Environment.NewLine}{Environment.NewLine}{filePreview}{Environment.NewLine}{Environment.NewLine}" +
            "Only checked files recorded by this scan are attempted. Close Edge or Chrome first to allow more browser-cache files to be removed. Files that changed since the scan, locked or protected files, and linked items are skipped. Folders are never removed.",
            "Confirm cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "Cleaning up selected files…");
        try
        {
            var selectedPaths = selectedFiles.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedFolders = scanned.Folders
                .Select(folder => folder with
                {
                    ScannedFiles = folder.ScannedFiles.Where(file => selectedPaths.Contains(file.Path)).ToArray()
                })
                .Where(folder => folder.ScannedFiles.Count > 0)
                .ToArray();
            var result = await Task.Run(() => TempCleaner.RemoveContents(selectedFolders, CancellationToken.None));
            CleanupStatusText.Text =
                $"Removed {result.DeletedFiles:N0} files ({FormatBytes(result.DeletedBytes)}). {result.Skipped:N0} item(s) were skipped.";
            StatusText.Text = "File cleanup finished.";

            var freshScan = await Task.Run(() => TempCleaner.Scan(CancellationToken.None));
            SetTempScan(freshScan);
            RefreshCounts();
            var report = result.Errors
                .Concat(freshScan.Errors.Select(error => $"Rescan: {error}"))
                .Take(10)
                .ToArray();
            if (report.Length > 0)
            {
                CleanupStatusText.Text += Environment.NewLine + string.Join(Environment.NewLine, report);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "Cleanup did not complete.";
            CleanupStatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "Cleanup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private void BrowseTempFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TempFolderResult folder })
        {
            OpenFolderInExplorer(folder.Path, TempCleaner.IsApprovedRoot, true);
        }
    }

    private void TempFileCheckBox_Click(object sender, RoutedEventArgs e) => UpdateTempSelectionState();

    private void UpdateTempSelectionState()
    {
        if (isUpdatingTempSelection)
        {
            return;
        }

        isUpdatingTempSelection = true;
        foreach (var category in cleanupCategories.Where(category => category.IsAvailable))
        {
            var allSelected = category.FileCount > 0 && category.Files.All(file => file.IsSelected);
            category.IsSelected = allSelected;
            category.RefreshSelectionSummary();
        }

        var selected = tempFiles.Where(file => file.IsSelected).ToArray();
        var totalBytes = selected.Sum(file => file.Bytes);
        selectedCleanupCategories.Clear();
        foreach (var category in cleanupCategories.Where(category => category.SelectedFileCount > 0))
        {
            selectedCleanupCategories.Add(category);
        }

        TempSelectionSummaryText.Text = tempFiles.Count == 0
            ? "Scan to review supported temporary and cache locations."
            : $"{selected.Length:N0} of {tempFiles.Count:N0} file(s) selected · {FormatBytes(totalBytes)} selected.";
        SelectedTempSizeText.Text = FormatBytes(totalBytes);
        SelectedTempCountText.Text = $"{selected.Length:N0} files · {selectedCleanupCategories.Count:N0} locations selected";
        SelectedTempCategoriesText.Text = selectedCleanupCategories.Count == 0
            ? "No categories selected"
            : $"{selected.Length:N0} file(s) selected from {selectedCleanupCategories.Count:N0} location(s)";
        CleanButton.Content = "Cleanup files";
        CleanButton.IsEnabled = !isBusy && selected.Length > 0;
        isUpdatingTempSelection = false;
    }

    private void CleanupCategoryCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: TempCleanupCategory category } || !category.IsAvailable)
        {
            return;
        }

        isUpdatingTempSelection = true;
        foreach (var file in category.Files)
        {
            file.IsSelected = category.IsSelected;
        }

        isUpdatingTempSelection = false;
        UpdateTempSelectionState();
    }

    private void TempSection_Click(object sender, RoutedEventArgs e) => ShowStoragePanel(TempCleanupPanel);
    private void FolderMapSection_Click(object sender, RoutedEventArgs e) => ShowStoragePanel(FolderMapPanel);
    private void ShortcutsSection_Click(object sender, RoutedEventArgs e) => ShowStoragePanel(ShortcutsPanel);

    private void ShowStoragePanel(FrameworkElement selectedPanel)
    {
        TempCleanupPanel.Visibility = selectedPanel == TempCleanupPanel ? Visibility.Visible : Visibility.Collapsed;
        FolderMapPanel.Visibility = selectedPanel == FolderMapPanel ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = selectedPanel == ShortcutsPanel ? Visibility.Visible : Visibility.Collapsed;
        SetStorageSectionButtonState(TempSectionButton, selectedPanel == TempCleanupPanel);
        SetStorageSectionButtonState(FolderMapSectionButton, selectedPanel == FolderMapPanel);
        SetStorageSectionButtonState(ShortcutsSectionButton, selectedPanel == ShortcutsPanel);
        if (!isBusy && MainTabs.SelectedIndex == 3)
        {
            ScanButton.Content = GetScanButtonText();
        }
    }

    private static void SetStorageSectionButtonState(Button button, bool isSelected)
    {
        button.Background = isSelected
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(86, 105, 232))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 243, 248));
        button.Foreground = isSelected
            ? System.Windows.Media.Brushes.White
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(83, 98, 122));
    }

    private void ToggleTempFiles_Click(object sender, RoutedEventArgs e)
    {
        showTempFileDetails = !showTempFileDetails;
        TempFilesGrid.Visibility = showTempFileDetails ? Visibility.Visible : Visibility.Collapsed;
        ToggleTempFilesButton.Content = showTempFileDetails ? "Hide file list" : "View all files";
    }

    private void BrowseCleanupCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TempCleanupCategory { IsAvailable: true } category })
        {
            OpenFolderInExplorer(category.Path, TempCleaner.IsApprovedRoot, true);
        }
    }

    private void AddFolderRootOptions()
    {
        foreach (var location in StorageExplorer.GetQuickLocations())
        {
            FolderRootCombo.Items.Add(location);
        }

        FolderRootCombo.SelectedIndex = FolderRootCombo.Items.Count > 0 ? 0 : -1;
        FolderPathTextBox.Text = StorageExplorer.ThisPcPath;
    }

    private async void ScanFolderMap_Click(object sender, RoutedEventArgs e) =>
        await RunScanAsync(ScanFolderMapAsync);

    private async Task ScanFolderMapAsync()
    {
        var path = GetFolderMapPath();
        storageFolders.Clear();
        StorageMapStatusText.Text = $"Listing the contents of {DisplayFolderPath(path)}…";
        var result = await Task.Run(() => StorageExplorer.List(path, CancellationToken.None));
        ShowStorageResults(result, path, false);
    }

    private void FolderRootCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == FolderRootCombo && FolderRootCombo.SelectedItem is StorageRootOption option)
        {
            folderMapHistory.Clear();
            FolderPathTextBox.Text = option.IsThisPc ? StorageExplorer.ThisPcPath : option.Path;
        }
    }

    private void StorageFoldersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StorageFoldersGrid.SelectedItem is StorageEntryInfo entry)
        {
            StorageMapStatusText.Text = $"{entry.Type}: {entry.Path}";
        }
    }

    private void BrowseStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StorageEntryInfo entry })
        {
            if (entry.Path == StorageExplorer.ThisPcPath)
            {
                OpenThisPcInExplorer();
            }
            else
            {
                OpenFolderInExplorer(entry.Path, _ => true, Directory.Exists(entry.Path));
            }
        }
    }

    private async void EnterStorageEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StorageEntryInfo { CanEnter: true } entry })
        {
            await NavigateFolderMapAsync(entry.Path);
        }
    }

    private async void StorageFoldersGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (StorageFoldersGrid.SelectedItem is StorageEntryInfo { CanEnter: true } entry)
        {
            await NavigateFolderMapAsync(entry.Path);
        }
    }

    private async Task NavigateFolderMapAsync(string path)
    {
        if (isBusy)
        {
            return;
        }

        if (!string.Equals(currentFolderMapPath, path, StringComparison.OrdinalIgnoreCase))
        {
            folderMapHistory.Push(currentFolderMapPath);
        }

        FolderPathTextBox.Text = path;
        await RunScanAsync(ScanFolderMapAsync);
    }

    private async void FolderMapUp_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            return;
        }

        var current = GetFolderMapPath();
        if (folderMapHistory.Count > 0)
        {
            FolderPathTextBox.Text = folderMapHistory.Pop();
        }
        else if (current == StorageExplorer.ThisPcPath)
        {
            return;
        }
        else
        {
            var root = Path.GetPathRoot(Path.GetFullPath(current));
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                FolderPathTextBox.Text = StorageExplorer.ThisPcPath;
            }
            else
            {
                FolderPathTextBox.Text = Directory.GetParent(Path.TrimEndingDirectorySeparator(current))?.FullName
                    ?? StorageExplorer.ThisPcPath;
            }
        }

        await RunScanAsync(ScanFolderMapAsync);
    }

    private string GetFolderMapPath()
    {
        var typedPath = Environment.ExpandEnvironmentVariables(FolderPathTextBox.Text.Trim().Trim('"'));
        if (typedPath.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
            typedPath.Equals(StorageExplorer.ThisPcPath, StringComparison.OrdinalIgnoreCase))
        {
            return StorageExplorer.ThisPcPath;
        }

        if (string.IsNullOrWhiteSpace(typedPath))
        {
            throw new InvalidOperationException("Choose a location or type a folder path.");
        }

        return Path.GetFullPath(typedPath);
    }

    private static string DisplayFolderPath(string path) =>
        path == StorageExplorer.ThisPcPath ? "This PC" : path;

    private void ShowStorageResults(StorageScanResult result, string path, bool isSearch)
    {
        storageFolders.Clear();
        StorageFoldersGrid.SelectedItem = null;
        foreach (var entry in result.Entries)
        {
            storageFolders.Add(entry);
        }

        currentFolderMapPath = path;
        StorageMapStatusText.Text = isSearch
            ? $"{result.Entries.Count:N0} match(es) under {DisplayFolderPath(path)}. Search does not follow linked folders."
            : $"{result.Entries.Count:N0} item(s) in {DisplayFolderPath(path)}. Folders open inside Nova; Open folder launches Explorer. No deletion is available here.";
        if (result.Warnings.Count > 0)
        {
            StorageMapStatusText.Text += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Take(12));
        }
    }

    private async void SearchFolderMap_Click(object sender, RoutedEventArgs e) =>
        await SearchFolderMapAsync();

    private async void FolderSearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await SearchFolderMapAsync();
        }
    }

    private async Task SearchFolderMapAsync()
    {
        if (isBusy)
        {
            return;
        }

        var path = GetFolderMapPath();
        var query = FolderSearchTextBox.Text.Trim();
        folderSearchCancellation = new CancellationTokenSource();
        CancelFolderSearchButton.IsEnabled = true;
        SetBusy(true, $"Searching for “{query}”… This can take time on large drives.");
        try
        {
            var result = await Task.Run(
                () => StorageExplorer.Search(path, query, folderSearchCancellation.Token),
                folderSearchCancellation.Token);
            ShowStorageResults(result, path, true);
            StatusText.Text = $"Folder search finished ({result.Entries.Count:N0} match(es)).";
        }
        catch (OperationCanceledException)
        {
            StorageMapStatusText.Text = "Folder search canceled. Partial results are not shown.";
            StatusText.Text = "Folder search canceled.";
        }
        catch (Exception exception)
        {
            StorageMapStatusText.Text = exception.Message;
            StatusText.Text = "Folder search failed.";
            MessageBox.Show(this, exception.Message, "Folder search failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            folderSearchCancellation.Dispose();
            folderSearchCancellation = null;
            CancelFolderSearchButton.IsEnabled = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private void CancelFolderSearch_Click(object sender, RoutedEventArgs e) =>
        folderSearchCancellation?.Cancel();

    private void OpenThisPcInExplorer()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:MyComputerFolder",
                UseShellExecute = true
            });
            if (process is null)
            {
                throw new InvalidOperationException("File Explorer did not open This PC.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open This PC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadShortcutsFolder()
    {
        try
        {
            shortcutsFolder = ShortcutScanner.LoadFolder();
            ShortcutsFolderPathText.Text = shortcutsFolder ?? "No shortcuts folder selected.";
            ShortcutsStatusText.Text = shortcutsFolder is null
                ? "Choose your shortcuts folder. This setting is stored locally for the current Windows user."
                : "Folder remembered. Select Scan shortcuts to review its .lnk files.";
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ShortcutsFolderPathText.Text = "Could not read the saved folder setting.";
            ShortcutsStatusText.Text = exception.Message;
        }
    }

    private void ChooseShortcutsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your games and software shortcuts folder",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(shortcutsFolder) && Directory.Exists(shortcutsFolder))
        {
            dialog.InitialDirectory = shortcutsFolder;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            shortcutsFolder = Path.GetFullPath(dialog.FolderName);
            ShortcutScanner.SaveFolder(shortcutsFolder);
            ShortcutsFolderPathText.Text = shortcutsFolder;
            ShortcutsStatusText.Text = "Folder remembered. Scan to review shortcut names and targets. Nothing will be moved or deleted.";
            shortcuts.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, exception.Message, "Could not save shortcuts folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ScanShortcuts_Click(object sender, RoutedEventArgs e) =>
        await RunScanAsync(ScanShortcutsAsync);

    private async Task ScanShortcutsAsync()
    {
        if (string.IsNullOrWhiteSpace(shortcutsFolder))
        {
            throw new InvalidOperationException("Choose your shortcuts folder first.");
        }

        var results = await Task.Run(() => ShortcutScanner.Scan(shortcutsFolder));
        shortcuts.Clear();
        foreach (var shortcut in results)
        {
            shortcuts.Add(shortcut);
        }

        ShortcutsStatusText.Text = $"{results.Count:N0} shortcut(s) found in {shortcutsFolder}. Target status is a clue, not proof of app health.";
        StatusText.Text = "Shortcut scan complete.";
    }

    private void BrowseShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShortcutInfo shortcut })
        {
            OpenFolderInExplorer(shortcut.Path, path =>
                !string.IsNullOrWhiteSpace(shortcutsFolder) &&
                IsWithinDirectory(path, shortcutsFolder), false);
        }
    }

    private void ShortcutsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShortcutsGrid.SelectedItem is ShortcutInfo shortcut)
        {
            ShortcutsStatusText.Text = $"{shortcut.Status}: {shortcut.Explanation} Target: {shortcut.Target}";
        }
    }

    private void OpenFolderInExplorer(string path, Func<string, bool> isAllowed, bool openFolder)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!isAllowed(fullPath) || (!Directory.Exists(fullPath) && !File.Exists(fullPath)))
            {
                throw new InvalidOperationException("The selected path is no longer available or is outside the scanned location.");
            }

            var arguments = openFolder ? $"\"{fullPath}\"" : $"/select,\"{fullPath}\"";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
            if (process is null)
            {
                throw new InvalidOperationException("File Explorer did not open the selected path.");
            }

            StatusText.Text = $"Opened {fullPath}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(this, exception.Message, "Could not open path", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private void RefreshCounts()
    {
        InstalledCountText.Text = software.Count.ToString("N0");
        UpdatesCountText.Text = updates.Count.ToString("N0");
        HeroTitle.Text = updates.Count == 0
            ? "No winget updates found"
            : $"{updates.Count:N0} update{(updates.Count == 1 ? string.Empty : "s")} ready to review";
        UpdatesSummaryText.Text = updates.Count == 0
            ? "No updates were reported by the winget source."
            : $"{updates.Count:N0} available update(s) from the winget source.";

        var scannedFiles = lastTempScan?.FileCount ?? 0;
        var scannedBytes = lastTempScan?.TotalBytes ?? 0;
        TempSizeText.Text = lastTempScan is null ? "—" : FormatBytes(scannedBytes);
        TempCountText.Text = lastTempScan is null
            ? "Scan to estimate"
            : $"{scannedFiles:N0} file(s) found";
        TempSummaryText.Text = lastTempScan is null
            ? "Run a scan to see the removable file estimate."
            : $"{scannedFiles:N0} file(s), {FormatBytes(scannedBytes)} estimated. Files may be locked or protected.";
        UpdateTempSelectionState();
    }

    private void SetBusy(bool busy, string status)
    {
        isBusy = busy;
        ScanButton.IsEnabled = !busy;
        ScanButton.Content = busy ? "Scanning…" : GetScanButtonText();
        TempScanButton.IsEnabled = !busy;
        FolderMapScanButton.IsEnabled = !busy;
        ShortcutsScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && tempFiles.Any(file => file.IsSelected);
        CheckAppUpdateButton.IsEnabled = !busy;
        InstallAppUpdateButton.IsEnabled = !busy && availableAppUpdate is not null;
        StatusText.Text = status;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MainTabs)
        {
            UpdateSectionChrome();
        }
    }

    private void UpdateSectionChrome()
    {
        if (!IsInitialized)
        {
            return;
        }

        PageTitle.Text = MainTabs.SelectedIndex switch
        {
            0 => $"Good morning, {Environment.UserName}",
            1 => "My software",
            2 => "Updates",
            3 => "Storage & cleanup",
            4 => "Settings",
            _ => "Nova Software Manager"
        };
        PageSubtitle.Text = MainTabs.SelectedIndex switch
        {
            0 => "Your software at a glance. Updates and cleanup stay in your control.",
            1 => "Browse applications and games found on this PC.",
            2 => "Review update candidates from the winget source.",
            3 => "Explore storage, temporary files, and your software shortcuts.",
            4 => "Manage Nova application updates.",
            _ => string.Empty
        };

        SetNavButtonState(OverviewNavButton, MainTabs.SelectedIndex == 0);
        SetNavButtonState(SoftwareNavButton, MainTabs.SelectedIndex == 1);
        SetNavButtonState(UpdatesNavButton, MainTabs.SelectedIndex == 2);
        SetNavButtonState(CleanupNavButton, MainTabs.SelectedIndex == 3);
        SetNavButtonState(SettingsGearButton, MainTabs.SelectedIndex == 4);
        if (!isBusy)
        {
            ScanButton.Content = GetScanButtonText();
        }

        if (MainTabs.SelectedIndex == 4)
        {
            _ = RefreshLatestReleaseNotesAsync();
        }
    }

    private static void SetNavButtonState(Button button, bool selected)
    {
        button.Background = selected ? new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(86, 105, 232)) : System.Windows.Media.Brushes.Transparent;
        button.Foreground = selected ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(174, 185, 203));
    }

    private string GetScanButtonText() => MainTabs.SelectedIndex switch
    {
        0 => "Scan this PC",
        1 => "Scan software",
        2 => "Scan updates",
        3 when FolderMapPanel.Visibility == Visibility.Visible => "Scan folder",
        3 when ShortcutsPanel.Visibility == Visibility.Visible => "Scan shortcuts",
        3 => "Scan temporary folders",
        4 => "Check for updates",
        _ => "Scan"
    };

    private string GetSectionName() => MainTabs.SelectedIndex switch
    {
        0 => "PC",
        1 => "software",
        2 => "updates",
        3 when FolderMapPanel.Visibility == Visibility.Visible => "folder",
        3 when ShortcutsPanel.Visibility == Visibility.Visible => "shortcuts",
        3 => "temporary folders",
        4 => "app updates",
        _ => "section"
    };

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:0.#} {suffixes[suffix]}";
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void SoftwareNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void UpdatesNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
    private void CleanupNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
}

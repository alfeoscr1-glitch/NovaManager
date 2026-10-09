using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;
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
    private readonly ObservableCollection<DashboardSpecification> dashboardSpecifications = new();
    private readonly ObservableCollection<DashboardDrive> dashboardDrives = new();
    private UserPreferences preferences;
    private readonly ObservableCollection<StorageEntryInfo> storageFolders = new();
    private readonly ObservableCollection<ShortcutInfo> shortcuts = new();
    private readonly UpdateNotificationService updateNotificationService = new();
    private readonly DispatcherTimer updateCheckTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly List<DashboardMetrics> dashboardSamples = new();
    private CancellationTokenSource? dashboardMonitoringCancellation;
    private TimeSpan dashboardChartRange = TimeSpan.FromHours(1);
    private DashboardSystemInfo? dashboardSystemInfo;
    private DateTime lastDashboardChartUpdateAt = DateTime.MinValue;
    private DateTime lastDashboardHistoryPruneAt = DateTime.MinValue;
    private DateTime? lastAppUpdateCheckAt;
    private AppUpdateRelease? availableAppUpdate;
    private TempScanResult? lastTempScan;
    private int? rememberedTempFileCount;
    private long? rememberedTempBytes;
    private DateTime? lastScanAt;
    private DateTime? lastCleanupAt;
    private string? lastCleanupSummary;
    private bool isBusy;
    private bool isUpdatingTempSelection;
    private bool showTempFileDetails;
    private bool hasInstalledScan;
    private bool hasUpdateScan;
    private bool hasTempScan;
    private bool isWindowLoaded;
    private bool isDashboardMonitoringActive;
    private bool isChangingDeveloperMode;
    private string? shortcutsFolder;
    private readonly Stack<string> folderMapHistory = new();
    private CancellationTokenSource? folderSearchCancellation;
    private string currentFolderMapPath = StorageExplorer.ThisPcPath;

    public MainWindow(bool openSettings = false)
    {
        InitializeComponent();
        try
        {
            preferences = UserPreferencesService.Load();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Nova could not load some saved behaviour settings. Default settings will be used for this session. {exception.Message}",
                "Saved settings unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            preferences = new UserPreferences(false, false, true, null);
        }

        lastAppUpdateCheckAt = preferences.LastAppUpdateCheckAt;
        MainTabs.SelectedIndex = openSettings ? 4 : 0;

        DataContext = new
        {
            Software = software,
            Updates = updates,
            TempFolders = tempFolders,
            TempFiles = tempFiles,
            CleanupCategories = cleanupCategories,
            SelectedCleanupCategories = selectedCleanupCategories,
            DashboardSpecifications = dashboardSpecifications,
            DashboardDrives = dashboardDrives,
            StorageFolders = storageFolders,
            Shortcuts = shortcuts
        };
        AddFolderRootOptions();
        LoadShortcutsFolder();
        AppearanceComboBox.SelectedIndex = ThemeManager.CurrentTheme == "Dark" ? 1 : 0;
        var currentVersion = AppUpdateService.CurrentVersion.ToString(3);
        var releaseLabel = AppUpdateService.GetBundledReleaseNotes(AppUpdateService.CurrentVersion).ReleaseName;
        var releaseType = releaseLabel[(releaseLabel.LastIndexOf('(') + 1)..].TrimEnd(')');
        var shortReleaseType = releaseType.Equals("Developer release", StringComparison.OrdinalIgnoreCase)
            ? "Dev"
            : releaseType;
        SettingsVersionText.Text = $"v{currentVersion} • {shortReleaseType}";
        AboutVersionText.Text = $"v{currentVersion} ({shortReleaseType})";
        AboutArchitectureText.Text = Environment.Is64BitProcess ? "64-bit" : "32-bit";
        SidebarVersionText.Text = $"Nova v{AppUpdateService.CurrentVersion}";
        MinimizeToTrayToggle.IsChecked = preferences.MinimizeToTray;
        StartWithWindowsToggle.IsChecked = preferences.StartWithWindows;
        StartupNotificationToggle.IsChecked = preferences.StartupNotification;
        UpdateLastUpdateCheckDisplay();
        try
        {
            SetDeveloperModeState(DeveloperModeService.IsAuthorized());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or CryptographicException)
        {
            MessageBox.Show(this, $"Saved Developer Mode access could not be read.{Environment.NewLine}{exception.Message}",
                "Developer Mode", MessageBoxButton.OK, MessageBoxImage.Error);
            SetDeveloperModeState(false);
        }
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            updateCheckTimer.Stop();
            dashboardMonitoringCancellation?.Cancel();
            updateNotificationService.Dispose();
        };
        updateNotificationService.NotificationClicked += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
            MainTabs.SelectedIndex = 4;
        });
        updateNotificationService.MainWindowExitRequested += (_, _) => Application.Current.Shutdown();
        updateCheckTimer.Tick += UpdateCheckTimer_Tick;
        StateChanged += MainWindow_StateChanged;
        ShowStoragePanel(TempCleanupPanel);
        UpdateSectionChrome();
        RestoreRememberedScan();
    }

    private void RestoreRememberedScan()
    {
        var snapshot = ScanMemory.Load();
        if (snapshot is null)
        {
            return;
        }

        SetSoftware(snapshot.Software ?? []);
        SetUpdates(snapshot.Updates ?? []);
        hasInstalledScan = snapshot.Software is not null;
        hasUpdateScan = snapshot.Updates is not null;
        rememberedTempFileCount = snapshot.TempFileCount;
        rememberedTempBytes = snapshot.TempBytes;
        hasTempScan = snapshot.TempBytes is not null;
        lastScanAt = snapshot.LastScanAt;
        lastCleanupAt = snapshot.LastCleanupAt;
        lastCleanupSummary = snapshot.LastCleanupSummary;
        if (lastScanAt is DateTime scannedAt)
        {
            LastScanText.Text = scannedAt.ToString("g");
        }

        if (lastCleanupSummary is not null && lastCleanupAt is DateTime cleanedAt)
        {
            CleanupStatusText.Text = $"Last cleanup ({cleanedAt:g}): {lastCleanupSummary}";
        }

        RefreshCounts();
    }

    private void RememberScan()
    {
        ScanMemory.Save(new ScanSnapshot(
            lastScanAt,
            software.ToList(),
            updates.ToList(),
            lastTempScan?.FileCount ?? rememberedTempFileCount,
            lastTempScan?.TotalBytes ?? rememberedTempBytes,
            lastCleanupAt,
            lastCleanupSummary));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        isWindowLoaded = true;
        SetDashboardMonitoring(MainTabs.SelectedIndex == 0);
        _ = LoadDashboardSystemInfoAsync();
        if (preferences.StartupNotification)
        {
            updateNotificationService.ShowStartupNotification();
        }

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

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && preferences.MinimizeToTray)
        {
            updateNotificationService.SetMainWindowMinimized(true);
            Hide();
            return;
        }

        updateNotificationService.SetMainWindowMinimized(false);
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
            lastScanAt = DateTime.Now;
            LastScanText.Text = lastScanAt.Value.ToString("g");
            DashboardLastScanText.Text = lastScanAt.Value.ToString("g");
            RememberScan();
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
        hasInstalledScan = false;
        hasUpdateScan = false;
        hasTempScan = false;
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
            hasInstalledScan = true;
            errors.AddRange(inventoryTask.Result.Warnings);
        }
        else
        {
            AddTaskError(errors, inventoryTask, "Installed software scan failed.");
        }

        if (updateTask.IsCompletedSuccessfully)
        {
            SetUpdates(updateTask.Result);
            hasUpdateScan = true;
        }
        else
        {
            AddTaskError(errors, updateTask, "winget update scan failed.");
        }

        if (tempTask.IsCompletedSuccessfully)
        {
            SetTempScan(tempTask.Result);
            hasTempScan = true;
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
        hasInstalledScan = false;
        RefreshCounts();
        var result = await SoftwareScanner.FindInstalledAsync(CancellationToken.None);
        SetSoftware(result.Items);
        hasInstalledScan = true;
        RefreshCounts();
        ThrowScanWarnings(result.Warnings);
    }

    private async Task ScanUpdatesAsync()
    {
        updates.Clear();
        hasUpdateScan = false;
        RefreshCounts();
        NovaUpdateScanStatusText.Text = "Checking GitHub for the latest Nova release…";
        StoreUpdateScanStatusText.Text = "Checking Microsoft Store app updates…";
        WindowsUpdateScanStatusText.Text = "Checking Windows Update…";
        OpenWindowsUpdateButton.Visibility = Visibility.Collapsed;

        var wingetTask = SoftwareScanner.FindUpdatesAsync(CancellationToken.None);
        var storeTask = SoftwareScanner.FindUpdatesAsync(CancellationToken.None, "msstore");
        var windowsTask = WindowsUpdateService.CheckAvailableAsync();
        var novaTask = RefreshUpdateAvailabilityAsync(forceRefresh: true, notifyIfNew: false);
        await Task.WhenAll(
            ObserveScanTaskAsync(wingetTask),
            ObserveScanTaskAsync(storeTask),
            ObserveScanTaskAsync(windowsTask),
            ObserveScanTaskAsync(novaTask));

        var errors = new List<string>();
        var updateResults = new List<UpdateCandidate>();
        if (wingetTask.IsCompletedSuccessfully)
        {
            updateResults.AddRange(wingetTask.Result);
            hasUpdateScan = true;
        }
        else
        {
            errors.Add(GetScanTaskError(wingetTask, "winget update check failed."));
        }

        if (storeTask.IsCompletedSuccessfully)
        {
            updateResults.AddRange(storeTask.Result);
            hasUpdateScan = true;
            StoreUpdateScanStatusText.Text = storeTask.Result.Count == 0
                ? "No Microsoft Store updates were reported by the Store source."
                : $"{storeTask.Result.Count:N0} Microsoft Store update(s) available.";
        }
        else
        {
            var error = GetScanTaskError(storeTask, "Microsoft Store update check failed.");
            StoreUpdateScanStatusText.Text = error;
            errors.Add(error);
        }

        if (windowsTask.IsCompletedSuccessfully)
        {
            var available = windowsTask.Result.Titles;
            WindowsUpdateScanStatusText.Text = available.Count == 0
                ? "Windows Update reports that your PC is up to date."
                : $"{available.Count:N0} Windows update(s) available: {string.Join("; ", available.Take(4))}" +
                  (available.Count > 4 ? "; …" : string.Empty);
            OpenWindowsUpdateButton.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            var error = GetScanTaskError(windowsTask, "Windows Update check failed.");
            WindowsUpdateScanStatusText.Text = error;
            errors.Add(error);
        }

        if (novaTask.IsCompletedSuccessfully)
        {
            NovaUpdateScanStatusText.Text = availableAppUpdate is null
                ? $"No Nova update available. Installed version: {AppUpdateService.CurrentVersion}."
                : $"Nova {availableAppUpdate.Version} is available (release {availableAppUpdate.Tag}).";
        }
        else
        {
            var error = GetScanTaskError(novaTask, "Nova Manager update check failed.");
            NovaUpdateScanStatusText.Text = error;
            errors.Add(error);
        }

        SetUpdates(updateResults.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase));
        RefreshCounts();
        ThrowScanWarnings(errors);
    }

    private static async Task ObserveScanTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static string GetScanTaskError(Task task, string message)
    {
        var exception = task.Exception?.GetBaseException();
        return exception is null ? message : $"{message} {exception.Message}";
    }

    private void OpenNovaUpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 4;
    }

    private void OpenWindowsUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open Windows Update",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ScanTemporaryFoldersAsync()
    {
        tempFolders.Clear();
        tempFiles.Clear();
        lastTempScan = null;
        hasTempScan = false;
        CleanupStatusText.Text = string.Empty;
        RefreshCounts();
        var result = await Task.Run(() => TempCleaner.Scan(CancellationToken.None));
        SetTempScan(result);
        hasTempScan = true;
        RefreshCounts();
        ThrowScanWarnings(result.Errors);
    }

    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForAppUpdatesAsync(forceRefresh: true);

    private void SuggestFeature_Click(object sender, RoutedEventArgs e) =>
        OpenFeedbackDialog(
            "Suggest a feature",
            "Share an idea or improvement for Nova.",
            "Feature suggestion",
            "enhancement",
            includeDiagnostics: false);

    private void ReportBug_Click(object sender, RoutedEventArgs e) =>
        OpenFeedbackDialog(
            "Report a bug",
            "Describe what happened and what you expected.",
            "Bug report",
            "bug",
            includeDiagnostics: true);

    private void OpenFeedbackDialog(
        string dialogTitle,
        string prompt,
        string issueType,
        string label,
        bool includeDiagnostics)
    {
        var dialog = new FeedbackDialog(
            dialogTitle,
            prompt,
            description => SubmitGitHubFeedbackAsync(description, issueType, label, includeDiagnostics))
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async Task<string> SubmitGitHubFeedbackAsync(
        string description,
        string issueType,
        string label,
        bool includeDiagnostics)
    {
        var body = $"### {issueType}\n\n{description}";
        if (includeDiagnostics)
        {
            body +=
                $"\n\n### Diagnostics\n- Nova version: {AppUpdateService.CurrentVersion}\n- Windows version: {Environment.OSVersion.VersionString}";
        }

        body += "\n\nSubmitted from Nova Manager Feedback & Support. @alfeoscr1-glitch";
        var title = $"[{issueType}] Nova Manager feedback";
        isFeedbackSubmissionInProgress = true;
        try
        {
            return await GitHubFeedbackService.SubmitIssueAsync(
                title,
                body,
                label,
                AuthorizeGitHubFeedbackAsync,
                CancellationToken.None);
        }
        finally
        {
            isFeedbackSubmissionInProgress = false;
        }
    }

    private bool isFeedbackSubmissionInProgress;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!isFeedbackSubmissionInProgress)
        {
            dashboardMonitoringCancellation?.Cancel();
            return;
        }

        e.Cancel = true;
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Nova could not save the appearance setting.{Environment.NewLine}{exception.Message}",
                "Appearance setting could not be saved", MessageBoxButton.OK, MessageBoxImage.Error);
            AppearanceComboBox.SelectedIndex = ThemeManager.CurrentTheme == "Dark" ? 1 : 0;
        }
    }

    private void SettingsCardsScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(scrollViewer.ScrollToTop));
        }
    }

    private void DeveloperMode_Click(object sender, RoutedEventArgs e)
    {
        if (isChangingDeveloperMode || sender is not CheckBox toggle)
        {
            return;
        }

        if (toggle.IsChecked != true)
        {
            SetDeveloperModeState(false);
            return;
        }

        try
        {
            if (!DeveloperModeService.IsAuthorized())
            {
                var passwordBox = new PasswordBox
                {
                    MinWidth = 280,
                    Margin = new Thickness(0, 8, 0, 14),
                    PasswordChar = '●'
                };
                var promptContent = new StackPanel { Margin = new Thickness(22) };
                var promptMessage = new TextBlock
                {
                    Text = "Developer Mode is intended for the Nova developer only. Enter the developer password to unlock this area on this Windows account.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                promptMessage.SetResourceReference(TextBlock.ForegroundProperty, "ThemeBrush_53627A");
                promptContent.Children.Add(promptMessage);
                promptContent.Children.Add(passwordBox);

                var buttons = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };
                var cancelButton = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
                var unlockButton = new Button { Content = "Unlock", IsDefault = true };
                buttons.Children.Add(cancelButton);
                buttons.Children.Add(unlockButton);
                promptContent.Children.Add(buttons);

                var prompt = new Window
                {
                    Title = "Developer Mode",
                    Owner = this,
                    Width = 390,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    Content = promptContent
                };
                prompt.SetResourceReference(Window.BackgroundProperty, "ThemeBrush_FFFFFF");
                unlockButton.Click += (_, _) => prompt.DialogResult = true;
                prompt.Loaded += (_, _) => passwordBox.Focus();
                if (prompt.ShowDialog() != true)
                {
                    passwordBox.Clear();
                    SetDeveloperModeState(false);
                    return;
                }

                var validPassword = DeveloperModeService.TryUnlock(passwordBox.Password);
                passwordBox.Clear();
                if (!validPassword)
                {
                    SetDeveloperModeState(false);
                    MessageBox.Show(this, "The developer password was not accepted.", "Developer Mode",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            SetDeveloperModeState(true);
            DeveloperNotificationStatusText.Text =
                "Developer Mode is unlocked on this Windows account. The local access marker is protected with Windows DPAPI.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or CryptographicException)
        {
            SetDeveloperModeState(false);
            MessageBox.Show(this, $"Developer Mode could not be unlocked.{Environment.NewLine}{exception.Message}",
                "Developer Mode", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetDeveloperModeState(bool enabled)
    {
        isChangingDeveloperMode = true;
        DeveloperModeToggle.IsChecked = enabled;
        DeveloperModeStatusText.Text = enabled
            ? "Developer Mode is enabled"
            : "Developer Mode is disabled";
        DeveloperToolsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        isChangingDeveloperMode = false;
    }

    private void BehaviourSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string preferenceName } toggle)
        {
            return;
        }

        var requestedValue = toggle.IsChecked == true;
        var previousPreferences = preferences;
        try
        {
            if (preferenceName == "StartWithWindows")
            {
                UserPreferencesService.SetStartWithWindows(requestedValue);
            }

            preferences = preferenceName switch
            {
                "MinimizeToTray" => preferences with { MinimizeToTray = requestedValue },
                "StartWithWindows" => preferences with { StartWithWindows = requestedValue },
                "StartupNotification" => preferences with { StartupNotification = requestedValue },
                _ => throw new InvalidOperationException("This Nova preference is not supported.")
            };
            UserPreferencesService.Save(preferences);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            preferences = previousPreferences;
            if (preferenceName == "StartWithWindows")
            {
                try
                {
                    UserPreferencesService.SetStartWithWindows(previousPreferences.StartWithWindows);
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException or InvalidOperationException or
                        System.Security.SecurityException or System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(this,
                        $"{exception.Message}{Environment.NewLine}Nova also could not restore the previous startup setting: {rollbackException.Message}",
                        "Preference could not be saved", MessageBoxButton.OK, MessageBoxImage.Error);
                    SetBehaviourToggle(preferenceName, previousPreferences);
                    return;
                }
            }

            SetBehaviourToggle(preferenceName, previousPreferences);
            MessageBox.Show(this, exception.Message, "Preference could not be saved",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetBehaviourToggle(string preferenceName, UserPreferences value)
    {
        switch (preferenceName)
        {
            case "MinimizeToTray":
                MinimizeToTrayToggle.IsChecked = value.MinimizeToTray;
                break;
            case "StartWithWindows":
                StartWithWindowsToggle.IsChecked = value.StartWithWindows;
                break;
            case "StartupNotification":
                StartupNotificationToggle.IsChecked = value.StartupNotification;
                break;
        }
    }

    private void DeveloperNotificationTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            updateNotificationService.ShowDeveloperTestNotification();
            DeveloperNotificationStatusText.Text =
                "A Windows notification test was sent to this PC. Sending notifications to all Nova installations requires a hosted notification service, which is not configured.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DeveloperNotificationStatusText.Text = $"The local notification test failed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Notification test failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowUpdateReleaseNotes(string releaseName, string version, string releaseNotes)
    {
        MainTabs.SelectedIndex = 4;
        StatusText.Text = $"Updated to Nova {version}.";
        OpenChangeLog(new AppReleaseHistoryEntry(
            Version.TryParse(version, out var parsedVersion) ? parsedVersion : AppUpdateService.CurrentVersion,
            releaseName,
            GetReleaseTypeFromName(releaseName),
            DateTimeOffset.Now,
            releaseNotes));
    }

    private void OpenChangeLog_Click(object sender, RoutedEventArgs e) => OpenChangeLog();

    private void OpenChangeLog(AppReleaseHistoryEntry? justInstalledRelease = null)
    {
        var window = new ChangeLogWindow { Owner = this };
        var releases = AppUpdateService.GetBundledReleaseHistory()
            .Select(ToReleaseHistoryItem)
            .ToList();
        if (justInstalledRelease is not null)
        {
            releases.Insert(0, ToReleaseHistoryItem(justInstalledRelease));
        }

        window.SetBundledReleases(releases);
        window.SetStatus("Showing release history bundled with this Nova installation. Checking GitHub for newer release notes…");
        window.SetFooter("Dates are shown for GitHub releases when published. Developer-only local releases may not have a date.");
        window.Loaded += async (_, _) =>
        {
            if (await LoadPublishedReleaseHistoryAsync(window, releases))
            {
                window.SetBundledReleases(releases
                    .OrderByDescending(release => release.Version)
                    .ThenByDescending(release => release.PublishedAt));
                window.SetStatus($"{releases.Count:N0} release entries loaded from this build and GitHub.");
            }
        };
        window.ShowDialog();
    }

    private static async Task<bool> LoadPublishedReleaseHistoryAsync(
        ChangeLogWindow window,
        List<ReleaseHistoryItem> releases)
    {
        try
        {
            var publishedReleases = await AppUpdateService.GetReleaseHistoryAsync(CancellationToken.None);
            foreach (var release in publishedReleases)
            {
                var item = ToReleaseHistoryItem(release);
                if (releases.Any(existing =>
                        existing.Version == item.Version &&
                        existing.ReleaseType.Equals(item.ReleaseType, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                releases.Add(item);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or
                InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            window.SetStatus(
                $"Could not refresh GitHub release history. Showing the locally bundled release notes instead. {exception.Message}");
            return false;
        }
    }

    private static ReleaseHistoryItem ToReleaseHistoryItem(AppReleaseHistoryEntry release)
    {
        var changes = release.Notes
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ||
                            line.StartsWith("* ", StringComparison.Ordinal)
                ? line[2..].Trim()
                : line)
            .ToArray();
        if (changes.Length == 0)
        {
            changes = ["No change notes were published for this release."];
        }

        return new ReleaseHistoryItem(release.Version, release.ReleaseType, release.PublishedAt, changes);
    }

    private static string GetReleaseTypeFromName(string releaseName)
    {
        var opening = releaseName.LastIndexOf('(');
        return opening >= 0 && releaseName.EndsWith(')')
            ? releaseName[(opening + 1)..^1]
            : "New version";
    }

    private void UpdateLastUpdateCheckDisplay()
    {
        AboutLastUpdateCheckText.Text = lastAppUpdateCheckAt?.ToString("g") ?? "Not checked yet";
    }

    private void RecordSuccessfulAppUpdateCheck()
    {
        lastAppUpdateCheckAt = DateTime.Now;
        UpdateLastUpdateCheckDisplay();
        preferences = preferences with { LastAppUpdateCheckAt = lastAppUpdateCheckAt };
        try
        {
            UserPreferencesService.Save(preferences);
        }
        catch (Exception exception)
        {
            AppUpdateStatusText.Text += $" The check succeeded, but its time could not be saved: {exception.Message}";
        }
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
            await RefreshUpdateAvailabilityAsync(forceRefresh, notifyIfNew: false);
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

    private async Task RefreshUpdateAvailabilityAsync(bool forceRefresh = false, bool notifyIfNew = true)
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
        RecordSuccessfulAppUpdateCheck();

        if (notifyIfNew && availableAppUpdate is not null)
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
            AppUpdateProgressBar.Value = 0;
            AppUpdateProgressBar.IsIndeterminate = true;
            AppUpdateProgressBar.Visibility = Visibility.Visible;
            AppUpdateProgressText.Text = "Preparing secure download…";
            AppUpdateProgressText.Visibility = Visibility.Visible;
            var progress = new Progress<AppUpdateDownloadProgress>(downloadProgress =>
            {
                if (downloadProgress.TotalBytes is > 0)
                {
                    var percentage = Math.Clamp(
                        downloadProgress.BytesReceived * 100d / downloadProgress.TotalBytes.Value, 0, 100);
                    AppUpdateProgressBar.IsIndeterminate = false;
                    AppUpdateProgressBar.Value = percentage;
                    var remaining = downloadProgress.EstimatedTimeRemaining is TimeSpan time
                        ? $" About {time:mm\\:ss} remaining."
                        : string.Empty;
                    AppUpdateProgressText.Text =
                        $"Downloading: {percentage:0}% ({FormatBytes(downloadProgress.BytesReceived)} of " +
                        $"{FormatBytes(downloadProgress.TotalBytes.Value)}).{remaining}";
                }
                else
                {
                    AppUpdateProgressBar.IsIndeterminate = true;
                    AppUpdateProgressText.Text =
                        $"Downloading: {FormatBytes(downloadProgress.BytesReceived)} received; total size unavailable.";
                }
            });
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Nova could not determine its executable path.");
            var stagePath = await AppUpdateService.DownloadAndVerifyAsync(
                release, CancellationToken.None, progress);
            AppUpdateProgressBar.IsIndeterminate = false;
            AppUpdateProgressBar.Value = 100;
            AppUpdateProgressText.Text = "Download verified. Preparing installation…";
            AppUpdateInstaller.StartUpdater(stagePath, executablePath, Environment.ProcessId, release);
            AppUpdateStatusText.Text = "Update verified. Nova is closing to install it, then will restart.";
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            AppUpdateStatusText.Text = $"Update could not be installed: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Could not install Nova update",
                MessageBoxButton.OK, MessageBoxImage.Error);
            AppUpdateProgressBar.Visibility = Visibility.Collapsed;
            AppUpdateProgressText.Visibility = Visibility.Collapsed;
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
        hasTempScan = true;
        rememberedTempFileCount = null;
        rememberedTempBytes = null;
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
            lastCleanupAt = DateTime.Now;
            lastCleanupSummary = $"removed {result.DeletedFiles:N0} files ({FormatBytes(result.DeletedBytes)}).";

            var freshScan = await Task.Run(() => TempCleaner.Scan(CancellationToken.None));
            SetTempScan(freshScan);
            RefreshCounts();
            RememberScan();
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
        var availableFiles = tempFiles.Count;
        var availableBytes = tempFiles.Sum(file => file.Bytes);
        CleanupTotalEstimateText.Text = lastTempScan is null
            ? "Scan to estimate"
            : $"{availableFiles:N0} files · {FormatBytes(availableBytes)} found";
        var selectableCategories = cleanupCategories.Where(category => category.IsSelectedAllowed).ToArray();
        var selectedCategories = selectableCategories.Count(category => category.IsSelected);
        SelectAllCleanupCheckBox.IsChecked = selectableCategories.Length == 0 || selectedCategories == 0
            ? false
            : selectedCategories == selectableCategories.Length ? true : null;
        CleanButton.Content = "Clean Selected Files";
        CleanButton.IsEnabled = !isBusy && selected.Length > 0;
        isUpdatingTempSelection = false;
    }

    private void SelectAllCleanupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var select = SelectAllCleanupCheckBox.IsChecked == true;
        isUpdatingTempSelection = true;
        foreach (var category in cleanupCategories.Where(category => category.IsSelectedAllowed))
        {
            category.IsSelected = select;
            foreach (var file in category.Files)
            {
                file.IsSelected = select;
            }
        }

        isUpdatingTempSelection = false;
        UpdateTempSelectionState();
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
        button.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            isSelected ? "AccentBrush" : "ThemeBrush_F0F3F8");
        if (isSelected)
        {
            button.Foreground = System.Windows.Media.Brushes.White;
            return;
        }

        button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ThemeBrush_202A3B");
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

    private void SetDashboardMonitoring(bool active)
    {
        if (!isWindowLoaded || isDashboardMonitoringActive == active)
        {
            return;
        }

        isDashboardMonitoringActive = active;
        if (!active)
        {
            dashboardMonitoringCancellation?.Cancel();
            dashboardMonitoringCancellation = null;
            DashboardMonitoringStatusText.Text = "  Monitoring paused";
            return;
        }

        DashboardMonitoringStatusText.Text = "  Monitoring active";
        var cancellation = new CancellationTokenSource();
        dashboardMonitoringCancellation = cancellation;
        _ = RunDashboardMetricsLoopAsync(cancellation);
        if (dashboardSystemInfo is null)
        {
            _ = LoadDashboardSystemInfoAsync();
        }
    }

    private async Task LoadDashboardSystemInfoAsync()
    {
        try
        {
            var info = await DashboardMonitor.ReadSystemInfoAsync();
            dashboardSystemInfo = info;
            dashboardSpecifications.Clear();
            foreach (var specification in info.Specifications)
            {
                dashboardSpecifications.Add(specification);
            }

            dashboardDrives.Clear();
            foreach (var drive in info.Drives)
            {
                dashboardDrives.Add(drive);
            }

            WindowsVersionText.Text = info.OperatingSystem;
            WindowsArchitectureText.Text = info.SystemType;
            WindowsLastBootText.Text = info.LastBoot;
            AboutSystemText.Text = info.OperatingSystem;
            if (dashboardSamples.Count > 0)
            {
                UpdateDashboardMetrics(dashboardSamples[^1]);
            }
        }
        catch (Exception exception) when (
            exception is COMException or Win32Exception or IOException or
                UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            DashboardMonitoringStatusText.Text = "  System information unavailable";
            StatusText.Text = $"Nova could not read some system information. {exception.Message}";
        }
    }

    private async Task RunDashboardMetricsLoopAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var metrics = await DashboardMonitor.ReadMetricsAsync(cancellationToken)
                    .ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!isDashboardMonitoringActive || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    UpdateDashboardMetrics(metrics);
                    if (dashboardSamples.Count == 0 ||
                        metrics.Timestamp - dashboardSamples[^1].Timestamp >= TimeSpan.FromSeconds(1))
                    {
                        dashboardSamples.Add(metrics);
                    }

                    if (metrics.Timestamp - lastDashboardHistoryPruneAt >= TimeSpan.FromMinutes(1))
                    {
                        var oldestRetainedSample = metrics.Timestamp - TimeSpan.FromHours(24);
                        dashboardSamples.RemoveAll(sample => sample.Timestamp < oldestRetainedSample);
                        lastDashboardHistoryPruneAt = metrics.Timestamp;
                    }

                    if (metrics.Timestamp - lastDashboardChartUpdateAt >= TimeSpan.FromSeconds(1))
                    {
                        UpdatePerformanceChart();
                        lastDashboardChartUpdateAt = metrics.Timestamp;
                    }
                }, DispatcherPriority.Background).Task.ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is COMException or Win32Exception or IOException or
                UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (isDashboardMonitoringActive)
                {
                    DashboardMonitoringStatusText.Text = "  Monitoring unavailable";
                    StatusText.Text = $"Live system monitoring could not read this PC's metrics. {exception.Message}";
                }
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(dashboardMonitoringCancellation, cancellation))
                {
                    dashboardMonitoringCancellation = null;
                }

                cancellation.Dispose();
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
    }

    private void UpdateDashboardMetrics(DashboardMetrics metrics)
    {
        SetMetric(CpuUsageText, CpuUsageBar, metrics.CpuUsage);
        SetMetric(GpuUsageText, GpuUsageBar, metrics.GpuUsage);
        SetMetric(RamUsageText, RamUsageBar, metrics.RamUsage);
        SetMetric(VramUsageText, VramUsageBar, metrics.VramUsage);
        CpuTemperatureText.Text = $"CPU temp: {metrics.CpuTemperature}";
        GpuTemperatureText.Text = $"GPU temp: {metrics.GpuTemperature}";

        VramBreakdownText.Text =
            metrics.VramUsedBytes is long vramUsed && metrics.VramTotalBytes is long vramTotal
                ? $"{FormatBytes(vramUsed)} / {FormatBytes(vramTotal)}"
                : "Unavailable / Unavailable";
        RamBreakdownText.Text = metrics.RamTotalBytes > 0
            ? $"{FormatBytes((long)Math.Min(metrics.RamUsedBytes, (ulong)long.MaxValue))} / " +
              $"{FormatBytes((long)Math.Min(metrics.RamTotalBytes, (ulong)long.MaxValue))}"
            : "Unavailable / Unavailable";
    }

    private static void SetMetric(
        TextBlock valueText,
        ProgressBar progressBar,
        double? value)
    {
        valueText.Text = value is double percentage ? $"{percentage:0}%" : "Unavailable";
        progressBar.Value = value ?? 0;
    }

    private void DashboardRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var hours))
        {
            return;
        }

        dashboardChartRange = TimeSpan.FromHours(hours);
        SetDashboardRangeButton(DashboardRange1HButton, hours == 1);
        SetDashboardRangeButton(DashboardRange6HButton, hours == 6);
        SetDashboardRangeButton(DashboardRange24HButton, hours == 24);
        UpdatePerformanceChart();
    }

    private static void SetDashboardRangeButton(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected
            ? Color.FromRgb(64, 95, 255)
            : Color.FromRgb(18, 37, 70));
        button.Foreground = new SolidColorBrush(selected
            ? Colors.White
            : Color.FromRgb(168, 185, 211));
    }

    private void PerformanceChart_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePerformanceChart();

    private void UpdatePerformanceChart()
    {
        if (!IsInitialized || PerformanceChart.ActualWidth <= 0 || PerformanceChart.ActualHeight <= 0)
        {
            return;
        }

        PerformanceChart.Children.Clear();
        var width = PerformanceChart.ActualWidth;
        var height = PerformanceChart.ActualHeight;
        var gridBrush = new SolidColorBrush(Color.FromRgb(24, 47, 82));
        for (var row = 1; row <= 3; row++)
        {
            var y = height * row / 4;
            PerformanceChart.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }

        var now = DateTime.Now;
        var start = now - dashboardChartRange;
        var samples = dashboardSamples
            .Where(sample => sample.Timestamp >= start && sample.Timestamp <= now)
            .ToArray();
        var sampleStride = Math.Max(1, (int)Math.Ceiling(samples.Length / 2400d));
        var plottedSamples = samples
            .Where((_, index) => index % sampleStride == 0 || index == samples.Length - 1)
            .ToArray();
        var collectedDuration = samples.Length > 0 ? now - samples[0].Timestamp : TimeSpan.Zero;
        PerformanceHistoryCoverageText.Text = samples.Length < 2
            ? "Waiting for samples"
            : collectedDuration < dashboardChartRange
                ? $"{FormatHistoryDuration(collectedDuration)} collected"
                : $"Last {dashboardChartRange.TotalHours:0}h";
        var graphStart = collectedDuration < dashboardChartRange && samples.Length > 0
            ? samples[0].Timestamp
            : start;
        var graphDurationMilliseconds = Math.Max((now - graphStart).TotalMilliseconds, 1);
        var series = new (Func<DashboardMetrics, double?> Value, Color Color)[]
        {
            (sample => sample.CpuUsage, Color.FromRgb(85, 117, 255)),
            (sample => sample.GpuUsage, Color.FromRgb(0, 201, 156)),
            (sample => sample.RamUsage, Color.FromRgb(25, 184, 243)),
            (sample => sample.VramUsage, Color.FromRgb(217, 66, 222))
        };
        var plottedPointCount = 0;
        foreach (var (getValue, color) in series)
        {
            Polyline? line = null;
            foreach (var sample in plottedSamples)
            {
                var value = getValue(sample);
                if (value is null)
                {
                    AddPerformanceLine(line, color);
                    line = null;
                    continue;
                }

                plottedPointCount++;
                line ??= new Polyline
                {
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1.6,
                    StrokeLineJoin = PenLineJoin.Round
                };
                var x = plottedSamples.Length == 1
                    ? width
                    : Math.Clamp(
                        (sample.Timestamp - graphStart).TotalMilliseconds / graphDurationMilliseconds * width,
                        0,
                        width);
                var y = height - Math.Clamp(value.Value, 0, 100) / 100 * height;
                line.Points.Add(new Point(x, y));
            }

            AddPerformanceLine(line, color);
        }

        PerformanceChartEmptyText.Visibility = plottedPointCount >= 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddPerformanceLine(Polyline? line, Color color)
    {
        if (line is null || line.Points.Count == 0)
        {
            return;
        }

        if (line.Points.Count == 1)
        {
            var point = line.Points[0];
            var marker = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(color)
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            PerformanceChart.Children.Add(marker);
            return;
        }

        PerformanceChart.Children.Add(line);
    }

    private static string FormatHistoryDuration(TimeSpan duration) =>
        duration.TotalMinutes < 1
            ? $"{Math.Max(1, (int)duration.TotalSeconds)}s"
            : duration.TotalHours < 1
                ? $"{(int)duration.TotalMinutes}m"
                : $"{(int)duration.TotalHours}h {(int)duration.Minutes}m";

    private void RefreshCounts()
    {
        InstalledCountText.Text = hasInstalledScan ? software.Count.ToString("N0") : "—";
        InstalledSummaryText.Text = hasInstalledScan ? "apps and games found" : "Scan this PC to count apps";
        UpdatesCountText.Text = hasUpdateScan ? updates.Count.ToString("N0") : "—";
        UpdatesSummaryText.Text = !hasUpdateScan
            ? "Scan to check available updates"
            : updates.Count == 0
                ? "You're up to date!"
                : $"{updates.Count:N0} found by winget";

        var scannedFiles = lastTempScan?.FileCount ?? rememberedTempFileCount ?? 0;
        var scannedBytes = lastTempScan?.TotalBytes ?? rememberedTempBytes ?? 0;
        hasTempScan |= lastTempScan is not null || rememberedTempBytes is not null;
        TempSizeText.Text = hasTempScan ? FormatBytes(scannedBytes) : "—";
        TempCountText.Text = hasTempScan
            ? $"{scannedFiles:N0} file(s) found"
            : "Scan to estimate";
        UpdateTempSelectionState();
    }

    private void SetBusy(bool busy, string status)
    {
        isBusy = busy;
        ScanButton.IsEnabled = !busy;
        ScanButton.Content = busy ? "Scanning…" : GetScanButtonText();
        DashboardScanButton.IsEnabled = !busy;
        DashboardScanButton.Content = busy ? "Scanning…" : "⌕   Scan for Updates";
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

        var dashboardSelected = MainTabs.SelectedIndex == 0;
        PageTitle.Text = MainTabs.SelectedIndex switch
        {
            0 => "Dashboard",
            1 => "My software",
            2 => "Updates",
            3 => "Storage & cleanup",
            4 => "Settings",
            _ => "Nova Software Manager"
        };
        PageSubtitle.Text = MainTabs.SelectedIndex switch
        {
            0 => "Live overview of your software, updates, and system.",
            1 => "Browse applications and games found on this PC.",
            2 => "Review update candidates from the winget source.",
            3 => "Explore storage, temporary files, and your software shortcuts.",
            4 => "Manage Nova application updates.",
            _ => string.Empty
        };

        DashboardGreetingText.Text =
            $"{(DateTime.Now.Hour < 12 ? "Good morning" : DateTime.Now.Hour < 18 ? "Good afternoon" : "Good evening")}, {Environment.UserName}";
        var settingsSelected = MainTabs.SelectedIndex == 4;
        PageHeader.Visibility = dashboardSelected || settingsSelected
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.Foreground = dashboardSelected
            ? new SolidColorBrush(Color.FromRgb(121, 148, 185))
            : new SolidColorBrush(Color.FromRgb(152, 163, 180));
        SetNavButtonState(OverviewNavButton, MainTabs.SelectedIndex == 0);
        SetNavButtonState(SoftwareNavButton, MainTabs.SelectedIndex == 1);
        SetNavButtonState(UpdatesNavButton, MainTabs.SelectedIndex == 2);
        SetNavButtonState(CleanupNavButton, MainTabs.SelectedIndex == 3);
        SetNavButtonState(SettingsGearButton, MainTabs.SelectedIndex == 4);
        if (!isBusy)
        {
            ScanButton.Content = GetScanButtonText();
        }

        if (isWindowLoaded)
        {
            SetDashboardMonitoring(dashboardSelected);
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
        0 => "Scan for updates",
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

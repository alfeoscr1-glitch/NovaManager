using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NovaManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (AppUpdateInstaller.IsApplyUpdateInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await AppUpdateInstaller.ApplyFromArgumentsAsync(e.Args);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or JsonException or
                    InvalidOperationException or InvalidDataException or IOException or
                    UnauthorizedAccessException or Win32Exception)
            {
                MessageBox.Show(exception.Message, "Nova update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Shutdown();
            }

            return;
        }

        if (e.Args.Contains("--check-update-notification", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await UpdateNotificationService.CheckAndNotifyWhenClosedAsync();
            }
            catch (Exception exception)
            {
                Environment.ExitCode = 1;
                MessageBox.Show(
                    $"Nova's background update check could not complete.{Environment.NewLine}{exception.Message}",
                    "Nova update check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                Shutdown();
            }

            return;
        }

        ThemeManager.Load(this);
        var mainWindow = new MainWindow(e.Args.Contains("--open-settings", StringComparer.OrdinalIgnoreCase));
        MainWindow = mainWindow;
        mainWindow.Show();
        if (AppUpdateInstaller.IsCleanupInvocation(e.Args))
        {
            _ = CleanupUpdateFilesAsync(e.Args, mainWindow);
        }
    }

    private static async Task CleanupUpdateFilesAsync(string[] args, MainWindow mainWindow)
    {
        try
        {
            await AppUpdateInstaller.CleanupAfterUpdateAsync(args, mainWindow);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Nova updated successfully, but temporary updater files could not be cleaned up.{Environment.NewLine}{exception.Message}",
                "Update cleanup needs attention",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

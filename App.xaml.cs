using System.Windows;

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
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Nova update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Shutdown();
            }

            return;
        }

        ThemeManager.Load(this);
        var mainWindow = new MainWindow();
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

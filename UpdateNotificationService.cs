using System.IO;
using System.Diagnostics;
using System.Windows.Forms;

namespace NovaManager;

internal sealed class UpdateNotificationService : IDisposable
{
    private static readonly string LastNotifiedReleasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "last-notified-release.txt");
    private const string ScheduledTaskName = "NovaSoftwareManagerUpdateNotification";

    private readonly NotifyIcon notifyIcon = new()
    {
        Icon = System.Drawing.SystemIcons.Application,
        Text = "Nova Software Manager",
        Visible = false
    };

    private readonly System.Windows.Threading.DispatcherTimer hideIconTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };

    public event EventHandler? NotificationClicked;

    public UpdateNotificationService()
    {
        notifyIcon.BalloonTipClicked += (_, _) => NotificationClicked?.Invoke(this, EventArgs.Empty);
        hideIconTimer.Tick += (_, _) =>
        {
            hideIconTimer.Stop();
            notifyIcon.Visible = false;
        };
    }

    public bool NotifyIfNew(AppUpdateRelease release)
    {
        var notificationKey = $"{release.Tag}:{release.Sha256}";
        var lastNotifiedRelease = File.Exists(LastNotifiedReleasePath)
            ? File.ReadAllText(LastNotifiedReleasePath).Trim()
            : string.Empty;
        if (lastNotifiedRelease.Equals(notificationKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(LastNotifiedReleasePath)
            ?? throw new InvalidOperationException("Nova could not determine the update notification settings folder.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(LastNotifiedReleasePath, notificationKey);

        notifyIcon.Visible = true;
        notifyIcon.ShowBalloonTip(
            10_000,
            "Nova update available",
            $"Nova {release.Version} is ready. Open Settings to download and install it.",
            ToolTipIcon.Info);
        hideIconTimer.Stop();
        hideIconTimer.Start();
        return true;
    }

    public static async Task RegisterScheduledCheckAsync()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Nova could not determine its executable path for update notifications.");
        var taskAction = $"\"{executablePath}\" --check-update-notification";
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "/Create", "/SC", "HOURLY", "/MO", "1", "/TN", ScheduledTaskName,
            "/TR", taskAction, "/F", "/IT", "/RL", "LIMITED"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start Task Scheduler to register update notifications.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { output.Trim(), error.Trim() }
                .Where(static text => text.Length > 0));
            throw new InvalidOperationException(
                $"Windows could not register Nova's hourly update check. {details}".Trim());
        }
    }

    public static async Task CheckAndNotifyWhenClosedAsync()
    {
        var release = await AppUpdateService.CheckAsync(CancellationToken.None);
        if (release is null)
        {
            return;
        }

        using var notificationService = new UpdateNotificationService();
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotificationClicked += (_, _) =>
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Nova could not determine its executable path to open Settings.");
            Process.Start(new ProcessStartInfo(executablePath, "--open-settings") { UseShellExecute = true });
            clicked.TrySetResult();
        };
        if (notificationService.NotifyIfNew(release))
        {
            await Task.WhenAny(clicked.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        }
    }

    public void Dispose()
    {
        hideIconTimer.Stop();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }
}

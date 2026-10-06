using System.IO;
using System.Windows.Forms;

namespace NovaManager;

internal sealed class UpdateNotificationService : IDisposable
{
    private static readonly string LastNotifiedReleasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "last-notified-release.txt");

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

    public void NotifyIfNew(AppUpdateRelease release)
    {
        var lastNotifiedRelease = File.Exists(LastNotifiedReleasePath)
            ? File.ReadAllText(LastNotifiedReleasePath).Trim()
            : string.Empty;
        if (lastNotifiedRelease.Equals(release.Tag, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(LastNotifiedReleasePath)
            ?? throw new InvalidOperationException("Nova could not determine the update notification settings folder.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(LastNotifiedReleasePath, release.Tag);

        notifyIcon.Visible = true;
        notifyIcon.ShowBalloonTip(
            10_000,
            "Nova update available",
            $"Nova {release.Version} is ready. Open Settings to download and install it.",
            ToolTipIcon.Info);
        hideIconTimer.Stop();
        hideIconTimer.Start();
    }

    public void Dispose()
    {
        hideIconTimer.Stop();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }
}

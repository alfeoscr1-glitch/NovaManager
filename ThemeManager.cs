using System.IO;
using System.Windows;
using System.Windows.Media;

namespace NovaManager;

internal static class ThemeManager
{
    private static readonly string[] ThemeColors =
    [
        "172238", "202A3B", "222F49", "25334A", "29364B", "293754", "39465B",
        "45536A", "4F5FD0", "526078", "5268E8", "53627A", "5669E8", "58667B",
        "596DE0", "5B6A81", "617391", "64738A", "71809A", "7657DF", "7888A4",
        "7F8BA0", "8998B1", "8B97AA", "91A0BA", "929DB0", "96A1B3", "98A3B4", "98A3B5",
        "AEB9CB", "B9C4FF", "DCE1FF", "E5EBFA", "E6E8FF", "E8ECF3", "ECF1FA",
        "EEF2FF", "EFF2F6", "F0F3F8", "F0F5FF", "F2F5FA", "F4F7FF", "F6F8FC",
        "F7F8FB", "FFFFFF"
    ];

    private static readonly HashSet<string> FixedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "172238", "222F49", "293754", "4F5FD0", "5268E8", "5669E8", "596DE0",
        "7657DF", "7888A4", "8998B1", "91A0BA", "AEB9CB", "B9C4FF", "DCE1FF",
        "E6E8FF", "ECF1FA"
    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "appearance.txt");

    public static string CurrentTheme { get; private set; } = "Light";

    public static void Load(Application application)
    {
        var theme = "Light";
        try
        {
            if (File.Exists(SettingsPath))
            {
                theme = File.ReadAllText(SettingsPath).Trim();
                if (!theme.Equals("Light", StringComparison.OrdinalIgnoreCase) &&
                    !theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The saved appearance must be Light or Dark.");
                }

                theme = char.ToUpperInvariant(theme[0]) + theme[1..].ToLowerInvariant();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                $"Nova could not read the saved appearance and will use Light mode.{Environment.NewLine}{exception.Message}",
                "Appearance setting unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Apply(application, theme);
    }

    public static void SetTheme(Application application, string theme)
    {
        if (!theme.Equals("Light", StringComparison.OrdinalIgnoreCase) &&
            !theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose Light or Dark appearance.", nameof(theme));
        }

        theme = char.ToUpperInvariant(theme[0]) + theme[1..].ToLowerInvariant();
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Nova could not determine the appearance settings folder.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, theme);
        Apply(application, theme);
    }

    private static void Apply(Application application, string theme)
    {
        var isDark = theme.Equals("Dark", StringComparison.Ordinal);
        foreach (var hex in ThemeColors)
        {
            var lightColor = (Color)ColorConverter.ConvertFromString($"#{hex}");
            var color = isDark && !FixedColors.Contains(hex) ? ToDarkColor(lightColor) : lightColor;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            application.Resources[$"ThemeBrush_{hex}"] = brush;
            application.Resources[$"ThemeColor_{hex}"] = color;
        }

        CurrentTheme = theme;
    }

    private static Color ToDarkColor(Color color)
    {
        var brightness = (color.R + color.G + color.B) / 3;
        return brightness >= 205
            ? Color.FromRgb(
                DarkenSurface(color.R),
                DarkenSurface(color.G),
                DarkenSurface(color.B))
            : Color.FromRgb(
                LightenText(color.R),
                LightenText(color.G),
                LightenText(color.B));
    }

    private static byte DarkenSurface(byte channel) =>
        (byte)Math.Clamp(28 + (255 - channel) * 0.28, 28, 100);

    private static byte LightenText(byte channel) =>
        (byte)Math.Clamp(255 - channel * 0.4, 160, 245);
}

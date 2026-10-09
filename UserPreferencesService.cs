using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NovaManager;

internal sealed record UserPreferences(
    bool MinimizeToTray,
    bool StartWithWindows,
    bool StartupNotification,
    DateTime? LastAppUpdateCheckAt);

internal static class UserPreferencesService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NovaSoftwareManager";
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "preferences.json");

    public static UserPreferences Load()
    {
        UserPreferences? preferences = null;
        try
        {
            if (File.Exists(PreferencesPath))
            {
                preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(PreferencesPath));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException(
                $"Nova could not read its saved preferences. {exception.Message}", exception);
        }

        return new UserPreferences(
            preferences?.MinimizeToTray ?? false,
            ReadStartWithWindows(),
            preferences?.StartupNotification ?? true,
            preferences?.LastAppUpdateCheckAt);
    }

    public static void Save(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = Path.GetDirectoryName(PreferencesPath)
            ?? throw new InvalidOperationException("Nova could not determine its settings folder.");
        Directory.CreateDirectory(directory);
        var temporaryPath = PreferencesPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences));
            File.Move(temporaryPath, PreferencesPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows could not open the current user's startup settings.");
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Nova could not determine its executable path.");
            runKey.SetValue(RunValueName, $"\"{executablePath}\"");
        }
        else
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static bool ReadStartWithWindows()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(RunValueName) is string command &&
               !string.IsNullOrWhiteSpace(command);
    }
}

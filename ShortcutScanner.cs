using System.IO;
using System.Text.Json;

namespace NovaManager;

internal sealed class UserSettings
{
    public string? ShortcutsFolder { get; set; }
}

internal static class ShortcutScanner
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "settings.json");

    public static string? LoadFolder()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
        var folder = settings?.ShortcutsFolder;
        return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? Path.GetFullPath(folder) : null;
    }

    public static void SaveFolder(string folder)
    {
        var settingsDirectory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Could not determine the settings folder.");
        Directory.CreateDirectory(settingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new UserSettings { ShortcutsFolder = folder }));
        File.Move(temporaryPath, SettingsPath, true);
    }

    public static IReadOnlyList<ShortcutInfo> Scan(string folder)
    {
        var fullFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(fullFolder))
        {
            throw new DirectoryNotFoundException($"Shortcut folder does not exist: {fullFolder}");
        }

        if ((File.GetAttributes(fullFolder) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The selected shortcuts folder is linked or redirected and was not scanned.");
        }

        var shortcuts = new List<ShortcutInfo>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullFolder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0 ||
                !Path.GetExtension(entry).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = ResolveTarget(entry);
            var exists = !string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target));
            shortcuts.Add(new ShortcutInfo(
                Path.GetFileNameWithoutExtension(entry),
                entry,
                target,
                exists ? "Target found" : "Target not verified",
                exists
                    ? "The shortcut target path exists. This does not assess whether the application itself is healthy."
                    : "The target could not be resolved to an existing file or folder. It may be a special shell link or may need your review."));
        }

        return shortcuts.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string ResolveTarget(string shortcutPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return string.Empty;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            return shortcut?.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return string.Empty;
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}

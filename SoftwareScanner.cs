using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NovaManager;

internal static class SoftwareScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static async Task<InstalledScanResult> FindInstalledAsync(CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, InstalledSoftware>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
                    if (uninstallKey is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var appKey = uninstallKey.OpenSubKey(subKeyName);
                            var name = ReadString(appKey, "DisplayName");
                            if (string.IsNullOrWhiteSpace(name) ||
                                ReadString(appKey, "SystemComponent") == "1" ||
                                !string.IsNullOrWhiteSpace(ReadString(appKey, "ParentKeyName")))
                            {
                                continue;
                            }

                            var publisher = ReadString(appKey, "Publisher");
                            var version = ReadString(appKey, "DisplayVersion");
                            var entryId = $"{hive}:{view}:{subKeyName}";
                            items.TryAdd(entryId, new InstalledSoftware(name, publisher, version, "Desktop app"));
                        }
                        catch (UnauthorizedAccessException)
                        {
                            AddWarning(warnings, $"Windows denied access to an installed-program record in {hive}.");
                        }
                        catch (IOException)
                        {
                            AddWarning(warnings, $"An installed-program record in {hive} could not be read.");
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    AddWarning(warnings, $"Windows denied access to the installed-program list in {hive}.");
                }
                catch (IOException)
                {
                    AddWarning(warnings, $"The installed-program list in {hive} could not be read.");
                }
            }
        }

        try
        {
            var storeApps = await FindStoreAppsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var app in storeApps)
            {
                items.TryAdd($"appx:{app.Name}:{app.Version}", app);
            }
        }
        catch (InvalidOperationException exception)
        {
            AddWarning(warnings, exception.Message);
        }

        return new InstalledScanResult(
            items.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            warnings);
    }

    public static async Task<IReadOnlyList<UpdateCandidate>> FindUpdatesAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "upgrade", "--source", "winget", "--accept-source-agreements", "--locale", "en-US"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Windows Package Manager did not start.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Windows Package Manager (winget) could not be started. Install or repair App Installer, then try again.",
                exception);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var errors = await errorTask.ConfigureAwait(false);
        var combined = string.Join(Environment.NewLine, output, errors).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"winget update check failed (exit code {process.ExitCode}).{Environment.NewLine}{Limit(combined, 1200)}");
        }

        return ParseUpgradeTable(output);
    }

    public static async Task<int> RunUpdateAsync(UpdateCandidate update, CancellationToken cancellationToken)
    {
        if (update.Id.Length > 160 || update.Id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+')))
        {
            throw new InvalidOperationException("The package ID contains unexpected characters; the update was not started.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = true,
            Arguments = $"upgrade --id \"{update.Id}\" --source winget --exact",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("winget could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static IReadOnlyList<UpdateCandidate> ParseUpgradeTable(string output)
    {
        var lines = output.Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var headerIndex = Array.FindIndex(lines, line =>
            line.Contains("Name", StringComparison.Ordinal) &&
            line.Contains("Id", StringComparison.Ordinal) &&
            line.Contains("Version", StringComparison.Ordinal) &&
            line.Contains("Available", StringComparison.Ordinal));

        if (headerIndex < 0)
        {
            if (lines.Any(line => line.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase) ||
                                  line.Contains("No installed package found", StringComparison.OrdinalIgnoreCase) ||
                                  line.Contains("No upgrades available", StringComparison.OrdinalIgnoreCase)))
            {
                return Array.Empty<UpdateCandidate>();
            }

            if (string.IsNullOrWhiteSpace(output) ||
                output.Contains("0 upgrades available", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<UpdateCandidate>();
            }

            throw new InvalidOperationException(
                $"winget returned an unrecognized update list. No updates were changed.{Environment.NewLine}{Limit(output.Trim(), 1000)}");
        }

        var header = lines[headerIndex];
        var idStart = header.IndexOf("Id", StringComparison.Ordinal);
        var versionStart = header.IndexOf("Version", StringComparison.Ordinal);
        var availableStart = header.IndexOf("Available", StringComparison.Ordinal);
        if (idStart <= 0 || versionStart <= idStart || availableStart <= versionStart)
        {
            throw new InvalidOperationException("winget returned an update table with unsupported columns. No updates were changed.");
        }

        var result = new List<UpdateCandidate>();
        for (var index = headerIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("version numbers that cannot be determined", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.Length <= availableStart)
            {
                continue;
            }

            var name = Slice(line, 0, idStart).Trim();
            var id = Slice(line, idStart, versionStart).Trim();
            var current = Slice(line, versionStart, availableStart).Trim();
            var available = line[availableStart..].Trim();
            if (name.Length == 0 || id.Length == 0 || available.Length == 0)
            {
                continue;
            }

            result.Add(new UpdateCandidate(name, id, current, available));
        }

        return result;
    }

    private static string Slice(string value, int start, int end)
    {
        if (start >= value.Length)
        {
            return string.Empty;
        }

        return value[start..Math.Min(end, value.Length)];
    }

    private static async Task<IReadOnlyList<InstalledSoftware>> FindStoreAppsAsync(CancellationToken cancellationToken)
    {
        const string script =
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } | " +
            "Select-Object Name,Version,PublisherDisplayName | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("PowerShell did not start for Windows app inventory.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Windows app packages could not be inventoried because PowerShell did not start.",
                exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorsTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var errors = await errorsTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows app package inventory failed (exit code {process.ExitCode}). {Limit(errors.Trim(), 800)}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<InstalledSoftware>();
        }

        var packages = JsonSerializer.Deserialize<JsonElement>(output);
        var rows = packages.ValueKind == JsonValueKind.Array ? packages.EnumerateArray().ToArray() : [packages];
        return rows
            .Select(row => new InstalledSoftware(
                ReadJson(row, "Name"),
                ReadJson(row, "PublisherDisplayName"),
                ReadJson(row, "Version"),
                "Windows app"))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();
    }

    private static string ReadString(RegistryKey? key, string name)
    {
        var value = key?.GetValue(name);
        return value is string text ? text.Trim() : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static string ReadJson(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.ToString() : string.Empty;

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 12)
        {
            warnings.Add(warning);
        }
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

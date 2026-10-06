using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NovaManager;

internal sealed record AppUpdateRelease(
    Version Version,
    string Tag,
    string ReleaseName,
    string ReleaseNotes,
    Uri DownloadUri,
    string Sha256);

internal static class AppUpdateService
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/alfeoscr1-glitch/NovaManager/releases/latest";
    private const string AssetName = "NovaManager.exe";
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = CreateHttpClient();

    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public static async Task<AppUpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(ReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The configured public GitHub repository or its latest release was not found. Publish a public release before checking updates.");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
        {
            throw new InvalidOperationException("The latest GitHub release is marked as a draft or prerelease; Nova only installs stable releases.");
        }

        var tag = release.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("The GitHub release did not include a version tag.");
        var releaseName = release.GetProperty("name").GetString() ?? $"Nova Manager {tag}";
        var releaseNotes = release.GetProperty("body").GetString()
            ?? throw new InvalidDataException($"The GitHub release {tag} did not include its changelog.");
        if (string.IsNullOrWhiteSpace(releaseNotes) || releaseNotes.Length > 20_000)
        {
            throw new InvalidDataException($"The GitHub release {tag} contains an empty or oversized changelog.");
        }

        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException($"The GitHub release tag “{tag}” is not a supported three-part version.");
        }

        var assets = release.GetProperty("assets").EnumerateArray();
        JsonElement asset = default;
        foreach (var candidate in assets)
        {
            if (string.Equals(candidate.GetProperty("name").GetString(), AssetName, StringComparison.Ordinal))
            {
                asset = candidate;
                break;
            }
        }

        if (asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"The GitHub release is missing its required {AssetName} asset.");
        }

        var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            digest.Length != 71 || !digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"GitHub did not provide a SHA-256 digest for {AssetName}; Nova will not install an unverified update.");
        }

        var downloadText = asset.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(downloadText, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !downloadUri.AbsolutePath.StartsWith("/alfeoscr1-glitch/NovaManager/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release asset URL was not a GitHub HTTPS release URL for the configured repository.");
        }

        return version > CurrentVersion
            ? new AppUpdateRelease(version, tag, releaseName, releaseNotes, downloadUri, digest[7..].ToLowerInvariant())
            : null;
    }

    public static async Task<string> DownloadAndVerifyAsync(
        AppUpdateRelease release,
        CancellationToken cancellationToken)
    {
        var targetPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Nova could not determine its executable path.");
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
            ?? throw new InvalidOperationException("Nova could not determine its installation folder.");
        if (!Path.GetFileName(targetPath).Equals(AssetName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic updates are supported only when this app is running as NovaManager.exe.");
        }

        var stageDirectory = Path.Combine(targetDirectory, $".NovaUpdate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        var stagePath = Path.Combine(stageDirectory, AssetName);
        try
        {
            using var response = await Client.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The update asset exceeds Nova's 512 MB safety limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            long totalBytes = 0;
            await using (var destination = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaximumDownloadBytes)
                    {
                        throw new InvalidDataException("The update asset exceeds Nova's 512 MB safety limit.");
                    }

                    hash.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded file does not match GitHub's published SHA-256 digest.");
            }

            if (totalBytes < 20_000_000)
            {
                throw new InvalidDataException("The verified release asset is unexpectedly small to be a self-contained Nova executable.");
            }

            await using var executable = File.OpenRead(stagePath);
            var signature = new byte[2];
            if (await executable.ReadAsync(signature, cancellationToken) != signature.Length ||
                signature[0] != (byte)'M' || signature[1] != (byte)'Z')
            {
                throw new InvalidDataException("The downloaded release asset is not a Windows executable.");
            }
        }
        catch
        {
            TryDeleteDirectory(stageDirectory);
            throw;
        }

        return stagePath;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NovaSoftwareManager", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class AppUpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string CleanupArgument = "--cleanup-update";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsApplyUpdateInvocation(string[] args) =>
        args.Length == 2 && args[0].Equals(ApplyArgument, StringComparison.Ordinal);

    public static bool IsCleanupInvocation(string[] args) =>
        args.Length == 5 && args[0].Equals(CleanupArgument, StringComparison.Ordinal);

    public static async Task ApplyFromArgumentsAsync(string[] args)
    {
        var configPath = Path.GetFullPath(args[1]);
        var updaterRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaSoftwareManager",
            "updater"));
        if (!IsWithinDirectory(configPath, updaterRoot))
        {
            throw new InvalidDataException("The update configuration is outside Nova's updater folder.");
        }

        var config = JsonSerializer.Deserialize<UpdateInstallConfig>(await File.ReadAllTextAsync(configPath), JsonOptions)
            ?? throw new InvalidDataException("The update configuration is empty.");
        if (!Version.TryParse(config.Version, out _) ||
            string.IsNullOrWhiteSpace(config.ReleaseName) ||
            string.IsNullOrWhiteSpace(config.ReleaseNotes) ||
            config.ReleaseNotes.Length > 20_000)
        {
            throw new InvalidDataException("The installed update's changelog is missing or invalid.");
        }
        var targetPath = Path.GetFullPath(config.TargetPath);
        var stagePath = Path.GetFullPath(config.StagePath);
        var executablePath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("The updater executable path could not be determined."));
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException("The application folder could not be determined.");
        var stageDirectory = Path.GetDirectoryName(stagePath)
            ?? throw new InvalidDataException("The update staging folder could not be determined.");
        if (!Path.GetFileName(targetPath).Equals("NovaManager.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(stagePath).Equals("NovaManager.exe", StringComparison.OrdinalIgnoreCase) ||
            !IsWithinDirectory(stagePath, targetDirectory) ||
            !Path.GetFileName(stageDirectory).StartsWith(".NovaUpdate-", StringComparison.OrdinalIgnoreCase) ||
            !IsWithinDirectory(executablePath, updaterRoot) ||
            !File.Exists(targetPath) || !File.Exists(stagePath))
        {
            throw new InvalidDataException("The update paths failed Nova's safety checks.");
        }

        using (var parent = TryGetProcess(config.ParentProcessId))
        {
            if (parent is not null)
            {
                string? parentPath;
                try
                {
                    parentPath = parent.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    throw new InvalidOperationException("Nova could not verify the running app process before updating.", exception);
                }

                if (parentPath is null || !Path.GetFullPath(parentPath).Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The process being replaced is not the expected NovaManager.exe.");
                }

                if (!parent.WaitForExit(60_000))
                {
                    throw new TimeoutException("Nova did not close within 60 seconds. Close it and try the update again.");
                }
            }
        }

        var backupPath = Path.Combine(targetDirectory, $"NovaManager.exe.previous-{DateTime.UtcNow:yyyyMMddHHmmss}");
        ReplaceWithRetry(stagePath, targetPath, backupPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true,
            WorkingDirectory = targetDirectory
        };
        startInfo.ArgumentList.Add(CleanupArgument);
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add(stageDirectory);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var started = Process.Start(startInfo);
        if (started is null)
        {
            throw new InvalidOperationException("The updated NovaManager.exe was installed but could not be restarted.");
        }
    }

    public static async Task CleanupAfterUpdateAsync(string[] args, MainWindow mainWindow)
    {
        var helperPath = Path.GetFullPath(args[1]);
        var configPath = Path.GetFullPath(args[2]);
        var stageDirectory = Path.GetFullPath(args[3]);
        if (!int.TryParse(args[4], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var helperProcessId))
        {
            throw new InvalidDataException("The temporary updater process ID is invalid.");
        }

        var updaterRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaSoftwareManager",
            "updater"));
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("The updated application path could not be determined.")))!;
        var updaterDirectory = Path.GetDirectoryName(helperPath)!;
        if (!IsWithinDirectory(helperPath, updaterRoot) ||
            !Path.GetFileName(helperPath).Equals("NovaManagerUpdater.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(configPath)!.Equals(updaterDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(configPath).Equals("update.json", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(stageDirectory)!.Equals(targetDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(stageDirectory).StartsWith(".NovaUpdate-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The temporary updater cleanup paths failed Nova's safety checks.");
        }

        var config = JsonSerializer.Deserialize<UpdateInstallConfig>(await File.ReadAllTextAsync(configPath), JsonOptions)
            ?? throw new InvalidDataException("The update configuration is empty.");
        if (!Version.TryParse(config.Version, out _) ||
            string.IsNullOrWhiteSpace(config.ReleaseName) ||
            string.IsNullOrWhiteSpace(config.ReleaseNotes) ||
            config.ReleaseNotes.Length > 20_000)
        {
            throw new InvalidDataException("The installed update's changelog is missing or invalid.");
        }

        using (var helper = TryGetProcess(helperProcessId))
        {
            if (helper is not null && !await Task.Run(() => helper.WaitForExit(60_000)))
            {
                throw new TimeoutException("Nova updated successfully, but the temporary updater did not exit in time for cleanup.");
            }
        }

        mainWindow.ShowUpdateReleaseNotes(config.ReleaseName, config.Version, config.ReleaseNotes);
        File.Delete(configPath);
        File.Delete(helperPath);
        Directory.Delete(updaterDirectory);
        if (Directory.Exists(stageDirectory))
        {
            Directory.Delete(stageDirectory);
        }
    }

    public static Process StartUpdater(string stagePath, string targetPath, int parentProcessId, AppUpdateRelease release)
    {
        var updaterDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaSoftwareManager",
            "updater",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updaterDirectory);
        var updaterPath = Path.Combine(updaterDirectory, "NovaManagerUpdater.exe");
        var configPath = Path.Combine(updaterDirectory, "update.json");
        try
        {
            var currentExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Nova could not determine its executable path.");
            File.Copy(currentExe, updaterPath);
            var config = new UpdateInstallConfig(
                parentProcessId,
                Path.GetFullPath(targetPath),
                Path.GetFullPath(stagePath),
                release.Version.ToString(3),
                release.ReleaseName,
                release.ReleaseNotes);
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"{ApplyArgument} \"{configPath}\"",
                UseShellExecute = true,
                WorkingDirectory = updaterDirectory
            });
            return process ?? throw new InvalidOperationException("The Nova updater helper did not start.");
        }
        catch
        {
            TryDeleteDirectory(updaterDirectory);
            throw;
        }
    }

    private static void ReplaceWithRollback(string stagePath, string targetPath, string backupPath)
    {
        File.Move(targetPath, backupPath);
        try
        {
            File.Move(stagePath, targetPath);
        }
        catch
        {
            File.Move(backupPath, targetPath);
            throw;
        }
    }

    private static void ReplaceWithRetry(string stagePath, string targetPath, string backupPath)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(stagePath, targetPath, backupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceWithRollback(stagePath, targetPath, backupPath);
                return;
            }
            catch (Exception exception) when (IsTransientFileLock(exception))
            {
                if (attempt >= maxAttempts)
                {
                    throw new IOException(
                        "Windows kept the update file locked. Close any other Nova Manager windows or update dialogs, then try again.",
                        exception);
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static bool IsTransientFileLock(Exception exception)
    {
        if (exception is not (IOException or UnauthorizedAccessException))
        {
            return false;
        }

        var windowsError = unchecked((uint)exception.HResult) & 0xffff;
        return windowsError is 5 or 32 or 33;
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record UpdateInstallConfig(
        int ParentProcessId,
        string TargetPath,
        string StagePath,
        string Version,
        string ReleaseName,
        string ReleaseNotes);
}

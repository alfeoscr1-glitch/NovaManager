using System.IO;
using System.Security;

namespace NovaManager;

internal static class TempCleaner
{
    public static TempScanResult Scan(CancellationToken cancellationToken)
    {
        var folders = new List<TempFolderResult>();
        var errors = new List<string>();
        var skipped = 0;

        var roots = GetRoots(errors);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = 0;
            long bytes = 0;
            var scannedFiles = new List<TempFileResult>();
            Walk(root.Path, file =>
            {
                files++;
                var length = file.Length;
                bytes += length;
                scannedFiles.Add(new TempFileResult(file.FullName, length, file.LastWriteTimeUtc));
            }, () => skipped++, errors, cancellationToken);
            folders.Add(new TempFolderResult(root.Name, root.Path, files, bytes, scannedFiles));
        }

        return new TempScanResult(folders, skipped, errors);
    }

    public static CleanupResult RemoveContents(
        IReadOnlyList<TempFolderResult> scannedFolders,
        CancellationToken cancellationToken)
    {
        var deletedFiles = 0;
        long deletedBytes = 0;
        var skipped = 0;
        var errors = new List<string>();
        var allowedRoots = GetAllowedRoots();

        foreach (var scannedFolder in scannedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = NormalizePath(scannedFolder.Path);
            if (!allowedRoots.ContainsKey(root))
            {
                errors.Add($"Cleanup stopped for {root}: this folder is not an approved temporary-folder location.");
                continue;
            }

            if (!Directory.Exists(root))
            {
                errors.Add($"Cleanup skipped {root}: the scanned folder no longer exists.");
                continue;
            }

            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"Cleanup skipped {root}: the folder is now a linked or redirected location.");
                    continue;
                }
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                errors.Add($"Cleanup skipped {root}: {exception.Message}");
                continue;
            }

            foreach (var file in scannedFolder.ScannedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.IsSelected)
                {
                    continue;
                }

                DeleteScannedFile(root, file, ref deletedFiles, ref deletedBytes, ref skipped, errors);
            }
        }

        return new CleanupResult(deletedFiles, deletedBytes, skipped, errors);
    }

    public static bool IsApprovedRoot(string path)
    {
        try
        {
            return GetAllowedRoots().ContainsKey(NormalizePath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<(string Name, string Path)> GetRoots(List<string> errors)
    {
        var allowedRoots = GetAllowedRoots();
        var candidates = new[]
        {
            (Name: "User temporary folder", Path: Environment.GetEnvironmentVariable("TEMP")),
            (Name: "User temporary folder", Path: Environment.GetEnvironmentVariable("TMP")),
            (Name: "User temporary folder", Path: Path.GetTempPath()),
            (Name: "Windows temporary folder", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
        };

        var roots = new Dictionary<string, (string Name, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Path))
            {
                continue;
            }

            try
            {
                var fullPath = NormalizePath(candidate.Path.Trim().Trim('"'));
                if (!allowedRoots.ContainsKey(fullPath))
                {
                    AddError(errors,
                        $"{fullPath}: not scanned because it is outside the standard user and Windows temporary folders.");
                    continue;
                }

                roots.TryAdd(fullPath, (candidate.Name, fullPath));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                AddError(errors, $"A configured temporary-folder path could not be checked: {exception.Message}");
            }
        }

        foreach (var root in allowedRoots.Values)
        {
            if (!Directory.Exists(root.Path))
            {
                AddError(errors, $"{root.Path}: standard temporary folder was not found.");
                continue;
            }

            try
            {
                if ((File.GetAttributes(root.Path) & FileAttributes.ReparsePoint) != 0)
                {
                    AddError(errors, $"{root.Path}: not scanned because the folder is a linked or redirected location.");
                    continue;
                }

                roots.TryAdd(root.Path, root);
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                AddError(errors, $"{root.Path}: could not access the temporary folder: {exception.Message}");
            }
        }

        if (roots.Count == 0 && errors.Count == 0)
        {
            errors.Add("No approved temporary folders were found to scan.");
        }

        return roots.Values.ToArray();
    }

    private static Dictionary<string, (string Name, string Path)> GetAllowedRoots()
    {
        var roots = new Dictionary<string, (string Name, string Path)>(StringComparer.OrdinalIgnoreCase);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        AddAllowedRoot(roots, "User temporary folder", Path.Combine(localAppData, "Temp"));
        AddAllowedRoot(roots, "Windows temporary folder", Path.Combine(windows, "Temp"));
        return roots;
    }

    private static void AddAllowedRoot(
        Dictionary<string, (string Name, string Path)> roots,
        string name,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = NormalizePath(path);
        roots.TryAdd(fullPath, (name, fullPath));
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void Walk(
        string folder,
        Action<FileInfo> onFile,
        Action onSkipped,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        onSkipped();
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Walk(entry, onFile, onSkipped, errors, cancellationToken);
                    }
                    else
                    {
                        var file = new FileInfo(entry);
                        onFile(file);
                    }
                }
                catch (Exception exception) when (IsFileAccessException(exception))
                {
                    onSkipped();
                    AddError(errors, entry, exception);
                }
            }
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            onSkipped();
            AddError(errors, folder, exception);
        }
    }

    private static void DeleteScannedFile(
        string root,
        TempFileResult scannedFile,
        ref int deletedFiles,
        ref long deletedBytes,
        ref int skipped,
        List<string> errors)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(scannedFile.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            skipped++;
            AddError(errors, scannedFile.Path, exception);
            return;
        }

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            skipped++;
            AddError(errors, $"{fullPath}: skipped because the scanned file is outside its approved temporary folder.");
            return;
        }

        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(parent))
            {
                var parentPath = Path.GetFullPath(parent);
                if ((File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    AddError(errors, $"{parentPath}: skipped because a folder in the scanned path is now linked or redirected.");
                    return;
                }

                if (parentPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                parent = Path.GetDirectoryName(parentPath);
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
            {
                skipped++;
                AddError(errors, $"{fullPath}: skipped because it is no longer a regular file.");
                return;
            }

            var info = new FileInfo(fullPath);
            info.Refresh();
            if (info.Length != scannedFile.Bytes || info.LastWriteTimeUtc != scannedFile.LastWriteTimeUtc)
            {
                skipped++;
                AddError(errors, $"{fullPath}: skipped because it changed after the scan.");
                return;
            }

            File.Delete(fullPath);
            deletedFiles++;
            deletedBytes += scannedFile.Bytes;
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            skipped++;
            AddError(errors, fullPath, exception);
        }
    }

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;

    private static void AddError(List<string> errors, string message)
    {
        if (errors.Count < 15)
        {
            errors.Add(message);
        }
    }

    private static void AddError(List<string> errors, string path, Exception exception) =>
        AddError(errors, $"{path}: {exception.Message}");
}

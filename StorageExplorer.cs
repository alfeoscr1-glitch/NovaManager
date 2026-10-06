using System.IO;

namespace NovaManager;

internal static class StorageExplorer
{
    public const string ThisPcPath = "::THIS_PC::";
    private const int MaxSearchResults = 5000;

    public static IReadOnlyList<StorageRootOption> GetQuickLocations()
    {
        var locations = new List<StorageRootOption>
        {
            new("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            new("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            new("Pictures (Gallery images)", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            new("AppData — Local", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            new("AppData — Roaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            new("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            new("Program Files (x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            new("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            new("Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            new("This PC", ThisPcPath, true)
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                locations.Add(new StorageRootOption(
                    !drive.IsReady || string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.Name
                        : $"{drive.Name} ({drive.VolumeLabel})",
                    drive.Name));
            }
            catch (Exception exception) when (IsAccessException(exception) || exception is InvalidOperationException)
            {
                locations.Add(new StorageRootOption(drive.Name, drive.Name));
            }
        }

        return locations
            .Where(location => location.IsThisPc ||
                               (!string.IsNullOrWhiteSpace(location.Path) &&
                                Directory.Exists(location.Path)))
            .DistinctBy(location => (location.Name, location.Path), StringTupleComparer.Instance)
            .ToArray();
    }

    public static StorageScanResult List(string selectedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedPath.Equals(ThisPcPath, StringComparison.Ordinal))
        {
            return new StorageScanResult(ListDrives(), Array.Empty<string>());
        }

        var fullPath = Path.GetFullPath(selectedPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {fullPath}");
        }

        var entries = new List<StorageEntryInfo>();
        var warnings = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                long? bytes = null;
                if (!isDirectory && !isLink)
                {
                    bytes = new FileInfo(entry).Length;
                }

                entries.Add(new StorageEntryInfo(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(entry)),
                    entry,
                    isLink ? (isDirectory ? "Linked folder — not followed" : "Linked file — not followed")
                        : isDirectory ? "Folder" : "File",
                    bytes,
                    isDirectory && !isLink));
            }
            catch (Exception exception) when (IsAccessException(exception))
            {
                AddWarning(warnings, $"{entry}: {exception.Message}");
            }
        }

        return new StorageScanResult(
            entries.OrderBy(entry => entry.Type.StartsWith("Folder", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            warnings);
    }

    public static StorageScanResult Search(
        string selectedPath,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Enter a name or part of a name to search for.");
        }

        var warnings = new List<string>();
        var roots = selectedPath.Equals(ThisPcPath, StringComparison.Ordinal)
            ? GetReadyDriveRoots(warnings)
            : [Path.GetFullPath(selectedPath)];
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Folder not found: {root}");
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Search was not started because the selected root is linked or redirected: {root}");
            }
        }

        var entries = new List<StorageEntryInfo>();
        var pending = new Stack<string>(roots.Reverse());
        var truncated = false;

        while (pending.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception exception) when (IsAccessException(exception))
            {
                AddWarning(warnings, $"{current}: {exception.Message}");
                continue;
            }

            try
            {
                foreach (var entry in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
                        var matches = name.Contains(query, StringComparison.OrdinalIgnoreCase);
                        long? bytes = null;
                        if (!isDirectory && !isLink && matches)
                        {
                            bytes = new FileInfo(entry).Length;
                        }

                        if (matches)
                        {
                            entries.Add(new StorageEntryInfo(
                                name,
                                entry,
                                isLink ? (isDirectory ? "Linked folder — not followed" : "Linked file")
                                    : isDirectory ? "Folder" : "File",
                                bytes,
                                isDirectory && !isLink));
                            if (entries.Count >= MaxSearchResults)
                            {
                                truncated = true;
                                break;
                            }
                        }

                        if (isDirectory && !isLink)
                        {
                            pending.Push(entry);
                        }
                    }
                    catch (Exception exception) when (IsAccessException(exception))
                    {
                        AddWarning(warnings, $"{entry}: {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (IsAccessException(exception))
            {
                AddWarning(warnings, $"{current}: {exception.Message}");
            }
        }

        if (truncated)
        {
            warnings.Add($"Search stopped after {MaxSearchResults:N0} matches. Narrow the location or use a more specific search term.");
        }

        return new StorageScanResult(
            entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            warnings);
    }

    private static IReadOnlyList<string> GetReadyDriveRoots(List<string> warnings)
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && (File.GetAttributes(drive.RootDirectory.FullName) & FileAttributes.ReparsePoint) == 0)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (Exception exception) when (IsAccessException(exception) || exception is InvalidOperationException)
            {
                AddWarning(warnings, $"{drive.Name}: {exception.Message}");
            }
        }

        return roots;
    }

    private static IReadOnlyList<StorageEntryInfo> ListDrives()
    {
        var drives = new List<StorageEntryInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.Name} ({drive.VolumeLabel})";
                var bytes = drive.IsReady ? drive.TotalSize - drive.AvailableFreeSpace : (long?)null;
                drives.Add(new StorageEntryInfo(
                    name,
                    drive.Name,
                    drive.IsReady ? drive.DriveType.ToString() : "Drive not ready",
                    bytes,
                    drive.IsReady));
            }
            catch (Exception exception) when (IsAccessException(exception) || exception is InvalidOperationException)
            {
                drives.Add(new StorageEntryInfo(drive.Name, drive.Name, "Drive unavailable", null, false));
            }
        }

        return drives;
    }

    private static bool IsAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 50)
        {
            warnings.Add(warning);
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Name, string Path)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string Name, string Path) left, (string Name, string Path) right) =>
            left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
            left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, string Path) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}

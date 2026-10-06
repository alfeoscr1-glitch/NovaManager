using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovaManager;

public sealed record InstalledSoftware(string Name, string Publisher, string Version, string Type);

public sealed record InstalledScanResult(
    IReadOnlyList<InstalledSoftware> Items,
    IReadOnlyList<string> Warnings);

public sealed record UpdateCandidate(string Name, string Id, string CurrentVersion, string AvailableVersion);

public sealed class TempFileResult : INotifyPropertyChanged
{
    private bool isSelected;

    public TempFileResult(string path, long bytes, DateTime lastWriteTimeUtc)
    {
        Path = path;
        Bytes = bytes;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public string Path { get; }
    public long Bytes { get; }
    public DateTime LastWriteTimeUtc { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
        }
    }

    public string FormattedBytes => TempFolderResult.FormatBytes(Bytes);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record TempFolderResult(
    string Name,
    string Path,
    int Files,
    long Bytes,
    IReadOnlyList<TempFileResult> ScannedFiles)
{
    public string FormattedBytes => FormatBytes(Bytes);

    internal static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:0.#} {suffixes[suffix]}";
    }
}

public sealed record TempScanResult(
    IReadOnlyList<TempFolderResult> Folders,
    int Skipped,
    IReadOnlyList<string> Errors)
{
    public int FileCount => Folders.Sum(folder => folder.Files);
    public long TotalBytes => Folders.Sum(folder => folder.Bytes);
}

public sealed class TempCleanupCategory : INotifyPropertyChanged
{
    private bool isSelected;

    public TempCleanupCategory(
        string name,
        string icon,
        string description,
        string path,
        IReadOnlyList<TempFileResult> files,
        string status,
        bool isAvailable)
    {
        Name = name;
        Icon = icon;
        Description = description;
        Path = path;
        Files = files;
        Status = status;
        IsAvailable = isAvailable;
    }

    public string Name { get; }
    public string Icon { get; }
    public string Description { get; }
    public string Path { get; }
    public IReadOnlyList<TempFileResult> Files { get; }
    public int FileCount => Files.Count;
    public long Bytes => Files.Sum(file => file.Bytes);
    public string FormattedBytes => TempFolderResult.FormatBytes(Bytes);
    public int SelectedFileCount => Files.Count(file => file.IsSelected);
    public string SelectedFormattedBytes => TempFolderResult.FormatBytes(Files.Where(file => file.IsSelected).Sum(file => file.Bytes));
    public string Status { get; }
    public bool IsAvailable { get; }
    public bool IsSelectedAllowed => IsAvailable && FileCount > 0;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
        }
    }

    public void RefreshSelectionSummary()
    {
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedFormattedBytes));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record CleanupResult(int DeletedFiles, long DeletedBytes, int Skipped, IReadOnlyList<string> Errors);

public sealed record StorageEntryInfo(string Name, string Path, string Type, long? Bytes, bool CanEnter)
{
    public string FormattedBytes => Bytes is long bytes ? TempFolderResult.FormatBytes(bytes) : "—";
}

public sealed record ShortcutInfo(string Name, string Path, string Target, string Status, string Explanation);

public sealed record StorageScanResult(IReadOnlyList<StorageEntryInfo> Entries, IReadOnlyList<string> Warnings);

public sealed record StorageRootOption(string Name, string Path, bool IsThisPc = false)
{
    public override string ToString() => Name;
}

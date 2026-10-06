using System.IO;
using System.Text.Json;

namespace NovaManager;

internal sealed record ScanSnapshot(
    DateTime? LastScanAt,
    List<InstalledSoftware> Software,
    List<UpdateCandidate> Updates,
    int? TempFileCount,
    long? TempBytes,
    DateTime? LastCleanupAt,
    string? LastCleanupSummary);

internal static class ScanMemory
{
    private static readonly string SnapshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        "last-scan.json");

    public static ScanSnapshot? Load()
    {
        try
        {
            return File.Exists(SnapshotPath)
                ? JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(SnapshotPath))
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(ScanSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            var temporaryPath = SnapshotPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
            File.Move(temporaryPath, SnapshotPath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Remembering the last scan is a convenience; never fail a scan because of it.
        }
    }
}

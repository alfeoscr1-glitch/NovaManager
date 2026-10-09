using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NovaManager;

internal sealed record DashboardSpecification(string Icon, string Name, string Value);

internal sealed record DashboardDrive(
    string Name,
    string Used,
    string Capacity,
    double UsedPercentage);

internal sealed record DashboardSystemInfo(
    IReadOnlyList<DashboardSpecification> Specifications,
    IReadOnlyList<DashboardDrive> Drives,
    string OperatingSystem,
    string SystemType,
    string LastBoot);

internal sealed record DashboardMetrics(
    DateTime Timestamp,
    double? CpuUsage,
    double? GpuUsage,
    double? RamUsage,
    double? VramUsage,
    ulong RamUsedBytes,
    ulong RamTotalBytes,
    long? VramUsedBytes,
    long? VramTotalBytes,
    string CpuTemperature,
    string GpuTemperature);

internal static class DashboardMonitor
{
    private const string ProcessorCategory = "Processor Information";
    private const string ProcessorUtilityCounter = "% Processor Utility";
    private const string TotalProcessorInstance = "_Total";
    private const string GpuEngineCategory = "GPU Engine";
    private const string GpuUtilizationCounter = "Utilization Percentage";
    private static readonly object NvidiaGpuMonitorLock = new();
    private static PerformanceCounter? cpuUtilityCounter;
    private static bool isCpuCounterWarmed;
    private static List<PerformanceCounter>? gpuUtilizationCounters;
    private static bool isGpuCounterInitialized;
    private static NvidiaGpuMonitor? nvidiaGpuMonitor;
    private static bool isNvidiaGpuMonitorInitialized;

    public static Task<DashboardSystemInfo> ReadSystemInfoAsync() =>
        Task.Run(ReadSystemInfo);

    public static Task<DashboardMetrics> ReadMetricsAsync(CancellationToken cancellationToken) =>
        Task.Run(ReadMetrics, cancellationToken);

    private static DashboardSystemInfo ReadSystemInfo()
    {
        var cpuModel = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString",
            null) as string;
        cpuModel = Normalize(cpuModel) ?? "Unavailable";

        var adapters = ReadVideoAdapters();
        var gpuModels = adapters.Select(adapter => adapter.Name)
            .Where(value => value is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gpuName = gpuModels.Length == 0 ? "Unavailable" : string.Join(", ", gpuModels);
        var driver = adapters.Select(adapter => adapter.DriverVersion)
            .FirstOrDefault(value => value is not null);
        var gpuInfo = GetNvidiaGpuMonitor()?.ReadTelemetry() ?? NvidiaTelemetry.Unavailable;
        if (driver is null)
        {
            driver = gpuInfo.DriverVersion;
        }

        var vramTotalBytes = gpuInfo.TotalMemoryBytes;
        var memoryStatus = ReadMemoryStatus();
        var totalMemory = memoryStatus.TotalPhysical > 0
            ? FormatBytes(memoryStatus.TotalPhysical)
            : "Unavailable";
        var osInfo = ReadOperatingSystemInfo();
        var motherboard = ReadMotherboard();
        var directX = ReadDirectXVersion();

        var specifications = new[]
        {
            new DashboardSpecification("▦", "Operating system", osInfo.Version),
            new DashboardSpecification("▣", "CPU", cpuModel),
            new DashboardSpecification("▣", "GPU", gpuName),
            new DashboardSpecification("▤", "RAM", totalMemory),
            new DashboardSpecification("▦", "Motherboard", motherboard),
            new DashboardSpecification("▧", "Graphics driver", driver ?? "Unavailable"),
            new DashboardSpecification("◈", "DirectX", directX),
            new DashboardSpecification("▣", "ReBAR", "Unavailable"),
            new DashboardSpecification("▥", "VRAM", vramTotalBytes is long bytes ? FormatBytes(bytes) : "Unavailable")
        };

        return new DashboardSystemInfo(
            specifications,
            ReadDrives(),
            osInfo.Caption,
            Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            (DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64))
                .ToString("g", CultureInfo.CurrentCulture));
    }

    private static DashboardMetrics ReadMetrics()
    {
        var cpuUsage = ReadCpuUtility();
        var memoryStatus = ReadMemoryStatus();
        double? ramUsage = memoryStatus.TotalPhysical > 0
            ? Math.Clamp((double)(memoryStatus.TotalPhysical >= memoryStatus.AvailablePhysical
                             ? memoryStatus.TotalPhysical - memoryStatus.AvailablePhysical
                             : 0) /
                         memoryStatus.TotalPhysical * 100, 0, 100)
            : null;

        var gpuInfo = GetNvidiaGpuMonitor()?.ReadTelemetry() ?? NvidiaTelemetry.Unavailable;
        var gpuUsage = gpuInfo.UsagePercentage ?? ReadGpuUsageFromPerformanceCounters();
        var vramUsedBytes = gpuInfo.UsedMemoryBytes;
        var vramTotalBytes = gpuInfo.TotalMemoryBytes;
        var gpuTemperature = gpuInfo.TemperatureCelsius is double temperature
            ? $"{temperature:0} °C"
            : "Unavailable";

        double? vramUsage = vramUsedBytes is long used && vramTotalBytes is long total && total > 0
            ? Math.Clamp((double)used / total * 100, 0, 100)
            : null;

        return new DashboardMetrics(
            DateTime.Now,
            NormalizePercentage(cpuUsage),
            NormalizePercentage(gpuUsage),
            ramUsage,
            vramUsage,
            memoryStatus.TotalPhysical >= memoryStatus.AvailablePhysical
                ? memoryStatus.TotalPhysical - memoryStatus.AvailablePhysical
                : 0,
            memoryStatus.TotalPhysical,
            vramUsedBytes,
            vramTotalBytes,
            "Unavailable",
            gpuTemperature);
    }

    private static double? ReadCpuUtility()
    {
        cpuUtilityCounter ??= new PerformanceCounter(
            ProcessorCategory,
            ProcessorUtilityCounter,
            TotalProcessorInstance,
            readOnly: true);
        var value = cpuUtilityCounter.NextValue();
        if (!isCpuCounterWarmed)
        {
            isCpuCounterWarmed = true;
            return null;
        }

        return NormalizePercentage(value);
    }

    private static double? ReadGpuUsageFromPerformanceCounters()
    {
        if (!isGpuCounterInitialized)
        {
            gpuUtilizationCounters = CreateGpuUtilizationCounters();
            isGpuCounterInitialized = true;
        }

        double? highestUsage = null;
        var counters = gpuUtilizationCounters;
        if (counters is null)
        {
            return null;
        }

        for (var index = counters.Count - 1; index >= 0; index--)
        {
            var counter = counters[index];
            try
            {
                var usage = counter.NextValue();
                if (float.IsFinite(usage))
                {
                    highestUsage = Math.Max(highestUsage ?? 0, usage);
                }
            }
            catch (InvalidOperationException)
            {
                counter.Dispose();
                counters.RemoveAt(index);
            }
        }

        return NormalizePercentage(highestUsage);
    }

    private static List<PerformanceCounter> CreateGpuUtilizationCounters()
    {
        if (!PerformanceCounterCategory.Exists(GpuEngineCategory))
        {
            return [];
        }

        var counters = new List<PerformanceCounter>();
        var category = new PerformanceCounterCategory(GpuEngineCategory);
        foreach (var instanceName in category.GetInstanceNames())
        {
            PerformanceCounter? counter = null;
            try
            {
                counter = new PerformanceCounter(
                    GpuEngineCategory,
                    GpuUtilizationCounter,
                    instanceName,
                    readOnly: true);
                _ = counter.NextValue();
                counters.Add(counter);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                counter?.Dispose();
                // GPU engine instances can disappear while the driver updates its counter set.
            }
        }

        return counters;
    }

    private static IReadOnlyList<DashboardDrive> ReadDrives()
    {
        var drives = new List<DashboardDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var total = drive.TotalSize;
                var used = Math.Max(0, total - drive.AvailableFreeSpace);
                var volumeLabel = drive.VolumeLabel;
                var driveName = string.IsNullOrWhiteSpace(volumeLabel)
                    ? drive.Name.TrimEnd(Path.DirectorySeparatorChar)
                    : $"{drive.Name.TrimEnd(Path.DirectorySeparatorChar)} ({volumeLabel})";
                drives.Add(new DashboardDrive(
                    driveName,
                    FormatBytes(used),
                    FormatBytes(total),
                    total > 0 ? Math.Clamp((double)used / total * 100, 0, 100) : 0));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                drives.Add(new DashboardDrive(drive.Name, "Unavailable", "Unavailable", 0));
            }
        }

        return drives;
    }

    private static IReadOnlyList<VideoAdapterInfo> ReadVideoAdapters()
    {
        const string videoPath = @"SYSTEM\CurrentControlSet\Control\Video";
        using var videoKey = Registry.LocalMachine.OpenSubKey(videoPath);
        if (videoKey is null)
        {
            return [];
        }

        var adapters = new List<VideoAdapterInfo>();
        foreach (var adapterKeyName in videoKey.GetSubKeyNames())
        {
            using var adapterKey = videoKey.OpenSubKey(Path.Combine(adapterKeyName, "0000"));
            if (adapterKey is null)
            {
                continue;
            }

            var name = Normalize(adapterKey.GetValue("DriverDesc") as string)
                ?? Normalize(adapterKey.GetValue("HardwareInformation.AdapterString") as string);
            var driverVersion = Normalize(adapterKey.GetValue("DriverVersion") as string);
            if (name is not null || driverVersion is not null)
            {
                adapters.Add(new VideoAdapterInfo(name, driverVersion));
            }
        }

        return adapters;
    }

    private static (string Caption, string Version) ReadOperatingSystemInfo()
    {
        const string currentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        using var key = Registry.LocalMachine.OpenSubKey(currentVersionPath);
        var productName = Normalize(key?.GetValue("ProductName") as string)
            ?? Environment.OSVersion.VersionString;
        var displayVersion = Normalize(key?.GetValue("DisplayVersion") as string);
        var build = Normalize(key?.GetValue("CurrentBuildNumber") as string)
            ?? Normalize(key?.GetValue("CurrentBuild") as string);
        if (int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var buildNumber) &&
            buildNumber >= 22000 &&
            productName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            productName = $"Windows 11{productName["Windows 10".Length..]}";
        }

        var caption = productName;
        var versionParts = new List<string>();
        if (displayVersion is not null)
        {
            versionParts.Add(displayVersion);
        }

        if (build is not null)
        {
            versionParts.Add($"Build {build}");
        }

        return (caption, versionParts.Count == 0
            ? caption
            : $"{caption} ({string.Join(", ", versionParts)})");
    }

    private static string ReadMotherboard()
    {
        const string biosPath = @"HARDWARE\DESCRIPTION\System\BIOS";
        using var key = Registry.LocalMachine.OpenSubKey(biosPath);
        var manufacturer = Normalize(key?.GetValue("BaseBoardManufacturer") as string);
        var product = Normalize(key?.GetValue("BaseBoardProduct") as string);
        return string.Join(" ", new[] { manufacturer, product }.Where(value => value is not null)) is { Length: > 0 } value
            ? value
            : "Unavailable";
    }

    private static MEMORYSTATUSEX ReadMemoryStatus()
    {
        var status = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status : default;
    }

    private static string ReadDirectXVersion()
    {
        if (!NativeLibrary.TryLoad("d3d12.dll", out var library))
        {
            return "Unavailable";
        }

        try
        {
            var address = NativeLibrary.GetExport(library, "D3D12CreateDevice");
            var createDevice = Marshal.GetDelegateForFunctionPointer<D3D12CreateDeviceDelegate>(address);
            var deviceInterfaceId = new Guid("189819F1-1DB6-4B57-BE54-1821339B85F7");
            var levels = new (Direct3DFeatureLevel Level, string Name)[]
            {
                (Direct3DFeatureLevel.Level12_2, "DirectX 12 (Feature Level 12_2)"),
                (Direct3DFeatureLevel.Level12_1, "DirectX 12 (Feature Level 12_1)"),
                (Direct3DFeatureLevel.Level12_0, "DirectX 12"),
                (Direct3DFeatureLevel.Level11_1, "DirectX 11 (Feature Level 11_1)"),
                (Direct3DFeatureLevel.Level11_0, "DirectX 11")
            };

            foreach (var (level, name) in levels)
            {
                var result = createDevice(IntPtr.Zero, level, ref deviceInterfaceId, out var device);
                if (result >= 0 && device != IntPtr.Zero)
                {
                    Marshal.Release(device);
                    return name;
                }

                if (device != IntPtr.Zero)
                {
                    Marshal.Release(device);
                }
            }
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or MarshalDirectiveException or ArgumentException)
        {
            return "Unavailable";
        }
        finally
        {
            NativeLibrary.Free(library);
        }

        return "Unavailable";
    }

    private static double? NormalizePercentage(double? value) =>
        value.HasValue ? Math.Clamp(value.Value, 0, 100) : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static NvidiaGpuMonitor? GetNvidiaGpuMonitor()
    {
        lock (NvidiaGpuMonitorLock)
        {
            if (!isNvidiaGpuMonitorInitialized)
            {
                nvidiaGpuMonitor = NvidiaGpuMonitor.TryCreate();
                isNvidiaGpuMonitorInitialized = true;
            }

            return nvidiaGpuMonitor;
        }
    }

    private sealed class NvidiaGpuMonitor : IDisposable
    {
        private const uint Success = 0;
        private const uint GpuTemperatureSensor = 0;
        private const int MaximumDriverVersionLength = 80;
        private readonly object telemetryLock = new();
        private readonly IntPtr library;
        private readonly NvmlShutdownDelegate shutdown;
        private readonly NvmlDeviceUtilizationDelegate getUtilization;
        private readonly NvmlDeviceMemoryDelegate getMemory;
        private readonly NvmlDeviceTemperatureDelegate getTemperature;
        private readonly IntPtr[] devices;
        private bool isInitialized;

        public string? DriverVersion { get; private init; }

        private NvidiaGpuMonitor(
            IntPtr library,
            NvmlShutdownDelegate shutdown,
            NvmlDeviceUtilizationDelegate getUtilization,
            NvmlDeviceMemoryDelegate getMemory,
            NvmlDeviceTemperatureDelegate getTemperature,
            IntPtr[] devices,
            string? driverVersion)
        {
            this.library = library;
            this.shutdown = shutdown;
            this.getUtilization = getUtilization;
            this.getMemory = getMemory;
            this.getTemperature = getTemperature;
            this.devices = devices;
            DriverVersion = driverVersion;
            isInitialized = true;
        }

        public static NvidiaGpuMonitor? TryCreate()
        {
            foreach (var libraryName in new[] { "nvidia-ml.dll", "nvml.dll" })
            {
                if (!NativeLibrary.TryLoad(libraryName, out var library))
                {
                    continue;
                }

                NvmlShutdownDelegate? shutdown = null;
                var initialized = false;
                try
                {
                    var initialize = GetExport<NvmlInitDelegate>(library, "nvmlInit_v2", "nvmlInit");
                    shutdown = GetExport<NvmlShutdownDelegate>(library, "nvmlShutdown");
                    if (initialize() != Success)
                    {
                        NativeLibrary.Free(library);
                        continue;
                    }

                    initialized = true;
                    var getDeviceCount = GetExport<NvmlDeviceCountDelegate>(
                        library,
                        "nvmlDeviceGetCount_v2",
                        "nvmlDeviceGetCount");
                    var getDeviceHandle = GetExport<NvmlDeviceHandleDelegate>(
                        library,
                        "nvmlDeviceGetHandleByIndex_v2",
                        "nvmlDeviceGetHandleByIndex");
                    var getUtilization = GetExport<NvmlDeviceUtilizationDelegate>(
                        library,
                        "nvmlDeviceGetUtilizationRates");
                    var getMemory = GetExport<NvmlDeviceMemoryDelegate>(library, "nvmlDeviceGetMemoryInfo");
                    var getTemperature = GetExport<NvmlDeviceTemperatureDelegate>(
                        library,
                        "nvmlDeviceGetTemperature");
                    if (getDeviceCount(out var deviceCount) != Success || deviceCount == 0)
                    {
                        shutdown();
                        NativeLibrary.Free(library);
                        continue;
                    }

                    var devices = new List<IntPtr>((int)deviceCount);
                    for (uint index = 0; index < deviceCount; index++)
                    {
                        if (getDeviceHandle(index, out var device) == Success && device != IntPtr.Zero)
                        {
                            devices.Add(device);
                        }
                    }

                    if (devices.Count == 0)
                    {
                        shutdown();
                        NativeLibrary.Free(library);
                        continue;
                    }

                    string? driverVersion = null;
                    try
                    {
                        var getDriverVersion = GetExport<NvmlDriverVersionDelegate>(
                            library,
                            "nvmlSystemGetDriverVersion");
                        var buffer = Marshal.AllocHGlobal(MaximumDriverVersionLength);
                        try
                        {
                            Marshal.Copy(new byte[MaximumDriverVersionLength], 0, buffer, MaximumDriverVersionLength);
                            if (getDriverVersion(buffer, MaximumDriverVersionLength) == Success)
                            {
                                driverVersion = Normalize(Marshal.PtrToStringAnsi(buffer));
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // Driver-version reporting is optional; telemetry can still be read.
                    }

                    return new NvidiaGpuMonitor(
                        library,
                        shutdown,
                        getUtilization,
                        getMemory,
                        getTemperature,
                        devices.ToArray(),
                        driverVersion);
                }
                catch (Exception exception) when (
                    exception is EntryPointNotFoundException or MarshalDirectiveException or ArgumentException)
                {
                    if (initialized)
                    {
                        shutdown?.Invoke();
                    }

                    NativeLibrary.Free(library);
                }
            }

            return null;
        }

        public NvidiaTelemetry ReadTelemetry()
        {
            lock (telemetryLock)
            {
                return ReadTelemetryCore();
            }
        }

        private NvidiaTelemetry ReadTelemetryCore()
        {
            double utilizationTotal = 0;
            var utilizationCount = 0;
            ulong usedMemoryTotal = 0;
            ulong totalMemoryTotal = 0;
            var memoryCount = 0;
            uint highestTemperature = 0;
            var temperatureFound = false;

            foreach (var device in devices)
            {
                if (getUtilization(device, out var utilization) == Success)
                {
                    utilizationTotal += utilization.Gpu;
                    utilizationCount++;
                }

                if (getMemory(device, out var memory) == Success)
                {
                    usedMemoryTotal = SaturatingAdd(usedMemoryTotal, memory.Used);
                    totalMemoryTotal = SaturatingAdd(totalMemoryTotal, memory.Total);
                    memoryCount++;
                }

                if (getTemperature(device, GpuTemperatureSensor, out var temperature) == Success)
                {
                    highestTemperature = Math.Max(highestTemperature, temperature);
                    temperatureFound = true;
                }
            }

            return new NvidiaTelemetry(
                utilizationCount == 0 ? null : utilizationTotal / utilizationCount,
                memoryCount == devices.Length ? ToLong(usedMemoryTotal) : null,
                memoryCount == devices.Length ? ToLong(totalMemoryTotal) : null,
                temperatureFound ? highestTemperature : null,
                DriverVersion);
        }

        public void Dispose()
        {
            if (isInitialized)
            {
                _ = shutdown();
                NativeLibrary.Free(library);
                isInitialized = false;
            }
        }

        private static T GetExport<T>(IntPtr library, params string[] exportNames)
            where T : Delegate
        {
            foreach (var exportName in exportNames)
            {
                if (NativeLibrary.TryGetExport(library, exportName, out var address))
                {
                    return Marshal.GetDelegateForFunctionPointer<T>(address);
                }
            }

            throw new EntryPointNotFoundException(
                $"The NVIDIA management library does not export {string.Join(" or ", exportNames)}.");
        }

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private static long ToLong(ulong value) =>
            (long)Math.Min(value, (ulong)long.MaxValue);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlInitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlDeviceCountDelegate(out uint deviceCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlDeviceHandleDelegate(uint index, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlDeviceUtilizationDelegate(
            IntPtr device,
            out NvmlUtilization utilization);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlDeviceMemoryDelegate(
            IntPtr device,
            out NvmlMemoryInfo memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlDeviceTemperatureDelegate(
            IntPtr device,
            uint sensor,
            out uint temperature);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NvmlDriverVersionDelegate(IntPtr version, uint length);

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlMemoryInfo
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }
    }

    private sealed record NvidiaTelemetry(
        double? UsagePercentage,
        long? UsedMemoryBytes,
        long? TotalMemoryBytes,
        double? TemperatureCelsius,
        string? DriverVersion)
    {
        public static NvidiaTelemetry Unavailable { get; } = new(null, null, null, null, null);
    }

    private sealed record VideoAdapterInfo(string? Name, string? DriverVersion);

    private static string FormatBytes(long bytes)
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

    private static string FormatBytes(ulong bytes) =>
        FormatBytes((long)Math.Min(bytes, (ulong)long.MaxValue));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D12CreateDeviceDelegate(
        IntPtr adapter,
        Direct3DFeatureLevel minimumFeatureLevel,
        ref Guid interfaceId,
        out IntPtr device);

    private enum Direct3DFeatureLevel
    {
        Level11_0 = 0xb000,
        Level11_1 = 0xb100,
        Level12_0 = 0xc000,
        Level12_1 = 0xc100,
        Level12_2 = 0xc200
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

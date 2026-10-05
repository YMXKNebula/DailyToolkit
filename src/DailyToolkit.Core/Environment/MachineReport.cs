using System.Text.Json;
using System.Text.Json.Serialization;

namespace DailyToolkit.Core.Environment;

public enum ProbeStage { Basic, Complete, Partial }
public enum RuntimeStatus { Available, Missing, Unknown }

public sealed record SystemInfo(
    string Name, string Version, int Build, string Architecture,
    string ProcessArchitecture, string HostRuntime, string Culture);

public sealed record CpuInfo(string Name, int LogicalProcessors, int? PhysicalCores,
    IReadOnlyList<string> Features);

public sealed record MemoryInfo(long? TotalBytes, long? AvailableBytes);
public sealed record DisplayInfo(int PixelWidth, int PixelHeight, double Scale,
    double WorkAreaWidth, double WorkAreaHeight, bool HighContrast);
public sealed record PowerInfo(bool? OnBattery, int? BatteryPercent);
public sealed record GraphicsInfo(string Name, string? DriverVersion);
public sealed record VolumeInfo(string Name, string FileSystem, long TotalBytes, long FreeBytes);
public sealed record InstalledSoftwareInfo(string Name, string? Version, string? Publisher);
public sealed record ProbeIssue(string Area, string Message);

public sealed record RuntimeInfo(string Id, string Name, RuntimeStatus Status, string? Version)
{
    // The path is useful for launching a backend, but should not appear in exported reports.
    [JsonIgnore]
    public string? ExecutablePath { get; init; }
}

public sealed record MachineReport
{
    public required SystemInfo System { get; init; }
    public required CpuInfo Cpu { get; init; }
    public required MemoryInfo Memory { get; init; }
    public required DisplayInfo Display { get; init; }
    public required PowerInfo Power { get; init; }
    public IReadOnlyList<GraphicsInfo> Graphics { get; init; } = [];
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];
    public IReadOnlyList<RuntimeInfo> Runtimes { get; init; } = [];
    public IReadOnlyList<InstalledSoftwareInfo> Software { get; init; } = [];
    public IReadOnlyList<ProbeIssue> Issues { get; init; } = [];
    public bool SoftwareInventoryCompleted { get; init; }
    public ProbeStage Stage { get; init; } = ProbeStage.Basic;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public double BasicReadMilliseconds { get; init; }
    public double DetailReadMilliseconds { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

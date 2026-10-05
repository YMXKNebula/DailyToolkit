using DailyToolkit.Core.Environment;

namespace DailyToolkit.Core.Tools;

public sealed record ToolRequirements
{
    public IReadOnlyList<string> Architectures { get; init; } = [];
    public IReadOnlyList<string> CpuFeatures { get; init; } = [];
    public int? MinimumWindowsBuild { get; init; }
    public long? MinimumMemoryBytes { get; init; }
    public string? RuntimeId { get; init; }
    public Version? MinimumRuntimeVersion { get; init; }
}

public sealed record CompatibilityResult(bool CanRun, IReadOnlyList<string> Reasons);

public static class CompatibilityEvaluator
{
    public static CompatibilityResult Evaluate(ToolRequirements requirements, MachineReport report)
    {
        var reasons = new List<string>();
        if (requirements.Architectures.Count > 0 &&
            !requirements.Architectures.Contains(report.System.Architecture, StringComparer.OrdinalIgnoreCase))
            reasons.Add($"不支持 {report.System.Architecture} 系统");

        if (requirements.MinimumWindowsBuild is int build && report.System.Build < build)
            reasons.Add($"需要 Windows 内部版本 {build} 或更高版本");

        if (requirements.MinimumMemoryBytes is long memory &&
            (report.Memory.TotalBytes is not long available || available < memory))
            reasons.Add("内存不足，或暂时无法确认内存容量");

        foreach (var feature in requirements.CpuFeatures)
            if (!report.Cpu.Features.Contains(feature, StringComparer.OrdinalIgnoreCase))
                reasons.Add($"处理器不支持 {feature}");

        if (requirements.RuntimeId is { } runtimeId)
        {
            var runtime = report.Runtimes.FirstOrDefault(x =>
                x.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase));
            if (runtime is null || runtime.Status != RuntimeStatus.Available)
                reasons.Add($"需要可用的 {runtime?.Name ?? runtimeId} 运行环境");
            else if (requirements.MinimumRuntimeVersion is { } minimum &&
                (!Version.TryParse(runtime.Version, out var version) || version < minimum))
                reasons.Add($"需要 {runtime.Name} {minimum} 或更高版本");
        }

        return new(reasons.Count == 0, reasons);
    }
}

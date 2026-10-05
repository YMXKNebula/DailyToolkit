namespace DailyToolkit.Core.Environment;

public sealed record AdaptationProfile(int WorkerLimit, bool ReducedEffects,
    bool CompactLayout, string BackendArchitecture, IReadOnlyList<string> Reasons);

public static class AdaptationPolicy
{
    private const long GiB = 1024L * 1024 * 1024;

    public static AdaptationProfile Evaluate(MachineReport report, double viewportWidth)
    {
        var workers = Math.Clamp(report.Cpu.LogicalProcessors / 2, 1, 8);
        var reasons = new List<string>();
        var lowMemory = report.Memory.TotalBytes is > 0 and < 4 * GiB;
        if (report.Memory.TotalBytes is > 0 and < 8 * GiB)
        {
            workers = Math.Min(workers, lowMemory ? 1 : 2);
            reasons.Add("内存较少，减少同时运行的后台任务");
        }

        var lowBattery = report.Power.OnBattery == true && report.Power.BatteryPercent is <= 20;
        if (lowBattery)
        {
            workers = Math.Min(workers, 2);
            reasons.Add("电量较低，减少后台任务和界面效果");
        }

        if (report.Display.HighContrast)
            reasons.Add("Windows 已开启高对比度显示");

        var compact = viewportWidth < 900;
        if (compact)
            reasons.Add("窗口较窄，使用紧凑布局");

        return new(workers, lowMemory || lowBattery || report.Display.HighContrast,
            compact, report.System.Architecture, reasons);
    }
}

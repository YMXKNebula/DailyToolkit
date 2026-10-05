using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Tools;
using DailyToolkit.Core.Gaming;


namespace DailyToolkit.Tests;

internal static partial class Program
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly MachineReport machine = new MachineReport
    {
        System = new("Windows 11", "24H2", 26100, "x64", "x64", "10.0.12", "zh-CN"),
        Cpu = new("Test CPU", 16, 8, ["SSE2", "AVX2"]),
        Memory = new(16 * GiB, 8 * GiB), Display = new(1920, 1080, 1, 1920, 1040, false),
        Power = new(false, 70), Runtimes = [new("python", "Python", RuntimeStatus.Available, "3.12.1")]
    };
    private static void RegisterAdaptationTests()
    {
        Test("Workers bounded by CPU and memory", () =>
        {
            Require(AdaptationPolicy.Evaluate(machine, 1040).WorkerLimit == 8);
            Require(AdaptationPolicy.Evaluate(machine with { Cpu = machine.Cpu with { LogicalProcessors = 1 } }, 1040).WorkerLimit == 1);
            Require(AdaptationPolicy.Evaluate(machine with { Memory = new(6 * GiB, GiB) }, 1040).WorkerLimit == 2);
            var low = AdaptationPolicy.Evaluate(machine with { Memory = new(2 * GiB, GiB) }, 1040);
            Require(low.WorkerLimit == 1 && low.ReducedEffects);
        });
        Test("Battery policy only applies when on battery", () =>
        {
            var battery = AdaptationPolicy.Evaluate(machine with { Power = new(true, 10) }, 1040);
            Require(battery.WorkerLimit == 2 && battery.ReducedEffects);
            Require(AdaptationPolicy.Evaluate(machine with { Power = new(false, 10) }, 1040).WorkerLimit == 8);
        });
        Test("Narrow and high contrast layouts", () =>
        {
            Require(AdaptationPolicy.Evaluate(machine, 680).CompactLayout);
            Require(!AdaptationPolicy.Evaluate(machine, 900).CompactLayout);
            Require(AdaptationPolicy.Evaluate(machine with { Display = machine.Display with { HighContrast = true } }, 1040).ReducedEffects);
        });
        Test("Unknown memory does not pass a memory requirement", () =>
            Require(!CompatibilityEvaluator.Evaluate(new() { MinimumMemoryBytes = GiB }, machine with { Memory = new(null, null) }).CanRun));
        Test("Architecture, instruction set, and Windows requirements", () =>
        {
            Require(CompatibilityEvaluator.Evaluate(new() { Architectures = ["X64"], CpuFeatures = ["avx2"], MinimumWindowsBuild = 22000 }, machine).CanRun);
            var failed = CompatibilityEvaluator.Evaluate(new() { Architectures = ["arm64"], CpuFeatures = ["AdvSimd"], MinimumWindowsBuild = 30000 }, machine);
            Require(!failed.CanRun && failed.Reasons.Count == 3);
        });
        Test("Unavailable, unknown, and older runtimes are blocked", () =>
        {
            var requirement = new ToolRequirements { RuntimeId = "python", MinimumRuntimeVersion = new(3, 11) };
            Require(CompatibilityEvaluator.Evaluate(requirement, machine).CanRun);
            foreach (var status in new[] { RuntimeStatus.Missing, RuntimeStatus.Unknown })
                Require(!CompatibilityEvaluator.Evaluate(requirement, machine with { Runtimes = [new("python", "Python", status, null)] }).CanRun);
            Require(!CompatibilityEvaluator.Evaluate(requirement, machine with { Runtimes = [new("python", "Python", RuntimeStatus.Available, "3.9.1")] }).CanRun);
            Require(!CompatibilityEvaluator.Evaluate(requirement, machine with { Runtimes = [new("python", "Python", RuntimeStatus.Available, "unknown")] }).CanRun);
        });
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyUSE.Core.Environment;
using DailyUSE.Core.Tools;

if (args.FirstOrDefault() == "--worker")
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = new UTF8Encoding(false);
    var request = JsonSerializer.Deserialize<ToolRequest>(Console.ReadLine()!)!;
    switch (args.ElementAtOrDefault(1))
    {
        case "delay":
            if (request.Input.TryGetProperty("pidFile", out var marker))
                await File.WriteAllTextAsync(marker.GetString()!, System.Environment.ProcessId.ToString());
            await Task.Delay(30000);
            break;
        case "failure": return 7;
        case "malformed": Console.Write("not JSON"); return 0;
        case "shape": Console.Write("{}"); return 0;
        case "oversize": Console.Write(new string('x', 2 * 1024 * 1024)); await Task.Delay(30000); return 0;
        case "stderr": Console.Error.Write(new string('x', 100000)); break;
    }
    Console.Write(JsonSerializer.Serialize(new ToolResponse(true, request.Input, null)));
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action check) => tests.Add((name, () => { check(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> check) => tests.Add((name, check));
void Require(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task Expect<TException>(Func<Task> action, string? message = null) where TException : Exception
{
    try { await action(); }
    catch (TException exception)
    {
        if (message is not null) Require(exception.Message.Contains(message));
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}");
}

const long GiB = 1024L * 1024 * 1024;
var machine = new MachineReport
{
    System = new("Windows 11", "24H2", 26100, "x64", "x64", "10.0.12", "zh-CN"),
    Cpu = new("Test CPU", 16, 8, ["SSE2", "AVX2"]),
    Memory = new(16 * GiB, 8 * GiB), Display = new(1920, 1080, 1, 1920, 1040, false),
    Power = new(false, 70), Runtimes = [new("python", "Python", RuntimeStatus.Available, "3.12.1")]
};
var runner = new ProcessToolRunner();
var processPath = System.Environment.ProcessPath!;
ProcessBackend Backend(string mode)
{
    var arguments = new List<string>();
    if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        arguments.Add(typeof(ProcessToolRunner).Assembly.Location.Replace("DailyUSE.Core.dll", "DailyUSE.Tests.dll"));
    arguments.AddRange(["--worker", mode]);
    return new(processPath, arguments);
}
ToolRequest Request(object input) => new("echo", JsonSerializer.SerializeToElement(input));

Test("Weather uses only valid Windows temperature labels", () =>
{
    Require(WindowsWeatherText.Parse("天气，18°C，多云").Available);
    Require(WindowsWeatherText.Parse("Weather 65°F, Partly cloudy").Available);
    Require(WindowsWeatherText.Parse("−2 ℃ 晴").Available);
    Require(WindowsWeatherText.Parse("18 degrees Celsius Cloudy").Available);
    Require(!WindowsWeatherText.Parse("小组件").Available);
    Require(!WindowsWeatherText.Parse("Stock market 1234.5").Available);
    Require(!WindowsWeatherText.Parse(null).Available);
    Require(!WindowsWeatherText.Parse(new string('x', 513) + "18°C").Available);
});
Test("Traffic rates survive adapter changes and resets", () =>
{
    var sampler = new TrafficSampler();
    Require(sampler.Sample([new("wifi", 1000, 500)], 1).ReceivedBytesPerSecond is null);
    var rate = sampler.Sample([new("wifi", 3000, 1000)], 3);
    Require(rate.ReceivedBytesPerSecond == 1000 && rate.SentBytesPerSecond == 250);
    var changed = sampler.Sample([new("vpn", 50000000, 50000000)], 4);
    Require(changed.ReceivedBytesPerSecond is null);
    var reset = sampler.Sample([new("vpn", 0, 0)], 5);
    Require(reset.ReceivedBytesPerSecond == 0 && reset.SentBytesPerSecond == 0);
    Require(sampler.Sample([new("vpn", 100, 100)], 5).ReceivedBytesPerSecond is null);
    Require(sampler.Sample([], 6).ReceivedBytesPerSecond is null);
});

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
Test("Catalog rejects duplicate ids and unknown tools", () =>
{
    var catalog = new ToolCatalog();
    catalog.Register(new("local", "Local", "Test", new()));
    Require(catalog.Check("LOCAL", machine).CanRun && !catalog.Check("missing", machine).CanRun);
    try { catalog.Register(new("LOCAL", "Duplicate", "Test", new())); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("Duplicate was accepted");
});
Test("Export omits runtime executable paths", () =>
{
    var report = machine with { Runtimes = [new RuntimeInfo("python", "Python", RuntimeStatus.Available, "3.12.1") { ExecutablePath = @"C:\Users\private-user\python.exe" }] };
    var json = JsonSerializer.Serialize(report, MachineReport.JsonOptions);
    Require(!json.Contains("private-user") && !json.Contains("ExecutablePath"));
    var restored = JsonSerializer.Deserialize<MachineReport>(json, MachineReport.JsonOptions)!;
    Require(restored.Cpu.Name == machine.Cpu.Name && restored.Runtimes[0].Status == RuntimeStatus.Available);
});
AsyncTest("UTF-8 local subprocess round trip", async () =>
{
    const string text = "本地工具 · 空格 \"引号\" 和 emoji 🌿";
    var result = await runner.RunAsync(Backend("echo"), Request(new { text }), TimeSpan.FromSeconds(10));
    Require(result.Success && result.Data?.GetProperty("text").GetString() == text);
});
AsyncTest("Concurrent stderr is drained", async () =>
    Require((await runner.RunAsync(Backend("stderr"), Request(new { value = 42 }), TimeSpan.FromSeconds(10))).Success));
AsyncTest("Backend exit failures are reported", () => Expect<ToolRunException>(() => runner.RunAsync(Backend("failure"), Request(new { }), TimeSpan.FromSeconds(10)), "7"));
AsyncTest("Malformed responses are rejected", async () =>
{
    await Expect<ToolRunException>(() => runner.RunAsync(Backend("malformed"), Request(new { }), TimeSpan.FromSeconds(10)), "格式");
    await Expect<ToolRunException>(() => runner.RunAsync(Backend("shape"), Request(new { }), TimeSpan.FromSeconds(10)), "格式");
});
AsyncTest("Oversized responses fail before the timeout", async () =>
{
    var timer = Stopwatch.StartNew();
    await Expect<ToolRunException>(() => runner.RunAsync(Backend("oversize"), Request(new { }), TimeSpan.FromSeconds(15)), "过多");
    Require(timer.Elapsed < TimeSpan.FromSeconds(10));
});
AsyncTest("Timeout terminates the local backend", async () =>
{
    var marker = Path.Combine(Path.GetTempPath(), $"dailyuse-test-{Guid.NewGuid():N}.pid");
    try
    {
        await Expect<ToolRunException>(() => runner.RunAsync(Backend("delay"), Request(new { pidFile = marker }), TimeSpan.FromSeconds(2)), "超时");
        Require(File.Exists(marker), "Backend did not start");
        var pid = int.Parse(await File.ReadAllTextAsync(marker));
        try { using var process = Process.GetProcessById(pid); Require(process.HasExited, "Backend remained alive"); }
        catch (ArgumentException) { }
    }
    finally { if (File.Exists(marker)) File.Delete(marker); }
});
AsyncTest("Caller cancellation remains cancellation", async () =>
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    await Expect<OperationCanceledException>(() => runner.RunAsync(Backend("delay"), Request(new { }), TimeSpan.FromSeconds(15), cancellation.Token));
});
AsyncTest("Invalid paths and protocol versions do not launch", async () =>
{
    await Expect<ToolRunException>(() => runner.RunAsync(new("relative.exe", []), Request(new { }), TimeSpan.FromSeconds(1)));
    await Expect<ToolRunException>(() => runner.RunAsync(Backend("echo"), Request(new { }) with { ProtocolVersion = 2 }, TimeSpan.FromSeconds(1)));
});

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

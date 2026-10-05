using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Tools;
using DailyToolkit.Core.Gaming;


namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static readonly ProcessToolRunner runner = new ProcessToolRunner();
    private static readonly string processPath = System.Environment.ProcessPath!;
    private static ProcessBackend Backend(string mode)
    {
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(typeof(ProcessToolRunner).Assembly.Location.Replace("DailyToolkit.Core.dll", "DailyToolkit.Tests.dll"));
        arguments.AddRange(["--worker", mode]);
        return new(processPath, arguments);
    }
    private static ToolRequest Request(object input) => new("echo", JsonSerializer.SerializeToElement(input));

    private static async Task<int> RunWorkerAsync(string[] args)
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
    private static void RegisterProcessTests()
    {
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
            var marker = Path.Combine(Path.GetTempPath(), $"dailytoolkit-test-{Guid.NewGuid():N}.pid");
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
    }
}

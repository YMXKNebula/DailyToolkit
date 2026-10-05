using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Tools;
using DailyToolkit.Core.Gaming;


namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterCatalogTests()
    {
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
    }
}

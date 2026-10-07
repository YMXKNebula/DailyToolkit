using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Tools;
using DailyToolkit.Core.Gaming;


namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static readonly List<(string Name, Func<Task> Run)> tests = new List<(string Name, Func<Task> Run)>();
    private static void Test(string name, Action check) => tests.Add((name, () => { check(); return Task.CompletedTask; }));
    private static void AsyncTest(string name, Func<Task> check) => tests.Add((name, check));
    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Expect<TException>(Func<Task> action, string? message = null) where TException : Exception
    {
        try { await action(); }
        catch (TException exception)
        {
            if (message is not null) Require(exception.Message.Contains(message));
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}");
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--worker") return await RunWorkerAsync(args);
        RegisterLensTests();
        RegisterLensMovementTests();
        RegisterLocalStatusTests();
        RegisterAdaptationTests();
        RegisterCatalogTests();
        RegisterProcessTests();
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception.Message}"); }
        }
        Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
        return failures == 0 ? 0 : 1;
    }
}

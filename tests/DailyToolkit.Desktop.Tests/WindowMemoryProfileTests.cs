using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckWindowMemoryProfileAsync(string report)
    {
        Require(ClipboardTestStation.Name.Length > 0 && !ClipboardTestStation.Name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase),
            "Memory profiling requires the private fixture window station");
        report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-window-memory-");
        var samples = new List<object>();
        var watch = Stopwatch.StartNew();
        MainViewModel? model = null;
        MainWindow? window = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = "failed";
        double previousCpu = 0;
        try
        {
            await SampleAsync("process-ready", 1000);
            model = ResourceHost(fixture.FullName);
            await Task.WhenAll(model.Notes.Ready, model.ClipRecall.Ready, model.FilePack.SettingsReady);
            model.Gaming.IsEnabled = false;
            model.Notes.IsEnabled = false;
            model.ClipRecall.RequestEnabled(false);
            await model.ClipRecall.PendingSettings;
            Require(await model.Notes.FlushAsync() && await model.ClipRecall.FlushSettingsAsync() && await model.FilePack.SetEnabledAsync(false),
                "Could not persist disabled fixture tool settings");
            model.Notes.ReleaseInactiveContent();
            model.CloseToTray = false; model.MinimizeToTray = false;
            Require(!model.IsHome && !model.Gaming.IsEnabled && !model.Notes.IsEnabled && !model.ClipRecall.IsEnabled && !model.FilePack.IsEnabled,
                "Memory profile tools were not disabled");
            await SampleAsync("model-ready", 1000);
            window = new MainWindow(model, enableShortcuts: false)
            {
                Width = 1040, Height = 760, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 80, Top = 80
            };
            window.Closed += (_, _) => closed.TrySetResult();
            // The eager baseline includes this settings view. Supply its existing
            // in-memory adapter before Loaded; a lazy candidate need not create it.
            if (window.FindName("FilePackSettings") is FilePackSettingsView filePackSettings)
                filePackSettings.ContextMenuRegistration = new FilePackContextMenuRegistration(new FilePackMenuFake());
            await SampleAsync("window-constructed", 1000);
            window.Show(); window.UpdateLayout();
            await SampleAsync("visible-idle", 3000);
            window.Hide();
            await SampleAsync("hidden-idle", 3000);
            window.Show(); window.UpdateLayout();
            await SampleAsync("reopened-idle", 3000);
            window.Hide();
            await SampleAsync("hidden-again-idle", 3000);
            Require(!window.IsLensHotkeyRegistered && !window.TrayAvailable && !window.IsLocalPolling && !model.Gaming.IsActive,
                "Isolated profile unexpectedly started shortcuts, tray, polling or live capture");
            status = "passed";
        }
        finally
        {
            try
            {
                if (window is not null && !closed.Task.IsCompleted)
                { window.RequestExit(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                if (model is not null)
                {
                    await model.ClipRecall.DisposeAsync();
                    await model.FilePack.DisposeAsync();
                    if (window is null) model.Dispose();
                }
            }
            catch { status = "failed"; throw; }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, status, scope = "private-window-station-temporary-data-no-live-input",
                    informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    desktopAssemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant(),
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    windowSize = new { width = 1040, height = 760 }, samples,
                    note = "Fresh process, fixed stage waits, disabled tools and isolated stores. Counters precede tree inspection. No forced GC, working-set trimming, live input, real clipboard, registration or installed application. Private-station measurements compare this fixture only; they do not establish interactive-desktop memory use."
                }, new JsonSerializerOptions { WriteIndented = true }));
                FilePackPaths.EnsureNoReparsePoints(fixture.FullName);
                Require(FilePackPaths.IsWithin(fixture.FullName, Path.GetTempPath()), "Memory fixture escaped temporary storage");
                fixture.Delete(recursive: true);
            }
        }
        Console.WriteLine("PASS Isolated window memory profile: " + report);

        async Task SampleAsync(string stage, int settleMilliseconds)
        {
            await Task.Delay(settleMilliseconds);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var elapsedSeconds = watch.Elapsed.TotalSeconds;
            var privateBytes = process.PrivateMemorySize64; var workingSetBytes = process.WorkingSet64;
            var privateWorkingSetBytes = ReadPrivateWorkingSet(process);
            var handles = process.HandleCount; var threads = process.Threads.Count;
            var cpuSeconds = process.TotalProcessorTime.TotalSeconds;
            var managedBytes = GC.GetTotalMemory(false); var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            var gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var inspectionStart = GC.GetAllocatedBytesForCurrentThread();
            var visual = InspectMemoryTree(window, visual: true);
            var logical = InspectMemoryTree(window, visual: false);
            var inspectionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - inspectionStart;
            samples.Add(new { stage, elapsedSeconds, settleMilliseconds, privateBytes, workingSetBytes, privateWorkingSetBytes, handles, threads,
                cpuSeconds, cpuSecondsSincePrevious = cpuSeconds - previousCpu, managedBytes, allocatedBytes, gcCollections,
                windowVisible = window?.IsVisible ?? false, visualTree = visual, logicalTree = logical, inspectionAllocatedBytes });
            previousCpu = cpuSeconds;
            Console.WriteLine($"PROFILE {stage}: private={privateBytes / 1048576d:F2} MiB, working={workingSetBytes / 1048576d:F2} MiB, managed={managedBytes / 1048576d:F2} MiB");
        }
    }

    private static object InspectMemoryTree(DependencyObject? root, bool visual)
    {
        var pending = new Stack<(DependencyObject Node, int Depth)>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var types = new Dictionary<string, int>();
        var nodes = 0; var frameworkElements = 0; var controls = 0; var effectiveVisible = 0; var collapsed = 0; var maximumDepth = 0;
        if (root is not null) pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            if (!seen.Add(item.Node)) continue;
            nodes++; maximumDepth = Math.Max(maximumDepth, item.Depth);
            if (item.Node is FrameworkElement) frameworkElements++;
            if (item.Node is Control) controls++;
            if (item.Node is UIElement element) { if (element.IsVisible) effectiveVisible++; if (element.Visibility == Visibility.Collapsed) collapsed++; }
            var type = item.Node.GetType().FullName ?? item.Node.GetType().Name;
            types[type] = types.GetValueOrDefault(type) + 1;
            if (visual)
            {
                if (item.Node is not (Visual or Visual3D)) continue;
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item.Node); index++)
                    pending.Push((VisualTreeHelper.GetChild(item.Node, index), item.Depth + 1));
            }
            else foreach (var child in LogicalTreeHelper.GetChildren(item.Node).OfType<DependencyObject>()) pending.Push((child, item.Depth + 1));
        }
        return new { nodes, frameworkElements, controls, effectiveVisible, collapsed, maximumDepth,
            topTypes = types.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(12).Select(item => new { type = item.Key, count = item.Value }).ToArray() };
    }
}

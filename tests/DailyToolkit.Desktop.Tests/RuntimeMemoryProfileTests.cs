using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckRuntimeMemoryProfileAsync(string report, bool releaseUi = true,
        bool optimizedCollectionExperiment = false, bool aggressiveCollectionExperiment = false, bool productionIdlePolicy = false)
    {
        RequireRuntimeFixtureStation();
        report = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-memory-");
        var samples = new List<object>(); var openTimings = new List<double>(); var releaseTimings = new List<double>();
        var collectionRequests = new List<object>();
        var watch = Stopwatch.StartNew();
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        var status = "failed"; double previousCpu = 0; var collectionExperimentPerformed = false;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            runtime = new(model, enableRuntime: true, idleDelay: productionIdlePolicy ? null : TimeSpan.FromMinutes(5), shutdownApplication: false);
            await Task.WhenAll(runtime.Notes!.Ready, runtime.ClipRecall!.Ready, runtime.FilePack!.Ready);
            runtime.WindowCreated += ConfigureRuntimeFixtureWindow;
            Require(runtime.Window is null && runtime.SourceHandle != IntPtr.Zero && ReviewIsWindow(runtime.SourceHandle),
                "Cold runtime profile created UI or did not retain its isolated message source");
            await SampleAsync("cold-host", 1000);
            ShowProfileWindow(); SetRuntimeProfileWindowSize(runtime);
            await VisitSettingsForMemoryProfileAsync(model, runtime);
            await SampleAsync("visible-idle", 3000);
            await HideProfileWindowAsync();
            await SampleAsync(releaseUi ? "hidden-views-released" : "hidden-views-retained", productionIdlePolicy ? 6500 : 3000);
            if (productionIdlePolicy)
                Require(runtime.IdleMemoryCollectionCount == 1 && runtime.Window is null && ReviewIsWindow(runtime.SourceHandle),
                    "Fully disabled production idle policy did not retire its main UI/HWND and collect once: " + DescribeIdleMemoryFixture(runtime, model));
            ShowProfileWindow();
            await SampleAsync("reopened-idle", 3000);
            await HideProfileWindowAsync();
            await SampleAsync(releaseUi ? "released-again" : "retained-again", productionIdlePolicy ? 6500 : 3000);
            await SampleAsync(releaseUi ? "released-long-idle" : "retained-long-idle", 15000);
            Require(!runtime.LensShortcuts!.IsRegistered && !runtime.LensShortcuts.HasMessageHook &&
                runtime.Notes!.NoteWindow is null && !runtime.ClipRecall!.IsListening && !runtime.ClipRecall.HasCaptureProcessor &&
                runtime.ClipRecall.Panel is null && !runtime.FilePack!.IsHotkeyRegistered &&
                runtime.FilePack.QuickWindow is null && runtime.FilePack.PackWindow is null,
                "Disabled production controllers unexpectedly started capture, hooks or independent windows");
            status = "passed";
        }
        finally
        {
            try
            {
                await CloseRuntimeFixtureAsync(runtime, model, fixture);
            }
            catch { status = "failed"; throw; }
            finally
            {
                var assembly = typeof(MainWindow).Assembly;
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, status, scope = "private-window-station-production-controllers-disabled-tools-temporary-data",
                    informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    desktopAssemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant(),
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    releaseUi, releaseMode = productionIdlePolicy ? "all-tools-disabled-full-main-ui-retirement" :
                        releaseUi ? "deferred-pages-stable-main-window" : "retain-settings-pages", settingsPagesVisited = true,
                    optimizedCollectionExperiment, aggressiveCollectionExperiment, productionIdlePolicy,
                    synchronousShowMilliseconds = openTimings, collectionRequests,
                    synchronousHideAndReleaseMilliseconds = releaseTimings,
                    windowSize = new { width = 1040, height = 760 }, samples,
                    note = "Fresh process; fixed waits; production controllers with all tools disabled; startup registration is a fake. The private station has no Explorer tray, so the normal tray retry may run; availability is recorded. No working-set trimming, real clipboard, registered shortcuts, live input or installed application. Ordinary profiles never request GC from test code; productionIdlePolicy records actual guarded product collection. Explicit experiments request one labelled collection after the first real release, or first hide in the retained control. Private-station observations do not establish interactive-desktop memory usage."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Console.WriteLine("PASS Isolated runtime UI-unload memory profile: " + report);

        void ShowProfileWindow()
        {
            var opening = Stopwatch.StartNew(); runtime!.ShowWindow();
            openTimings.Add(opening.Elapsed.TotalMilliseconds);
        }

        async Task HideProfileWindowAsync()
        {
            if (productionIdlePolicy) { runtime!.Window!.Hide(); return; }
            if (!releaseUi)
            {
                runtime!.Window!.Hide();
                if (aggressiveCollectionExperiment && !collectionExperimentPerformed) await RequestExperimentalCollectionAsync();
                return;
            }
            var (released, milliseconds) = HideAndReleaseRuntimeProfileWindow(runtime!);
            releaseTimings.Add(milliseconds);
            // An empty second hide is not a new retirement and must not request
            // collection even in this explicitly selected test-only experiment.
            if ((optimizedCollectionExperiment || aggressiveCollectionExperiment) && released && !collectionExperimentPerformed)
                await RequestExperimentalCollectionAsync();
        }

        async Task RequestExperimentalCollectionAsync()
        {
            collectionExperimentPerformed = true;
            await Task.Delay(250);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var before = GC.CollectionCount(GC.MaxGeneration);
            var request = Stopwatch.StartNew();
            if (aggressiveCollectionExperiment)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            else GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            collectionRequests.Add(new { mode = aggressiveCollectionExperiment ? "Aggressive" : "Optimized",
                requestMilliseconds = request.Elapsed.TotalMilliseconds,
                fullCollectionsBefore = before, fullCollectionsAtReturn = GC.CollectionCount(GC.MaxGeneration) });
        }

        async Task SampleAsync(string stage, int settleMilliseconds)
        {
            await Task.Delay(settleMilliseconds);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var snapshot = CaptureRuntimeMemorySample(runtime!, stage, watch.Elapsed.TotalSeconds, settleMilliseconds, previousCpu, out var cpu);
            samples.Add(snapshot); previousCpu = cpu;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetRuntimeProfileWindowSize(ApplicationRuntimeHost runtime)
    { runtime.Window!.Width = 1040; runtime.Window.Height = 760; runtime.Window.UpdateLayout(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool Released, double Milliseconds) HideAndReleaseRuntimeProfileWindow(ApplicationRuntimeHost runtime)
    {
        var watch = Stopwatch.StartNew();
        var source = runtime.SourceHandle;
        runtime.Window!.Hide();
        var released = runtime.TryReleaseHiddenViews();
        Require(runtime.Window is { CreatedSettingsViewCount: 0 } && runtime.SourceHandle == source && ReviewIsWindow(source),
            "Profile did not release deferred views while preserving its main window and message source");
        return (released, watch.Elapsed.TotalMilliseconds);
    }

    private static async Task VisitSettingsForMemoryProfileAsync(MainViewModel model, ApplicationRuntimeHost runtime)
    {
        model.Page = "floating-notes"; await SettleRuntimeFixtureAsync();
        model.Page = "clip-recall"; await SettleRuntimeFixtureAsync();
        model.Page = "file-pack"; ConfigureRuntimeProfileFilePack(runtime); await SettleRuntimeFixtureAsync();
        Require(runtime.Window!.CreatedSettingsViewCount == 3, "Profile did not visit its three settings pages");
        model.Page = "screen-lens"; await SettleRuntimeFixtureAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConfigureRuntimeProfileFilePack(ApplicationRuntimeHost runtime)
    {
        ((DailyToolkit.Desktop.FilePack.FilePackSettingsView)runtime.Window!.FindName("FilePackSettings")).ContextMenuRegistration =
            new(new FilePackMenuFake());
    }

    // Keep MainWindow locals out of the async profile state machine. Returning
    // only numeric/tree summaries must not keep a released visual tree alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object CaptureRuntimeMemorySample(ApplicationRuntimeHost runtime, string stage, double elapsedSeconds,
        int settleMilliseconds, double previousCpu, out double cpuSeconds)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var privateBytes = process.PrivateMemorySize64; var workingSetBytes = process.WorkingSet64;
        var privateWorkingSetBytes = ReadPrivateWorkingSet(process);
        var handles = process.HandleCount; var threads = process.Threads.Count; cpuSeconds = process.TotalProcessorTime.TotalSeconds;
        var managedBytes = GC.GetTotalMemory(false); var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var gcInfo = GC.GetGCMemoryInfo();
        var lastGcMaxPauseMilliseconds = 0d;
        foreach (var pause in gcInfo.PauseDurations) lastGcMaxPauseMilliseconds = Math.Max(lastGcMaxPauseMilliseconds, pause.TotalMilliseconds);
        var window = runtime.Window; var hwnd = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        var inspectionStart = GC.GetAllocatedBytesForCurrentThread();
        var visual = InspectMemoryTree(window, visual: true); var logical = InspectMemoryTree(window, visual: false);
        var inspectionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - inspectionStart;
        Console.WriteLine($"PROFILE {stage}: private={privateBytes / 1048576d:F2} MiB, working={workingSetBytes / 1048576d:F2} MiB, managed={managedBytes / 1048576d:F2} MiB, ui={window is not null}");
        return new { stage, elapsedSeconds, settleMilliseconds, privateBytes, workingSetBytes, privateWorkingSetBytes, handles, threads,
            cpuSeconds, cpuSecondsSincePrevious = cpuSeconds - previousCpu, managedBytes, allocatedBytes, gcCollections,
            lastGcMaxPauseMilliseconds,
            sourceHwndAlive = ReviewIsWindow(runtime.SourceHandle), uiWindowPresent = window is not null,
            trayAvailable = runtime.TrayAvailable,
            createdSettingsViews = window?.CreatedSettingsViewCount ?? 0,
            idleMemoryCollections = runtime.IdleMemoryCollectionCount,
            lastIdleMemoryCollectionMilliseconds = runtime.LastIdleMemoryCollectionMilliseconds,
            lastIdleUiRetirementMilliseconds = runtime.LastIdleUiRetirementMilliseconds,
            lastIdleAttemptHiddenMilliseconds = runtime.LastIdleAttemptHiddenMilliseconds,
            uiWindowVisible = window?.IsVisible ?? false, uiHwndAlive = hwnd != IntPtr.Zero && ReviewIsWindow(hwnd),
            applicationWindowCount = Application.Current.Windows.Count, visualTree = visual, logicalTree = logical, inspectionAllocatedBytes };
    }
}

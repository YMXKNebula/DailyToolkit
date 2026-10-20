using System.IO;
using System.Windows.Interop;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckIdleMemoryPolicyAsync()
    {
        RequireRuntimeFixtureStation();
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-idle-memory-policy-");
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            Require(model.Notes.SetShortcut(false, null, null) && model.Notes.SetShortcut(true, null, null) &&
                await model.Notes.FlushAsync(), "Idle-memory fixture could not clear its shortcuts");
            runtime = new(model, enableRuntime: true, idleDelay: TimeSpan.FromMilliseconds(150), shutdownApplication: false);
            runtime.WindowCreated += ConfigureRuntimeFixtureWindow;
            await Task.WhenAll(runtime.Notes!.Ready, runtime.ClipRecall!.Ready, runtime.FilePack!.Ready);
            runtime.ShowWindow(); await VisitSettingsForMemoryProfileAsync(model, runtime);
            var window = runtime.Window!;
            var handle = new WindowInteropHelper(window).Handle;
            var source = runtime.SourceHandle;
            Require(!runtime.TryQueueIdleMemoryReclaim() && runtime.IdleMemoryCollectionCount == 0 &&
                window.CreatedSettingsViewCount == 3, "Visible UI was retired or collected by the idle policy");

            model.Notes.IsEnabled = true; await runtime.Notes.Pending;
            window.Hide(); await Task.Delay(400);
            Require(runtime.IdleMemoryCollectionCount == 0 && window.CreatedSettingsViewCount == 3 &&
                !runtime.HasPendingIdleMemoryReclaim && model.Notes.IsEnabled,
                "The idle policy collected while a feature remained enabled, or kept polling a veto");
            model.Notes.IsEnabled = false; await runtime.Notes.Pending;
            await WaitNativeFixtureAsync(() => model.Notes.IsIdleForMemoryCollection,
                "Disabled notes did not finish its ordinary save debounce before the accelerated idle fixture");
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            window.Hide();
            // Hold only this private fixture's dispatcher briefly so the real
            // hide delay elapses before its timer callback can run. Queue the
            // retirement explicitly, then model a tray reopen before cleanup.
            Thread.Sleep(200);
            Require(runtime.TryQueueIdleMemoryReclaim(), DescribeIdleMemoryFixture(runtime, model));
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            Require(window.IsVisible && runtime.IdleMemoryCollectionCount == 0 && !runtime.HasPendingIdleMemoryReclaim &&
                new WindowInteropHelper(window).Handle == handle && runtime.SourceHandle == source,
                "Reopening did not cancel queued idle collection on the retained window");

            Require(window.CreatedSettingsViewCount == 0,
                "Reopening on the lens page eagerly recreated unvisited settings pages");
            window.Hide();
            await WaitNativeFixtureAsync(() => runtime.IdleMemoryCollectionCount == 1 && !runtime.HasPendingIdleMemoryReclaim,
                "Fully disabled idle runtime did not finish its single collection: " + DescribeIdleMemoryFixture(runtime, model), 8000);
            Require(window.CreatedSettingsViewCount == 0 && window.IsDeferredAutomationCleanupComplete &&
                ReferenceEquals(runtime.Window, window) && ReviewIsWindow(handle) && runtime.SourceHandle == source &&
                !model.Gaming.HasCaptureResources && runtime.Notes.NoteWindow is null && !runtime.ClipRecall.IsListening &&
                !runtime.ClipRecall.HasCaptureProcessor && runtime.FilePack.QuickWindow is null && runtime.FilePack.PackWindow is null,
                "Idle collection interrupted its host or left disabled runtime resources alive");

            runtime.ShowWindow(); await VisitSettingsForMemoryProfileAsync(model, runtime);
            window.Hide(); await Task.Delay(400);
            Require(runtime.IdleMemoryCollectionCount == 1 && window.CreatedSettingsViewCount == 3 &&
                !runtime.HasPendingIdleMemoryReclaim,
                "Rapid reopening caused another blocking collection or a recurring idle timer during cooldown");
            Console.WriteLine($"PASS Guarded idle memory policy: visible/enabled veto, queued reopen cancellation, completed UIA cleanup, one collection, retained HWND/controllers and cooldown without polling; collection={runtime.LastIdleMemoryCollectionMilliseconds:F2} ms (fixture observation, not a latency guarantee)");
        }
        finally { await CloseRuntimeFixtureAsync(runtime, model, fixture); }
    }

    private static string DescribeIdleMemoryFixture(ApplicationRuntimeHost runtime, MainViewModel model) =>
        $"Idle memory eligibility: notes={model.Notes.IsIdleForMemoryCollection}, pack={model.FilePack.IsIdleForMemoryCollection}, " +
        $"ui={runtime.Window?.CanReclaimHiddenUiMemory}, views={runtime.Window?.CreatedSettingsViewCount}, " +
        $"heap={GC.GetTotalMemory(false)}, hiddenMs={runtime.LastIdleAttemptHiddenMilliseconds:F2}, collections={runtime.IdleMemoryCollectionCount}";
}

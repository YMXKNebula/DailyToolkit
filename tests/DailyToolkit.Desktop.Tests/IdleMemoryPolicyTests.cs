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
            await WaitNativeFixtureAsync(() => runtime.Window is null && !runtime.HasPendingIdleMemoryReclaim,
                "Disabling the final feature while already hidden did not retire the main UI without reopening", 8000);
            Require(runtime.SourceHandle == source && ReviewIsWindow(source), "Automatic UI retirement closed the stable message source");
            runtime.ShowWindow(); await VisitSettingsForMemoryProfileAsync(model, runtime);
            window = runtime.Window!;
            var beforeReopenCollectionCount = runtime.IdleMemoryCollectionCount;
            window.Hide();
            // Hold only this private fixture's dispatcher briefly so the real
            // hide delay elapses before its timer callback can run. Queue the
            // retirement explicitly, then model a tray reopen before cleanup.
            Thread.Sleep(200);
            Require(runtime.TryQueueIdleMemoryReclaim(), DescribeIdleMemoryFixture(runtime, model));
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            window = runtime.Window!;
            Require(window.IsVisible && runtime.IdleMemoryCollectionCount == beforeReopenCollectionCount &&
                !runtime.HasPendingIdleMemoryReclaim && runtime.SourceHandle == source && ReferenceEquals(window.DataContext, model),
                "Reopening did not cancel queued cleanup/collection or lost its stable model/message source");

            Require(window.CreatedSettingsViewCount == 0,
                "Reopening on the lens page eagerly recreated unvisited settings pages");
            var handle = new WindowInteropHelper(window).Handle;
            window.Hide();
            await WaitNativeFixtureAsync(() => runtime.Window is null && runtime.IdleMemoryCollectionCount == 1 && !runtime.HasPendingIdleMemoryReclaim,
                "Fully disabled runtime with no deferred pages did not retire its main UI: " + DescribeIdleMemoryFixture(runtime, model), 8000);
            Require(window.CreatedSettingsViewCount == 0 && window.IsDeferredAutomationCleanupComplete &&
                !ReviewIsWindow(handle) && runtime.SourceHandle == source &&
                !model.Gaming.HasCaptureResources && runtime.Notes.NoteWindow is null && !runtime.ClipRecall.IsListening &&
                !runtime.ClipRecall.HasCaptureProcessor && runtime.FilePack.QuickWindow is null && runtime.FilePack.PackWindow is null,
                "Idle collection interrupted its host or left disabled runtime resources alive");

            runtime.ShowWindow(); await VisitSettingsForMemoryProfileAsync(model, runtime);
            window = runtime.Window!;
            window.Hide(); await Task.Delay(400);
            await WaitNativeFixtureAsync(() => runtime.Window is null && !runtime.HasPendingIdleMemoryReclaim,
                "GC cooldown incorrectly retained a safely disposable hidden UI", 8000);
            Require(runtime.IdleMemoryCollectionCount == 1,
                "Rapid reopening caused another blocking collection during cooldown");

            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            var largeLiveFixture = new byte[70 * 1024 * 1024];
            largeLiveFixture[0] = 1;
            Require(GC.GetTotalMemory(false) > 64 * 1024 * 1024, "Large-heap fixture did not exceed the collection eligibility limit");
            window = runtime.Window!; handle = new WindowInteropHelper(window).Handle; window.Hide();
            await WaitNativeFixtureAsync(() => runtime.Window is null && !runtime.HasPendingIdleMemoryReclaim,
                "Large managed heap incorrectly prevented safe main UI/HWND retirement", 8000);
            Require(!ReviewIsWindow(handle) && runtime.IdleMemoryCollectionCount == 1,
                "Large-heap retirement retained the HWND or bypassed the bounded collection policy");
            GC.KeepAlive(largeLiveFixture);

            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            var probe = model.InitializeAsync();
            await WaitNativeFixtureAsync(() => model.IsRefreshing, "Deferred probe fixture never started");
            window = runtime.Window!; window.Hide(); await Task.Delay(400);
            Require(ReferenceEquals(runtime.Window, window) && runtime.HasPendingIdleMemoryReclaim,
                "In-flight explicit work was retired or its completion was not observed");
            model.CancelPending(); await probe;
            await WaitNativeFixtureAsync(() => runtime.Window is null && !runtime.HasPendingIdleMemoryReclaim,
                "Actual work completion after the initial hide attempt did not rearm UI retirement", 8000);
            await Task.Delay(400);
            Require(!runtime.HasPendingIdleMemoryReclaim && runtime.IdleMemoryCollectionCount == 1,
                "Stable hidden runtime kept a polling/collection timer after its completion-triggered attempt");
            Console.WriteLine($"PASS Guarded idle memory policy: visible/enabled veto, final-disable and work-completion rearm, queued reopen cancellation, completed UIA cleanup, actual HWND retirement, zero deferred pages and >64 MiB heap release, one bounded collection and cooldown without polling; collection={runtime.LastIdleMemoryCollectionMilliseconds:F2} ms (fixture observation, not a latency guarantee)");
        }
        catch
        {
            if (runtime is not null && model is not null) Console.WriteLine("INFO " + DescribeIdleMemoryFixture(runtime, model));
            throw;
        }
        finally { await CloseRuntimeFixtureAsync(runtime, model, fixture); }
    }

    private static string DescribeIdleMemoryFixture(ApplicationRuntimeHost runtime, MainViewModel model) =>
        $"Idle memory eligibility: notes={model.Notes.IsIdleForMemoryCollection}, pack={model.FilePack.IsIdleForMemoryCollection}, " +
        $"ui={runtime.Window?.CanReclaimHiddenUiMemory}, views={runtime.Window?.CreatedSettingsViewCount}, " +
        $"heap={GC.GetTotalMemory(false)}, hiddenMs={runtime.LastIdleAttemptHiddenMilliseconds:F2}, collections={runtime.IdleMemoryCollectionCount}, " +
        $"pendingReclaim={runtime.HasPendingIdleMemoryReclaim}, notesSaveScheduled={model.Notes.IsSaveScheduled}, " +
        $"notesPending={runtime.Notes?.Pending.Status}, cleanup={runtime.Window?.DeferredAutomationCleanup.Status}, " +
        $"uiBusy={runtime.Window?.IsUiBusy}, localPolling={runtime.Window?.IsLocalPolling}, refreshing={model.IsRefreshing}";
}

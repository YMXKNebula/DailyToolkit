using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static MainViewModel ResourceHost(string root)
    {
        var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
        return new(new ControlledProbe(display), display, new LocalProbe(),
            favoritesStore: new FavoritesStore(Path.Combine(root, "favorites.json")),
            gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(root, "gaming.json")),
            appPreferencesStore: new AppPreferencesStore(Path.Combine(root, "app.json")),
            navigationStore: new NavigationOrderStore(Path.Combine(root, "navigation.json")),
            startupRegistration: new StartupFake(), notesStore: new NotesStore(Path.Combine(root, "Notes")),
            clipboardSettingsStore: new ClipboardSettingsStore(Path.Combine(root, "Clipboard")),
            filePackStore: new FilePackStore(Path.Combine(root, "FilePack")));
    }

    private sealed class ResourceBroker(Func<ValueTask> dispose) : IAsyncDisposable
    { public ValueTask DisposeAsync() => dispose(); }

    private static async Task CheckRequestRuntimeAsync()
    {
        var active = 0; var created = 0;
        await using var runtime = new FilePackRequestRuntime(() =>
        {
            Require(++active == 1, "Enabling started a second IPC server before the old server closed");
            created++;
            return new ResourceBroker(async () => { await Task.Delay(10); active--; });
        }, error => throw new InvalidOperationException("IPC runtime transition failed", error));
        runtime.Configure(false); await runtime.Pending;
        Require(created == 0 && !runtime.IsActive, "Disabled startup allocated an IPC server");
        for (var cycle = 0; cycle < 12; cycle++)
        {
            runtime.Configure(true); await runtime.Pending;
            Require(runtime.IsActive && active == 1, "Enabling did not start the IPC server");
            runtime.Configure(false); await runtime.Pending;
            Require(!runtime.IsActive && active == 0, "Disabling retained the IPC server");
            runtime.Configure(true); runtime.Configure(false); runtime.Configure(true);
            await runtime.Pending;
            Require(runtime.IsActive && active == 1, "Rapid toggle broke IPC state");
            runtime.Configure(false); await runtime.Pending;
        }
    }

    private static async Task CheckClipboardResourceLifecycleAsync(int cycles = 8)
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-background-");
        MainViewModel? host = null;
        ClipboardHistoryController? controller = null;
        try
        {
            host = ResourceHost(directory.FullName);
            var model = host.ClipRecall;
            await model.Ready;
            // No global hotkey is registered by this fixture, even on WinSta0.
            Require(model.SetShortcut(null, []) && await model.FlushSettingsAsync(), "Fixture shortcut settings failed");
            using var source = ClipboardTestWindow();
            var access = new FakeClipboardAccess();
            controller = new(host, source, () => [], access: access, cursor: () => (80, 80));
            await controller.Ready;
            Require(!controller.HasCaptureProcessor && !controller.IsListening && !model.HasMaintenanceTimer && !model.Repository.IsReady,
                "Disabled clipboard startup retained runtime work");
            Guid? first = null;
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                model.RequestEnabled(true); await model.PendingSettings; await controller.Pending;
                Require(controller.HasCaptureProcessor && controller.IsListening && !controller.IsHotkeyRegistered,
                    $"Enabled clipboard runtime or fixture shortcut state is wrong: cycle={cycle}, enabled={model.IsEnabled}, paused={model.IsPaused}, processor={controller.HasCaptureProcessor}, listening={controller.IsListening}, hotkey={controller.IsHotkeyRegistered}, repository={model.Repository.IsReady}, failure={controller.ConfigurationFailureCode}, notice={model.Notice}");
                var record = await model.Repository.CaptureAsync(ClipboardContent.FromText("isolated retained fixture " + cycle),
                    DateTimeOffset.UtcNow, null, model.Settings, manual: true);
                first ??= record.Id;
                model.IsPaused = true; await model.PendingSettings; await controller.Pending;
                Require(!controller.HasCaptureProcessor && !controller.IsListening, "Pause retained the processing pumps or native listener");
                model.RequestEnabled(false); await model.PendingSettings; await controller.Pending;
                Require(!controller.HasCaptureProcessor && !controller.IsListening && !controller.IsHotkeyRegistered &&
                    controller.Panel is null && !model.HasMaintenanceTimer && !model.Repository.IsReady &&
                    model.Entries.Count == 0 && model.ThumbnailCount == 0,
                    "Disable retained clipboard resources or cached content");
            }
            model.RequestEnabled(true); await model.PendingSettings; await controller.Pending;
            Require(await model.Repository.GetAsync(first!.Value) is not null, "Re-enabling lost persisted history");
            for (var cycle = 0; cycle < 10; cycle++) { model.RequestEnabled(false); model.RequestEnabled(true); }
            await model.PendingSettings; await controller.Pending;
            model.RequestEnabled(false); await model.PendingSettings; await controller.Pending;
            Require(!controller.HasCaptureProcessor && !model.Repository.IsReady && !model.HasMaintenanceTimer,
                "Rapid clipboard switches restarted a disabled runtime");
            await model.RefreshStorageAsync();
            Require(!model.Repository.IsReady && !model.HasMaintenanceTimer && !controller.HasCaptureProcessor,
                "Reading disabled storage statistics retained an opened repository");
            Require(await model.BackupAsync(), "Disabled history backup failed");
            await controller.Pending;
            Require(!model.Repository.IsReady && !controller.HasCaptureProcessor,
                "A completed disabled management action retained its temporary repository session");
            for (var cycle = 0; cycle < 6; cycle++)
            {
                var opening = model.EnsureRepositoryAsync();
                model.RequestEnabled(true); model.RequestEnabled(false);
                await opening; await model.PendingSettings; await controller.Pending;
                Require(!model.Repository.IsReady && !controller.HasCaptureProcessor && !model.HasMaintenanceTimer,
                    "A late repository initialization revived a disabled clipboard runtime");
            }
            controller.Toggle();
            try { await WaitClipboardAsync(() => Task.FromResult(controller.Panel?.IsVisible == true)); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"Disabled manual history did not open: opening={controller.IsOpeningPanel}, pending={controller.Pending.Status}, visible={model.IsVisible}, repository={model.Repository.IsReady}, panel={controller.Panel is not null}, failure={controller.ConfigurationFailureCode}, notice={model.Notice}", error);
            }
            Require(!model.IsEnabled && !controller.IsListening && !controller.HasCaptureProcessor,
                "Manual disabled history browsing started recording");
            var panel = controller.Panel!; panel.Width = 740; panel.Height = 520; panel.UpdateLayout();
            var panelHandle = panel.Handle;
            var recordForCopy = await model.Repository.GetAsync(first!.Value);
            Require((await model.RestoreRequested!(recordForCopy!)).Success && access.Writes == 1 &&
                !controller.HasCaptureProcessor && !controller.IsListening,
                "Manual copy while recording was disabled failed or retained its temporary processing worker");
            controller.Toggle(); await controller.Pending;
            Require(controller.Panel is null && !model.Repository.IsReady && !ReviewIsWindow(panelHandle), "Closing manual disabled history retained its window or session");
            controller.Toggle();
            await WaitClipboardAsync(() => Task.FromResult(controller.Panel?.IsVisible == true));
            Require(controller.Panel!.Width == 740 && controller.Panel.Height == 520,
                "Rebuilding the history panel lost its saved window size");
            controller.Toggle(); await controller.Pending;
            Require(await controller.PrepareExitAsync(), "Fixture exit did not finish safely");
        }
        finally
        {
            controller?.Dispose();
            if (host is not null) { await host.ClipRecall.DisposeAsync(); host.Dispose(); }
            DeleteClipboardFixture(directory);
        }
        Console.WriteLine("PASS Disabled clipboard has no listener, processing pumps, STA worker, cleanup timer or cached rows; history survives re-enable");
    }

    private static async Task CheckBackgroundResourcesAsync(string report, double durationMinutes = 0)
    {
        if (!double.IsFinite(durationMinutes) || durationMinutes < 0 || durationMinutes > 1440)
            throw new ArgumentOutOfRangeException(nameof(durationMinutes));
        report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var watch = Stopwatch.StartNew();
        var checks = new List<object>(); var samples = new List<object>();
        var status = "failed";
        try
        {
            await CheckRequestRuntimeAsync();
            checks.Add(new { name = "ipc-runtime-switches", status = "passed" });
            await CheckInactiveNotesResourcesAsync(); await CheckPassiveComputerCancellationAsync();
            checks.Add(new { name = "notes-idle-and-passive-computer-cancellation", status = "passed" });
            await CheckFilePackDisabledResourcesAsync();
            checks.Add(new { name = "filepack-disable-and-idle-data-retention", status = "passed" });
            await CheckDeferredSettingsViewsAsync();
            checks.Add(new { name = "deferred-settings-and-session-retention", status = "passed" });
            await CheckNativeMessageWindowAsync();
            checks.Add(new { name = "native-message-source-and-ui-entry", status = "passed" });
            await CheckRuntimeHostLifecycleAsync();
            checks.Add(new { name = "runtime-ui-release-and-background-tool-continuity", status = "passed" });
            await CheckUiStateRetentionAsync();
            checks.Add(new { name = "ui-session-lazy-state-and-draft-retention", status = "passed" });
            await CheckDeferredViewReleaseAsync();
            checks.Add(new { name = "idle-page-release-stable-window-and-session-retention", status = "passed" });
            await CheckIdleMemoryPolicyAsync();
            checks.Add(new { name = "guarded-idle-memory-collection-and-reopen-cancellation", status = "passed" });
            await CheckClipboardExitPreparationAsync();
            checks.Add(new { name = "cancelled-exit-resumes-clipboard-runtime", status = "passed" });
            do
            {
                await CheckClipboardResourceLifecycleAsync();
                using var process = Process.GetCurrentProcess(); process.Refresh();
                samples.Add(new { seconds = watch.Elapsed.TotalSeconds, process.PrivateMemorySize64,
                    process.WorkingSet64, process.HandleCount, threads = process.Threads.Count,
                    cpuSeconds = process.TotalProcessorTime.TotalSeconds });
                if (durationMinutes == 0 || watch.Elapsed.TotalMinutes >= durationMinutes) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, Math.Min(20, durationMinutes * 60 - watch.Elapsed.TotalSeconds))));
            } while (watch.Elapsed.TotalMinutes < durationMinutes);
            checks.Add(new { name = "clipboard-disable-reenable-and-data-retention", status = "passed" });
            status = "passed";
        }
        finally
        {
            var unverified = new[] { "native-hotkey-registration-interactive-desktop", "physical-mixed-dpi", "gpu-device-loss", "sleep-resume", "live-input-and-uac",
                "physical-disk-full-and-power-loss", "network-slowdown-causality" };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, scope = "isolated-fixtures-no-live-input-no-user-data",
                durationSeconds = watch.Elapsed.TotalSeconds, requestedMinutes = durationMinutes,
                checks, samples, unverified,
                note = "Counters include preceding fixture work, including explicit collections in reachability assertions; they are not idle application memory comparisons. Deterministic teardown assertions decide pass/fail. No working-set trimming."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

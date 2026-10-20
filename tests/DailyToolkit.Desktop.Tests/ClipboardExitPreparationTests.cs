using System.IO;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckClipboardExitPreparationAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-exit-");
        MainViewModel? host = null;
        ClipboardHistoryController? controller = null;
        try
        {
            host = ResourceHost(Path.Combine(directory.FullName, "runtime"));
            var model = host.ClipRecall;
            await model.Ready;
            Require(model.SetShortcut(null, []) && await model.FlushSettingsAsync(), "Exit fixture could not disable its global shortcut");
            using var source = ClipboardTestWindow();
            controller = new(host, source, () => [], access: new FakeClipboardAccess(), cursor: () => (80, 80));
            await controller.Ready;
            model.RequestEnabled(true); await model.PendingSettings; await controller.Pending;
            Require(controller.IsListening && controller.HasCaptureProcessor, "Exit fixture did not start its isolated runtime");
            model.EditorText = "isolated exit preparation first";
            Require(await model.CreateManualAsync(), "Exit fixture could not commit its first manual record");
            model.IsVisible = true;
            await WaitClipboardAsync(() => Task.FromResult(model.HasSelection));
            Require(await controller.PrepareExitAsync() && !controller.IsListening && !controller.HasCaptureProcessor &&
                !model.HasMaintenanceTimer && model.Repository.IsReady,
                "Preparing exit retained active capture or permanently disposed the repository");

            controller.CancelExitPreparation(); await controller.Pending;
            Require(controller.IsListening && controller.HasCaptureProcessor && model.IsEnabled && !model.IsPaused,
                "Cancelling exit did not resume the prior recording state");
            model.MaximumRecords = 4321;
            await model.PendingSettings;
            Require(await model.FlushSettingsAsync() && (await model.Store.LoadAsync()).Settings.MaximumRecords == 4321,
                "Cancelling exit left settings writes stopped");
            await controller.Pending;
            model.EditorText = "isolated exit preparation second";
            Require(await model.CreateManualAsync(), "Cancelling exit left manual creation stopped");
            model.Search = "preparation second";
            await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 1 && model.HasSelection &&
                model.DetailText.Contains("preparation second", StringComparison.Ordinal)));

            model.IsPaused = true; await model.PendingSettings; await controller.Pending;
            Require(await controller.PrepareExitAsync(), "Paused runtime could not prepare exit");
            controller.CancelExitPreparation(); await controller.Pending;
            Require(model.IsPaused && !controller.IsListening && !controller.HasCaptureProcessor,
                "Cancelling exit restarted a previously paused capture pipeline");
            model.IsPaused = false; await model.PendingSettings; await controller.Pending;
            Require(controller.IsListening && controller.HasCaptureProcessor, "Resuming after a cancelled exit failed");
            model.IsVisible = false;
            model.RequestEnabled(false); await model.PendingSettings; await controller.Pending;
            Require(!controller.IsListening && !controller.HasCaptureProcessor && !model.Repository.IsReady,
                "Disabling after a cancelled exit retained background work");
            model.RequestEnabled(true); await model.PendingSettings; await controller.Pending;
            Require((await model.Repository.QueryAsync(new("isolated exit preparation"))).Total == 2,
                "A cancelled exit or subsequent disable lost committed history");
            Require(await controller.PrepareExitAsync(), "Final exit preparation failed after a cancel/resume cycle");
            controller.Dispose(); controller = null;
            await model.DisposeAsync();
            Require(!model.Repository.IsReady && !model.HasMaintenanceTimer,
                "Final asynchronous disposal did not release the prepared repository");

            var blockedStore = new ClipboardSettingsStore(Path.Combine(directory.FullName, "failed-prepare"));
            await using var failed = new ClipboardHistoryViewModel(blockedStore);
            await failed.Ready;
            Directory.CreateDirectory(blockedStore.SettingsPath);
            failed.MaximumRecords = 4321;
            await failed.PendingSettings;
            Require(!await failed.FlushSettingsAsync() && !await failed.PrepareExitAsync(),
                "Blocked settings writes did not veto exit preparation");
            // This empty fixture directory is the deliberate write obstruction.
            Directory.Delete(blockedStore.SettingsPath);
            failed.MaximumRecords = 4322;
            await failed.PendingSettings;
            Require(await failed.FlushSettingsAsync() && (await blockedStore.LoadAsync()).Settings.MaximumRecords == 4322,
                "Failed preparation did not automatically restore settings writes");
            failed.EditorText = "isolated write after failed exit";
            Require(await failed.CreateManualAsync(), "Failed preparation left manual actions stopped");
            failed.IsVisible = true;
            await WaitClipboardAsync(() => Task.FromResult(failed.HasSelection));
            Require(await failed.PrepareExitAsync(), "A repaired settings destination still blocked exit");
            await failed.DisposeAsync();
            Require(!failed.Repository.IsReady, "Repaired fixture retained its repository after final disposal");
        }
        finally
        {
            controller?.Dispose();
            if (host is not null) { await host.ClipRecall.DisposeAsync(); host.Dispose(); }
            DeleteClipboardFixture(directory);
        }
        Console.WriteLine("PASS Clipboard exit preparation can cancel/resume capture, pause, writes and refresh; failures recover before final disposal");
    }
}

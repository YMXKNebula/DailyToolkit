using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.FilePack;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackDisabledResourcesAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-disable-");
        try
        {
            var source = Path.Combine(fixture.FullName, "source.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
            var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var entries = (await new FileCollectionService().CollectAsync([source])).Entries;
            var store = new FilePackStore(Path.Combine(fixture.FullName, "module"));
            var engine = new SevenZipAdapter(Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe"));
            await using var manager = new FilePackManager(store, engine);
            await manager.InitializeAsync(); await manager.SavePreferencesAsync(new(1));
            var external = EngineSnapshot(entries, fixture.FullName, "external") with
            { Options = new(PackMode.External, PackFormat.Zip, PackCompression.Standard, fixture.FullName, "external", AllowStaging: true) };
            await manager.EnqueueAsync(external);
            var externalReady = await WaitPackStateAsync(manager, external.TaskId, task => task.State == PackState.AwaitingExternal);
            await WaitPackInactiveAsync(manager, external.TaskId);
            var externalMarker = Path.Combine(externalReady.WorkspacePath, "finished-by-user.txt");
            await File.WriteAllTextAsync(externalMarker, "preserve external work");
            var previous = Path.Combine(fixture.FullName, "previous.7z");
            var previousBytes = new byte[] { 1, 3, 5, 7 }; await File.WriteAllBytesAsync(previous, previousBytes);
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProcessStarted += id =>
            {
                if (started.TrySetResult(id)) release.Wait(TimeSpan.FromSeconds(20));
            };
            var active = EngineSnapshot(entries, fixture.FullName, "previous", PackFormat.SevenZip, PackCompression.Maximum) with
            { Options = new(PackMode.BuiltIn, PackFormat.SevenZip, PackCompression.Maximum, fixture.FullName, "previous",
                PackOutputPolicy.Overwrite, ApprovedOverwriteStamp: FilePackPaths.CaptureSourceStamp(previous)) };
            await manager.EnqueueAsync(active);
            var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = await manager.EnqueueAsync(EngineSnapshot(entries, fixture.FullName, "queued"));
            Require(manager.TryGetTask(queued.TaskId, out var queuedInfo) && queuedInfo?.State == PackState.Queued,
                "Disable fixture did not have a queued job");
            var denied = false;
            using (File.OpenHandle(Path.Combine(store.RootDirectory, "Tasks", queued.TaskId.ToString("N") + ".json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var suspension = manager.SuspendAsync(); release.Set();
                try { await suspension.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { denied = true; }
                Require(denied && manager.IsSuspended && manager.Tasks.Count == 3,
                    "A denied terminal save dropped stopped task snapshots instead of retaining them for retry");
            }
            await manager.SuspendAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(manager.IsSuspended && manager.Tasks.Count == 0, "Stopped FilePack retained task snapshots");
            try
            {
                using var native = Process.GetProcessById(pid);
                Require(native.HasExited, "Stopped FilePack retained its owned compression process");
            }
            catch (ArgumentException) { /* The owned process no longer exists. */ }
            var persisted = await store.LoadTasksAsync();
            Require(persisted.Single(task => task.TaskId == active.TaskId).State == PackState.Cancelled &&
                persisted.Single(task => task.TaskId == queued.TaskId).State == PackState.Cancelled &&
                persisted.Single(task => task.TaskId == external.TaskId).State == PackState.AwaitingExternal &&
                File.Exists(externalMarker), "Disable lost cancelled records or modified a prepared external workspace");
            Require((await File.ReadAllBytesAsync(previous)).SequenceEqual(previousBytes) &&
                SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(sourceHash),
                "Disable modified an existing archive or an original source");
            manager.Resume(); await manager.InitializeAsync();
            var retry = await manager.RetryAsync(queued.TaskId);
            Require((await WaitPackTerminalAsync(manager, retry.TaskId)).State == PackState.Completed,
                "A cancelled record could not restart after re-enabling");
            await manager.CompleteExternalAsync(external.TaskId);
            await manager.SuspendAsync();
            Console.WriteLine("PASS FilePack disable cancels owned native and queued jobs, retains failed state saves for retry, releases snapshots, preserves original/old output/external work and supports restart");
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
        await CheckFilePackDraftRetentionAsync();
        await CheckFilePackEnabledIdleAsync();
    }

    private static async Task CheckFilePackDraftRetentionAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-draft-");
        try
        {
            var source = Path.Combine(fixture.FullName, "source.txt"); await File.WriteAllTextAsync(source, "keep original");
            await using var model = new FilePackViewModel(new FilePackStore(Path.Combine(fixture.FullName, "module")), "missing-engine.exe", _ => { });
            await model.InitializeAsync(); await model.AddPathsAsync([source]);
            model.Entries.Single().PathDraft = "正在编辑/"; model.TaskName = "未提交的任务";
            await model.FlushAsync();
            var draftPath = Path.Combine(model.Store.RootDirectory, "draft.json");
            using (File.OpenHandle(draftPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(!await model.SetEnabledAsync(false) && !model.IsEnabled && model.Entries.Count == 1 && model.HasNotice,
                    "A denied draft save dropped unsaved entries or reported successful release");
            }
            Require(await model.SetEnabledAsync(false) && model.Entries.Count == 0 && model.Tasks.Count == 0 && !model.IsLoaded,
                "Retrying disable did not save and release the draft");
            Require(await model.SetEnabledAsync(true) && !model.IsLoaded, "Re-enabling eagerly recovered file history");
            await model.InitializeAsync();
            Require(model.Entries.Single().PathDraft == "正在编辑/" && model.TaskName == "未提交的任务",
                "Re-enabling lost the list or an uncommitted path edit");
            await Task.WhenAll(model.SetEnabledAsync(false), model.SetEnabledAsync(true), model.SetEnabledAsync(false));
            Require(!model.IsEnabled && !model.IsLoaded && model.Entries.Count == 0 && model.Manager.Tasks.Count == 0,
                "Rapid switch requests raced resource release");
            Require(await model.SetEnabledAsync(true), "Rapid switch recovery could not be enabled");
            await model.InitializeAsync();
            Require(model.Entries.Count == 1 && await File.ReadAllTextAsync(source) == "keep original", "Rapid switches lost the draft or changed source data");
            Console.WriteLine("PASS FilePack draft atomic persistence, denied-save memory retention/retry, lazy recovery, unfinished edit preservation and rapid switch serialization");
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
    }

    private static async Task CheckFilePackEnabledIdleAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-idle-");
        using var release = new ManualResetEventSlim();
        try
        {
            var source = Path.Combine(fixture.FullName, "source.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
            var engine = new SevenZipAdapter(Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe"));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProcessStarted += _ => { if (started.TrySetResult()) release.Wait(TimeSpan.FromSeconds(20)); };
            await using var model = new FilePackViewModel(new FilePackStore(Path.Combine(fixture.FullName, "module")),
                openDirectory: _ => { }, engine: engine);
            model.SetToolWindowsVisible(true);
            await model.InitializeAsync();
            model.MaximumParallel = 1; await model.PendingOperations;
            await model.AddPathsAsync([source]);
            var entries = model.Entries.Select(row => row.Entry).ToArray();
            var active = await model.Manager.EnqueueAsync(EngineSnapshot(entries, fixture.FullName, "active"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = await model.Manager.EnqueueAsync(EngineSnapshot(entries, fixture.FullName, "queued"));
            model.SetToolWindowsVisible(false);
            await model.IdleWork.WaitAsync(TimeSpan.FromSeconds(5));
            Require(model.IsEnabled && !model.IsLoaded && model.Entries.Count == 0 && model.Tasks.Count == 0 &&
                model.HasUnfinishedTasks && model.RunningCount == 1 && model.WaitingCount == 1 &&
                model.Manager.TryGetTask(active.TaskId, out var activeInfo) && activeInfo?.State == PackState.Compressing &&
                model.Manager.TryGetTask(queued.TaskId, out var queuedInfo) && queuedInfo?.State == PackState.Queued,
                "Enabled idle release retained UI caches or stopped normal queued/running work");
            release.Set();
            await WaitForFilePackAsync(() => !model.Manager.IsInitialized && model.Manager.Tasks.Count == 0,
                "Completed background work did not release idle task snapshots");
            var saved = await model.Store.LoadTasksAsync();
            Require(saved.Count == 2 && saved.All(task => task.State == PackState.Completed && File.Exists(task.OutputPath)),
                "Enabled idle compression did not finish both jobs");
            model.SetToolWindowsVisible(true); await model.InitializeAsync();
            Require(model.Entries.Single().Entry.SourcePath == source && model.Tasks.Count == 2,
                "First use after idle did not restore the draft and completed records");
            model.SetToolWindowsVisible(false); await model.IdleWork.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!model.IsLoaded && !model.Manager.IsInitialized && model.Entries.Count == 0,
                "Idle re-use left task or draft snapshots resident");
            Console.WriteLine("PASS FilePack enabled idle releases draft/UI snapshots, continues queued/native work, unloads after completion and restores on demand without polling");
        }
        finally
        {
            release.Set();
            FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true);
        }
    }
}

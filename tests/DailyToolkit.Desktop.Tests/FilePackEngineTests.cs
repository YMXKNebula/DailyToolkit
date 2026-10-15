using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using DailyToolkit.Core.FilePack;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackEngineAsync(string? executablePath = null)
    {
        var enginePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe");
        Require(File.Exists(enginePath), "The bundled official 7-Zip engine is missing");
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-filepack-engine-");
        DirectoryInfo? crossVolumeDirectory = null;
        try
        {
            var sources = Path.Combine(directory.FullName, "sources");
            var firstDirectory = Path.Combine(sources, "甲 来源"); var secondDirectory = Path.Combine(sources, "乙 source");
            Directory.CreateDirectory(firstDirectory); Directory.CreateDirectory(secondDirectory);
            var first = Path.Combine(firstDirectory, "中文 空格.txt"); var second = Path.Combine(secondDirectory, "-leading.txt");
            var crossRoot = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_FILEPACK_CROSS_VOLUME_ROOT");
            if (!string.IsNullOrWhiteSpace(crossRoot))
            {
                if (!Directory.Exists(crossRoot) || string.Equals(Path.GetPathRoot(Path.GetFullPath(crossRoot)), Path.GetPathRoot(directory.FullName), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cross-volume fixture root must be an existing directory on a different volume");
                crossVolumeDirectory = Directory.CreateDirectory(Path.Combine(crossRoot, "DailyToolkit-filepack-cross-" + Guid.NewGuid().ToString("N")));
                second = Path.Combine(crossVolumeDirectory.FullName, "-leading.txt");
                Console.WriteLine("PASS FilePack direct source fixture roots are on different volumes");
            }
            else Console.WriteLine("SKIP FilePack physical cross-volume source check: DAILYTOOLKIT_FILEPACK_CROSS_VOLUME_ROOT is not set; multi-directory direct reads are checked");
            await File.WriteAllTextAsync(first, "Unicode direct source: 临时样本"); await File.WriteAllTextAsync(second, "second direct source");
            var firstHash = SHA256.HashData(await File.ReadAllBytesAsync(first)); var secondHash = SHA256.HashData(await File.ReadAllBytesAsync(second));
            var entries = (await new FileCollectionService().CollectAsync([first, second])).Entries;
            var outputs = Path.Combine(directory.FullName, "custom output"); Directory.CreateDirectory(outputs);
            var engine = new SevenZipAdapter(enginePath);
            var store = new FilePackStore(Path.Combine(directory.FullName, "module"));
            await using (var manager = new FilePackManager(store, engine))
            {
                await manager.InitializeAsync();
                foreach (var format in new[] { PackFormat.Zip, PackFormat.SevenZip, PackFormat.Tar })
                foreach (var compression in format == PackFormat.Tar ? new[] { PackCompression.Store } : Enum.GetValues<PackCompression>())
                {
                    var snapshot = EngineSnapshot(entries, outputs, format + "-" + compression, format, compression);
                    Require(!new PackPlanner().Create(snapshot).RequiresStaging, "Separate source roots forced staging");
                    var queued = await manager.EnqueueAsync(snapshot);
                    var completed = await WaitPackTerminalAsync(manager, queued.TaskId);
                    Require(completed.State == PackState.Completed && File.Exists(completed.OutputPath), "Real archive failed: " + completed.Error);
                    Require(!Directory.Exists(Path.Combine(store.RootDirectory, "Sessions", snapshot.SessionId.ToString("N"))), "Direct archive copied source data");
                    if (format == PackFormat.Zip)
                    {
                        using var archive = ZipFile.OpenRead(completed.OutputPath);
                        Require(archive.Entries.Count == 2 && archive.GetEntry("中文 空格.txt") is not null && archive.GetEntry("-leading.txt") is not null,
                            "Direct archive exposed source roots or lost Unicode/leading-dash names: " + string.Join(", ", archive.Entries.Select(entry => entry.FullName)));
                        await using var content = archive.GetEntry("中文 空格.txt")!.Open();
                        Require(SHA256.HashData(content).SequenceEqual(firstHash), "ZIP content differs from original source");
                    }
                }
                var mappedEntries = entries.Select((entry, index) => entry with { ArchivePath = "整理/" + (index == 0 ? "改名.txt" : "第二.txt") }).ToArray();
                var emptySource = Directory.CreateDirectory(Path.Combine(firstDirectory, "空目录"));
                var entriesWithEmpty = (await new FileCollectionService().CollectAsync([emptySource.FullName], entries)).Entries;
                foreach (var format in Enum.GetValues<PackFormat>())
                {
                    var emptySnapshot = EngineSnapshot(entriesWithEmpty, outputs, "direct-empty-" + format, format);
                    Require(!new PackPlanner().Create(emptySnapshot).RequiresStaging, "A real empty source directory forced full staging");
                    var completed = await manager.EnqueueAsync(emptySnapshot); completed = await WaitPackTerminalAsync(manager, completed.TaskId);
                    Require(completed.State == PackState.Completed, "Direct empty source directory failed: " + completed.Error);
                    if (format == PackFormat.Zip)
                    {
                        using var archive = ZipFile.OpenRead(completed.OutputPath);
                        Require(archive.GetEntry("空目录/") is not null, "Direct ZIP lost the empty source folder");
                    }
                }
                var emptyDirectory = Path.Combine(firstDirectory, "空目录"); Directory.CreateDirectory(emptyDirectory);
                var directoryEntries = (await new FileCollectionService().CollectAsync([first, emptyDirectory])).Entries
                    .Append(new PackEntry(Guid.NewGuid(), "", "虚拟/空目录", true, 0, null, "")).ToArray();
                foreach (var format in Enum.GetValues<PackFormat>())
                {
                    var folders = EngineSnapshot(directoryEntries, outputs, "folders-" + format, format) with
                    { Options = new(PackMode.BuiltIn, format, PackCompression.Standard, outputs, "folders-" + format, AllowStaging: true) };
                    var completed = await manager.EnqueueAsync(folders); completed = await WaitPackTerminalAsync(manager, completed.TaskId);
                    Require(completed.State == PackState.Completed, "Empty/virtual directories failed in " + format + ": " + completed.Error);
                    if (format == PackFormat.Zip)
                    {
                        using var archive = ZipFile.OpenRead(completed.OutputPath);
                        Require(archive.GetEntry("空目录/") is not null && archive.GetEntry("虚拟/空目录/") is not null,
                            "Empty or virtual archive directories were lost");
                    }
                }
                var mapped = EngineSnapshot(mappedEntries, outputs, "mapped", PackFormat.Zip, PackCompression.Standard) with
                { Options = new(PackMode.BuiltIn, PackFormat.Zip, PackCompression.Standard, outputs, "mapped", AllowStaging: true) };
                var mappedInfo = await manager.EnqueueAsync(mapped);
                mappedInfo = await WaitPackTerminalAsync(manager, mappedInfo.TaskId);
                Require(mappedInfo.State == PackState.Completed && mappedInfo.WorkspacePath.Length == 0, "Mapped archive failed: " + mappedInfo.Error);
                using (var archive = ZipFile.OpenRead(mappedInfo.OutputPath))
                    Require(archive.GetEntry("整理/改名.txt") is not null && archive.GetEntry("整理/第二.txt") is not null,
                        "Staging did not preserve the edited internal layout");
                var external = mapped with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Options = mapped.Options with { Mode = PackMode.External } };
                var externalInfo = await manager.EnqueueAsync(external);
                externalInfo = await WaitPackStateAsync(manager, externalInfo.TaskId, task => task.State == PackState.AwaitingExternal);
                Require(File.Exists(Path.Combine(externalInfo.WorkspacePath, "整理", "改名.txt")), "External workspace was not prepared");
                await WaitPackInactiveAsync(manager, externalInfo.TaskId);
                var secondExternal = external with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid() };
                await manager.EnqueueAsync(secondExternal);
                await WaitPackStateAsync(manager, secondExternal.TaskId, task => task.State == PackState.AwaitingExternal);
                var whileWaiting = await manager.EnqueueAsync(EngineSnapshot(entries, outputs, "while-external-waits"));
                Require((await WaitPackTerminalAsync(manager, whileWaiting.TaskId)).State == PackState.Completed &&
                    manager.Tasks.Single(task => task.TaskId == external.TaskId).State == PackState.AwaitingExternal,
                    "Waiting external workspaces consumed a preparation or compression slot");
                await manager.CompleteExternalAsync(secondExternal.TaskId);
                await manager.CompleteExternalAsync(externalInfo.TaskId, keepWorkspace: true);
                Require(Directory.Exists(manager.Tasks.Single(task => task.TaskId == external.TaskId).WorkspacePath), "Explicit workspace retention was ignored");
                await manager.CleanupAsync(external.TaskId);
                Require(manager.Tasks.Single(task => task.TaskId == external.TaskId).WorkspacePath.Length == 0, "Manual cleanup retained the workspace path");
                await manager.RemoveAsync(external.TaskId);
                Require(manager.Tasks.All(task => task.TaskId != external.TaskId), "Completed history could not be removed");
            }
            Require(SHA256.HashData(await File.ReadAllBytesAsync(first)).SequenceEqual(firstHash) &&
                SHA256.HashData(await File.ReadAllBytesAsync(second)).SequenceEqual(secondHash), "Packing changed original files");
            Console.WriteLine("PASS FilePack real ZIP/7z/TAR, all levels, Unicode direct multi-root reads, edited layouts, original hashes and explicit external keep/cleanup");

            var large = Path.Combine(firstDirectory, "shared-random.bin");
            var random = new byte[16 * 1024 * 1024]; RandomNumberGenerator.Fill(random); await File.WriteAllBytesAsync(large, random);
            var largeEntries = (await new FileCollectionService().CollectAsync([large])).Entries;
            foreach (var limit in new[] { 2, 4 })
            {
                var schedulerEngine = new SevenZipAdapter(enginePath); var sync = new object(); var live = new HashSet<int>(); var peak = 0;
                schedulerEngine.ProcessStarted += id => { lock (sync) { live.Add(id); peak = Math.Max(peak, live.Count); } };
                schedulerEngine.ProcessExited += id => { lock (sync) live.Remove(id); };
                var resources = new PackResourceProbe(() => { lock (sync) return live.ToArray(); });
                using var resourceCancellation = new CancellationTokenSource();
                var resourceMonitor = resources.MonitorAsync(resourceCancellation.Token);
                await using var manager = new FilePackManager(new(Path.Combine(directory.FullName, "parallel-" + limit)), schedulerEngine);
                await manager.InitializeAsync(); await manager.SavePreferencesAsync(new(limit));
                var tasks = new List<PackTaskInfo>();
                for (var index = 0; index < limit + 1; index++)
                    tasks.Add(await manager.EnqueueAsync(EngineSnapshot(largeEntries, outputs, "parallel-" + limit + "-" + index, PackFormat.SevenZip, PackCompression.Maximum)));
                Require(manager.Tasks.Count(task => task.State == PackState.Queued) >= 1, "Tasks beyond the concurrent limit did not queue");
                manager.MoveQueued(tasks[^1].TaskId, -1);
                await WaitPackStateAsync(manager, tasks[0].TaskId, task => task.State == PackState.Compressing);
                await Task.Delay(100); await manager.CancelAsync(tasks[0].TaskId);
                Require(manager.Tasks.Single(task => task.TaskId == tasks[0].TaskId).State == PackState.Cancelled &&
                    !File.Exists(tasks[0].OutputPath), "Cancellation produced a successful output");
                foreach (var task in tasks.Skip(1))
                    Require((await WaitPackTerminalAsync(manager, task.TaskId)).State == PackState.Completed, "Cancelling one job affected another");
                Require(peak == limit, $"Expected {limit} simultaneous owned native processes, saw {peak}");
                Require(live.Count == 0, "Owned 7-Zip processes remained after terminal states");
                resourceCancellation.Cancel(); await resourceMonitor;
                Console.WriteLine($"PASS FilePack {limit} native processes: peak working set {resources.PeakWorkingSet / 1048576d:F1} MiB, " +
                    $"sampled CPU {resources.CpuSeconds:F2}s, process I/O read {resources.ReadBytes / 1048576d:F1} MiB / write {resources.WriteBytes / 1048576d:F1} MiB");
            }
            Console.WriteLine("PASS FilePack real 2/4-process concurrency, queued overflow, independent cancellation and shared-source reads");

            await CheckPackOutputProtectionAsync(directory.FullName, outputs, first, entries, largeEntries, enginePath);
            await CheckPackRecoveryAsync(directory.FullName, outputs, entries, enginePath);
            await CheckPackQueueOrderAsync(directory.FullName, outputs, entries, enginePath);
            await CheckPackReadOnlyHistoryAsync(directory.FullName, outputs, entries, enginePath);
            await CheckPackMetadataLimitsAsync(directory.FullName, outputs, entries);
        }
        finally
        {
            if (!FilePackPaths.IsWithin(directory.FullName, Path.GetTempPath())) throw new IOException("Engine fixture escaped its temporary root");
            FilePackPaths.EnsureNoReparsePoints(directory.FullName);
            Directory.Delete(directory.FullName, recursive: true);
            if (crossVolumeDirectory is not null)
            {
                var crossRoot = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_FILEPACK_CROSS_VOLUME_ROOT")!;
                if (!FilePackPaths.IsWithin(crossVolumeDirectory.FullName, crossRoot) ||
                    !crossVolumeDirectory.Name.StartsWith("DailyToolkit-filepack-cross-", StringComparison.Ordinal))
                    throw new IOException("Cross-volume fixture escaped its explicitly supplied root");
                FilePackPaths.EnsureNoReparsePoints(crossVolumeDirectory.FullName);
                Directory.Delete(crossVolumeDirectory.FullName, recursive: true);
            }
        }
    }

    private static PackSnapshot EngineSnapshot(IReadOnlyList<PackEntry> entries, string output, string name, PackFormat format = PackFormat.Zip,
        PackCompression compression = PackCompression.Standard) => new(Guid.NewGuid(), Guid.NewGuid(), name, entries,
            new(PackMode.BuiltIn, format, compression, output, name), DateTimeOffset.UtcNow);

    private static async Task CheckPackQueueOrderAsync(string root, string outputs, IReadOnlyList<PackEntry> entries, string enginePath)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new SevenZipAdapter(enginePath); var firstNative = 0;
        engine.ProcessStarted += _ =>
        {
            if (Interlocked.Exchange(ref firstNative, 1) != 0) return;
            started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(30));
        };
        await using var manager = new FilePackManager(new(Path.Combine(root, "queue-order")), engine);
        await manager.InitializeAsync(); manager.MaximumParallel = 1;
        var order = new List<Guid>(); var sync = new object();
        manager.TaskChanged += task => { if (task.State == PackState.Collecting) lock (sync) order.Add(task.TaskId); };
        try
        {
            var active = await manager.EnqueueAsync(EngineSnapshot(entries, outputs, "queue-active"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var first = await manager.EnqueueAsync(EngineSnapshot(entries, outputs, "queue-first"));
            var second = await manager.EnqueueAsync(EngineSnapshot(entries, outputs, "queue-second"));
            var cancelled = await manager.EnqueueAsync(EngineSnapshot(entries, outputs, "queue-cancelled"));
            manager.MoveQueued(second.TaskId, -1);
            Require(manager.Tasks.Single(task => task.TaskId == second.TaskId).QueueOrder <
                manager.Tasks.Single(task => task.TaskId == first.TaskId).QueueOrder, "Moving a queued task did not change priority");
            await manager.CancelAsync(cancelled.TaskId);
            Require(manager.Tasks.Single(task => task.TaskId == cancelled.TaskId).State == PackState.Cancelled &&
                !File.Exists(cancelled.OutputPath), "Cancelling a queued task started compression");
            release.Set();
            foreach (var task in new[] { active, second, first })
                Require((await WaitPackTerminalAsync(manager, task.TaskId)).State == PackState.Completed, "Reordered task did not complete");
            lock (sync) Require(order.SequenceEqual([active.TaskId, second.TaskId, first.TaskId]), "Scheduler ignored the changed queue order");
            Console.WriteLine("PASS FilePack changed queue priority controls real launch order; queued cancellation never starts a native process");
        }
        finally { release.Set(); }
    }

    private static async Task CheckPackReadOnlyHistoryAsync(string root, string outputs, IReadOnlyList<PackEntry> entries, string enginePath)
    {
        var store = new FilePackStore(Path.Combine(root, "readonly-history"));
        var snapshot = EngineSnapshot(entries, outputs, "readonly");
        var completed = new PackTaskInfo(snapshot, PackState.Completed, Percent: 100, FinishedAt: DateTimeOffset.UtcNow);
        await store.SaveTaskAsync(completed);
        var path = Path.Combine(store.RootDirectory, "Tasks", snapshot.TaskId.ToString("N") + ".json");
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var stamp = File.GetLastWriteTimeUtc(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await using var manager = new FilePackManager(store, new SevenZipAdapter(enginePath));
            await manager.InitializeAsync();
            Require(manager.Tasks.Single().State == PackState.Completed && File.GetLastWriteTimeUtc(path) == stamp &&
                SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(hash),
                "Opening completed history needlessly rewrote immutable records");
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Console.WriteLine("PASS FilePack unchanged completed history opens read-only without rewriting records");
    }

    private static async Task CheckPackMetadataLimitsAsync(string root, string outputs, IReadOnlyList<PackEntry> entries)
    {
        var store = new FilePackStore(Path.Combine(root, "metadata-limits"));
        await store.SavePreferencesAsync(new());
        var rejected = false;
        try { await store.SavePreferencesAsync(new(LastOutputDirectory: new string('x', 17000))); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && (await store.LoadPreferencesAsync()).LastOutputDirectory == "", "Unreloadable preferences replaced the previous file");
        var snapshot = EngineSnapshot(entries, outputs, "metadata");
        await store.SaveTaskAsync(new(snapshot, PackState.Queued));
        var path = Path.Combine(store.RootDirectory, "Tasks", snapshot.TaskId.ToString("N") + ".json");
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var prefix = new string('a', 200) + "/" + new string('b', 200) + "/";
        var many = Enumerable.Range(0, FilePackPaths.MaximumEntries).Select(index => entries[0] with
        { Id = Guid.NewGuid(), ArchivePath = prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt" }).ToArray();
        rejected = false;
        try { await store.SaveTaskAsync(new(snapshot with { Entries = many }, PackState.Queued)); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(hash) &&
            !Directory.EnumerateFiles(store.RootDirectory, "*.tmp", SearchOption.AllDirectories).Any(),
            "Oversized metadata replaced the previous task or left a large unreferenced temporary file");
        Console.WriteLine("PASS FilePack bounded metadata prevents unreloadable records, preserves previous files and cleans partial JSON writes");
    }

    private static async Task CheckPackOutputProtectionAsync(string root, string outputs, string original,
        IReadOnlyList<PackEntry> entries, IReadOnlyList<PackEntry> largeEntries, string enginePath)
    {
        await using var manager = new FilePackManager(new(Path.Combine(root, "protection")), new(enginePath));
        await manager.InitializeAsync(); manager.MaximumParallel = 1;
        var first = await manager.EnqueueAsync(EngineSnapshot(largeEntries, outputs, "occupied", PackFormat.SevenZip, PackCompression.Maximum));
        var duplicate = EngineSnapshot(entries, outputs, "occupied", PackFormat.SevenZip);
        var rejected = false;
        try { await manager.EnqueueAsync(duplicate); } catch (IOException) { rejected = true; }
        Require(rejected, "Two tasks reserved the same final output path");
        var renamed = await manager.EnqueueAsync(duplicate with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Options = duplicate.Options with { OutputPolicy = PackOutputPolicy.AutoRename } });
        Require(renamed.OutputPath != first.OutputPath, "Auto-rename reused a reserved output path");
        await manager.CancelAsync(first.TaskId); await WaitPackTerminalAsync(manager, renamed.TaskId);

        var old = Path.Combine(outputs, "approved.zip"); var oldBytes = new byte[] { 1, 3, 5, 7 }; await File.WriteAllBytesAsync(old, oldBytes);
        var stamp = FilePackPaths.CaptureSourceStamp(old);
        await using (var missing = new FilePackManager(new(Path.Combine(root, "missing-engine")), new(Path.Combine(root, "missing.exe"))))
        {
            var approved = EngineSnapshot(entries, outputs, "approved") with
            { Options = new(PackMode.BuiltIn, PackFormat.Zip, PackCompression.Standard, outputs, "approved", PackOutputPolicy.Overwrite, ApprovedOverwriteStamp: stamp) };
            var failed = await missing.EnqueueAsync(approved); failed = await WaitPackTerminalAsync(missing, failed.TaskId);
            Require(failed.State == PackState.Failed && (await File.ReadAllBytesAsync(old)).SequenceEqual(oldBytes), "Failed packing deleted the previous archive");
        }
        var sourceArchive = Path.Combine(Path.GetDirectoryName(original)!, "source.zip"); await File.WriteAllBytesAsync(sourceArchive, oldBytes);
        var sourceEntries = (await new FileCollectionService().CollectAsync([sourceArchive])).Entries;
        var sourceTarget = EngineSnapshot(sourceEntries, Path.GetDirectoryName(sourceArchive)!, "source") with
        { Options = new(PackMode.BuiltIn, PackFormat.Zip, PackCompression.Standard, Path.GetDirectoryName(sourceArchive)!, "source", PackOutputPolicy.Overwrite,
            ApprovedOverwriteStamp: FilePackPaths.CaptureSourceStamp(sourceArchive)) };
        rejected = false; try { await manager.EnqueueAsync(sourceTarget); } catch (IOException) { rejected = true; }
        Require(rejected && (await File.ReadAllBytesAsync(sourceArchive)).SequenceEqual(oldBytes), "Output could replace an original source");

        var changing = Path.Combine(Path.GetDirectoryName(original)!, "changing.bin"); await File.WriteAllBytesAsync(changing, new byte[8 * 1024 * 1024]);
        var changingEntries = (await new FileCollectionService().CollectAsync([changing])).Entries;
        var changingTask = EngineSnapshot(changingEntries, outputs, "changing", PackFormat.SevenZip, PackCompression.Maximum);
        var changingEngine = new SevenZipAdapter(enginePath); var changed = 0;
        changingEngine.ProcessStarted += _ => { if (Interlocked.Exchange(ref changed, 1) == 0) File.SetLastWriteTimeUtc(changing, DateTime.UtcNow.AddMinutes(1)); };
        await using (var changingManager = new FilePackManager(new(Path.Combine(root, "changing-module")), changingEngine))
        {
            var failed = await changingManager.EnqueueAsync(changingTask); failed = await WaitPackTerminalAsync(changingManager, failed.TaskId);
            Require(failed.State == PackState.Failed && !File.Exists(failed.OutputPath), "Source changes were reported as verified success");
            var independent = await changingManager.EnqueueAsync(EngineSnapshot(entries, outputs, "after-failure"));
            Require((await WaitPackTerminalAsync(changingManager, independent.TaskId)).State == PackState.Completed, "A failed task prevented unrelated work");
        }
        var atomicEngine = new SevenZipAdapter(enginePath); var preservedDuringWrites = true;
        atomicEngine.ProcessStarted += _ => preservedDuringWrites &= File.Exists(old) && File.ReadAllBytes(old).SequenceEqual(oldBytes);
        await using (var atomic = new FilePackManager(new(Path.Combine(root, "atomic-success")), atomicEngine))
        {
            var approved = EngineSnapshot(entries, outputs, "approved") with { Options = new(PackMode.BuiltIn, PackFormat.Zip,
                PackCompression.Standard, outputs, "approved", PackOutputPolicy.Overwrite, ApprovedOverwriteStamp: FilePackPaths.CaptureSourceStamp(old)) };
            var completed = await atomic.EnqueueAsync(approved); completed = await WaitPackTerminalAsync(atomic, completed.TaskId);
            Require(completed.State == PackState.Completed && preservedDuringWrites && !File.ReadAllBytes(old).SequenceEqual(oldBytes),
                "Successful atomic overwrite removed the original before validation or failed to commit");
        }
        var changedOutputBytes = new byte[] { 2, 4, 6, 8, 10, 12 }; var mutateOnce = 0;
        var changedTargetEngine = new SevenZipAdapter(enginePath);
        changedTargetEngine.ProcessStarted += _ => { if (Interlocked.Exchange(ref mutateOnce, 1) == 0) File.WriteAllBytes(old, changedOutputBytes); };
        await using (var changedTarget = new FilePackManager(new(Path.Combine(root, "atomic-conflict")), changedTargetEngine))
        {
            var approved = EngineSnapshot(entries, outputs, "approved") with { Options = new(PackMode.BuiltIn, PackFormat.Zip,
                PackCompression.Standard, outputs, "approved", PackOutputPolicy.Overwrite, ApprovedOverwriteStamp: FilePackPaths.CaptureSourceStamp(old)) };
            var failed = await changedTarget.EnqueueAsync(approved); failed = await WaitPackTerminalAsync(changedTarget, failed.TaskId);
            Require(failed.State == PackState.Failed && File.ReadAllBytes(old).SequenceEqual(changedOutputBytes), "An output changed during compression was overwritten");
        }
        Console.WriteLine("PASS FilePack output reservations, automatic names, old-archive and source protection, native failure isolation and changed-source rejection");
    }

    private static async Task CheckPackRecoveryAsync(string root, string outputs, IReadOnlyList<PackEntry> entries, string enginePath)
    {
        var store = new FilePackStore(Path.Combine(root, "recovery"));
        var interrupted = EngineSnapshot(entries, outputs, "interrupted");
        await store.SaveTaskAsync(new(interrupted, PackState.Compressing));
        var external = EngineSnapshot(entries, outputs, "external") with { Options = new(PackMode.External, PackFormat.Zip, PackCompression.Standard, outputs, "external") };
        var workspace = new FilePackWorkspaceManager(store.RootDirectory); var staging = await workspace.PrepareAsync(external);
        await store.SaveTaskAsync(new(external, PackState.AwaitingExternal, WorkspacePath: staging));
        await using (var recovered = new FilePackManager(store, new(enginePath), workspace))
        {
            await recovered.InitializeAsync();
            Require(recovered.Tasks.Single(task => task.TaskId == interrupted.TaskId).State == PackState.Interrupted,
                "Restored native work was automatically resumed");
            Require(recovered.Tasks.Single(task => task.TaskId == external.TaskId).State == PackState.AwaitingExternal && Directory.Exists(staging),
                "Owned external workspace was not restored");
            await recovered.CompleteExternalAsync(external.TaskId);
            var retried = await recovered.RetryAsync(interrupted.TaskId);
            Require(retried.Snapshot.SessionId != interrupted.SessionId && (await WaitPackTerminalAsync(recovered, retried.TaskId)).State == PackState.Completed,
                "Interrupted retry reused old partial data or failed to restart");
        }
        var partial = external with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid() };
        var partialStaging = workspace.GetWorkspacePath(partial); var partialSession = Path.GetDirectoryName(partialStaging)!;
        Directory.CreateDirectory(partialSession);
        await File.WriteAllTextAsync(Path.Combine(partialSession, ".filepack-owner.json"), System.Text.Json.JsonSerializer.Serialize(new
        { Version = 1, partial.TaskId, partial.SessionId }));
        await store.SaveTaskAsync(new(partial, PackState.Copying, WorkspacePath: partialStaging));
        await using (var recovered = new FilePackManager(store, new(enginePath), workspace))
        {
            await recovered.InitializeAsync();
            Require(recovered.Tasks.Single(task => task.TaskId == partial.TaskId).State == PackState.Interrupted, "Partial copy was exposed as externally ready");
            await recovered.CleanupAsync(partial.TaskId);
            Require(!Directory.Exists(partialSession), "Owned session marker survived cleanup when staging did not yet exist");
        }
        var corruptId = Guid.NewGuid(); var corruptPath = Path.Combine(store.RootDirectory, "Tasks", corruptId.ToString("N") + ".json");
        await File.WriteAllTextAsync(corruptPath, "{");
        var corruptStore = new FilePackStore(store.RootDirectory); await corruptStore.LoadTasksAsync();
        var rejected = false;
        try { await corruptStore.SaveTaskAsync(new(interrupted with { TaskId = corruptId }, PackState.Queued)); } catch (IOException) { rejected = true; }
        Require(rejected && await File.ReadAllTextAsync(corruptPath) == "{" && corruptStore.Diagnostics.Count > 0,
            "Corrupt task records were silently overwritten");
        Console.WriteLine("PASS FilePack persisted interruption, owned external restoration, clean retry and fail-closed per-task corruption");
    }

    private static async Task<PackTaskInfo> WaitPackTerminalAsync(FilePackManager manager, Guid id) =>
        await WaitPackStateAsync(manager, id, task => task.State is PackState.Completed or PackState.Cancelled or PackState.Failed or PackState.Interrupted);
    private static async Task<PackTaskInfo> WaitPackStateAsync(FilePackManager manager, Guid id, Func<PackTaskInfo, bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMinutes(2))
        {
            var task = manager.Tasks.Single(task => task.TaskId == id);
            if (condition(task)) return task;
            if (task.State is PackState.Failed or PackState.Cancelled or PackState.Interrupted) throw new InvalidOperationException("Task stopped before the expected state: " + task.Error);
            await Task.Delay(20);
        }
        throw new TimeoutException("FilePack task did not finish within two minutes");
    }
    private static async Task WaitPackInactiveAsync(FilePackManager manager, Guid id)
    {
        // The final event is sent immediately before the scheduler releases its slot.
        await Task.Delay(30);
        Require(manager.Tasks.Single(task => task.TaskId == id).State == PackState.AwaitingExternal, "External task left its waiting state");
    }

    private sealed class PackResourceProbe(Func<int[]> ownedProcessIds)
    {
        private readonly Dictionary<int, (double Cpu, ulong Read, ulong Write)> _samples = [];
        public long PeakWorkingSet { get; private set; }
        public double CpuSeconds => _samples.Values.Sum(sample => sample.Cpu);
        public ulong ReadBytes => _samples.Values.Aggregate(0UL, (sum, sample) => sum + sample.Read);
        public ulong WriteBytes => _samples.Values.Aggregate(0UL, (sum, sample) => sum + sample.Write);
        public async Task MonitorAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    long memory = 0;
                    foreach (var id in ownedProcessIds())
                    {
                        try
                        {
                            using var process = Process.GetProcessById(id); memory += process.WorkingSet64;
                            GetProcessIoCounters(process.Handle, out var io);
                            _samples[id] = (process.TotalProcessorTime.TotalSeconds, io.ReadTransferCount, io.WriteTransferCount);
                        }
                        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                    PeakWorkingSet = Math.Max(PeakWorkingSet, memory);
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PackIoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr process, out PackIoCounters counters);
    }
}

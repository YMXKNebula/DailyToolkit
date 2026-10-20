using System.IO;
using System.IO.Compression;
using DailyToolkit.Core.FilePack;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackCompletionNamingAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-completion-");
        try
        {
            var source = Path.Combine(fixture.FullName, "source.txt");
            await File.WriteAllTextAsync(source, "completion-time naming fixture");
            var entries = (await new FileCollectionService().CollectAsync([source])).Entries;
            var output = Directory.CreateDirectory(Path.Combine(fixture.FullName, "output")).FullName;
            var before = new DateTimeOffset(2026, 10, 20, 5, 59, 58, TimeSpan.Zero);
            var after = before.AddMinutes(2);
            Require(PackArchiveNaming.AtCompletion(after).EndsWith(after.ToLocalTime().ToString("HH：mm", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "Completion time must show a filename-safe fullwidth colon between hours and minutes");
            var clock = new PackNamingClock(before);
            var engine = new SevenZipAdapter(Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe"));
            engine.ProcessExited += _ => clock.Set(after);
            var store = new FilePackStore(Path.Combine(fixture.FullName, "module"));
            await using var manager = new FilePackManager(store, engine, timeProvider: clock);
            var existing = Path.Combine(output, PackArchiveNaming.AtCompletion(after) + ".zip");
            await File.WriteAllTextAsync(existing, "pre-existing file must survive");
            var initial = new PackSnapshot(Guid.NewGuid(), Guid.NewGuid(), "自动命名", entries,
                new(PackMode.BuiltIn, PackFormat.Zip, PackCompression.Fast, output, PackArchiveNaming.DefaultName,
                    PackOutputPolicy.AutoRename, UseCompletionTimeName: true), before.AddDays(-2));
            var queued = await manager.EnqueueAsync(initial);
            Require(Path.GetFileName(queued.OutputPath).StartsWith(PackArchiveNaming.AtCompletion(before), StringComparison.Ordinal),
                "Queued name did not use the fixture's initial clock");
            var finished = await WaitPackTerminalAsync(manager, queued.TaskId);
            Require(finished.State == PackState.Completed && finished.FinishedAt == after &&
                Path.GetFileName(finished.OutputPath) == PackArchiveNaming.AtCompletion(after) + " (2).zip",
                "Default archive name used enqueue time or failed to preserve the completion-minute collision: " + finished.Error);
            Require(await File.ReadAllTextAsync(existing) == "pre-existing file must survive", "Automatic naming overwrote an existing file");
            using (var archive = ZipFile.OpenRead(finished.OutputPath))
                Require(archive.GetEntry("source.txt") is not null, "Completion rename lost the verified archive");
            var second = await manager.EnqueueAsync(initial with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid() });
            second = await WaitPackTerminalAsync(manager, second.TaskId);
            Require(second.State == PackState.Completed && Path.GetFileName(second.OutputPath) == PackArchiveNaming.AtCompletion(after) + " (3).zip",
                "Repeated completion-minute naming did not select a unique output");
            var custom = initial with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid(),
                Options = initial.Options with { ArchiveName = "旅行整理", UseCompletionTimeName = false } };
            var named = await manager.EnqueueAsync(custom); named = await WaitPackTerminalAsync(manager, named.TaskId);
            Require(named.State == PackState.Completed && Path.GetFileName(named.OutputPath) == "旅行整理.zip", "Custom archive name was replaced");
            var saved = await store.LoadTasksAsync();
            Require(saved.Single(row => row.TaskId == finished.TaskId).OutputPath == finished.OutputPath &&
                saved.Single(row => row.TaskId == finished.TaskId).FinishedAt == after, "Persisted history lost the completion destination or time");
            Require(PackArchiveNaming.NormalizeDefault("文件集装箱") == PackArchiveNaming.DefaultName &&
                PackArchiveNaming.NormalizeDefault("旅行整理") == "旅行整理", "Default migration changed a custom name");
            Console.WriteLine("PASS Completion-minute archive names cross the enqueue minute, preserve existing/same-minute files and custom names, retain ZIP content and persist final destinations");
        }
        finally { fixture.Delete(true); }
    }

    private sealed class PackNamingClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }
}

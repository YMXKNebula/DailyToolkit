using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using DailyToolkit.Core.FilePack;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterFilePackPlanningTests()
    {
        Test("FilePack rejects traversing, rooted, device and Windows ambiguous archive paths", () =>
        {
            foreach (var path in new[] { "", "../outside", "a/../b", "a/./b", "/root", "C:/root", @"\\server\share",
                "a//b", "a/", "a:stream", "CON.txt", "NUL .txt", "COM1", "LPT¹.txt", "tail.", "tail ", "a\0b", "a?b" })
            {
                var rejected = false;
                try { FilePackPaths.NormalizeArchivePath(path); } catch (ArgumentException) { rejected = true; }
                Require(rejected, $"Unsafe archive path accepted: {path}");
            }
            Require(FilePackPaths.NormalizeArchivePath(@"文档\目录\说明.txt") == "文档/目录/说明.txt");
            Require(new FilePackPreferences(100).Normalize().MaximumParallel == 4 && new FilePackPreferences(0).Normalize().MaximumParallel == 1);
        });
        Test("FilePack rejects case-insensitive duplicates and file-directory prefix conflicts in either order", () =>
        {
            PackEntry Entry(string name, bool directory) => new(Guid.NewGuid(), directory ? "" : "source", name, directory,
                0, directory ? null : new(0, 0, 0), directory ? "" : "root");
            foreach (var entries in new[]
            {
                new[] { Entry("a", false), Entry("A", false) },
                new[] { Entry("folder", false), Entry("folder/child", false) },
                new[] { Entry("folder/child", false), Entry("FOLDER", false) },
                new[] { Entry("folder/empty", true), Entry("FOLDER", false) }
            })
            {
                var rejected = false;
                try { FilePackPaths.ValidateEntries(entries); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Manifest path conflict accepted");
            }
            FilePackPaths.ValidateEntries([Entry("folder", true), Entry("folder/empty", true), Entry("folder/file", false)]);
        });
        AsyncTest("FilePack recognizes existing physical source aliases without conflating equal contents", async () =>
        {
            if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP FilePack Windows file-identity fixture on another OS."); return; }
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-identity-");
            try
            {
                var source = Path.Combine(root.FullName, "source.zip"); var alias = Path.Combine(root.FullName, "alias.zip");
                var different = Path.Combine(root.FullName, "different.zip");
                await File.WriteAllTextAsync(source, "identical fixture contents"); await File.WriteAllTextAsync(different, "identical fixture contents");
                if (!FilePackCreateHardLink(alias, source, IntPtr.Zero))
                { Console.WriteLine($"SKIP FilePack hard-link fixture: Windows error {Marshal.GetLastPInvokeError()}."); return; }
                Require(FilePackPaths.AreSameFile(source, alias), "A physical source alias bypassed identity comparison");
                Require(!FilePackPaths.AreSameFile(source, different), "Different files with equal contents were conflated");
                Require(!FilePackPaths.AreSameFile(source, Path.Combine(root.FullName, "missing.zip")));
            }
            finally { Directory.Delete(root.FullName, true); }
        });
        AsyncTest("FilePack collection preserves Unicode folders, empty directories and originals", async () =>
        {
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-collect-");
            try
            {
                var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "资料"));
                Directory.CreateDirectory(Path.Combine(folder.FullName, "空目录"));
                var nested = Directory.CreateDirectory(Path.Combine(folder.FullName, "子目录"));
                var source = Path.Combine(nested.FullName, "说明.txt"); await File.WriteAllTextAsync(source, "Unicode fixture 文件集装箱");
                var stamp = FilePackPaths.CaptureSourceStamp(source); var digest = SHA256.HashData(await File.ReadAllBytesAsync(source));
                var service = new FileCollectionService(); var result = await service.CollectAsync([folder.FullName, source]);
                Require(result.Entries.Count == 2 && result.Entries.Any(entry => entry.ArchivePath == "资料/空目录" && entry.IsDirectory));
                Require(result.Entries.Any(entry => entry.ArchivePath == "资料/子目录/说明.txt" && entry.Size == stamp.Length));
                Require(result.Warnings.Any(warning => warning.Contains("重复来源")), "Duplicate source was not reported");
                var afterDigest = SHA256.HashData(await File.ReadAllBytesAsync(source));
                Require(stamp == FilePackPaths.CaptureSourceStamp(source) && digest.AsSpan().SequenceEqual(afterDigest));
                var again = await service.CollectAsync([source], result.Entries);
                Require(again.Entries.Count == result.Entries.Count, "CollectAsync did not return a deduplicated merged manifest");
            }
            finally { Directory.Delete(root.FullName, true); }
        });
        AsyncTest("FilePack renames conflicting files and whole folder prefixes without merging trees", async () =>
        {
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-conflict-");
            try
            {
                var first = Directory.CreateDirectory(Path.Combine(root.FullName, "first"));
                var second = Directory.CreateDirectory(Path.Combine(root.FullName, "second"));
                var firstFile = Path.Combine(first.FullName, "report.txt"); var secondFile = Path.Combine(second.FullName, "report.txt");
                await File.WriteAllTextAsync(firstFile, "first"); await File.WriteAllTextAsync(secondFile, "second");
                var result = await new FileCollectionService().CollectAsync([firstFile, secondFile]);
                Require(result.Entries.Select(entry => entry.ArchivePath).ToHashSet().SetEquals(["report (2).txt", "report.txt"]));
                var blockingFile = Path.Combine(first.FullName, "资料"); await File.WriteAllTextAsync(blockingFile, "blocking file");
                var folder = Directory.CreateDirectory(Path.Combine(second.FullName, "资料"));
                var child = Path.Combine(folder.FullName, "child.bin"); await File.WriteAllBytesAsync(child, [1, 2, 3]);
                var combined = await new FileCollectionService().CollectAsync([blockingFile, folder.FullName]);
                Require(combined.Entries.Any(entry => entry.ArchivePath == "资料 (2)/child.bin") && combined.Warnings.Count > 0);
                FilePackPaths.ValidateEntries(combined.Entries);
            }
            finally { Directory.Delete(root.FullName, true); }
        });
        AsyncTest("FilePack planner uses independent direct roots and empty source folders, stages custom mappings or virtual folders", async () =>
        {
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-plan-");
            try
            {
                var left = Directory.CreateDirectory(Path.Combine(root.FullName, "left")); var right = Directory.CreateDirectory(Path.Combine(root.FullName, "right"));
                var a = Path.Combine(left.FullName, "a.txt"); var b = Path.Combine(right.FullName, "b.txt");
                await File.WriteAllTextAsync(a, "a"); await File.WriteAllTextAsync(b, "bb");
                var collection = await new FileCollectionService().CollectAsync([a, b]); var snapshot = FilePackFixture(root.FullName, collection.Entries);
                var planner = new PackPlanner(); var direct = planner.Create(snapshot);
                Require(!direct.RequiresStaging && direct.Batches.Count == 2 && direct.Batches.All(batch => Path.IsPathFullyQualified(batch.WorkingDirectory)));
                var empty = Directory.CreateDirectory(Path.Combine(left.FullName, "empty"));
                var withEmpty = await new FileCollectionService().CollectAsync([empty.FullName], collection.Entries);
                Require(!planner.Create(snapshot with { Entries = withEmpty.Entries }).RequiresStaging,
                    "An unchanged empty source folder unnecessarily copied all files");
                var mapped = snapshot with { Entries = collection.Entries.Select(entry => entry with { ArchivePath = "自定义/" + entry.ArchivePath }).ToArray() };
                var staged = planner.Create(mapped); Require(staged.RequiresStaging && staged.RequiredStagingBytes == collection.Entries.Sum(entry => entry.Size));
                Require(planner.Create(snapshot with { Options = snapshot.Options with { Mode = PackMode.External } }).RequiresStaging);
                var virtualFolder = new PackEntry(Guid.NewGuid(), "", "空文件夹", true, 0, null, "");
                Require(planner.Create(snapshot with { Entries = [virtualFolder] }).RequiresStaging);
                Require(FilePackPaths.GetOutputPath(snapshot.Options with { ArchiveName = "name.zip", Format = PackFormat.SevenZip }).EndsWith("name.7z"));
                Require(!FilePackPaths.IsWithin(root.FullName + "-outside/a", root.FullName));
            }
            finally { Directory.Delete(root.FullName, true); }
        });
        AsyncTest("FilePack staging copies exact manifest, retains ownership and isolates sessions", async () =>
        {
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-stage-");
            try
            {
                var sourceRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "originals"));
                var source = Path.Combine(sourceRoot.FullName, "selected.bin"); await File.WriteAllBytesAsync(source, [4, 5, 6, 7]);
                File.SetAttributes(source, FileAttributes.ReadOnly);
                await File.WriteAllTextAsync(Path.Combine(sourceRoot.FullName, "not-selected.txt"), "must not be copied");
                var collected = await new FileCollectionService().CollectAsync([source]);
                PackEntry empty = new(Guid.NewGuid(), "", "renamed/empty", true, 0, null, "");
                var snapshot = FilePackFixture(root.FullName, [collected.Entries[0] with { ArchivePath = "renamed/data.bin" }, empty], PackMode.External);
                var manager = new FilePackWorkspaceManager(Path.Combine(root.FullName, "workspaces")); var stamp = FilePackPaths.CaptureSourceStamp(source);
                var staging = await manager.PrepareAsync(snapshot);
                Require(await manager.IsOwnedAsync(snapshot) && File.Exists(Path.Combine(staging, "renamed", "data.bin")) &&
                    Directory.Exists(Path.Combine(staging, "renamed", "empty")) && Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count() == 1);
                Require((await File.ReadAllBytesAsync(Path.Combine(staging, "renamed", "data.bin"))).SequenceEqual(new byte[] { 4, 5, 6, 7 }));
                await Expect<IOException>(() => manager.PrepareAsync(snapshot), "已有临时文件");
                var second = snapshot with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid() }; var otherStaging = await manager.PrepareAsync(second);
                Require(staging != otherStaging && await manager.IsOwnedAsync(second));
                await Expect<IOException>(() => manager.CleanupAsync(snapshot with { TaskId = Guid.NewGuid() }), "归属");
                Require(File.Exists(source) && FilePackPaths.CaptureSourceStamp(source) == stamp && await manager.IsOwnedAsync(snapshot));
                await manager.CleanupAsync(snapshot); Require(!Directory.Exists(staging) && Directory.Exists(otherStaging));
                await manager.CleanupAsync(second); Require(File.Exists(source) && File.Exists(Path.Combine(sourceRoot.FullName, "not-selected.txt")));
                var partial = snapshot with { TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid() };
                var partialSession = Directory.CreateDirectory(Path.GetDirectoryName(manager.GetWorkspacePath(partial))!);
                await File.WriteAllTextAsync(Path.Combine(partialSession.FullName, ".filepack-owner.json"),
                    JsonSerializer.Serialize(new { Version = 1, partial.TaskId, partial.SessionId }));
                Require(!await manager.IsOwnedAsync(partial), "An absent staging directory was treated as ready for external use");
                await manager.CleanupAsync(partial); Require(!Directory.Exists(partialSession.FullName), "Owned partial session was not cleaned");
            }
            finally
            {
                var source = Path.Combine(root.FullName, "originals", "selected.bin");
                if (File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
                Directory.Delete(root.FullName, true);
            }
        });
        AsyncTest("FilePack cancellation, insufficient space and changed sources fail without touching originals", async () =>
        {
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-errors-");
            try
            {
                var source = Path.Combine(root.FullName, "source.txt"); await File.WriteAllTextAsync(source, "before");
                var entries = (await new FileCollectionService().CollectAsync([source])).Entries; var snapshot = FilePackFixture(root.FullName, entries, PackMode.External);
                var manager = new FilePackWorkspaceManager(Path.Combine(root.FullName, "workspaces"));
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Expect<OperationCanceledException>(() => new FileCollectionService().CollectAsync([source], cancellationToken: cancelled.Token));
                await Expect<OperationCanceledException>(() => manager.PrepareAsync(snapshot, cancellationToken: cancelled.Token));
                Require(!Directory.Exists(manager.GetWorkspacePath(snapshot)) && await File.ReadAllTextAsync(source) == "before");
                using var duringPreparation = new CancellationTokenSource();
                var progress = new FilePackFixtureProgress(value => { if (value.State == PackState.Copying) duringPreparation.Cancel(); });
                await Expect<OperationCanceledException>(() => manager.PrepareAsync(snapshot, progress, duringPreparation.Token));
                Require(await manager.IsOwnedAsync(snapshot), "Cancelled preparation lost its cleanup ownership marker");
                await Expect<OperationCanceledException>(() => manager.CleanupAsync(snapshot, duringPreparation.Token));
                Require(await manager.IsOwnedAsync(snapshot), "Cancelled cleanup removed ownership");
                await manager.CleanupAsync(snapshot); Require(await File.ReadAllTextAsync(source) == "before");
                var noSpace = false;
                try { FilePackPaths.EnsureAvailableSpace(root.FullName, long.MaxValue); } catch (IOException) { noSpace = true; }
                Require(noSpace, "Impossible staging-space demand was accepted");
                await File.WriteAllTextAsync(source, "changed contents");
                await Expect<IOException>(() => manager.PrepareAsync(snapshot), "发生变化");
                Require(await File.ReadAllTextAsync(source) == "changed contents" && !Directory.Exists(manager.GetWorkspacePath(snapshot)));
            }
            finally { Directory.Delete(root.FullName, true); }
        });
        AsyncTest("FilePack skips source links and refuses linked or unmarked workspace cleanup", async () =>
        {
            var root = Directory.CreateTempSubdirectory("DailyToolkit-filepack-links-"); var link = Path.Combine(root.FullName, "link");
            string? workspaceLink = null;
            try
            {
                var sourceRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "originals"));
                var source = Path.Combine(sourceRoot.FullName, "keep.txt"); await File.WriteAllTextAsync(source, "keep originals");
                var snapshot = FilePackFixture(root.FullName, (await new FileCollectionService().CollectAsync([source])).Entries, PackMode.External);
                var manager = new FilePackWorkspaceManager(Path.Combine(root.FullName, "workspaces"));
                var unowned = Directory.CreateDirectory(Path.GetDirectoryName(manager.GetWorkspacePath(snapshot))!);
                await File.WriteAllTextAsync(Path.Combine(unowned.FullName, "foreign.txt"), "unowned data");
                await Expect<IOException>(() => manager.CleanupAsync(snapshot), "归属");
                Require(File.Exists(Path.Combine(unowned.FullName, "foreign.txt")) && !await manager.IsOwnedAsync(snapshot));
                Directory.Delete(unowned.FullName, true);
                try { Directory.CreateSymbolicLink(link, sourceRoot.FullName); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                { Console.WriteLine("SKIP FilePack symlink fixture: this Windows session does not permit symbolic-link creation."); return; }
                var collected = await new FileCollectionService().CollectAsync([link, source]);
                Require(collected.Entries.Count == 1 && collected.Warnings.Any(warning => warning.Contains("链接")));
                var staging = await manager.PrepareAsync(snapshot);
                workspaceLink = Path.Combine(staging, "unsafe-link"); Directory.CreateSymbolicLink(workspaceLink, sourceRoot.FullName);
                await Expect<IOException>(() => manager.CleanupAsync(snapshot), "链接");
                Require(await File.ReadAllTextAsync(source) == "keep originals" && await manager.IsOwnedAsync(snapshot));
                Directory.Delete(workspaceLink); workspaceLink = null;
                await manager.CleanupAsync(snapshot); Require(File.Exists(source));
            }
            finally
            {
                if (workspaceLink is not null && Directory.Exists(workspaceLink)) Directory.Delete(workspaceLink);
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(root.FullName, true);
            }
        });
    }

    private static PackSnapshot FilePackFixture(string directory, IReadOnlyList<PackEntry> entries, PackMode mode = PackMode.BuiltIn) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "isolated fixture", entries,
            new(mode, PackFormat.Zip, PackCompression.Standard, Path.Combine(directory, "outputs"), "fixture", AllowStaging: true), DateTimeOffset.UtcNow);

    private sealed class FilePackFixtureProgress(Action<PackProgress> report) : IProgress<PackProgress>
    {
        public void Report(PackProgress value) => report(value);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FilePackCreateHardLink(string newPath, string existingPath, IntPtr securityAttributes);
}

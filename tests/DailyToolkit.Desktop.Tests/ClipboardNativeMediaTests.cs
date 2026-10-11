using System.IO;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckClipboardNativeMediaAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-native-media-");
        var sources = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-native-media-source-");
        await using var repository = new ClipboardHistoryRepository(directory.FullName);
        using var source = ClipboardTestWindow(); using var probe = ClipboardTestWindow(); using var monitor = new ClipboardMonitor(source);
        var identity = new ClipboardWriteIdentity();
        var native = new ClipboardNativeAccess(probe.Handle, identity);
        await using var processor = new ClipboardCaptureProcessor(repository, new ClipboardNativeAccess(source.Handle, identity));
        try
        {
            await repository.InitializeAsync();
            var settings = new ClipboardHistorySettings { IsEnabled = true, ConsentAccepted = true, IncludeRestoredInWindowsHistory = true, UseCachedFiles = true };
            processor.Configure(settings); monitor.Updated += processor.Notify; monitor.Configure(true);
            await WriteClipboardFixtureAsync("image-png");
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Total == 1);
            var image = (await repository.GetAsync((await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Records.Single().Id))!;
            Require(ClipboardImageCodec.SamePixels(image.Content, SyntheticClipboardImage()), "Native PNG pixels changed");
            await WriteClipboardFixtureAsync("image-v5");
            await WaitClipboardAsync(async () => (await repository.GetAsync(image.Id))!.LastCapturedAt > image.LastCapturedAt);
            Require((await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Total == 1, "PNG and DIBV5 created separate image records");
            var before = (await repository.GetAsync(image.Id))!;
            for (var index = 0; index < 40; index++)
            {
                Require((await processor.RestoreAsync(before)).Success, "Native image restore failed");
                var read = await ReadClipboardFixtureAsync(native, settings);
                Require(read.Disposition == ClipboardReadDisposition.OwnWrite && ClipboardImageCodec.SamePixels(read.Content!, before.Content),
                    $"Native image own restore mismatch: {index}, {read.Disposition}, sequence {read.Sequence}");
            }
            await Task.Delay(100);
            Require((await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Total == 1 &&
                (await repository.GetAsync(image.Id))!.LastCapturedAt == before.LastCapturedAt, "40 native image restores changed capture time or history");
            await WriteClipboardFixtureAsync("image-dib");
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Total == 2);
            await WriteClipboardFixtureAsync("image-opaque"); await Task.Delay(100);
            Require((await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Total == 2, "Legacy DIB and opaque PNG did not deduplicate");
            foreach (var mode in new[] { "image-forbid", "image-malformed", "image-huge" })
            {
                await WriteClipboardFixtureAsync(mode); await Task.Delay(80);
                var disposition = (await ReadClipboardFixtureAsync(native, settings)).Disposition;
                Require(disposition == (mode == "image-forbid" ? ClipboardReadDisposition.Excluded :
                    mode == "image-huge" ? ClipboardReadDisposition.TooLarge : ClipboardReadDisposition.Unsupported), "Unsafe image fixture not safely classified");
            }
            Require((await repository.QueryAsync(new(Type: ClipboardContentType.Image))).Total == 2, "Rejected image entered history");
            Console.WriteLine("PASS Isolated native PNG/DIB/DIBV5 capture, pixel dedup, 40 image restores with exact sequence identity, privacy/malformed/size filters");

            var video = Path.Combine(sources.FullName, "synthetic-video.mp4");
            var payload = new byte[] { 10, 20, 30, 40, 50 }; await File.WriteAllBytesAsync(video, payload);
            await WriteClipboardFixtureAsync("files-real", video);
            await WaitClipboardAsync(async () =>
            {
                var page = await repository.QueryAsync(new(Type: ClipboardContentType.Files));
                return page.Total == 1 && await repository.HasFileCacheAsync(page.Records.Single().Id);
            });
            var files = (await repository.GetAsync((await repository.QueryAsync(new(Type: ClipboardContentType.Files))).Records.Single().Id))!;
            File.Delete(video);
            var paths = await repository.MaterializeCachedFilesAsync(files.Id, settings);
            var restoredFiles = files with { Content = ClipboardContent.FromFiles(paths) };
            var order = (await repository.QueryAsync(new())).Records.Select(entry => entry.Id).ToArray();
            for (var index = 0; index < 12; index++)
            {
                Require((await processor.RestoreAsync(index % 2 == 0 ? restoredFiles : before)).Success, "Native cached file/image mixed restore failed");
                Require((await ReadClipboardFixtureAsync(native, settings)).Disposition == ClipboardReadDisposition.OwnWrite, "Cached paths own-write suppression failed");
            }
            await Task.Delay(100);
            Require((await repository.QueryAsync(new())).Records.Select(entry => entry.Id).SequenceEqual(order) &&
                (await File.ReadAllBytesAsync(paths.Single())).AsSpan().SequenceEqual(payload) &&
                (await repository.GetAsync(files.Id))!.Content.Paths.Single() == video, "Cached restore modified original history paths/order or content");
            Console.WriteLine("PASS Isolated cross-process automatic video-file cache, deletion of original, native CF_HDROP cached recovery and mixed image/file restore without new history");
        }
        finally { monitor.Configure(false); await processor.DisposeAsync(); await repository.DisposeAsync(); DeleteClipboardFixture(directory); DeleteClipboardFixture(sources); }
    }
}

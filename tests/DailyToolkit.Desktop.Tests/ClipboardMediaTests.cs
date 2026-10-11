using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;
using Microsoft.Data.Sqlite;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static ClipboardContent SyntheticClipboardImage(int seed = 0, bool alpha = true)
    {
        const int width = 128, height = 96;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x + seed); pixels[offset + 1] = (byte)(y * 2 + seed);
            pixels[offset + 2] = (byte)(x + y + seed); pixels[offset + 3] = alpha ? (byte)(x * 2) : (byte)255;
        }
        return ClipboardImageCodec.FromPixels(width, height, pixels, 8);
    }
    private static byte[] ClipboardPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static async Task CheckClipboardMediaAsync()
    {
        var image = SyntheticClipboardImage();
        var dib = ClipboardImageCodec.FromDib(ClipboardImageCodec.Dib(image, true), 8);
        Require(dib.StableHash() == image.StableHash() && ClipboardImageCodec.SamePixels(image, dib), "DIBV5 alpha/pixel identity roundtrip failed");
        var source = ClipboardImageCodec.Decode(image);
        var otherDpi = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 144, 144, PixelFormats.Bgra32, null, ClipboardImageCodec.Pixels(source), source.PixelWidth * 4);
        var normalized = ClipboardImageCodec.FromPng(ClipboardPng(otherDpi), 8);
        Require(normalized.StableHash() == image.StableHash(), "PNG DPI changed image identity");
        var opaque = SyntheticClipboardImage(alpha: false);
        var legacy = ClipboardImageCodec.FromDib(ClipboardImageCodec.Dib(opaque, false), 8);
        Require(legacy.StableHash() == opaque.StableHash(), "DIB RGB normalization failed");
        var opaquePixels = ClipboardImageCodec.Pixels(ClipboardImageCodec.Decode(opaque));
        var dib24 = new byte[40 + 128 * 3 * 96];
        BitConverter.GetBytes(40).CopyTo(dib24, 0); BitConverter.GetBytes(128).CopyTo(dib24, 4); BitConverter.GetBytes(96).CopyTo(dib24, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib24, 12); BitConverter.GetBytes((ushort)24).CopyTo(dib24, 14);
        for (var y = 0; y < 96; y++) for (var x = 0; x < 128; x++)
            opaquePixels.AsSpan((y * 128 + x) * 4, 3).CopyTo(dib24.AsSpan(40 + ((95 - y) * 128 + x) * 3, 3));
        Require(ClipboardImageCodec.FromDib(dib24, 8).StableHash() == opaque.StableHash(), "Bottom-up 24-bit DIB changed pixels/orientation");
        var small = ClipboardImageCodec.Preview(image.Binary!, 40);
        Require(small.IsFrozen && small.PixelWidth <= 40 && small.PixelHeight <= 40, "Detached preview decoding was not bounded/frozen");
        var bomb = image.Binary!.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16, 4), 16384);
        BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(20, 4), 16384);
        await ClipboardThrowsAsync<ClipboardImageLimitException>(() => Task.Run(() => ClipboardImageCodec.FromPng(bomb, 8)));
        await ClipboardThrowsAsync<ClipboardImageFormatException>(() => Task.Run(() => ClipboardImageCodec.FromDib(new byte[12], 8)));
        Console.WriteLine("PASS Images: PNG normalization, DIB/DIBV5 alpha, stable pixels, bounded frozen previews, malformed/oversized decoding");

        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-media-");
        var protection = new ClipboardDataProtection(); var clock = new ClipboardClock { Now = DateTimeOffset.UtcNow };
        var settings = new ClipboardHistorySettings { IsEnabled = true, ConsentAccepted = true, UseCachedFiles = true };
        var repository = new ClipboardHistoryRepository(directory.FullName, protection, clock);
        try
        {
            await repository.InitializeAsync();
            var first = await repository.CaptureAsync(image, clock.Now, "synthetic-image", settings);
            await repository.SetFlagsAsync([first.Id], true, true, settings); await repository.UpdateNoteAsync(first.Id, "image secret note", settings);
            var repeated = await repository.CaptureAsync(normalized, clock.Now.AddSeconds(1), null, settings);
            Require(first.Id == repeated.Id && repeated.IsSaved && repeated.IsPinned && repeated.Note == "image secret note", "Image dedup lost identity or annotations");
            Require((await repository.GetSummaryAsync(first.Id))!.Content.Binary is null && (await repository.GetAsync(first.Id))!.Content.Binary is { Length: > 0 } &&
                (await repository.GetThumbnailAsync(first.Id)) is { Length: > 0 }, "Image paging loaded full binary or attachments missing");
            await repository.BackupAsync();
            var database = await File.ReadAllBytesAsync(repository.DatabasePath);
            Require(database.AsSpan().IndexOf(image.Binary!.AsSpan(0, 16)) == -1, "Plain PNG signature appeared in database");
            Require(!System.Text.Encoding.UTF8.GetString(database).Contains("image secret note"), "Image note leaked into metadata");
            await repository.DisposeAsync(); repository = new(directory.FullName, protection, clock); await repository.InitializeAsync();
            Require((await repository.GetAsync(first.Id))!.IsSaved && ClipboardImageCodec.SamePixels((await repository.GetAsync(first.Id))!.Content, image), "Image restart lost saved pixels");
            await repository.RestoreBackupAsync(); var receipt = await repository.DeleteAsync([first.Id]); await repository.UndoAsync(receipt, settings);
            Require((await repository.GetAsync(first.Id)) is not null, "Image delete undo lost binary");
            var differentImage = await repository.CaptureAsync(SyntheticClipboardImage(2), clock.Now, null, settings);
            using (var external = new SqliteConnection($"Data Source={repository.DatabasePath};Pooling=False"))
            {
                external.Open(); using var command = external.CreateCommand();
                command.CommandText = "UPDATE records SET hash=(SELECT hash FROM records WHERE id=$b) WHERE id=$a; DELETE FROM records WHERE id=$b;";
                command.Parameters.AddWithValue("$a", first.Id.ToString()); command.Parameters.AddWithValue("$b", differentImage.Id.ToString()); command.ExecuteNonQuery();
            }
            var collision = await repository.CaptureAsync(SyntheticClipboardImage(2), clock.Now, null, settings);
            Require(collision.Id != first.Id && ClipboardImageCodec.SamePixels((await repository.GetAsync(first.Id))!.Content, image), "Image hash collision merged different pixels");
            await repository.CleanupAsync(settings with { MaximumRecords = 1, RetentionDays = 1 });
            Require((await repository.GetAsync(first.Id))!.IsSaved, "Image cleanup removed saved record");
            Console.WriteLine("PASS Images: encrypted body/thumbnail, summary-only reads, dedup/annotations, restart, backup recovery, delete/undo and saved retention");

            var sourceDirectory = Path.Combine(directory.FullName, "sources");
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "folder", "empty"));
            var textPath = Path.Combine(sourceDirectory, "folder", "private.txt");
            var videoPath = Path.Combine(sourceDirectory, "video.mp4");
            await File.WriteAllTextAsync(textPath, "cached private file body");
            var video = RandomNumberGenerator.GetBytes(2500000); await File.WriteAllBytesAsync(videoPath, video);
            var fileRecord = await repository.CaptureAsync(ClipboardContent.FromFiles([Path.Combine(sourceDirectory, "folder"), videoPath]), clock.Now, null, settings);
            // Sources are deliberately outside the history directory; production
            // rejects recursive caching of its own database/cache directories.
            var outside = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-cache-source-");
            try
            {
                var folder = Path.Combine(outside.FullName, "folder"); Directory.Move(Path.Combine(sourceDirectory, "folder"), folder);
                var videoSource = Path.Combine(outside.FullName, "video.mp4"); File.Move(videoPath, videoSource);
                fileRecord = await repository.CaptureAsync(ClipboardContent.FromFiles([folder, videoSource]), clock.Now.AddSeconds(1), null, settings);
                await repository.CacheFilesAsync(fileRecord.Id, settings);
                Require(await repository.HasFileCacheAsync(fileRecord.Id) && (await repository.GetAsync(fileRecord.Id))!.StoredBytes > video.Length, "File chunks not counted in quota");
                await repository.UpdateNoteAsync(fileRecord.Id, "file note", settings);
                var bytesBefore = (await repository.GetAsync(fileRecord.Id))!.StoredBytes;
                await repository.CaptureAsync(fileRecord.Content, clock.Now.AddSeconds(2), null, settings);
                Require((await repository.GetAsync(fileRecord.Id))!.StoredBytes == bytesBefore, "Recapture forgot cached-file storage bytes");
                await repository.BackupAsync();
                await ClipboardThrowsAsync<OperationCanceledException>(() => repository.CacheFilesAsync(fileRecord.Id, settings, shouldContinue: () => false));
                await ClipboardThrowsAsync<InvalidDataException>(() => repository.CacheFilesAsync(fileRecord.Id, settings with { MaximumCachedFileMegabytes = 1 }));
                Require(await repository.HasFileCacheAsync(fileRecord.Id), "Rejected recache destroyed previous cache");
                Directory.Delete(folder, true); File.Delete(videoSource);
                var restored = await repository.MaterializeCachedFilesAsync(fileRecord.Id, settings);
                Require(restored.Length == 2 && Directory.Exists(Path.Combine(restored[0], "empty")) &&
                    await File.ReadAllTextAsync(Path.Combine(restored[0], "private.txt")) == "cached private file body" &&
                    (await File.ReadAllBytesAsync(restored[1])).AsSpan().SequenceEqual(video), "Deleted originals could not be materialized from verified cache");
                Require((await repository.MaterializeCachedFilesAsync(fileRecord.Id, settings)).SequenceEqual(restored), "Intact materialization was duplicated");
                await File.WriteAllTextAsync(restored[1], "consumer changed it");
                var repaired = await repository.MaterializeCachedFilesAsync(fileRecord.Id, settings);
                Require(!repaired.SequenceEqual(restored) && (await repository.MaterializeCachedFilesAsync(fileRecord.Id, settings)).SequenceEqual(repaired) &&
                    (await File.ReadAllBytesAsync(repaired[1])).AsSpan().SequenceEqual(video), "Modified materialization was reused or duplicated repeatedly");
                clock.Now = clock.Now.AddDays(2); await repository.CleanupAsync(settings);
                Require(!Directory.EnumerateDirectories(repository.RestoredFilesPath).Any() && await repository.HasFileCacheAsync(fileRecord.Id), "Temporary expiry retained files or deleted encrypted cache");
                var afterExpiry = await repository.MaterializeCachedFilesAsync(fileRecord.Id, settings);
                Require((await File.ReadAllBytesAsync(afterExpiry[1])).AsSpan().SequenceEqual(video), "Temporary expiry prevented later cached restore");
                var encryptedDb = await File.ReadAllBytesAsync(repository.DatabasePath);
                Require(!System.Text.Encoding.UTF8.GetString(encryptedDb).Contains("cached private file body") &&
                    !System.Text.Encoding.UTF8.GetString(encryptedDb).Contains(outside.FullName), "Cached body/path leaked into SQLite");
                await repository.DisposeAsync(); repository = new(directory.FullName, protection, clock); await repository.InitializeAsync();
                Require(await repository.HasFileCacheAsync(fileRecord.Id), "File cache did not survive restart");
                await repository.RestoreBackupAsync();
                var deletion = await repository.DeleteAsync([fileRecord.Id]);
                var recaptured = await repository.CaptureAsync(fileRecord.Content, clock.Now.AddSeconds(3), null, settings);
                await repository.UndoAsync(deletion, settings);
                Require(await repository.HasFileCacheAsync(recaptured.Id) && !await repository.HasFileCacheAsync(fileRecord.Id), "Undo recapture merge lost/duplicated file cache");
                var fromMerged = await repository.MaterializeCachedFilesAsync(recaptured.Id, settings);
                Require(File.Exists(fromMerged[1]), "Merged cached chunks not restored");
                await repository.RemoveFileCacheAsync(recaptured.Id);
                Require(!await repository.HasFileCacheAsync(recaptured.Id), "Individual cache removal failed");
                await repository.ClearAsync(true);
                Require(!Directory.EnumerateDirectories(repository.RestoredFilesPath).Any() && (await repository.QueryAsync(new())).Total == 0, "Clear-all retained plaintext materializations or records");
            }
            finally { DeleteClipboardFixture(outside); }
            Console.WriteLine("PASS Files: folders/empty directories/video chunks, encryption, quota rollback, original deletion, stable materialization, restart/backup, recapture undo, remove and clear-all");
        }
        finally { await repository.DisposeAsync(); DeleteClipboardFixture(directory); }
        await CheckClipboardMediaMigrationAsync();
        await CheckClipboardAutomaticCacheAsync();
        await CheckClipboardMediaViewModelAsync();
    }
    private static async Task CheckClipboardMediaMigrationAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-media-migration-");
        var settings = new ClipboardHistorySettings();
        var repository = new ClipboardHistoryRepository(directory.FullName);
        try
        {
            await repository.InitializeAsync();
            var text = await repository.CaptureAsync(ClipboardContent.FromText("v1 migration identity"), DateTimeOffset.UtcNow, null, settings);
            await repository.SetFlagsAsync([text.Id], true, true, settings); await repository.UpdateNoteAsync(text.Id, "v1 note");
            await repository.DisposeAsync();
            using (var connection = new SqliteConnection($"Data Source={repository.DatabasePath};Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE file_chunks; DROP TABLE file_cache; ALTER TABLE records DROP COLUMN image_payload; ALTER TABLE records DROP COLUMN thumbnail; PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }
            repository = new(directory.FullName); await repository.InitializeAsync();
            var repeated = await repository.CaptureAsync(text.Content, DateTimeOffset.UtcNow, null, settings);
            Require(repeated.Id == text.Id && repeated.IsSaved && repeated.IsPinned && repeated.Note == "v1 note", "v1 migration changed hash, identity or annotation");
            Console.WriteLine("PASS Atomic v1-to-v3 migration preserves existing text hashes, IDs, saved/pinned and notes");
        }
        finally { await repository.DisposeAsync(); DeleteClipboardFixture(directory); }
    }
    private static async Task CheckClipboardAutomaticCacheAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-auto-cache-");
        var sources = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-auto-source-");
        await using var repository = new ClipboardHistoryRepository(directory.FullName);
        ClipboardCaptureProcessor? processor = null;
        try
        {
            await repository.InitializeAsync();
            var access = new FakeClipboardAccess(); processor = new(repository, access); access.Updated = processor.Notify;
            var settings = new ClipboardHistorySettings { IsEnabled = true, ConsentAccepted = true, UseCachedFiles = true };
            processor.Configure(settings);
            var source = Path.Combine(sources.FullName, "one.txt"); await File.WriteAllTextAsync(source, "automatic cache one");
            access.CopyExternal(ClipboardContent.FromFiles([source]));
            Guid id = Guid.Empty;
            await WaitClipboardAsync(async () =>
            {
                var page = await repository.QueryAsync(new(Type: ClipboardContentType.Files));
                if (page.Total == 0) return false; id = page.Records.Single().Id; return await repository.HasFileCacheAsync(id);
            });
            processor.Configure(settings with { UseCachedFiles = false });
            var second = Path.Combine(sources.FullName, "two.txt"); await File.WriteAllTextAsync(second, "path only");
            access.CopyExternal(ClipboardContent.FromFiles([second]));
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new())).Total == 2); await Task.Delay(100);
            var pathOnly = (await repository.QueryAsync(new("two.txt"))).Records.Single();
            Require(!await repository.HasFileCacheAsync(pathOnly.Id) && await repository.HasFileCacheAsync(id), "Off switch cached new files or deleted previous cache");
            processor.Configure(settings with { MaximumCachedFileMegabytes = 1 });
            var large = Path.Combine(sources.FullName, "large.bin"); await File.WriteAllBytesAsync(large, new byte[1048577]);
            var notice = ""; processor.Status += value => notice = value;
            access.CopyExternal(ClipboardContent.FromFiles([large]));
            await WaitClipboardAsync(() => Task.FromResult(notice.Contains("大小限制")));
            Require((await repository.QueryAsync(new("large.bin"))).Total == 1, "Overlimit automatic cache lost path history");
            Console.WriteLine("PASS Automatic file cache on/off, retained existing cache, bounded queue and overlimit fallback diagnostics");
        }
        finally { if (processor is not null) await processor.DisposeAsync(); await repository.DisposeAsync(); DeleteClipboardFixture(directory); DeleteClipboardFixture(sources); }
    }
    private static async Task CheckClipboardMediaViewModelAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-media-viewmodel-");
        await using var model = new ClipboardHistoryViewModel(new(directory.FullName));
        try
        {
            await model.Ready; await model.EnsureRepositoryAsync();
            model.UseCachedFiles = true; model.MaximumCachedFileMegabytes = 80; Require(await model.FlushSettingsAsync(), "File settings not persisted");
            var image = await model.Repository.CaptureAsync(SyntheticClipboardImage(), DateTimeOffset.UtcNow, null, model.Settings);
            model.IsVisible = true;
            await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 1 && model.HasSelection));
            Require(model.HasImage && !model.CanDuplicateText && model.SelectedImage is null, "Image full payload loaded while collapsed");
            model.IsPreviewExpanded = true;
            await WaitClipboardAsync(() => Task.FromResult(model.SelectedImage is not null));
            Require(model.SelectedImage!.IsFrozen && await model.ThumbnailAsync(image.Id) is not null, "Image preview/thumbnail failed");
            for (var index = 0; index < 34; index++)
            {
                var another = await model.Repository.CaptureAsync(SyntheticClipboardImage(index + 1), DateTimeOffset.UtcNow, null, model.Settings);
                _ = await model.ThumbnailAsync(another.Id);
            }
            Require(model.ThumbnailCount <= 32, "Thumbnail memory cache grew without bounds");
            var missing = await model.Repository.CaptureAsync(ClipboardContent.FromFiles([Path.Combine(directory.FullName, "missing.bin")]), DateTimeOffset.UtcNow.AddSeconds(1), null, model.Settings);
            model.Selected = (await model.Repository.QueryAsync(new("missing.bin"))).Records.Single();
            await WaitClipboardAsync(() => Task.FromResult(model.HasFiles));
            var writes = 0; model.RestoreRequested = _ => { writes++; return Task.FromResult(ClipboardOperationResult.Ok); };
            Require(!await model.RestoreSelectedAsync() && writes == 0 && model.Notice.Contains("已不存在"), "Missing file restore falsely succeeded/wrote clipboard");
            model.IsVisible = false; Require(model.ThumbnailCount == 0 && model.SelectedImage is null, "Hidden panel retained image buffers");
            var reloaded = await model.Store.LoadAsync(); Require(reloaded.Settings.UseCachedFiles && reloaded.Settings.MaximumCachedFileMegabytes == 80, "Media settings did not survive reload");
            var view = new ClipboardSettingsView { DataContext = model };
            var toggle = (DailyToolkit.Desktop.Controls.FeatureSwitch)view.FindName("FileCacheSwitch");
            await view.Dispatcher.InvokeAsync(view.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            toggle.GetBindingExpression(DailyToolkit.Desktop.Controls.FeatureSwitch.IsOnProperty)!.UpdateTarget();
            toggle.SetCurrentValue(DailyToolkit.Desktop.Controls.FeatureSwitch.IsOnProperty, false);
            await view.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Require(!model.UseCachedFiles && await model.FlushSettingsAsync(), "Shared settings switch did not update/persist file-cache setting");
            Console.WriteLine("PASS Media view-model: lazy bounded previews/cache, missing-file explicit failure, hidden release and settings restore");
        }
        finally { await model.DisposeAsync(); DeleteClipboardFixture(directory); }
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;
using Microsoft.Data.Sqlite;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private sealed class ClipboardClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task ClipboardThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static void DeleteClipboardFixture(DirectoryInfo directory)
    {
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(directory.FullName).StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && directory.Name.StartsWith("DailyToolkit-clipboard-", StringComparison.Ordinal), "Unsafe fixture cleanup path");
        directory.Delete(true);
    }
    private static async Task CheckClipboardRepositoryAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-repository-");
        var clock = new ClipboardClock(); var settings = new ClipboardHistorySettings { ConsentAccepted = true, IsEnabled = true };
        var protection = new ClipboardDataProtection();
        var repository = new ClipboardHistoryRepository(directory.FullName, protection, clock);
        try
        {
            await repository.InitializeAsync();
            var alpha = ClipboardContent.FromText("private-body-alpha-📝\n第二行");
            var first = await repository.CaptureAsync(alpha, clock.Now, "fixture-app", settings);
            var repeated = await repository.CaptureAsync(alpha, clock.Now.AddSeconds(1), "fixture-app", settings);
            Require(first.Id == repeated.Id && first.CreatedAt == repeated.CreatedAt && (await repository.QueryAsync(new())).Total == 1, "Continuous duplicate created a record");
            await repository.UpdateNoteAsync(first.Id, "private-note-omega");
            await repository.SetFlagsAsync([first.Id], true, true, settings);
            var second = await repository.CaptureAsync(ClipboardContent.FromText("beta"), clock.Now.AddSeconds(2), null, settings);
            var recaptured = await repository.CaptureAsync(alpha, clock.Now.AddSeconds(3), "fixture-app", settings);
            Require(recaptured.Id == first.Id && recaptured.IsSaved && recaptured.IsPinned && recaptured.Note == "private-note-omega" && recaptured.LastCapturedAt > repeated.LastCapturedAt, "Noncontinuous duplicate lost annotations or capture time");
            var order = (await repository.QueryAsync(new())).Records.Select(record => record.Id).ToArray();
            await repository.MarkUsedAsync(second.Id);
            Require((await repository.QueryAsync(new())).Records.Select(record => record.Id).SequenceEqual(order) && (await repository.GetAsync(second.Id))!.CreatedAt == second.CreatedAt, "Use changed history order or creation time");
            var files = await repository.CaptureAsync(ClipboardContent.FromFiles(["D:\\fixture.txt", "D:\\fixture-folder"]), clock.Now, null, settings);
            var pathText = await repository.CaptureAsync(ClipboardContent.FromText(files.Content.DisplayText), clock.Now, null, settings);
            Require(files.Id != pathText.Id, "CF_HDROP was merged with plain text");
            var url = await repository.CaptureAsync(ClipboardContent.FromText("https://example.com/private-fixture"), clock.Now, null, settings);
            Require((await repository.QueryAsync(new("EXAMPLE.COM", Type: ClipboardContentType.Link))).Records.Single().Id == url.Id, "Case insensitive URL/type search failed");
            Require((await repository.QueryAsync(new("OMEGA"))).Records.Single().Id == first.Id, "Encrypted note search failed");
            Require((await repository.QueryAsync(new("fixture-folder", Type: ClipboardContentType.Files))).Records.Single().Id == files.Id, "Filename/path search failed");
            Console.WriteLine("PASS ClipRecall text/link/files, typed deduplication, annotations, capture/use times and encrypted search");

            var receipt = await repository.DeleteAsync([second.Id, pathText.Id]);
            Require(receipt.Count == 2 && await repository.GetAsync(second.Id) is null && await repository.GetAsync(files.Id) is not null, "Batch delete affected unrelated record");
            Require(await repository.UndoAsync(receipt, settings) == 2 && await repository.GetAsync(second.Id) is not null, "Batch undo lost records");
            receipt = await repository.DeleteAsync([first.Id]);
            var afterDelete = await repository.CaptureAsync(alpha, clock.Now.AddSeconds(5), null, settings);
            Require(afterDelete.Id != first.Id && !afterDelete.IsSaved, "Deleted content could not re-enter from external capture");
            await repository.UndoAsync(receipt, settings);
            var merged = await repository.GetAsync(afterDelete.Id);
            Require(merged is { IsSaved: true, IsPinned: true, Note: "private-note-omega" } && (await repository.QueryAsync(new("private-body-alpha"))).Total == 1, "Undo recapture race introduced duplicate or lost saved state");
            receipt = await repository.DeleteAsync([second.Id]); clock.Now = clock.Now.AddSeconds(13);
            Require(await repository.UndoAsync(receipt, settings) == 0, "Expired deletion was undone");
            await repository.CleanupAsync(settings);
            Console.WriteLine("PASS ClipRecall independent deletion, recapture, batch undo/merge and expiry");

            var manual = await repository.CaptureAsync(ClipboardContent.FromText("manual fixture"), clock.Now, null, settings, manual: true);
            Require(manual.IsManual && manual.LastCapturedAt is null, "Manual creation pretended an external capture");
            await repository.EditManualAsync(manual.Id, ClipboardContent.FromText("manual edited"), settings);
            Require((await repository.GetAsync(manual.Id))!.Content.Text == "manual edited", "Manual text was not edited");
            await ClipboardThrowsAsync<InvalidOperationException>(() => repository.EditManualAsync(files.Id, ClipboardContent.FromText("forbidden"), settings));
            await ClipboardThrowsAsync<InvalidOperationException>(() => repository.EditManualAsync(manual.Id, alpha, settings));
            await repository.UpdateNoteAsync(manual.Id, "independent note");
            Require((await repository.GetAsync(manual.Id))!.Content.Text == "manual edited", "Note modified clipboard payload");
            await ClipboardThrowsAsync<InvalidDataException>(() => repository.CaptureAsync(ClipboardContent.FromText(new string('x', 1025)), clock.Now, null, settings with { MaximumTextKilobytes = 1 }));

            // Force a real hash-index collision through an external connection; the
            // repository must compare decrypted content, not trust the index alone.
            var collisionA = await repository.CaptureAsync(ClipboardContent.FromText("collision-a"), clock.Now, null, settings);
            var collisionB = await repository.CaptureAsync(ClipboardContent.FromText("collision-b"), clock.Now, null, settings);
            using (var external = new SqliteConnection($"Data Source={repository.DatabasePath};Pooling=False"))
            {
                external.Open(); using var command = external.CreateCommand();
                command.CommandText = "UPDATE records SET hash=(SELECT hash FROM records WHERE id=$b) WHERE id=$a; DELETE FROM records WHERE id=$b;";
                command.Parameters.AddWithValue("$a", collisionA.Id.ToString()); command.Parameters.AddWithValue("$b", collisionB.Id.ToString()); command.ExecuteNonQuery();
            }
            var collisionNew = await repository.CaptureAsync(ClipboardContent.FromText("collision-b"), clock.Now, null, settings);
            Require(collisionNew.Id != collisionA.Id && (await repository.GetAsync(collisionA.Id))!.Content.Text == "collision-a", "Hash collision merged different contents");
            Console.WriteLine("PASS ClipRecall manual editing, immutable external content, size limits and actual-content collision validation");

            var changes = 0; void Changed() => Interlocked.Increment(ref changes); repository.Changed += Changed;
            await repository.SetFlagsAsync([manual.Id, files.Id], saved: true, pinned: null, settings);
            Require(changes == 1, "Batch flags emitted one full refresh per item"); repository.Changed -= Changed;
            await repository.CleanupAsync(settings with { MaximumRecords = 1 });
            var statistics = await repository.StatisticsAsync(); Require(statistics.Ordinary <= 1 && statistics.Saved == 3, "Quantity cleanup deleted saved records");
            clock.Now = clock.Now.AddDays(31); await repository.CleanupAsync(settings);
            Require((await repository.StatisticsAsync()).Ordinary == 0 && await repository.GetAsync(manual.Id) is not null && await repository.GetAsync(files.Id) is not null, "Retention cleanup deleted saved records");
            await repository.SetFlagsAsync([manual.Id], saved: false, pinned: true, settings);
            Require(await repository.GetAsync(manual.Id) is null, "Unsaved old pinned record ignored ordinary cleanup");
            var large1 = await repository.CaptureAsync(ClipboardContent.FromText(new string('a', 600000)), clock.Now, null, settings);
            clock.Now = clock.Now.AddSeconds(1);
            var large2 = await repository.CaptureAsync(ClipboardContent.FromText(new string('b', 600000)), clock.Now, null, settings with { MaximumOrdinaryMegabytes = 1 });
            Require(await repository.GetAsync(large1.Id) is null && await repository.GetAsync(large2.Id) is not null && (await repository.StatisticsAsync()).OrdinaryBytes <= 1048576, "Byte quota was not enforced");
            await ClipboardThrowsAsync<InvalidDataException>(() => repository.CaptureAsync(ClipboardContent.FromText(new string('z', 1048576)), clock.Now, null,
                settings with { MaximumOrdinaryMegabytes = 1 }));
            Require(await repository.GetAsync(large2.Id) is not null, "Rejected oversized encrypted payload evicted existing history");
            await repository.BackupAsync(); var deletion = await repository.DeleteAsync([large2.Id]);
            Require(!File.Exists(repository.BackupPath), "Deleted content remained in an active recovery backup"); await repository.UndoAsync(deletion, settings);
            Console.WriteLine("PASS ClipRecall count/age/byte cleanup, independent saved/pinned flags and single batch notification");

            using (var external = new SqliteConnection($"Data Source={repository.DatabasePath};Pooling=False"))
            {
                external.Open(); using var transaction = external.BeginTransaction();
                using var command = external.CreateCommand(); command.CommandText = "UPDATE records SET pinned=pinned;"; command.ExecuteNonQuery();
                await ClipboardThrowsAsync<SqliteException>(() => repository.CaptureAsync(ClipboardContent.FromText("locked fixture"), clock.Now, null, settings));
                transaction.Rollback();
            }
            Require((await repository.QueryAsync(new("locked fixture"))).Total == 0, "Locked write partially committed");
            await repository.BackupAsync();
            foreach (var path in new[] { repository.DatabasePath, repository.DatabasePath + "-wal", repository.BackupPath })
            {
                if (!File.Exists(path)) continue;
                var bytes = await File.ReadAllBytesAsync(path);
                foreach (var secret in new[] { "private-body-alpha", "private-note-omega", "fixture-folder" })
                    Require(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0 && bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)) < 0, "Plaintext leaked to database or backup");
            }
            await repository.DisposeAsync(); repository = new(directory.FullName, protection, clock); await repository.InitializeAsync();
            Require((await repository.GetAsync(afterDelete.Id)) is { IsSaved: true, Note: "private-note-omega" } && (await repository.GetAsync(files.Id)) is { IsSaved: true }, "Saved records/notes did not survive reopening");
            await repository.DisposeAsync();
            var corrupted = Encoding.UTF8.GetBytes("not a SQLite database fixture"); await File.WriteAllBytesAsync(repository.DatabasePath, corrupted);
            repository = new(directory.FullName, protection, clock);
            await ClipboardThrowsAsync<SqliteException>(() => repository.InitializeAsync());
            Require((await File.ReadAllBytesAsync(repository.DatabasePath)).SequenceEqual(corrupted), "Corrupt database was overwritten");
            await repository.RestoreBackupAsync();
            Require((await repository.GetAsync(afterDelete.Id)) is { IsSaved: true } && Directory.GetDirectories(directory.FullName, "recovery-*").Length == 1, "Validated recovery did not preserve original database");
            await repository.ClearAsync(false); Require((await repository.StatisticsAsync()).Saved == 2 && !File.Exists(repository.BackupPath), "Ordinary clear lost permanent saves or retained a backup");
            await repository.ClearAsync(true); Require((await repository.StatisticsAsync()).Total == 0 &&
                Directory.GetDirectories(directory.FullName, "recovery-*").Length == 0, "Clear all retained saved history or recovery copies");
            Console.WriteLine("PASS ClipRecall busy rollback, DPAPI-only payload storage, reopen, corrupt-file preservation, backup recovery and clearing");
            await repository.CaptureAsync(ClipboardContent.FromText("interrupted restore fixture"), clock.Now, null, settings);
            await repository.BackupAsync(); await repository.DisposeAsync();
            var interruptedRecovery = Path.Combine(directory.FullName, "recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(interruptedRecovery);
            File.Move(repository.DatabasePath, Path.Combine(interruptedRecovery, "history.db"));
            var interruptedTemporary = repository.DatabasePath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(repository.BackupPath, interruptedTemporary);
            repository = new(directory.FullName, protection, clock);
            await ClipboardThrowsAsync<InvalidDataException>(() => repository.InitializeAsync());
            Require(!File.Exists(repository.DatabasePath) && File.Exists(interruptedTemporary), "Interrupted restore silently created empty history or removed recovery data");
            await repository.RestoreBackupAsync();
            Require((await repository.QueryAsync(new("interrupted restore"))).Total == 1, "Interrupted restore could not recover validated backup");
            await repository.ClearAsync(true);
            Require(!File.Exists(interruptedTemporary) && Directory.GetDirectories(directory.FullName, "recovery-*").Length == 0,
                "Explicit all-history clear retained interrupted recovery files");
            Console.WriteLine("PASS ClipRecall interrupted-restore fail-closed startup, original-file preservation, recovery and explicit orphan cleanup");
        }
        finally { await repository.DisposeAsync(); DeleteClipboardFixture(directory); }
    }
}

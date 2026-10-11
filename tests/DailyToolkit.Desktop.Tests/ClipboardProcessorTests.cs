using System.IO;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private sealed class FakeClipboardAccess : IClipboardAccess
    {
        private readonly object _gate = new();
        private readonly ClipboardWriteIdentity _identity = new();
        private ClipboardContent? _content;
        private byte[]? _marker;
        private uint _sequence = 1;
        internal int BusyReads, Writes;
        internal bool FailRestore, Excluded;
        internal Action? Updated;
        internal Action<int>? AfterWrite;
        internal void CopyExternal(ClipboardContent content, bool keepMarker = false, bool notify = true)
        {
            lock (_gate) { _content = content; _sequence++; if (!keepMarker) _marker = null; }
            if (notify) Updated?.Invoke();
        }
        public ClipboardReadResult Read(ClipboardHistorySettings settings)
        {
            lock (_gate)
            {
                if (BusyReads-- > 0) return new(ClipboardReadDisposition.Busy, _sequence);
                if (Excluded) return new(ClipboardReadDisposition.Excluded, _sequence);
                if (_content is null) return new(ClipboardReadDisposition.Empty, _sequence);
                return new(_identity.IsOwn(_sequence, _marker, _content) ? ClipboardReadDisposition.OwnWrite : ClipboardReadDisposition.Captured, _sequence, _content, "fixture-app");
            }
        }
        public ClipboardRestoreAttempt Restore(ClipboardContent content, ClipboardHistorySettings settings)
        {
            if (FailRestore) return new(new(false, "fixture restore failed"));
            ClipboardReadResult previous;
            lock (_gate)
            {
                previous = Read(settings); _content = content; _sequence++; var operation = Guid.NewGuid();
                _marker = _identity.CreateMarker(operation); _identity.Register(_sequence, operation, content); Writes++;
            }
            Updated?.Invoke(); AfterWrite?.Invoke(Writes);
            return new(ClipboardOperationResult.Ok, previous);
        }
    }
    private static async Task WaitClipboardAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await predicate()) return; await Task.Delay(20); }
        throw new InvalidOperationException("Clipboard fixture did not reach expected state");
    }
    private static async Task CheckClipboardProcessorAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-processor-");
        await using var repository = new ClipboardHistoryRepository(directory.FullName);
        ClipboardCaptureProcessor? processor = null;
        try
        {
            await repository.InitializeAsync(); var settings = new ClipboardHistorySettings { ConsentAccepted = true, IsEnabled = true };
            var access = new FakeClipboardAccess(); processor = new(repository, access); processor.Configure(settings); access.Updated = processor.Notify;
            access.CopyExternal(ClipboardContent.FromText("first external"));
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new())).Total == 1);
            var first = await repository.GetAsync((await repository.QueryAsync(new())).Records.Single().Id);
            access.CopyExternal(ClipboardContent.FromText("first external"));
            await WaitClipboardAsync(async () => (await repository.GetAsync(first!.Id))!.LastCapturedAt > first!.LastCapturedAt);
            access.CopyExternal(ClipboardContent.FromText("second external"));
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new())).Total == 2);
            var second = await repository.GetAsync((await repository.QueryAsync(new("second"))).Records.Single().Id);
            var order = (await repository.QueryAsync(new())).Records.Select(entry => entry.Id).ToArray();
            var capturedAt = (await repository.GetAsync(first!.Id))!.LastCapturedAt;
            var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => processor.RestoreAsync(index % 2 == 0 ? first : second!)));
            Require(results.All(result => result.Success) && (await repository.QueryAsync(new())).Total == 2 &&
                (await repository.QueryAsync(new())).Records.Select(entry => entry.Id).SequenceEqual(order) &&
                (await repository.GetAsync(first.Id))!.LastCapturedAt == capturedAt, "Rapid self restore entered capture/dedup ordering");
            Console.WriteLine("PASS ClipRecall external events, continuous duplicates and 40 queued restores without new history or reordering");

            // No notification: emulate an external change whose update message is
            // still queued when the user restores. Preflight must preserve it.
            access.CopyExternal(ClipboardContent.FromText("pending external race"), notify: false);
            Require((await processor.RestoreAsync(first)).Success && (await repository.QueryAsync(new("pending external race"))).Total == 1,
                "Restore overwrote an external copy whose notification had not been processed");
            access.AfterWrite = count => { if (count == 43) access.CopyExternal(ClipboardContent.FromText("external during restore")); };
            await processor.RestoreAsync(first); await processor.RestoreAsync(second!); await processor.RestoreAsync(first);
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new("external during restore"))).Total == 1);
            access.AfterWrite = null;
            // Other programs may forward our marker. A different sequence must be
            // treated as a real external write even when content/marker are equal.
            var before = (await repository.GetAsync(first.Id))!.LastCapturedAt;
            await processor.RestoreAsync(first); access.CopyExternal(first.Content, keepMarker: true);
            await WaitClipboardAsync(async () => (await repository.GetAsync(first.Id))!.LastCapturedAt > before);
            Console.WriteLine("PASS ClipRecall pending/external restore races and forwarded marker with a new sequence");

            access.BusyReads = 2; access.CopyExternal(ClipboardContent.FromText("busy then readable"));
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new("busy then readable"))).Total == 1);
            access.FailRestore = true; Require(!(await processor.RestoreAsync(first)).Success, "Failed restore reported success"); access.FailRestore = false;
            processor.Configure(settings with { IsPaused = true });
            access.CopyExternal(ClipboardContent.FromText("paused secret")); await processor.ReleaseIdleWorkerAsync();
            Require((await repository.QueryAsync(new("paused secret"))).Total == 0 && !processor.HasWorker, "Pause recorded content or retained STA worker");
            processor.Configure(settings); access.Excluded = true; access.CopyExternal(ClipboardContent.FromText("excluded secret"));
            await Task.Delay(100); Require((await repository.QueryAsync(new("excluded secret"))).Total == 0, "Excluded content was captured");
            access.Excluded = false;
            var countBefore = (await repository.QueryAsync(new())).Total;
            var readsBefore = access.BusyReads; await Task.Delay(300);
            Require(access.BusyReads == readsBefore && (await repository.QueryAsync(new())).Total == countBefore, "Idle processor polled clipboard");
            processor.Configure(new()); await processor.ReleaseIdleWorkerAsync();
            await repository.CaptureAsync(ClipboardContent.FromText("manual does not copy"), DateTimeOffset.UtcNow, null, settings, manual: true);
            Require(access.Writes == 45, "Manual creation unexpectedly wrote the clipboard");
            Console.WriteLine("PASS ClipRecall bounded contention retries, explicit restore failure, pause/exclusion, no idle polling and no manual clipboard write");
        }
        finally
        {
            if (processor is not null) await processor.DisposeAsync();
            await repository.DisposeAsync(); DeleteClipboardFixture(directory);
        }
    }
}

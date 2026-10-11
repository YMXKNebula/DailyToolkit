using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using DailyToolkit.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace DailyToolkit.Desktop.ClipRecall;

// A single pending event coalesces notifications while the clipboard is busy.
// Restore shares the same operation gate and samples the previous payload under
// the native clipboard lock, so our own writes cannot hide a pending external copy.
internal sealed class ClipboardCaptureProcessor : IAsyncDisposable
{
    private readonly ClipboardHistoryRepository _repository;
    private readonly IClipboardAccess _access;
    private readonly Channel<byte> _notifications = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim _operation = new(1);
    private readonly Task _pump;
    private readonly Channel<(Guid Id, long Epoch)> _fileNotifications = Channel.CreateBounded<(Guid, long)>(
        new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _filePump;
    private readonly TimeProvider _clock;
    private ClipboardHistorySettings _settings = new();
    private ClipboardStaWorker? _worker;
    private uint? _lastSequence;
    private long _sequenceEpoch = -1;
    private long _epoch, _coalesced;
    private bool _disposed, _stopCaching;
    public event Action<string>? Status;
    internal long CoalescedNotifications => Interlocked.Read(ref _coalesced);
    internal bool HasWorker => _worker is not null;
    internal bool IsCapturing => _settings.IsEnabled && !_settings.IsPaused;
    public ClipboardCaptureProcessor(ClipboardHistoryRepository repository, IClipboardAccess access, TimeProvider? clock = null)
    { _repository = repository; _access = access; _clock = clock ?? TimeProvider.System; _pump = PumpAsync(); _filePump = CacheFilesAsync(); }
    public void Configure(ClipboardHistorySettings settings)
    {
        var previous = _settings;
        Volatile.Write(ref _settings, settings);
        if (previous.IsEnabled != settings.IsEnabled || previous.IsPaused != settings.IsPaused || previous.UseCachedFiles != settings.UseCachedFiles)
        { Interlocked.Increment(ref _epoch); }
    }
    public void Notify()
    {
        if (_disposed || !IsCapturing) return;
        if (!_notifications.Writer.TryWrite(0)) Interlocked.Increment(ref _coalesced);
    }
    private async Task<ClipboardReadResult> ReadAsync(ClipboardHistorySettings settings)
    {
        _worker ??= new();
        ClipboardReadResult? result = null;
        foreach (var delay in new[] { 0, 20, 40, 80, 160 })
        {
            if (delay != 0) await Task.Delay(delay).ConfigureAwait(false);
            result = await _worker.InvokeAsync(() => _access.Read(settings)).ConfigureAwait(false);
            if (result.Disposition != ClipboardReadDisposition.Busy) return result;
        }
        return result!;
    }
    private async Task ProcessAsync(ClipboardReadResult result, ClipboardHistorySettings settings, long epoch)
    {
        if (epoch != Interlocked.Read(ref _epoch) || !IsCapturing) return;
        if (_sequenceEpoch != epoch) { _lastSequence = null; _sequenceEpoch = epoch; }
        if (result.Disposition == ClipboardReadDisposition.Captured && result.Content is not null)
        {
            if (_lastSequence == result.Sequence) return;
            var record = await _repository.CaptureAsync(result.Content, _clock.GetUtcNow(), result.Source, settings).ConfigureAwait(false);
            if (settings.UseCachedFiles && result.Content.Type == ClipboardContentType.Files &&
                !_fileNotifications.Writer.TryWrite((record.Id, epoch)))
                Status?.Invoke("文件缓存队列已满，此次仅保留原路径；可以稍后手动缓存。");
            _lastSequence = result.Sequence;
        }
        else if (result.Disposition == ClipboardReadDisposition.OwnWrite) _lastSequence = result.Sequence;
        else if (result.Message.Length > 0) Status?.Invoke(result.Message);
    }
    private async Task CacheFilesAsync()
    {
        await foreach (var item in _fileNotifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool Continue() => !_disposed && !Volatile.Read(ref _stopCaching) && IsCapturing && _settings.UseCachedFiles && item.Epoch == Interlocked.Read(ref _epoch);
            if (!Continue()) continue;
            try
            {
                await _repository.CacheFilesAsync(item.Id, _settings, shouldContinue: Continue).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (IsExpectedError(exception))
            {
                Status?.Invoke(exception is InvalidDataException or InvalidOperationException ? exception.Message :
                    "文件未能缓存，原路径记录保留；请检查文件可读性、空间或权限。");
            }
        }
    }
    private async Task PumpAsync()
    {
        await foreach (var _ in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _operation.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsCapturing) continue;
                var settings = _settings; var epoch = Interlocked.Read(ref _epoch);
                await ProcessAsync(await ReadAsync(settings).ConfigureAwait(false), settings, epoch).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedError(exception))
            { Status?.Invoke("剪贴板内容未能保存，原历史已保留；请检查磁盘空间、数据目录权限或数据库占用。"); }
            finally { _operation.Release(); }
        }
    }
    public async Task<ClipboardOperationResult> RestoreAsync(ClipboardHistoryRecord record)
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= new(); var settings = _settings; var epoch = Interlocked.Read(ref _epoch);
            ClipboardRestoreAttempt? attempt = null;
            foreach (var delay in new[] { 0, 20, 40, 80, 160 })
            {
                if (delay != 0) await Task.Delay(delay).ConfigureAwait(false);
                attempt = await _worker.InvokeAsync(() => _access.Restore(record.Content, settings)).ConfigureAwait(false);
                // Previous != null means the clipboard was opened; a partial write
                // must be reported, rather than retrying and pretending atomicity.
                if (attempt.Result.Success || attempt.Previous is not null) break;
            }
            var message = attempt!.Result.Message;
            if (attempt.Previous is not null && IsCapturing)
            {
                try { await ProcessAsync(attempt.Previous, settings, epoch).ConfigureAwait(false); }
                catch (Exception exception) when (IsExpectedError(exception))
                { message += " 恢复前的外部内容未能保存，请检查数据目录。"; }
            }
            if (!attempt.Result.Success) return attempt.Result with { Message = message };
            try { await _repository.MarkUsedAsync(record.Id).ConfigureAwait(false); }
            catch (Exception exception) when (IsExpectedError(exception)) { message += " 内容已复制，但最近使用时间未能保存。"; }
            return new(true, message.Length > 0 ? message.Trim() : "已复制；历史顺序保持不变。");
        }
        catch (Exception exception) when (IsExpectedError(exception)) { return new(false, "剪贴板恢复未完成，请重试。"); }
        finally { _operation.Release(); }
    }
    public async Task ReleaseIdleWorkerAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try { if (!IsCapturing && _worker is not null) { await _worker.DisposeAsync().ConfigureAwait(false); _worker = null; } }
        finally { _operation.Release(); }
    }
    internal static bool IsExpectedError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or SqliteException or CryptographicException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
        or System.IO.FileFormatException or NotSupportedException or System.Runtime.InteropServices.COMException or OutOfMemoryException;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Volatile.Write(ref _stopCaching, true);
        _notifications.Writer.TryComplete(); await _pump.ConfigureAwait(false);
        _fileNotifications.Writer.TryComplete(); await _filePump.ConfigureAwait(false);
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_worker is not null) { await _worker.DisposeAsync().ConfigureAwait(false); _worker = null; }
        }
        finally { _operation.Release(); }
    }
}

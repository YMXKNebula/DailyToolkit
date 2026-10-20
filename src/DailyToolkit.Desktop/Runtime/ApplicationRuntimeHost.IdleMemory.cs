using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace DailyToolkit.Desktop.Runtime;

internal sealed partial class ApplicationRuntimeHost
{
    private const long MinimumIdleAllocation = 8 * 1024 * 1024, MaximumIdleHeap = 64 * 1024 * 1024;
    private CancellationTokenSource? _idleMemoryCancellation;
    private DispatcherOperation? _idleMemoryOperation;
    private long _hiddenSince, _hiddenGeneration, _lastIdleCollectionAt, _lastIdleCollectionAllocated;
    private bool _idleMemoryReclaimDeferred;
    // DispatcherTimer uses coarse Environment.TickCount, while eligibility uses
    // Stopwatch. A small scheduling margin preserves the full hide interval.
    private TimeSpan IdleTimerInterval => _idleDelay + TimeSpan.FromMilliseconds(25);

    internal int IdleMemoryCollectionCount { get; private set; }
    internal double LastIdleMemoryCollectionMilliseconds { get; private set; }
    internal double LastIdleUiRetirementMilliseconds { get; private set; }
    internal double LastIdleAttemptHiddenMilliseconds { get; private set; }
    internal bool HasPendingIdleMemoryReclaim => _idleTimer.IsEnabled || _idleMemoryCancellation is not null ||
        _idleMemoryOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing;

    internal bool TryQueueIdleMemoryReclaim()
    {
        _dispatcher.VerifyAccess();
        _idleTimer.Stop();
        LastIdleAttemptHiddenMilliseconds = _hiddenSince == 0 ? 0 : Stopwatch.GetElapsedTime(_hiddenSince).TotalMilliseconds;
        var current = Window;
        if (_idleMemoryCancellation is not null || current is null ||
            (current.CreatedSettingsViewCount == 0 && !_idleMemoryReclaimDeferred) ||
            !CanCollectHiddenMemory(current, _hiddenGeneration)) return false;
        if (current.CreatedSettingsViewCount > 0)
        {
            var watch = Stopwatch.StartNew();
            if (!current.TryReleaseDeferredViewsForIdle()) return false;
            LastIdleUiRetirementMilliseconds = watch.Elapsed.TotalMilliseconds;
            _idleMemoryReclaimDeferred = true;
        }
        var cancellation = _idleMemoryCancellation = new();
        _ = CollectAfterPageCleanupAsync(current, _hiddenGeneration, cancellation);
        return true;
    }

    private async Task CollectAfterPageCleanupAsync(MainWindow window, long generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        DispatcherOperation? operation = null;
        try
        {
            // UIA cleanup may enqueue a bounded retry. Await its actual result;
            // merely crossing one dispatcher idle callback is insufficient.
            var results = await window.DeferredAutomationCleanup.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!results.All(success => success)) return;
            operation = _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_idleMemoryOperation, operation)) _idleMemoryOperation = null;
                if (token.IsCancellationRequested || !CanCollectHiddenMemory(window, generation, requireMinimumHeap: false)) return;
                // This deliberately blocks managed execution once. All tools,
                // writes and windows must be idle; showing UI cancels queued work.
                // The heap bound is an eligibility limit, not a pause guarantee.
                var watch = Stopwatch.StartNew();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                LastIdleMemoryCollectionMilliseconds = watch.Elapsed.TotalMilliseconds;
                IdleMemoryCollectionCount++;
                _idleMemoryReclaimDeferred = false;
                _lastIdleCollectionAt = Stopwatch.GetTimestamp();
                _lastIdleCollectionAllocated = GC.GetTotalAllocatedBytes(precise: false);
            }, DispatcherPriority.ApplicationIdle);
            _idleMemoryOperation = operation;
            await operation.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { DiagnosticReportService.Shared.RecordError(DiagnosticModule.Application, error); }
        finally
        {
            if (ReferenceEquals(_idleMemoryCancellation, cancellation))
                _idleMemoryCancellation = null;
            if (ReferenceEquals(_idleMemoryOperation, operation)) _idleMemoryOperation = null;
            cancellation.Dispose();
        }
    }

    private bool CanCollectHiddenMemory(MainWindow window, long generation, bool requireMinimumHeap = true)
    {
        if (_disposed || !ReferenceEquals(Window, window) || generation != _hiddenGeneration || _hiddenSince == 0 ||
            Stopwatch.GetElapsedTime(_hiddenSince) < _idleDelay || !window.CanReclaimHiddenUiMemory ||
            Application.Current.Windows.Cast<Window>().Any(item => item.IsVisible) ||
            (_lastIdleCollectionAt != 0 && Stopwatch.GetElapsedTime(_lastIdleCollectionAt) < TimeSpan.FromMinutes(1)) ||
            GC.GetTotalAllocatedBytes(precise: false) - _lastIdleCollectionAllocated < MinimumIdleAllocation)
            return false;
        var heap = GC.GetTotalMemory(forceFullCollection: false);
        if ((requireMinimumHeap && heap < MinimumIdleAllocation) || heap > MaximumIdleHeap) return false;
        var model = _viewModel;
        if (model.Gaming.IsEnabled || model.Gaming.IsActive || model.Gaming.IsVisible || model.Gaming.HasCaptureResources ||
            !model.Notes.IsIdleForMemoryCollection || model.ClipRecall.IsEnabled || model.ClipRecall.IsBusy ||
            model.ClipRecall.IsVisible || model.ClipRecall.HasMaintenanceTimer || model.ClipRecall.Repository.IsReady ||
            !model.ClipRecall.Ready.IsCompletedSuccessfully || !model.ClipRecall.PendingSettings.IsCompletedSuccessfully ||
            !model.ClipRecall.PendingAppearanceSave.IsCompletedSuccessfully || !model.FilePack.IsIdleForMemoryCollection ||
            model.IsRefreshing || !model.CurrentProbeTask.IsCompleted || !model.CurrentLocalStatusTask.IsCompleted ||
            !model.CanEditStartup || !model.PendingStartupChange.IsCompletedSuccessfully)
            return false;
        return LensShortcuts is { IsRegistered: false, HasMessageHook: false, IsReleasePolling: false } &&
            Notes is { NoteWindow: null } notes && notes.Ready.IsCompletedSuccessfully && notes.Pending.IsCompletedSuccessfully &&
            ClipRecall is { Panel: null, IsOpeningPanel: false, IsListening: false, HasCaptureProcessor: false, IsHotkeyRegistered: false } clipboard &&
            clipboard.Ready.IsCompletedSuccessfully && clipboard.Pending.IsCompletedSuccessfully &&
            FilePack is { QuickWindow: null, PackWindow: null, IsHotkeyRegistered: false } pack && pack.Ready.IsCompletedSuccessfully;
    }

    private void CancelIdleMemoryReclaim()
    {
        _idleMemoryCancellation?.Cancel();
        _idleMemoryCancellation = null;
        _idleMemoryOperation?.Abort();
        _idleMemoryOperation = null;
    }
}

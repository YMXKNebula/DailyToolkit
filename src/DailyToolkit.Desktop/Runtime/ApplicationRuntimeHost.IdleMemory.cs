using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace DailyToolkit.Desktop.Runtime;

internal sealed partial class ApplicationRuntimeHost
{
    private const long MinimumIdleAllocation = 8 * 1024 * 1024, MaximumIdleHeap = 64 * 1024 * 1024;
    private CancellationTokenSource? _idleMemoryCancellation, _idlePrerequisiteCancellation;
    private DispatcherOperation? _idleMemoryOperation;
    private long _hiddenSince, _hiddenGeneration, _lastIdleCollectionAt, _lastIdleCollectionAllocated;
    private bool _retiringHiddenWindow;
    // DispatcherTimer uses coarse Environment.TickCount, while eligibility uses
    // Stopwatch. A small scheduling margin preserves the full hide interval.
    private TimeSpan IdleTimerInterval => _idleDelay + TimeSpan.FromMilliseconds(25);

    internal int IdleMemoryCollectionCount { get; private set; }
    internal double LastIdleMemoryCollectionMilliseconds { get; private set; }
    internal double LastIdleUiRetirementMilliseconds { get; private set; }
    internal double LastIdleAttemptHiddenMilliseconds { get; private set; }
    internal bool HasPendingIdleMemoryReclaim => _idleTimer.IsEnabled || _idleMemoryCancellation is not null ||
        _idlePrerequisiteCancellation is not null ||
        _idleMemoryOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing;

    private bool AllToolsDisabled => !_viewModel.Gaming.IsEnabled && !_viewModel.Notes.IsEnabled &&
        !_viewModel.ClipRecall.IsEnabled && !_viewModel.FilePack.IsEnabled;

    private void ObserveIdleMemoryChanges(bool subscribe)
    {
        if (subscribe)
        {
            _viewModel.PropertyChanged += IdleMemoryStateChanged;
            _viewModel.Gaming.PropertyChanged += IdleMemoryStateChanged;
            _viewModel.Notes.PropertyChanged += IdleMemoryStateChanged;
            _viewModel.Notes.IdleStateChanged += ScheduleIdleMemoryAttempt;
            _viewModel.ClipRecall.PropertyChanged += IdleMemoryStateChanged;
            _viewModel.FilePack.PropertyChanged += IdleMemoryStateChanged;
        }
        else
        {
            _viewModel.PropertyChanged -= IdleMemoryStateChanged;
            _viewModel.Gaming.PropertyChanged -= IdleMemoryStateChanged;
            _viewModel.Notes.PropertyChanged -= IdleMemoryStateChanged;
            _viewModel.Notes.IdleStateChanged -= ScheduleIdleMemoryAttempt;
            _viewModel.ClipRecall.PropertyChanged -= IdleMemoryStateChanged;
            _viewModel.FilePack.PropertyChanged -= IdleMemoryStateChanged;
        }
    }

    private void IdleMemoryStateChanged(object? sender, PropertyChangedEventArgs args) => ScheduleIdleMemoryAttempt();

    private void ScheduleIdleMemoryAttempt()
    {
        if (_disposed || Notes is null || !AllToolsDisabled || _retiringHiddenWindow ||
            Window is not { IsVisible: false } || _hiddenSince == 0 ||
            _idleTimer.IsEnabled || _idleMemoryCancellation is not null) return;
        // A real state change or completion can start one attempt. A veto never
        // restarts this timer by itself, so an idle process does not poll or GC.
        var remaining = _idleDelay - Stopwatch.GetElapsedTime(_hiddenSince);
        _idleTimer.Interval = (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) + TimeSpan.FromMilliseconds(25);
        _idleTimer.Start();
    }

    internal bool TryQueueIdleMemoryReclaim()
    {
        _dispatcher.VerifyAccess();
        _idleTimer.Stop();
        LastIdleAttemptHiddenMilliseconds = _hiddenSince == 0 ? 0 : Stopwatch.GetElapsedTime(_hiddenSince).TotalMilliseconds;
        var current = Window;
        if (_idleMemoryCancellation is not null || current is null) return false;
        if (!CanReleaseHiddenUi(current, _hiddenGeneration))
        {
            ObservePendingIdlePrerequisites(current);
            return false;
        }

        var watch = Stopwatch.StartNew();
        // UI lifetime does not depend on heap size, allocation totals or GC
        // cooldown. A larger heap must not keep the hidden HWND alive.
        if (current.CreatedSettingsViewCount > 0 && !current.TryReleaseDeferredViewsForIdle()) return false;
        var cleanup = current.DeferredAutomationCleanup;
        if (!cleanup.IsCompletedSuccessfully)
        {
            LastIdleUiRetirementMilliseconds = watch.Elapsed.TotalMilliseconds;
            ObservePendingIdlePrerequisites(current);
            return true;
        }
        if (!cleanup.Result.All(success => success)) return false;
        var cancellation = _idleMemoryCancellation = new();
        bool released;
        _retiringHiddenWindow = true;
        try { released = ReleaseHiddenWindowCore(current); }
        finally { _retiringHiddenWindow = false; }
        if (!released)
        {
            if (ReferenceEquals(_idleMemoryCancellation, cancellation)) _idleMemoryCancellation = null;
            cancellation.Dispose();
            ObservePendingIdlePrerequisites(current);
            return false;
        }
        LastIdleUiRetirementMilliseconds = watch.Elapsed.TotalMilliseconds;
        CancelIdlePrerequisiteWait();
        // Passing a retired Window into the async state machine or callback
        // would itself root the visual tree during the intended collection.
        _ = CollectAfterUiCleanupAsync(cleanup, _hiddenGeneration, cancellation);
        return true;
    }

    private void ObservePendingIdlePrerequisites(MainWindow window)
    {
        if (_disposed || !AllToolsDisabled || _hiddenSince == 0 || _idlePrerequisiteCancellation is not null) return;
        var model = _viewModel;
        var tasks = new Task?[]
        {
            model.CurrentProbeTask, model.CurrentLocalStatusTask, model.PendingStartupChange,
            model.Notes.Ready, model.Notes.PendingColorSave, Notes?.Ready, Notes?.Pending,
            model.ClipRecall.Ready, model.ClipRecall.PendingSettings, model.ClipRecall.PendingAppearanceSave,
            ClipRecall?.Ready, ClipRecall?.Pending, model.FilePack.Ready, model.FilePack.PendingOperations,
            model.FilePack.IdleWork, model.FilePack.Manager.Ready, FilePack?.Ready, FilePack?.PendingDropCleanup,
            window.DeferredAutomationCleanup
        }.Concat(model.FilePack.Appearances.Select(appearance => (Task?)appearance.PendingSave))
            .Where(task => task is { IsCompleted: false }).Cast<Task>().Distinct().ToArray();
        if (tasks.Length == 0) return;
        var cancellation = _idlePrerequisiteCancellation = new();
        _ = RearmAfterPrerequisitesAsync(tasks, _hiddenGeneration, cancellation);
    }

    private async Task RearmAfterPrerequisitesAsync(Task[] tasks, long generation, CancellationTokenSource cancellation)
    {
        var cancelled = false;
        try { await Task.WhenAll(tasks).WaitAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        catch (Exception) { /* Model diagnostics own failures; the eligibility guard still vetoes them. */ }
        finally
        {
            if (ReferenceEquals(_idlePrerequisiteCancellation, cancellation)) _idlePrerequisiteCancellation = null;
            cancellation.Dispose();
        }
        if (!cancelled && generation == _hiddenGeneration) ScheduleIdleMemoryAttempt();
    }

    private async Task CollectAfterUiCleanupAsync(Task<bool[]> cleanup, long generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        DispatcherOperation? operation = null;
        try
        {
            // UIA cleanup may enqueue a bounded retry. Await its actual result;
            // merely crossing one dispatcher idle callback is insufficient.
            var results = await cleanup.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!results.All(success => success)) return;
            operation = _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_idleMemoryOperation, operation)) _idleMemoryOperation = null;
                if (token.IsCancellationRequested || !CanCollectReleasedMemory(generation)) return;
                // UI retirement is already complete. The heap bound limits GC
                // eligibility only, not UI release or the actual pause duration.
                var watch = Stopwatch.StartNew();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                LastIdleMemoryCollectionMilliseconds = watch.Elapsed.TotalMilliseconds;
                IdleMemoryCollectionCount++;
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

    private bool CanReleaseHiddenUi(MainWindow window, long generation) =>
        !_disposed && ReferenceEquals(Window, window) && generation == _hiddenGeneration && _hiddenSince != 0 &&
        Stopwatch.GetElapsedTime(_hiddenSince) >= _idleDelay && window.CanReclaimHiddenUiMemory &&
        !Application.Current.Windows.Cast<Window>().Any(item => item.IsVisible) && ToolsAndWorkAreIdle();

    private bool CanCollectReleasedMemory(long generation)
    {
        if (_disposed || Window is not null || generation != _hiddenGeneration || _hiddenSince == 0 ||
            Application.Current.Windows.Cast<Window>().Any(item => item.IsVisible) ||
            (_lastIdleCollectionAt != 0 && Stopwatch.GetElapsedTime(_lastIdleCollectionAt) < TimeSpan.FromMinutes(1)) ||
            GC.GetTotalAllocatedBytes(precise: false) - _lastIdleCollectionAllocated < MinimumIdleAllocation)
            return false;
        var heap = GC.GetTotalMemory(forceFullCollection: false);
        return heap >= MinimumIdleAllocation && heap <= MaximumIdleHeap && ToolsAndWorkAreIdle();
    }

    private bool ToolsAndWorkAreIdle()
    {
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
            FilePack is { QuickWindow: null, PackWindow: null, IsHotkeyRegistered: false } pack &&
            pack.Ready.IsCompletedSuccessfully && pack.PendingDropCleanup.IsCompletedSuccessfully;
    }

    private void CancelIdlePrerequisiteWait()
    {
        _idlePrerequisiteCancellation?.Cancel();
        _idlePrerequisiteCancellation = null;
    }

    private void CancelIdleMemoryReclaim()
    {
        CancelIdlePrerequisiteWait();
        _idleMemoryCancellation?.Cancel();
        _idleMemoryCancellation = null;
        _idleMemoryOperation?.Abort();
        _idleMemoryOperation = null;
    }
}

namespace DailyToolkit.Desktop.ClipRecall;

public sealed partial class ClipboardHistoryViewModel
{
    internal async Task ReleaseRuntimeAsync()
    {
        if (_disposed || IsEnabled || IsVisible) return;
        _cleanupTimer?.Dispose(); _cleanupTimer = null;
        _searchCancellation?.Cancel(); _selectionCancellation?.Cancel();
        ReleaseMedia();
        var opening = _opening;
        if (opening is not null)
        {
            try { await opening; }
            catch (Exception error) when (ClipboardCaptureProcessor.IsExpectedError(error))
            { Runtime.DiagnosticReportService.Shared.RecordError(Runtime.DiagnosticModule.ClipRecall, error); }
        }
        await Task.WhenAll(_actionTask, _refreshTask, _selectionTask, _imageTask, _maintenanceTask);
        if (_disposed || IsEnabled || IsVisible) return;
        // User edits must be confirmed by the controller before releasing them.
        if (HasNoteChanges || EditorText.Length > 0) return;
        Entries.Clear(); Selected = null; _detail = null; _undo = null;
        _total = _offset = 0; _search = ""; DiscardNoteDrafts();
        await Repository.ReleaseSessionAsync(); _opening = null;
        Notify(nameof(Search)); Notify(nameof(CanUndo)); Notify(nameof(PageText));
    }
}

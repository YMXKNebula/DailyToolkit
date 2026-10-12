using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.ClipRecall;

public partial class ClipboardHistoryPanel : Window
{
    private readonly ClipboardHistoryViewModel _model;
    private bool _closingRuntime, _multi;
    private Guid? _editing;
    private HwndSource? _source;
    public event Action? HideRequested;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal ListBox RecordsList => HistoryList;
    internal TextBox SearchInput => SearchBox;
    public ClipboardHistoryPanel(ClipboardHistoryViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        SourceInitialized += (_, _) => { _source = HwndSource.FromHwnd(Handle); _source?.AddHook(Message); };
        SizeChanged += (_, _) => UpdateChrome();
        StateChanged += (_, _) => _model.IsVisible = IsVisible && WindowState != WindowState.Minimized;
        Closing += (_, e) => { if (!_closingRuntime) { e.Cancel = true; HideRequested?.Invoke(); } };
        Closed += (_, _) => { _source?.RemoveHook(Message); _source = null; };
    }
    internal void ShowNearCursor() => ShowNearPoint(NotesPositionService.Cursor());
    internal void ShowNearPoint((int X, int Y) cursor)
    {
        FocusManager.SetFocusedElement(this, WindowFrame);
        WindowState = WindowState.Normal;
        new WindowInteropHelper(this).EnsureHandle();
        var areas = NotesPositionService.WorkAreas();
        var area = areas.Where(value => cursor.X >= value.X && cursor.X < value.X + value.Width && cursor.Y >= value.Y && cursor.Y < value.Y + value.Height).Select(value => (DailyToolkit.Core.Notes.NotesRect?)value).FirstOrDefault()
            ?? areas.OrderBy(value => Math.Pow(Math.Max(value.X - cursor.X, Math.Max(0, cursor.X - value.X - value.Width)), 2) +
                Math.Pow(Math.Max(value.Y - cursor.Y, Math.Max(0, cursor.Y - value.Y - value.Height)), 2)).First();
        // Move the native HWND to the target monitor first. WPF PerMonitorV2
        // handles WM_DPICHANGED, then GetDpiForWindow reports the target DPI.
        NotesPositionService.Move(Handle, area.X + 8, area.Y + 8);
        var dpi = Math.Max(1, GetDpiForWindow(Handle)) / 96d;
        var availableWidth = Math.Max(1, area.Width / dpi - 16);
        var availableHeight = Math.Max(1, area.Height / dpi - 16);
        MinWidth = Math.Min(520, availableWidth); MinHeight = Math.Min(480, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Show(); UpdateLayout();
        var bounds = NotesPositionService.Bounds(Handle);
        var x = Math.Clamp(cursor.X + 12d, area.X, Math.Max(area.X, area.X + area.Width - bounds.Width));
        var y = Math.Clamp(cursor.Y + 12d, area.Y, Math.Max(area.Y, area.Y + area.Height - bounds.Height));
        NotesPositionService.Move(Handle, x, y);
    }
    private void UpdateChrome()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        // Keep the outer resize band; the remaining frame is a native drag area.
        chrome.CaptionHeight = Math.Max(0, ActualHeight - chrome.ResizeBorderThickness.Top - chrome.ResizeBorderThickness.Bottom);
    }
    internal void ActivatePanel()
    {
        Activate(); FocusManager.SetFocusedElement(this, WindowFrame);
        Keyboard.Focus(WindowFrame);
    }
    internal void CloseRuntime() { _closingRuntime = true; Close(); }
    private IntPtr Message(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsVisible && WindowState != WindowState.Minimized && message is 0x007E or 0x001A)
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && WindowState != WindowState.Minimized) NotesPositionService.Recover(Handle); }));
        return IntPtr.Zero;
    }
    internal bool ConfirmHide()
    {
        if (_model.IsBusy) { _model.Notice = "操作正在保存，请完成后再关闭面板。"; return false; }
        if (ManualEditor.Visibility == Visibility.Visible && _model.EditorText.Length > 0 &&
            MessageBox.Show(this, "手动记录尚未保存。是否放弃本次编辑？", "关闭剪贴回溯", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        if (_model.HasNoteChanges && MessageBox.Show(this, "有未保存的备注。是否放弃这些备注编辑？", "关闭剪贴回溯",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        _model.DiscardNoteDrafts();
        ManualEditor.Visibility = Visibility.Collapsed; _model.EditorText = ""; return true;
    }
    private void ClosePanel(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void MinimizePanel(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ClearSearch(object sender, RoutedEventArgs e) { _model.Search = ""; SearchBox.Focus(); }
    private static T? FindParent<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null) { if (value is T match) return match; value = value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value); }
        return null;
    }
    private void PanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Escape)
        {
            if (TypeFilter.IsDropDownOpen || HistoryList.ContextMenu?.IsOpen == true) return;
            e.Handled = true;
            if (ManualEditor.Visibility == Visibility.Visible) { CancelManual(sender, e); return; }
            if (_multi) { SetMulti(false); return; }
            HideRequested?.Invoke(); return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ManualEditor.Visibility == Visibility.Visible) return;
        if (e.Key is Key.Down or Key.Up && SearchBox.IsKeyboardFocusWithin)
        {
            HistoryList.Focus();
            if (HistoryList.SelectedIndex < 0 && HistoryList.Items.Count > 0) HistoryList.SelectedIndex = 0;
            e.Handled = true; return;
        }
        // Preserve normal editing Delete/Enter in search and note textboxes.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && !NoteEditor.IsKeyboardFocusWithin)
        { e.Handled = true; RestoreRecord(sender, e); }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox)
        { e.Handled = true; DeleteRecords(sender, e); }
    }
    private void SelectRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item && !item.IsSelected)
        { if (!_multi) HistoryList.SelectedItems.Clear(); item.IsSelected = true; }
    }
    private void SelectedChanged(object sender, SelectionChangedEventArgs e)
    { if (SelectionCount is not null) SelectionCount.Text = $"已选 {HistoryList.SelectedItems.Count} 条"; }
    private void RestoreDoubleClick(object sender, MouseButtonEventArgs e)
    { if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) { RestoreRecord(sender, e); e.Handled = true; } }
    private Guid[] SelectedIds() => HistoryList.SelectedItems.Cast<ClipboardHistoryEntry>().Select(entry => entry.Id).ToArray();
    private async void RestoreRecord(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItems.Count > 1) { _model.Notice = "请选择一条记录重新复制。"; return; }
        await _model.RestoreSelectedAsync();
    }
    private async void ToggleSaved(object sender, RoutedEventArgs e)
    { if (_model.Selected is { } entry) await _model.SetFlagsAsync([entry.Id], saved: !entry.IsSaved); }
    private async void TogglePinned(object sender, RoutedEventArgs e)
    { if (_model.Selected is { } entry) await _model.SetFlagsAsync([entry.Id], pinned: !entry.IsPinned); }
    private async void DeleteRecords(object sender, RoutedEventArgs e)
    {
        var selected = HistoryList.SelectedItems.Cast<ClipboardHistoryEntry>().ToArray(); if (selected.Length == 0) return;
        if ((_multi || selected.Length > 1 || selected.Any(entry => entry.IsSaved)) &&
            MessageBox.Show(this, $"删除所选 {selected.Length} 条记录？" + (selected.Any(entry => entry.IsSaved) ? "\n其中包含永久保存记录。" : ""),
                "删除记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _model.DeleteAsync(selected.Select(entry => entry.Id));
    }
    private async void UndoDelete(object sender, RoutedEventArgs e) => await _model.UndoAsync();
    private async void SaveNote(object sender, RoutedEventArgs e) => await _model.SaveNoteAsync();
    private void FocusNote(object sender, RoutedEventArgs e) { PreviewExpander.IsExpanded = true; NoteEditor.Focus(); NoteEditor.SelectAll(); }
    private void ToggleMulti(object sender, RoutedEventArgs e) => SetMulti(!_multi);
    private void SetMulti(bool multi)
    {
        _multi = multi; HistoryList.SelectionMode = multi ? SelectionMode.Extended : SelectionMode.Single;
        MultiButton.Content = multi ? "退出多选" : "多选"; BatchBar.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (!multi) HistoryList.UnselectAll();
    }
    private void ClearSelection(object sender, RoutedEventArgs e) => HistoryList.UnselectAll();
    private async void BatchFlags(object sender, RoutedEventArgs e)
    {
        var ids = SelectedIds(); if (ids.Length == 0) return;
        switch (((FrameworkElement)sender).Tag)
        {
            case "save": await _model.SetFlagsAsync(ids, saved: true); break;
            case "unsave": await _model.SetFlagsAsync(ids, saved: false); break;
            case "pin": await _model.SetFlagsAsync(ids, pinned: true); break;
            case "unpin": await _model.SetFlagsAsync(ids, pinned: false); break;
        }
    }
    private void NewRecord(object sender, RoutedEventArgs e) => ShowEditor("新建记录", "", null);
    private void EditRecord(object sender, RoutedEventArgs e)
    {
        if (!_model.CanEditManual || _model.Selected is null) { _model.Notice = "外部原始内容不可编辑，请使用复制为新记录。"; return; }
        ShowEditor("编辑手动记录", _model.DetailText, _model.Selected.Id);
    }
    private void DuplicateRecord(object sender, RoutedEventArgs e)
    { if (_model.CanDuplicateText) ShowEditor("复制为新记录", _model.DetailText, null); }
    private async void CacheFiles(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItems.Count != 1 || !_model.HasFiles) { _model.Notice = "请选择一条文件记录缓存。"; return; }
        await _model.CacheSelectedFilesAsync();
    }
    private async void RemoveFileCache(object sender, RoutedEventArgs e)
    {
        if (!_model.HasFileCache) return;
        if (MessageBox.Show(this, "移除文件缓存后，原文件删除时将无法重新复制。原路径记录会保留。", "移除文件缓存",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _model.RemoveSelectedFileCacheAsync();
    }
    private void ShowEditor(string title, string text, Guid? editing)
    {
        _editing = editing; EditorTitle.Text = title; _model.EditorText = text;
        ManualEditor.Visibility = Visibility.Visible; ManualText.Focus();
    }
    private async void CommitManual(object sender, RoutedEventArgs e)
    { if (await _model.CreateManualAsync(_editing)) { ManualEditor.Visibility = Visibility.Collapsed; Keyboard.Focus(WindowFrame); } }
    private void CancelManual(object sender, RoutedEventArgs e)
    { _model.EditorText = ""; ManualEditor.Visibility = Visibility.Collapsed; Keyboard.Focus(WindowFrame); }
    private void PreviousPage(object sender, RoutedEventArgs e) => _model.PreviousPage();
    private void NextPage(object sender, RoutedEventArgs e) => _model.NextPage();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}

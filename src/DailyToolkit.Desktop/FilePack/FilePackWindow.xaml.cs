using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Core.Notes;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.FilePack.DropHost;

namespace DailyToolkit.Desktop.FilePack;

internal sealed record FilePackWindowSession(double Width, double Height, Guid[] SelectedEntries, Guid? SelectedTask,
    double EntryVertical, double EntryHorizontal, double TaskVertical, double WorkbenchVertical, bool FolderExpanded,
    (Guid TaskId, string Header)[] ExpandedTaskSections);

public partial class FilePackWindow : Window
{
    private readonly FilePackAppearance _appearance;
    private readonly FilePackViewModel _model;
    private readonly bool _quick;
    private readonly double _minimumWidth, _minimumHeight;
    private HwndSource? _source;
    private bool _closingRuntime;
    private readonly bool _enableExternalDrop;
    private readonly Func<Func<IReadOnlyList<string>, Task>, Action, FilePackDropMessage, FilePackDropBridge> _createDropBridge;
    private FilePackDropBridge? _dropBridge;
    private FilePackDropSurface? _dropSurface;
    private Task _dropStarting = Task.CompletedTask;
    private Task _dropStopping = Task.CompletedTask;
    private Task _dropSuspending = Task.CompletedTask;
    private bool _dropPaused;
    private int _dropGeneration;
    public event Action? HideRequested;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    public FilePackWindow(FilePackViewModel model, bool quick) : this(model, quick, false) { }
    internal FilePackWindow(FilePackViewModel model, bool quick, bool enableExternalDrop,
        Func<Func<IReadOnlyList<string>, Task>, Action, FilePackDropMessage, FilePackDropBridge>? createDropBridge = null)
    {
        InitializeComponent(); DataContext = model; _model = model; _quick = quick;
        _enableExternalDrop = enableExternalDrop && (createDropBridge is not null || FilePackDropProcess.NeedsStandardDropHost);
        _createDropBridge = createDropBridge ?? ((receive, failure, palette) => new(receive, failure, palette));
        _appearance = quick ? model.QuickAppearance : model.PackAppearance;
        Width = quick ? 480 : 1080; Height = quick ? 440 : 780;
        MinWidth = _minimumWidth = quick ? 380 : 680; MinHeight = _minimumHeight = quick ? 320 : 540;
        if (!quick && model.DetailedWindowPlacement is { } saved) { Width = saved.Width; Height = saved.Height; }
        Title = quick ? "文件集装箱 · 快速装载" : "文件集装箱"; WindowTitle.Text = quick ? "—" : Title;
        if (quick)
        {
            var header = (Grid)WindowTitle.Parent;
            header.ColumnDefinitions[0].Width = GridLength.Auto; header.ColumnDefinitions[1].Width = new(1, GridUnitType.Star);
            Grid.SetColumn(MinimizeButton, 0); MinimizeButton.Margin = new(0);
            Grid.SetColumn(WindowTitle, 1); WindowTitle.HorizontalAlignment = HorizontalAlignment.Center; WindowTitle.Margin = new(8, 0, 8, 0);
        }
        Body.Content = quick ? new FilePackQuickView() : new FilePackView();
        if (_enableExternalDrop) SetDropHint(false);
        _appearance.Apply(Resources); _appearance.PropertyChanged += AppearanceChanged;
        _model.PropertyChanged += DropStateChanged;
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(Handle); _source?.AddHook(Message);
        };
        SizeChanged += (_, _) => WindowChrome.GetWindowChrome(this).CaptionHeight = Math.Max(0, ActualHeight - 12);
        Closing += (_, e) => { if (!_closingRuntime) { e.Cancel = true; HideRequested?.Invoke(); } };
        IsVisibleChanged += (_, _) => UpdateDropLifetime();
        StateChanged += (_, _) => UpdateDropLifetime();
        Closed += (_, _) =>
        {
            _appearance.PropertyChanged -= AppearanceChanged; SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
            _model.PropertyChanged -= DropStateChanged; StopDropBridge();
            _source?.RemoveHook(Message); _source = null;
        };
    }
    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(FilePackAppearance.Applied)) { _appearance.Apply(Resources); UpdateDropPalette(); } }
    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(new Action(() => { if (!_closingRuntime) { _appearance.Apply(Resources); UpdateDropPalette(); } })); }
    private void DropStateChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(FilePackViewModel.CanEditDraft) or nameof(FilePackViewModel.IsEnabled)) UpdateDropPalette(); }
    private FilePackDropMessage DropPalette()
    {
        uint Rgb(string key) { var color = ((SolidColorBrush)FindResource(key)).Color; return (uint)(color.R << 16 | color.G << 8 | color.B); }
        return new("palette", Background: Rgb("SurfaceBrush"), Foreground: Rgb("MutedBrush"), Border: Rgb("BorderBrush"),
            Enabled: _model.IsEnabled && _model.CanEditDraft && !_closingRuntime && !_dropPaused);
    }
    private void UpdateDropPalette() => _dropBridge?.UpdatePalette(DropPalette());
    private void SetDropHint(bool active)
    {
        if (((FrameworkElement)Body.Content).FindName("DropHint") is TextBlock hint)
            hint.Text = active ? "拖到窗口下方的拖入区" : "使用上方按钮添加文件";
    }
    private void UpdateDropLifetime()
    {
        if (!_enableExternalDrop) return;
        if (_closingRuntime) { StopDropBridge(); return; }
        if (!IsVisible || WindowState == WindowState.Minimized)
        { if (_dropSuspending.IsCompleted) _dropSuspending = SuspendDropBridgeAsync(); return; }
        if (_dropBridge is null) _dropStarting = StartDropBridgeAsync();
        else if (_dropSuspending.IsCompleted) ResumeDrop();
    }
    private async Task StartDropBridgeAsync()
    {
        var generation = ++_dropGeneration;
        await _dropStopping;
        if (_closingRuntime || generation != _dropGeneration || !IsVisible || WindowState == WindowState.Minimized) return;
        _dropPaused = false;
        var bridge = _createDropBridge(paths => Dispatcher.InvokeAsync(async () =>
        {
            if (!_model.IsEnabled || _model.IsStopping) return;
            await _model.WaitForDraftAsync();
            if (_model.IsEnabled && !_model.IsStopping) await _model.AddPathsAsync(paths);
        }).Task.Unwrap(), () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closingRuntime && generation == _dropGeneration)
            { StopDropBridge(); _model.SetNotice("拖放入口已关闭，请重新打开窗口。"); }
        })), DropPalette());
        _dropBridge = bridge;
        try
        {
            await bridge.StartAsync();
            if (_closingRuntime || generation != _dropGeneration || !IsVisible || WindowState == WindowState.Minimized)
            { await bridge.DisposeAsync(); return; }
            _dropSurface = new(bridge.Handle, bridge.ExpectedProcessId, () => bridge.IsHostAlive);
            DropHostArea.Content = _dropSurface; DropHostArea.Visibility = Visibility.Visible;
            DropHostArea.UpdateLayout();
            SetDropHint(true);
            UpdateDropPalette();
        }
        catch (Exception error) when (FilePackDropWire.IsExpected(error) || error is System.Runtime.InteropServices.COMException)
        {
            if (generation == _dropGeneration)
            {
                _dropSurface?.Dispose(); _dropSurface = null; DropHostArea.Content = null; DropHostArea.Visibility = Visibility.Collapsed;
                _dropBridge = null;
                SetDropHint(false);
                if (!_closingRuntime) _model.SetNotice("拖放入口未能打开，可用文件选择或右键装载。");
            }
            await bridge.DisposeAsync();
        }
    }
    private void StopDropBridge()
    {
        ++_dropGeneration;
        _dropSurface?.Dispose(); _dropSurface = null; DropHostArea.Content = null; DropHostArea.Visibility = Visibility.Collapsed;
        SetDropHint(false);
        var bridge = _dropBridge; _dropBridge = null;
        if (bridge is not null) _dropStopping = Task.WhenAll(_dropStopping, DisposeDropBridgeAsync(bridge));
    }
    private static async Task DisposeDropBridgeAsync(FilePackDropBridge bridge) => await bridge.DisposeAsync();
    private async Task SuspendDropBridgeAsync()
    {
        await _dropStarting;
        var bridge = _dropBridge;
        if (bridge is null) return;
        _dropPaused = true;
        await bridge.FlushAsync();
        if (!ReferenceEquals(bridge, _dropBridge)) return;
        if (!_closingRuntime && IsVisible && WindowState != WindowState.Minimized) ResumeDrop();
        else { StopDropBridge(); await _dropStopping; }
    }
    internal Task DropCleanup => Task.WhenAll(_dropStopping, _dropStarting, _dropSuspending);
    internal void ResumeDrop() { _dropPaused = false; UpdateDropPalette(); }
    internal async Task<bool> FlushDropAsync()
    {
        await _dropStarting;
        _dropPaused = true;
        if (_dropBridge is null || await _dropBridge.FlushAsync()) return true;
        _model.SetNotice("拖入的文件尚未接收完成，请稍后再关闭。"); return false;
    }
    internal void ShowNearCursor() => ShowNearPoint(NotesPositionService.Cursor());
    internal void ShowNearPoint((int X, int Y) point)
    {
        WindowState = WindowState.Normal; new WindowInteropHelper(this).EnsureHandle();
        var areas = NotesPositionService.WorkAreas();
        var saved = _quick ? null : _model.DetailedWindowPlacement;
        var area = NearestArea(saved?.X ?? point.X, saved?.Y ?? point.Y, areas);
        NotesPositionService.Move(Handle, area.X + 8, area.Y + 8);
        var dpi = Math.Max(1, GetDpiForWindow(Handle)) / 96d;
        var width = Math.Max(1, area.Width / dpi - 16); var height = Math.Max(1, area.Height / dpi - 16);
        MinWidth = Math.Min(_minimumWidth, width); MinHeight = Math.Min(_minimumHeight, height);
        Width = Math.Min(Width, width); Height = Math.Min(Height, height);
        Show(); UpdateLayout();
        var bounds = NotesPositionService.Bounds(Handle);
        double left, top;
        if (_quick)
        {
            // Place the title under the pointer without changing the pointer position.
            var title = WindowTitle.PointToScreen(new Point(WindowTitle.ActualWidth / 2, WindowTitle.ActualHeight / 2));
            left = bounds.X + point.X - title.X; top = bounds.Y + point.Y - title.Y;
        }
        else
        {
            left = saved?.X ?? area.X + (area.Width - bounds.Width) / 2;
            top = saved?.Y ?? area.Y + (area.Height - bounds.Height) / 2;
        }
        var position = ClampToArea(bounds with { X = left, Y = top }, area);
        NotesPositionService.Move(Handle, position.X, position.Y);
        Activate(); Keyboard.Focus(WindowFrame);
    }
    internal async Task<bool> SavePlacementAsync()
    {
        if (_quick || Handle == IntPtr.Zero || WindowState != WindowState.Normal) return true;
        try
        {
            var bounds = NotesPositionService.Bounds(Handle);
            return await _model.SaveDetailedWindowPlacementAsync(new(bounds.X, bounds.Y, Width, Height));
        }
        catch (Exception exception) when (FilePackViewModel.IsExpected(exception))
        { _model.SetNotice("窗口位置未能保存，请稍后重试。"); return false; }
    }
    internal static NotesRect ClampToArea(NotesRect bounds, NotesRect area) => bounds with
    {
        X = Math.Clamp(bounds.X, area.X, Math.Max(area.X, area.X + area.Width - bounds.Width)),
        Y = Math.Clamp(bounds.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - bounds.Height))
    };
    private static NotesRect NearestArea(double x, double y, IReadOnlyList<NotesRect> areas) => areas.OrderBy(area =>
        Math.Pow(Math.Max(area.X - x, Math.Max(0, x - area.X - area.Width)), 2) +
        Math.Pow(Math.Max(area.Y - y, Math.Max(0, y - area.Y - area.Height)), 2)).First();
    private async void RecoverPosition()
    {
        if (!IsVisible || _closingRuntime) return;
        try
        {
            var bounds = NotesPositionService.Bounds(Handle);
            var area = NearestArea(bounds.X, bounds.Y, NotesPositionService.WorkAreas());
            var initial = ClampToArea(bounds, area);
            if (initial != bounds) NotesPositionService.Move(Handle, initial.X, initial.Y);
            var dpi = Math.Max(1, GetDpiForWindow(Handle)) / 96d;
            var width = Math.Max(1, area.Width / dpi - 16); var height = Math.Max(1, area.Height / dpi - 16);
            MinWidth = Math.Min(_minimumWidth, width); MinHeight = Math.Min(_minimumHeight, height);
            Width = Math.Min(Width, width); Height = Math.Min(Height, height); UpdateLayout();
            var position = ClampToArea(NotesPositionService.Bounds(Handle) with { X = bounds.X, Y = bounds.Y }, area);
            if (NotesPositionService.Bounds(Handle) != position) NotesPositionService.Move(Handle, position.X, position.Y);
            await SavePlacementAsync();
        }
        catch (Exception exception) when (FilePackViewModel.IsExpected(exception)) { _model.SetNotice("窗口位置未能恢复，请稍后重试。"); }
    }
    private IntPtr Message(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsVisible && message is 0x007E or 0x001A) Dispatcher.BeginInvoke(new Action(RecoverPosition));
        if (message == 0x0232) _ = SavePlacementAsync(); // WM_EXITSIZEMOVE: one save after a move/resize gesture.
        return IntPtr.Zero;
    }
    internal void CloseRuntime() { _ = SavePlacementAsync(); _closingRuntime = true; Close(); }
    internal FilePackWindowSession CaptureSession()
    {
        var view = (FrameworkElement)Body.Content;
        var quickEntries = view.FindName("FilesList") as ListBox;
        var detailEntries = view.FindName("EntriesGrid") as DataGrid;
        var selected = quickEntries?.SelectedItems.OfType<FilePackEntryViewModel>() ??
            detailEntries?.SelectedItems.OfType<FilePackEntryViewModel>() ?? [];
        var entryScroll = FindScroll((FrameworkElement?)quickEntries ?? detailEntries);
        var tasks = view.FindName("TasksList") as ListBox;
        var taskScroll = FindScroll(tasks);
        var outer = view is FilePackView details ? details.Content as ScrollViewer : null;
        var expanders = Descendants(view).OfType<Expander>().ToArray();
        return new(Width, Height, selected.Select(row => row.Id).ToArray(), (tasks?.SelectedItem as FilePackTaskViewModel)?.TaskId,
            entryScroll?.VerticalOffset ?? 0, entryScroll?.HorizontalOffset ?? 0, taskScroll?.VerticalOffset ?? 0,
            outer?.VerticalOffset ?? 0, expanders.Any(value => value.Header as string == "整理包内文件夹" && value.IsExpanded),
            expanders.Where(value => value.IsExpanded && value.DataContext is FilePackTaskViewModel && value.Header is string)
                .Select(value => (((FilePackTaskViewModel)value.DataContext).TaskId, (string)value.Header)).ToArray());
    }
    internal void RestoreSession(FilePackWindowSession? session)
    {
        if (session is null) return;
        var view = (FrameworkElement)Body.Content;
        var quick = view.FindName("FilesList") as ListBox;
        var detail = view.FindName("EntriesGrid") as DataGrid;
        var selected = session.SelectedEntries.ToHashSet();
        if (quick is not null)
            foreach (var row in quick.Items.OfType<FilePackEntryViewModel>().Where(row => selected.Contains(row.Id))) quick.SelectedItems.Add(row);
        if (detail is not null)
            foreach (var row in detail.Items.OfType<FilePackEntryViewModel>().Where(row => selected.Contains(row.Id))) detail.SelectedItems.Add(row);
        var tasks = view.FindName("TasksList") as ListBox;
        if (tasks is not null && session.SelectedTask is { } selectedTask)
            tasks.SelectedItem = tasks.Items.OfType<FilePackTaskViewModel>().FirstOrDefault(row => row.TaskId == selectedTask);
        foreach (var expander in Descendants(view).OfType<Expander>())
        {
            if (expander.Header as string == "整理包内文件夹") expander.IsExpanded = session.FolderExpanded;
            else if (expander.DataContext is FilePackTaskViewModel row && expander.Header is string header)
                expander.IsExpanded = session.ExpandedTaskSections.Contains((row.TaskId, header));
        }
        UpdateLayout();
        if (FindScroll((FrameworkElement?)quick ?? detail) is { } entryScroll)
        { entryScroll.ScrollToVerticalOffset(session.EntryVertical); entryScroll.ScrollToHorizontalOffset(session.EntryHorizontal); }
        FindScroll(tasks)?.ScrollToVerticalOffset(session.TaskVertical);
        if (view is FilePackView details && details.Content is ScrollViewer outer) outer.ScrollToVerticalOffset(session.WorkbenchVertical);
        UpdateLayout();
    }
    private static ScrollViewer? FindScroll(FrameworkElement? root) => root is null ? null : Descendants(root).OfType<ScrollViewer>().FirstOrDefault();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape && !e.Handled) { HideRequested?.Invoke(); e.Handled = true; } }
    private void HideWindow(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}

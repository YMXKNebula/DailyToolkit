using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.FilePack;

internal sealed record FilePackWindowSession(double Width, double Height, Guid[] SelectedEntries, Guid? SelectedTask,
    double EntryVertical, double EntryHorizontal, double TaskVertical, double WorkbenchVertical, bool FolderExpanded,
    (Guid TaskId, string Header)[] ExpandedTaskSections);

public partial class FilePackWindow : Window
{
    private readonly FilePackAppearance _appearance;
    private readonly double _minimumWidth, _minimumHeight;
    private HwndSource? _source;
    private bool _closingRuntime;
    public event Action? HideRequested;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    public FilePackWindow(FilePackViewModel model, bool quick)
    {
        InitializeComponent(); DataContext = model; _appearance = quick ? model.QuickAppearance : model.PackAppearance;
        Width = quick ? 480 : 1080; Height = quick ? 440 : 780;
        MinWidth = _minimumWidth = quick ? 380 : 680; MinHeight = _minimumHeight = quick ? 320 : 540;
        Title = quick ? "文件集装箱 · 快速装载" : "文件集装箱 · 详细打包"; WindowTitle.Text = Title;
        if (quick)
        {
            var header = (Grid)WindowTitle.Parent;
            header.ColumnDefinitions[0].Width = GridLength.Auto; header.ColumnDefinitions[1].Width = new(1, GridUnitType.Star);
            Grid.SetColumn(MinimizeButton, 0); MinimizeButton.Margin = new(0);
            Grid.SetColumn(WindowTitle, 1); WindowTitle.HorizontalAlignment = HorizontalAlignment.Center; WindowTitle.Margin = new(8, 0, 8, 0);
        }
        Body.Content = quick ? new FilePackQuickView() : new FilePackView();
        _appearance.Apply(Resources); _appearance.PropertyChanged += AppearanceChanged;
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        SourceInitialized += (_, _) => { _source = HwndSource.FromHwnd(Handle); _source?.AddHook(Message); };
        SizeChanged += (_, _) => WindowChrome.GetWindowChrome(this).CaptionHeight = Math.Max(0, ActualHeight - 12);
        Closing += (_, e) => { if (!_closingRuntime) { e.Cancel = true; HideRequested?.Invoke(); } };
        Closed += (_, _) => { _appearance.PropertyChanged -= AppearanceChanged; SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged; _source?.RemoveHook(Message); _source = null; };
    }
    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(FilePackAppearance.Applied)) _appearance.Apply(Resources); }
    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(new Action(() => { if (!_closingRuntime) _appearance.Apply(Resources); })); }
    internal void ShowNearCursor() => ShowNearPoint(NotesPositionService.Cursor());
    internal void ShowNearPoint((int X, int Y) point)
    {
        WindowState = WindowState.Normal; new WindowInteropHelper(this).EnsureHandle();
        var areas = NotesPositionService.WorkAreas();
        var area = areas.OrderBy(value => Math.Pow(Math.Max(value.X - point.X, Math.Max(0, point.X - value.X - value.Width)), 2) +
            Math.Pow(Math.Max(value.Y - point.Y, Math.Max(0, point.Y - value.Y - value.Height)), 2)).First();
        NotesPositionService.Move(Handle, area.X + 8, area.Y + 8);
        var dpi = Math.Max(1, GetDpiForWindow(Handle)) / 96d;
        var width = Math.Max(1, area.Width / dpi - 16); var height = Math.Max(1, area.Height / dpi - 16);
        MinWidth = Math.Min(_minimumWidth, width); MinHeight = Math.Min(_minimumHeight, height);
        Width = Math.Min(Width, width); Height = Math.Min(Height, height);
        Show(); UpdateLayout();
        var bounds = NotesPositionService.Bounds(Handle);
        NotesPositionService.Move(Handle, Math.Clamp(point.X + 12d, area.X, Math.Max(area.X, area.X + area.Width - bounds.Width)),
            Math.Clamp(point.Y + 12d, area.Y, Math.Max(area.Y, area.Y + area.Height - bounds.Height)));
        Activate(); Keyboard.Focus(WindowFrame);
    }
    private IntPtr Message(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsVisible && message is 0x007E or 0x001A) Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) NotesPositionService.Recover(Handle); }));
        return IntPtr.Zero;
    }
    internal void CloseRuntime() { _closingRuntime = true; Close(); }
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

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DailyToolkit.Core.Notes;

namespace DailyToolkit.Desktop.Notes;

internal partial class NotesWindow : Window
{
    private readonly NotesViewModel _model;
    private HwndSource? _source;
    private NotesPreferences? _applied;
    private readonly DispatcherTimer _holdTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(200) };
    private Point _pressPoint;
    private Point _dragAnchor;
    private long _pressTime;
    private bool _pressed, _applying, _closing, _composing, _keyboardReleased, _dragging;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal TextBox NoteEditor => Editor;
    public event Action? BeforeMouseActivate;
    public event Action? UnfocusRequested;
    public event Action? HideRequested;

    public NotesWindow(NotesViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        ApplyPreferences();
        SourceInitialized += (_, _) => { _source = HwndSource.FromHwnd(Handle); _source?.AddHook(Message); Place(); };
        _model.PropertyChanged += Changed;
        Activated += (_, _) => QueueEditorFocus();
        Deactivated += (_, _) => { StopPress(); _model.IsFocused = false; };
        IsKeyboardFocusWithinChanged += (_, _) => _model.IsFocused = IsVisible && IsActive && IsKeyboardFocusWithin &&
            !_keyboardReleased && new NotesForeground().Current == Handle;
        LocationChanged += (_, _) => RememberPosition();
        SizeChanged += (_, _) => { if (!_applying && IsVisible) { _model.Update(_model.Preferences with { Width = Width, Height = Height }); RememberPosition(); } };
        PreviewKeyDown += OnNoteKeyDown;
        PreviewMouseDown += OnNoteMouseDown;
        DragHandle.MouseMove += (_, e) => TryDrag(e.GetPosition(DragHandle));
        DragHandle.LostMouseCapture += (_, _) => StopPress();
        PreviewMouseUp += (_, _) => StopPress();
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); TryDrag(Mouse.GetPosition(DragHandle)); };
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(Editor, (_, _) => _composing = false);
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; HideRequested?.Invoke(); } };
        Closed += (_, _) => { StopPress(); _source?.RemoveHook(Message); _model.PropertyChanged -= Changed; };
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (!_applying && (e.PropertyName is "" or nameof(NotesViewModel.Preferences))) ApplyPreferences(); }

    internal static SolidColorBrush Brush(string color, double opacity)
    {
        var parsed = (Color)ColorConverter.ConvertFromString(color); parsed.A = (byte)Math.Round(opacity * 255, MidpointRounding.AwayFromZero);
        var brush = new SolidColorBrush(parsed); brush.Freeze(); return brush;
    }
    private void ApplyPreferences()
    {
        var preferences = _model.Preferences;
        if (_applied == preferences) return;
        _applying = true;
        try
        {
            Topmost = preferences.Topmost; Width = preferences.Width; Height = preferences.Height;
            NoteBackground.Background = Brush(preferences.Background.Color, preferences.Background.Opacity);
            NoteBackground.CornerRadius = new(preferences.Background.Radius);
            NoteBackground.BorderBrush = Brush(preferences.Font.Color, .2);
            Editor.Foreground = Brush(preferences.Font.Color, preferences.Font.Opacity);
            Editor.CaretBrush = Editor.Foreground;
            HideButton.Foreground = Editor.Foreground;
            Editor.FontFamily = new(preferences.Font.Family + ", Microsoft YaHei UI");
            Editor.FontSize = preferences.Font.Size; Editor.FontWeight = preferences.Font.Bold ? FontWeights.Bold : FontWeights.Normal;
            Editor.IsReadOnly = !_model.ContentWritable;
            HandleLabel.Foreground = Brush(preferences.Font.Color, .7);
            DragHandle.Cursor = _model.Movable ? Cursors.SizeAll : Cursors.Hand;
            if (Handle != IntPtr.Zero && (_applied?.X != preferences.X || _applied?.Y != preferences.Y)) Place();
            _applied = preferences;
        }
        finally { _applying = false; }
    }
    private void Place()
    {
        try { NotesPositionService.Place(Handle, _model.Preferences.X, _model.Preferences.Y); }
        catch (Win32Exception) { _model.Notice = "无法调整浮笺位置，请重试重置位置。"; }
    }
    internal void RecoverPosition()
    {
        try { NotesPositionService.Recover(Handle); RememberPosition(); }
        catch (Win32Exception) { _model.Notice = "无法读取显示器布局，请重试重置位置。"; }
    }
    private void RememberPosition()
    {
        if (_applying || !IsVisible || Handle == IntPtr.Zero) return;
        try
        {
            _applying = true;
            var bounds = NotesPositionService.Bounds(Handle);
            _model.Update(_model.Preferences with { X = bounds.X, Y = bounds.Y });
            _applied = _model.Preferences;
        }
        catch (Win32Exception) { _model.Notice = "浮笺位置暂时未能保存。"; }
        finally { _applying = false; }
    }
    internal bool ActivateEditor()
    {
        _keyboardReleased = false;
        if (!Activate()) return false;
        FocusEditor(); QueueEditorFocus(); return true;
    }
    private void QueueEditorFocus() => Dispatcher.BeginInvoke(new Action(() =>
    {
        // Cross-thread foreground activation is asynchronous. Defer once, without
        // reactivating or polling; a user switch/hide/unfocus cancels editor focus.
        if (IsVisible && IsActive && !_keyboardReleased) FocusEditor();
    }), DispatcherPriority.ContextIdle);
    internal void FocusEditor()
    {
        if (!IsActive || new NotesForeground().Current != Handle) { _model.IsFocused = false; return; }
        _keyboardReleased = false; Editor.Focus(); Keyboard.Focus(Editor);
        _model.IsFocused = IsVisible && IsActive && Editor.IsKeyboardFocusWithin;
        if (_model.IsFocused && _model.Notice.StartsWith("Windows 未允许浮笺获得输入焦点", StringComparison.Ordinal)) _model.Notice = "";
    }
    internal void ReleaseKeyboard()
    {
        _keyboardReleased = true;
        FocusManager.SetFocusedElement(this, null); Keyboard.ClearFocus();
        NotesForeground.ReleaseKeyboard(); _model.IsFocused = false;
    }
    internal void CloseRuntime() { _closing = true; Close(); }
    private IntPtr Message(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0216 && _dragging && lParam != IntPtr.Zero) // WM_MOVING: keep the original grip point in DIPs.
        {
            try
            {
                var rect = Marshal.PtrToStructure<MovingRect>(lParam);
                var cursor = NotesPositionService.Cursor(); var dpi = GetDpiForWindow(hwnd);
                var scale = dpi == 0 ? VisualTreeHelper.GetDpi(this).DpiScaleX : dpi / 96d;
                var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
                rect.Left = (int)Math.Round(cursor.X - _dragAnchor.X * scale);
                rect.Top = (int)Math.Round(cursor.Y - _dragAnchor.Y * scale);
                rect.Right = rect.Left + width; rect.Bottom = rect.Top + height;
                Marshal.StructureToPtr(rect, lParam, false); handled = true; return new IntPtr(1);
            }
            catch (Win32Exception) { _model.Notice = "跨屏位置暂时未能调整，请松开后重试。"; }
        }
        if (message == 0x0021) // WM_MOUSEACTIVATE arrives before the user click activates the HWND.
        { BeforeMouseActivate?.Invoke(); _keyboardReleased = false; }
        if (message is 0x007E or 0x001A) Dispatcher.BeginInvoke(RecoverPosition, DispatcherPriority.Background);
        return IntPtr.Zero;
    }
    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
        if (_composing) { _composing = false; return; }
        e.Handled = true; UnfocusRequested?.Invoke();
    }
    private static bool InsideEditor(DependencyObject? item)
    {
        while (item is not null)
        {
            if (item is TextBox || item is ScrollBar || item is ContextMenu || item is Thumb) return true;
            item = item is Visual || item is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }
    private void OnNoteMouseDown(object sender, MouseButtonEventArgs e)
    {
        _keyboardReleased = false;
        if (InsideEditor(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2 && e.ChangedButton is MouseButton.Left or MouseButton.Right)
        { StopPress(); e.Handled = true; UnfocusRequested?.Invoke(); return; }
        if (_keyboardReleased || !_model.IsFocused) FocusEditor();
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || !_model.Movable || !DragHandle.IsMouseOver) return;
        _pressed = true; _pressTime = Stopwatch.GetTimestamp(); _pressPoint = e.GetPosition(DragHandle);
        _dragAnchor = e.GetPosition(this);
        DragHandle.CaptureMouse(); _holdTimer.Start(); e.Handled = true;
    }
    private void TryDrag(Point current)
    {
        if (!_pressed || Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (!NotesPosition.CanStartDrag(_model.Preferences.PositionMode, Stopwatch.GetElapsedTime(_pressTime),
            current.X - _pressPoint.X, current.Y - _pressPoint.Y,
            SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance, 1)) return;
        StopPress();
        try
        {
            _dragging = true; DragMove();
        }
        catch (InvalidOperationException) { } // Button may be released before the native move loop starts.
        catch (Win32Exception) { _model.Notice = "浮笺拖动未完成，请重试。"; }
        finally { _dragging = false; }
        RememberPosition();
    }
    private void StopPress() { _pressed = false; _holdTimer.Stop(); if (DragHandle.IsMouseCaptured) DragHandle.ReleaseMouseCapture(); }
    private void ResizeNote(object sender, DragDeltaEventArgs e)
    { Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth); Height = Math.Clamp(Height + e.VerticalChange, MinHeight, MaxHeight); }
    private void HideNote(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    internal Point DragAnchorInDips => _dragAnchor;
    [StructLayout(LayoutKind.Sequential)] private struct MovingRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}

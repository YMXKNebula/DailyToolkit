using System.ComponentModel;
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
    private readonly NotesDisplayArea _displayArea;
    private HwndSource? _source;
    private NotesPreferences? _applied;
    private Point _pressPoint;
    private Point _dragAnchor;
    private double _resizeWidth, _resizeHeight;
    private bool _pressed, _applying, _closing, _composing, _keyboardReleased, _dragging, _manualDragging;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal TextBox NoteEditor => Editor;
    public event Action? BeforeMouseActivate;
    public event Action? UnfocusRequested;
    public event Action? HideRequested;

    public NotesWindow(NotesViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        _displayArea = new(NoteBackground, Editor);
        ApplyPreferences();
        SourceInitialized += (_, _) => { _source = HwndSource.FromHwnd(Handle); _source?.AddHook(Message); Place(); };
        _model.PropertyChanged += Changed;
        Activated += (_, _) => QueueEditorFocus();
        Deactivated += (_, _) => { StopPress(); _keyboardReleased = true; _model.IsFocused = false; };
        IsKeyboardFocusWithinChanged += (_, _) => _model.IsFocused = IsVisible && IsActive && IsKeyboardFocusWithin &&
            !_keyboardReleased && new NotesForeground().Current == Handle;
        LocationChanged += (_, _) => RememberPosition();
        SizeChanged += (_, _) => { if (!_applying && IsVisible) { _model.Update(_model.Preferences with { Width = Width, Height = Height }); RememberPosition(); } };
        PreviewKeyDown += OnNoteKeyDown;
        PreviewMouseDown += OnNoteMouseDown;
        DragHandle.MouseMove += (_, e) => TryDrag(e.GetPosition(DragHandle));
        DragHandle.LostMouseCapture += (_, _) => StopPress();
        PreviewMouseUp += (_, _) => StopPress();
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(Editor, (_, _) => _composing = false);
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; HideRequested?.Invoke(); } };
        Closed += (_, _) => { StopPress(); _source?.RemoveHook(Message); _model.PropertyChanged -= Changed; };
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName is "" or nameof(NotesViewModel.Preferences)) ApplyPreferences();
        if (e.PropertyName == nameof(NotesViewModel.IsFocused)) ApplyFocusAppearance();
    }

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
            NoteBackground.CornerRadius = new(preferences.Background.Radius);
            ApplyFocusAppearance();
            Editor.FontFamily = new(preferences.Font.Family + ", Microsoft YaHei UI");
            Editor.FontSize = preferences.Font.Size; Editor.FontWeight = preferences.Font.Bold ? FontWeights.Bold : FontWeights.Normal;
            Editor.IsReadOnly = !_model.ContentWritable;
            if (Handle != IntPtr.Zero && (_applied?.X != preferences.X || _applied?.Y != preferences.Y)) Place();
            _applied = preferences;
        }
        finally { _applying = false; }
    }
    private void ApplyFocusAppearance()
    {
        var preferences = _model.Preferences;
        _displayArea.Apply(_model.IsFocused);
        PaintChrome(NoteBackground, EditorFrame, Editor, HideButton, ResizeGrip, MoveIndicator, preferences, _model.IsFocused);
        DragHandle.Cursor = _model.IsFocused && _model.Movable ? Cursors.SizeAll : Cursors.Arrow;
        DragHandle.ToolTip = _model.IsFocused && _model.Movable ? "按住顶部区域即可拖动。" : null;
    }
    internal static void PaintChrome(Border surface, Border editorFrame, TextBox editor, Control hide, Control resize,
        Border moveIndicator, NotesPreferences preferences, bool focused)
    {
        var opaque = focused && preferences.OpaqueWhenFocused;
        var opacity = opaque ? 1 : preferences.Background.Opacity;
        var accent = Brush(preferences.FocusBorderColor, opacity);
        surface.Background = Brush(preferences.Background.Color, opacity);
        surface.CornerRadius = new(preferences.Background.Radius);
        surface.BorderBrush = editorFrame.BorderBrush = focused ? accent : Brushes.Transparent;
        // Scrollbars retain their layout space while becoming transparent and noninteractive.
        editor.Tag = focused;
        surface.Resources["NotesChromeOpacity"] = opacity;
        surface.Resources["ScrollThumbBrush"] = Brush(preferences.FocusBorderColor, 1);
        surface.Resources["ScrollTrackBrush"] = Brush(preferences.FocusBorderColor, .12);
        editor.Foreground = Brush(preferences.Font.Color, opaque ? 1 : preferences.Font.Opacity);
        editor.CaretBrush = editor.Foreground;
        hide.Foreground = resize.Foreground = moveIndicator.Background = accent;
        hide.Background = resize.Background = Brush(preferences.FocusBorderColor, opacity * .12);
        hide.BorderBrush = resize.BorderBrush = Brush(preferences.FocusBorderColor, opacity * .35);
        hide.Visibility = focused ? Visibility.Visible : Visibility.Hidden;
        resize.Visibility = focused && preferences.AllowManualResize ? Visibility.Visible : Visibility.Hidden;
        moveIndicator.Visibility = focused && preferences.PositionMode == NotesPositionMode.Movable ? Visibility.Visible : Visibility.Hidden;
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
        if (_model.IsFocused && (_model.Notice.StartsWith("Windows 未允许浮笺获得输入焦点", StringComparison.Ordinal) ||
            _model.Notice.StartsWith("先前窗口已关闭或 Windows 未允许切换", StringComparison.Ordinal))) _model.Notice = "";
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
        {
            if (!_model.IsFocused)
            {
                if (!_model.FocusOnClick)
                { _keyboardReleased = true; handled = true; return new IntPtr(3); } // MA_NOACTIVATE still permits dragging.
                BeforeMouseActivate?.Invoke(); _keyboardReleased = false;
            }
        }
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
        if (!_model.IsFocused && _model.FocusOnClick) FocusEditor();
        if (!_model.IsFocused && !HideButton.IsMouseOver && !ResizeGrip.IsMouseOver && !DragHandle.IsMouseOver)
        { e.Handled = true; return; } // Disabled mouse focus leaves the current application active.
        if (!_model.IsFocused && DragHandle.IsMouseOver && !_model.Movable) { e.Handled = true; return; }
        if (InsideEditor(e.OriginalSource as DependencyObject)) return;
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || !_model.Movable || !DragHandle.IsMouseOver) return;
        _pressed = true; _pressPoint = e.GetPosition(DragHandle);
        _dragAnchor = e.GetPosition(this);
        DragHandle.CaptureMouse(); e.Handled = true;
    }
    private void TryDrag(Point current)
    {
        if (!_pressed || Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (!_manualDragging && !NotesPosition.CanStartDrag(_model.Preferences.PositionMode,
            current.X - _pressPoint.X, current.Y - _pressPoint.Y,
            SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance, 1)) return;
        if (!_model.IsFocused)
        {
            // The native system move loop requires activation. An unfocused note
            // follows captured mouse events using NOACTIVATE instead.
            _manualDragging = true;
            try
            {
                var cursor = NotesPositionService.Cursor();
                var dpi = GetDpiForWindow(Handle);
                var scale = dpi == 0 ? VisualTreeHelper.GetDpi(this).DpiScaleX : dpi / 96d;
                NotesPositionService.Move(Handle, cursor.X - _dragAnchor.X * scale, cursor.Y - _dragAnchor.Y * scale);
                var nextDpi = GetDpiForWindow(Handle);
                if (nextDpi != 0 && nextDpi != dpi)
                    NotesPositionService.Move(Handle, cursor.X - _dragAnchor.X * nextDpi / 96d, cursor.Y - _dragAnchor.Y * nextDpi / 96d);
                RememberPosition();
            }
            catch (Win32Exception) { StopPress(); _model.Notice = "浮笺拖动未完成，请重试。"; }
            return;
        }
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
    private void StopPress() { _pressed = _manualDragging = false; if (DragHandle.IsMouseCaptured) DragHandle.ReleaseMouseCapture(); }
    private void BeginResize(object sender, DragStartedEventArgs e) { _resizeWidth = Width; _resizeHeight = Height; }
    private void ResizeNote(object sender, DragDeltaEventArgs e)
    {
        if (!_model.IsFocused || !_model.AllowManualResize) return;
        _resizeWidth = Math.Clamp(_resizeWidth + e.HorizontalChange, MinWidth, MaxWidth);
        _resizeHeight = Math.Clamp(_resizeHeight + e.VerticalChange, MinHeight, MaxHeight);
        Width = Math.Round(_resizeWidth, MidpointRounding.AwayFromZero);
        Height = Math.Round(_resizeHeight, MidpointRounding.AwayFromZero);
    }
    private void HideNote(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    internal Point DragAnchorInDips => _dragAnchor;
    [StructLayout(LayoutKind.Sequential)] private struct MovingRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}

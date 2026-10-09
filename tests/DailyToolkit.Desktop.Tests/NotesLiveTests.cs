using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DailyToolkit.Core.Notes;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct NotesMouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NotesPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool ReadNotesCursor(out NotesPoint point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")] private static extern bool MoveNotesCursor(int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short NotesKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern IntPtr NotesWindowAtPoint(NotesPoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr NotesExtendedStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool NotesFixtureStack(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private static void NotesMouse(uint flags, int delta = 0)
    {
        NotesTestInput[] input = [new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, Data = unchecked((uint)delta) } } }];
        Require(SendNotesInput(1, input, Marshal.SizeOf<NotesTestInput>()) == 1, "Own-window test mouse input was rejected");
    }
    private static async Task CheckNotesMouseGesturesAsync(NotesViewModel model, NotesController controller, NotesWindow window, Window companion)
    {
        Require(NotesKeyState(1) >= 0 && NotesKeyState(2) >= 0, "Release mouse buttons before notes mouse checks");
        ReadNotesCursor(out var originalCursor);
        var originalText = model.Text; var originalClickFocus = model.FocusOnClick;
        var native = new NotesForeground(); var companionHandle = new WindowInteropHelper(companion).Handle;
        var clicks = 0; var rights = 0; var middles = 0; var wheels = 0;
        HwndSource? backdropSource = null;
        IntPtr ObserveBackdrop(IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (message == 0x201) clicks++;
            if (message == 0x204) rights++;
            if (message == 0x207) middles++;
            if (message == 0x20A) wheels++;
            return IntPtr.Zero;
        }
        try
        {
            model.FocusOnClick = false;
            companion.Show(); companionHandle = new WindowInteropHelper(companion).Handle;
            companion.WindowStyle = WindowStyle.None; companion.ResizeMode = ResizeMode.NoResize;
            companion.Content = new System.Windows.Controls.Border { Background = Brushes.LightGray };
            companion.Width = window.Width + 100; companion.Height = window.Height + 100;
            var bounds = NotesPositionService.Bounds(window.Handle);
            NotesPositionService.Place(companionHandle, bounds.X - 20, bounds.Y - 20);
            backdropSource = HwndSource.FromHwnd(companionHandle); backdropSource?.AddHook(ObserveBackdrop);
            window.NoteEditor.Text = "alpha bravo\n中文鼠标操作测试"; window.UpdateLayout();
            var character = window.NoteEditor.GetRectFromCharacterIndex(1);
            var body = window.NoteEditor.PointToScreen(new Point(character.X + 2, character.Y + character.Height / 2));
            var background = window.PointToScreen(new Point(5, 50));
            var grip = ((FrameworkElement)window.FindName("DragHandle")).PointToScreen(new Point(70, 12));
            async Task Click(Point point, bool twice = false, bool right = false)
            {
                await Task.Delay((int)GetDoubleClickTime() + 25);
                Require(MoveNotesCursor((int)point.X, (int)point.Y), "Could not position own-window test cursor");
                var down = right ? 8u : 2u; var up = right ? 16u : 4u;
                NotesMouse(down); NotesMouse(up);
                if (twice) { await Task.Delay(60); NotesMouse(down); NotesMouse(up); }
                await Task.Delay(100);
            }
            Require(companion.Activate(), "Mouse fixture could not obtain foreground"); await Task.Delay(70);
            var inactiveOffset = window.NoteEditor.VerticalOffset;
            var inactiveSelection = (window.NoteEditor.SelectionStart, window.NoteEditor.SelectionLength);
            foreach (var point in new[] { body, background, grip })
            {
                await Click(point); await Click(point, twice: true);
                Require(!model.IsFocused && !window.NoteEditor.IsKeyboardFocusWithin && native.Current == companionHandle,
                    $"Mouse click/double click altered focus: point={point}, focused={model.IsFocused}, keyboard={window.NoteEditor.IsKeyboardFocusWithin}, foreground={native.Current}, expected={companionHandle}, note={window.Handle}");
            }
            Require(clicks == 9 && window.NoteEditor.VerticalOffset == inactiveOffset &&
                (window.NoteEditor.SelectionStart, window.NoteEditor.SelectionLength) == inactiveSelection,
                $"Inactive note blocked underlying left clicks or changed text selection: received={clicks}");
            async Task CheckOtherButtons()
            {
                var oldRights = rights; var oldMiddles = middles; var oldWheels = wheels;
                var offset = window.NoteEditor.VerticalOffset;
                await Click(body, right: true);
                NotesMouse(32); NotesMouse(64); NotesMouse(0x800, -120); await Task.Delay(100);
                Require(!model.IsFocused && native.Current == companionHandle && rights == oldRights + 1 &&
                    middles == oldMiddles + 1 && wheels == oldWheels + 1 && window.NoteEditor.VerticalOffset == offset,
                    $"Inactive right/middle/wheel failed to reach backdrop: right={rights-oldRights}, middle={middles-oldMiddles}, wheel={wheels-oldWheels}, focused={model.IsFocused}");
            }
            await CheckOtherButtons();
            model.SetShortcut(true, new(7, 0x83, "Ctrl + Alt + Shift + F20"), null);
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            Require(model.IsFocused && controller.RestoreTarget == companionHandle, "Explicit keyboard focus failed to capture the current app");
            await Click(body, twice: true);
            Require(model.IsFocused && window.NoteEditor.SelectionLength > 0,
                $"Body double click did not retain ordinary word selection: focused={model.IsFocused}, selection={window.NoteEditor.SelectionStart}:{window.NoteEditor.SelectionLength}, body={body}, editor={window.NoteEditor.ActualWidth}x{window.NoteEditor.ActualHeight}, text={window.NoteEditor.Text}");
            await Click(grip, twice: true); await Click(grip, twice: true, right: true);
            Require(model.IsFocused, "Top double click still canceled focus");
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            Require(!model.IsFocused && native.Current == companionHandle, "Focus shortcut did not release focus and restore the previous app");
            model.FocusOnClick = true;
            await CheckOtherButtons();
            foreach (var point in new[] { body, background, grip })
            {
                await Click(point);
                Require(model.IsFocused && window.NoteEditor.IsKeyboardFocusWithin && native.Current == window.Handle && controller.RestoreTarget == companionHandle,
                    "Enabled mouse focus did not focus the editor and capture the previously active application");
                controller.Unfocus(); await Task.Delay(100);
                Require(!model.IsFocused && native.Current == companionHandle, "Mouse focus lost its restoration target");
            }
            Require(clicks == 9, "Click-to-focus also delivered activation clicks to the underlying program");
            model.FocusOnClick = false; await Click(body);
            Require(!model.IsFocused && native.Current == companionHandle, "Disabling click focus did not immediately restore keyboard-only activation");
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            Require(model.IsFocused, "Same focus shortcut did not focus again");
            var hide = (FrameworkElement)window.FindName("HideButton"); var resize = (FrameworkElement)window.FindName("ResizeGrip");
            var hidePoint = hide.TranslatePoint(new(), window); var resizePoint = resize.TranslatePoint(new(), window);
            Require(hidePoint.Y == resizePoint.Y && hide.ActualWidth == resize.ActualWidth && hide.ActualHeight == resize.ActualHeight &&
                hidePoint.X <= 8 && window.Width - resizePoint.X - resize.ActualWidth <= 8 &&
                window.Height - hidePoint.Y - hide.ActualHeight <= 8 && hide.IsVisible && resize.IsVisible,
                "Hide and resize controls were not symmetric bottom controls");
            await Click(hide.PointToScreen(new Point(14, 14))); await controller.Pending;
            Require(!model.IsVisible && native.Current == companionHandle, "Bottom hide control did not save, hide and restore focus");
            controller.Toggle(); await controller.Pending; await Task.Delay(100);
            Console.WriteLine("PASS Notes native left/right/middle/wheel passthrough, optional left-only focus, previous-app restoration, word selection and corner controls");
        }
        finally
        {
            backdropSource?.RemoveHook(ObserveBackdrop);
            NotesMouse(4); MoveNotesCursor(originalCursor.X, originalCursor.Y);
            model.Text = originalText; model.FocusOnClick = originalClickFocus;
        }
    }
    private static async Task CheckLiveNotesAsync(string output)
    {
        Require(NotesKeyState(1) >= 0 && NotesKeyState(2) >= 0, "Release mouse buttons before the explicit live notes check");
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-notes-live-");
        ReadNotesCursor(out var originalCursor); var native = new NotesForeground(); var originalWindow = native.Current;
        using var source = new HwndSource(new HwndSourceParameters("Notes live input check")
            { PositionX = -20000, PositionY = -20000, Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        using var model = new NotesViewModel(new NotesStore(directory.FullName));
        using var controller = new NotesController(model, source, () => null);
        var companion = new Window { Title = "DailyToolkit notes input fixture", Width = 240, Height = 100,
            ShowInTaskbar = false, Left = 700, Top = 60 };
        var areas = NotesPositionService.WorkAreas(); var geometry = new List<object>();
        try
        {
            await controller.Ready; model.SetShortcut(false, new(3, 0x83, "Ctrl + Alt + F20"), null);
            PressNotesTestHotkey(); await Task.Delay(150); await controller.Pending;
            var window = controller.NoteWindow!; Require(model.IsFocused, "Live note did not obtain focus through its registered hotkey");
            window.NoteEditor.Text = "alpha bravo\n中文编辑与跨屏测试";
            window.UpdateLayout(); await Task.Delay(80);
            await CheckNotesMouseGesturesAsync(model, controller, window, companion);
            await CheckNotesCrossProcessAsync(model, controller, window, directory.FullName);
            companion.Activate(); await Task.Delay(80);
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            var handle = (System.Windows.Controls.Border)window.FindName("DragHandle");
            var grip = handle.PointToScreen(new Point(70, 12));
            var immediateDrags = new List<double>();
            async Task DragTo(Point destination, bool movable, bool movement = true)
            {
                model.Movable = movable; var start = handle.PointToScreen(new Point(70, 12));
                var before = NotesPositionService.Bounds(window.Handle); var noteHandle = window.Handle;
                Console.WriteLine($"Native note drag: movable={movable}, start={start}, destination={destination}, before={before}, focus={model.IsFocused}");
                Require(MoveNotesCursor((int)start.X, (int)start.Y), "Could not position test drag");
                var pressedAt = Stopwatch.GetTimestamp();
                NotesMouse(2);
                try
                {
                    // The OS move loop runs on the dispatcher; input continues on the worker.
                    await Task.Run(async () =>
                    {
                        await Task.Delay(20);
                        for (var i = 1; i <= 35; i++)
                        {
                            Require(MoveNotesCursor((int)Math.Round(start.X + (destination.X - start.X) * i / 35),
                                (int)Math.Round(start.Y + (destination.Y - start.Y) * i / 35)), "Test drag failed");
                            await Task.Delay(18);
                            if (movable && movement && i == 5)
                            {
                                var elapsed = Stopwatch.GetElapsedTime(pressedAt).TotalMilliseconds;
                                Require(elapsed < 200 && NotesPositionService.Bounds(noteHandle) != before,
                                    $"Top drag did not start before the old hold delay: elapsed={elapsed}, bounds={NotesPositionService.Bounds(noteHandle)}, start={start}, destination={destination}");
                                immediateDrags.Add(elapsed);
                            }
                        }
                        NotesMouse(4);
                    });
                }
                finally { NotesMouse(4); }
                await Task.Delay(120);
            }
            var fixedBounds = NotesPositionService.Bounds(window.Handle);
            await DragTo(new(grip.X + 80, grip.Y + 35), false);
            Require(NotesPositionService.Bounds(window.Handle) == fixedBounds, "Fixed note moved under ordinary mouse drag");
            foreach (var area in areas)
            {
                await DragTo(new(area.X + area.Width / 2, area.Y + 130), true);
                var bounds = NotesPositionService.Bounds(window.Handle); var dpi = VisualTreeHelper.GetDpi(window);
                ReadNotesCursor(out var cursor);
                Require(bounds.Intersects(area) && Math.Abs(bounds.Width - model.Width * dpi.DpiScaleX) <= 2,
                    $"Cross-monitor drag lost its target or DPI-scaled dimensions: bounds={bounds}, target={area}, dpi={dpi.DpiScaleX}, width={model.Width}, height={model.Height}");
                Require(Math.Abs(model.Preferences.X!.Value - bounds.X) <= 1 && Math.Abs(model.Preferences.Y!.Value - bounds.Y) <= 1,
                    "Dragged physical coordinates were not saved");
                var anchorErrorX = cursor.X - bounds.X - window.DragAnchorInDips.X * dpi.DpiScaleX;
                var anchorErrorY = cursor.Y - bounds.Y - window.DragAnchorInDips.Y * dpi.DpiScaleY;
                Require(Math.Abs(anchorErrorX) <= 3 && Math.Abs(anchorErrorY) <= 3,
                    $"DPI crossing moved the grip away from the pointer: {anchorErrorX}, {anchorErrorY}");
                geometry.Add(new { bounds, dpi.DpiScaleX, dpi.DpiScaleY, DipWidth = model.Width, DipHeight = model.Height, anchorErrorX, anchorErrorY });
            }
            // Inactive notes must not move or block the underlying application.
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            Require(!model.IsFocused, "Resize/drag fixture did not unfocus");
            var passiveBounds = NotesPositionService.Bounds(window.Handle);
            companion.Width = window.Width + 200; companion.Height = window.Height + 200;
            NotesPositionService.Place(new WindowInteropHelper(companion).Handle, passiveBounds.X - 30, passiveBounds.Y - 30);
            await Task.Delay(100);
            NotesPositionService.Place(new WindowInteropHelper(companion).Handle, passiveBounds.X - 30, passiveBounds.Y - 30);
            companion.Topmost = true;
            Require(NotesFixtureStack(window.Handle, new(-1), 0, 0, 0, 0, 0x13), "Could not stack the inactive note above its test backdrop");
            companion.Activate(); await Task.Delay(80);
            var previousApp = native.Current;
            var unfocusedGrip = handle.PointToScreen(new Point(70, 12));
            await DragTo(new(unfocusedGrip.X + 65, unfocusedGrip.Y + 35), true, movement: false);
            Require(!model.IsFocused && !window.NoteEditor.IsKeyboardFocusWithin && native.Current == previousApp &&
                NotesPositionService.Bounds(window.Handle) == passiveBounds,
                "Dragging an unfocused note moved it or changed the foreground app");
            var resizeGrip = (FrameworkElement)window.FindName("ResizeGrip");
            async Task Resize(bool enabled, bool focused)
            {
                model.AllowManualResize = enabled; window.UpdateLayout();
                Require(model.IsFocused == focused && resizeGrip.IsVisible == (enabled && focused), "Resize visibility ignored focus or manual-size settings");
                var before = new Size(window.Width, window.Height);
                var start = focused ? resizeGrip.PointToScreen(new Point(14, 14)) :
                    window.PointToScreen(new Point(window.ActualWidth - 20, window.ActualHeight - 20));
                if (!focused) Require(NotesWindowAtPoint(new() { X = (int)start.X, Y = (int)start.Y }) == previousApp,
                    $"Inactive resize fixture did not cover the cursor: start={start}, hit={NotesWindowAtPoint(new() { X = (int)start.X, Y = (int)start.Y })}, backdropHandle={previousApp}, noteHandle={window.Handle}, style={NotesExtendedStyle(window.Handle,-20).ToInt64():X}, backdrop={NotesPositionService.Bounds(previousApp)}, note={NotesPositionService.Bounds(window.Handle)}");
                Require(MoveNotesCursor((int)start.X, (int)start.Y), "Could not position resize cursor");
                NotesMouse(2);
                try
                {
                    for (var i = 1; i <= 12; i++)
                    {
                        MoveNotesCursor((int)start.X + i * 3, (int)start.Y + i * 2); await Task.Delay(20);
                    }
                }
                finally { NotesMouse(4); }
                await Task.Delay(100);
                Require(model.Width == Math.Round(model.Width) && model.Height == Math.Round(model.Height) &&
                    window.Width == model.Width && window.Height == model.Height, "Native resizing produced fractional dimensions");
                Require(enabled && focused ? window.Width > before.Width && window.Height > before.Height : new Size(window.Width, window.Height) == before,
                    "Manual resize setting did not allow/block resize");
                Require(model.IsFocused == focused && native.Current == (focused ? window.Handle : previousApp),
                    $"Resizing changed the expected foreground state: focused={model.IsFocused}, expectedFocused={focused}, foreground={native.Current}, previous={previousApp}, note={window.Handle}, start={start}");
            }
            await Resize(true, false);
            companion.Topmost = false;
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            model.Movable = false; await Resize(true, true); await Resize(false, true);
            model.Movable = true; await Resize(true, true);
            model.AllowManualResize = true;
            Require(await controller.PrepareExitAsync(), "Live notes data did not flush");
            await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(new
            { Monitors = areas, Geometry = geometry, WordSelection = true, ManualFocusCapture = true,
                CrossProcessMousePassthrough = true, LeftOnlyFocus = true,
                KeyboardFocusToggle = true, FixedDragBlocked = true, OptionalClickFocus = true, BottomControlsSymmetric = true,
                IntegerResize = true, FixedResizeEnabled = true, DisabledResizeBlocked = true, UnfocusedResizeBlocked = true, UnfocusedDragBlocked = true,
                ImmediateDragMilliseconds = immediateDrags }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS Live notes native body/grip clicks, manual focus capture, fixed drag and movable drag on {areas.Count} actual monitors");
        }
        finally
        {
            NotesMouse(4); controller.Dispose(); companion.Close(); model.Dispose();
            MoveNotesCursor(originalCursor.X, originalCursor.Y);
            if (native.IsValid(originalWindow)) native.Activate(originalWindow);
            directory.Delete(true);
        }
    }
}

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
    private static void NotesMouse(uint flags)
    {
        NotesTestInput[] input = [new() { Type = 0, Data = new() { Mouse = new() { Flags = flags } } }];
        Require(SendNotesInput(1, input, Marshal.SizeOf<NotesTestInput>()) == 1, "Own-window test mouse input was rejected");
    }
    private static async Task CheckNotesMouseGesturesAsync(NotesViewModel model, NotesController controller, NotesWindow window, Window companion)
    {
        Require(NotesKeyState(1) >= 0 && NotesKeyState(2) >= 0, "Release mouse buttons before notes mouse checks");
        ReadNotesCursor(out var originalCursor);
        var originalPreferences = model.Preferences; var originalText = model.Text;
        var native = new NotesForeground(); var companionHandle = new WindowInteropHelper(companion).Handle;
        try
        {
            companion.Show(); companionHandle = new WindowInteropHelper(companion).Handle;
            model.DoubleClickUnfocus = true; model.ClickToFocus = true;
            window.NoteEditor.Text = "alpha bravo\n中文鼠标操作测试"; window.UpdateLayout();
            var body = window.NoteEditor.PointToScreen(new Point(14, 16));
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
            await Click(body);
            Require(model.IsFocused && controller.RestoreTarget == companionHandle, "Body click failed to focus and capture the previous app");
            await Click(body, twice: true);
            Require(model.IsVisible && !model.IsFocused && native.Current == companionHandle && !window.NoteEditor.IsKeyboardFocusWithin,
                "Enabled body double-click failed to release keyboard focus and restore the previous app");
            await Click(background); Require(model.IsFocused, "Clicking note padding failed to refocus");
            await Click(background, twice: true);
            Require(!model.IsFocused && native.Current == companionHandle, "Background double-click failed to unfocus");
            await Click(grip); Require(model.IsFocused, "Clicking the top area failed to refocus");
            await Click(grip, twice: true, right: true);
            Require(!model.IsFocused && native.Current == companionHandle, "Right double-click on the top area failed to unfocus");
            model.ClickToFocus = false;
            foreach (var point in new[] { body, background, grip })
            {
                await Click(point);
                Require(!model.IsFocused && !window.NoteEditor.IsKeyboardFocusWithin && native.Current == companionHandle,
                    "Disabled click-to-focus still activated the note or editor");
            }
            controller.Refocus(); await controller.Pending; await Task.Delay(80);
            Require(model.IsFocused, "Disabling mouse focus also disabled explicit refocus");
            model.DoubleClickUnfocus = false;
            await Click(body, twice: true);
            Require(model.IsFocused && window.NoteEditor.SelectionLength > 0, "Disabling double-click unfocus did not restore normal word selection");
            await Click(grip, twice: true);
            Require(model.IsFocused, "Disabled double-click unfocus still released focus on the top area");
            Console.WriteLine("PASS Notes actual body/background/top clicks, anywhere double-click, independent mouse toggles and word selection when disabled");
        }
        finally
        {
            NotesMouse(4); MoveNotesCursor(originalCursor.X, originalCursor.Y);
            model.DoubleClickUnfocus = originalPreferences.DoubleClickUnfocus; model.ClickToFocus = originalPreferences.ClickToFocus;
            model.Text = originalText;
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
            async Task Click(Point point, uint down = 2, uint up = 4, bool twice = false)
            {
                await Task.Delay((int)GetDoubleClickTime() + 25);
                Require(MoveNotesCursor((int)point.X, (int)point.Y), "Could not position the test cursor");
                NotesMouse(down); NotesMouse(up);
                if (twice) { await Task.Delay(60); NotesMouse(down); NotesMouse(up); }
                await Task.Delay(90);
            }
            await CheckNotesMouseGesturesAsync(model, controller, window, companion);
            var handle = (System.Windows.Controls.Border)window.FindName("DragHandle");
            var grip = handle.PointToScreen(new Point(70, 12));
            await Click(grip, twice: true);
            Require(model.IsVisible && !model.IsFocused && native.Current == new WindowInteropHelper(companion).Handle,
                "Left double click on the grip did not restore focus");
            controller.Refocus(); await controller.Pending; await Task.Delay(60);
            await Click(grip, 8, 16, true);
            Require(model.IsVisible && !model.IsFocused, "Right double click on grip did not unfocus");
            controller.Refocus(); await controller.Pending;
            var immediateDrags = new List<double>();
            async Task DragTo(Point destination, bool movable)
            {
                model.Movable = movable; var start = handle.PointToScreen(new Point(70, 12));
                var before = NotesPositionService.Bounds(window.Handle); var noteHandle = window.Handle;
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
                            if (movable && i == 5)
                            {
                                var elapsed = Stopwatch.GetElapsedTime(pressedAt).TotalMilliseconds;
                                Require(elapsed < 200 && NotesPositionService.Bounds(noteHandle) != before,
                                    $"Top drag did not start before the old hold delay: elapsed={elapsed}");
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
                await DragTo(new(area.X + Math.Min(area.Width - 80, 230), area.Y + 130), true);
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
            Require(await controller.PrepareExitAsync(), "Live notes data did not flush");
            await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(new
            { Monitors = areas, Geometry = geometry, WordSelection = true, ManualFocusCapture = true,
                LeftRightDoubleClick = true, FixedDragBlocked = true, AnywhereDoubleClick = true, IndependentMouseSwitches = true,
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

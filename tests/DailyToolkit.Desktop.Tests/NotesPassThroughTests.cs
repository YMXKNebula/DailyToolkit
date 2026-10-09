using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private const int ReadBackdropCount = 0x8001;
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr NotesBackdropMessage(IntPtr hwnd, int message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    private static async Task RunNotesInputBackdropAsync(string readyFile)
    {
        var done = new TaskCompletionSource();
        var counts = new Dictionary<int, int>();
        var window = new Window { Title = "DailyToolkit isolated cross-process input fixture", Width = 1000, Height = 850,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Content = new Border { Background = Brushes.LightGray } };
        window.Closed += (_, _) => done.TrySetResult();
        window.Show();
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle)!;
        source.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (message == ReadBackdropCount) { handled = true; return new(counts.GetValueOrDefault((int)wp)); }
            if (message is 0x201 or 0x204 or 0x207 or 0x20A) counts[message] = counts.GetValueOrDefault(message) + 1;
            return IntPtr.Zero;
        });
        File.WriteAllText(readyFile + ".tmp", JsonSerializer.Serialize(new { Handle = handle.ToInt64() }));
        File.Move(readyFile + ".tmp", readyFile);
        await done.Task;
    }

    private static async Task CheckNotesCrossProcessAsync(NotesViewModel model, NotesController controller, NotesWindow window, string directory)
    {
        var readyFile = Path.Combine(directory, "backdrop-ready.json");
        var originalClickFocus = model.FocusOnClick;
        var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--notes-input-backdrop"); start.ArgumentList.Add(readyFile);
        using var child = Process.Start(start)!;
        IntPtr handle = IntPtr.Zero;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(readyFile) && !child.HasExited; i++) await Task.Delay(50);
            Require(File.Exists(readyFile), "Cross-process input fixture did not start");
            handle = new(JsonDocument.Parse(await File.ReadAllTextAsync(readyFile)).RootElement.GetProperty("Handle").GetInt64());
            GetWindowThreadProcessId(handle, out var processId);
            Require(processId == child.Id, "Backdrop HWND was not owned by the isolated child process");
            var bounds = NotesPositionService.Bounds(window.Handle);
            NotesPositionService.Place(handle, bounds.X - 20, bounds.Y - 20);
            var native = new NotesForeground();
            model.FocusOnClick = false; controller.Unfocus(); await Task.Delay(80);
            window.UpdateLayout();
            var character = window.NoteEditor.GetRectFromCharacterIndex(1);
            var point = window.NoteEditor.PointToScreen(new(character.X + 2, character.Y + character.Height / 2));
            Require(MoveNotesCursor((int)point.X, (int)point.Y), "Could not position cross-process test cursor");
            int Count(int message) => (int)NotesBackdropMessage(handle, ReadBackdropCount, new(message), IntPtr.Zero);
            var left = Count(0x201);
            NotesMouse(2); NotesMouse(4); await Task.Delay(120);
            Require(Count(0x201) == left + 1 && native.Current == handle && !model.IsFocused,
                "Inactive left click did not activate the other process underneath");
            foreach (var clickFocus in new[] { false, true })
            {
                model.FocusOnClick = clickFocus;
                var right = Count(0x204); var middle = Count(0x207); var wheel = Count(0x20A);
                var offset = window.NoteEditor.VerticalOffset; var selection = window.NoteEditor.SelectionStart;
                NotesMouse(8); NotesMouse(16); NotesMouse(32); NotesMouse(64); NotesMouse(0x800, -120); await Task.Delay(120);
                Require(Count(0x204) == right + 1 && Count(0x207) == middle + 1 && Count(0x20A) == wheel + 1 &&
                    native.Current == handle && !model.IsFocused && window.NoteEditor.VerticalOffset == offset && window.NoteEditor.SelectionStart == selection,
                    "Inactive note blocked another process's right/middle/wheel input");
            }
            left = Count(0x201);
            NotesMouse(2); NotesMouse(4); await Task.Delay(150);
            Require(model.IsFocused && native.Current == window.Handle && window.NoteEditor.IsKeyboardFocusWithin &&
                controller.RestoreTarget == handle && Count(0x201) == left,
                $"Left-only focus from another process failed: focused={model.IsFocused}, foreground={native.Current}, note={window.Handle}, previous={controller.RestoreTarget}, expected={handle}");
            controller.Unfocus(); await Task.Delay(100);
            Require(native.Current == handle && !model.IsFocused, "Unfocus did not restore the other process");
            Console.WriteLine("PASS Cross-process left/right/middle/wheel passthrough and left-only focus/restore");
        }
        finally
        {
            model.FocusOnClick = originalClickFocus;
            if (handle != IntPtr.Zero) PostMessage(handle, 0x10, IntPtr.Zero, IntPtr.Zero);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(child.ExitCode == 0, "Input fixture did not exit normally");
        }
    }
}

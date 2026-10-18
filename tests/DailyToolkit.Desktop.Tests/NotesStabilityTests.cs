using System.IO;
using System.Reflection;
using System.Windows.Interop;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckNotesStabilityAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-notes-stability-");
        using var source = new HwndSource(new HwndSourceParameters("Notes stability check")
            { PositionX = -20000, PositionY = -20000, Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        try
        {
            var store = new NotesStore(directory.FullName);
            using var model = new NotesViewModel(store);
            await model.Ready;
            model.Text = "original"; Require(await model.FlushAsync(), "Initial note did not persist");
            model.Text = new string('字', 1_000_000);
            var saving = model.FlushAsync();
            model.Text = "last edit during save 📝"; model.FontSize = 25;
            Require(await saving && await File.ReadAllTextAsync(store.ContentPath) == model.Text,
                "Concurrent edit was lost by the revision-aware save loop");
            Require((await store.LoadAsync()).Preferences.Font.Size == 25, "Settings changed during content save were lost");

            using var controller = new NotesController(model, source, () => null);
            await controller.Ready; controller.Toggle(); await controller.Pending;
            var window = controller.NoteWindow!;
            Require(model.IsVisible, "Notes stability window did not open");
            IntPtr MouseHook(NotesWindow note)
            {
                var mouse = typeof(NotesWindow).GetField("_mouse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(note)!;
                return (IntPtr)typeof(NotesMousePassThrough).GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mouse)!;
            }
            model.FocusOnClick = true; controller.Unfocus(); model.IsFocused = false;
            Require(MouseHook(window) != IntPtr.Zero, "Optional inactive click-focus hook did not install");
            var original = await File.ReadAllBytesAsync(store.ContentPath);
            var backup = await File.ReadAllBytesAsync(store.ContentPath + ".bak");
            using (var locked = new FileStream(store.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                window.NoteEditor.Text = "unsaved last input";
                controller.Toggle(); await controller.Pending;
                Require(model.IsVisible && controller.NoteWindow == window && !window.NoteEditor.IsReadOnly && model.Text == "unsaved last input",
                    "Failed hide discarded the live note or left editing frozen");
                Require(!await controller.PrepareExitAsync() && model.IsVisible && !window.NoteEditor.IsReadOnly,
                    "Failed save allowed exit or left the editor frozen");
                Require((await File.ReadAllBytesAsync(store.ContentPath)).SequenceEqual(original) &&
                    (await File.ReadAllBytesAsync(store.ContentPath + ".bak")).SequenceEqual(backup),
                    "Failed replacement changed the original or backup");
                window.NoteEditor.Text += " after cancelled exit";
                controller.ToggleFocus(); await controller.Pending;
                Require(model.Text.EndsWith("after cancelled exit", StringComparison.Ordinal), "Cancelled exit lost subsequent input");
            }
            var beforeUndo = model.Text;
            window.NoteEditor.Select(window.NoteEditor.Text.Length, 0);
            window.NoteEditor.SelectedText = " undo survives hiding";
            var afterUndo = model.Text;
            Require(window.NoteEditor.CanUndo, "The note editing fixture did not create an undo action");
            window.NoteEditor.Select(3, 4);
            controller.Toggle(); await controller.Pending;
            Require(!model.IsVisible && controller.NoteWindow == window && model.IsContentLoaded &&
                MouseHook(window) == IntPtr.Zero, "Hiding lost an active undo session or retained its optional mouse hook");
            var savedText = await File.ReadAllTextAsync(store.ContentPath);
            controller.Toggle(); await controller.Pending;
            Require(controller.NoteWindow == window && model.Text == savedText &&
                window.NoteEditor.SelectionStart == 3 && window.NoteEditor.SelectionLength == 4,
                "Reopening lost the undo session, note text or selection");
            window.NoteEditor.Undo();
            Require(model.Text == beforeUndo && window.NoteEditor.CanRedo, "Undo stopped working after hide/show");
            controller.Toggle(); await controller.Pending;
            controller.Toggle(); await controller.Pending;
            Require(controller.NoteWindow == window && window.NoteEditor.CanRedo, "Hiding discarded a redo-only session");
            window.NoteEditor.Redo();
            Require(model.Text == afterUndo, "Redo stopped working after hide/show");
            for (var i = 0; i < 4; i++) controller.Toggle();
            await controller.Pending;
            Require(model.IsVisible && controller.NoteWindow is not null && await File.ReadAllTextAsync(store.ContentPath) == model.Text,
                "Queued hide/show operations lost final input or order");
            window = controller.NoteWindow!;
            afterUndo += "\n" + string.Join('\n', Enumerable.Range(1, 70).Select(line => $"saved scroll fixture line {line}"));
            window.NoteEditor.Text = afterUndo;
            window.NoteEditor.IsUndoEnabled = false;
            window.NoteEditor.IsUndoEnabled = true;
            Require(!window.NoteEditor.CanUndo && !window.NoteEditor.CanRedo,
                "Resetting the clean note fixture retained its editing history");
            window.UpdateLayout();
            window.NoteEditor.Select(420, 4);
            window.NoteEditor.ScrollToVerticalOffset(160);
            window.UpdateLayout();
            var scrollOffset = window.NoteEditor.VerticalOffset;
            Require(scrollOffset > 0, "The clean note fixture did not provide a scrolled document");
            var width = window.Width; var height = window.Height;
            controller.Toggle(); await controller.Pending;
            Require(controller.NoteWindow is null && !model.IsContentLoaded && model.Text.Length == 0,
                "A clean hidden note without undo/redo retained its window or document");
            var released = window;
            controller.Toggle(); await controller.Pending;
            window = controller.NoteWindow!;
            Require(window != released && model.Text == afterUndo && window.Width == width && window.Height == height &&
                window.NoteEditor.SelectionStart == 420 && window.NoteEditor.SelectionLength == 4 &&
                Math.Abs(window.NoteEditor.VerticalOffset - scrollOffset) < 2,
                $"Recreating a released clean note lost state: new={window != released}, text={model.Text == afterUndo}, size={window.Width}×{window.Height}/{width}×{height}, selection={window.NoteEditor.SelectionStart}+{window.NoteEditor.SelectionLength}/420+4, scroll={window.NoteEditor.VerticalOffset}/{scrollOffset}, savedScroll={controller.SavedVerticalOffset}, focused={model.IsFocused}");
            Require(await controller.PrepareExitAsync(), "Save did not recover after releasing the file lock");
            Console.WriteLine("PASS Notes edits during save, failed hide/exit, original/backup preservation, cross-hide undo/redo, clean-session release and rapid queue recovery");

            // Loading a damaged primary must leave both generations untouched.
            controller.Unfocus(); model.IsFocused = false;
            Require(MouseHook(window) != IntPtr.Zero, "Visible inactive note did not re-install its optional hook");
            controller.Dispose();
            Require(MouseHook(window) == IntPtr.Zero, "Exit retained the optional mouse hook");
            await File.WriteAllBytesAsync(store.ContentPath, [0xC3, 0x28]);
            await File.WriteAllTextAsync(store.SettingsPath, "{");
            var contentBackup = await File.ReadAllBytesAsync(store.ContentPath + ".bak");
            var settingsBackup = await File.ReadAllBytesAsync(store.SettingsPath + ".bak");
            using var damaged = new NotesViewModel(store);
            await damaged.Ready; damaged.Text = "do not overwrite"; damaged.FontSize = 29;
            Require(!damaged.ContentWritable && !await damaged.FlushAsync() &&
                (await File.ReadAllBytesAsync(store.ContentPath)).SequenceEqual(new byte[] { 0xC3, 0x28 }) &&
                await File.ReadAllTextAsync(store.SettingsPath) == "{" &&
                (await File.ReadAllBytesAsync(store.ContentPath + ".bak")).SequenceEqual(contentBackup) &&
                (await File.ReadAllBytesAsync(store.SettingsPath + ".bak")).SequenceEqual(settingsBackup),
                "Damaged load or attempted save replaced a primary file or its recovery backup");
            Require(!Directory.GetFiles(directory.FullName, "*.tmp").Any(), "Failed saves retained temporary files");
        }
        finally { directory.Delete(true); }
    }
}

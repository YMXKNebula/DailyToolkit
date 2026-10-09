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
            controller.Toggle(); await controller.Pending;
            Require(!model.IsVisible && MouseHook(window) == IntPtr.Zero, "Hiding retained the optional mouse hook");
            controller.Toggle(); await controller.Pending;
            for (var i = 0; i < 4; i++) controller.Toggle();
            await controller.Pending;
            Require(model.IsVisible && controller.NoteWindow == window && await File.ReadAllTextAsync(store.ContentPath) == model.Text,
                "Queued hide/show operations lost final input, order or window reuse");
            Require(await controller.PrepareExitAsync(), "Save did not recover after releasing the file lock");
            Console.WriteLine("PASS Notes edits during save, failed hide/exit, original/backup preservation, cancelled-exit editing and rapid queue recovery");

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

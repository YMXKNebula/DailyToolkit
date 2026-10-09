using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Core.Notes;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private sealed class FakeNotesForeground : INotesForeground
    {
        public IntPtr Current { get; set; }
        public HashSet<IntPtr> Windows { get; } = [];
        public bool Allow { get; set; } = true;
        public int Activations { get; private set; }
        public bool IsValid(IntPtr window) => window != IntPtr.Zero && Windows.Contains(window);
        public bool Activate(IntPtr window) { Activations++; if (!Allow) return false; Current = window; return true; }
    }

    private static async Task CheckNotesPersistenceAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-notes-store-");
        try
        {
            var store = new NotesStore(directory.FullName);
            using (var model = new NotesViewModel(store))
            {
                await model.Ready;
                Require(model.IsLoaded && model.Text == "" && model.Preferences == new NotesPreferences(), "Missing notes did not initialize defaults");
                model.Text = "第一行\n第二行 📝"; model.FontSize = 26; model.BackgroundOpacity = .4;
                for (var attempt = 0; attempt < 30 && !File.Exists(store.SettingsPath); attempt++) await Task.Delay(100);
                Require(File.Exists(store.ContentPath) && await File.ReadAllTextAsync(store.ContentPath) == model.Text,
                    "Debounced autosave did not preserve Unicode multi-line notes without hiding or exiting");
                model.Text += "\n隐藏前最后输入";
                Require(await model.FlushAsync(), "Pending final input did not flush");
                Require(await File.ReadAllTextAsync(store.ContentPath) == model.Text && File.Exists(store.ContentPath + ".bak"), "Atomic replacement lost the final input or backup");
            }
            using (var reopened = new NotesViewModel(store))
            {
                await reopened.Ready;
                Require(reopened.Text.Contains("隐藏前最后输入") && reopened.FontSize == 26 && reopened.BackgroundOpacity == .4,
                    "Text and independent appearance did not survive a new model/version");
                var text = reopened.Text; reopened.ResetSettings();
                Require(await reopened.FlushAsync() && reopened.Text == text && reopened.Preferences == new NotesPreferences(), "Reset settings changed note content");
            }
            await File.WriteAllTextAsync(store.SettingsPath, "{}");
            Require((await store.LoadAsync()).Preferences == new NotesPreferences(), "Older settings missing notes fields failed");
            Require(!Directory.GetFiles(directory.FullName, "*.tmp").Any(), "Atomic saves leaked temporary files");
            Console.WriteLine("PASS Notes debounce, Unicode, atomic backup, final flush, version-independent restart and defaults without text loss");

            await File.WriteAllBytesAsync(store.ContentPath, [0xC3, 0x28]);
            await File.WriteAllTextAsync(store.SettingsPath, "{");
            using (var damaged = new NotesViewModel(store))
            {
                await damaged.Ready; var original = await File.ReadAllBytesAsync(store.ContentPath);
                Require(!damaged.ContentWritable && damaged.Notice.Length > 0, "Unreadable notes were silently treated as empty writable content");
                damaged.Text = "must not overwrite"; damaged.FontSize = 22;
                Require(!await damaged.FlushAsync() && (await File.ReadAllBytesAsync(store.ContentPath)).SequenceEqual(original) &&
                    await File.ReadAllTextAsync(store.SettingsPath) == "{", "Damaged original files were overwritten");
                damaged.ResetSettings(); Require(await damaged.FlushAsync(), "Explicit settings reset could not recover broken preferences");
                Require((await File.ReadAllBytesAsync(store.ContentPath)).SequenceEqual(original), "Settings reset overwrote unreadable content");
            }
            var blocked = Path.Combine(directory.FullName, "blocked"); await File.WriteAllTextAsync(blocked, "file");
            using (var failure = new NotesViewModel(new NotesStore(blocked)))
            {
                await failure.Ready; failure.Text = "unsaved text";
                Require(!await failure.FlushAsync() && failure.Text == "unsaved text" && failure.Notice.StartsWith("保存失败"), "Write failure lost text or claimed success");
            }
            Console.WriteLine("PASS Notes invalid UTF-8/JSON and blocked writes preserve originals, memory content and recovery errors");
        }
        finally { directory.Delete(true); }
    }
    private static void CheckNotesFocus()
    {
        var self = new IntPtr(10); var browser = new IntPtr(20); var editor = new IntPtr(30);
        var native = new FakeNotesForeground { Current = browser }; native.Windows.UnionWith([self, browser, editor]);
        var manager = new NotesFocusManager(native, () => self);
        manager.Capture(); native.Current = self; manager.Capture();
        Require(manager.Previous == browser && manager.Restore() && native.Current == browser, "Focus capture recorded itself or failed browser restoration");
        native.Current = editor; manager.Capture(); native.Current = self;
        Require(manager.Previous == editor && manager.Restore() && native.Current == editor, "Refocusing retained the first restoration target");
        native.Allow = false; var calls = native.Activations;
        Require(!manager.Restore() && native.Activations == calls + 1, "Foreground refusal was retried or claimed success");
        native.Windows.Remove(editor); calls = native.Activations;
        Require(!manager.Restore() && native.Activations == calls, "Closed target was activated");
        native.Current = new IntPtr(99); manager.Capture();
        Require(manager.Previous == IntPtr.Zero && !manager.Restore(), "Invalid foreground retained a stale restoration target");
        Console.WriteLine("PASS Notes browser/editor focus targets, self exclusion, refocus updates, closed windows and one-attempt system refusal");
    }
    private static async Task CheckNotesShortcutsAsync()
    {
        HwndSource Source(string name) => new(new HwndSourceParameters(name)
            { PositionX = -20000, PositionY = -20000, Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        using var source = Source("Notes hotkey check"); using var other = Source("Notes conflict check");
        using var shortcuts = new NotesShortcutController(source); using var blocker = new NotesShortcutController(other);
        var a = new KeyboardShortcut(3, 0x83, "Ctrl + Alt + F20"); var b = new KeyboardShortcut(7, 0x83, "Ctrl + Alt + Shift + F20");
        var replacement = new KeyboardShortcut(3, 0x82, "Ctrl + Alt + F19"); var toggles = 0; var focuses = 0;
        shortcuts.ToggleRequested += () => toggles++; shortcuts.FocusRequested += () => focuses++;
        async Task Press(int id, KeyboardShortcut chord)
        {
            Require(PostMessage(source.Handle, 0x0312, new(id), new((long)chord.VirtualKey << 16 | chord.Modifiers)), "Could not post notes hotkey");
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        Require(shortcuts.Configure(a, b), "Notes hotkeys did not register");
        await Press(NotesShortcutController.ToggleId, a); await Press(NotesShortcutController.FocusId, b);
        for (var i = 0; i < 6; i++) await Press(NotesShortcutController.ToggleId, a);
        Require(toggles == 7 && focuses == 1, "Independent or rapid hotkeys lost actions");
        Require(!blocker.Configure(a, null) && !blocker.ToggleRegistered && shortcuts.ToggleRegistered, "Another registration replaced an owned hotkey");
        Require(shortcuts.Configure(replacement, b) && blocker.Configure(a, null), "Rebinding did not release the old hotkey");
        await Press(NotesShortcutController.ToggleId, a); Require(toggles == 7, "Queued old hotkey still activated");
        await Press(NotesShortcutController.ToggleId, replacement); Require(toggles == 8, "Replacement shortcut did not activate");
        Require(shortcuts.Suspend(true) && !shortcuts.ToggleRegistered && !shortcuts.FocusRegistered, "Editing failed to unregister both hotkeys");
        await Press(NotesShortcutController.FocusId, b); Require(focuses == 1, "Suspended hotkey activated");
        Require(shortcuts.Suspend(false) && shortcuts.ToggleRegistered && shortcuts.FocusRegistered, "Leaving editing did not re-register");
        Require(shortcuts.Configure(null, null) && !shortcuts.ToggleRegistered && !shortcuts.FocusRegistered, "Disabled tool retained hotkeys");
        Require(shortcuts.Configure(replacement, b), "Re-enabled tool failed");
        shortcuts.Dispose(); Require(blocker.Configure(replacement, b), "Disposal leaked registrations");
        Require(!blocker.Configure(a, a), "Equal notes hotkeys were accepted");
        Console.WriteLine("PASS Notes native hotkeys, conflicts, rapid messages, replacement, suspension, disable/re-enable and disposal");
    }
    private static async Task CheckNotesAppearanceAndNavigationAsync(string? images = null)
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-notes-navigation-");
        MainViewModel? model = null; var closedByWindow = false;
        try
        {
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            model = new MainViewModel(new ControlledProbe(display), display, new LocalProbe(),
                favoritesStore: new(Path.Combine(directory.FullName, "favorites.json")), gamingPreferencesStore: new(Path.Combine(directory.FullName, "gaming.json")),
                appPreferencesStore: new(Path.Combine(directory.FullName, "app.json")), navigationStore: new(Path.Combine(directory.FullName, "order.json")),
                notesStore: new(Path.Combine(directory.FullName, "Notes")));
            await model.Notes.Ready; var notes = model.Notes;
            notes.Text = "private note fixture"; notes.FontSize = 30; notes.FontBold = true;
            var font = notes.Preferences.Font; notes.ApplyBackground(notes.BackgroundPresets.Last().Value);
            Require(notes.Preferences.Font == font, "Background preset replaced font settings");
            var background = notes.Preferences.Background; notes.ApplyFont(notes.FontPresets.Last().Value);
            Require(notes.Preferences.Background == background && notes.BackgroundPresets.Count >= 6, "Font preset replaced background settings");
            Require(!notes.SetShortcut(true, notes.Preferences.ToggleShortcut, model.Gaming.ToggleShortcut) &&
                !notes.SetShortcut(false, model.Gaming.ToggleShortcut, model.Gaming.ToggleShortcut), "Shortcut validation accepted notes or lens conflicts");
            notes.IsFavorite = true; model.Page = "floating-notes"; model.OpenFavoritesCommand.Execute(null);
            Require(model.NavigationItems.Single().Id == "floating-notes" && model.ShowFloatingNotes && !model.ShowRefresh, "Notes favorite cloned or lost tool routing");
            model.ToggleToolCommand.Execute(model.NavigationItems.Single());
            Require(!notes.IsEnabled && !model.NavigationItems.Single().IsEnabled && model.Gaming.IsEnabled, "Notes disable affected lens or retained enabled navigation");
            model.ToggleToolCommand.Execute(model.NavigationItems.Single()); model.ExitFavoritesCommand.Execute(null);
            Require(model.ShowFloatingNotes && notes.IsEnabled && !model.Gaming.IsActive, "Notes re-enable changed selection or started capture");
            model.MoveNavigationItem("floating-notes", "screen-lens");
            Require(model.NavigationItems[0].Id == "floating-notes" && !model.ExportJson().Contains("private note fixture"), "Notes sorting failed or diagnostics exposed text");
            var original = notes.Preferences; model.DarkThemeCommand.Execute(null);
            Require(notes.Preferences == original, "App theme reset independent note appearance");
            Require(await notes.FlushAsync(), "Notes settings did not persist");
            var window = new MainWindow(model, enableShortcuts: false) { ShowActivated = false, ShowInTaskbar = false,
                Left = -20000, Top = -20000, Width = 1040, Height = 960, WindowStartupLocation = WindowStartupLocation.Manual };
            var closed = new TaskCompletionSource(); window.Closed += (_, _) => { closedByWindow = true; closed.TrySetResult(); };
            IEnumerable<DependencyObject> Descendants(DependencyObject root)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i); yield return child;
                    foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            void Image(string name)
            {
                if (images is null) return;
                var content = window.PreviewContent; var dpi = VisualTreeHelper.GetDpi(content);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(images);
                using var stream = File.Create(Path.Combine(images, name + ".png")); encoder.Save(stream);
            }
            try
            {
                window.Show(); await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                var view = Descendants(window).OfType<NotesSettingsView>().Single();
                Require(view.IsVisible && notes.InstalledFonts.Count > 0,
                    $"Notes page or installed font list stayed unavailable: page={model.Page}, visible={view.IsVisible}, fonts={notes.InstalledFonts.Count}");
                var picker = Descendants(view).OfType<ComboBox>().Single(); picker.ApplyTemplate();
                Require(picker.Template.FindName("PART_EditableTextBox", picker) is TextBox, "Editable font picker lacked its editor");
                picker.SelectedItem = notes.InstalledFonts.First(family => family != notes.FontFamily);
                Require(notes.FontFamily == (string)picker.SelectedItem, "Selecting an installed font did not update notes");
                var editor = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
                editor.Text = "Consolas";
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Require(notes.FontFamily == "Consolas", "Typing a font family did not update notes");
                notes.ResetFont(); notes.ResetBackground();
                foreach (var dark in new[] { true, false })
                {
                    if (dark) model.DarkThemeCommand.Execute(null); else model.LightThemeCommand.Execute(null);
                    await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    var color = ((SolidColorBrush)window.FindResource("TextBrush")).Color;
                    Require(Descendants(view).OfType<CheckBox>().All(box => box.Foreground is SolidColorBrush brush && brush.Color == color),
                        "Notes checkboxes ignored the current theme's readable text color");
                    var scroll = Descendants(view).OfType<ScrollViewer>().First(); scroll.ScrollToTop(); window.UpdateLayout();
                    Image("settings-" + (dark ? "dark" : "light") + "-top");
                    scroll.ScrollToBottom(); await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    Require(((FrameworkElement)view.FindName("AppearancePreview")).IsVisible && scroll.VerticalOffset > 0,
                        "Font preview and data controls could not be reached by scrolling");
                    Image("settings-" + (dark ? "dark" : "light") + "-bottom");
                }
            }
            finally { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            Console.WriteLine("PASS Notes independent presets, hotkeys, navigation, privacy, themed controls and editable installed fonts");
        }
        finally { if (!closedByWindow) model?.Dispose(); directory.Delete(true); }
    }
    private static async Task CheckNotesWindowAsync(string? images = null)
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-notes-window-");
        var foreground = new NotesForeground(); var original = foreground.Current;
        using var source = new HwndSource(new HwndSourceParameters("Notes lifecycle check")
            { PositionX = -20000, PositionY = -20000, Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        using var model = new NotesViewModel(new NotesStore(directory.FullName));
        using var controller = new NotesController(model, source, () => null);
        var companion = new Window { Title = "DailyToolkit notes focus test", Width = 260, Height = 150,
            ShowInTaskbar = false, Left = 450, Top = 80, Content = new System.Windows.Controls.TextBox { Text = "test editor" } };
        try
        {
            await controller.Ready;
            model.SetShortcut(false, new(3, 0x83, "Ctrl + Alt + F20"), null);
            model.SetShortcut(true, new(7, 0x83, "Ctrl + Alt + Shift + F20"), null);
            controller.Refocus(); await controller.Pending;
            Require(!model.IsVisible && controller.NoteWindow is null, "Refocus displayed a hidden note");
            companion.Show();
            // A real owned hotkey supplies legitimate last input. Posted WM_HOTKEY messages
            // cannot grant foreground rights and would give false results in a background test runner.
            PressNotesTestHotkey(); await Task.Delay(120); await controller.Pending;
            Require(model.IsFocused && foreground.Current == controller.NoteWindow?.Handle, "Real registered hotkey did not give notes foreground focus");
            Require(companion.Activate(), "Test companion could not activate from its foreground process"); await Task.Delay(60);
            controller.Toggle(); await controller.Pending;
            PressNotesTestHotkey(); await Task.Delay(120); await controller.Pending;
            var window = controller.NoteWindow!;
            Require(model.IsVisible && model.IsFocused && window.NoteEditor.IsKeyboardFocusWithin && window.Opacity == 1 && window.Topmost,
                $"Native note show did not focus editor or conflated whole-window opacity/topmost: visible={model.IsVisible}, focused={model.IsFocused}, keyboard={window.NoteEditor.IsKeyboardFocusWithin}, foreground={foreground.Current}, note={window.Handle}, notice={model.Notice}");
            window.NoteEditor.Text = "文本编辑 / selection"; window.NoteEditor.Select(3, 4);
            controller.Unfocus(); await Task.Delay(100);
            Require(model.IsVisible && !model.IsFocused && foreground.Current == new WindowInteropHelper(companion).Handle,
                $"Unfocus hid note or failed to restore companion editor: visible={model.IsVisible}, focused={model.IsFocused}, foreground={foreground.Current}, expected={new WindowInteropHelper(companion).Handle}, notice={model.Notice}");
            controller.Refocus(); await controller.Pending; await Task.Delay(80);
            Require(model.IsFocused && window.NoteEditor.SelectionStart == 3 && window.NoteEditor.SelectionLength == 4, "Refocus lost selection");
            void Escape()
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.NoteEditor.RaiseEvent(key);
            }
            var composition = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, window.NoteEditor, "组合"))
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent };
            window.NoteEditor.RaiseEvent(composition); Escape();
            Require(model.IsFocused, "Escape canceled note focus before IME composition cancellation");
            Escape(); await Task.Delay(60);
            Require(model.IsVisible && !model.IsFocused && foreground.Current == new WindowInteropHelper(companion).Handle, "Escape did not restore previous window");
            controller.Refocus(); await controller.Pending;
            companion.Activate(); await Task.Delay(80);
            Require(model.IsVisible && !model.IsFocused && foreground.Current == new WindowInteropHelper(companion).Handle,
                "External deactivation hid note or stole focus");
            controller.Toggle(); await controller.Pending;
            Require(!model.IsVisible && foreground.Current == new WindowInteropHelper(companion).Handle && controller.NoteWindow == window,
                "Hiding an unfocused note switched the active app or destroyed the reusable window");
            for (var i = 0; i < 6; i++) controller.Toggle();
            await controller.Pending; Require(!model.IsVisible && ReferenceEquals(window, controller.NoteWindow), "Rapid toggle leaked windows or lost parity");
            controller.Toggle(); await controller.Pending; await Task.Delay(80);
            model.BackgroundOpacity = .3; model.FontOpacity = 1;
            Require(window.Opacity == 1 && ((System.Windows.Controls.Border)window.FindName("NoteBackground")).Background is SolidColorBrush noteBrush &&
                noteBrush.Color.A == 77 && ((SolidColorBrush)window.NoteEditor.Foreground).Color.A == 255,
                "Background transparency also faded text");
            var bounds = NotesPositionService.Bounds(window.Handle);
            Require(NotesPositionService.WorkAreas().Any(bounds.Intersects), "Note remained outside available monitors");
            model.Update(model.Preferences with { X = -50000, Y = -50000 }); window.RecoverPosition();
            bounds = NotesPositionService.Bounds(window.Handle);
            Require(NotesPositionService.WorkAreas().Any(bounds.Intersects), "Offscreen position did not recover");
            controller.Refocus(); await controller.Pending; companion.Close(); controller.Unfocus();
            Require(model.IsVisible && !model.IsFocused && !window.NoteEditor.IsKeyboardFocusWithin,
                "Closed target kept editor keyboard focus or hid the note");
            controller.Refocus(); await controller.Pending;
            if (images is not null)
            {
                Directory.CreateDirectory(images); window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(images, "floating-notes-window.png")); encoder.Save(stream);
            }
            model.IsEnabled = false; await controller.Pending;
            Require(!model.IsVisible && controller.NoteWindow is null && model.Text == window.NoteEditor.Text, "Disable did not release window or preserve text");
            model.IsEnabled = true; controller.Toggle(); await controller.Pending;
            Require(model.IsVisible && controller.NoteWindow != window, "Re-enable did not recreate a released window");
            controller.NoteWindow!.NoteEditor.Text += " last input";
            Require(await controller.PrepareExitAsync() && await File.ReadAllTextAsync(model.Store.ContentPath) == model.Text, "Exit flush lost final edit");
            controller.Dispose(); Require(!model.IsVisible && controller.NoteWindow is null, "Exit leaked note window");
            Console.WriteLine("PASS Actual notes window focus/restore, external deactivation, selection, rapid hide/reuse, brush alpha, recovery, disable and exit flush");
        }
        finally { controller.Dispose(); companion.Close(); model.Dispose(); if (foreground.IsValid(original)) foreground.Activate(original); directory.Delete(true); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NotesKeyInput
    { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct NotesInputUnion
    { [FieldOffset(0)] public NotesKeyInput Keyboard; [FieldOffset(0)] public NotesMouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NotesTestInput
    { public uint Type; public NotesInputUnion Data; }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendNotesInput(uint count, NotesTestInput[] inputs, int size);
    private static void PressNotesTestHotkey()
    {
        var sequence = new List<NotesTestInput>();
        foreach (var key in new ushort[] { 0x11, 0x12, 0x83 }) sequence.Add(new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } });
        foreach (var key in new ushort[] { 0x83, 0x12, 0x11 }) sequence.Add(new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = 2 } } });
        Require(SendNotesInput((uint)sequence.Count, sequence.ToArray(), Marshal.SizeOf<NotesTestInput>()) == sequence.Count,
            "Windows did not accept the isolated registered test hotkey");
    }
}

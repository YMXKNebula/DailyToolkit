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
                model.AllowManualResize = false; model.FocusOnClick = true; model.FocusBorderColor = "#A675D1"; model.Width = 361.5; model.Height = 419.6;
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
                Require(reopened.Text.Contains("隐藏前最后输入") && reopened.FontSize == 26 && reopened.BackgroundOpacity == .4 &&
                    !reopened.AllowManualResize && reopened.FocusOnClick && reopened.FocusBorderColor == "#A675D1" && reopened.Width == 362 && reopened.Height == 420,
                    "Text and independent appearance did not survive a new model/version");
                var text = reopened.Text; reopened.ResetSettings();
                Require(await reopened.FlushAsync() && reopened.Text == text && reopened.Preferences == new NotesPreferences(), "Reset settings changed note content");
            }
            await File.WriteAllTextAsync(store.SettingsPath, "{}");
            Require((await store.LoadAsync()).Preferences == new NotesPreferences(), "Older settings missing notes fields failed");
            await File.WriteAllTextAsync(store.SettingsPath, "{\"Width\":360.5,\"Height\":420.4,\"DoubleClickUnfocus\":true,\"ClickToFocus\":true}");
            var migrated = (await store.LoadAsync()).Preferences;
            Require(migrated.Width == 361 && migrated.Height == 420 && migrated.AllowManualResize && migrated.OpaqueWhenFocused && !migrated.FocusOnClick,
                "Legacy settings did not preserve rounded size and new defaults");
            await store.SaveSettingsAsync(migrated);
            var migratedJson = await File.ReadAllTextAsync(store.SettingsPath);
            Require(!migratedJson.Contains("DoubleClickUnfocus", StringComparison.OrdinalIgnoreCase) && !migratedJson.Contains("ClickToFocus", StringComparison.OrdinalIgnoreCase),
                "Removed mouse focus settings were still persisted");
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
            static double Luminance(string hex)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                static double Linear(byte value) { var channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
                return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
            }
            Require(notes.AppearancePresets.Count >= 10, "Notes offered too few complete color schemes");
            foreach (var preset in notes.AppearancePresets)
            {
                var before = notes.Preferences; notes.ApplyAppearance(preset.Value);
                var bg = Luminance(notes.BackgroundColor); var fg = Luminance(notes.FontColor);
                Require((Math.Max(bg, fg) + .05) / (Math.Min(bg, fg) + .05) >= 7 && notes.FocusBorderColor == preset.Value.Border,
                    $"Preset {preset.Name} lacks readable text/background contrast or its matching focus border");
                Require(notes.FontFamily == before.Font.Family && notes.FontSize == before.Font.Size && notes.FontBold == before.Font.Bold &&
                    notes.Text == "private note fixture" && notes.Width == before.Width, "Color preset replaced text, font styling or dimensions");
            }
            var font = notes.Preferences.Font; notes.ApplyBackground(notes.BackgroundPresets.Last().Value);
            Require(notes.Preferences.Font == font, "Background preset replaced font settings");
            var background = notes.Preferences.Background; notes.ApplyFont(notes.FontPresets.Last().Value);
            Require(notes.Preferences.Background == background && notes.BackgroundPresets.Count >= 6, "Font preset replaced background settings");
            Require(!notes.SetShortcut(true, notes.Preferences.ToggleShortcut, model.Gaming.ToggleShortcut) &&
                !notes.SetShortcut(false, model.Gaming.ToggleShortcut, model.Gaming.ToggleShortcut), "Shortcut validation accepted notes or lens conflicts");
            notes.ToggleFavoriteCommand.Execute(null); model.Page = "floating-notes"; model.OpenFavoritesCommand.Execute(null);
            Require(notes.FavoriteSymbol == "★" && notes.FavoriteHint == "取消收藏", "Notes favorite star did not update");
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
                Left = -20000, Top = -20000, Width = 1040, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
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
                Require(!picker.IsEditable && picker.Template.FindName("PART_EditableTextBox", picker) is null,
                    "Font chooser still exposed a text input");
                picker.SelectedItem = notes.InstalledFonts.First(family => family != notes.FontFamily);
                Require(notes.FontFamily == (string)picker.SelectedItem, "Selecting an installed font did not update notes");
                picker.SelectedItem = "Consolas";
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Require(notes.FontFamily == "Consolas", "Installed font selection did not update notes");
                var fixedMode = (RadioButton)view.FindName("NotesFixedMode");
                var movableMode = (RadioButton)view.FindName("NotesMovableMode");
                movableMode.IsChecked = true;
                Require(notes.Movable && fixedMode.IsChecked == false, "Position radios did not select movable mode");
                fixedMode.IsChecked = true;
                Require(!notes.Movable && movableMode.IsChecked == false, "Position radios did not select fixed mode");
                var favorite = (Button)window.FindName("ToolFavoriteButton");
                var favoriteIcon = (System.Windows.Shapes.Path)window.FindName("ToolFavoriteIcon");
                Require(favoriteIcon.ActualHeight >= 26 && favoriteIcon.StrokeLineJoin == PenLineJoin.Round &&
                    favoriteIcon.Fill == (System.Windows.Media.Brush)window.FindResource("AccentBrush"), "Favorite icon was too small, angular or failed to show the saved state");
                favorite.Command.Execute(null);
                Require(!notes.IsFavorite && notes.FavoriteSymbol == "☆" &&
                    !new FavoritesStore(Path.Combine(directory.FullName, "favorites.json")).Load().Contains("floating-notes"),
                    "Favorite star command did not update the existing navigation item");
                Require(((SolidColorBrush)favoriteIcon.Fill).Color.A == 0, "Favorite icon did not become outlined after removing the favorite");
                var resize = (CheckBox)view.FindName("ManualResize"); resize.IsChecked = false;
                Require(!notes.AllowManualResize, "Resize setting did not update notes");
                var clickFocus = (CheckBox)view.FindName("ClickFocus"); clickFocus.IsChecked = true;
                Require(notes.FocusOnClick, "Position click-focus setting did not update notes"); clickFocus.IsChecked = false;
                var previewFocus = (CheckBox)view.FindName("PreviewFocused");
                var previewResize = (FrameworkElement)view.FindName("PreviewResize");
                var previewHide = (FrameworkElement)view.FindName("PreviewHide");
                var previewMove = (FrameworkElement)view.FindName("PreviewMove");
                var previewFrame = (Border)view.FindName("PreviewEditorFrame");
                previewFocus.IsChecked = true; resize.IsChecked = true;
                Require(!notes.Movable && previewResize.Visibility == Visibility.Visible && previewHide.Visibility == Visibility.Visible &&
                    previewMove.Visibility == Visibility.Hidden && ((SolidColorBrush)previewFrame.BorderBrush).Color.A == 255,
                    "Focused fixed preview did not retain its resize corner and writing border");
                movableMode.IsChecked = true;
                Require(previewMove.Visibility == Visibility.Visible, "Focused movable preview lacked its central move indicator");
                previewFocus.IsChecked = false;
                Require(previewResize.Visibility == Visibility.Hidden && previewHide.Visibility == Visibility.Hidden &&
                    previewMove.Visibility == Visibility.Hidden && ((SolidColorBrush)previewFrame.BorderBrush).Color.A == 0,
                    "Unfocused preview retained writing border or interactive chrome");
                fixedMode.IsChecked = true; previewFocus.IsChecked = true;
                notes.Width = 362.3; notes.Height = 419.8;
                Require(((TextBox)view.FindName("NoteWidth")).Text == "362" && ((TextBox)view.FindName("NoteHeight")).Text == "420", "Note dimensions displayed fractional values");
                var textPicker = (DailyToolkit.Desktop.Controls.ColorPicker)view.FindName("FontColorPicker");
                Require(!textPicker.ShowHexInput && Descendants(textPicker).OfType<TextBox>().All(box => !box.IsVisible), "Notes color picker required hex input");
                var swatch = Descendants(textPicker).OfType<Button>().First(button => button.Tag as string == "#A675D1");
                swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(notes.FontColor == "#A675D1", "Clicking a color did not update the bound text color");
                model.Page = "screen-lens"; await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Require(favorite.IsVisible && favorite.Command == model.Gaming.ToggleFavoriteCommand, "Shared title star did not switch to the lens favorite command");
                model.Page = "floating-notes"; await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Require(favorite.Command == notes.ToggleFavoriteCommand, "Shared title star did not return to notes");
                notes.FontColor = "#B02070";
                Require(((SolidColorBrush)((Button)view.FindName("FontColorSwatch")).Background).Color == Color.FromRgb(176, 32, 112) &&
                    ((SolidColorBrush)((TextBox)view.FindName("PreviewText")).Foreground).Color == Color.FromRgb(176, 32, 112),
                    "Text color swatch and adjacent preview failed to update together");
                notes.ResetFont(); notes.ResetBackground();
                foreach (var dark in new[] { true, false })
                {
                    if (dark) model.DarkThemeCommand.Execute(null); else model.LightThemeCommand.Execute(null);
                    await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    var color = ((SolidColorBrush)window.FindResource("TextBrush")).Color;
                    Require(Descendants(view).OfType<CheckBox>().All(box => box.Foreground is SolidColorBrush brush && brush.Color == color),
                        "Notes checkboxes ignored the current theme's readable text color");
                    var scroll = (ScrollViewer)view.FindName("SettingsScroll"); scroll.ScrollToTop();
                    await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    Image("settings-" + (dark ? "dark" : "light") + "-top");
                    var appearanceCard = (FrameworkElement)view.FindName("AppearanceCard");
                    appearanceCard.BringIntoView(); await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    var appearanceScroll = (ScrollViewer)view.FindName("AppearanceScroll"); appearanceScroll.ScrollToTop();
                    await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    var preview = (FrameworkElement)view.FindName("AppearancePreview"); var previewTop = preview.TranslatePoint(new(), view);
                    Require(preview.IsDescendantOf(appearanceCard) && picker.IsDescendantOf(appearanceCard),
                        "Preview was separated from the background and font settings area");
                    Image("settings-" + (dark ? "dark" : "light") + "-background");
                    appearanceScroll.ScrollToBottom(); await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    Require(preview.IsVisible && appearanceScroll.VerticalOffset > 0 && preview.TranslatePoint(new(), view) == previewTop &&
                        previewTop.Y >= 0 && previewTop.Y + preview.ActualHeight <= view.ActualHeight && Grid.GetColumn((UIElement)view.FindName("PreviewPanel")) == 1,
                        $"Embedded preview moved out of view while scrolling appearance controls: top={previewTop}, height={preview.ActualHeight}, view={view.ActualHeight}, offset={appearanceScroll.VerticalOffset}");
                    Image("settings-" + (dark ? "dark" : "light") + "-font");
                    scroll.ScrollToBottom(); await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    Image("settings-" + (dark ? "dark" : "light") + "-bottom");
                }
                window.Width = 680; window.Height = 700;
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                ((FrameworkElement)view.FindName("AppearanceCard")).BringIntoView();
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                var narrowScroll = (ScrollViewer)view.FindName("AppearanceScroll"); var previewPanel = (FrameworkElement)view.FindName("PreviewPanel");
                Require(Grid.GetColumn(previewPanel) == 0 && Grid.GetRow(narrowScroll) == 1 && previewPanel.ActualHeight > 100,
                    "Narrow appearance area did not keep its embedded preview above the controls");
                var narrowTop = previewPanel.TranslatePoint(new(), view);
                narrowScroll.ScrollToBottom(); await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Require(narrowTop == previewPanel.TranslatePoint(new(), view) && narrowScroll.VerticalOffset > 0,
                    "Narrow preview disappeared when scrolling controls");
                Image("settings-light-narrow");
            }
            finally { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            Console.WriteLine("PASS Notes independent presets, shared title star, integer sizes, clickable colors, position radios, selection-only fonts, color swatches and embedded wide/narrow appearance previews");
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
            model.Text = "首行\n" + string.Join('\n', Enumerable.Range(1, 42).Select(line => $"初次显示第 {line} 行"));
            model.SetShortcut(false, new(3, 0x83, "Ctrl + Alt + F20"), null);
            model.SetShortcut(true, new(7, 0x83, "Ctrl + Alt + Shift + F20"), null);
            controller.ToggleFocus(); await controller.Pending;
            Require(!model.IsVisible && controller.NoteWindow is null, "Refocus displayed a hidden note");
            companion.Show();
            // A real owned hotkey supplies legitimate last input. Posted WM_HOTKEY messages
            // cannot grant foreground rights and would give false results in a background test runner.
            PressNotesTestHotkey(); await Task.Delay(120); await controller.Pending;
            for (var attempt = 0; attempt < 40 && !model.IsFocused; attempt++) { await Task.Delay(25); await controller.Pending; }
            Require(model.IsFocused && foreground.Current == controller.NoteWindow?.Handle,
                $"Real registered hotkey did not give notes foreground focus: visible={model.IsVisible}, focused={model.IsFocused}, foreground={foreground.Current}, note={controller.NoteWindow?.Handle}, notice={model.Notice}");
            Require(controller.NoteWindow!.NoteEditor.GetFirstVisibleLineIndex() == 0, "Showing a long note for the first time skipped its first line");
            Require(companion.Activate(), "Test companion could not activate from its foreground process"); await Task.Delay(60);
            controller.Toggle(); await controller.Pending;
            PressNotesTestHotkey(); await Task.Delay(120); await controller.Pending;
            var window = controller.NoteWindow!;
            void WindowImage(string name)
            {
                if (images is null) return;
                Directory.CreateDirectory(images); window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(images, name + ".png")); encoder.Save(stream);
            }
            Require(model.IsVisible && model.IsFocused && window.NoteEditor.IsKeyboardFocusWithin && window.Opacity == 1 && window.Topmost,
                $"Native note show did not focus editor or conflated whole-window opacity/topmost: visible={model.IsVisible}, focused={model.IsFocused}, keyboard={window.NoteEditor.IsKeyboardFocusWithin}, foreground={foreground.Current}, note={window.Handle}, notice={model.Notice}");
            window.NoteEditor.Text = "文本编辑 / selection"; window.NoteEditor.Select(3, 4);
            controller.Unfocus(); await Task.Delay(100);
            Require(model.IsVisible && !model.IsFocused && foreground.Current == new WindowInteropHelper(companion).Handle,
                $"Unfocus hid note or failed to restore companion editor: visible={model.IsVisible}, focused={model.IsFocused}, foreground={foreground.Current}, expected={new WindowInteropHelper(companion).Handle}, notice={model.Notice}");
            controller.ToggleFocus(); await controller.Pending; await Task.Delay(80);
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
            controller.ToggleFocus(); await controller.Pending;
            await CheckNotesMouseGesturesAsync(model, controller, window, companion);
            companion.Activate(); await Task.Delay(80);
            Require(model.IsVisible && !model.IsFocused && foreground.Current == new WindowInteropHelper(companion).Handle,
                "External deactivation hid note or stole focus");
            controller.Toggle(); await controller.Pending;
            Require(!model.IsVisible && foreground.Current == new WindowInteropHelper(companion).Handle && controller.NoteWindow == window,
                "Hiding an unfocused note switched the active app or destroyed the reusable window");
            for (var i = 0; i < 6; i++) controller.Toggle();
            await controller.Pending; Require(!model.IsVisible && ReferenceEquals(window, controller.NoteWindow), "Rapid toggle leaked windows or lost parity");
            controller.Toggle(); await controller.Pending; await Task.Delay(80);
            model.BackgroundOpacity = .3; model.FontOpacity = .4; model.FocusBorderColor = "#4285F4";
            var noteBackground = (Border)window.FindName("NoteBackground");
            var writingFrame = (Border)window.FindName("EditorFrame");
            var hideButton = (FrameworkElement)window.FindName("HideButton");
            var resizeGrip = (FrameworkElement)window.FindName("ResizeGrip");
            var moveIndicator = (FrameworkElement)window.FindName("MoveIndicator");
            window.NoteEditor.Text = string.Join('\n', Enumerable.Range(1, 50).Select(line => $"第 {line} 行 · scrolling fixture"));
            window.NoteEditor.ScrollToHome(); window.UpdateLayout();
            IEnumerable<DependencyObject> NoteDescendants(DependencyObject root)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i); yield return child;
                    foreach (var nested in NoteDescendants(child)) yield return nested;
                }
            }
            var scrollbar = NoteDescendants(window.NoteEditor).OfType<System.Windows.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical);
            var textViewport = NoteDescendants(window.NoteEditor).OfType<ScrollContentPresenter>().Single();
            await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            void CheckFade(bool top, bool bottom)
            {
                if (!top && !bottom) { Require(textViewport.OpacityMask is null, "Unclipped or inactive text retained a fade"); return; }
                Require(textViewport.OpacityMask is LinearGradientBrush fade && fade.IsFrozen &&
                    fade.GradientStops[0].Color.A == (top ? 0 : 255) && fade.GradientStops[^1].Color.A == (bottom ? 0 : 255) &&
                    fade.GradientStops[1].Color.A == 255 && fade.GradientStops[2].Color.A == 255 &&
                    window.NoteEditor.OpacityMask is null && writingFrame.OpacityMask is null && scrollbar.OpacityMask is null,
                    "Text edge fade affected the wrong edges, full editor, frame or scrollbar");
            }
            CheckFade(false, true);
            Require(window.Opacity == 1 && ((SolidColorBrush)noteBackground.Background).Color.A == 255 &&
                ((SolidColorBrush)window.NoteEditor.Foreground).Color.A == 255 && ((SolidColorBrush)noteBackground.BorderBrush).Color == Color.FromRgb(66, 133, 244) &&
                writingFrame.BorderBrush == noteBackground.BorderBrush && scrollbar.IsVisible && scrollbar.IsEnabled && scrollbar.Opacity == 1 &&
                hideButton.IsVisible && resizeGrip.IsVisible && moveIndicator.Visibility == Visibility.Hidden,
                "Focus did not apply an opaque background/text and configured border");
            WindowImage("floating-notes-fixed-focused");
            var focusedSize = new Size(window.Width, window.Height);
            var focusedEditorSize = new Size(window.NoteEditor.ActualWidth, window.NoteEditor.ActualHeight);
            var focusedCharacter = window.NoteEditor.GetRectFromCharacterIndex(0);
            var focusedTextOrigin = window.NoteEditor.TranslatePoint(focusedCharacter.TopLeft, window);
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            Require(!model.IsFocused && ((SolidColorBrush)noteBackground.Background).Color.A == 77 &&
                ((SolidColorBrush)window.NoteEditor.Foreground).Color.A == 102 && ((SolidColorBrush)noteBackground.BorderBrush).Color.A == 0 &&
                focusedSize == new Size(window.Width, window.Height) && ((SolidColorBrush)writingFrame.BorderBrush).Color.A == 0 &&
                !hideButton.IsVisible && !resizeGrip.IsVisible && scrollbar.Opacity == 0 && !scrollbar.IsHitTestVisible,
                "Focus shortcut did not hide chrome and restore independent alpha without resizing");
            window.UpdateLayout();
            CheckFade(false, false);
            Require(window.NoteEditor.ActualHeight >= focusedEditorSize.Height + 20 && window.NoteEditor.ActualWidth == focusedEditorSize.Width,
                "Unfocused text did not expand into the hidden controls' space or changed its wrapping width");
            var unfocusedCharacter = window.NoteEditor.GetRectFromCharacterIndex(0);
            Require(window.NoteEditor.TranslatePoint(unfocusedCharacter.TopLeft, window) == focusedTextOrigin &&
                unfocusedCharacter.Height == focusedCharacter.Height, "Expanding the display area shifted or stretched the text");
            WindowImage("floating-notes-unfocused");
            PressNotesTestHotkey(true); await Task.Delay(100); await controller.Pending;
            Require(model.IsFocused && ((SolidColorBrush)noteBackground.Background).Color.A == 255, "Same shortcut failed to restore focus and opacity");
            window.UpdateLayout();
            Require(new Size(window.NoteEditor.ActualWidth, window.NoteEditor.ActualHeight) == focusedEditorSize,
                "Refocusing did not restore the original editor area");
            foreach (var offset in new[] { 160d, double.MaxValue })
            {
                if (offset == double.MaxValue) window.NoteEditor.ScrollToEnd(); else window.NoteEditor.ScrollToVerticalOffset(offset);
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                var visibleIndex = window.NoteEditor.GetCharacterIndexFromPoint(new Point(20, window.NoteEditor.ActualHeight / 2), true);
                window.NoteEditor.Select(visibleIndex, 0);
                var character = window.NoteEditor.GetRectFromCharacterIndex(visibleIndex);
                var position = window.NoteEditor.TranslatePoint(character.TopLeft, window);
                var originalOffset = window.NoteEditor.VerticalOffset;
                CheckFade(true, offset != double.MaxValue);
                if (offset != double.MaxValue)
                {
                    var dpi = VisualTreeHelper.GetDpi(window.NoteEditor);
                    var width = (int)Math.Ceiling(window.NoteEditor.ActualWidth * dpi.DpiScaleX);
                    var height = (int)Math.Ceiling(window.NoteEditor.ActualHeight * dpi.DpiScaleY);
                    byte[] Pixels()
                    {
                        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bitmap.Render(window.NoteEditor); var pixels = new byte[width * height * 4];
                        bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
                    }
                    var faded = Pixels(); var mask = textViewport.OpacityMask;
                    textViewport.OpacityMask = null;
                    var plain = Pixels(); textViewport.OpacityMask = mask;
                    var changed = Enumerable.Range(0, faded.Length).Count(index => faded[index] != plain[index]);
                    Require(changed > 30, "Static edge mask did not visibly fade rendered text");
                    Console.WriteLine($"PASS Static text fade changes {changed} rendered pixel channels");
                }
                WindowImage(offset == double.MaxValue ? "floating-notes-end-focused" : "floating-notes-middle-focused");
                controller.Unfocus(); await Task.Delay(100); window.UpdateLayout();
                CheckFade(false, false);
                var expanded = window.NoteEditor.GetRectFromCharacterIndex(visibleIndex);
                var top = writingFrame.TranslatePoint(new(), window);
                Require(Math.Abs(top.X - top.Y) < .1 && Math.Abs(top.X - (window.Height - top.Y - writingFrame.ActualHeight)) < .1,
                    "Unfocused display area did not use equal top, bottom and side margins");
                Require((window.NoteEditor.TranslatePoint(expanded.TopLeft, window) - position).Length < .1 && expanded.Height == character.Height,
                    $"Expanding a scrolled note moved or stretched visible text: offset={originalOffset}");
                WindowImage(offset == double.MaxValue ? "floating-notes-end-unfocused" : "floating-notes-middle-unfocused");
                controller.ToggleFocus(); await controller.Pending; await Task.Delay(100); window.UpdateLayout();
                CheckFade(true, offset != double.MaxValue);
                Require(Math.Abs(window.NoteEditor.VerticalOffset - originalOffset) < .1 &&
                    (window.NoteEditor.TranslatePoint(window.NoteEditor.GetRectFromCharacterIndex(visibleIndex).TopLeft, window) - position).Length < .1,
                    "Refocusing a scrolled note lost its scroll offset or text position");
            }
            window.NoteEditor.ScrollToHome(); window.NoteEditor.Select(0, 0);
            model.OpaqueWhenFocused = false;
            await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            Require(((SolidColorBrush)noteBackground.Background).Color.A == 77 && ((SolidColorBrush)writingFrame.BorderBrush).Color.A == 77 &&
                Math.Abs(scrollbar.Opacity - .3) < .001 && scrollbar.IsHitTestVisible,
                "Focused writing border and scrollbar ignored the configured background alpha");
            model.Movable = true;
            Require(moveIndicator.Visibility == Visibility.Visible && ((Border)moveIndicator).Background == writingFrame.BorderBrush,
                "Focused move indicator did not follow the frame color/opacity");
            WindowImage("floating-notes-movable-translucent");
            model.Movable = false;
            Require(!moveIndicator.IsVisible && resizeGrip.IsVisible, "Fixed mode incorrectly hid its resize corner");
            model.AllowManualResize = false;
            Require(!resizeGrip.IsVisible && hideButton.IsVisible, "Disabling manual resize removed unrelated controls or kept the grip");
            model.AllowManualResize = true;
            model.OpaqueWhenFocused = true;
            var bounds = NotesPositionService.Bounds(window.Handle);
            Require(NotesPositionService.WorkAreas().Any(bounds.Intersects), "Note remained outside available monitors");
            model.Update(model.Preferences with { X = -50000, Y = -50000 }); window.RecoverPosition();
            bounds = NotesPositionService.Bounds(window.Handle);
            Require(NotesPositionService.WorkAreas().Any(bounds.Intersects), "Offscreen position did not recover");
            companion.Close(); controller.Unfocus();
            Require(model.IsVisible && !model.IsFocused && !window.NoteEditor.IsKeyboardFocusWithin,
                "Closed target kept editor keyboard focus or hid the note");
            controller.ToggleFocus(); await controller.Pending;
            Require(model.IsFocused && !model.Notice.StartsWith("先前窗口已关闭", StringComparison.Ordinal), "Successful focus retained a stale restore failure");
            WindowImage("floating-notes-window");
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
    private static void PressNotesTestHotkey(bool focus = false)
    {
        var sequence = new List<NotesTestInput>();
        foreach (var key in focus ? new ushort[] { 0x11, 0x12, 0x10, 0x83 } : new ushort[] { 0x11, 0x12, 0x83 }) sequence.Add(new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } });
        foreach (var key in focus ? new ushort[] { 0x83, 0x10, 0x12, 0x11 } : new ushort[] { 0x83, 0x12, 0x11 }) sequence.Add(new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = 2 } } });
        Require(SendNotesInput((uint)sequence.Count, sequence.ToArray(), Marshal.SizeOf<NotesTestInput>()) == sequence.Count,
            "Windows did not accept the isolated registered test hotkey");
    }
}

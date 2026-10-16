using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackQuickAsync(string images)
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-quick-");
        try
        {
            var module = Path.Combine(fixture.FullName, "module");
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            using var host = new MainViewModel(new ControlledProbe(display), display, new LocalProbe(),
                favoritesStore: new FavoritesStore(Path.Combine(fixture.FullName, "favorites.json")),
                gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(fixture.FullName, "gaming.json")),
                navigationStore: new NavigationOrderStore(Path.Combine(fixture.FullName, "navigation.json")),
                appPreferencesStore: new AppPreferencesStore(Path.Combine(fixture.FullName, "app.json")), startupRegistration: new StartupFake(),
                notesStore: IsolatedNotes(), clipboardSettingsStore: IsolatedClipboard(), filePackStore: new FilePackStore(module));
            var model = host.FilePack; await model.SettingsReady;
            Require(!model.IsLoaded && !Directory.Exists(module), "Reading tool preferences eagerly recovered tasks or created storage");
            Require(model.IsEnabled && model.ToggleShortcut?.Matches(new(3, 0x50, "P")) == true, "FilePack defaults changed old tool availability");
            var globalPalette = host.Theme;
            using var source = ClipboardTestWindow();
            var foreground = new FakeNotesForeground { Current = new IntPtr(9999) }; foreground.Windows.Add(foreground.Current);
            using var controller = new FilePackController(host, source, () => [host.Gaming.ToggleShortcut,
                host.Notes.Preferences.ToggleShortcut, host.Notes.Preferences.FocusShortcut, host.ClipRecall.Settings.ToggleShortcut], foreground,
                () => { var area = DailyToolkit.Desktop.Notes.NotesPositionService.WorkAreas()[0]; return ((int)area.X + 20, (int)area.Y + 20); });
            await controller.Ready;
            Require(!model.SetShortcut(host.Gaming.ToggleShortcut, [host.Gaming.ToggleShortcut]), "FilePack accepted an existing tool chord");
            controller.ToggleQuick();
            await WaitForFilePackAsync(() => controller.QuickWindow?.IsVisible == true, "Quick loading window did not open");
            var quick = controller.QuickWindow!;
            quick.UpdateLayout();
            var minimize = (Button)quick.FindName("MinimizeButton"); var close = (Button)quick.FindName("CloseButton");
            var header = (Grid)minimize.Parent;
            var minimizePosition = minimize.TranslatePoint(new Point(), header); var closePosition = close.TranslatePoint(new Point(), header);
            Require(Math.Abs(minimizePosition.X) < 1 && Math.Abs(closePosition.X + close.ActualWidth - header.ActualWidth) < 1 &&
                Math.Abs(minimizePosition.Y - closePosition.Y) < 1, "Quick minimize/close buttons were not at opposite ends of the top row");
            model.SetNotice("");
            var input = Path.Combine(fixture.FullName, "中文 空格.txt"); await File.WriteAllTextAsync(input, "quick packaging fixture");
            await model.AddPathsAsync([input]);
            Require(quick.Content is not null && model.Entries.Count == 1, "Quick loading did not use the shared collector");
            controller.OpenDetails();
            await WaitForFilePackAsync(() => controller.PackWindow?.IsVisible == true, "Detailed packaging window did not open");
            var detail = controller.PackWindow!;
            Require(!quick.IsVisible && detail != quick && detail.Handle != quick.Handle && model.Entries.Count == 1,
                "Detailed packaging reused the quick HWND or lost its files");
            CheckFilePackChrome(detail); detail.Hide();
            controller.ToggleQuick(); await WaitForFilePackAsync(() => quick.IsVisible, "Quick window did not reopen");
            CheckFilePackChrome(quick);
            foreach (var area in DailyToolkit.Desktop.Notes.NotesPositionService.WorkAreas())
            {
                foreach (var window in new[] { quick, detail })
                {
                    window.ShowNearPoint(((int)(area.X + area.Width - 1), (int)(area.Y + area.Height - 1)));
                    var bounds = DailyToolkit.Desktop.Notes.NotesPositionService.Bounds(window.Handle);
                    Require(bounds.X >= area.X && bounds.Y >= area.Y && bounds.X + bounds.Width <= area.X + area.Width &&
                        bounds.Y + bounds.Height <= area.Y + area.Height, "A FilePack window overflowed the target monitor work area");
                }
            }
            detail.Hide();
            Require(model.Entries.Count == 1, "Closing/reopening cleared the shared draft");
            var quickView = (FilePackQuickView)((ContentControl)quick.FindName("Body")).Content;
            var files = (ListBox)quickView.FindName("FilesList"); files.SelectedIndex = 0; quick.UpdateLayout();
            var fileItem = (ListBoxItem)files.ItemContainerGenerator.ContainerFromIndex(0);
            var fileBox = (Border)fileItem.Template.FindName("FileBox", fileItem);
            Require(fileBox.BorderThickness.Left == 1 && ((SolidColorBrush)fileBox.BorderBrush).Color ==
                (Color)ColorConverter.ConvertFromString(model.QuickAppearance.Applied.Accent), "Quick files lacked separate boxes or a selected outline");
            var settingsView = new FilePackSettingsView { DataContext = model };
            var settingsSurface = new Border { Child = settingsView };
            settingsSurface.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            var settingsWindow = new Window { Content = settingsSurface, Width = 800, Height = 1020, ShowInTaskbar = false };
            settingsWindow.Show(); settingsWindow.UpdateLayout();
            try
            {
                host.SelectThemePresetCommand.Execute(host.ThemePresets.Single(value => value.Name == "深海"));
                host.ApplyTheme(settingsWindow.Resources); globalPalette = host.Theme;
                settingsWindow.UpdateLayout();
                CheckFilePackSettingsTheme(settingsView, globalPalette);
                Require(model.Appearances.Count == 2 && model.Appearances.All(value => value == model.QuickAppearance || value == model.PackAppearance),
                    "Settings page still exposed a palette overriding the application theme");
                foreach (var appearance in model.Appearances)
                { appearance.PresetCommand.Execute(appearance.Presets.Single(value => value.Name == "深海")); await appearance.PendingSave; }
                foreach (var (width, height) in new[] { (380d, 320d), (480d, 440d), (720d, 600d) })
                {
                    quick.Width = width; quick.Height = height; quick.UpdateLayout();
                    foreach (var control in FilePackDescendants(quick).OfType<Button>().Where(value => value.IsVisible))
                    {
                        var point = control.TranslatePoint(new Point(), quick);
                        Require(point.X >= 0 && point.Y >= 0 && point.X + control.ActualWidth <= quick.ActualWidth + 1 &&
                            point.Y + control.ActualHeight <= quick.ActualHeight + 1, "Quick window buttons overflowed at a supported size");
                    }
                    foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
                        SaveFilePackFrame(quick, images, $"quick-dark-{width}x{height}-dpi-{scale * 100:0}", scale);
                }
                quick.Width = 480; quick.Height = 440;
                foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
                    SaveFilePackFrame(settingsWindow, images, $"settings-dark-dpi-{scale * 100:0}", scale);
                detail.Show(); detail.UpdateLayout(); SaveFilePackFrame(detail, images, "detail-dark"); detail.Hide();
                Console.WriteLine("PASS FilePack dark UI bounds at three window sizes and 100/125/150/200 percent DPI rendering; actual mixed-DPI transitions not simulated");
                foreach (var appearance in model.Appearances)
                {
                    model.SelectedAppearance = appearance;
                    var preset = appearance.Presets.Single(value => value.Name == "海蓝");
                    settingsWindow.UpdateLayout();
                    var button = FilePackDescendants(settingsView).OfType<Button>().Single(value => ReferenceEquals(value.DataContext, preset));
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    await appearance.PendingSave;
                    Require(appearance.Applied == preset.Palette, "Clicking the preset preview did not apply and save it");
                    var applied = appearance.Applied;
                    appearance.Options.Single(option => option.Key == "Window").Value = "#372349";
                    Require(appearance.Applied == applied && appearance.Draft.Window == "#372349" && appearance.HasChanges,
                        "Custom color was applied before Save");
                    Require(host.Theme == globalPalette, "A FilePack palette modified the application palette");
                    CheckFilePackSettingsTheme(settingsView, globalPalette);
                    if (appearance == model.QuickAppearance)
                    {
                        Require(((SolidColorBrush)quick.Resources["WindowBrush"]).Color == (Color)ColorConverter.ConvertFromString(applied.Window),
                            "Editing the preview modified the actual quick window");
                        Require(((SolidColorBrush)((Border)settingsView.FindName("PalettePreview")).Resources["WindowBrush"]).Color ==
                            (Color)ColorConverter.ConvertFromString("#372349"), "Custom colors did not update the preview");
                    }
                    appearance.SaveCommand.Execute(null); await appearance.PendingSave;
                    Require(appearance.Applied.Window == "#372349" && !appearance.HasChanges, "Saving custom colors did not apply them");
                    appearance.Options.Single(option => option.Key == "Window").Value = "#123456";
                    appearance.DiscardCommand.Execute(null); Require(!appearance.HasChanges, "Discard failed");
                }
                model.SelectedAppearance = model.QuickAppearance;
                model.QuickAppearance.PresetCommand.Execute(model.QuickAppearance.Presets.Single(value => value.Name == "薄荷"));
                await model.QuickAppearance.PendingSave;
                Require(model.PackAppearance.Applied.Window == "#372349",
                    "Selecting one window preset changed the other windows");
                CheckFilePackSettingsTheme(settingsView, globalPalette);
                SaveFilePackFrame(quick, images, "filepack-quick");
                detail.Show(); detail.UpdateLayout(); SaveFilePackFrame(detail, images, "filepack-detail"); detail.Hide();
                SaveFilePackFrame(settingsWindow, images, "filepack-settings");
                settingsWindow.Width = 650; settingsWindow.UpdateLayout();
                Require(settingsView.ActualWidth <= settingsWindow.ActualWidth, "Narrow settings content overflowed the window");
                SaveFilePackFrame(settingsWindow, images, "filepack-settings-narrow");
                // A failed save keeps both the old applied palette and the draft for retry.
                await model.FlushAsync();
                var old = model.QuickAppearance.Applied;
                model.QuickAppearance.Options.Single(option => option.Key == "Window").Value = "#110022";
                using (File.OpenHandle(Path.Combine(module, "preferences.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                { model.QuickAppearance.SaveCommand.Execute(null); await model.QuickAppearance.PendingSave; }
                Require(model.QuickAppearance.Applied == old && model.QuickAppearance.HasChanges && model.HasNotice,
                    "A failed palette write pretended success or discarded the draft");
                model.QuickAppearance.SaveCommand.Execute(null); await model.QuickAppearance.PendingSave;
                Require(!model.QuickAppearance.HasChanges, "Failed palette save could not be retried");
                var savedWindow = model.QuickAppearance.Applied.Window;
                model.QuickAppearance.PresetCommand.Execute(model.QuickAppearance.Presets.Single(value => value.Name == "深海"));
                model.QuickCompression = PackCompression.Maximum; model.MaximumParallel = 3;
                await model.QuickAppearance.PendingSave; await model.PendingOperations;
                var concurrent = await model.Store.LoadPreferencesAsync();
                Require(concurrent.QuickWindowColors["Window"] == model.QuickAppearance.Applied.Window &&
                    concurrent.QuickPack.Compression == PackCompression.Maximum && concurrent.MaximumParallel == 3,
                    "A concurrent preference write reverted a saved palette");
                model.QuickAppearance.Options.Single(option => option.Key == "Window").Value = savedWindow;
                model.QuickAppearance.SaveCommand.Execute(null); await model.QuickAppearance.PendingSave;
            }
            finally { settingsWindow.Close(); }
            model.Mode = PackMode.BuiltIn; model.Format = PackFormat.Tar; model.Compression = PackCompression.Maximum;
            model.ArchiveName = "详细设置"; model.OutputDirectory = Path.Combine(fixture.FullName, "detail-output");
            model.QuickFormat = PackFormat.Zip; model.QuickCompression = PackCompression.Fast;
            model.QuickArchiveName = "快速归档"; model.QuickOutputDirectory = Path.Combine(fixture.FullName, "quick-output");
            Directory.CreateDirectory(model.QuickOutputDirectory);
            var occupied = Path.Combine(model.QuickOutputDirectory, "快速归档.zip"); await File.WriteAllTextAsync(occupied, "keep existing output");
            var task = await model.CreateQuickTaskAsync();
            Require(task is not null && model.Entries.Count == 0 && task.Snapshot.Options.Format == PackFormat.Zip &&
                task.Snapshot.Options.Compression == PackCompression.Fast && task.Snapshot.Options.OutputDirectory == model.QuickOutputDirectory &&
                task.Snapshot.Options.OutputPolicy == PackOutputPolicy.AutoRename, "Quick pack ignored its saved configuration");
            Require(model.Format == PackFormat.Tar && model.ArchiveName == "详细设置" && model.OutputDirectory.EndsWith("detail-output"),
                "Quick pack overwrote the detailed settings");
            await model.SetEnabledAsync(false);
            Require(!quick.IsVisible && !model.CanEditDraft && !model.OpenQuickWindowCommand.CanExecute(null), "Disabled FilePack still accepted quick loading");
            await WaitForFilePackAsync(() => model.Tasks.Single(row => row.TaskId == task!.TaskId).Info.State is PackState.Completed or PackState.Failed,
                "Disabling FilePack stopped a submitted task");
            var finished = model.Tasks.Single(row => row.TaskId == task!.TaskId).Info;
            Require(finished.State == PackState.Completed && finished.OutputPath != occupied && await File.ReadAllTextAsync(occupied) == "keep existing output",
                "Quick packing failed or replaced an existing output: " + finished.Error);
            using (var archive = ZipFile.OpenRead(finished.OutputPath)) Require(archive.Entries.Single().FullName == "中文 空格.txt", "Quick ZIP manifest changed");
            Require(await File.ReadAllTextAsync(input) == "quick packaging fixture", "Quick pack modified its source");
            await model.FlushAsync();
            await using (var restored = new FilePackViewModel(new FilePackStore(module)))
            {
                await restored.SettingsReady;
                Require(!restored.IsEnabled && restored.QuickFormat == PackFormat.Zip && restored.QuickArchiveName == "快速归档" &&
                    restored.QuickOutputDirectory == model.QuickOutputDirectory && restored.QuickAppearance.Applied.Window == "#110022" &&
                    restored.PackAppearance.Applied.Window == "#372349",
                    "Tool switch, quick options or independent palettes did not survive restart");
            }
            var legacyStore = new FilePackStore(Path.Combine(fixture.FullName, "legacy-colors"));
            await legacyStore.SavePreferencesAsync(new FilePackPreferences(2, "")
                { SettingsColors = new() { ["Window"] = "#FFFFFF", ["Text"] = "#000000" } });
            await using (var legacyModel = new FilePackViewModel(legacyStore))
            {
                await legacyModel.SettingsReady;
                var legacyView = new FilePackSettingsView { DataContext = legacyModel };
                var legacyWindow = new Window { Content = legacyView, Width = 800, Height = 800, ShowInTaskbar = false };
                host.ApplyTheme(legacyWindow.Resources); legacyWindow.Show(); legacyWindow.UpdateLayout();
                try { CheckFilePackSettingsTheme(legacyView, host.Theme); }
                finally { legacyWindow.Close(); }
                legacyModel.QuickAppearance.PresetCommand.Execute(legacyModel.QuickAppearance.Presets.Single(value => value.Name == "深海"));
                await legacyModel.QuickAppearance.PendingSave;
                var legacyPreferences = await legacyStore.LoadPreferencesAsync();
                Require(legacyPreferences.SettingsColors["Window"] == "#FFFFFF" && legacyPreferences.SettingsColors["Text"] == "#000000",
                    "Saving a window palette destroyed older settings-page color data");
                Console.WriteLine("PASS Older settings-page colors no longer override the application theme and remain preserved in storage");
            }
            Require(await model.SetEnabledAsync(true), "FilePack could not be re-enabled");
            await model.ShutdownAsync();
            Console.WriteLine("PASS FilePack switch, separate quick/detail/settings views, shared draft, independent saved quick ZIP settings, auto-rename, source preservation, two window palette drafts/presets/failure/restart, application-themed settings and native chrome");
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
        await CheckFilePackBrokerAsync();
        CheckFilePackContextMenu();
    }
    private static void CheckFilePackSettingsTheme(FilePackSettingsView view, ThemePalette palette)
    {
        var scroll = (ScrollViewer)view.Content;
        var card = FilePackDescendants(view).OfType<Border>().First(value => ReferenceEquals(value.Style, view.FindResource("Card")));
        Require((scroll.Background is null || scroll.Background == Brushes.Transparent) &&
            ((SolidColorBrush)card.Background).Color == (Color)ColorConverter.ConvertFromString(palette.Surface) &&
            ((SolidColorBrush)view.Foreground).Color == (Color)ColorConverter.ConvertFromString(palette.Text),
            "FilePack settings background, cards or text did not follow the application palette");
    }
    private static void CheckFilePackShortcutNotices(FilePackViewModel model, FilePackController controller)
    {
        var free = new KeyboardShortcut(3, 0x84, "Ctrl + Alt + F21");
        var occupied = new KeyboardShortcut(3, 0x85, "Ctrl + Alt + F22");
        var accepted = model.SetShortcut(free, []);
        Require(accepted && controller.IsHotkeyRegistered && !model.HasNotice,
            $"Changing a rejected shortcut to a free chord failed: accepted={accepted}, registered={controller.IsHotkeyRegistered}, notice={model.Notice}");
        using var source = ClipboardTestWindow();
        using var competitor = new LensShortcutController(source, hotkeyId: 0xB042);
        Require(competitor.Configure(occupied, LensActivationMode.Toggle), "Shortcut notice fixture could not register its test chord");
        Require(model.SetShortcut(occupied, []) && !controller.IsHotkeyRegistered && model.Notice.Contains("被占用", StringComparison.Ordinal),
            "A native FilePack registration conflict did not produce a notice");
        Require(model.SetShortcut(free, []) && controller.IsHotkeyRegistered && !model.HasNotice,
            "A successful replacement left the native conflict notice");
        model.SetShortcut(occupied, []); competitor.Suspend(true); controller.Suspend(false);
        Require(controller.IsHotkeyRegistered && !model.HasNotice, "A successful resume left the stale conflict notice");
        const string unrelated = "配色未能保存，请检查空间或权限。";
        model.SetNotice(unrelated); controller.Suspend(true); controller.Suspend(false);
        Require(controller.IsHotkeyRegistered && model.Notice == unrelated, "Shortcut recovery cleared an unrelated storage error");
        model.SetShortcut(free, []); Require(competitor.Suspend(false), "Shortcut fixture could not reoccupy its chord");
        model.SetShortcut(occupied, []);
        Require(model.HasNotice && model.SetShortcut(null, []) && !controller.IsHotkeyRegistered && !model.HasNotice,
            "Clearing the shortcut left its conflict notice");
        model.SetShortcut(free, []);
        Console.WriteLine("PASS FilePack native conflict notices clear after replacement, resume or clearing; unrelated errors retained; no input simulation");
    }
    private static async Task CheckFilePackShortcutNoticesAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-shortcut-");
        try
        {
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            using var host = new MainViewModel(new ControlledProbe(display), display, new LocalProbe(),
                favoritesStore: new FavoritesStore(Path.Combine(fixture.FullName, "favorites.json")),
                gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(fixture.FullName, "gaming.json")),
                navigationStore: new NavigationOrderStore(Path.Combine(fixture.FullName, "navigation.json")),
                appPreferencesStore: new AppPreferencesStore(Path.Combine(fixture.FullName, "app.json")), startupRegistration: new StartupFake(),
                notesStore: IsolatedNotes(), clipboardSettingsStore: IsolatedClipboard(), filePackStore: new FilePackStore(Path.Combine(fixture.FullName, "module")));
            var model = host.FilePack; await model.SettingsReady;
            model.SetShortcut(new(3, 0x84, "Ctrl + Alt + F21"), []);
            using var source = ClipboardTestWindow();
            using var controller = new FilePackController(host, source, () => [host.Gaming.ToggleShortcut]);
            await controller.Ready;
            Require(!model.SetShortcut(host.Gaming.ToggleShortcut, [host.Gaming.ToggleShortcut]), "Reserved shortcut was accepted");
            CheckFilePackShortcutNotices(model, controller);
            await model.ShutdownAsync();
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
    }
    private static void CheckFilePackChrome(FilePackWindow window)
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        Require(window.ResizeMode == ResizeMode.CanResize && chrome.ResizeBorderThickness.Left == 6, "FilePack resize frame was lost");
        foreach (var name in new[] { "MinimizeButton", "CloseButton" })
        {
            var button = (Button)window.FindName(name); var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var buttonPoint = ((uint)(int)center.X & 0xffff) | (((uint)(int)center.Y & 0xffff) << 16);
            Require(ClipboardChromeHit(window.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)buttonPoint))).ToInt32() == 1,
                "FilePack caption button was treated as a draggable surface");
        }
        var point = window.PointToScreen(new Point(window.ActualWidth / 2, 3));
        var packed = ((uint)(int)point.X & 0xffff) | (((uint)(int)point.Y & 0xffff) << 16);
        Require(ClipboardChromeHit(window.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)packed))).ToInt32() == 12,
            "FilePack top edge was not resizable");
        point = window.PointToScreen(new Point(window.ActualWidth / 2, 24));
        packed = ((uint)(int)point.X & 0xffff) | (((uint)(int)point.Y & 0xffff) << 16);
        Require(ClipboardChromeHit(window.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)packed))).ToInt32() == 2,
            "FilePack header was not draggable");
        foreach (var (local, expected) in new[]
        {
            (new Point(3, window.ActualHeight / 2), 10), (new Point(window.ActualWidth - 3, window.ActualHeight / 2), 11),
            (new Point(window.ActualWidth / 2, window.ActualHeight - 3), 15),
            (new Point(10, window.ActualHeight / 2), 2), (new Point(window.ActualWidth - 10, window.ActualHeight / 2), 2),
            (new Point(window.ActualWidth / 2, window.ActualHeight - 10), 2)
        })
        {
            point = window.PointToScreen(local); packed = ((uint)(int)point.X & 0xffff) | (((uint)(int)point.Y & 0xffff) << 16);
            Require(ClipboardChromeHit(window.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked((int)packed))).ToInt32() == expected,
                $"FilePack frame hit at {local} did not provide resize/drag behavior ({expected})");
        }
    }
    private static void SaveFilePackFrame(Window window, string directory, string name, double scale = 1)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> FilePackDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); yield return child; foreach (var descendant in FilePackDescendants(child)) yield return descendant; }
    }
    private static async Task CheckFilePackBrokerAsync()
    {
        var name = "DailyToolkit.FilePack.Tests." + Guid.NewGuid().ToString("N");
        var paths = Enumerable.Range(0, 12).Select(index => Path.GetFullPath($"fixture {index}.txt")).ToArray();
        var received = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using var broker = new FilePackRequestBroker(items => { foreach (var path in items) received.TryAdd(path, true); return Task.CompletedTask; }, name);
        Require(FilePackRequestBroker.TryReadArguments(["--file-pack-load", paths[0], paths[0]], out var normalized) && normalized.Length == 1 &&
            !FilePackRequestBroker.TryReadArguments(["--file-pack-load", "relative.txt"], out _) &&
            !FilePackRequestBroker.TryReadArguments(["--file-pack-load", "--exit"], out _), "Explorer arguments accepted invalid paths or commands");
        var results = await Task.WhenAll(paths.Select(path => FilePackRequestBroker.SendAsync([path], name)));
        Require(results.All(value => value), "Concurrent Explorer requests were not acknowledged");
        await WaitForFilePackAsync(() => received.Count == paths.Length, "Explorer burst lost selected files");
        await using (var invalid = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        { await invalid.ConnectAsync(2000); await invalid.WriteAsync(BitConverter.GetBytes(2 * 1024 * 1024)); await invalid.FlushAsync(); }
        Require(await FilePackRequestBroker.SendAsync([paths[0]], name), "An oversized request stopped subsequent Explorer requests");
        Console.WriteLine("PASS FilePack same-user local IPC: argument validation, concurrent selection bursts, bounded request rejection, subsequent recovery and disposal; no Explorer or registry changes");
    }
    private sealed class FilePackMenuFake : IFilePackMenuRegistry
    {
        internal Dictionary<string, Dictionary<string, string>> Values { get; } = [];
        internal string? FailOnce { get; set; }
        public Dictionary<string, string>? Read(string path) => Values.TryGetValue(path, out var values) ? new(values) : null;
        public void Write(string path, Dictionary<string, string> values)
        {
            if (path == FailOnce) { FailOnce = null; throw new IOException("fixture denied write"); }
            Values[path] = new(values);
        }
        public void Delete(string path)
        { foreach (var key in Values.Keys.Where(key => key == path || key.StartsWith(path + "\\", StringComparison.Ordinal)).ToArray()) Values.Remove(key); }
    }
    private static void CheckFilePackContextMenu()
    {
        var registry = new FilePackMenuFake();
        var menu = new FilePackContextMenuRegistration(registry, executable: System.Environment.ProcessPath);
        Require(!menu.IsRegistered && registry.Values.Count == 0, "Reading context menu state wrote registration");
        menu.SetEnabled(true);
        Require(menu.IsRegistered && registry.Values.Count == 4 && registry.Values.Keys.All(key => key.StartsWith(@"Software\Classes\", StringComparison.Ordinal)),
            "Context menu did not register only the per-user file and folder verbs");
        Require(registry.Values.Where(pair => pair.Key.EndsWith(@"\command", StringComparison.Ordinal)).All(pair =>
            pair.Value[""].StartsWith("\"" + System.Environment.ProcessPath + "\" --file-pack-load \"", StringComparison.Ordinal) && pair.Value[""].EndsWith("%1\"", StringComparison.Ordinal)),
            "Context menu executable/selection arguments were not quoted correctly");
        menu.SetEnabled(false); Require(!menu.IsRegistered && registry.Values.Count == 0, "Removing context menu left owned entries");
        var directory = @"Software\Classes\Directory\shell\DailyToolkit.FilePack";
        registry.FailOnce = directory + @"\command";
        var failed = false; try { menu.SetEnabled(true); } catch (IOException) { failed = true; }
        Require(failed && registry.Values.Count == 0, "Partial menu registration did not roll back");
        registry.Values[directory] = new() { ["DailyToolkitOwner"] = "another-tool", [""] = "keep" };
        failed = false; try { menu.SetEnabled(true); } catch (IOException) { failed = true; }
        menu.SetEnabled(false);
        Require(failed && registry.Values.Count == 1 && registry.Values[directory][""] == "keep", "Menu removal changed an unowned entry");
        Console.WriteLine("PASS FilePack context-menu ownership, per-user paths, quoted command, file/folder verbs, partial-write rollback and removal using an isolated registry adapter");
    }
}

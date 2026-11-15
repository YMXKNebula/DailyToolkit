using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Controls;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckDisabledToolSettingsAsync(MainViewModel model, MainWindow window,
        NotesSettingsView notes, ClipboardSettingsView clipboard, FilePackSettingsView pack)
    {
        // The caller owns an isolated store and a window without tool runtimes or registered shortcuts.
        var noteData = (Border)notes.FindName("DataManagementCard");
        var clipboardData = (Border)clipboard.FindName("DataManagementCard");
        var lens = (Border)window.FindName("LensSettingsCard");
        var lensColor = (LensColorPicker)window.FindName("LensFrameColor");
        var customLensColor = (Button)lensColor.FindName("CustomColorButton");
        var lensMenu = customLensColor.ContextMenu!;
        lensMenu.PlacementTarget = customLensColor;
        var more = (Button)clipboard.FindName("DataManagementMoreButton");
        var menu = more.ContextMenu!;
        menu.PlacementTarget = more;
        var reset = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "恢复默认设置");
        var recovery = menu.Items.OfType<MenuItem>().Where(item => !ReferenceEquals(item, reset)).ToArray();
        var noteActions = FilePackDescendants(noteData).OfType<Button>().ToArray();
        var noteReset = noteActions.Single(button => (string)button.Content == "恢复默认设置");
        var saveNote = noteActions.Single(button => (string)button.Content == "立即保存");
        var pages = new (string Id, FrameworkElement View, Border? Data)[]
        {
            ("screen-lens", lens, null), ("floating-notes", notes, noteData),
            ("clip-recall", clipboard, clipboardData), ("file-pack", pack, null)
        };

        await SetEnabledAsync(true);
        var states = new Dictionary<Control, bool>();
        foreach (var page in pages)
        {
            model.Page = page.Id; await LayoutAsync();
            var controls = SettingsControls(page.View, page.Data).ToArray();
            Require(controls.Length > 5 && controls.Any(control => control.IsEnabled),
                $"Enabled {page.Id} settings were inaccessible");
            foreach (var control in controls) states[control] = control.IsEnabled;
        }
        Require(reset.IsEnabled && noteReset.IsEnabled && lensMenu.IsEnabled, "Enabled tools could not reset their settings");
        model.Gaming.PreviewBorderColor = "#8350A4";
        model.Notes.PreviewBackgroundColor = "#8350A4";
        var noteColor = model.Notes.Preferences.Background.Color;
        var clipboardColor = model.ClipRecall.Appearance;
        var clipboardDraft = model.ClipRecall.AppearanceColors.First().Value;
        model.FilePack.SelectedAppearance.Options.First().Value = "#8350A4";
        var packDraft = model.FilePack.SelectedAppearance.Options.First().Value;

        await SetEnabledAsync(false);
        foreach (var page in pages)
        {
            model.Page = page.Id; await LayoutAsync();
            var editable = SettingsControls(page.View, page.Data).Where(control => control.IsEnabled).ToArray();
            Require(editable.Length == 0,
                $"Disabled {page.Id} still allowed editing: {string.Join(", ", editable.Select(control => $"{control.GetType().Name}/{control.Name}"))}");
            if (page.View is not Border)
                Require(FilePackDescendants(page.View).OfType<ScrollViewer>().First().IsEnabled,
                    $"Disabled {page.Id} blocked page scrolling");
            Require(FilePackDescendants((ItemsControl)window.FindName("NavigationList")).OfType<FeatureSwitch>()
                .Where(control => control.DataContext is NavigationItem { CanToggle: true }).All(control => control.IsEnabled) &&
                ((Button)window.FindName("ToolFavoriteButton")).IsEnabled, "Disabling settings blocked re-enabling or favorites");
        }
        model.Page = "screen-lens";
        Require(!model.RefreshCommand.CanExecute(null), "Disabled magnifier allowed refreshing its configuration");
        Require(!reset.IsEnabled && !noteReset.IsEnabled && !lensMenu.IsEnabled && recovery.All(item => item.IsEnabled) &&
            noteActions.Where(button => !ReferenceEquals(button, noteReset)).All(button => button.IsEnabled),
            "Disabling settings blocked recovery/data actions or left a reset entry available");

        // Saving existing note data while off must not apply the retained custom-color draft.
        saveNote.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await model.Notes.FlushAsync(); await LayoutAsync();
        Require(model.Notes.HasColorChanges && model.Notes.PreviewBackgroundColor == "#8350A4" &&
            model.Notes.Preferences.Background.Color == noteColor, "Saving note data while disabled applied or lost a color draft");

        model.Page = "format-conversion"; await LayoutAsync();
        var conversionHost = (ContentControl)window.FindName("ConversionHost");
        Require(FilePackDescendants(conversionHost).OfType<ComboBox>().All(box => !box.IsEnabled) &&
            FilePackDescendants(conversionHost).OfType<TextBox>().All(box => !box.IsEnabled),
            "Conversion options could be edited while disabled");
        await RenderDisabledSettingsAsync();

        await SetEnabledAsync(true);
        foreach (var page in pages)
        {
            model.Page = page.Id; await LayoutAsync();
            Require(SettingsControls(page.View, page.Data).All(control => !states.TryGetValue(control, out var enabled) || control.IsEnabled == enabled ||
                control is Button { Content: "保存配色" or "撤销修改" }),
                $"Re-enabling {page.Id} lost its original editing/command restrictions");
        }
        model.Page = "screen-lens";
        Require(model.RefreshCommand.CanExecute(null) && reset.IsEnabled && noteReset.IsEnabled &&
            model.ClipRecall.Appearance == clipboardColor && model.ClipRecall.AppearanceColors.First().Value == clipboardDraft &&
            model.FilePack.SelectedAppearance.Options.First().Value == packDraft && model.Notes.HasColorChanges &&
            model.Gaming.PreviewBorderColor == "#8350A4" && model.Gaming.HasBorderColorChanges,
            "Toggle cycle failed to restore settings access or preserved drafts were applied/discarded");
        await SetEnabledAsync(false);
        Console.WriteLine("PASS All disabled tools lock settings, shortcuts, resets and presets; navigation, data recovery, draft retention and re-enable remain available; light/dark 100/150/200% renders retained");

        async Task SetEnabledAsync(bool enabled)
        {
            model.Gaming.IsEnabled = enabled; model.Notes.IsEnabled = enabled;
            model.ClipRecall.RequestEnabled(enabled); await model.ClipRecall.PendingSettings;
            Require(await model.FilePack.SetEnabledAsync(enabled), "FilePack fixture could not change enabled state");
            model.Conversion.SetEnabled(enabled); await LayoutAsync();
        }
        async Task LayoutAsync() => await Dispatcher.CurrentDispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
        async Task RenderDisabledSettingsAsync()
        {
            var directory = Path.GetFullPath(".local/quality-checks/disabled-settings");
            var originalTheme = model.Theme;
            var originalWidth = window.Width; var originalHeight = window.Height;
            var contact = new DrawingVisual();
            using var drawing = contact.RenderOpen();
            var tile = 0;
            try
            {
                foreach (var dark in new[] { false, true })
                {
                    (dark ? model.DarkThemeCommand : model.LightThemeCommand).Execute(null);
                    model.ApplyTheme(Application.Current.Resources);
                    foreach (var width in new[] { 850d, 1200d })
                    foreach (var id in pages.Select(page => page.Id).Append("format-conversion"))
                    {
                        window.Width = width; window.Height = 820; model.Page = id; await LayoutAsync();
                        foreach (var scale in new[] { 1d, 1.5, 2d })
                            SaveFilePackFrame(window, directory, $"{id}-{(dark ? "dark" : "light")}-{width}-dpi-{scale * 100:0}", scale);
                        if (dark && width == 1200)
                        {
                            var content = (FrameworkElement)window.Content;
                            var frame = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            frame.Render(content);
                            drawing.DrawImage(frame, new Rect(tile % 2 * 480, tile / 2 * 328, 480, 328)); tile++;
                        }
                    }
                }
            }
            finally
            {
                model.SelectThemePresetCommand.Execute(model.ThemePresets.First(preset => preset.Palette == originalTheme));
                model.ApplyTheme(Application.Current.Resources);
                window.Width = originalWidth; window.Height = originalHeight;
            }
            drawing.Close();
            var board = new RenderTargetBitmap(960, 984, 96, 96, PixelFormats.Pbgra32); board.Render(contact);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(board));
            using var preview = new MemoryStream(); encoder.Save(preview);
            File.WriteAllBytes(Path.Combine(directory, "overview-dark.png"), preview.ToArray());
            if (System.Environment.GetEnvironmentVariable("CI") == "true")
                Console.WriteLine("VISUAL_DISABLED_SETTINGS_PNG=" + Convert.ToBase64String(preview.ToArray()));
        }
    }

    private static IEnumerable<Control> SettingsControls(DependencyObject view, DependencyObject? data)
    {
        var excluded = data is null ? new HashSet<DependencyObject>() : FilePackDescendants(data).ToHashSet();
        // Scrollbar arrows remain available for navigating disabled settings.
        foreach (var scrollbar in FilePackDescendants(view).OfType<ScrollBar>())
            excluded.UnionWith(FilePackDescendants(scrollbar));
        return FilePackDescendants(view).OfType<Control>()
            .Where(control => !excluded.Contains(control) && control is ButtonBase or TextBox or Selector or Slider);
    }
}

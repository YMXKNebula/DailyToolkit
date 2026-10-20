using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckShortcutCatalogAsync(string? images = null)
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-shortcut-catalog-");
        MainWindow? window = null;
        MainViewModel? model = null;
        try
        {
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            var probe = new ControlledProbe(display); probe.Finish.TrySetResult();
            var appStore = new AppPreferencesStore(Path.Combine(fixture.FullName, "settings.json"));
            appStore.Save(new() { Theme = ThemePalette.Dark, PageAnimationsEnabled = false, CloseToTray = false });
            model = new(probe, display, new LocalProbe(),
                favoritesStore: new(Path.Combine(fixture.FullName, "favorites.json")),
                gamingPreferencesStore: new(Path.Combine(fixture.FullName, "gaming.json")),
                navigationStore: new(Path.Combine(fixture.FullName, "navigation.json")), appPreferencesStore: appStore,
                startupRegistration: new StartupFake(), notesStore: new(Path.Combine(fixture.FullName, "notes")),
                clipboardSettingsStore: new(Path.Combine(fixture.FullName, "clipboard")),
                filePackStore: new(Path.Combine(fixture.FullName, "filepack")));
            await Task.WhenAll(model.Notes.Ready, model.ClipRecall.Ready, model.FilePack.Ready);
            var catalog = model.Shortcuts;
            Require(catalog.Rows.Count == 5 && catalog.Rows.Select(row => row.Id).Distinct().Count() == 5 &&
                catalog.WindowRows.All(row => row.Tool.Length > 0 && row.Action.Length > 0 && row.ShortcutText.Length > 0),
                "The shortcut overview omitted configured tools or window actions");
            var original = model.Gaming.ToggleShortcut!;
            model.Notes.Update(model.Notes.Preferences with { ToggleShortcut = original, IsEnabled = false });
            Require(catalog.LensNotice.Contains("浮笺") && catalog.LensNotice.Contains("已停用") &&
                catalog.NotesToggleNotice.Contains("放大镜") && catalog.Rows[1].StatusText == "功能已停用",
                "Disabled-tool duplicates were not shown immediately in both locations");
            model.Notes.Update(model.Notes.Preferences with { ToggleShortcut = null });
            Require(!catalog.HasLensNotice && catalog.Rows[1].ShortcutText == "未设置", "Clearing a duplicate left a stale notice");
            var unchangedFilePack = model.FilePack.ToggleShortcut;
            catalog.ReportAssignmentAttempt(ShortcutIds.FilePackToggle, original, accepted: false);
            Require(catalog.FilePackNotice.Contains("未更改") && catalog.FilePackNotice.Contains("放大镜") &&
                model.FilePack.ToggleShortcut == unchangedFilePack, "A rejected assignment did not show beside the unchanged saved shortcut");
            catalog.ReportAssignmentAttempt(ShortcutIds.FilePackToggle, unchangedFilePack, accepted: true);
            Require(!catalog.HasFilePackNotice, "A successful assignment retained a rejected attempt notice");
            var windows = new KeyboardShortcut(8, 0x52, "Win + R");
            Require(model.Gaming.SetShortcut(windows) && catalog.LensNotice.Contains("Windows") && catalog.LensNotice.Contains("运行"),
                "Known Windows combinations were not shown on the current shortcut");
            Require(ShortcutCatalog.WindowsShortcutNotice(new(0, 0x7B, "F12")).Contains("调试器") &&
                ShortcutCatalog.WindowsShortcutNotice(new(6, 0x1B, "Ctrl + Shift + Esc")).Contains("任务管理器") &&
                ShortcutCatalog.WindowsShortcutNotice(new(8, 0x87, "Win + F24")).Contains("保留") &&
                ShortcutCatalog.WindowsShortcutNotice(original) == "", "Windows checks treated an unknown non-Win chord as occupied");
            model.Gaming.SetShortcut(original);
            catalog.ReportRegistration(ShortcutIds.LensToggle, windows, ShortcutRegistrationState.Failed, 1409);
            Require(!catalog.HasLensNotice, "A stale registration result overwrote a newer shortcut");
            catalog.ReportRegistration(ShortcutIds.LensToggle, original, ShortcutRegistrationState.Failed, 1409);
            Require(catalog.LensNotice.Contains("已被占用") && catalog.Rows[0].StatusText == "未能启用", "Occupied registration was not reported");
            catalog.ReportRegistration(ShortcutIds.LensToggle, original, ShortcutRegistrationState.Failed, 1459);
            Require(catalog.LensNotice.Contains("注册失败") && !catalog.LensNotice.Contains("已被占用"),
                "A registration permission failure was misreported as an occupied shortcut");
            catalog.ReportRegistration(ShortcutIds.LensToggle, original, ShortcutRegistrationState.Paused);
            Require(!catalog.HasLensNotice && catalog.Rows[0].StatusText == "编辑时暂停", "Shortcut editing created a false conflict");
            catalog.ReportRegistration(ShortcutIds.LensToggle, original, ShortcutRegistrationState.Registered);
            Require(!catalog.HasLensNotice && catalog.Rows[0].StatusText == "已启用", "Successful registration left a failed state");
            catalog.ReportRegistration(ShortcutIds.LensToggle, null, ShortcutRegistrationState.NotRegistered);
            Require(catalog.Rows[0].StatusText == "未启用", "A controller declining an internally conflicting shortcut retained a stale registered state");
            model.Gaming.IsEnabled = false;
            Require(catalog.Rows[0].StatusText == "功能已停用" && !catalog.HasLensNotice, "Turning a tool off retained its registration failure");
            model.Gaming.IsEnabled = true;
            Require(catalog.Rows[0].StatusText == "等待注册", "Re-enabling reused a stale registration success");
            model.ClipRecall.SetShortcut(original, []);
            Require(catalog.ClipboardNotice.Contains("放大镜") && catalog.LensNotice.Contains("剪贴回溯"),
                "Clipboard shortcut edits did not refresh duplicate hints");
            model.Page = "settings-shortcuts";
            Require(model.ShowShortcutSettings && model.IsSettings && model.PageTitle == "快捷键" &&
                model.NavigationItems.Count == 5 && !model.ShowRuntimeSettings && !model.ShowAboutSettings,
                "Shortcut settings did not participate in existing settings navigation");
            Require(!probe.DetailsStarted.Task.IsCompleted && !File.Exists(model.ClipRecall.Repository.DatabasePath),
                "The shortcut overview started hardware probing or opened the disabled clipboard repository");
            Console.WriteLine("PASS Shortcut catalog includes disabled tools; live duplicate/Windows hints, registration result/error distinction and setting navigation without polling or input");

            if (images is not null)
            {
                window = new MainWindow(model, enableShortcuts: false)
                { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                window.Show();
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                var panel = (FrameworkElement)window.FindName("ShortcutSettingsPanel");
                foreach (var size in new[] { new Size(680, 620), new Size(1040, 800) })
                {
                    window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                    Require(panel.IsVisible && panel.ActualWidth > 100 && panel.ActualWidth <= window.ActualWidth,
                        "The shortcut overview overflowed a narrow window");
                    Require(((SolidColorBrush)window.Foreground).Color == ((SolidColorBrush)window.FindResource("TextBrush")).Color,
                        "The shortcut overview lost the shared dark theme");
                    foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                    {
                        var root = window.PreviewContent;
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
                            (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        Directory.CreateDirectory(images);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var target = File.Create(Path.Combine(images, $"shortcuts-dark-{size.Width}-{scale * 100:0}.png"));
                        encoder.Save(target);
                    }
                }
                var scrollParent = VisualTreeHelper.GetParent(panel);
                while (scrollParent is not null and not ScrollViewer) scrollParent = VisualTreeHelper.GetParent(scrollParent);
                Require(scrollParent is ScrollViewer, "Shortcut settings did not reuse the settings page scroll viewer");
                var scroll = (ScrollViewer)scrollParent!;
                scroll.ScrollToEnd();
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                SaveImage("shortcuts-dark-bottom", 1.5);
                Require(VisibleText(panel).Any(text => text.Text == "窗口内快捷键") &&
                    VisibleText(panel).Any(text => text.Text == "确认 / 取消路径修改"), "Window shortcut actions were missing from the overview");

                foreach (var page in new[] { "screen-lens", "floating-notes", "clip-recall", "file-pack" })
                {
                    model.Page = page;
                    if (page == "screen-lens") model.Gaming.SetShortcut(windows);
                    if (page == "floating-notes") model.Notes.Update(model.Notes.Preferences with { ToggleShortcut = windows });
                    if (page == "clip-recall") model.ClipRecall.SetShortcut(windows, []);
                    if (page == "file-pack") model.FilePack.SetShortcut(windows, []);
                    await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    var notice = page switch
                    {
                        "screen-lens" => catalog.LensNotice, "floating-notes" => catalog.NotesToggleNotice,
                        "clip-recall" => catalog.ClipboardNotice, _ => catalog.FilePackNotice
                    };
                    Require(notice.Contains("Windows") && VisibleText(window.PreviewContent).Any(text => text.Text == notice),
                        page + " did not show the shared Windows/duplicate hint beside its shortcut");
                }
                Console.WriteLine("PASS Each tool shortcut displays the shared live hint beside its editor; all window actions are present at the overview bottom");
                Console.WriteLine("PASS Shortcut overview shared theme at two sizes and 100/150/200 percent rendering (not physical mixed-DPI validation)");

                void SaveImage(string name, double scale)
                {
                    var root = window.PreviewContent;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
                        (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var target = File.Create(Path.Combine(images, name + ".png")); encoder.Save(target);
                }
                static IEnumerable<TextBlock> VisibleText(DependencyObject root)
                {
                    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                    {
                        var child = VisualTreeHelper.GetChild(root, index);
                        if (child is TextBlock { IsVisible: true } text) yield return text;
                        foreach (var nested in VisibleText(child)) yield return nested;
                    }
                }
            }
            await model.Notes.FlushAsync(); await model.ClipRecall.FlushSettingsAsync(); await model.FilePack.FlushAsync();
        }
        finally
        {
            if (window is not null) { window.Close(); await Task.Delay(100); }
            else model?.Dispose();
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(fixture.FullName).StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                fixture.Name.StartsWith("DailyToolkit-shortcut-catalog-", StringComparison.Ordinal)) fixture.Delete(recursive: true);
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void SaveClipboardVisual(FrameworkElement visual, string directory, string name, bool contentOnly = false)
    {
        visual.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        if (contentOnly)
        {
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            bitmap.Render(drawing);
        }
        else bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static async Task CheckClipboardVisualsAsync(string images)
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-visual-");
        await using var model = new ClipboardHistoryViewModel(new(directory.FullName));
        ClipboardHistoryPanel? panel = null; Window? settingsWindow = null;
        try
        {
            await model.Ready; await model.EnsureRepositoryAsync();
            var now = DateTimeOffset.UtcNow;
            var note = await model.Repository.CaptureAsync(ClipboardContent.FromText("周末采购清单\n牛奶、鸡蛋、面包\n\n这条记录保存了原始换行，可展开查看完整内容。"), now, "Notepad", model.Settings);
            await model.Repository.UpdateNoteAsync(note.Id, "每周六确认冰箱库存"); await model.Repository.SetFlagsAsync([note.Id], true, true, model.Settings);
            await model.Repository.CaptureAsync(ClipboardContent.FromText("https://example.com/docs"), now.AddSeconds(1), "Browser", model.Settings);
            await model.Repository.CaptureAsync(ClipboardContent.FromFiles(["D:\\工作资料\\项目说明.docx", "D:\\工作资料\\参考图片"]), now.AddSeconds(2), "Explorer", model.Settings);
            for (var index = 0; index < 16; index++) await model.Repository.CaptureAsync(ClipboardContent.FromText($"演示记录 {index + 1}：待办事项与临时文字"), now.AddSeconds(index + 3), null, model.Settings);
            var image = await model.Repository.CaptureAsync(SyntheticClipboardImage(alpha: false), now.AddSeconds(20), "图像编辑器", model.Settings);
            await model.Repository.CaptureAsync(ClipboardContent.FromText("https://example.com/docs"), now.AddSeconds(21), "Browser", model.Settings);
            await model.Repository.CaptureAsync(ClipboardContent.FromFiles(["D:\\工作资料\\项目说明.docx", "D:\\工作资料\\参考图片"]), now.AddSeconds(22), "Explorer", model.Settings);
            model.IsVisible = true; await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 20 && model.HasSelection));
            panel = new(model); model.ApplyAppearance(panel.Resources);
            var searchFocusCount = 0; panel.SearchInput.GotKeyboardFocus += (_, _) => searchFocusCount++;
            var areas = NotesPositionService.WorkAreas();
            foreach (var area in areas)
            {
                panel.ShowNearPoint(((int)(area.X + area.Width - 2), (int)(area.Y + area.Height - 2)));
                await panel.Dispatcher.InvokeAsync(panel.UpdateLayout, DispatcherPriority.ContextIdle);
                var bounds = NotesPositionService.Bounds(panel.Handle);
                var dpi = VisualTreeHelper.GetDpi(panel);
                Console.WriteLine($"INFO Clipboard monitor work area={area.X},{area.Y},{area.Width},{area.Height}, window DPI={dpi.PixelsPerInchX:0}/{dpi.PixelsPerInchY:0}");
                Require(bounds.X >= area.X && bounds.Y >= area.Y && bounds.X + bounds.Width <= area.X + area.Width + 1 &&
                    bounds.Y + bounds.Height <= area.Y + area.Height + 1, "Clipboard panel escaped monitor work area");
                CheckClipboardChrome(panel);
            }
            panel.ActivatePanel();
            Require(!panel.SearchInput.IsKeyboardFocusWithin && searchFocusCount == 0, "Opening panel activated search input");
            var clearSearch = (Button)panel.FindName("ClearSearchButton");
            Require(clearSearch.Visibility == Visibility.Collapsed, "Empty search exposed clear button");
            model.Search = "采购";
            await panel.Dispatcher.InvokeAsync(panel.UpdateLayout, DispatcherPriority.ContextIdle);
            Require(clearSearch.Visibility == Visibility.Visible && clearSearch.BorderThickness == new Thickness(0), "Nonempty search did not expose borderless clear action");
            SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-search");
            clearSearch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(model.Search == "", "Search clear did not clear its bound filter");
            await panel.Dispatcher.InvokeAsync(panel.UpdateLayout, DispatcherPriority.ContextIdle);
            Require(clearSearch.Visibility == Visibility.Collapsed, "Clear action remained visible after clearing search");
            var previousSearchFocus = searchFocusCount;
            panel.Hide(); panel.ShowNearPoint(((int)areas[0].X + 100, (int)areas[0].Y + 100)); panel.ActivatePanel();
            Require(!panel.SearchInput.IsKeyboardFocusWithin && searchFocusCount == previousSearchFocus, "Reopening panel restored search focus");
            var beforeWidth = panel.Width; panel.Width = 610; panel.Height = 650;
            ((Button)panel.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(panel.WindowState == WindowState.Minimized && !model.IsVisible, "Minimize did not hide view-model work");
            panel.ShowNearPoint(((int)areas[0].X + 100, (int)areas[0].Y + 100));
            Require(panel.WindowState == WindowState.Normal && Math.Abs(panel.Width - 610) < 1 && Math.Abs(panel.Height - 650) < 1, "Restore lost resized dimensions");
            model.IsVisible = true; panel.Width = beforeWidth; panel.Height = 720;
            await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 20 && model.HasSelection));
            panel.UpdateLayout();
            var restore = (Button)panel.FindName("RestoreButton"); var undo = (Button)panel.FindName("UndoDeleteButton");
            Require(Math.Abs(restore.TranslatePoint(new Point(), panel).Y - undo.TranslatePoint(new Point(), panel).Y) < 1, "Copy and undo controls are not aligned");
            SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-light");
            model.Selected = model.Entries.Single(entry => entry.Id == image.Id);
            await WaitClipboardAsync(() => Task.FromResult(model.HasImage));
            model.IsPreviewExpanded = true;
            await WaitClipboardAsync(() => Task.FromResult(model.SelectedImage is not null));
            SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-image");
            model.IsPreviewExpanded = false;
            model.SelectAppearancePresetCommand.Execute(model.AppearancePresets.Single(preset => preset.Name == "深海"));
            Require(await model.PendingAppearanceSave, "Visual preset did not save automatically"); model.ApplyAppearance(panel.Resources);
            model.Selected = model.Entries.Single(entry => entry.Id == note.Id); await WaitClipboardAsync(() => Task.FromResult(model.HasSelection));
            ((Expander)panel.FindName("PreviewExpander")).IsExpanded = true;
            await panel.Dispatcher.InvokeAsync(panel.UpdateLayout, DispatcherPriority.ContextIdle);
            SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-deepsea");
            panel.Width = 480; panel.Height = 600; panel.UpdateLayout(); SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-compact");
            panel.CloseRuntime(); panel = null; model.IsVisible = false;
            var view = new ClipboardSettingsView { DataContext = model, Margin = new Thickness(18) };
            settingsWindow = new() { Content = view, Width = 900, Height = 920, ShowActivated = false, ShowInTaskbar = false,
                FontFamily = new("Microsoft YaHei UI"), FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextBrush"],
                Background = (Brush)Application.Current.Resources["WindowBrush"] };
            view.Background = settingsWindow.Background;
            settingsWindow.Show(); await settingsWindow.Dispatcher.InvokeAsync(settingsWindow.UpdateLayout, DispatcherPriority.ContextIdle);
            SaveClipboardVisual(view, images, "settings-top");
            ((ScrollViewer)view.Content).ScrollToVerticalOffset(440);
            await settingsWindow.Dispatcher.InvokeAsync(settingsWindow.UpdateLayout, DispatcherPriority.ContextIdle);
            SaveClipboardVisual(view, images, "settings-colors");
            ((ScrollViewer)view.Content).ScrollToVerticalOffset(980);
            await settingsWindow.Dispatcher.InvokeAsync(settingsWindow.UpdateLayout, DispatcherPriority.ContextIdle);
            SaveClipboardVisual(view, images, "settings-files");
            ((ScrollViewer)view.Content).ScrollToBottom();
            await settingsWindow.Dispatcher.InvokeAsync(settingsWindow.UpdateLayout, DispatcherPriority.ContextIdle);
            var dataCard = (Border)view.FindName("DataManagementCard");
            SaveClipboardVisual(dataCard, images, "data-management", contentOnly: true);
            var more = (Button)view.FindName("DataManagementMoreButton");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await settingsWindow.Dispatcher.InvokeAsync(settingsWindow.UpdateLayout, DispatcherPriority.ContextIdle);
            Require(more.ContextMenu is { IsOpen: true } menu && menu.PlacementTarget == more && menu.DataContext == model,
                "Data-management menu did not open on click or lost its model");
            SaveClipboardVisual(more.ContextMenu!, images, "data-management-menu");
            more.ContextMenu!.IsOpen = false;
            model.ApplyAppearance(settingsWindow.Resources);
            settingsWindow.Foreground = (Brush)settingsWindow.FindResource("TextBrush");
            settingsWindow.Background = (Brush)settingsWindow.FindResource("WindowBrush"); view.Background = settingsWindow.Background;
            SaveClipboardVisual(dataCard, images, "data-management-deepsea", contentOnly: true);
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await settingsWindow.Dispatcher.InvokeAsync(settingsWindow.UpdateLayout, DispatcherPriority.ContextIdle);
            SaveClipboardVisual(more.ContextMenu!, images, "data-management-menu-deepsea");
            more.ContextMenu!.IsOpen = false;
            Console.WriteLine($"PASS Isolated clipboard panel placement on {areas.Count} available monitor work areas, independent light/deep-sea palettes and settings rendering; images: {Path.GetFullPath(images)}");
        }
        finally { panel?.CloseRuntime(); settingsWindow?.Close(); await model.DisposeAsync(); DeleteClipboardFixture(directory); }
    }
    private static void CheckClipboardChrome(ClipboardHistoryPanel panel)
    {
        var width = panel.ActualWidth; var height = panel.ActualHeight;
        foreach (var (point, expected) in new[]
        {
            (new Point(2, height / 2), 10), (new Point(width - 2, height / 2), 11),
            (new Point(width / 2, 2), 12), (new Point(width / 2, height - 2), 15),
            (new Point(4, 4), 13), (new Point(width - 4, 4), 14),
            (new Point(4, height - 4), 16), (new Point(width - 4, height - 4), 17),
            (new Point(12, height / 2), 2), (new Point(width - 12, height / 2), 2),
            (new Point(width / 2, height - 10), 2), (new Point(width / 2, 25), 2)
        })
        {
            var screen = panel.PointToScreen(point);
            var packed = ((uint)(int)screen.X & 0xFFFF) | (((uint)(int)screen.Y & 0xFFFF) << 16);
            var actual = ClipboardChromeHit(panel.Handle, 0x0084, IntPtr.Zero, new IntPtr(unchecked((int)packed))).ToInt32();
            Require(actual == expected, $"Native clipboard frame hit at {point}: expected={expected}, actual={actual}");
        }
        var search = panel.SearchInput.PointToScreen(new Point(20, panel.SearchInput.ActualHeight / 2));
        var searchPacked = ((uint)(int)search.X & 0xFFFF) | (((uint)(int)search.Y & 0xFFFF) << 16);
        Require(ClipboardChromeHit(panel.Handle, 0x0084, IntPtr.Zero, new IntPtr(unchecked((int)searchPacked))).ToInt32() == 1,
            "Search textbox became a non-client drag area");
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr ClipboardChromeHit(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

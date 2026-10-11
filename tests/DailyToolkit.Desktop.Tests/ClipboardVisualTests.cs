using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void SaveClipboardVisual(FrameworkElement visual, string directory, string name)
    {
        visual.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
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
            model.IsVisible = true; await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 20 && model.HasSelection));
            panel = new(model) { ShowActivated = false }; model.ApplyAppearance(panel.Resources);
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
            }
            SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-light");
            model.Selected = model.Entries.Single(entry => entry.Id == image.Id);
            await WaitClipboardAsync(() => Task.FromResult(model.HasImage));
            model.IsPreviewExpanded = true;
            await WaitClipboardAsync(() => Task.FromResult(model.SelectedImage is not null));
            SaveClipboardVisual((FrameworkElement)panel.Content, images, "panel-image");
            model.IsPreviewExpanded = false;
            model.SelectAppearancePresetCommand.Execute(model.AppearancePresets.Single(preset => preset.Name == "深海"));
            Require(await model.SaveAppearanceAsync(), "Visual palette save failed"); model.ApplyAppearance(panel.Resources);
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
            Console.WriteLine($"PASS Isolated clipboard panel placement on {areas.Count} available monitor work areas, independent light/deep-sea palettes and settings rendering; images: {Path.GetFullPath(images)}");
        }
        finally { panel?.CloseRuntime(); settingsWindow?.Close(); await model.DisposeAsync(); DeleteClipboardFixture(directory); }
    }
}

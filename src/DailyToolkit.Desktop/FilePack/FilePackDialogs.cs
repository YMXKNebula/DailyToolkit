using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DailyToolkit.Desktop.FilePack;

public enum FilePackOutputChoice { Cancel, Overwrite, AutoRename, Reselect }
public enum FilePackEntryConflictChoice { Cancel, Rename, Skip }

internal static class FilePackDialogs
{
    internal static FilePackOutputChoice OutputConflict(string path) => Choice("文件名已被占用", path,
        ("自动更名", FilePackOutputChoice.AutoRename), ("覆盖现有压缩包", FilePackOutputChoice.Overwrite),
        ("重新选择", FilePackOutputChoice.Reselect), ("取消", FilePackOutputChoice.Cancel));

    internal static FilePackEntryConflictChoice EntryConflict(string details) => Choice("包内路径重名", details,
        ("自动更名", FilePackEntryConflictChoice.Rename), ("跳过重名项", FilePackEntryConflictChoice.Skip),
        ("取消添加", FilePackEntryConflictChoice.Cancel));

    internal static bool Confirm(string title, string message) => Choice(title, message,
        ("继续", true), ("取消", false));

    private static T Choice<T>(string title, string message, params (string Label, T Value)[] choices)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        var dialog = new Window
        {
            Title = title, Width = 550, SizeToContent = SizeToContent.Height, MaxHeight = 600,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = owner is null ?
                WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = owner?.FontFamily ?? new FontFamily("Microsoft YaHei UI"), FontSize = owner?.FontSize ?? 14
        };
        if (owner is not null) dialog.Owner = owner;
        dialog.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 18,
            Margin = new Thickness(0, 0, 0, 14) });
        var details = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        details.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 300 });
        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        var result = choices[^1].Value;
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Label, Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 8, 12, 8), IsCancel = EqualityComparer<T>.Default.Equals(choice.Value, choices[^1].Value) };
            if (!button.IsCancel) button.Click += (_, _) => { result = choice.Value; dialog.DialogResult = true; };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); dialog.Content = panel;
        dialog.ShowDialog(); return result;
    }
}

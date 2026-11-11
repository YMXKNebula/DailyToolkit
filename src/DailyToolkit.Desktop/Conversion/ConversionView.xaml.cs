using System.Windows;
using System.Windows.Controls;
using DailyToolkit.Core.Conversion;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.Conversion;

public partial class ConversionView : UserControl
{
    private ConversionViewModel? Model => DataContext as ConversionViewModel;
    public ConversionView() => InitializeComponent();

    private void AddFiles(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model) return;
        var filter = model.Kind switch
        {
            ConversionKind.Image => "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.webp",
            ConversionKind.Pdf => "PDF|*.pdf",
            ConversionKind.Media => "音视频|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.wma;*.opus;*.mp4;*.m4v;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.mpeg;*.mpg",
            _ => "文档|*.docx;*.odt;*.txt;*.xlsx;*.ods;*.csv;*.pptx;*.odp"
        };
        var dialog = new OpenFileDialog { Title = "添加转换文件", Filter = filter, Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) model.AddFiles(dialog.FileNames);
    }

    private void BrowseOutput(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model) return;
        var dialog = new OpenFolderDialog { Title = "选择保存位置", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) model.OutputDirectory = dialog.FolderName;
    }

    private void BrowseEngine(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || sender is not Button { Tag: string tag }) return;
        var dialog = new OpenFileDialog { Title = tag == "ffmpeg" ? "选择 FFmpeg" : "选择 LibreOffice", Filter = "程序|*.exe;*.com", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (tag == "ffmpeg") model.FFmpegPath = dialog.FileName; else model.LibreOfficePath = dialog.FileName;
    }

    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = Model?.CanEdit == true && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private void FilesDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Model is { CanEdit: true } model && e.Data.GetData(DataFormats.FileDrop) is string[] paths) model.AddFiles(paths);
    }
}

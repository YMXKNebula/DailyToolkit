using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DailyToolkit.Desktop.FilePack;

public partial class FilePackQuickView : UserControl
{
    private FilePackViewModel? Model => DataContext as FilePackViewModel;
    public FilePackQuickView() => InitializeComponent();
    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = Model?.CanEditDraft == true && (e.AllowedEffects & DragDropEffects.Copy) != 0 &&
            e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private async void FilesDropped(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (Model is not { CanEditDraft: true } model || (e.AllowedEffects & DragDropEffects.Copy) == 0 ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Effects = DragDropEffects.Copy;
        await model.AddPathsAsync(paths);
    }
    private void FilesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Model?.CanEditDraft != true) return;
        Model.RemoveEntries(FilesList.SelectedItems.OfType<FilePackEntryViewModel>().ToArray()); e.Handled = true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DailyToolkit.Desktop.FilePack;

public partial class FilePackQuickView : UserControl
{
    private FilePackViewModel? Model => DataContext as FilePackViewModel;
    public FilePackQuickView() => InitializeComponent();
    private void FilesDragOver(object sender, DragEventArgs e)
    { e.Effects = Model?.CanEditDraft == true && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void FilesDropped(object sender, DragEventArgs e)
    { e.Handled = true; if (Model?.CanEditDraft == true && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Model.AddPathsAsync(paths); }
    private void FilesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Model?.CanEditDraft != true) return;
        Model.RemoveEntries(FilesList.SelectedItems.OfType<FilePackEntryViewModel>().ToArray()); e.Handled = true;
    }
}

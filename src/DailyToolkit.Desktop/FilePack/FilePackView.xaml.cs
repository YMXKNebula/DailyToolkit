using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DailyToolkit.Desktop.FilePack;

public partial class FilePackView : UserControl
{
    private FilePackViewModel? Model => DataContext as FilePackViewModel;
    public FilePackView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisibility();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        DataContextChanged += (_, _) => UpdateVisibility();
    }
    private void UpdateVisibility() { if (Model is not null) Model.IsVisible = IsVisible && IsLoaded; }
    private void WorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var sideBySide = e.NewSize.Width >= 1000;
        WorkbenchLayout.ColumnDefinitions[0].Width = new(3, GridUnitType.Star);
        WorkbenchLayout.ColumnDefinitions[1].Width = sideBySide ? new(2, GridUnitType.Star) : new(0);
        Grid.SetColumn(TasksCard, sideBySide ? 1 : 0); Grid.SetRow(TasksCard, sideBySide ? 0 : 1);
        TasksCard.Margin = sideBySide ? new(12, 0, 0, 0) : new(0, 12, 0, 0);
        TasksList.Height = sideBySide ? 610 : 360;
    }
    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = Model?.CanEditDraft == true && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void FilesDropped(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Model?.CanEditDraft != true || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await Model.AddPathsAsync(paths);
    }
    private void EntriesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.OriginalSource is TextBox || Model?.CanEditDraft != true) return;
        Model.RemoveEntries(EntriesGrid.SelectedItems.OfType<FilePackEntryViewModel>().ToArray()); e.Handled = true;
    }
    private void PathEditingEnded(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FilePackEntryViewModel row } && Model?.CanEditDraft == true)
            Model.TryRenameEntry(row, row.PathDraft);
    }
    private void PathKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FilePackEntryViewModel row } text) return;
        if (e.Key == Key.Escape) { row.ResetDraft(); text.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); e.Handled = true; }
        if (e.Key == Key.Enter)
        { Model?.TryRenameEntry(row, row.PathDraft); text.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); e.Handled = true; }
    }
}

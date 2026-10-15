using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackVisualsAsync(string? images = null)
    {
        var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FilePackFixtures"));
        var fixture = Directory.CreateDirectory(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            var favorites = new FavoritesStore(Path.Combine(fixture, "favorites.json"));
            var navigationRoot = Path.Combine(fixture, "navigation-module");
            using (var shell = new MainViewModel(new ControlledProbe(display), display, new LocalProbe(),
                favoritesStore: favorites, gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(fixture, "gaming.json")),
                navigationStore: new NavigationOrderStore(Path.Combine(fixture, "navigation.json")),
                appPreferencesStore: new AppPreferencesStore(Path.Combine(fixture, "app.json")), startupRegistration: new StartupFake(),
                notesStore: IsolatedNotes(), clipboardSettingsStore: IsolatedClipboard(), filePackStore: new FilePackStore(navigationRoot)))
            {
                Require(!Directory.Exists(navigationRoot), "Shell eagerly initialized FilePack storage");
                shell.Page = "file-pack"; await shell.FilePack.Ready;
                Require(shell.ShowFilePack && shell.PageTitle == "文件集装箱" && !shell.ShowRefresh && !shell.RefreshCommand.CanExecute(null) && shell.FilePack.IsVisible,
                    "FilePack was not wired to the existing tool navigation");
                shell.FilePack.ToggleFavoriteCommand.Execute(null);
                Require(favorites.Load().Contains("file-pack"), "FilePack favorite did not use the shared store");
                shell.OpenFavoritesCommand.Execute(null);
                Require(shell.NavigationItems.Single().Id == "file-pack", "FilePack did not share its navigation entry in favorites");
                shell.OpenSettingsCommand.Execute(null); Require(!shell.FilePack.IsVisible, "Settings left FilePack active as a visible page");
                shell.ExitSettingsCommand.Execute(null);
                Require(shell.ShowFilePack && shell.FilePack.IsVisible, "Closing settings lost the selected FilePack page");
                await shell.FilePack.ShutdownAsync();
            }
            var inputA = Directory.CreateDirectory(Path.Combine(fixture, "来源甲")).FullName;
            var inputB = Directory.CreateDirectory(Path.Combine(fixture, "来源乙")).FullName;
            var sourceA = Path.Combine(inputA, "文档.txt"); var sourceB = Path.Combine(inputB, "文档.txt");
            await File.WriteAllTextAsync(sourceA, "测试甲"); await File.WriteAllTextAsync(sourceB, "测试乙");
            var stampA = FilePackPaths.CaptureSourceStamp(sourceA); var stampB = FilePackPaths.CaptureSourceStamp(sourceB);
            var unopenedRoot = Path.Combine(fixture, "未打开模块");
            await using (var unopened = new FilePackViewModel(new FilePackStore(unopenedRoot), "missing-engine.exe", _ => { }))
            {
                unopened.ToggleFavoriteCommand.Execute(null);
                Require(unopened.IsFavorite && !unopened.IsLoaded && !Directory.Exists(unopenedRoot), "FilePack constructor/favorite eagerly touched storage");
            }
            Require(!Directory.Exists(unopenedRoot), "Disposing unopened FilePack initialized storage");

            var module = Path.Combine(fixture, "module");
            var opened = new List<string>(); var confirmations = 0;
            await using (var model = new FilePackViewModel(new FilePackStore(module), "missing-engine.exe", path => opened.Add(path))
            { ConfirmAction = (_, _) => { confirmations++; return true; } })
            {
                model.IsVisible = true; await model.Ready; model.MaximumParallel = 99; await model.FlushAsync();
                Require(model.IsLoaded && model.MaximumParallel == 4 && (await model.Store.LoadPreferencesAsync()).MaximumParallel == 4,
                    "Lazy initialization or bounded parallel preference did not persist");
                await model.AddPathsAsync([sourceA, sourceA]);
                Require(model.Entries.Count == 1, "Duplicate source was collected twice");
                model.ResolveEntryConflict = _ => FilePackEntryConflictChoice.Skip;
                await model.AddPathsAsync([sourceB]); Require(model.Entries.Count == 1, "Skip-name-conflict changed the existing manifest");
                model.ResolveEntryConflict = _ => FilePackEntryConflictChoice.Cancel;
                await model.AddPathsAsync([sourceB]); Require(model.Entries.Count == 1, "Cancel-name-conflict changed the existing manifest");
                model.ResolveEntryConflict = _ => FilePackEntryConflictChoice.Rename;
                await model.AddPathsAsync([sourceB]);
                Require(model.Entries.Count == 2 && model.Entries.Select(row => row.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                    "Renamed conflict did not produce distinct archive paths");
                Require(model.AddVirtualFolder("资料") && model.MoveEntries(model.Entries.Where(row => !row.Entry.IsDirectory).ToArray(), "资料"),
                    "Virtual archive folder/multiple-item mapping failed");
                Require(!model.TryRenameEntry(model.Entries.First(row => !row.Entry.IsDirectory), "../逃逸.txt"), "Archive traversal was accepted");
                model.Mode = PackMode.External; model.IsVisible = false;
                var created = await model.CreateTaskAsync();
                Require(created is not null && model.Entries.Count == 0 && confirmations == 1 && created.Snapshot.Options.AllowStaging,
                    "External job did not freeze/configure/enqueue the manifest after the extra-space confirmation: " + model.Notice);
                await WaitForFilePackAsync(() => model.Tasks.Count == 1 && model.Tasks[0].IsExternalReady, "External preparation while page hidden did not complete");
                Require(opened.Count == 1 && Directory.Exists(opened[0]) && File.Exists(Path.Combine(opened[0], "资料", "文档.txt")),
                    "External prepared folder was not automatically opened once with correct mapping");
                await model.AddPathsAsync([sourceA, sourceB]); model.Mode = PackMode.BuiltIn; model.OutputDirectory = inputA;
                Require(created!.Snapshot.Entries.Count == 3 && created.Snapshot.Options.Mode == PackMode.External,
                    "Editing the next draft mutated the running task snapshot");
                CheckFilePackLayout(model, images);
                model.ClearDraft(); model.Mode = PackMode.External;
                model.KeepExternalCommand.Execute(model.Tasks[0]);
                await WaitForFilePackAsync(() => model.Tasks[0].Info.State == PackState.Completed, "Finish-and-keep did not finish the external task");
                Require(Directory.Exists(opened[0]) && model.Tasks[0].CanCleanup, "Finish-and-keep removed or hid the retained workspace");
                model.CleanupTaskCommand.Execute(model.Tasks[0]);
                await WaitForFilePackAsync(() => model.Tasks[0].Info.WorkspacePath.Length == 0, "Manual owned-workspace cleanup did not finish");
                Require(!Directory.Exists(opened[0]), "Manual cleanup left the staging folder");
                await model.AddPathsAsync([sourceA]); await model.CreateTaskAsync();
                await WaitForFilePackAsync(() => model.Tasks.Any(row => row.IsExternalReady), "External cancellation fixture did not prepare");
                var toCancel = model.Tasks.Single(row => row.IsExternalReady);
                Require(toCancel.CanCancel, "An external waiting task could not be cancelled");
                model.CancelTaskCommand.Execute(toCancel);
                await WaitForFilePackAsync(() => toCancel.Info.State == PackState.Cancelled, "External waiting cancellation failed");
                Require(Directory.Exists(toCancel.Info.WorkspacePath) && toCancel.CanCleanup, "External cancellation deleted or hid retained data");
                model.CleanupTaskCommand.Execute(toCancel);
                await WaitForFilePackAsync(() => toCancel.Info.WorkspacePath.Length == 0, "Cancelled external workspace could not be cleaned explicitly");
                model.RemoveTaskCommand.Execute(toCancel);
                await WaitForFilePackAsync(() => !model.Tasks.Contains(toCancel), "Removing stopped history left its UI row");
                await model.AddPathsAsync([sourceA]); await model.CreateTaskAsync();
                await WaitForFilePackAsync(() => model.Tasks.Any(row => row.IsExternalReady), "Second independent external job did not prepare");
                await model.ShutdownAsync();
            }
            var openedBeforeRecovery = opened.Count;
            await using (var restored = new FilePackViewModel(new FilePackStore(module), "missing-engine.exe", path => opened.Add(path))
            { ConfirmAction = (_, _) => { confirmations++; return true; } })
            {
                await restored.InitializeAsync();
                var pending = restored.Tasks.Single(row => row.IsExternalReady);
                Require(opened.Count == openedBeforeRecovery && Directory.Exists(pending.Info.WorkspacePath), "Recovery auto-opened or removed an unfinished external workspace");
                var prepared = pending.Info.WorkspacePath;
                restored.CompleteExternalCommand.Execute(pending);
                await WaitForFilePackAsync(() => pending.Info.State == PackState.Completed, "Recovered external task could not finish-and-clean");
                Require(!Directory.Exists(prepared), "Finish-and-clean did not remove the recovered owned workspace");
            }
            Require(FilePackPaths.CaptureSourceStamp(sourceA) == stampA && FilePackPaths.CaptureSourceStamp(sourceB) == stampB &&
                await File.ReadAllTextAsync(sourceA) == "测试甲" && await File.ReadAllTextAsync(sourceB) == "测试乙", "UI workflow altered original files");
            await CheckFilePackLargeDraftAsync(fixture);
            Console.WriteLine("PASS FilePack lazy storage, favorites, conflict choices, duplicate sources, virtual mapping, immutable tasks, external preparation/open, hidden-page work, retain/cleanup, recovery, source invariance and wide/narrow WPF layout");
        }
        finally
        {
            if (!FilePackPaths.IsWithin(fixture, fixtureRoot)) throw new IOException("Fixture cleanup escaped its dedicated root.");
            FilePackPaths.EnsureNoReparsePoints(fixture); Directory.Delete(fixture, true);
        }
    }

    private static async Task CheckFilePackLargeDraftAsync(string fixture)
    {
        var source = Directory.CreateDirectory(Path.Combine(fixture, "many-sources")).FullName;
        const int count = 2000;
        for (var index = 0; index < count; index++)
            await File.WriteAllBytesAsync(Path.Combine(source, $"sample-{index:D4}.bin"), [7]);
        await using var model = new FilePackViewModel(new FilePackStore(Path.Combine(fixture, "large-module")), "missing-engine.exe", _ => { });
        await model.InitializeAsync();
        var changes = 0;
        model.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(FilePackViewModel.DraftSummary)) changes++; };
        var watch = Stopwatch.StartNew();
        await model.AddPathsAsync([source]);
        Require(model.Entries.Count == count && model.DraftSummary == $"{count} 项 · 2 KB" && changes <= 2,
            "Large file collection lost rows/size or refreshed the full summary once per file");
        var view = new FilePackView { DataContext = model };
        view.Measure(new Size(1120, 1000)); view.Arrange(new Rect(0, 0, 1120, 1000)); view.UpdateLayout();
        var grid = (DataGrid)view.FindName("EntriesGrid");
        int CountRows(DependencyObject element)
        {
            var rows = element is DataGridRow ? 1 : 0;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                rows += CountRows(VisualTreeHelper.GetChild(element, index));
            return rows;
        }
        var realized = CountRows(grid);
        Require(grid.Items.Count == count && realized < 40, "Large collection did not virtualize its row controls");
        Require(model.MoveEntries(model.Entries.ToArray(), "资料") && model.Entries.All(row => row.ArchivePath.StartsWith("资料/", StringComparison.Ordinal)),
            "Bulk archive mapping lost entries");
        model.RemoveEntries(model.Entries.ToArray());
        Require(model.Entries.Count == 0 && model.DraftSummary == "0 项 · 0 B" && changes <= 4,
            "Bulk removal lost cached size consistency or refreshed the summary per entry");
        view.DataContext = null;
        Console.WriteLine($"PASS FilePack {count}-file async collection, {realized} realized WPF rows, batched summary, bulk mapping/removal ({watch.ElapsedMilliseconds} ms)");
    }

    private static async Task WaitForFilePackAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Require(condition(), message);
    }

    private static void CheckFilePackLayout(FilePackViewModel model, string? images)
    {
        var view = new FilePackView { DataContext = model, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Background = (Brush)Application.Current.FindResource("WindowBrush") };
        foreach (var (width, height, name) in new[] { (1120, 1050, "filepack-wide"), (620, 1400, "filepack-narrow") })
        {
            view.Width = width; view.Height = height; view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var layout = (Grid)view.FindName("WorkbenchLayout"); var tasks = (Border)view.FindName("TasksCard");
            Require(Grid.GetRow(tasks) == (width >= 1000 ? 0 : 1) && layout.ActualWidth <= width,
                "FilePack adaptive workbench overflowed or failed to reposition the task panel");
            var entries = (DataGrid)view.FindName("EntriesGrid");
            Require(entries.Columns.Count == 4 && entries.EnableRowVirtualization && ((ListBox)view.FindName("TasksList")).Items.Count == model.Tasks.Count,
                "FilePack collection/real-task list was missing or did not virtualize");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            if (images is null) continue;
            var path = Path.Combine(Path.GetFullPath(images), name + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path); encoder.Save(output);
        }
        view.DataContext = null;
    }
}

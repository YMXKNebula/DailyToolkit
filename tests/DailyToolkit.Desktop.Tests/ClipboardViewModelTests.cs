using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckClipboardViewModelAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-viewmodel-");
        ClipboardHistoryPanel? panel = null; ClipboardHistoryViewModel? model = null;
        try
        {
            var store = new ClipboardSettingsStore(directory.FullName); model = new(store); await model.Ready;
            Require(!model.IsEnabled && !Directory.Exists(Path.Combine(directory.FullName, "history.db")) && !File.Exists(model.Repository.DatabasePath), "Disabled tool created a database or enabled capture");
            model.RequestEnabled(true);
            Require(model.IsEnabled && !model.IsPaused && model.Settings.ConsentAccepted, "User enable did not start recording directly");
            model.IsPaused = true; model.MaximumRecords = 2000;
            var settingsSaved = await model.FlushSettingsAsync();
            Require(settingsSaved && model.IsEnabled && model.IsPaused,
                $"Tool settings did not persist: saved={settingsSaved}, enabled={model.IsEnabled}, paused={model.IsPaused}, notice={model.Notice}");
            Require(!model.SetShortcut(new(6, 0x77, "Ctrl + Shift + F8"), [new(6, 0x77, "Ctrl + Shift + F8")]), "Internal hotkey conflict was accepted");
            Require(model.SetShortcut(new(3, 0x55, "Ctrl + Alt + U"), []), "Valid custom shortcut rejected");
            model.ExcludedApplications = "KeePass.exe\nfixture-app";
            model.SelectAppearancePresetCommand.Execute(model.AppearancePresets.Single(preset => preset.Name == "深海"));
            Require(await model.PendingAppearanceSave && model.Appearance.Window == "#101D32" && !model.HasAppearanceChanges &&
                (await store.LoadAsync()).Settings.PanelColors["Window"] == "#101D32", "Preset did not apply and persist without a separate save");
            var applied = model.Appearance;
            model.AppearanceColors.Single(option => option.Key == "Text").Value = "#EECCAA";
            Require(model.HasAppearanceChanges && model.Appearance == applied, "Custom draft changed applied colors before save");
            Require(await model.FlushSettingsAsync() && (await store.LoadAsync()).Settings.PanelColors["Text"] == applied.Text,
                "Ordinary settings flush persisted a custom palette draft");
            Require(await model.SaveAppearanceAsync() && model.Appearance.Window == "#101D32" && model.Appearance.Text == "#EECCAA", "Independent preset/custom colors were not saved");
            model.EditorText = "private manual fixture"; Require(await model.CreateManualAsync(), "Manual creation failed");
            await model.FlushSettingsAsync(); model.Dispose(); await model.Repository.DisposeAsync(); model = new(store); await model.Ready;
            Require(model.IsEnabled && model.IsPaused && model.MaximumRecords == 2000 && model.ShortcutText == "Ctrl + Alt + U" &&
                model.Settings.ExcludedApplications.Length == 2 && model.Appearance.Window == "#101D32" && model.Appearance.Text == "#EECCAA", "Restart lost history settings or independent appearance");
            model.RequestEnabled(false); model.RequestEnabled(true);
            Require(model.IsEnabled && !model.IsPaused && await model.FlushSettingsAsync(), "Re-enabling a paused tool did not resume recording");
            await model.EnsureRepositoryAsync(); Require((await model.Repository.QueryAsync(new("private manual"))).Total == 1, "Manual data did not survive restart");
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            using (var host = new MainViewModel(new ControlledProbe(display), display, new LocalProbe(),
                favoritesStore: new FavoritesStore(Path.Combine(directory.FullName, "favorites.json")),
                gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(directory.FullName, "gaming.json")),
                appPreferencesStore: new AppPreferencesStore(Path.Combine(directory.FullName, "app.json")),
                navigationStore: new NavigationOrderStore(Path.Combine(directory.FullName, "navigation.json")), startupRegistration: new StartupFake(),
                notesStore: new NotesStore(Path.Combine(directory.FullName, "Notes")), clipboardSettingsStore: store))
            {
                await host.ClipRecall.Ready; var appearance = host.ClipRecall.Appearance;
                host.DarkThemeCommand.Execute(null); host.Page = "clip-recall";
                Require(host.ShowClipRecall && !host.ShowRefresh && host.ClipRecall.Appearance == appearance, "Main theme changed tool colors or navigation routing");
                host.ClipRecall.IsFavorite = true; host.OpenFavoritesCommand.Execute(null);
                Require(host.NavigationItems.Single().Id == "clip-recall", "ClipRecall favorite did not share tool state");
            }
            Console.WriteLine("PASS ClipRecall direct switch enable/resume, pause/settings restart, hotkey conflicts, immediate preset persistence, preview-only custom colors, main theme isolation and shared favorites");

            var seed = Stopwatch.StartNew();
            for (var index = 0; index < 1000; index++)
                await model.Repository.CaptureAsync(ClipboardContent.FromText($"search fixture {index:D4} " + new string('x', 800)), DateTimeOffset.UtcNow.AddMilliseconds(index), null, model.Settings);
            Console.WriteLine($"INFO ClipRecall 1000 persisted fixtures: {seed.ElapsedMilliseconds} ms");
            var search = Stopwatch.StartNew();
            var page = await model.Repository.QueryAsync(new("FIXTURE 09", Offset: 0, Limit: 100));
            Require(page.Total == 100 && page.Records.All(entry => entry.Preview.Contains("fixture 09")), "Large encrypted query returned wrong records");
            Console.WriteLine($"INFO ClipRecall 1000-record encrypted search: {search.ElapsedMilliseconds} ms");
            using (var cancelled = new CancellationTokenSource())
            { cancelled.Cancel(); await ClipboardThrowsAsync<OperationCanceledException>(() => model.Repository.QueryAsync(new("fixture"), cancelled.Token)); }
            model.IsVisible = true;
            await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 100));
            panel = new(model) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            model.ApplyAppearance(panel.Resources); panel.Show(); await panel.Dispatcher.InvokeAsync(panel.UpdateLayout, DispatcherPriority.ContextIdle);
            Require((bool)panel.RecordsList.GetValue(VirtualizingPanel.IsVirtualizingProperty) && panel.RecordsList.Items.Count == 100,
                "Panel did not use pagination/virtualization");
            var realized = Enumerable.Range(0, 100).Count(index => panel.RecordsList.ItemContainerGenerator.ContainerFromIndex(index) is not null);
            Require(realized < 100 && ((SolidColorBrush)panel.Resources["WindowBrush"]).Color == Color.FromRgb(0x10, 0x1D, 0x32), "Panel realized full page or ignored independent colors");
            model.Search = "no match"; model.Search = "fixture 0999";
            await WaitClipboardAsync(() => Task.FromResult(model.Entries.Count == 1 && model.Entries[0].Preview.Contains("0999")));
            await WaitClipboardAsync(() => Task.FromResult(model.HasSelection));
            var selectedId = model.Selected!.Id;
            model.NoteText = "draft preserved across refresh"; model.RequestRefresh();
            await WaitClipboardAsync(() => Task.FromResult(model.HasSelection));
            await Task.Delay(250);
            Require(model.Selected?.Id == selectedId && model.NoteText == "draft preserved across refresh" && model.HasNoteChanges, "Incremental refresh discarded a pending note edit");
            model.RestoreRequested = _ => Task.FromResult(new ClipboardOperationResult(false, "fixture failure"));
            Require(!await model.RestoreSelectedAsync(), "UI restore returned success for native failure"); model.RestoreRequested = null;
            var content = model.DetailText; model.NoteText = "searchable note"; Require(await model.SaveNoteAsync() && model.DetailText == content, "Note save changed clipboard body");
            Require(!model.HasNoteChanges, "Saved note retained a dirty draft");
            model.NoteText = "draft for deleted record";
            Require(await model.DeleteAsync([model.Entries[0].Id]) && !model.HasNoteChanges,
                "Deleting a record retained an unsaveable note draft");
            Require(await model.UndoAsync(), "Viewmodel delete undo failed");
            model.IsVisible = false; Require(model.Entries.Count == 0 && !model.HasSelection, "Hidden panel retained full content/rows");
            panel.CloseRuntime(); panel = null;
            Console.WriteLine($"PASS ClipRecall 1000-record search/cancellation, debounce, paged virtualized panel ({realized} realized rows), note isolation and hidden-data release");

            await model.FlushSettingsAsync(); model.Dispose(); await model.Repository.DisposeAsync(); model = null;
            await File.WriteAllTextAsync(store.SettingsPath, "{");
            model = new(store); await model.Ready; model.MaximumRecords = 500;
            Require(!model.IsEnabled && await File.ReadAllTextAsync(store.SettingsPath) == "{", "Broken settings were overwritten or listening enabled");
            model.SelectAppearancePresetCommand.Execute(model.AppearancePresets.Single(preset => preset.Name == "深海"));
            Require(!await model.PendingAppearanceSave && model.Appearance == new ThemePalette() && !model.HasAppearanceChanges,
                "Read-only corrupt settings applied a failed preset");
            model.AppearanceColors.Single(option => option.Key == "Text").Value = "#EECCAA";
            Require(!await model.SaveAppearanceAsync() && model.Appearance == new ThemePalette() && model.AppearanceDraft.Text == "#EECCAA",
                "Failed custom save changed applied colors or discarded its draft");
            Require(!model.SetShortcut(new(3, 0x42, "Ctrl + Alt + B"), []), "Read-only corrupt settings claimed a successful shortcut change");
            await model.ResetSettingsAsync(); Require(!model.IsEnabled && (await store.LoadAsync()).Writable, "Explicit settings recovery failed");
            Console.WriteLine("PASS ClipRecall fail-closed corrupt settings and explicit recovery without history deletion");
            model.EditorText = "exit waits for pending manual record";
            var creation = model.CreateManualAsync(); Require(await model.PrepareExitAsync() && await creation, "Exit did not await pending manual save");
            await model.DisposeAsync(); model = new(store); await model.Ready; await model.EnsureRepositoryAsync();
            Require((await model.Repository.QueryAsync(new("exit waits"))).Total == 1, "Exit lost pending manual content");
            Console.WriteLine("PASS ClipRecall pending-note preservation, honest restore/palette failure and exit waiting for committed manual data");
        }
        finally
        {
            panel?.CloseRuntime();
            if (model is not null) { model.Dispose(); await model.Repository.DisposeAsync(); }
            DeleteClipboardFixture(directory);
        }
    }
}

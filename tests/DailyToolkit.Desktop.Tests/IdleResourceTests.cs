using System.IO;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckInactiveNotesResourcesAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-idle-notes-");
        try
        {
            var store = new NotesStore(directory.FullName);
            var original = new string('笺', 1_000_000) + "\nlast saved input 📝";
            await store.SaveContentAsync(original);
            using var model = new NotesViewModel(store);
            await model.Ready;
            Require(model.IsContentLoaded && model.ReleaseInactiveContent() && !model.IsContentLoaded && model.Text.Length == 0,
                "A clean hidden note retained its large document");
            model.FontSize = 27;
            Require(await model.FlushAsync() && await File.ReadAllTextAsync(store.ContentPath) == original,
                "Saving unloaded note settings overwrote its document");
            for (var index = 0; index < 4; index++)
            {
                await model.EnsureContentLoadedAsync();
                Require(model.Text == original, "Reloading a hidden note changed its saved text");
                Require(model.ReleaseInactiveContent(), "Repeated reload/release retained note content");
            }
            await model.EnsureContentLoadedAsync();
            var unsaved = original + "\nunsaved change";
            model.Text = unsaved;
            using (var locked = new FileStream(store.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(!await model.FlushAsync() && !model.ReleaseInactiveContent() && model.Text == unsaved,
                    "Failed persistence allowed the only unsaved note copy to be released");
                Require(await File.ReadAllTextAsync(store.ContentPath) == original,
                    "A failed save changed the original note");
            }
            Require(await model.FlushAsync() && !model.IsSaveScheduled && model.ReleaseInactiveContent(),
                "Saving did not recover or retained its idle save timer");
            model.IsEnabled = false;
            model.LoadFontList();
            Require(model.InstalledFonts.Count == 0 && await model.FlushAsync(),
                "A disabled note allocated the system font list or failed to save its switch");
            await File.WriteAllBytesAsync(store.ContentPath, [0xC3, 0x28]);
            await model.EnsureContentLoadedAsync();
            Require(!model.ContentWritable && model.Notice.Contains("读取失败", StringComparison.Ordinal),
                "Reloading damaged content removed the read-only protection");
            model.Text = "must never replace damaged input";
            await model.FlushAsync();
            Require((await File.ReadAllBytesAsync(store.ContentPath)).SequenceEqual(new byte[] { 0xC3, 0x28 }),
                "A damaged reloaded note was overwritten");
            Console.WriteLine("PASS Hidden note releases clean text; repeated reload, setting-only save, lock failures and damaged-file protection preserve the document");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task CheckPassiveComputerCancellationAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-idle-computer-");
        var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
        MainViewModel Create(string name, IEnvironmentProbe probe, ILocalStatusProbe status)
        {
            var root = Path.Combine(directory.FullName, name);
            return new(probe, display, status,
                favoritesStore: new FavoritesStore(Path.Combine(root, "favorites.json")),
                gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(root, "gaming.json")),
                navigationStore: new NavigationOrderStore(Path.Combine(root, "navigation.json")),
                appPreferencesStore: new AppPreferencesStore(Path.Combine(root, "app.json")),
                startupRegistration: new StartupFake(), notesStore: new NotesStore(Path.Combine(root, "notes")),
                clipboardSettingsStore: new ClipboardSettingsStore(Path.Combine(root, "clipboard")),
                filePackStore: new FilePackStore(Path.Combine(root, "filepack")));
        }
        try
        {
            var passiveProbe = new ControlledProbe(display);
            var passiveStatus = new LocalProbe { WaitForCancel = true };
            using (var model = Create("cancel", passiveProbe, passiveStatus))
            {
                model.SetPassiveMonitoring(true);
                var pending = model.InitializePassiveAsync();
                await passiveProbe.DetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                model.SetPassiveMonitoring(false);
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Require(passiveProbe.WasCanceled && passiveStatus.WasCanceled && !model.IsRefreshing,
                    "Leaving computer details did not cancel passive computer/weather work");
                var reads = passiveStatus.WeatherReads;
                model.QueueLocalRefresh(passive: true);
                await model.CurrentLocalStatusTask;
                Require(passiveStatus.WeatherReads == reads, "An inactive page scheduled another passive weather read");
                passiveProbe.Finish.TrySetResult();
                model.SetPassiveMonitoring(true);
                var restart = model.InitializePassiveAsync();
                // The weather fixture waits for cancellation again; leaving immediately
                // exercises return/leave while a canceled probe is being restarted.
                model.SetPassiveMonitoring(false);
                await restart.WaitAsync(TimeSpan.FromSeconds(5));
                await model.PendingWork;
            }
            var explicitProbe = new ControlledProbe(display);
            using (var model = Create("explicit", explicitProbe, new LocalProbe()))
            {
                model.SetPassiveMonitoring(true);
                var automatic = model.InitializePassiveAsync();
                await explicitProbe.DetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var refresh = model.InitializeAsync();
                model.SetPassiveMonitoring(false);
                Require(!refresh.IsCompleted && !explicitProbe.WasCanceled,
                    "Leaving the page canceled an explicit user refresh");
                explicitProbe.Finish.TrySetResult();
                await Task.WhenAll(automatic, refresh);
                Require(!explicitProbe.WasCanceled && model.Report.Stage == ProbeStage.Complete,
                    "Explicit refresh did not retain its completed report");
                await model.PendingWork;
            }
            Console.WriteLine("PASS Leaving computer details cancels passive reads and prevents idle polling, while explicit Refresh remains usable");
        }
        finally { directory.Delete(recursive: true); }
    }
}

using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task<MainViewModel> DisabledRuntimeFixtureModelAsync(string root)
    {
        var model = ResourceHost(root);
        await Task.WhenAll(model.Notes.Ready, model.ClipRecall.Ready, model.FilePack.SettingsReady);
        model.Gaming.IsEnabled = false; model.Notes.IsEnabled = false;
        model.ClipRecall.RequestEnabled(false); await model.ClipRecall.PendingSettings;
        Require(await model.Notes.FlushAsync() && await model.ClipRecall.FlushSettingsAsync() &&
            await model.FilePack.SetEnabledAsync(false), "Disabled runtime fixture settings could not be saved");
        model.Notes.ReleaseInactiveContent();
        model.CloseToTray = false; model.MinimizeToTray = false; model.PageAnimationsEnabled = false;
        Require(!model.IsHome && !model.Gaming.IsEnabled && !model.Notes.IsEnabled &&
            !model.ClipRecall.IsEnabled && !model.FilePack.IsEnabled, "Runtime fixture did not disable its tools");
        return model;
    }

    private static void ConfigureRuntimeFixtureWindow(MainWindow window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 80; window.Top = 80;
        if (window.FindName("FilePackSettings") is FilePackSettingsView filePack)
            filePack.ContextMenuRegistration = new FilePackContextMenuRegistration(new FilePackMenuFake());
    }

    private static async Task CheckRuntimeHostLifecycleAsync()
    {
        RequireRuntimeFixtureStation();
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-lifecycle-");
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            runtime = new(model, enableRuntime: false, idleDelay: TimeSpan.FromMinutes(5), shutdownApplication: false);
            runtime.WindowCreated += ConfigureRuntimeFixtureWindow;
            var source = runtime.SourceHandle;
            Require(runtime.Window is null && source != IntPtr.Zero && ReviewIsWindow(source) &&
                !runtime.TrayAvailable && runtime.LensShortcuts is null && runtime.Notes is null && runtime.ClipRecall is null && runtime.FilePack is null,
                "Cold test runtime created UI/controllers or did not retain its isolated message source");
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            var first = runtime.Window!;
            Require(first.IsVisible && ReferenceEquals(first.DataContext, model), "Runtime did not create its first UI on demand");
            model.OpenSettingsCommand.Execute(null); model.Page = "settings-theme";
            first.Width = 980; first.Height = 640; first.UpdateLayout();
            var scroll = (ScrollViewer)first.FindName("SettingsScroll");
            scroll.ScrollToVerticalOffset(180); await SettleRuntimeFixtureAsync();
            var expectedScroll = scroll.VerticalOffset;
            Require(expectedScroll > 0, "Runtime session fixture did not scroll its settings page");
            var originalHwnd = new WindowInteropHelper(first).Handle;
            first.Hide();
            Require(runtime.TryReleaseHiddenWindow() && runtime.Window is null && !ReviewIsWindow(originalHwnd) &&
                runtime.SourceHandle == source && ReviewIsWindow(source),
                "Hidden runtime UI or its HWND was retained, or release destroyed the stable message source");
            model.Notes.FontSize = 27;
            Require(await model.Notes.FlushAsync() && model.Page == "settings-theme" && model.IsSettings,
                "UI release disposed its shared model or lost the selected settings page");
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            var reopened = runtime.Window!;
            var reopenedScroll = (ScrollViewer)reopened.FindName("SettingsScroll");
            Require(!ReferenceEquals(reopened, first) && new WindowInteropHelper(reopened).Handle != originalHwnd &&
                ReferenceEquals(reopened.DataContext, model) && Math.Abs(reopened.Width - 980) < 1 && Math.Abs(reopened.Height - 640) < 1 &&
                model.Page == "settings-theme" && model.IsSettings && Math.Abs(reopenedScroll.VerticalOffset - expectedScroll) < 1,
                $"Rebuilt UI lost model/page/size/scroll: expectedOffset={expectedScroll}, actualOffset={reopenedScroll.VerticalOffset}");

            // Explicit source updates model an unfinished edit without keyboard,
            // focus changes, native input or a dialog on the interactive desktop.
            var pending = new RuntimePendingText { Value = "saved fixture value" };
            var input = new TextBox { Width = 220, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            input.SetBinding(TextBox.TextProperty, new Binding(nameof(RuntimePendingText.Value))
            { Source = pending, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
            var root = (Grid)reopened.FindName("RootContent");
            Grid.SetRow(input, 1); root.Children.Add(input); reopened.UpdateLayout();
            input.SetCurrentValue(TextBox.TextProperty, "unfinished fixture input");
            var binding = input.GetBindingExpression(TextBox.TextProperty)!;
            Require(binding.IsDirty && pending.Value == "saved fixture value", "Pending-text fixture was already committed");
            reopened.Hide();
            Require(!runtime.TryReleaseHiddenWindow() && ReferenceEquals(runtime.Window, reopened) &&
                ReviewIsWindow(new WindowInteropHelper(reopened).Handle) && input.Text == "unfinished fixture input" &&
                pending.Value == "saved fixture value", "UI release discarded or silently committed an unfinished text edit");
            binding.UpdateSource(); root.Children.Remove(input); BindingOperations.ClearAllBindings(input);
            Require(pending.Value == "unfinished fixture input" && runtime.TryReleaseHiddenWindow(),
                "UI could not unload after its explicit edit was committed");

            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            var releasedWindow = CreateReleasedRuntimeWindowReference(runtime);
            // This is a separate reachability assertion. Memory profiling never
            // invokes this helper and never forces a collection or trims a set.
            for (var attempt = 0; attempt < 10 && releasedWindow.IsAlive; attempt++)
            {
                await SettleRuntimeFixtureAsync();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(50);
            }
            Require(!releasedWindow.IsAlive, "Stable runtime callbacks or providers kept the released MainWindow strongly reachable");
            Require(runtime.SourceHandle == source && ReviewIsWindow(source), "Window reachability checks closed the message source");
            Console.WriteLine("PASS Runtime cold start has no main UI; hidden HWND releases while message source/model survive; rebuild restores page/size/scroll; unfinished text vetoes; released window is collectible (explicit GC only in reachability assertion)");
        }
        finally
        {
            await CloseRuntimeFixtureAsync(runtime, model, fixture);
        }
        await CheckRuntimeToolsSurviveUiReleaseAsync();
    }

    private static async Task CheckRuntimeToolsSurviveUiReleaseAsync()
    {
        RequireRuntimeFixtureStation();
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-tools-");
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            Require(model.Notes.SetShortcut(false, null, model.Gaming.ToggleShortcut) &&
                model.Notes.SetShortcut(true, null, model.Gaming.ToggleShortcut) && model.ClipRecall.SetShortcut(null, []) &&
                model.FilePack.SetShortcut(null, []), "Tool fixture shortcuts could not be cleared");
            Require(await model.Notes.FlushAsync() && await model.ClipRecall.FlushSettingsAsync(), "Tool fixture shortcuts did not save");
            await model.FilePack.PendingOperations;
            runtime = new(model, enableRuntime: true, idleDelay: TimeSpan.FromMinutes(5), shutdownApplication: false);
            runtime.WindowCreated += ConfigureRuntimeFixtureWindow;
            await Task.WhenAll(runtime.Notes!.Ready, runtime.ClipRecall!.Ready, runtime.FilePack!.Ready);
            var notes = runtime.Notes!; var clipboard = runtime.ClipRecall!; var pack = runtime.FilePack!;
            var source = runtime.SourceHandle;
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            var mainHandle = new WindowInteropHelper(runtime.Window!).Handle;
            await model.Notes.EnsureContentLoadedAsync();
            model.Notes.Text = string.Join('\n', Enumerable.Range(0, 100).Select(index => $"isolated runtime note line {index}"));
            model.Notes.IsEnabled = true;
            Require(await model.Notes.FlushAsync(), "Visible runtime note did not save");
            notes.Toggle(); await notes.Pending;
            var note = notes.NoteWindow!;
            Require(note.IsVisible, "Runtime note window did not open on the private station");
            note.NoteEditor.Select(note.NoteEditor.Text.Length, 0); note.NoteEditor.SelectedText = " retained undo fixture";
            Require(note.NoteEditor.CanUndo, "Runtime note fixture did not create an editing session");
            note.NoteEditor.Select(12, 7); note.UpdateLayout(); note.NoteEditor.ScrollToVerticalOffset(80);
            await SettleRuntimeFixtureAsync();
            var expectedText = model.Notes.Text; var expectedOffset = note.NoteEditor.VerticalOffset;
            var noteHandle = note.Handle;
            Require(await model.Notes.FlushAsync(), "Runtime editing session was not saved before releasing main UI");
            model.ClipRecall.RequestEnabled(true); await model.ClipRecall.PendingSettings; await clipboard.Pending;
            Require(clipboard.IsListening && clipboard.HasCaptureProcessor, "Isolated runtime clipboard did not start recording");
            Require(await model.FilePack.SetEnabledAsync(true), "Temporary FilePack switch did not enable");
            await model.FilePack.InitializeAsync();
            var sourcePath = Path.Combine(fixture.FullName, "background-source.bin");
            await File.WriteAllBytesAsync(sourcePath, RandomNumberGenerator.GetBytes(16 * 1024 * 1024));
            var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourcePath));
            var entries = (await new FileCollectionService().CollectAsync([sourcePath])).Entries;
            var task = await model.FilePack.Manager.EnqueueAsync(EngineSnapshot(entries, fixture.FullName,
                "background-after-ui-release", PackFormat.SevenZip, PackCompression.Maximum));
            await WaitPackStateAsync(model.FilePack.Manager, task.TaskId, info => info.State == PackState.Compressing);
            Require(model.FilePack.HasUnfinishedTasks, "Background task fixture was not running before UI release");
            runtime.Window!.Hide();
            Require(runtime.TryReleaseHiddenWindow() && runtime.Window is null && !ReviewIsWindow(mainHandle),
                "An independent background tool or compression task incorrectly prevented main-UI release");
            Require(runtime.SourceHandle == source && ReviewIsWindow(source) &&
                ReferenceEquals(runtime.Notes, notes) && ReferenceEquals(runtime.ClipRecall, clipboard) && ReferenceEquals(runtime.FilePack, pack) &&
                ReferenceEquals(notes.NoteWindow, note) && note.IsVisible && ReviewIsWindow(noteHandle) &&
                model.Notes.Text == expectedText && note.NoteEditor.CanUndo && note.NoteEditor.SelectionStart == 12 &&
                note.NoteEditor.SelectionLength == 7 && Math.Abs(note.NoteEditor.VerticalOffset - expectedOffset) < 1 &&
                clipboard.IsListening && clipboard.HasCaptureProcessor,
                "Releasing main UI replaced a tool controller, closed the note, lost its edit session or stopped clipboard recording");
            const string clipboardText = "isolated runtime capture after main UI release";
            ClipboardTestNative.Write(source, (13u, Encoding.Unicode.GetBytes(clipboardText + '\0')));
            await WaitClipboardAsync(async () => (await model.ClipRecall.Repository.QueryAsync(new())).Total > 0);
            var captured = await model.ClipRecall.Repository.QueryAsync(new());
            var capturedEntry = captured.Records.FirstOrDefault(record => record.Preview == clipboardText);
            Require(capturedEntry is not null && (await model.ClipRecall.Repository.GetAsync(capturedEntry.Id))?.Content.Text == clipboardText,
                "Stable source did not capture the private-station clipboard after main UI release");
            // The idle manager may already have unloaded completed snapshots while
            // native clipboard delivery was being checked. Observe durable records.
            PackTaskInfo? completed = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                completed = (await model.FilePack.Store.LoadTasksAsync()).SingleOrDefault(info => info.TaskId == task.TaskId);
                if (completed?.State is PackState.Completed or PackState.Cancelled or PackState.Interrupted or PackState.Failed) break;
                await Task.Delay(25);
            }
            Require(completed is { State: PackState.Completed } && File.Exists(completed.OutputPath) &&
                SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(sourceHash),
                "Main-UI release interrupted compression or modified its original source");
            note.NoteEditor.Undo(); Require(model.Notes.Text != expectedText && note.NoteEditor.CanRedo, "Note undo stopped after main UI release");
            note.NoteEditor.Redo(); Require(model.Notes.Text == expectedText, "Note redo stopped after main UI release");
            Require(await model.Notes.FlushAsync(), "Runtime note final editing session did not save");
            // A denied temporary settings write must cancel exit before FilePack
            // shuts down, and the prepared clipboard/notes runtimes must resume.
            await model.FilePack.FlushAsync();
            using (File.OpenHandle(Path.Combine(model.FilePack.Store.RootDirectory, "preferences.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                model.FilePack.MaximumParallel = 3;
                await model.FilePack.PendingOperations;
                runtime.RequestExit();
                await WaitClipboardAsync(() => Task.FromResult(runtime.Window is { IsExitInProgress: false } &&
                    model.Notice.StartsWith("文件集装箱尚未保存", StringComparison.Ordinal)));
                await clipboard.Pending;
                Require(!model.FilePack.IsStopping && clipboard.IsListening && clipboard.HasCaptureProcessor &&
                    !note.NoteEditor.IsReadOnly && runtime.Window!.IsVisible,
                    "A denied FilePack save left a prepared tool stopped after cancelling exit");
            }
            await model.FilePack.FlushAsync();
            Require((await model.FilePack.Store.LoadPreferencesAsync()).MaximumParallel == 3,
                "FilePack settings writes did not recover after the exit preflight failure");
            model.ClipRecall.RequestEnabled(false); await model.ClipRecall.PendingSettings; await clipboard.Pending;
            Require(!clipboard.IsListening && !clipboard.HasCaptureProcessor && !model.ClipRecall.Repository.IsReady,
                "Disabling isolated recording after UI release retained its runtime resources");
            Console.WriteLine("PASS Real isolated runtime controllers survive main UI release: note HWND/text/selection/scroll/undo/redo, private clipboard event capture and native FilePack completion; failed exit preflight resumes tools and settings; explicit disable tears down recording");
        }
        finally
        {
            // Every mutation belongs to these temporary stores. Save and explicitly
            // disable tools before a normal fixture exit; never discard live edits.
            try
            {
                if (model is not null && runtime is not null)
                {
                    Require(await model.Notes.FlushAsync(), "Could not save runtime tool fixture note during cleanup");
                    model.Notes.IsEnabled = false;
                    if (runtime.Notes is not null) await runtime.Notes.Pending;
                    model.ClipRecall.RequestEnabled(false); await model.ClipRecall.PendingSettings;
                    if (runtime.ClipRecall is not null) await runtime.ClipRecall.Pending;
                    Require(await model.FilePack.SetEnabledAsync(false) && await model.Notes.FlushAsync() &&
                        await model.ClipRecall.FlushSettingsAsync(), "Runtime tool fixture did not save and disable before exit");
                }
            }
            finally { await CloseRuntimeFixtureAsync(runtime, model, fixture); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedRuntimeWindowReference(ApplicationRuntimeHost runtime)
    {
        var window = runtime.Window!; window.UpdateLayout();
        window.Hide();
        Require(runtime.TryReleaseHiddenWindow() && runtime.Window is null, "Collectibility fixture did not release its window");
        return new WeakReference(window);
    }

    private sealed class RuntimePendingText
    { public string Value { get; set; } = ""; }

    private static async Task SettleRuntimeFixtureAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(75);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static void RequireRuntimeFixtureStation() => Require(ClipboardTestStation.Name.Length > 0 &&
        !ClipboardTestStation.Name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase), "Runtime fixture requires a private window station");

    private static async Task ExitRuntimeFixtureAsync(ApplicationRuntimeHost runtime)
    {
        if (runtime.SourceHandle == IntPtr.Zero) return;
        runtime.RequestExit();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (runtime.SourceHandle != IntPtr.Zero && DateTime.UtcNow < deadline) await Task.Delay(25);
        Require(runtime.SourceHandle == IntPtr.Zero && runtime.Window is null, "Isolated runtime did not finish its normal exit");
        runtime.Dispose();
    }

    private static async Task CloseRuntimeFixtureAsync(ApplicationRuntimeHost? runtime, MainViewModel? model, DirectoryInfo fixture)
    {
        try { if (runtime is not null) await ExitRuntimeFixtureAsync(runtime); }
        finally
        {
            // Normal exit owns the persistence guards. If it refuses to exit,
            // retain the temporary files and live fixture rather than force-dispose.
            if (runtime is null || runtime.SourceHandle == IntPtr.Zero)
            {
                try
                {
                    if (model is not null) { await model.ClipRecall.DisposeAsync(); await model.FilePack.DisposeAsync(); }
                }
                finally { DeleteRuntimeFixture(fixture); }
            }
        }
    }

    private static void DeleteRuntimeFixture(DirectoryInfo fixture)
    {
        FilePackPaths.EnsureNoReparsePoints(fixture.FullName);
        Require(FilePackPaths.IsWithin(fixture.FullName, Path.GetTempPath()) && fixture.Name.StartsWith("DailyToolkit-runtime-", StringComparison.Ordinal),
            "Runtime fixture escaped its temporary directory");
        fixture.Delete(recursive: true);
    }
}

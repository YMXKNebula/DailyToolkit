using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Controls;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private sealed record DeferredReleaseValues(double NotesScroll, double AppearanceScroll,
        double ClipboardScroll, double PackScroll);

    private static async Task CheckDeferredViewReleaseAsync()
    {
        RequireRuntimeFixtureStation();
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-deferred-release-");
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            var appliedTheme = model.Theme;
            var appliedNotes = model.Notes.Preferences;
            var appliedClipboard = model.ClipRecall.Appearance;
            var packAppearance = model.FilePack.SelectedAppearance;
            var appliedPack = packAppearance.Applied;
            model.ThemeColors.First().Value = "#674A91";
            model.Notes.PreviewBackgroundColor = "#79508B";
            model.ClipRecall.AppearanceColors.First().Value = "#536A8D";
            packAppearance.Options.First().Value = "#605478";
            runtime = new(model, enableRuntime: false, idleDelay: TimeSpan.FromMinutes(5), shutdownApplication: false);
            runtime.WindowCreated += ConfigureRuntimeFixtureWindow;
            runtime.ShowWindow();
            var window = runtime.Window!;
            window.Width = 980; window.Height = 640;
            var values = await PrepareDeferredReleaseValuesAsync(window, model);
            var mainHandle = new WindowInteropHelper(window).Handle;
            var source = runtime.SourceHandle;
            Require(!window.TryReleaseDeferredViewsForIdle(), "Visible editing pages were unloaded");
            var oldViews = ReleaseDeferredViewReferences(window);
            Console.WriteLine("INFO Deferred views immediately after detach: " + DescribeDeferredViewReferences(oldViews));
            Console.WriteLine("INFO Deferred view focus after detach: " + DescribeDeferredFocusReferences(window, oldViews));
            Require(ReferenceEquals(runtime.Window, window) && window.CreatedSettingsViewCount == 0 &&
                new WindowInteropHelper(window).Handle == mainHandle && ReviewIsWindow(mainHandle) &&
                runtime.SourceHandle == source && ReviewIsWindow(source) &&
                window.FindName("NotesSettings") is null && window.FindName("ClipRecallSettings") is null &&
                window.FindName("FilePackSettings") is null,
                "Partial release kept view registrations or changed the main window, HWND or message source");
            Require(!window.TryReleaseDeferredViewsForIdle(), "An empty page set requested repeated idle work");
            // Reachability only: memory profiles never call this correctness test.
            for (var attempt = 0; attempt < 10 && oldViews.Any(reference => reference.IsAlive); attempt++)
            {
                await SettleRuntimeFixtureAsync();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(50);
            }
            if (oldViews.Any(reference => reference.IsAlive))
                await WaitForDeferredFixtureDumpAsync(window, oldViews);
            Require(oldViews.All(reference => !reference.IsAlive),
                "An unloaded settings view remains reachable: " + DescribeDeferredViewReferences(oldViews) + "; " +
                DescribeDeferredFocusReferences(window, oldViews));

            model.Page = "floating-notes";
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            Require(ReferenceEquals(runtime.Window, window) && window.CreatedSettingsViewCount == 1,
                "Reopening recreated the main window or an unvisited settings page");
            CheckDeferredNotesValues(window, values);
            CheckDeferredPageAutomation(window, (NotesSettingsView)window.FindName("NotesSettings"));
            window.Hide();
            Require(window.TryReleaseDeferredViewsForIdle() && window.CreatedSettingsViewCount == 0,
                "A second partial release could not retain the still-unvisited page snapshots");
            runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
            CheckDeferredNotesValues(window, values);
            model.Page = "clip-recall"; await SettleRuntimeFixtureAsync();
            var clipboard = (ClipboardSettingsView)window.FindName("ClipRecallSettings");
            CheckDeferredPageAutomation(window, clipboard);
            Require(window.CreatedSettingsViewCount == 2 && UiStateFixtureDescendants(clipboard).OfType<Expander>().Single().IsExpanded &&
                Math.Abs(((ScrollViewer)clipboard.Content).VerticalOffset - values.ClipboardScroll) < 1,
                "Unvisited clipboard values were lost across repeated partial releases");
            model.Page = "file-pack";
            var pack = (FilePackSettingsView)window.FindName("FilePackSettings");
            pack.ContextMenuRegistration = new(new FilePackMenuFake());
            await SettleRuntimeFixtureAsync();
            CheckDeferredPageAutomation(window, pack);
            Require(window.CreatedSettingsViewCount == 3 && UiStateFixtureDescendants(pack).OfType<Expander>().Single().IsExpanded &&
                Math.Abs(((ScrollViewer)pack.Content).VerticalOffset - values.PackScroll) < 1,
                "Unvisited FilePack values were lost across repeated partial releases");
            Require(model.Theme == appliedTheme && model.HasThemeChanges && model.ThemeColors.First().Value == "#674A91" &&
                model.Notes.Preferences == appliedNotes && model.Notes.HasColorChanges && model.Notes.PreviewBackgroundColor == "#79508B" &&
                model.ClipRecall.Appearance == appliedClipboard && model.ClipRecall.HasAppearanceChanges &&
                model.ClipRecall.AppearanceColors.First().Value == "#536A8D" &&
                ReferenceEquals(model.FilePack.SelectedAppearance, packAppearance) && packAppearance.Applied == appliedPack &&
                packAppearance.HasChanges && packAppearance.Options.First().Value == "#605478",
                "Partial page release saved, applied or discarded a model-owned custom palette draft");

            model.Page = "clip-recall"; await SettleRuntimeFixtureAsync();
            var picker = UiStateFixtureDescendants(clipboard).OfType<ColorPicker>().First();
            var hex = (TextBox)picker.FindName("HexInput");
            var color = picker.Color;
            hex.SetCurrentValue(TextBox.TextProperty, "#12unfinished");
            window.Hide();
            Require(!window.TryReleaseDeferredViewsForIdle() && !window.IdleReleaseCanRetry &&
                window.CreatedSettingsViewCount == 3 && hex.Text == "#12unfinished" && picker.Color == color,
                "Partial page release discarded, committed or kept polling unfinished raw color input");
            hex.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            hex.IsUndoEnabled = false; hex.IsUndoEnabled = true;
            Require(window.TryReleaseDeferredViewsForIdle() && ReferenceEquals(runtime.Window, window) &&
                new WindowInteropHelper(window).Handle == mainHandle && runtime.SourceHandle == source,
                "Cancelling fixture-only input did not permit a safe partial release on the same window");
            Console.WriteLine("PASS Hidden idle unloads only visited settings views; main HWND/runtime remain, old views collect, lazy repeated reopening preserves per-page scroll/selection/toggles and palette drafts; unfinished raw input vetoes");
        }
        finally { await CloseRuntimeFixtureAsync(runtime, model, fixture); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckDeferredPageAutomation(MainWindow window, FrameworkElement page)
    {
        Require(UIElementAutomationPeer.CreatePeerForElement(window) is { } mainPeer &&
            mainPeer.GetAutomationControlType() == AutomationControlType.Window && mainPeer.GetName() == window.Title &&
            UIElementAutomationPeer.CreatePeerForElement(page) is not null,
            "Retiring an old settings page disabled accessibility for the retained main window or the recreated page");
        var button = UiStateFixtureDescendants(page).OfType<Button>().First(button =>
            button.GetType() == typeof(Button) && button.Content is string { Length: > 0 });
        var expectedName = AutomationProperties.GetName(button);
        if (string.IsNullOrEmpty(expectedName)) expectedName = (string)button.Content;
        Require(UIElementAutomationPeer.CreatePeerForElement(button) is { } peer &&
            peer.GetAutomationControlType() == AutomationControlType.Button && peer.GetName() == expectedName &&
            peer.GetPattern(PatternInterface.Invoke) is not null,
            "A recreated settings button lost its accessible name or invoke pattern");
    }

    private static async Task WaitForDeferredFixtureDumpAsync(MainWindow window, WeakReference[] references)
    {
        var requested = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_FIXTURE_DUMP_ROOT");
        if (string.IsNullOrWhiteSpace(requested)) return;
        RequireRuntimeFixtureStation();
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project is not null && !File.Exists(Path.Combine(project.FullName, "DailyToolkit.slnx")))
            project = project.Parent;
        Require(project is not null, "Fixture dump output requires the actual project checkout");
        var allowed = Path.GetFullPath(Path.Combine(project!.FullName, ".local", "memory-20261018"));
        var output = Path.GetFullPath(requested);
        Require(DailyToolkit.Core.FilePack.FilePackPaths.IsWithin(output, allowed),
            "Fixture dump output escaped the project's temporary memory evidence directory");
        DailyToolkit.Core.FilePack.FilePackPaths.EnsureNoReparsePoints(output);
        Directory.CreateDirectory(output);
        var ready = Path.Combine(output, "fixture-ready.json");
        var resume = Path.Combine(output, "fixture-resume");
        await File.WriteAllTextAsync(ready, JsonSerializer.Serialize(new
        {
            ProcessId = System.Environment.ProcessId,
            PrivateStation = ClipboardTestStation.Name,
            Views = DescribeDeferredViewReferences(references),
            Focus = DescribeDeferredFocusReferences(window, references)
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"INFO Owned private fixture {System.Environment.ProcessId} is ready for optional heap inspection; waiting up to 120 seconds");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(resume) && wait.Elapsed < TimeSpan.FromSeconds(120)) await Task.Delay(50);
    }

    private static async Task<DeferredReleaseValues> PrepareDeferredReleaseValuesAsync(MainWindow window, MainViewModel model)
    {
        model.Page = "floating-notes"; await SettleRuntimeFixtureAsync();
        var notes = (NotesSettingsView)window.FindName("NotesSettings");
        ((CheckBox)notes.FindName("PreviewFocused")).IsChecked = false;
        var notesScroll = (ScrollViewer)notes.FindName("SettingsScroll");
        var appearance = (ScrollViewer)notes.FindName("AppearanceScroll");
        notesScroll.ScrollToVerticalOffset(100); appearance.ScrollToVerticalOffset(120);
        ((TextBox)notes.FindName("PreviewText")).Select(2, 3);
        await SettleRuntimeFixtureAsync();
        var noteOffset = notesScroll.VerticalOffset;
        var appearanceOffset = appearance.VerticalOffset;
        model.Page = "clip-recall"; await SettleRuntimeFixtureAsync();
        var clipboard = (ClipboardSettingsView)window.FindName("ClipRecallSettings");
        UiStateFixtureDescendants(clipboard).OfType<Expander>().Single().IsExpanded = true;
        await SettleRuntimeFixtureAsync();
        var clipboardScroll = (ScrollViewer)clipboard.Content;
        clipboardScroll.ScrollToVerticalOffset(130); await SettleRuntimeFixtureAsync();
        var clipboardOffset = clipboardScroll.VerticalOffset;
        model.Page = "file-pack";
        var pack = (FilePackSettingsView)window.FindName("FilePackSettings");
        pack.ContextMenuRegistration = new(new FilePackMenuFake());
        await SettleRuntimeFixtureAsync();
        UiStateFixtureDescendants(pack).OfType<Expander>().Single().IsExpanded = true;
        await SettleRuntimeFixtureAsync();
        var packScroll = (ScrollViewer)pack.Content;
        packScroll.ScrollToVerticalOffset(115); await SettleRuntimeFixtureAsync();
        var packOffset = packScroll.VerticalOffset;
        Require(noteOffset > 0 && appearanceOffset > 0 && clipboardOffset > 0 && packScroll.VerticalOffset > 0,
            "Partial-release fixture did not establish independent scroll values");
        // Explicitly clear hoisted async locals before returning the value-only
        // result, so the correctness test cannot keep the old views itself.
        notes = null!; notesScroll = null!; appearance = null!;
        clipboard = null!; clipboardScroll = null!; pack = null!; packScroll = null!;
        return new(noteOffset, appearanceOffset, clipboardOffset, packOffset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ReleaseDeferredViewReferences(MainWindow window)
    {
        var references = new[] { "NotesSettings", "ClipRecallSettings", "FilePackSettings" }
            .Select(name => new WeakReference(window.FindName(name))).ToArray();
        Require(references.All(reference => reference.IsAlive), "Partial-release fixture did not visit all three settings pages");
        window.Hide();
        Require(window.TryReleaseDeferredViewsForIdle(), "Clean hidden settings or model-owned palette drafts vetoed partial release");
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DescribeDeferredViewReferences(WeakReference[] references)
    {
        return string.Join("; ", references.Select((reference, index) =>
        {
            if (reference.Target is not FrameworkElement view) return $"view{index}:collected";
            return $"view{index}:{view.GetType().Name},loaded={view.IsLoaded},logical={view.Parent?.GetType().Name ?? "null"}," +
                $"visual={VisualTreeHelper.GetParent(view)?.GetType().Name ?? "null"},data={view.DataContext?.GetType().Name ?? "null"}";
        }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DescribeDeferredFocusReferences(MainWindow window, WeakReference[] references)
    {
        return $"logical={Describe(FocusManager.GetFocusedElement(window))}; keyboard={Describe(Keyboard.FocusedElement)}";
        string Describe(IInputElement? focus)
        {
            if (focus is null) return "null";
            var dependency = focus as DependencyObject;
            var owners = references.Select((reference, index) =>
                reference.Target is FrameworkElement view && dependency is not null &&
                (ReferenceEquals(view, dependency) || view.IsAncestorOf(dependency)) ? index.ToString() : "-");
            return $"{focus.GetType().Name}:{(focus as FrameworkElement)?.Name},old-view={string.Join(",", owners)}";
        }
    }

    private static void CheckDeferredNotesValues(MainWindow window, DeferredReleaseValues values)
    {
        var notes = (NotesSettingsView)window.FindName("NotesSettings");
        var text = (TextBox)notes.FindName("PreviewText");
        Require(((CheckBox)notes.FindName("PreviewFocused")).IsChecked == false && text.SelectionStart == 2 && text.SelectionLength == 3 &&
            Math.Abs(((ScrollViewer)notes.FindName("SettingsScroll")).VerticalOffset - values.NotesScroll) < 1 &&
            Math.Abs(((ScrollViewer)notes.FindName("AppearanceScroll")).VerticalOffset - values.AppearanceScroll) < 1,
            "Recreated notes settings lost preview state, selection or independent scrolling");
    }
}

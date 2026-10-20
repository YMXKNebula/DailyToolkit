using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DailyToolkit.Core.Environment;
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
    private static async Task CheckUiStateRetentionAsync()
    {
        RequireRuntimeFixtureStation();
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-ui-state-");
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            var themeApplied = model.Theme;
            var notesApplied = model.Notes.Preferences;
            var clipboardApplied = model.ClipRecall.Appearance;
            var packAppearance = model.FilePack.SelectedAppearance;
            var packApplied = packAppearance.Applied;
            model.ThemeColors.First().Value = "#674A91";
            model.Notes.PreviewBackgroundColor = "#79508B";
            model.ClipRecall.AppearanceColors.First().Value = "#536A8D";
            packAppearance.Options.First().Value = "#605478";
            Require(model.HasThemeChanges && model.Notes.HasColorChanges && model.ClipRecall.HasAppearanceChanges &&
                packAppearance.HasChanges, "UI-state fixture did not create model-owned color drafts");
            runtime = new(model, enableRuntime: false, idleDelay: TimeSpan.FromMinutes(5), shutdownApplication: false);
            runtime.WindowCreated += ConfigureRuntimeFixtureWindow;
            // Keep the real model-owned collection view. A second fixture-only
            // ItemsSource assignment would exercise DataGrid's source-change
            // sorting reset instead of the production initial binding path.
            var softwareView = model.SoftwareView;
            softwareView.SortDescriptions.Add(new(nameof(InstalledSoftwareInfo.Publisher), ListSortDirection.Descending));
            var sortChanges = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)softwareView.SortDescriptions).CollectionChanged +=
                (_, _) => sortChanges++;
            runtime.ShowWindow();
            var window = runtime.Window!;
            window.Width = 980; window.Height = 640;
            // Record a user-visible, measured grid rather than pre-layout Auto
            // widths, whose first measurement can legitimately resize stars.
            model.Page = "settings-computer"; await SettleRuntimeFixtureAsync();
            var softwareDetails = (Expander)window.FindName("SoftwareExpander");
            softwareDetails.IsExpanded = true; await SettleRuntimeFixtureAsync();
            var softwareGrid = UiStateSoftwareGrid(window);
            softwareGrid.Columns[2].DisplayIndex = 0;
            softwareGrid.Columns[0].DisplayIndex = 2;
            softwareGrid.Columns[0].Width = new(214.5);
            softwareGrid.Columns[2].Width = DataGridLength.Auto;
            await SettleRuntimeFixtureAsync();
            softwareGrid.Columns[1].Width = new(2.25, DataGridLengthUnitType.Star);
            softwareGrid.Columns[1].SortDirection = ListSortDirection.Descending;
            await SettleRuntimeFixtureAsync();
            var expectedColumns = softwareGrid.Columns.Select((column, index) => new UiGridColumnState(index,
                column.Width.Value, column.Width.UnitType, column.DisplayIndex, column.SortDirection)).ToArray();
            Require(expectedColumns[1].Width == 2.25 && expectedColumns[2].WidthUnit == DataGridLengthUnitType.Auto,
                "Grid fixture did not establish a stable Auto width and star weight");
            softwareDetails.IsExpanded = false;
            model.Page = "settings-theme"; await SettleRuntimeFixtureAsync();
            var details = (Expander)window.FindName("ThemeDetails");
            details.IsExpanded = true; await SettleRuntimeFixtureAsync();
            var detailScroll = (ScrollViewer)details.Content;
            detailScroll.ScrollToVerticalOffset(85); await SettleRuntimeFixtureAsync();
            var expectedDetailsOffset = detailScroll.VerticalOffset;
            Require(expectedDetailsOffset > 0, "UI-state fixture did not scroll custom theme details");
            details.IsExpanded = false; await SettleRuntimeFixtureAsync();

            model.Page = "floating-notes"; await SettleRuntimeFixtureAsync();
            var notes = (NotesSettingsView)window.FindName("NotesSettings");
            ((CheckBox)notes.FindName("PreviewFocused")).IsChecked = false;
            var notesScroll = (ScrollViewer)notes.FindName("SettingsScroll");
            var appearanceScroll = (ScrollViewer)notes.FindName("AppearanceScroll");
            notesScroll.ScrollToVerticalOffset(100); appearanceScroll.ScrollToVerticalOffset(120);
            var notePreview = (TextBox)notes.FindName("PreviewText");
            notePreview.Select(2, 3); await SettleRuntimeFixtureAsync();
            var expectedNotesOffset = notesScroll.VerticalOffset;
            var expectedAppearanceOffset = appearanceScroll.VerticalOffset;
            Require(expectedNotesOffset > 0 && expectedAppearanceOffset > 0,
                "UI-state fixture did not scroll the note settings and its appearance section");

            model.Page = "clip-recall"; await SettleRuntimeFixtureAsync();
            var clipboard = (ClipboardSettingsView)window.FindName("ClipRecallSettings");
            var clipboardColors = UiStateFixtureDescendants(clipboard).OfType<Expander>().Single();
            clipboardColors.IsExpanded = true; await SettleRuntimeFixtureAsync();
            var clipboardScroll = (ScrollViewer)clipboard.Content;
            clipboardScroll.ScrollToVerticalOffset(130); await SettleRuntimeFixtureAsync();
            var expectedClipboardOffset = clipboardScroll.VerticalOffset;

            model.Page = "file-pack";
            var pack = (FilePackSettingsView)window.FindName("FilePackSettings");
            pack.ContextMenuRegistration = new FilePackContextMenuRegistration(new FilePackMenuFake());
            await SettleRuntimeFixtureAsync();
            var packColors = UiStateFixtureDescendants(pack).OfType<Expander>().Single();
            packColors.IsExpanded = true; await SettleRuntimeFixtureAsync();
            var packScroll = (ScrollViewer)pack.Content;
            packScroll.ScrollToVerticalOffset(115); await SettleRuntimeFixtureAsync();
            var expectedPackOffset = packScroll.VerticalOffset;
            Require(expectedClipboardOffset > 0 && expectedPackOffset > 0,
                "UI-state fixture did not scroll clipboard/FilePack settings");

            model.Page = "settings-theme"; await SettleRuntimeFixtureAsync();
            var settingsScroll = (ScrollViewer)window.FindName("SettingsScroll");
            settingsScroll.ScrollToVerticalOffset(175); await SettleRuntimeFixtureAsync();
            var expectedSettingsOffset = settingsScroll.VerticalOffset;
            var preview = (LensPreview)window.FindName("LensPhotoPreview");
            preview.RestorePointerPosition(new(0.27, 0.68));
            Require(window.CreatedSettingsViewCount == 3, "UI-state fixture did not visit its three deferred pages");
            window.Hide();
            Require(runtime.TryReleaseHiddenWindow(), "Model-owned color drafts or clean visited pages incorrectly vetoed UI release");

            // Neither rebuild visits a deferred page. Their unapplied states, and
            // the collapsed details' nested offset, must survive another release.
            for (var generation = 0; generation < 2; generation++)
            {
                runtime.ShowWindow(); await SettleRuntimeFixtureAsync();
                window = runtime.Window!;
                details = (Expander)window.FindName("ThemeDetails");
                Require(window.CreatedSettingsViewCount == 0 && !details.IsExpanded &&
                    Math.Abs(((ScrollViewer)window.FindName("SettingsScroll")).VerticalOffset - expectedSettingsOffset) < 1 &&
                    ((LensPreview)window.FindName("LensPhotoPreview")).CapturePointerPosition() == new LensPreviewPosition(0.27, 0.68),
                    $"UI generation {generation} created an unvisited page or lost collapsed state, page offset or preview position");
                if (generation == 0)
                {
                    var pendingGrid = window.CaptureUiState().Scopes.Single(scope => scope.Id == "main").Elements
                        .Single(element => element.Columns is { Length: 3 } columns && columns[0] == expectedColumns[0]);
                    Require(pendingGrid.Columns!.SequenceEqual(expectedColumns),
                        "An unvisited grid lost its pure-value column snapshot before the second idle release");
                    window.Hide();
                    Require(runtime.TryReleaseHiddenWindow(), "Unapplied deferred-page state prevented a second idle release");
                }
            }
            details.IsExpanded = true; await SettleRuntimeFixtureAsync();
            Require(Math.Abs(((ScrollViewer)details.Content).VerticalOffset - expectedDetailsOffset) < 1,
                $"Collapsed details lost its pending offset across releases: expected={expectedDetailsOffset}, actual={((ScrollViewer)details.Content).VerticalOffset}");

            model.Page = "floating-notes"; await SettleRuntimeFixtureAsync();
            notes = (NotesSettingsView)window.FindName("NotesSettings");
            notePreview = (TextBox)notes.FindName("PreviewText");
            Require(window.CreatedSettingsViewCount == 1,
                $"Visiting deferred notes created unrelated pages after two releases: views={window.CreatedSettingsViewCount}/1");
            Require(((CheckBox)notes.FindName("PreviewFocused")).IsChecked == false &&
                Math.Abs(((ScrollViewer)notes.FindName("SettingsScroll")).VerticalOffset - expectedNotesOffset) < 1 &&
                Math.Abs(((ScrollViewer)notes.FindName("AppearanceScroll")).VerticalOffset - expectedAppearanceOffset) < 1 &&
                notePreview.SelectionStart == 2 && notePreview.SelectionLength == 3,
                $"Deferred notes lost state after two releases: views={window.CreatedSettingsViewCount}; focused={((CheckBox)notes.FindName("PreviewFocused")).IsChecked}; " +
                $"outer={((ScrollViewer)notes.FindName("SettingsScroll")).VerticalOffset}/{expectedNotesOffset}; " +
                $"appearance={((ScrollViewer)notes.FindName("AppearanceScroll")).VerticalOffset}/{expectedAppearanceOffset}; " +
                $"selection={notePreview.SelectionStart}+{notePreview.SelectionLength}/2+3");
            model.Page = "clip-recall"; await SettleRuntimeFixtureAsync();
            clipboard = (ClipboardSettingsView)window.FindName("ClipRecallSettings");
            Require(window.CreatedSettingsViewCount == 2 && UiStateFixtureDescendants(clipboard).OfType<Expander>().Single().IsExpanded &&
                Math.Abs(((ScrollViewer)clipboard.Content).VerticalOffset - expectedClipboardOffset) < 1,
                "Deferred clipboard lost its expanded section or scroll after two releases");
            model.Page = "file-pack";
            pack = (FilePackSettingsView)window.FindName("FilePackSettings");
            pack.ContextMenuRegistration = new FilePackContextMenuRegistration(new FilePackMenuFake());
            await SettleRuntimeFixtureAsync();
            Require(window.CreatedSettingsViewCount == 3 && UiStateFixtureDescendants(pack).OfType<Expander>().Single().IsExpanded &&
                Math.Abs(((ScrollViewer)pack.Content).VerticalOffset - expectedPackOffset) < 1,
                "Deferred FilePack lost its expanded section or scroll after two releases");
            Require(model.Theme == themeApplied && model.HasThemeChanges && model.ThemeColors.First().Value == "#674A91" &&
                model.Notes.Preferences == notesApplied && model.Notes.HasColorChanges && model.Notes.PreviewBackgroundColor == "#79508B" &&
                model.ClipRecall.Appearance == clipboardApplied && model.ClipRecall.HasAppearanceChanges && model.ClipRecall.AppearanceColors.First().Value == "#536A8D" &&
                ReferenceEquals(model.FilePack.SelectedAppearance, packAppearance) && packAppearance.Applied == packApplied &&
                packAppearance.HasChanges && packAppearance.Options.First().Value == "#605478",
                "UI recreation discarded, saved or applied a custom palette draft");

            model.Page = "settings-computer"; await SettleRuntimeFixtureAsync();
            ((Expander)window.FindName("SoftwareExpander")).IsExpanded = true;
            await SettleRuntimeFixtureAsync();
            softwareGrid = UiStateSoftwareGrid(window);
            var actualColumns = softwareGrid.Columns.Select((column, index) => new UiGridColumnState(index,
                column.Width.Value, column.Width.UnitType, column.DisplayIndex, column.SortDirection)).ToArray();
            Require(actualColumns.SequenceEqual(expectedColumns),
                $"Grid columns lost width units, order or sort indicators after two releases: actual={string.Join(";", actualColumns)}, expected={string.Join(";", expectedColumns)}");
            Require(ReferenceEquals(softwareGrid.ItemsSource, softwareView) && sortChanges == 0 &&
                softwareView.SortDescriptions.SequenceEqual(new[] { new SortDescription(nameof(InstalledSoftwareInfo.Publisher), ListSortDirection.Descending) }),
                "Restoring grid header values replaced, cleared or repeated the retained collection-view sort");

            model.Page = "settings-theme"; await SettleRuntimeFixtureAsync();
            var picker = UiStateFixtureDescendants((Expander)window.FindName("ThemeDetails")).OfType<ColorPicker>().First();
            var hex = (TextBox)picker.FindName("HexInput");
            var draftBeforeRawInput = picker.Color;
            hex.SetCurrentValue(TextBox.TextProperty, "#12unfinished");
            window.Hide();
            Require(!runtime.TryReleaseHiddenWindow() && !window.IdleReleaseCanRetry &&
                hex.Text == "#12unfinished" && picker.Color == draftBeforeRawInput && model.Theme == themeApplied,
                "UI release discarded or committed an unfinished OneWay hex edit, or polled a persistent draft");
            // Fixture-only cancel of raw input; do not invoke CommitColor/Save.
            hex.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            hex.IsUndoEnabled = false; hex.IsUndoEnabled = true;
            Require(runtime.TryReleaseHiddenWindow(), "Cancelling the fixture-only raw hex edit did not restore UI releasability");
            Console.WriteLine("PASS UI value snapshots preserve collapsed/nested scrolling, independent deferred pages across two releases, unbound notes preview/text selection, lens position, grid column width/order/sort indicators and unsaved palette drafts; grid sort is not repeated; raw hex input vetoes without saving or polling");
        }
        finally
        {
            if (runtime is not null) await ExitRuntimeFixtureAsync(runtime);
            if (model is not null) { await model.ClipRecall.DisposeAsync(); await model.FilePack.DisposeAsync(); }
            DeleteRuntimeFixture(fixture);
        }
    }

    private static DataGrid UiStateSoftwareGrid(MainWindow window) =>
        UiStateFixtureDescendants((Expander)window.FindName("SoftwareExpander")).OfType<DataGrid>().Single();

    private static IEnumerable<DependencyObject> UiStateFixtureDescendants(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        return Walk(root);
        IEnumerable<DependencyObject> Walk(DependencyObject current)
        {
            if (!seen.Add(current)) yield break;
            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current))
                if (child is DependencyObject dependency)
                    foreach (var value in Walk(dependency)) yield return value;
            if (current is not Visual) yield break;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                foreach (var value in Walk(VisualTreeHelper.GetChild(current, index))) yield return value;
        }
    }
}

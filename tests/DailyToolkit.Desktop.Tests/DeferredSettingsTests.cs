using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckDeferredSettingsViewsAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-deferred-settings-");
        MainViewModel? model = null;
        MainWindow? window = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            model = ResourceHost(fixture.FullName);
            await Task.WhenAll(model.Notes.Ready, model.ClipRecall.Ready, model.FilePack.SettingsReady);
            model.Gaming.IsEnabled = false;
            model.Notes.IsEnabled = false;
            model.ClipRecall.RequestEnabled(false);
            await model.ClipRecall.PendingSettings;
            Require(await model.FilePack.SetEnabledAsync(false), "Could not disable the isolated FilePack fixture");
            window = new(model, enableShortcuts: false)
            {
                Width = 1040, Height = 760, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
            };
            window.Closed += (_, _) => closed.TrySetResult();
            var notesHost = (ContentControl)window.FindName("NotesSettingsHost");
            var clipboardHost = (ContentControl)window.FindName("ClipRecallSettingsHost");
            var packHost = (ContentControl)window.FindName("FilePackSettingsHost");
            Require(notesHost.Content is null && clipboardHost.Content is null && packHost.Content is null,
                "Constructing the main window eagerly created unused settings pages");
            window.Show(); await LayoutAsync();
            Require(notesHost.Content is null && clipboardHost.Content is null && packHost.Content is null &&
                !File.Exists(model.ClipRecall.Repository.DatabasePath),
                "Displaying another page initialized unused settings or a disabled history database");

            model.Page = "floating-notes"; await LayoutAsync();
            var notes = notesHost.Content as NotesSettingsView;
            Require(notes is not null && notes.IsVisible && ReferenceEquals(notes.DataContext, model.Notes) &&
                clipboardHost.Content is null && packHost.Content is null,
                "First navigation did not create only the requested notes page");
            var width = (TextBox)notes!.FindName("NoteWidth");
            width.Text = "713";
            var noteWidthBefore = model.Notes.Width;
            var scroll = (ScrollViewer)notes.FindName("SettingsScroll");
            scroll.ScrollToVerticalOffset(40); notes.UpdateLayout();
            var offsetBefore = scroll.VerticalOffset;
            model.Page = "screen-lens"; await LayoutAsync();
            window.Hide(); await LayoutAsync(); window.Show();
            model.Page = "floating-notes"; await LayoutAsync();
            Require(ReferenceEquals(notesHost.Content, notes) && width.Text == "713" &&
                model.Notes.Width == noteWidthBefore && scroll.VerticalOffset == offsetBefore,
                "Revisiting settings lost uncommitted input or the existing scroll session");

            model.Page = "clip-recall"; await LayoutAsync();
            var clipboard = clipboardHost.Content as ClipboardSettingsView;
            Require(clipboard is not null && clipboard.IsVisible && ReferenceEquals(clipboard.DataContext, model.ClipRecall) &&
                packHost.Content is null, "Lazy clipboard page lost its model or created another unused page");
            var draft = model.ClipRecall.AppearanceColors.First();
            var applied = model.ClipRecall.Appearance;
            draft.Value = "#8350A4";
            model.Page = "floating-notes"; await LayoutAsync();
            model.Page = "clip-recall"; await LayoutAsync();
            Require(ReferenceEquals(clipboardHost.Content, clipboard) && draft.Value == "#8350A4" &&
                model.ClipRecall.Appearance == applied && model.ClipRecall.HasAppearanceChanges,
                "Revisiting settings discarded or prematurely applied a custom palette draft");

            model.Page = "file-pack";
            var pack = packHost.Content as FilePackSettingsView;
            Require(pack is not null, "First FilePack navigation did not construct its settings page");
            pack!.ContextMenuRegistration = new FilePackContextMenuRegistration(new FilePackMenuFake());
            await LayoutAsync();
            Require(pack.IsVisible && ReferenceEquals(pack.DataContext, model.FilePack),
                "Lazy FilePack page lost its model");
            foreach (var host in new[] { notesHost, clipboardHost, packHost })
                Require(host.HorizontalContentAlignment == HorizontalAlignment.Stretch && host.VerticalContentAlignment == VerticalAlignment.Stretch,
                    "Lazy settings host changed page sizing");
            model.Page = "screen-lens"; await LayoutAsync();
            model.Page = "file-pack"; await LayoutAsync();
            Require(ReferenceEquals(packHost.Content, pack), "Revisiting FilePack recreated its settings session");
            Console.WriteLine("PASS Unvisited settings remain unconstructed without disabled database initialization; first navigation, layout and repeated input/palette/scroll sessions survive hide and return");
        }
        finally
        {
            if (window is not null && !closed.Task.IsCompleted)
            { window.RequestExit(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            if (model is not null)
            {
                await model.ClipRecall.DisposeAsync(); await model.FilePack.DisposeAsync();
                if (window is null) model.Dispose();
            }
            DeleteClipboardFixture(fixture);
        }

        async Task LayoutAsync()
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.ContextIdle);
        }
    }
}

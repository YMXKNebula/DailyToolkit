using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckDiagnosticReportsAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-diagnostics-");
        try
        {
            var diagnostics = new DiagnosticReportService();
            const string secret = "private-note-clipboard-C:\\Users\\SecretUser\\document.txt";
            for (var index = 0; index < 80; index++)
                diagnostics.RecordError(DiagnosticModule.FloatingNotes, new IOException(secret));
            diagnostics.RecordError((DiagnosticModule)999, new InvalidOperationException(secret));
            diagnostics.RecordError(DiagnosticModule.Diagnostics, new UnauthorizedAccessException(secret));
            var states = new[] { new DiagnosticModuleState(DiagnosticModule.ScreenLens, false),
                new DiagnosticModuleState(DiagnosticModule.FilePack, true) };
            var destination = Path.Combine(fixture.FullName, "report.json");
            await diagnostics.ExportAsync(destination, states);
            var bytes = await File.ReadAllBytesAsync(destination);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            using (var document = JsonDocument.Parse(bytes))
            {
                var root = document.RootElement;
                Require(root.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(
                    new[] { "schemaVersion", "version", "uptimeMinutes", "modules", "resources", "recentErrors" }.Order()),
                    "Diagnostic schema accepted additional fields");
                var errors = root.GetProperty("recentErrors");
                Require(errors.GetArrayLength() == DiagnosticReportService.MaximumErrors &&
                    errors[errors.GetArrayLength() - 1].GetProperty("category").GetString() == "AccessDenied" &&
                    errors[errors.GetArrayLength() - 2].GetProperty("module").GetString() == "Application",
                    "Diagnostic bounds, categories or enum sanitization failed");
                foreach (var error in errors.EnumerateArray())
                    Require(error.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(
                        new[] { "minute", "module", "category" }.Order()), "Diagnostic error exported arbitrary details");
                Require(root.GetProperty("modules")[0].GetProperty("enabled").GetBoolean() == false,
                    "Diagnostic report lost disabled module status");
                var resources = diagnostics.Capture(states).Resources;
                Require((resources.PrivateMemoryMiB is null || resources.PrivateMemoryMiB % 8 == 0) &&
                    (resources.Handles is null || resources.Handles % 10 == 0), "Resource counters were not bucketed");
            }
            Require(!text.Contains(secret, StringComparison.Ordinal) && !text.Contains("SecretUser", StringComparison.Ordinal) &&
                !text.Contains(fixture.FullName, StringComparison.Ordinal) &&
                !text.Contains("stackTrace", StringComparison.OrdinalIgnoreCase) && bytes.Length < 24 * 1024,
                "Diagnostic report exposed user details or exceeded its bound");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await diagnostics.ExportAsync(destination, states, canceled.Token); throw new InvalidOperationException("Canceled export succeeded"); }
                catch (OperationCanceledException) { }
            }
            var preservedBytes = await File.ReadAllBytesAsync(destination);
            Require(bytes.AsSpan().SequenceEqual(preservedBytes) && Directory.GetFiles(fixture.FullName, "*.tmp").Length == 0,
                "Canceled diagnostic export damaged the existing report or retained staging data");
            var blocked = Path.Combine(fixture.FullName, "blocked.json"); Directory.CreateDirectory(blocked);
            try { await diagnostics.ExportAsync(blocked, states); throw new InvalidOperationException("Blocked export succeeded"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            Require(Directory.Exists(blocked) && Directory.GetFiles(fixture.FullName, "*.tmp").Length == 0,
                "Failed diagnostic export removed a destination or retained staging data");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                for (var index = 0; index < 300; index++)
                    diagnostics.RecordError(DiagnosticModule.ClipRecall, new TimeoutException(secret));
            })));
            Require(diagnostics.Capture(states).RecentErrors.Length == DiagnosticReportService.MaximumErrors,
                "Concurrent diagnostic records escaped their memory bound");
            diagnostics.ClearRecentErrors();
            Require(diagnostics.Capture(states).RecentErrors.Length == 0, "Clearing diagnostics retained previous records");
            Console.WriteLine("PASS Diagnostic privacy allowlist, bounded memory, concurrent writes, cleanup and canceled/failed export preserve existing files");
        }
        finally { fixture.Delete(recursive: true); }
    }

    private static async Task CheckDiagnosticReportLayoutAsync(string? images = null)
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-diagnostic-layout-");
        MainWindow? window = null;
        try
        {
            var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
            var probe = new ControlledProbe(display); probe.Finish.TrySetResult();
            var appStore = new AppPreferencesStore(Path.Combine(fixture.FullName, "settings.json"));
            appStore.Save(new() { Theme = ThemePalette.Dark, PageAnimationsEnabled = false });
            var model = new MainViewModel(probe, display, new LocalProbe(),
                favoritesStore: new(Path.Combine(fixture.FullName, "favorites.json")),
                gamingPreferencesStore: new GamingPreferencesStore(Path.Combine(fixture.FullName, "gaming.json")),
                navigationStore: new(Path.Combine(fixture.FullName, "navigation.json")), appPreferencesStore: appStore,
                startupRegistration: new StartupFake(), notesStore: new NotesStore(Path.Combine(fixture.FullName, "notes")),
                clipboardSettingsStore: new ClipboardSettingsStore(Path.Combine(fixture.FullName, "clipboard")),
                filePackStore: new FilePackStore(Path.Combine(fixture.FullName, "filepack")));
            model.OpenSettingsCommand.Execute(null); model.Page = "settings-about";
            window = new MainWindow(model, enableShortcuts: false)
            {
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000
            };
            window.Show();
            await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            var section = (Expander)window.FindName("DiagnosticsSection");
            var export = (Button)window.FindName("ExportDiagnosticsButton");
            Require(!section.IsExpanded && export.Style == window.FindResource(typeof(Button)),
                "Diagnostics did not reuse the shared collapsed section/button style");
            section.IsExpanded = true;
            foreach (var size in new[] { new Size(680, 540), new Size(1040, 800) })
            {
                window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                Require(export.ActualWidth > 0 && export.ActualWidth <= section.ActualWidth &&
                    ((SolidColorBrush)export.Foreground).Color == ((SolidColorBrush)window.FindResource("TextBrush")).Color &&
                    ((SolidColorBrush)export.Background).Color == ((SolidColorBrush)window.FindResource("SurfaceBrush")).Color,
                    "Diagnostic action overflowed or lost the active dark theme");
                foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                {
                    var root = window.PreviewContent;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
                        (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    if (images is null) continue;
                    Directory.CreateDirectory(images);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var target = File.Create(Path.Combine(images, $"diagnostics-dark-{size.Width}-{scale * 100:0}.png"));
                    encoder.Save(target);
                }
            }
            Console.WriteLine("PASS Diagnostic entry shared styles and dark layouts at two window sizes; 100/150/200 percent render checks (not mixed-monitor validation)");
        }
        finally
        {
            if (window is not null) { window.Close(); await Task.Delay(100); }
            fixture.Delete(recursive: true);
        }
    }
}

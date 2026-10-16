using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static readonly DirectoryInfo NotesFixtures = Directory.CreateTempSubdirectory("DailyToolkit-desktop-notes-fixtures-");
    private static DailyToolkit.Desktop.Notes.NotesStore IsolatedNotes() => new(Path.Combine(NotesFixtures.FullName, Guid.NewGuid().ToString("N")));
    private static DailyToolkit.Desktop.ClipRecall.ClipboardSettingsStore IsolatedClipboard() => new(Path.Combine(NotesFixtures.FullName, "clipboard-" + Guid.NewGuid().ToString("N")));
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--filepack-ipc-client", var pipe, var file])
            return RunFilePackIpcClientAsync(pipe, file).GetAwaiter().GetResult();
        if (args is ["--filepack-crash-worker", var fixture, var engine])
            return RunFilePackCrashWorkerAsync(fixture, engine).GetAwaiter().GetResult();
        try
        {
            if (args is ["--clipboard-native-checks"] or ["--clipboard-native-visuals", _] or ["--clipboard-native-palette-checks", _] or ["--clipboard-native-filepack-checks", _] or ["--clipboard-native-filepack-quick-checks", _]) ClipboardTestStation.Connect();
            else if (args.Length is 4 or 5 && args[0] == "--clipboard-native-writer") ClipboardTestStation.Connect(args[1], args[2]);
        }
        catch (Exception exception) { Console.WriteLine("FAIL Clipboard test isolation: " + exception); return 1; }
        if (args.Length > 0 && args[0].StartsWith("--clipboard-native-", StringComparison.Ordinal))
        {
            var result = 1;
            var isolatedThread = new Thread(() =>
            {
                try { ClipboardTestStation.ConnectThread(); result = RunDesktopChecks(args); }
                catch (Exception exception) { Console.WriteLine("FAIL Clipboard test desktop isolation: " + exception); }
            });
            isolatedThread.SetApartmentState(ApartmentState.STA); isolatedThread.Start(); isolatedThread.Join(); return result;
        }
        return RunDesktopChecks(args);
    }

    // Keep WPF initialization out of the entry method's JIT path so isolated
    // clipboard tests connect their station before any hidden WPF HWND exists.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunDesktopChecks(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DailyToolkit;component/Theme.xaml", UriKind.Relative)
        });
        var exitCode = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args is ["--clipboard-native-filepack-quick-checks", var quickImages])
                { await CheckFilePackQuickAsync(quickImages); exitCode = 0; return; }
                if (args is ["--clipboard-native-filepack-checks", var filePackImages])
                {
                    await CheckFilePackEngineAsync(); await CheckFilePackCrashRecoveryAsync();
                    await CheckFilePackVisualsAsync(filePackImages); await CheckFilePackExitRetryAsync();
                    await CheckFilePackQuickAsync(Path.Combine(filePackImages, "quick"));
                    CheckFavorites(); CheckNavigation(); CheckAppSettings(); exitCode = 0; return;
                }
                if (args is ["--clipboard-native-checks"])
                { await CheckClipboardNativeAsync(); await CheckClipboardNativeMediaAsync(); exitCode = 0; return; }
                if (args is ["--clipboard-shortcut-checks"])
                { await CheckClipboardNativeShortcutsAsync(); await CheckFilePackShortcutNoticesAsync(); exitCode = 0; return; }
                if (args is ["--clipboard-native-visuals", var clipboardImages])
                { await CheckClipboardVisualsAsync(clipboardImages); exitCode = 0; return; }
                if (args is ["--clipboard-native-palette-checks", var paletteImages])
                {
                    await CheckNotesPersistenceAsync(); await CheckNotesStabilityAsync();
                    await CheckPalettePersistenceAsync(); await CheckThemeEditingAsync();
                    await CheckClipboardViewModelAsync(); await CheckClipboardVisualsAsync(Path.Combine(paletteImages, "clipboard"));
                    await CheckNotesAppearanceAndNavigationAsync(Path.Combine(paletteImages, "notes"));
                    await CheckPreviewLayoutAsync(Path.Combine(paletteImages, "lens")); exitCode = 0; return;
                }
                if (args.Length is 4 or 5 && args[0] == "--clipboard-native-writer")
                { await RunClipboardWriterAsync(args[3], args.Length == 5 ? args[4] : null); exitCode = 0; return; }
                if (args is ["--clipboard-checks"])
                { await CheckClipboardRepositoryAsync(); await CheckClipboardProcessorAsync(); await CheckClipboardViewModelAsync(); exitCode = 0; return; }
                if (args is ["--clipboard-media-checks"])
                { await CheckClipboardMediaAsync(); exitCode = 0; return; }
                if (args is ["--safe-regression-checks"])
                {
                    CheckShortcutPersistence(); CheckRenamedSettingsCompatibility(); CheckFavorites(); CheckNavigation(); CheckAppSettings();
                    await CheckNotesPersistenceAsync(); await CheckNotesStabilityAsync(); CheckNotesFocus();
                    await CheckLensStartupTimingAsync(); CheckRendererRepeatedDispose(); exitCode = 0; return;
                }
                if (args is ["--update-checks"])
                { await CheckAppUpdatesAsync(); exitCode = 0; return; }
                if (args is ["--update-live", var updateDirectory])
                { await CheckLiveAppUpdatesAsync(updateDirectory); exitCode = 0; return; }
                if (args is ["--notes-input-backdrop", var readyFile])
                { await RunNotesInputBackdropAsync(readyFile); exitCode = 0; return; }
                if (args is ["--lens-image-checks"])
                { LensDiagnostics.CheckShader();CheckLensUpgrade();exitCode=0;return; }
                if (args is ["--startup-timing-checks"])
                { await CheckLensStartupTimingAsync(); exitCode=0; return; }
                if (args is ["--renderer-dispose-checks"])
                { CheckRendererRepeatedDispose(); exitCode=0; return; }
                if (args is ["--stability-checks"])
                { await CheckLensStartupTimingAsync(); await CheckNotesStabilityAsync(); exitCode=0; return; }
                if (args is ["--resource-stability", var resourceReport])
                { await CheckLensResourceStabilityAsync(resourceReport); exitCode=0; return; }
                if (args is ["--notes-checks", var notesImages])
                {
                    await CheckNotesPersistenceAsync(); CheckNotesFocus(); await CheckNotesShortcutsAsync();
                    await CheckNotesAppearanceAndNavigationAsync(notesImages); await CheckNotesWindowAsync(notesImages); exitCode = 0; return;
                }
                if (args is ["--notes-live", var notesReport])
                { await CheckLiveNotesAsync(notesReport); exitCode = 0; return; }
                if (args is ["--notes-cross-process-repeat"])
                { await CheckNotesRepeatedCrossProcessAsync(); exitCode = 0; return; }
                if (args is ["--drag-benchmark",var output])
                {
                    await RunDragBenchmarkAsync(output); exitCode=0; return;
                }
                if (args is ["--interface-checks",var images])
                { await CheckToolkitInterfaceAsync(images); exitCode=0; return; }
                if (args is ["--lens-upgrade-live",var outputPath])
                {await CheckLiveImageUpgradeAsync(outputPath);exitCode=0;return;}
                if (args is ["--lens-upgrade-live",var matrixPath,var monitorIndex])
                {await CheckLiveImageUpgradeAsync(matrixPath,int.Parse(monitorIndex));exitCode=0;return;}
                if (!args.Contains("--live-lens-only")) await CheckAsync();
                if (args.Contains("--live-lens") || args.Contains("--live-lens-only"))
                { await CheckLiveLensMovementAsync(); await CheckLivePresetRelayAsync(); await CheckLivePresetRelayAsync(16); await CheckLiveImageUpgradeAsync(); }
                exitCode = 0;
            }
            catch (Exception exception) { Console.WriteLine($"FAIL {exception}"); }
            finally { app.Shutdown(); }
        };
        try { app.Run(); }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(NotesFixtures.FullName).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
                NotesFixtures.Name.StartsWith("DailyToolkit-desktop-notes-fixtures-", StringComparison.Ordinal)) NotesFixtures.Delete(recursive: true);
        }
        return exitCode;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task CheckAsync()
    {
        await CheckWeatherCacheAsync();
        await CheckAppUpdatesAsync();
        await CheckClipboardRepositoryAsync(); await CheckClipboardProcessorAsync(); await CheckClipboardViewModelAsync();
        CheckShortcutPersistence();
        CheckRenamedSettingsCompatibility();
        CheckFavorites();
        CheckNavigation();
        CheckAppSettings();
        await CheckStartupSettingsAsync();
        await CheckTrayLifecycleAsync();
        await CheckSingleInstanceAsync();
        await CheckBackgroundShortcutAsync();
        await CheckNotesPersistenceAsync();
        await CheckNotesStabilityAsync();
        CheckNotesFocus();
        await CheckNotesShortcutsAsync();
        await CheckNotesAppearanceAndNavigationAsync();
        await CheckNotesWindowAsync();
        LensDiagnostics.CheckShader();
        CheckLensUpgrade();
        CheckRendererRepeatedDispose();
        Console.WriteLine("PASS Actual Direct3D shader preserves colors and reconstructs subpixel edges");
        await CheckPhotoPreviewAsync();
        CheckLensPointer();
        await CheckLensStartupTimingAsync();
        await CheckPreviewLayoutAsync();
        await CheckTransitionContinuityAsync();
        await CheckThemeEditingAsync();
        await CheckToolkitInterfaceAsync();
        await CheckLifecycleAsync();
        await CheckDetectorDiagnosticsAsync();
        Console.WriteLine("Desktop checks passed");
    }
}

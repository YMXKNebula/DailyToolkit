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
    [STAThread]
    public static int Main(string[] args)
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
                if (args is ["--notes-input-backdrop", var readyFile])
                { await RunNotesInputBackdropAsync(readyFile); exitCode = 0; return; }
                if (args is ["--lens-image-checks"])
                { LensDiagnostics.CheckShader();CheckLensUpgrade();exitCode=0;return; }
                if (args is ["--notes-checks", var notesImages])
                {
                    await CheckNotesPersistenceAsync(); CheckNotesFocus(); await CheckNotesShortcutsAsync();
                    await CheckNotesAppearanceAndNavigationAsync(notesImages); await CheckNotesWindowAsync(notesImages); exitCode = 0; return;
                }
                if (args is ["--notes-live", var notesReport])
                { await CheckLiveNotesAsync(notesReport); exitCode = 0; return; }
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
        app.Run();
        return exitCode;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task CheckAsync()
    {
        await CheckWeatherCacheAsync();
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
        CheckNotesFocus();
        await CheckNotesShortcutsAsync();
        await CheckNotesAppearanceAndNavigationAsync();
        await CheckNotesWindowAsync();
        LensDiagnostics.CheckShader();
        CheckLensUpgrade();
        Console.WriteLine("PASS Actual Direct3D shader preserves colors and reconstructs subpixel edges");
        await CheckPhotoPreviewAsync();
        CheckLensPointer();
        await CheckPreviewLayoutAsync();
        await CheckTransitionContinuityAsync();
        await CheckThemeEditingAsync();
        await CheckToolkitInterfaceAsync();
        await CheckLifecycleAsync();
        await CheckDetectorDiagnosticsAsync();
        Console.WriteLine("38/38 desktop checks passed");
    }
}

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
                if (args is ["--drag-benchmark",var output])
                {
                    await RunDragBenchmarkAsync(output); exitCode=0; return;
                }
                if (!args.Contains("--live-lens-only")) await CheckAsync();
                if (args.Contains("--live-lens") || args.Contains("--live-lens-only")) await CheckLiveLensMovementAsync();
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
        LensDiagnostics.CheckShader();
        Console.WriteLine("PASS Actual Direct3D shader preserves colors and reconstructs subpixel edges");
        await CheckPhotoPreviewAsync();
        CheckLensPointer();
        await CheckPreviewLayoutAsync();
        await CheckTransitionContinuityAsync();
        await CheckThemeEditingAsync();
        await CheckLifecycleAsync();
        await CheckDetectorDiagnosticsAsync();
        Console.WriteLine("25/25 desktop checks passed");
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Desktop.Environment;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop;

public partial class App : Application
{
    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--configure-admin-startup",var userSid])
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(StartupRegistration.ConfigureElevated(userSid)); return;
        }
        if (e.Args is ["--exit"])
        {
            using var instance=new SingleInstance(); instance.ActivateExistingWindow(exit:true); Shutdown(); return;
        }
        if (e.Args is ["--benchmark-lens",var benchmarkOutput,var referenceShader])
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try { await LensRenderBenchmark.RunAsync(Path.GetFullPath(benchmarkOutput),Path.GetFullPath(referenceShader)); Shutdown(); }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.GetFullPath(benchmarkOutput)+".error.txt",exception.ToString()); Shutdown(1);
            }
            return;
        }
        if (e.Args is ["--check-lens-shortcuts", var shortcutOutput])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await LensShortcutDiagnostics.RunAsync(Path.GetFullPath(shortcutOutput)); Shutdown(); }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.GetFullPath(shortcutOutput)+".error.txt",exception.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args is ["--check-lens", var outputPath])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await LensDiagnostics.RunCaptureAsync(Path.GetFullPath(outputPath)); Shutdown(); }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.GetFullPath(outputPath)+".error.txt",exception.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args is ["--read-windows-weather"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.OutputEncoding = new UTF8Encoding(false);
            var weather = await new WindowsWeatherCacheReader().ReadAsync(DateTimeOffset.UtcNow);
            if (!weather.Available)
            {
                var taskbarWeather = await Task.Run(WindowsTaskbarWeatherReader.Read);
                if (taskbarWeather.Available) weather = taskbarWeather;
            }
            Console.Write(JsonSerializer.Serialize(weather, MachineReport.JsonOptions));
            Shutdown();
            return;
        }
        var preview=e.Args.Length >= 2 && e.Args[0] == "--preview";
        string? startupRecoveryNotice=null;
        if (!preview && !(e.Args.Length == 2 && e.Args[0] == "--diagnose"))
        {
            _instance=new();
            if (e.Args.Contains("--admin-task"))
            {
                var registration=new StartupRegistration();
                if (!registration.IsAdministrator) { _instance.Dispose(); _instance=null; Shutdown(2); return; }
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while (!_instance.IsPrimary && DateTime.UtcNow < deadline)
                {
                    _instance.Dispose(); await Task.Delay(150); _instance=new();
                }
            }
            if (!_instance.IsPrimary) { _instance.ActivateExistingWindow(); Shutdown(); return; }
            var preferences=new AppPreferencesStore().Load();
            var startupRegistration=new StartupRegistration();
            if (preferences.AdminStartup && !startupRegistration.IsAdministrator)
            {
                try
                {
                    if (!startupRegistration.Read().Administrator) throw new InvalidOperationException("管理员启动任务已被移除，请重新启用管理员自启。");
                    startupRegistration.LaunchAdministrator(requireRegisteredTask:true); Shutdown(); return;
                }
                catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // An externally removed/disabled task must leave settings reachable.
                    // No automatic UAC fallback is used when a registered task fails.
                    startupRecoveryNotice=exception.Message+" 本次以普通权限打开设置，请关闭或重新启用管理员自启。";
                    new AppPreferencesStore().Save(preferences with { StartAtLogin=false,AdminStartup=false });
                }
            }
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
        }
        ApplyAccessibilityColors();
        DirectoryInfo? previewDirectory=null;
        var preferencesStore=new AppPreferencesStore();
        if (preview)
        {
            previewDirectory=Directory.CreateTempSubdirectory("DailyToolkit-page-preview-");
            var localPreferences=preferencesStore.Load();
            preferencesStore=new(Path.Combine(previewDirectory.FullName,"settings.json"));
            preferencesStore.Save(localPreferences);
        }
        var viewModel = new MainViewModel(new WindowsEnvironmentProbe(), NativeWindowsInfo.ReadDisplay(),appPreferencesStore:preferencesStore);
        if (startupRecoveryNotice is not null) viewModel.Page="settings";
        try
        {
            // These switches produce local artifacts for development; the normal app opens immediately.
            if (e.Args.Length == 2 && e.Args[0] == "--diagnose")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await viewModel.InitializeAsync();
                await File.WriteAllTextAsync(Path.GetFullPath(e.Args[1]), viewModel.ExportJson(), new UTF8Encoding(false));
                viewModel.Dispose();
                Shutdown();
                return;
            }

            var window = new MainWindow(viewModel, enableShortcuts: !preview);
            MainWindow = window;
            if (startupRecoveryNotice is not null) window.Loaded += (_,_) => viewModel.ShowStartupRecovery(startupRecoveryNotice);
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ContentRendered += (_, _) =>
            {
                if (firstFrame.TrySetResult())
                {
                    viewModel.SetFirstFrameTime(_startup.Elapsed.TotalMilliseconds);
                    if (!preview) _ = LensGpuRenderer.WarmShaderAsync();
                }
            };
            if (preview)
            {
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Width = ReadDimension(e.Args, "--width", 1040, 680);
                window.Height = ReadDimension(e.Args, "--height", 800, 540);
                // Exported frames have no physical LCD subpixels; set this before
                // capturing an animation snapshot as well as the final bitmap.
                TextOptions.SetTextRenderingMode(window.PreviewContent,TextRenderingMode.Grayscale);
            }
            if (!preview && startupRecoveryNotice is null && new AppPreferencesStore().Load().SilentStartup && !e.Args.Contains("--show"))
            {
                window.StartHidden(); _=LensGpuRenderer.WarmShaderAsync();
            }
            else window.Show();
            if (!preview) return;

            await firstFrame.Task;
            await viewModel.CurrentProbeTask;
            // A second local counter sample provides a rate without sending any traffic.
            await Task.Delay(250);
            viewModel.QueueLocalRefresh();
            await viewModel.CurrentLocalStatusTask;
            viewModel.Page = ReadArgument(e.Args, "--page") ?? "home";
            if (ReadArgument(e.Args,"--theme") == "dark") viewModel.DarkThemeCommand.Execute(null);
            else if (ReadArgument(e.Args,"--theme") == "light") viewModel.LightThemeCommand.Execute(null);
            else if (viewModel.ThemePresets.FirstOrDefault(preset => preset.Name == ReadArgument(e.Args,"--theme")) is { } preset)
                viewModel.SelectThemePresetCommand.Execute(preset);
            if (ReadArgument(e.Args,"--animation-color") is { } animationColor) viewModel.AnimationColor=animationColor;
            if (e.Args.Contains("--theme-details")) ((System.Windows.Controls.Expander)window.FindName("ThemeDetails")).IsExpanded=true;
            if (ReadArgument(e.Args,"--preview-color")?.Split('=',2) is [var key,var color] &&
                viewModel.ThemeColors.FirstOrDefault(option => option.Key == key) is { } previewColor)
                previewColor.Value=color;
            if (e.Args.Contains("--details") || e.Args.Contains("--software"))
                window.ShowPreviewDetails(e.Args.Contains("--software"));
            await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            if (viewModel.IsSettings && ReadArgument(e.Args,"--settings-section") == "startup")
            {
                viewModel.Page="settings-startup";
                await Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ContextIdle);
            }
            await window.WaitForLensPreviewAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            if (ReadArgument(e.Args,"--transition") is "favorites" or "settings")
            {
                ((System.Windows.Controls.Button)window.FindName(ReadArgument(e.Args,"--transition") == "settings" ? "SettingsButton" : "FavoritesButton")).RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                if (double.TryParse(ReadArgument(e.Args,"--transition-at"),out var transitionMilliseconds) &&
                    double.IsFinite(transitionMilliseconds) && transitionMilliseconds is >= 0 and <= 460)
                    window.SeekPageTransition(TimeSpan.FromMilliseconds(transitionMilliseconds));
                else await Task.Delay(180);
                window.UpdateLayout();
            }
            var content = window.PreviewContent;
            // An exported bitmap has no physical LCD subpixels; avoid colored fringes when it is scaled.
            TextOptions.SetTextRenderingMode(content, TextRenderingMode.Grayscale);
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var imagePath = Path.GetFullPath(e.Args[1]);
            using (var file = File.Create(imagePath)) encoder.Save(file);
            await File.WriteAllTextAsync(Path.ChangeExtension(imagePath, ".json"), viewModel.ExportJson(), new UTF8Encoding(false));
            window.Close();
        }
        catch (Exception exception) when (preview || e.Args is ["--diagnose",_])
        {
            // No dialogs in development modes: automation receives a nonzero exit code.
            if (e.Args.Length >= 2)
                await File.WriteAllTextAsync(Path.GetFullPath(e.Args[1]) + ".error.txt", exception.ToString());
            viewModel.Dispose();
            Shutdown(1);
        }
        finally
        {
            if (previewDirectory is not null && previewDirectory.Name.StartsWith("DailyToolkit-page-preview-",StringComparison.Ordinal) &&
                Path.GetFullPath(previewDirectory.FullName).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            {
                try { previewDirectory.Delete(recursive:true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Trace.WriteLine(exception); }
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose(); _instance=null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.RequestExit();
        base.OnSessionEnding(e);
    }

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double ReadDimension(string[] args, string name, double fallback, double minimum) =>
        double.TryParse(ReadArgument(args, name), out var value) && double.IsFinite(value)
            ? Math.Clamp(value, minimum, 2400) : fallback;

    private void ApplyAccessibilityColors()
    {
        if (!SystemParameters.HighContrast) return;
        Resources["WindowBrush"] = SystemColors.WindowBrush;
        Resources["SurfaceBrush"] = SystemColors.WindowBrush;
        Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["MutedBrush"] = SystemColors.WindowTextBrush;
        Resources["BorderBrush"] = SystemColors.WindowTextBrush;
        Resources["AccentBrush"] = SystemColors.HighlightBrush;
        Resources["AccentSoftBrush"] = SystemColors.WindowBrush;
        Resources["AccentForegroundBrush"] = SystemColors.HighlightTextBrush;
        Resources["WarningBrush"] = SystemColors.WindowBrush;
        Resources["WarningTextBrush"] = SystemColors.WindowTextBrush;
        Resources["HoverBrush"] = SystemColors.WindowBrush;
    }
}

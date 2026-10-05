using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyUSE.Desktop.Environment;
using DailyUSE.Desktop.Presentation;

namespace DailyUSE.Desktop;

public partial class App : Application
{
    private readonly Stopwatch _startup = Stopwatch.StartNew();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyAccessibilityColors();
        var viewModel = new MainViewModel(new WindowsEnvironmentProbe(), NativeWindowsInfo.ReadDisplay());
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

            var preview = e.Args.Length >= 2 && e.Args[0] == "--preview";
            var window = new MainWindow(viewModel);
            MainWindow = window;
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ContentRendered += (_, _) =>
            {
                if (firstFrame.TrySetResult())
                    viewModel.SetFirstFrameTime(_startup.Elapsed.TotalMilliseconds);
            };
            if (preview)
            {
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Width = ReadDimension(e.Args, "--width", 1040, 680);
                window.Height = ReadDimension(e.Args, "--height", 760, 540);
            }
            window.Show();
            if (!preview) return;

            await firstFrame.Task;
            await viewModel.CurrentProbeTask;
            viewModel.Page = ReadArgument(e.Args, "--page") ?? "home";
            await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            var content = window.PreviewContent;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var imagePath = Path.GetFullPath(e.Args[1]);
            using (var file = File.Create(imagePath)) encoder.Save(file);
            await File.WriteAllTextAsync(Path.ChangeExtension(imagePath, ".json"), viewModel.ExportJson(), new UTF8Encoding(false));
            window.Close();
        }
        catch (Exception exception) when (e.Args.Length > 0)
        {
            // No dialogs in development modes: automation receives a nonzero exit code.
            if (e.Args.Length >= 2)
                await File.WriteAllTextAsync(Path.GetFullPath(e.Args[1]) + ".error.txt", exception.ToString());
            viewModel.Dispose();
            Shutdown(1);
        }
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

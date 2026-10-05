using System.Windows;
using System.Windows.Threading;
using DailyUSE.Core.Environment;
using DailyUSE.Desktop;
using DailyUSE.Desktop.Presentation;

namespace DailyUSE.Desktop.Tests;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DailyUSE;component/Theme.xaml", UriKind.Relative)
        });
        var exitCode = 1;
        app.Startup += async (_, _) =>
        {
            try { await CheckAsync(); exitCode = 0; }
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
        var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
        var probe = new ControlledProbe(display);
        using var model = new MainViewModel(probe, display);
        var refresh = model.InitializeAsync();
        await probe.DetailsStarted.Task;
        Require(model.IsRefreshing && !model.RefreshCommand.CanExecute(null), "Refresh was not disabled during detection");
        var dispatched = false;
        await Application.Current.Dispatcher.InvokeAsync(() => dispatched = true, DispatcherPriority.Background);
        Require(dispatched && !refresh.IsCompleted && model.CpuName == "Test CPU", "Detection blocked the UI or basic results were delayed");
        probe.Finish.TrySetResult();
        await refresh;
        Require(model.Report.Stage == ProbeStage.Complete && model.RefreshCommand.CanExecute(null), "Detection did not complete");
        Console.WriteLine("PASS UI stays responsive while detailed detection is pending");

        model.NavigateCommand.Execute("tools");
        Require(model.IsTools, "Tools navigation failed");
        model.OpenComputerCommand.Execute(null);
        Require(model.IsComputer, "Built-in tool did not open");
        model.SoftwareSearch = "beta";
        Require(model.SoftwareView.Cast<InstalledSoftwareInfo>().Single().Name == "Beta Editor", "Software filtering failed");
        model.SoftwareSearch = "";
        Require(model.SoftwareView.Cast<object>().Count() == 2, "Software filtering did not reset");
        model.SetViewportWidth(680);
        Require(model.Profile.CompactLayout, "Compact layout not applied");
        Console.WriteLine("PASS Tool navigation, software search, and compact adaptation");

        using var failedModel = new MainViewModel(new FailingProbe(probe.Basic), display);
        await failedModel.InitializeAsync();
        Require(failedModel.Report.Stage == ProbeStage.Partial && failedModel.HasIssues && !failedModel.IsRefreshing,
            "A failed detector did not leave a usable partial report");
        Console.WriteLine("PASS Detector failure preserves the basic report");

        var closingProbe = new ControlledProbe(display);
        var closingModel = new MainViewModel(closingProbe, display);
        var window = new MainWindow(closingModel)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closingProbe.DetailsStarted.Task;
        window.Close();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(closingProbe.WasCanceled && closingModel.CurrentProbeTask.IsCompleted, "Closing did not cancel pending detection");
        Console.WriteLine("PASS Closing cancels detection before releasing the window");
        Console.WriteLine("4/4 desktop checks passed");
    }

    private sealed class ControlledProbe(DisplayInfo display) : IEnvironmentProbe
    {
        public MachineReport Basic { get; } = new()
        {
            System = new("Windows 11", "Test", 26100, "x64", "x64", "10.0", "zh-CN"),
            Cpu = new("Test CPU", 8, null, ["SSE2"]), Memory = new(16L << 30, 8L << 30),
            Display = display, Power = new(false, 80)
        };
        public TaskCompletionSource DetailsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCanceled { get; private set; }
        public MachineReport ReadBasic(DisplayInfo value) => Basic;
        public async Task<MachineReport> ReadDetailsAsync(MachineReport basic, CancellationToken token)
        {
            DetailsStarted.TrySetResult();
            try { await Finish.Task.WaitAsync(token); }
            catch (OperationCanceledException) { WasCanceled = true; throw; }
            return basic with
            {
                Stage = ProbeStage.Complete, SoftwareInventoryCompleted = true,
                Software = [new("Alpha Tool", "1.0", "Test"), new("Beta Editor", "2.0", "Test")]
            };
        }
    }

    private sealed class FailingProbe(MachineReport basic) : IEnvironmentProbe
    {
        public MachineReport ReadBasic(DisplayInfo display) => basic;
        public Task<MachineReport> ReadDetailsAsync(MachineReport report, CancellationToken token) =>
            throw new InvalidOperationException("Simulated detector failure");
    }
}

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
    private static async Task CheckDetectorDiagnosticsAsync()
    {
        var display=new DisplayInfo(1920,1080,1,1920,1040,false);
        var probe=new ControlledProbe(display);
        using var output=new StringWriter();
        using var listener=new System.Diagnostics.TextWriterTraceListener(output);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            using var model=new MainViewModel(new FailingProbe(probe.Basic),display,new LocalProbe(),notesStore:IsolatedNotes());
            await model.InitializeAsync();
            listener.Flush();
            Require(model.Report.Stage == ProbeStage.Partial && model.HasIssues && !model.IsRefreshing,
                "Logging a detector failure changed its usable partial report");
            Require(output.ToString().Contains("InvalidOperationException") &&
                output.ToString().Contains("Simulated detector failure"),
                "The original detector exception was not retained for diagnostics");
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
        Console.WriteLine("PASS Detector failures retain original exceptions without changing the friendly partial report");
    }

    private static async Task CheckLifecycleAsync()
    {
        var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
        var probe = new ControlledProbe(display);
        var clock = new ManualClock();
        var status = new LocalProbe();
        using var model = new MainViewModel(probe, display, status, clock, notesStore:IsolatedNotes());
        model.Page="settings-computer";
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
        Require(model.IsHome && model.IsSettings && model.PageTitle == "电脑详情", "Legacy category navigation did not resolve to computer details");
        model.NavigateCommand.Execute("gaming");
        Require(model.ShowScreenLens && model.PageTitle == "屏幕局部放大" && !model.Gaming.IsActive, "Direct tool navigation was not ready");
        Console.WriteLine("PASS Game navigation keeps screen capture off until explicitly enabled");
        model.NavigateCommand.Execute("computer");
        Require(model.IsHome, "The former computer page did not resolve to the merged home");
        model.SoftwareSearch = "beta";
        Require(model.SoftwareView.Cast<InstalledSoftwareInfo>().Single().Name == "Beta Editor", "Software filtering failed");
        model.SoftwareSearch = "";
        Require(model.SoftwareView.Cast<object>().Count() == 2, "Software filtering did not reset");
        model.SetViewportWidth(680);
        Require(model.Profile.CompactLayout, "Compact layout not applied");
        Console.WriteLine("PASS Tool navigation, software search, and compact adaptation");

        Require(model.NetworkText == "已联网" && model.Weather.Available, "Windows status was not applied");
        clock.UtcNow += TimeSpan.FromMinutes(2);
        model.UpdateClock();
        Require(model.TimeText == "00:01" && model.DateText.StartsWith("2026年10月6日"), "Clock/date did not cross midnight");
        model.QueueLocalRefresh();
        await model.CurrentLocalStatusTask;
        Require(status.WeatherReads == 2, "Weather did not refresh after the interval");
        Console.WriteLine("PASS Local clock rollover and periodic status refresh");

        var cachedClock = new ManualClock();
        var cachedWeather = new WeatherInfo(true, "23°C · 晴朗", WindowsWeatherCache.Source, null)
        {
            Location = "测试城", UpdatedAt = cachedClock.UtcNow.AddHours(-1), FromCache = true
        };
        using var cachedModel = new MainViewModel(probe, display, new LocalProbe { WeatherResult = cachedWeather }, cachedClock, notesStore:IsolatedNotes());
        await cachedModel.InitializeAsync();
        Require(cachedModel.WeatherNote == "测试城 · Windows 小组件缓存" &&
            cachedModel.WeatherUpdatedText == "10月5日 22:59 更新", "Cached weather source or local timestamp is incorrect");
        cachedClock.UtcNow += TimeSpan.FromHours(3);
        cachedModel.UpdateClock();
        Require(cachedModel.WeatherUpdatedText.StartsWith("缓存较旧"), "Older cached weather was presented as fresh");
        Console.WriteLine("PASS Cached weather displays its source, city, observation time, and age");

        using var failedModel = new MainViewModel(new FailingProbe(probe.Basic), display, new LocalProbe(), notesStore:IsolatedNotes());
        await failedModel.InitializeAsync();
        Require(failedModel.Report.Stage == ProbeStage.Partial && failedModel.HasIssues && !failedModel.IsRefreshing,
            "A failed detector did not leave a usable partial report");
        Console.WriteLine("PASS Detector failure preserves the basic report");

        using var partialStatus = new MainViewModel(probe, display, new LocalProbe { FailWeather = true }, notesStore:IsolatedNotes());
        await partialStatus.InitializeAsync();
        Require(partialStatus.Network.State == NetworkState.Internet && !partialStatus.Weather.Available,
            "Weather failure discarded valid network data or showed invented weather");
        Console.WriteLine("PASS Missing Windows weather leaves network and computer data usable");

        var closingProbe = new ControlledProbe(display);
        var closingStatus = new LocalProbe { WaitForCancel = true };
        var closingModel = new MainViewModel(closingProbe, display, closingStatus, notesStore:IsolatedNotes());
        closingModel.Page="settings-computer";
        var window = new MainWindow(closingModel, enableShortcuts: false)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closingProbe.DetailsStarted.Task;
        window.Close();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(closingProbe.WasCanceled && closingStatus.WasCanceled && closingModel.PendingWork.IsCompleted,
            "Closing did not cancel pending computer and weather detection");
        Console.WriteLine("PASS Closing cancels detection before releasing the window");
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

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-05T15:59:00Z");
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test China", TimeSpan.FromHours(8), "Test China", "Test China");
    }

    private sealed class LocalProbe : ILocalStatusProbe
    {
        public WeatherInfo? WeatherResult { get; init; }
        public bool FailWeather { get; init; }
        public bool WaitForCancel { get; init; }
        public bool WasCanceled { get; private set; }
        public int WeatherReads { get; private set; }
        public Task<NetworkInfo> ReadNetworkAsync(CancellationToken token) =>
            Task.FromResult(new NetworkInfo(NetworkState.Internet, "Wi-Fi", 1024, 512));
        public async Task<WeatherInfo> ReadWeatherAsync(CancellationToken token)
        {
            WeatherReads++;
            if (FailWeather) throw new InvalidOperationException("Simulated unavailable weather");
            if (WaitForCancel)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { WasCanceled = true; throw; }
            }
            return WeatherResult ?? WindowsWeatherText.Parse("天气，18°C，多云");
        }
    }
}

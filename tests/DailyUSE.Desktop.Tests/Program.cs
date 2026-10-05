using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Text;
using System.Text.Json;
using DailyUSE.Core.Environment;
using DailyUSE.Desktop;
using DailyUSE.Desktop.Environment;
using DailyUSE.Desktop.Gaming;
using DailyUSE.Core.Gaming;
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
        await CheckWeatherCacheAsync();
        CheckShortcutPersistence();
        LensDiagnostics.CheckShader();
        Console.WriteLine("PASS Actual Direct3D shader preserves colors and reconstructs subpixel edges");
        var display = new DisplayInfo(1920, 1080, 1, 1920, 1040, false);
        var probe = new ControlledProbe(display);
        var clock = new ManualClock();
        var status = new LocalProbe();
        using var model = new MainViewModel(probe, display, status, clock);
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
        model.NavigateCommand.Execute("gaming");
        Require(model.IsGaming && model.PageTitle == "游戏" && !model.Gaming.IsActive, "The separate DailyUSE game page was not ready");
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
        using var cachedModel = new MainViewModel(probe, display, new LocalProbe { WeatherResult = cachedWeather }, cachedClock);
        await cachedModel.InitializeAsync();
        Require(cachedModel.WeatherNote == "测试城 · Windows 小组件缓存" &&
            cachedModel.WeatherUpdatedText == "10月5日 22:59 更新", "Cached weather source or local timestamp is incorrect");
        cachedClock.UtcNow += TimeSpan.FromHours(3);
        cachedModel.UpdateClock();
        Require(cachedModel.WeatherUpdatedText.StartsWith("缓存较旧"), "Older cached weather was presented as fresh");
        Console.WriteLine("PASS Cached weather displays its source, city, observation time, and age");

        using var failedModel = new MainViewModel(new FailingProbe(probe.Basic), display, new LocalProbe());
        await failedModel.InitializeAsync();
        Require(failedModel.Report.Stage == ProbeStage.Partial && failedModel.HasIssues && !failedModel.IsRefreshing,
            "A failed detector did not leave a usable partial report");
        Console.WriteLine("PASS Detector failure preserves the basic report");

        using var partialStatus = new MainViewModel(probe, display, new LocalProbe { FailWeather = true });
        await partialStatus.InitializeAsync();
        Require(partialStatus.Network.State == NetworkState.Internet && !partialStatus.Weather.Available,
            "Weather failure discarded valid network data or showed invented weather");
        Console.WriteLine("PASS Missing Windows weather leaves network and computer data usable");

        var closingProbe = new ControlledProbe(display);
        var closingStatus = new LocalProbe { WaitForCancel = true };
        var closingModel = new MainViewModel(closingProbe, display, closingStatus);
        var window = new MainWindow(closingModel)
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
        Console.WriteLine("11/11 desktop checks passed");
    }

    private static void CheckShortcutPersistence()
    {
        var directory=Directory.CreateTempSubdirectory("DailyUSE-shortcut-tests-");
        var path=Path.Combine(directory.FullName,"gaming.json");
        try
        {
            var store=new GamingPreferencesStore(path);
            using(var gaming=new GamingViewModel(store))
            {
                var custom=new KeyboardShortcut(3,0x5A,"Ctrl + Alt + Z");
                Require(gaming.SetShortcut(true,custom),"A custom shortcut was rejected");
                Require(!gaming.SetShortcut(false,custom),"Duplicate shortcuts were accepted");
                Require(gaming.SetShortcut(false,null),"Empty close shortcut was rejected");
            }
            using(var reopened=new GamingViewModel(store))
            {
                Require(reopened.ToggleShortcut?.Name == "Ctrl + Alt + Z" && reopened.CloseShortcut is null,"Shortcut choices were not restored");
                Require(reopened.SetShortcut(true,null),"Empty toggle shortcut was rejected");
            }
            using(var empty=new GamingViewModel(store)) Require(empty.ToggleShortcut is null && empty.CloseShortcut is null,
                "Cleared shortcuts reverted to defaults after restart");
            File.WriteAllText(path,"{");
            Require(store.Load().IsValid,"Corrupt preferences prevented a usable default");
            Console.WriteLine("PASS Custom and cleared shortcuts persist without registering capture or accepting duplicates");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyUSE-shortcut-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }

    private static async Task CheckWeatherCacheAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyUSE-weather-tests-");
        var path = Path.Combine(directory.FullName, "Packages", "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy",
            "LocalState", "DiagOutputDir", "IDX_CONTENT_TASKBARHEADLINES.json");
        try
        {
            var reader = new WindowsWeatherCacheReader(directory.FullName);
            var now = DateTimeOffset.Parse("2026-10-05T06:00:00Z");
            Require(!(await reader.ReadAsync(now)).Available, "Missing Windows cache did not degrade gracefully");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = JsonSerializer.Serialize(new
            {
                responses = new[] { new { weather = new[] { new { current = new { temp = 23, cap = "晴朗", created = "2026-10-05T05:40:00Z" } } } } },
                units = new { temperature = "°C" }
            });
            var json = JsonSerializer.Serialize(new
            {
                sections = new[] { new { cards = new[] { new { type = "WeatherSummary", dataType = "WeatherOverview", data } } } }
            });
            await File.WriteAllBytesAsync(path, Encoding.Unicode.GetBytes(json));
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Require((await reader.ReadAsync(now)).Summary == "23°C · 晴朗", "The Windows-owned shared UTF-16 cache was not read");
            await File.WriteAllTextAsync(path, "{");
            Require(!(await reader.ReadAsync(now)).Available, "Partially written cache was treated as weather");
            using (var oversized = new FileStream(path, FileMode.Open, FileAccess.Write))
                oversized.SetLength(WindowsWeatherCache.MaximumBytes + 1L);
            Require(!(await reader.ReadAsync(now)).Available, "Oversized cache was read");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await reader.ReadAsync(now, cancellation.Token); throw new InvalidOperationException("Canceled cache read continued"); }
            catch (OperationCanceledException) { }
            Console.WriteLine("PASS Local Windows weather cache tolerates sharing, missing/partial files, size limits, and cancellation");
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyUSE-weather-tests-", StringComparison.Ordinal)) directory.Delete(recursive: true);
        }
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

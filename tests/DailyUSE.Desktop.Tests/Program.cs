using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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
        CheckFavorites();
        await CheckBackgroundShortcutAsync();
        LensDiagnostics.CheckShader();
        Console.WriteLine("PASS Actual Direct3D shader preserves colors and reconstructs subpixel edges");
        await CheckPhotoPreviewAsync();
        CheckLensPointer();
        await CheckPreviewLayoutAsync();
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
        Require(model.IsDaily && model.PageTitle == "日常", "The daily section or legacy navigation failed");
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
        Console.WriteLine("16/16 desktop checks passed");
    }

    private static async Task CheckPhotoPreviewAsync()
    {
        await Task.Run(() =>
        {
            using var renderer=new LensPhotoPreviewRenderer(software:true);
            var settings=new LensPreviewSettings(2560,1440,320,192,2,0,0);
            var first=renderer.Render(settings);
            Require(first.Photo.IsFrozen && first.Magnified.IsFrozen && first.Photo.PixelWidth == 1920,"Local photo was not loaded safely for background rendering");
            byte[] Pixels(System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];
                bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
                return pixels;
            }
            var original=Pixels(first.Magnified);
            var guided=Pixels(renderer.Render(settings with { VerticalGuide=true,HorizontalGuide=true }).Magnified);
            Require(guided.SequenceEqual(original),"Desktop guides were painted inside the magnified photo");
            Require(Pixels(renderer.Render(settings).Magnified).SequenceEqual(original),"Ending assistance left guide pixels in the output");
            var sharp=Pixels(renderer.Render(settings with { Sharpening=1 }).Magnified);
            Require(!original.SequenceEqual(sharp),"Sharpening did not change the actual photo shader output");
            var zoom=renderer.Render(settings with { Zoom=8 });
            Require(!original.SequenceEqual(Pixels(zoom.Magnified)),"Zoom did not change the crop");
            var resized=renderer.Render(settings with { Width=480,Height=640,PointerX=1,PointerY=1 });
            Require(resized.Magnified.PixelWidth == 480 && resized.Magnified.PixelHeight == 640 &&
                resized.Layout.Output == new PixelBounds(2080,800,480,640),"Independent size or edge-constrained dragging was incorrect");
            var clamped=renderer.Render(settings with { ScreenWidth=200,ScreenHeight=100,Width=480,Height=640,Zoom=1 });
            Require(clamped.Magnified.PixelWidth == 200 && clamped.Magnified.PixelHeight == 100,"Preview did not match monitor size limits");
        });
        Console.WriteLine("PASS Bundled photo preview runs the real shader, with zoom, sharpening, independent dimensions and monitor clipping");
    }

    private static void CheckLensPointer()
    {
        PixelBounds? bounds=new(10,20,320,192);
        var moved=(0,0); var wheel=0;
        var canMove=true; var ended=0;
        using var controller=new LensPointerController(() => bounds,(x,y) => moved=(x,y),delta => wheel+=delta,install:false,
            canMove:() => canMove,dragEnded:() => ended++);
        Require(!controller.Process(0x201,0,0),"Clicks outside the lens were intercepted");
        Require(!controller.Process(0x20A,0,0,120),"Wheel outside the lens was intercepted");
        Require(controller.Process(0x201,170,116) && controller.Process(0x200,210,176) && moved == (50,80),
            "Dragging from the magnified picture's center did not preserve its pointer offset");
        Require(controller.Process(0x202,210,176),"Picture drag did not end");
        Require(controller.Process(0x201,12,22) && controller.Process(0x200,52,82) && moved == (50,80),"Border dragging lost its pointer offset");
        Require(controller.Process(0x202,900,900),"Drag release outside the frame was not consumed");
        Require(controller.Process(0x20A,100,100,120) && wheel == 120,"Wheel inside the lens did not adjust zoom");
        Require(controller.Process(0x201,12,22),"Second drag did not begin");
        canMove=false;
        Require(!controller.Process(0x200,52,82) && controller.Process(0x202,52,82) && ended == 3,
            "Changing to fixed during a drag lost release routing or kept dragging");
        Require(!controller.Process(0x201,12,22) && !controller.Process(0x201,170,116) &&
            controller.Process(0x20A,100,100,120) && wheel == 240,
            "Fixed mode intercepted clicks or disabled wheel zoom");
        canMove=true;
        Require(controller.Process(0x201,12,22),"Returning to movable did not allow dragging");
        bounds=null;
        Require(!controller.Process(0x200,60,60) && controller.Process(0x202,60,60) &&
            !controller.Process(0x20A,100,100,120),"Hiding retained a drag or intercepted unrelated input");
        using var gaming=new GamingViewModel();
        gaming.FrameWidth=800; gaming.FrameHeight=240;
        Require(gaming.FrameSizeText == "800 × 240 像素","Changing width changed the height");
        for (var i=0;i<100;i++) gaming.AdjustZoom(120);
        Require(gaming.Zoom == 8 && !gaming.IsActive,"Wheel upper limit changed activation state");
        for (var i=0;i<100;i++) gaming.AdjustZoom(-120);
        Require(gaming.Zoom == 1 && !gaming.IsActive,"Wheel lower limit changed activation state");
        Console.WriteLine("PASS Movable lenses drag from the picture or border; fixed lenses pass clicks through; wheel routing and cleanup stay safe");
    }

    private static async Task CheckPreviewLayoutAsync()
    {
        var directory=Directory.CreateTempSubdirectory("DailyUSE-lens-layout-tests-");
        var display=new DisplayInfo(1920,1080,1,1920,1040,false);
        var probe=new ControlledProbe(display);
        probe.Finish.TrySetResult();
        var model=new MainViewModel(probe,display,new LocalProbe(),
            gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")));
        model.Page="gaming";
        var window=new MainWindow(model,enableShortcuts:false)
        {
            ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000
        };
        try
        {
            window.Show();
            var preview=(DailyUSE.Desktop.Controls.LensPreview)window.FindName("LensPhotoPreview");
            Require(preview.CurrentFrame is null && !model.Gaming.IsActive,"Collapsed tool decoded the photo or started capture");
            window.ShowPreviewDetails(false);
            await window.WaitForLensPreviewAsync().WaitAsync(TimeSpan.FromSeconds(15));
            window.UpdateLayout();
            var fixedRadio=(System.Windows.Controls.RadioButton)window.FindName("LensFixedMode");
            var movableRadio=(System.Windows.Controls.RadioButton)window.FindName("LensMovableMode");
            Require(fixedRadio.IsChecked == true && movableRadio.IsChecked == false,"Position radio buttons did not default to fixed");
            movableRadio.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Require(model.Gaming.IsMovableMode && fixedRadio.IsChecked == false,"Choosing movable in the actual settings did not update the lens mode");
            fixedRadio.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Require(model.Gaming.IsFixedMode && movableRadio.IsChecked == false,"Choosing fixed in the actual settings did not lock the lens mode");
            Require(preview.CurrentFrame is not null && !model.Gaming.IsActive && System.Windows.Controls.Grid.GetColumn(preview) == 1,
                "Expanded preview was missing or did not sit beside settings");
            window.Width=680;
            window.UpdateLayout();
            Require(System.Windows.Controls.Grid.GetRow(preview) == 1 && System.Windows.Controls.Grid.GetColumn(preview) == 0,
                "Narrow layout did not place the preview below the settings");
        }
        finally
        {
            window.Close(); await model.PendingWork;
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyUSE-lens-layout-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
        Console.WriteLine("PASS Preview loads only when expanded, stays offline, leaves capture off and adapts to narrow windows");
    }

    private static void CheckFavorites()
    {
        var directory=Directory.CreateTempSubdirectory("DailyUSE-favorites-tests-");
        var path=Path.Combine(directory.FullName,"favorites.json");
        try
        {
            var store=new FavoritesStore(path);
            Require(store.Load().Count==0 && store.Save(["future-tool"]),"Missing favorites did not start empty");
            var display=new DisplayInfo(1920,1080,1,1920,1040,false);
            using(var model=new MainViewModel(new ControlledProbe(display),display,favoritesStore:store))
            {
                model.NavigateCommand.Execute("favorites");
                Require(model.IsFavorites && model.ShowFavoritesEmpty && !model.ShowScreenLens,"Favorites empty state was missing");
                model.Gaming.ToggleFavoriteCommand.Execute(null);
                Require(model.HasFavorites && model.ShowScreenLens && !model.ShowFavoritesEmpty && !model.Gaming.IsActive,
                    "Starring a tool did not show it in favorites, or started capture");
            }
            using(var reopened=new MainViewModel(new ControlledProbe(display),display,favoritesStore:store))
            {
                reopened.NavigateCommand.Execute("favorites");
                Require(reopened.Gaming.IsFavorite && reopened.ShowScreenLens,"Favorites did not survive restart");
                reopened.Gaming.ToggleFavoriteCommand.Execute(null);
                Require(reopened.ShowFavoritesEmpty,"Removing the last visible favorite did not show the empty state");
            }
            Require(store.Load().SetEquals(["future-tool"]),"Removing this tool changed other saved favorites");
            File.WriteAllText(path,"{");
            Require(store.Load().Count==0,"Broken favorites prevented an empty usable view");
            Require(!store.Save(["../invalid"]),"Invalid tool identifiers were persisted");
            Console.WriteLine("PASS Daily/favorites navigation, star/unstar, saved favorites and independent tool state");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyUSE-favorites-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }

    private static void CheckShortcutPersistence()
    {
        var directory=Directory.CreateTempSubdirectory("DailyUSE-shortcut-tests-");
        var path=Path.Combine(directory.FullName,"gaming.json");
        try
        {
            var store=new GamingPreferencesStore(path);
            Require(store.Load().MovementMode == LensMovementMode.Fixed,"New settings did not default to fixed");
            using(var gaming=new GamingViewModel(store))
            {
                var custom=new KeyboardShortcut(3,0x5A,"Ctrl + Alt + Z");
                Require(gaming.SetShortcut(custom),"A custom shortcut was rejected");
                gaming.ActivationMode=LensActivationMode.Hold;
                gaming.IsFixedMode=true;
            }
            using(var reopened=new GamingViewModel(store))
            {
                Require(reopened.ToggleShortcut?.Name == "Ctrl + Alt + Z" && reopened.IsHoldMode && reopened.IsFixedMode,
                    "Shortcut, activation or movement mode was not restored");
                reopened.FrameWidth=800; reopened.FrameHeight=600; reopened.Zoom=7;
                reopened.Sharpening=1; reopened.FrameRate=144; reopened.IsFavorite=true;
                var revision=reopened.PositionResetVersion;
                reopened.ResetDefaultsCommand.Execute(null);
                Require(reopened.FrameWidth == 640 && reopened.FrameHeight == 384 && reopened.Zoom == 2 &&
                    reopened.Sharpening == 0.35 && reopened.FrameRate == 0 && reopened.IsFixedMode &&
                    reopened.PositionResetVersion > revision && !reopened.IsActive && !reopened.HasCaptureResources,
                    "Restoring defaults did not reset picture, placement and resource state");
                Require(reopened.ToggleShortcut?.Name == "Ctrl + Alt + Z" && reopened.IsHoldMode && reopened.IsFavorite,
                    "Restoring defaults overwrote the user's shortcut, activation mode or favorite");
                Require(reopened.SetShortcut(null),"Empty shortcut was rejected");
            }
            using(var empty=new GamingViewModel(store))
            {
                Require(empty.ToggleShortcut is null && empty.IsHoldMode,"Cleared shortcut or mode reverted after restart");
                empty.ResetDefaults();
                Require(empty.ToggleShortcut is null && empty.IsHoldMode,"Reset restored a key the user had cleared");
                empty.IsToggleMode=true;
            }
            Require(store.Load().ActivationMode == LensActivationMode.Toggle,"Toggle mode was not persisted");
            File.WriteAllText(path,"{\"ToggleShortcut\":null,\"CloseShortcut\":{\"Modifiers\":6,\"VirtualKey\":120,\"Name\":\"Ctrl + Shift + F9\"}}");
            Require(store.Load() is { ToggleShortcut:null,ActivationMode:LensActivationMode.Toggle,MovementMode:LensMovementMode.Fixed },
                "Legacy empty shortcut or the new fixed default was not preserved");
            File.WriteAllText(path,"{\"ToggleShortcut\":{\"Modifiers\":3,\"VirtualKey\":90,\"Name\":\"Ctrl + Alt + Z\"},\"CloseShortcut\":null}");
            Require(store.Load().ToggleShortcut?.Name == "Ctrl + Alt + Z","Legacy custom shortcut was not preserved");
            File.WriteAllText(path,"{\"ToggleShortcut\":null,\"MovementMode\":0}");
            Require(store.Load() is { ToggleShortcut:null,MovementMode:LensMovementMode.Movable },"An explicitly saved movable choice was overwritten");
            File.WriteAllText(path,"{");
            Require(store.Load().IsValid,"Corrupt preferences prevented a usable default");
            Console.WriteLine("PASS Shortcut and movement choices persist; restoring defaults preserves custom/empty shortcuts, activation mode and favorites");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyUSE-shortcut-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }

    private static async Task CheckBackgroundShortcutAsync()
    {
        using var source = new HwndSource(new HwndSourceParameters("DailyUSE shortcut check")
        {
            PositionX=-20000,PositionY=-20000,Width=1,Height=1,WindowStyle=unchecked((int)0x80000000)
        });
        var keys = new HashSet<uint>();
        using var controller = new LensShortcutController(source,keys.Contains);
        var toggle=0; var shown=0; var hidden=0;
        controller.ToggleRequested += () => toggle++;
        controller.StartRequested += () => shown++;
        controller.StopRequested += () => hidden++;
        var binding=new KeyboardShortcut(6,0x87,"Ctrl + Shift + F24");
        async Task PressAsync(KeyboardShortcut shortcut)
        {
            Require(PostMessage(source.Handle,0x0312,new IntPtr(LensShortcutController.HotkeyId),
                new IntPtr((long)shortcut.VirtualKey << 16 | shortcut.Modifiers)),"Could not route a native hotkey message");
            await Application.Current.Dispatcher.InvokeAsync(() => { },DispatcherPriority.ContextIdle);
        }
        Require(controller.Configure(binding,LensActivationMode.Toggle) && controller.IsRegistered,
            "The background shortcut was not registered before capture starts");
        await PressAsync(binding); await PressAsync(binding);
        Require(toggle==2 && shown==0,"The same background shortcut did not toggle twice");
        Require(controller.Configure(binding,LensActivationMode.Hold),"Hold mode could not register");
        keys.UnionWith([0x87u,0x11u,0x10u]);
        await PressAsync(binding); await PressAsync(binding);
        Require(shown==1 && hidden==0,"A held/repeated chord did not show exactly once");
        keys.Remove(0x10);
        await Task.Delay(50);
        Require(hidden==1,"Releasing a modifier did not hide the lens in the background");
        keys.Add(0x10);
        await PressAsync(binding);
        Require(shown==2,"Hold mode could not show again after release");
        Require(controller.Suspend(true) && !controller.IsRegistered && hidden==2,"Editing did not release the hold and shortcut");
        await PressAsync(binding);
        Require(shown==2,"Editing still activated the shortcut");
        Require(controller.Suspend(false) && controller.IsRegistered,"Leaving the editor did not restore the shortcut");
        Require(controller.Configure(null,LensActivationMode.Hold) && !controller.IsRegistered,"Empty shortcut still reserved a key");
        await PressAsync(binding);
        Require(shown==2,"A queued message for a cleared shortcut still activated the lens");
        var replacement=new KeyboardShortcut(6,0x86,"Ctrl + Shift + F23");
        Require(controller.Configure(replacement,LensActivationMode.Toggle),"Replacement shortcut was not registered");
        await PressAsync(binding);
        Require(toggle==2,"The previous shortcut still activated after replacement");
        await PressAsync(replacement);
        Require(toggle==3,"The replacement shortcut did not activate");
        controller.Dispose();
        Require(!controller.IsRegistered,"Exit retained the global shortcut");
        Console.WriteLine("PASS Native background shortcut registration, single-key toggle, held release, rebinding, empty choice and cleanup");
    }

    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);

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

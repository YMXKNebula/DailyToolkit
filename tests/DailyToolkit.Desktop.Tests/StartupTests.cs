using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Runtime;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;
namespace DailyToolkit.Desktop.Tests;
internal static partial class Program
{
    private sealed class StartupFake : IStartupRegistration
    {
        public StartupChoice Choice=new(false,false,false);
        public bool IsAdministrator => false;
        public int Authorizations,Launches;
        public bool Cancel;
        public StartupChoice Read() => Choice;
        public Task ApplyAsync(StartupChoice choice)
        {
            if (choice.Enabled && choice.Administrator && !Choice.Administrator)
            { Authorizations++; if (Cancel) throw new Win32Exception(1223); }
            Choice=choice; return Task.CompletedTask;
        }
        public void LaunchAdministrator(bool requireRegisteredTask=false) { Launches++; }
    }
    private static async Task CheckStartupSettingsAsync()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-startup-tests-");
        try
        {
            var path=Path.Combine(directory.FullName,"settings.json"); var store=new AppPreferencesStore(path);
            var display=new DisplayInfo(1920,1080,1,1920,1040,false); var startup=new StartupFake();
            using var model=new MainViewModel(new ControlledProbe(display),display,new LocalProbe(),
                favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
                gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),
                navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),appPreferencesStore:store,startupRegistration:startup);
            Require(!model.StartAtLogin && !model.AdminStartup && !model.SilentStartup && !model.CloseToTray && !model.MinimizeToTray,
                "New runtime options changed defaults or enabled autostart without user action");
            model.AdminStartup=true; Require(!model.AdminStartup,"Administrator startup was available without autostart");
            var restarts=0; model.AdministratorRestartRequested += () => restarts++;
            model.StartAtLogin=true; model.AdminStartup=true; model.SilentStartup=true;
            Require(startup.Authorizations == 0 && !store.Load().StartAtLogin,"Changing checkboxes applied system startup settings immediately");
            await model.ApplyStartupAsync();
            Require(startup.Authorizations == 1 && startup.Launches == 1 && restarts == 1 &&
                store.Load() is { StartAtLogin:true,AdminStartup:true,SilentStartup:true } && !model.StartupDirty,
                "Combined admin/silent startup did not save and restart the entire app after one authorization");
            model.SilentStartup=false; await model.ApplyStartupAsync();
            Require(startup.Authorizations == 1,"Changing silent startup prompted for authorization again");
            model.StartAtLogin=false; await model.ApplyStartupAsync();
            Require(!startup.Choice.Enabled && !startup.Choice.Administrator,"Disabling startup retained the administrator task");
            model.StartAtLogin=true; model.AdminStartup=true; await model.ApplyStartupAsync();
            Require(startup.Authorizations == 2,"Re-enabling administrator startup skipped the required new authorization");
            model.StartAtLogin=false; await model.ApplyStartupAsync();
            startup.Cancel=true; model.StartAtLogin=true; model.AdminStartup=true; model.SilentStartup=true; await model.ApplyStartupAsync();
            Require(!startup.Choice.Enabled && !store.Load().StartAtLogin && model.StartupStatus.Contains("取消") && model.CanEditStartup,
                "Canceled UAC enabled startup, saved unapproved choices or left controls disabled");
            model.CloseToTray=true; model.MinimizeToTray=true; model.AnimationColor="#123456";
            model.ThemeColors.Single(option => option.Key == "Text").Value="#654321";
            Require(store.Load() is { CloseToTray:true,MinimizeToTray:true,AnimationColor:"#123456",Theme.Text:"#654321" },
                "Background and independent appearance preferences did not persist");
            File.WriteAllText(path,"{\"Theme\":{\"Accent\":\"oops\",\"Text\":\"#abcdef\"},\"AnimationColor\":\"#GGGGGG\"}");
            Require(store.Load() is { AnimationColor:"#267A5D",Theme.Accent:"#267A5D",Theme.Text:"#ABCDEF" },"Invalid colors did not recover independently");
            var xml=XDocument.Parse(StartupRegistration.CreateTaskXml(@"C:\工具 & files\DailyToolkit.exe","S-1-5-21-123"));
            XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
            string Value(string name) => xml.Descendants(ns+name).Single().Value;
            Require(Value("RunLevel") == "HighestAvailable" && Value("LogonType") == "InteractiveToken" &&
                Value("Arguments") == "--startup --admin-task" && Value("Command") == @"C:\工具 & files\DailyToolkit.exe" &&
                Value("ExecutionTimeLimit") == "PT0S" && Value("DisallowStartIfOnBatteries") == "false" &&
                xml.Descendants(ns+"UserId").All(id => id.Value == "S-1-5-21-123"),
                "Administrator startup did not use the authorized user's interactive task or safely escape paths");
            object? scheduler=null;
            try
            {
                scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                ((dynamic)scheduler).Connect(); dynamic definition=((dynamic)scheduler).NewTask(0);
                Require(StartupRegistration.FindTask(((dynamic)scheduler).GetFolder("\\"),"DailyToolkit.Absent."+Guid.NewGuid().ToString("N")) is null,
                    "A missing Windows startup task crashed the startup registration reader");
                _=new StartupRegistration().Read(); // Read-only; no task or registry writes.
                definition.XmlText=xml.ToString();
                Require(definition.Principal.RunLevel == 1 && definition.Principal.LogonType == 3,
                    "Windows rejected the administrator startup task definition");
            }
            finally { if (scheduler is not null) Marshal.FinalReleaseComObject(scheduler); }
            Console.WriteLine("PASS Admin and silent startup coexist; explicit apply, re-enable authorization, canceled UAC and task policy verified without changing Windows startup");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-startup-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }
    private static async Task CheckTrayLifecycleAsync()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-tray-tests-");
        MainWindow? window=null;
        try
        {
            var display=new DisplayInfo(1920,1080,1,1920,1040,false); var probe=new ControlledProbe(display);
            var model=new MainViewModel(probe,display,new LocalProbe(),
                favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
                gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),
                navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),
                appPreferencesStore:new AppPreferencesStore(Path.Combine(directory.FullName,"settings.json")),startupRegistration:new StartupFake());
            window=new(model,enableShortcuts:false) { ShowActivated=false,ShowInTaskbar=false,
                WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
            var handle=new WindowInteropHelper(window).EnsureHandle();
            window.InitializeRuntime(HwndSource.FromHwnd(handle)!);
            window.StartHidden(); await probe.DetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!window.IsVisible && !probe.WasCanceled,"Silent startup showed the window or skipped local services");
            Require(PostTrayTestMessage(handle,SingleInstance.ShowMessage,IntPtr.Zero,IntPtr.Zero),"Could not post the restore message");
            await Application.Current.Dispatcher.InvokeAsync(() => { },DispatcherPriority.ApplicationIdle);
            Require(window.IsVisible,"Reopening could not restore a hidden window");
            if (window.TrayAvailable)
            {
                model.CloseToTray=true; window.Close();
                Require(!window.IsVisible && !probe.WasCanceled && !model.PendingWork.IsCompleted,"Closing to tray canceled the app instead of retaining it");
                window.ShowFromTray(); Require(window.IsVisible,"The tray did not restore the hidden window");
                model.MinimizeToTray=true; window.WindowState=WindowState.Minimized;
                Require(!window.IsVisible,"Minimize-to-tray left a visible taskbar window");
                window.ShowFromTray(); Require(window.IsVisible && window.WindowState != WindowState.Minimized,"Tray restore kept the window minimized");
            }
            else Console.WriteLine("INFO Explorer tray unavailable; native hide actions omitted, hidden startup/restore/exit still checked");
            var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_,_) => closed.TrySetResult();
            window.RequestExit(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(probe.WasCanceled && model.PendingWork.IsCompleted,"Real exit did not bypass tray mode and release local work");
            Console.WriteLine("PASS Silent startup, native tray, close/minimize hiding, hidden-window restore and true exit preserve lifecycle correctly");
        }
        finally
        {
            if (window is not null) window.RequestExit();
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-tray-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }
    [DllImport("user32.dll",EntryPoint="PostMessageW")]
    private static extern bool PostTrayTestMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}

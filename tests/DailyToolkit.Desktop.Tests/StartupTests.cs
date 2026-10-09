using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
        public bool IsAdministrator { get; set; }
        public int Authorizations,Launches,ApplyCalls,ActiveCalls,MaximumActiveCalls;
        public bool Cancel, CancelLaunch, LastRequiredTask;
        public TaskCompletionSource? Pending,Started;
        public StartupChoice Read() => Choice;
        public async Task ApplyAsync(StartupChoice choice)
        {
            ApplyCalls++; MaximumActiveCalls=Math.Max(MaximumActiveCalls,++ActiveCalls);
            try
            {
                Started?.TrySetResult();
                if (Pending is not null) await Pending.Task;
                if (choice.Enabled && choice.Administrator && !Choice.Administrator && !IsAdministrator)
                { Authorizations++; if (Cancel) throw new Win32Exception(1223); }
                Choice=choice;
            }
            finally { ActiveCalls--; }
        }
        public void LaunchAdministrator(bool requireRegisteredTask=false)
        { LastRequiredTask = requireRegisteredTask; Launches++; if (CancelLaunch) throw new Win32Exception(1223); }
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
            Require(startup.ApplyCalls == 0,"Loading startup settings changed Windows registration");
            model.AdminStartup=true; Require(!model.AdminStartup,"Administrator startup was available without autostart");
            var restarts=0; model.AdministratorRestartRequested += () => restarts++;
            model.StartAtLogin=true; model.AdminStartup=true; model.SilentStartup=true;
            await model.PendingStartupChange;
            Require(startup.Authorizations == 1 && startup.Launches == 1 && restarts == 1 &&
                store.Load() is { StartAtLogin:true,AdminStartup:true,SilentStartup:true } && model.CanEditStartup,
                "Combined admin/silent startup did not save and restart the entire app after one authorization");
            model.SilentStartup=false; await model.PendingStartupChange;
            Require(startup.Authorizations == 1 && startup.ApplyCalls == 1 && startup.Launches == 1,
                "Changing only silent startup rewrote Windows registration, requested authorization or restarted the app");
            model.StartAtLogin=false; await model.PendingStartupChange;
            Require(!startup.Choice.Enabled && !startup.Choice.Administrator,"Disabling startup retained the administrator task");
            model.StartAtLogin=true; model.AdminStartup=true; await model.PendingStartupChange;
            Require(startup.Authorizations == 2,"Re-enabling administrator startup skipped the required new authorization");
            model.StartAtLogin=false; await model.PendingStartupChange;
            startup.Cancel=true; model.StartAtLogin=true; model.AdminStartup=true; model.SilentStartup=true; await model.PendingStartupChange;
            Require(!startup.Choice.Enabled && !store.Load().StartAtLogin && !model.StartAtLogin && !model.AdminStartup && !model.SilentStartup &&
                model.StartupStatus.Contains("取消") && model.CanEditStartup,
                "Canceled UAC enabled startup, saved unapproved choices or left controls disabled");
            startup.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
            startup.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
            model.StartAtLogin=true; model.AdminStartup=true;
            await startup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!model.CanEditStartup,"Startup controls stayed editable during an authorization request");
            model.AnimationColor="#123456";
            startup.Pending.SetResult(); await model.PendingStartupChange;
            Require(!model.StartAtLogin && store.Load().AnimationColor == "#123456",
                "Canceled authorization reverted unrelated appearance changes");
            startup.Cancel=false;
            startup.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
            startup.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
            model.StartAtLogin=true;
            await startup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SilentStartup=true; model.StartAtLogin=false;
            startup.Pending.SetResult(); await model.PendingStartupChange;
            Require(startup.MaximumActiveCalls == 1 && !startup.Choice.Enabled &&
                store.Load() is { StartAtLogin:false,AdminStartup:false,SilentStartup:true } && model.CanEditStartup,
                "Queued checkbox changes overlapped registration or lost the latest selection");
            startup.Pending=null; startup.Started=null;
            var authorizations=startup.Authorizations; var launches=startup.Launches; var previousRestarts=restarts;
            startup.IsAdministrator=true;
            model.StartAtLogin=true; model.AdminStartup=true; await model.PendingStartupChange;
            Require(store.Load() is { StartAtLogin:true,AdminStartup:true } && startup.Choice.Administrator &&
                startup.Authorizations == authorizations && startup.Launches == launches && restarts == previousRestarts &&
                model.CanEditStartup && !model.HasStartupStatus && !model.RunAdministratorCommand.CanExecute(null),
                "An already elevated app could not enable administrator startup without another authorization or restart");
            startup.IsAdministrator=false; startup.CancelLaunch=true;
            var beforeCanceledRestart=restarts;
            model.RunAdministratorCommand.Execute(null);
            Require(!startup.LastRequiredTask && restarts == beforeCanceledRestart && model.StartupStatus.Contains("无法切换"),
                "A manual administrator restart required a possibly stale task or exited the original app after canceled UAC");
            startup.CancelLaunch=false; model.RunAdministratorCommand.Execute(null);
            Require(!startup.LastRequiredTask && restarts == beforeCanceledRestart + 1,
                "A manual administrator restart could not request the current executable with existing administrator preferences");
            startup.IsAdministrator=true;
            model.AdminStartup=false; await model.PendingStartupChange;
            Require(startup.Choice is { Enabled:true,Administrator:false } && store.Load().StartAtLogin,
                "An elevated app could not switch back to normal startup");
            model.StartAtLogin=false; await model.PendingStartupChange;
            Require(!startup.Choice.Enabled && !model.AdminStartup,"An elevated app could not disable startup");
            var blocked=Path.Combine(directory.FullName,"blocked"); File.WriteAllText(blocked,"file");
            var blockedStartup=new StartupFake();
            using (var blockedModel=new MainViewModel(new ControlledProbe(display),display,new LocalProbe(),
                favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites-blocked.json")),
                gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming-blocked.json")),
                navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation-blocked.json")),
                appPreferencesStore:new AppPreferencesStore(Path.Combine(blocked,"settings.json")),startupRegistration:blockedStartup))
            {
                blockedModel.StartAtLogin=true; await blockedModel.PendingStartupChange;
                Require(!blockedModel.StartAtLogin && blockedStartup.ApplyCalls == 0 && blockedModel.HasStartupStatus && blockedModel.CanEditStartup,
                    "A failed local save changed Windows registration or retained an unsaved checkbox");
            }
            model.CloseToTray=true; model.MinimizeToTray=true; model.AnimationColor="#123456";
            model.ThemeColors.Single(option => option.Key == "Text").Value="#654321";
            model.SaveThemeCommand.Execute(null);
            Require(store.Load() is { CloseToTray:true,MinimizeToTray:true,AnimationColor:"#123456",Theme.Text:"#654321" },
                "Background and independent appearance preferences did not persist");
            File.WriteAllText(path,"{\"Theme\":{\"Accent\":\"oops\",\"Text\":\"#abcdef\"},\"AnimationColor\":\"#GGGGGG\"}");
            Require(store.Load() is { AnimationColor:"#267A5D",Theme.Accent:"#267A5D",Theme.Text:"#ABCDEF" },"Invalid colors did not recover independently");
            Require(StartupRegistration.SameExecutable(@"C:\工具\DailyToolkit.exe", @"c:\工具\DailyToolkit.exe") &&
                !StartupRegistration.SameExecutable(@"C:\工具\0.7.0\DailyToolkit.exe", @"C:\工具\0.7.1\DailyToolkit.exe"),
                "Administrator handoff matched a different version directory or rejected case-insensitive current path");
            var administratorStart=StartupRegistration.CreateAdministratorStartInfo(@"C:\工具\0.7.2\DailyToolkit.exe");
            Require(administratorStart.FileName == @"C:\工具\0.7.2\DailyToolkit.exe" && administratorStart.UseShellExecute &&
                administratorStart.Verb == "runas" && administratorStart.WorkingDirectory == @"C:\工具\0.7.2" &&
                administratorStart.ArgumentList.SequenceEqual(["--admin-task", "--show"]),
                "UAC fallback did not launch and show this version with the matching working directory");
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
                using var identity=WindowsIdentity.GetCurrent(); var sid=identity.User!.Value;
                Require(StartupRegistration.IsCurrentUser(sid) && StartupRegistration.IsCurrentUser(identity.Name) &&
                    !StartupRegistration.IsCurrentUser("S-1-5-18") &&
                    !StartupRegistration.IsCurrentUser("DailyToolkit.Missing."+Guid.NewGuid().ToString("N")) &&
                    !StartupRegistration.IsCurrentUser(""),"Task accounts were not compared by resolved SID");
                definition.XmlText=StartupRegistration.CreateTaskXml(@"C:\工具\DailyToolkit.exe",sid);
                definition.Principal.UserId=identity.Name;
                dynamic task=new System.Dynamic.ExpandoObject(); task.Definition=definition; task.Enabled=true;
                Require(StartupRegistration.OwnedTask(task),"An owned task returned with an account name was rejected");
                Require(StartupRegistration.CanRunAdministratorTask(task, @"C:\工具\DailyToolkit.exe"),
                    "A matching enabled administrator task was rejected");
                Require(!StartupRegistration.CanRunAdministratorTask(task, @"C:\工具\0.7.2\DailyToolkit.exe") &&
                    !StartupRegistration.CanRunAdministratorTask(null, @"C:\工具\DailyToolkit.exe"),
                    "A missing task or a task pointing at an older directory could restart the app");
                task.Enabled=false;
                Require(!StartupRegistration.CanRunAdministratorTask(task, @"C:\工具\DailyToolkit.exe"), "Disabled administrator task was runnable");
                task.Enabled=true; definition.Principal.RunLevel=0;
                Require(!StartupRegistration.CanRunAdministratorTask(task, @"C:\工具\DailyToolkit.exe"), "Non-elevated task was runnable as administrator");
                definition.Principal.RunLevel=1;
                definition.Principal.UserId="S-1-5-18";
                Require(!StartupRegistration.OwnedTask(task) && !StartupRegistration.CanRunAdministratorTask(task, @"C:\工具\DailyToolkit.exe"),
                    "A task for a different identity passed the ownership or launch check");
                definition.Principal.UserId=sid; definition.RegistrationInfo.Description="Other application";
                Require(!StartupRegistration.OwnedTask(task),"A foreign task passed the ownership check");
                var registered=StartupRegistration.FindTask(((dynamic)scheduler).GetFolder("\\"),"DailyToolkit-"+sid);
                if (registered is not null && registered.Definition.RegistrationInfo.Description == "DailyToolkit automatic startup. Owner: "+sid)
                    Require(StartupRegistration.OwnedTask(registered),"Windows' existing DailyToolkit task was not recognized");
            }
            finally { if (scheduler is not null) Marshal.FinalReleaseComObject(scheduler); }
            Console.WriteLine("PASS Startup checkbox changes save automatically; authorization, rollback, serialization and silent changes verified without changing Windows startup");
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
            window.StartHidden();
            Require(!window.IsVisible && !probe.WasCanceled && !probe.DetailsStarted.Task.IsCompleted && !window.IsLocalPolling,
                "Silent startup showed the window or eagerly started computer polling");
            Require(PostTrayTestMessage(handle,SingleInstance.ShowMessage,IntPtr.Zero,IntPtr.Zero),"Could not post the restore message");
            await Application.Current.Dispatcher.InvokeAsync(() => { },DispatcherPriority.ApplicationIdle);
            Require(window.IsVisible,"Reopening could not restore a hidden window");
            model.Page="settings-computer"; await probe.DetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

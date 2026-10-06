using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;
namespace DailyToolkit.Desktop.Runtime;

public sealed record StartupChoice(bool Enabled,bool Administrator,bool Silent);
public interface IStartupRegistration
{
    StartupChoice Read(); Task ApplyAsync(StartupChoice choice); bool IsAdministrator { get; } void LaunchAdministrator(bool requireRegisteredTask=false);
}
public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Description="DailyToolkit automatic startup. Owner: ";
    private readonly string _executable=System.Environment.ProcessPath ?? throw new InvalidOperationException("没有找到程序路径。");
    private static string UserSid { get { using var identity=WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
    private static string TaskName => "DailyToolkit-"+UserSid;
    public bool IsAdministrator { get { using var identity=WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
    private static dynamic Connect()
    {
        var service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        ((dynamic)service).Connect(); return service;
    }
    internal static bool OwnedTask(dynamic task) => task.Definition.RegistrationInfo.Description == Description+UserSid &&
        IsCurrentUser((string)task.Definition.Principal.UserId) && task.Definition.Actions.Count == 1 &&
        Path.GetFileName((string)task.Definition.Actions[1].Path).Equals("DailyToolkit.exe",StringComparison.OrdinalIgnoreCase) &&
        task.Definition.Actions[1].Arguments == "--startup --admin-task";
    internal static bool IsCurrentUser(string account)
    {
        // Task Scheduler can return the account name even when registration used a SID.
        // Resolve it to an identity; never accept a matching display name alone.
        if (account.Equals(UserSid,StringComparison.OrdinalIgnoreCase)) return true;
        try { return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value == UserSid; }
        catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or System.Security.SecurityException)
        { return false; }
    }
    private static bool OwnedRun(string? value) => value is not null && value.StartsWith('"') && value.EndsWith("\" --startup",StringComparison.Ordinal) &&
        Path.GetFileName(value.Substring(1,value.LastIndexOf('"')-1)).Equals("DailyToolkit.exe",StringComparison.OrdinalIgnoreCase);
    internal static dynamic? FindTask(dynamic folder,string? name=null)
    {
        try { return folder.GetTask(name ?? TaskName); }
        // The dynamic COM binder maps HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
        // to FileNotFoundException rather than COMException on this runtime.
        catch (Exception e) when (e is COMException or FileNotFoundException &&
            (uint)e.HResult is 0x80070002 or 0x8004130F) { return null; }
    }
    public StartupChoice Read()
    {
        object? service=null;
        try
        {
            service=Connect(); dynamic? task=FindTask(((dynamic)service).GetFolder("\\"));
            if (task is not null && OwnedTask(task!) && task!.Enabled && task!.Definition.Principal.RunLevel == 1) return new(true,true,false);
            using var key=Registry.CurrentUser.OpenSubKey(RunKey);
            return new(OwnedRun(key?.GetValue("DailyToolkit") as string),false,false);
        }
        finally { if (service is not null) Marshal.FinalReleaseComObject(service); }
    }
    public async Task ApplyAsync(StartupChoice choice)
    {
        if (choice.Enabled && choice.Administrator && !IsAdministrator && !Read().Administrator)
        {
            var start=new ProcessStartInfo(_executable) { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--configure-admin-startup"); start.ArgumentList.Add(UserSid);
            using var process=Process.Start(start) ?? throw new InvalidOperationException("没有启动授权程序。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !Read().Administrator) throw new InvalidOperationException("管理员自启未能启用，请确认授权和任务计划服务状态。");
            return;
        }
        Configure(choice);
    }
    internal void Configure(StartupChoice choice)
    {
        object? service=null;
        try
        {
            service=Connect(); dynamic scheduler=service; dynamic folder=scheduler.GetFolder("\\"); dynamic? existing=FindTask(folder);
            if (existing is not null && !OwnedTask(existing)) throw new InvalidOperationException("同名启动任务不属于 DailyToolkit，未作修改。");
            using var key=Registry.CurrentUser.CreateSubKey(RunKey);
            var run=key.GetValue("DailyToolkit") as string;
            if (run is not null && !OwnedRun(run)) throw new InvalidOperationException("同名启动项不属于 DailyToolkit，未作修改。");
            if (choice.Enabled && choice.Administrator)
            {
                if (existing is null || !existing!.Enabled || existing!.Definition.Actions[1].Path != _executable)
                {
                    if (!IsAdministrator) throw new InvalidOperationException("需要重新授权管理员自启。");
                    // Owner can read/run/delete, but cannot replace the elevated action.
                    folder.RegisterTask(TaskName,CreateTaskXml(_executable,UserSid),6,UserSid,null,3,$"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFXSD;;;{UserSid})");
                }
                if (run is not null) key.DeleteValue("DailyToolkit",false);
            }
            else
            {
                if (existing is not null) folder.DeleteTask(TaskName,0);
                if (choice.Enabled) key.SetValue("DailyToolkit",$"\"{_executable}\" --startup",RegistryValueKind.String);
                else if (run is not null) key.DeleteValue("DailyToolkit",false);
            }
        }
        finally { if (service is not null) Marshal.FinalReleaseComObject(service); }
    }
    internal static string CreateTaskXml(string executable,string sid)
    {
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name,object value) => new(ns+name,value);
        return new XDocument(new XElement(ns+"Task",new XAttribute("version","1.4"),
            E("RegistrationInfo",E("Description",Description+sid)),
            E("Triggers",E("LogonTrigger",new object[] { E("Enabled","true"),E("UserId",sid) })),
            E("Principals",new XElement(ns+"Principal",new XAttribute("id","Owner"),E("UserId",sid),E("LogonType","InteractiveToken"),E("RunLevel","HighestAvailable"))),
            E("Settings",new object[] { E("MultipleInstancesPolicy","IgnoreNew"),E("DisallowStartIfOnBatteries","false"),
                E("StopIfGoingOnBatteries","false"),E("AllowHardTerminate","false"),E("StartWhenAvailable","true"),
                E("RunOnlyIfNetworkAvailable","false"),E("Enabled","true"),E("Hidden","false"),E("ExecutionTimeLimit","PT0S") }),
            new XElement(ns+"Actions",new XAttribute("Context","Owner"),E("Exec",new object[] {
                E("Command",executable),E("Arguments","--startup --admin-task"),E("WorkingDirectory",Path.GetDirectoryName(executable)!) })))).ToString();
    }
    public void LaunchAdministrator(bool requireRegisteredTask=false)
    {
        object? service=null;
        try
        {
            service=Connect(); dynamic? task=FindTask(((dynamic)service).GetFolder("\\"));
            if (task is not null && OwnedTask(task!) && task!.Enabled && task!.Definition.Principal.RunLevel == 1) { task!.Run(null); return; }
            if (requireRegisteredTask) throw new InvalidOperationException("管理员启动任务不可用，请重新启用管理员自启。");
            var start=new ProcessStartInfo(_executable) { UseShellExecute=true,Verb="runas" }; start.ArgumentList.Add("--admin-task");
            Process.Start(start)?.Dispose();
        }
        finally { if (service is not null) Marshal.FinalReleaseComObject(service); }
    }
    internal static int ConfigureElevated(string sid)
    {
        try { var service=new StartupRegistration(); if (sid != UserSid || !service.IsAdministrator) return 2; service.Configure(new(true,true,false)); return 0; }
        catch (Exception e) { Trace.WriteLine(e); return 1; }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DailyToolkit.Desktop;

// The normal app owns the mutex; weather workers and developer checks never acquire it.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _ownsMutex;
    internal bool IsPrimary => _ownsMutex;

    public SingleInstance(string? name=null)
    {
        using var identity=WindowsIdentity.GetCurrent();
        try { _mutex=new(false,name ?? $"Local\\DailyToolkit.Desktop.{identity.User!.Value}"); _ownsMutex=_mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex=true; }
        catch (UnauthorizedAccessException) { _ownsMutex=false; }
    }

    internal static string WindowMarker
    { get { using var identity=WindowsIdentity.GetCurrent(); return "DailyToolkit.MainWindow."+identity.User!.Value; } }
    internal static readonly uint ShowMessage=RegisterWindowMessage("DailyToolkit.ShowMainWindow");
    internal static readonly uint ExitMessage=RegisterWindowMessage("DailyToolkit.ExitApplication");
    public bool ActivateExistingWindow(bool exit=false)
    {
        var activated=false;
        var ownProcess=System.Environment.ProcessId;
        var executable=System.Environment.ProcessPath;
        EnumWindows((window,_) =>
        {
            GetWindowThreadProcessId(window,out var processId);
            if (processId == ownProcess) return true;
            if (GetProp(window,WindowMarker) != IntPtr.Zero)
            {
                if (!exit) AllowSetForegroundWindow(processId);
                activated=PostMessage(window,exit ? ExitMessage : ShowMessage,IntPtr.Zero,IntPtr.Zero);
                if (!exit) SetForegroundWindow(window);
                return false;
            }
            var title=new StringBuilder(256);
            GetWindowText(window,title,title.Capacity);
            if (title.ToString() != "DailyToolkit") return true;
            try
            {
                using var process=Process.GetProcessById((int)processId);
                if (!string.Equals(process.MainModule?.FileName,executable,StringComparison.OrdinalIgnoreCase)) return true;
                if (exit) { activated=PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero); return false; }
                AllowSetForegroundWindow(processId);
                if (IsIconic(window)) ShowWindow(window,9);
                activated=SetForegroundWindow(window);
                return false;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            { Trace.WriteLine(exception); return true; }
        },IntPtr.Zero);
        return activated;
    }

    public void Dispose()
    {
        if (_ownsMutex) { _mutex!.ReleaseMutex(); _ownsMutex=false; }
        _mutex?.Dispose();
    }

    private delegate bool WindowCallback(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback,IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}

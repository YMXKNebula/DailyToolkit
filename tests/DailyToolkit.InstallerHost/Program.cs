using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DailyToolkit.InstallerHost;

// A temporary native window exercises setup's exit protocol without loading user data.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--admin-check", var setup, var directory, var portable, var report])
            return AdminInstallerChecks.Run(setup, directory, portable, report);
        if (args is not [var ready, var savedNote, var stop, var mode]) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        using var mutex = new Mutex(true, "Local\\DailyToolkit.Desktop." + sid, out var created);
        if (!created) return 3;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window { ShowInTaskbar = false, ShowActivated = false };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var marker = "DailyToolkit.MainWindow." + sid;
        if (!SetProp(handle, marker, new IntPtr(1))) return 4;
        var message = RegisterWindowMessage("DailyToolkit.ExitApplication");
        var source = HwndSource.FromHwnd(handle)!;
        source.AddHook((IntPtr _, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if ((uint)msg == message)
            {
                File.WriteAllText(ready + ".exit-request", "received");
                handled = true;
                if (mode == "save")
                {
                    File.WriteAllText(savedNote, "最后一次修改\nlast edit");
                    application.Shutdown();
                }
            }
            return IntPtr.Zero;
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (File.Exists(stop)) application.Shutdown(); };
        timer.Start();
        File.WriteAllText(ready, JsonSerializer.Serialize(new { Handle = handle.ToInt64(), ProcessId = System.Environment.ProcessId, Sid = sid }));
        try { application.Run(); }
        finally
        {
            timer.Stop(); RemoveProp(handle, marker); mutex.ReleaseMutex();
        }
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}

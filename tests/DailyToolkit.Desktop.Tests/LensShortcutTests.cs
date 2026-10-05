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
    private static async Task CheckBackgroundShortcutAsync()
    {
        using var source = new HwndSource(new HwndSourceParameters("DailyToolkit shortcut check")
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
}

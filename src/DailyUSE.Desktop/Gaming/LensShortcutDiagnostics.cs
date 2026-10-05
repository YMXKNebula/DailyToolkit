using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using DailyUSE.Core.Gaming;
using DailyUSE.Desktop.Presentation;

namespace DailyUSE.Desktop.Gaming;

internal static class LensShortcutDiagnostics
{
    public static async Task RunAsync(string outputPath)
    {
        var directory = Directory.CreateTempSubdirectory("DailyUSE-lens-key-check-");
        using var source = new HwndSource(new HwndSourceParameters("DailyUSE background shortcut check")
        {
            PositionX=-20000,PositionY=-20000,Width=1,Height=1,WindowStyle=unchecked((int)0x80000000)
        });
        using var gaming = new GamingViewModel(new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")))
        {
            FrameWidth=320
        };
        using var controller = new LensShortcutController(source);
        controller.ToggleRequested += gaming.Toggle;
        controller.ShowRequested += gaming.Show;
        controller.HideRequested += gaming.Hide;
        var keyDown = false;
        void Key(bool down)
        {
            var input = new Input { Type=1, Data=new() { Keyboard=new() { Key=0x87,Flags=down ? 0u : 2u } } };
            if (SendInput(1,[input],Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            keyDown=down;
        }
        async Task Wait(Func<bool> ready, string failure)
        {
            var deadline=DateTime.UtcNow.AddSeconds(6);
            while (!ready() && DateTime.UtcNow<deadline) await Task.Delay(20);
            if (!ready()) throw new InvalidOperationException(failure+" "+gaming.Status);
        }
        try
        {
            if (new[] { 0x10,0x11,0x12,0x5B,0x5C,0x87 }.Any(key => GetAsyncKeyState(key)<0))
                throw new InvalidOperationException("Release modifier keys and F24 before running the developer check.");
            // F24 is reserved by this test before input is generated. No text or modifier keys are sent.
            var binding = new KeyboardShortcut(0,0x87,"F24");
            if (!controller.Configure(binding,LensActivationMode.Toggle)) throw new InvalidOperationException("F24 is unavailable for this developer check.");
            if (gaming.IsActive || GetForegroundWindow()==source.Handle) throw new InvalidOperationException("The check did not begin in the background with capture off.");
            Key(true); Key(false);
            await Wait(() => gaming.IsVisible,"The first background shortcut did not start and display the lens.");
            Key(true); Key(false);
            await Wait(() => !gaming.IsRequestedVisible,"The same shortcut did not hide the lens.");
            if (GetForegroundWindow()==source.Handle) throw new InvalidOperationException("Shortcut handling activated its background window.");
            gaming.Stop();
            gaming.ActivationMode=LensActivationMode.Hold;
            if (!controller.Configure(binding,gaming.ActivationMode)) throw new InvalidOperationException("Hold mode did not register.");
            Key(true);
            await Wait(() => gaming.IsVisible,"Holding the background shortcut did not display the lens.");
            Key(false);
            await Wait(() => !gaming.IsRequestedVisible,"Releasing the key did not hide the lens.");
            gaming.Stop();
            Key(true); Key(false);
            await Task.Delay(100);
            if (gaming.IsActive) throw new InvalidOperationException("An already released short press started capture in hold mode.");
            gaming.SetShortcut(null);
            if (!controller.Configure(gaming.ToggleShortcut,gaming.ActivationMode) || controller.IsRegistered)
                throw new InvalidOperationException("The empty shortcut was still registered.");
            await File.WriteAllTextAsync(outputPath,JsonSerializer.Serialize(new
            {
                BackgroundRegistration=true,FirstShortcutStartsCapture=true,SingleShortcutToggles=true,
                HoldShows=true,ReleaseHides=true,ReleasedBeforeStartupStaysHidden=true,EmptyShortcutUnregistered=true
            }));
        }
        finally
        {
            if (keyDown) Key(false);
            controller.Dispose(); gaming.Dispose();
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyUSE-lens-key-check-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public IntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X,Y;
        public uint Data,Flags,Time;
        public IntPtr Extra;
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

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
            FrameWidth=320,FrameHeight=240
        };
        using var controller = new LensShortcutController(source);
        controller.ToggleRequested += gaming.Toggle;
        controller.StartRequested += gaming.Start;
        controller.StopRequested += gaming.Stop;
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
            gaming.SetShortcut(binding);
            if (!controller.Configure(binding,LensActivationMode.Toggle)) throw new InvalidOperationException("F24 is unavailable for this developer check.");
            if (gaming.IsActive || GetForegroundWindow()==source.Handle) throw new InvalidOperationException("The check did not begin in the background with capture off.");
            Key(true); Key(false);
            await Wait(() => gaming.IsVisible,"The first background shortcut did not start and display the lens.");
            var initial=gaming.ActiveBounds ?? throw new InvalidOperationException("The visible lens has no bounds.");
            var initialHandle=gaming.LensHandle;
            if (initial.Width != 320 || initial.Height != 240) throw new InvalidOperationException("The live lens did not use independent width and height.");
            var pointer=gaming.PointerForDiagnostics ?? throw new InvalidOperationException("The native mouse hook was not installed.");
            if (!pointer.Process(0x201,initial.Left+2,initial.Top+2) ||
                !pointer.Process(0x200,initial.Left+82,initial.Top+72) || !pointer.Process(0x202,initial.Left+82,initial.Top+72))
                throw new InvalidOperationException("The live lens did not accept a border drag.");
            await Wait(() =>
            {
                if (!GetWindowRect(gaming.LensHandle,out var rect)) return false;
                return rect.Left == initial.Left+80 && rect.Top == initial.Top+70 && rect.Right-rect.Left == 320 && rect.Bottom-rect.Top == 240;
            },"The live native window did not move without resizing.");
            var moved=gaming.ActiveBounds!;
            for (var i=0;i<40;i++) pointer.Process(0x20A,moved.Left+20,moved.Top+20,120);
            if (gaming.Zoom != 8 || !gaming.IsVisible) throw new InvalidOperationException("Wheel zoom hid the lens at its upper limit.");
            for (var i=0;i<40;i++) pointer.Process(0x20A,moved.Left+20,moved.Top+20,-120);
            if (gaming.Zoom != 1 || !gaming.IsVisible) throw new InvalidOperationException("Wheel zoom hid the lens at its lower limit.");
            await Task.Delay(120);
            if (!gaming.IsVisible) throw new InvalidOperationException("Rendering failed after dragging or repeated wheel changes.");
            Key(true); Key(false);
            await Wait(() => !gaming.IsActive,"The same shortcut did not close the lens.");
            RequireReleased(initialHandle);
            if (!controller.IsRegistered) throw new InvalidOperationException("Closing capture released the background shortcut.");
            if (GetForegroundWindow()==source.Handle) throw new InvalidOperationException("Shortcut handling activated its background window.");
            Key(true); Key(false);
            await Wait(() => gaming.IsVisible,"The shortcut could not restart after full resource release.");
            if (gaming.ActiveBounds != moved || gaming.Zoom != 1) throw new InvalidOperationException("Reopening lost the dragged position or zoom.");
            var restartedHandle=gaming.LensHandle;
            Key(true); Key(false);
            await Wait(() => !gaming.IsActive,"The restarted lens could not close.");
            RequireReleased(restartedHandle);

            gaming.Start();
            await Wait(() => gaming.IsVisible,"Movement checks did not start.");
            gaming.IsFixedMode=true;
            await Wait(() => At(initial),"Fixed mode did not center the native window.");
            pointer=gaming.PointerForDiagnostics!;
            if (pointer.Process(0x201,initial.Left+2,initial.Top+2) || pointer.Process(0x200,initial.Left+82,initial.Top+72))
                throw new InvalidOperationException("Fixed mode still allowed dragging.");
            pointer.Process(0x20A,initial.Left+20,initial.Top+20,120);
            if (gaming.Zoom != 1.25 || !gaming.IsVisible) throw new InvalidOperationException("Fixed mode disabled wheel zoom.");
            gaming.IsMovableMode=true;
            pointer.Process(0x201,initial.Left+2,initial.Top+2);
            pointer.Process(0x200,initial.Left+82,initial.Top+72);
            pointer.Process(0x200,initial.Left+12,initial.Top+12);
            await Wait(() => At(initial),"Center assistance did not snap the native window.");
            if (!gaming.VerticalGuide || !gaming.HorizontalGuide) throw new InvalidOperationException("Snapping did not activate center guides.");
            pointer.Process(0x202,initial.Left+12,initial.Top+12);
            if (gaming.VerticalGuide || gaming.HorizontalGuide) throw new InvalidOperationException("Releasing the drag left center guides enabled.");
            pointer.Process(0x201,initial.Left+2,initial.Top+2);
            pointer.Process(0x200,initial.Left+82,initial.Top+72);
            pointer.Process(0x202,initial.Left+82,initial.Top+72);
            gaming.CenterCommand.Execute(null);
            await Wait(() => At(initial),"Recenter button did not move the live native window.");
            var resetHandle=gaming.LensHandle;
            gaming.IsFavorite=true;
            gaming.ResetDefaultsCommand.Execute(null);
            RequireReleased(resetHandle);
            if (gaming.Zoom != 2 || gaming.FrameWidth != 640 || gaming.FrameHeight != 384 || gaming.Sharpening != 0.35 ||
                gaming.FrameRate != 0 || !gaming.IsMovableMode || !gaming.ToggleShortcut!.Matches(binding) ||
                !gaming.IsToggleMode || !gaming.IsFavorite || !controller.IsRegistered)
                throw new InvalidOperationException("Reset did not restore picture defaults while preserving personal choices and the background shortcut.");

            // Stop before the UI can process the first-frame callback. A stale callback must not reopen the window.
            gaming.Start();
            if (!gaming.IsActive) throw new InvalidOperationException("Pending startup did not create a capture session.");
            var pendingHandle=gaming.LensHandle;
            gaming.Toggle();
            await Task.Delay(150);
            RequireReleased(pendingHandle);

            gaming.ActivationMode=LensActivationMode.Hold;
            if (!controller.Configure(binding,gaming.ActivationMode)) throw new InvalidOperationException("Hold mode did not register.");
            Key(true);
            await Wait(() => gaming.IsVisible,"Holding the background shortcut did not display the lens.");
            var heldHandle=gaming.LensHandle;
            Key(false);
            await Wait(() => !gaming.IsActive,"Releasing the key did not close the lens.");
            RequireReleased(heldHandle);
            Key(true); Key(false);
            await Task.Delay(100);
            if (gaming.IsActive) throw new InvalidOperationException("An already released short press started capture in hold mode.");
            gaming.SetShortcut(null);
            if (!controller.Configure(gaming.ToggleShortcut,gaming.ActivationMode) || controller.IsRegistered)
                throw new InvalidOperationException("The empty shortcut was still registered.");
            await File.WriteAllTextAsync(outputPath,JsonSerializer.Serialize(new
            {
                BackgroundRegistration=true,FirstShortcutStartsCapture=true,SingleShortcutToggles=true,
                HoldStartsCapture=true,ReleaseStopsCapture=true,ReleasedBeforeStartupStaysClosed=true,EmptyShortcutUnregistered=true,
                ToggleReleasesResources=true,ReopensAfterFullStop=true,PendingStartupCanClose=true,PositionAndZoomPreserved=true,
                IndependentLiveDimensions=true,NativeWindowDrag=true,WheelLimitsRemainVisible=true,FollowCaptureFrames=gaming.FrameRate == 0,
                FixedCentersAndBlocksDrag=true,FixedWheelZoom=true,CenterSnapAndGuideRelease=true,RecenterButton=true,ResetDefaultsPreservesPersonalChoices=true
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

        void RequireReleased(IntPtr previousHandle)
        {
            if (gaming.IsActive || gaming.HasCaptureResources || gaming.LensHandle != IntPtr.Zero ||
                gaming.ActiveBounds is not null || !gaming.CanConfigure || IsWindow(previousHandle))
                throw new InvalidOperationException("Closing did not release the capture, renderer, pointer hook and native window.");
        }
        bool At(PixelBounds expected) => gaming.ActiveBounds == expected && GetWindowRect(gaming.LensHandle,out var rect) &&
            rect.Left == expected.Left && rect.Top == expected.Top && rect.Right-rect.Left == expected.Width && rect.Bottom-rect.Top == expected.Height;
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}

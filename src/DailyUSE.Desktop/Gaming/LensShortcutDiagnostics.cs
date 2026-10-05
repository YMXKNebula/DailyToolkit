using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        // Real mouse input is directed at this read-only test backdrop, never a user's application.
        var backdrop = new Window
        {
            Title="DailyUSE 拖动验证",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
            Width=720,Height=540,Topmost=true,ShowActivated=false,ShowInTaskbar=false,
            WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,
            Background=new SolidColorBrush(Color.FromRgb(32,64,100))
        };
        var testScene=new System.Windows.Controls.Canvas();
        var label=new System.Windows.Controls.TextBlock { Text="DailyUSE",FontSize=28,Foreground=Brushes.White };
        System.Windows.Controls.Canvas.SetLeft(label,210); System.Windows.Controls.Canvas.SetTop(label,200);
        testScene.Children.Add(label); backdrop.Content=testScene;
        var preview=new Controls.LensPreview { DataContext=gaming };
        var previewWindow=new Window
        {
            Title="DailyUSE 预览验证",Content=preview,Width=480,Height=760,Topmost=true,
            ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.Manual,Left=24,Top=24
        };
        previewWindow.SourceInitialized += (_,_) =>
        {
            var handle=new WindowInteropHelper(previewWindow).Handle;
            SetWindowLongPtr(handle,-20,new IntPtr(GetWindowLongPtr(handle,-20).ToInt64() | 0x08000000));
        };
        controller.ToggleRequested += gaming.Toggle;
        controller.StartRequested += gaming.Start;
        controller.StopRequested += gaming.Stop;
        var keyDown = false; var mouseDown=false;
        GetCursorPos(out var originalCursor);
        void Key(bool down)
        {
            var input = new Input { Type=1, Data=new() { Keyboard=new() { Key=0x87,Flags=down ? 0u : 2u } } };
            if (SendInput(1,[input],Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            keyDown=down;
        }
        void Mouse(uint flags,int x=0,int y=0,uint data=0)
        {
            var input=new Input { Type=0,Data=new() { Mouse=new() { X=x,Y=y,Flags=flags,Data=data } } };
            if (SendInput(1,[input],Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((flags & 2) != 0) mouseDown=true;
            if ((flags & 4) != 0) mouseDown=false;
        }
        void MoveMouse(int x,int y)
        {
            var width=GetSystemMetrics(78); var height=GetSystemMetrics(79);
            var normalizedX=(int)Math.Clamp((x-GetSystemMetrics(76)+0.5)*65536/width,0,65535);
            var normalizedY=(int)Math.Clamp((y-GetSystemMetrics(77)+0.5)*65536/height,0,65535);
            Mouse(0xC001,normalizedX,normalizedY); // absolute move over the full virtual desktop
        }
        async Task Wait(Func<bool> ready, string failure)
        {
            var deadline=DateTime.UtcNow.AddSeconds(6);
            while (!ready() && DateTime.UtcNow<deadline) await Task.Delay(20);
            if (!ready()) throw new InvalidOperationException(failure+" "+gaming.Status);
        }
        try
        {
            if (new[] { 0x01,0x02,0x10,0x11,0x12,0x5B,0x5C,0x87 }.Any(key => GetAsyncKeyState(key)<0))
                throw new InvalidOperationException("Release mouse buttons, modifier keys and F24 before running the developer check.");
            if (!gaming.IsFixedMode) throw new InvalidOperationException("New settings did not default to fixed.");
            var monitor=gaming.SelectedMonitor!.Bounds;
            var planned=LensLayout.Calculate(monitor,320,240,2,monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2).Output;
            backdrop.Show();
            if (!SetWindowPos(new WindowInteropHelper(backdrop).Handle,new IntPtr(-1),planned.Left-100,planned.Top-100,720,540,0x10))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            gaming.IsMovableMode=true;
            previewWindow.Show();
            await preview.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            if (preview.CurrentFrame is null) throw new InvalidOperationException("The developer photo preview did not render.");
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
            var stage=new PixelBounds(planned.Left-100,planned.Top-100,720,540);
            var capture=gaming.CaptureForDiagnostics!;
            // Observe desktop composition, not the lens's private back buffer or HWND bounds.
            // Pause this test capture and temporarily include its own window in the observation.
            capture.Paused=true;
            if (!SetWindowDisplayAffinity(initialHandle,0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(100);
            var before=LensDesktopSnapshot.Read(stage);
            Save(before,Path.ChangeExtension(outputPath,"before.png"));
            var borderColor=Color(before,initial.Left+1,initial.Top+60);
            var backdropColor=Color(before,stage.Left+10,stage.Top+10);
            if (borderColor[1] <= borderColor[2]+50 || borderColor[1] <= borderColor[0]+20 ||
                borderColor.SequenceEqual(backdropColor))
            {
                var at=((initial.Top+60-stage.Top)*stage.Width+initial.Left+1-stage.Left)*4;
                throw new InvalidOperationException($"The magnified window's border was not visibly present before dragging. RGB={before[at+2]},{before[at+1]},{before[at]}; HWND={initialHandle}; bounds={initial}");
            }
            MoveMouse(initial.Left+160,initial.Top+120);
            await Task.Delay(30);
            if (WindowFromPoint(new() { X=initial.Left+160,Y=initial.Top+120 }) != initialHandle)
                throw new InvalidOperationException("Movable mode still made the magnified window mouse transparent.");
            Mouse(2);
            await Task.Delay(30);
            var guides=gaming.GuidesForDiagnostics!;
            if (!guides.Visible || !GetWindowRect(guides.VerticalHandle,out var vertical) ||
                !GetWindowRect(guides.HorizontalHandle,out var horizontal) ||
                vertical.Left != monitor.Left+monitor.Width/2-1 || vertical.Top != monitor.Top || vertical.Bottom != monitor.Top+monitor.Height ||
                horizontal.Top != monitor.Top+monitor.Height/2-1 || horizontal.Left != monitor.Left || horizontal.Right != monitor.Left+monitor.Width)
                throw new InvalidOperationException("Dragging did not show guides in desktop center coordinates.");
            SetWindowDisplayAffinity(guides.VerticalHandle,0); SetWindowDisplayAffinity(guides.HorizontalHandle,0);
            await Task.Delay(50);
            var assisted=LensDesktopSnapshot.Read(stage);
            var guideX=monitor.Left+monitor.Width/2-1;
            var guideY=monitor.Top+(int)Math.Ceiling((stage.Top+24-monitor.Top)/12d)*12+3;
            if (ColorAt(assisted,guideX,guideY,backdropColor) ||
                !ColorAt(assisted,guideX+4,guideY,backdropColor))
                throw new InvalidOperationException("The desktop center guide was not visibly drawn outside the lens.");
            for (var y=4;y<initial.Height-4;y+=8) for (var x=4;x<initial.Width-4;x+=8)
                if (!ColorAt(assisted,initial.Left+x,initial.Top+y,Color(before,initial.Left+x,initial.Top+y)))
                    throw new InvalidOperationException("A desktop guide was painted over the magnified picture.");
            Save(assisted,Path.ChangeExtension(outputPath,"guides.png"));
            SetWindowDisplayAffinity(guides.VerticalHandle,0x11); SetWindowDisplayAffinity(guides.HorizontalHandle,0x11);
            // Relative input follows the same path as an ordinary mouse, including pointer acceleration.
            // Verify against Windows' resulting cursor position rather than assuming a fixed speed.
            for (var step=0;step<4;step++)
            {
                GetCursorPos(out var previousCursor);
                Mouse(0x0001,16,10);
                await Task.Delay(30);
                GetCursorPos(out var currentCursor);
                if (currentCursor.X <= previousCursor.X || currentCursor.Y <= previousCursor.Y)
                    throw new InvalidOperationException("Relative mouse motion was suppressed during the drag.");
                var expected=LensPlacement.Snap(monitor,320,240,currentCursor.X-160,currentCursor.Y-120).Bounds;
                if (!At(expected)) throw new InvalidOperationException("The floating window did not follow consecutive relative mouse moves.");
            }
            // A single mouse move leaves the old frame in both axes, exercising continuation outside it.
            MoveMouse(initial.Left+400,initial.Top+290);
            await Wait(() =>
            {
                if (!GetWindowRect(gaming.LensHandle,out var rect)) return false;
                return rect.Left == initial.Left+240 && rect.Top == initial.Top+170 && rect.Right-rect.Left == 320 && rect.Bottom-rect.Top == 240;
            },"Actual mouse input from the picture's center did not move the native window.");
            GetCursorPos(out var draggedCursor);
            if (Math.Abs(draggedCursor.X-(initial.Left+400))>1 || Math.Abs(draggedCursor.Y-(initial.Top+290))>1)
                throw new InvalidOperationException("Dragging moved the floating window but suppressed the system cursor's movement.");
            Mouse(4);
            await Task.Delay(30);
            var moved=gaming.ActiveBounds!;
            if (guides.Visible) throw new InvalidOperationException("Desktop guides remained visible after mouse release.");
            var after=LensDesktopSnapshot.Read(stage);
            if (!ColorAt(after,moved.Left+1,moved.Top+60,borderColor) ||
                !ColorAt(after,initial.Left+1,initial.Top+60,backdropColor))
                throw new InvalidOperationException("The displayed magnified pixels did not move away from their original position.");
            for (var y=4;y<initial.Height-4;y+=8) for (var x=4;x<initial.Width-4;x+=8)
                if (!ColorAt(after,moved.Left+x,moved.Top+y,Color(before,initial.Left+x,initial.Top+y)))
                    throw new InvalidOperationException("The displayed magnified picture did not travel with the floating window.");
            Save(after,Path.ChangeExtension(outputPath,"png"));
            if (!SetWindowDisplayAffinity(initialHandle,0x11)) throw new Win32Exception(Marshal.GetLastWin32Error());
            capture.Paused=false;
            if (preview.CurrentFrame.Settings.PointerX != 0.5 || preview.CurrentFrame.Settings.PointerY != 0.5)
                throw new InvalidOperationException("Moving the live lens changed the independent photo position.");
            Mouse(0x0800,data:120);
            await Wait(() => gaming.Zoom == 2.25,"Actual wheel input did not reach the mouse hook.");
            for (var i=0;i<40;i++) pointer.Process(0x20A,moved.Left+20,moved.Top+20,120);
            if (gaming.Zoom != 8 || !gaming.IsVisible) throw new InvalidOperationException("Wheel zoom hid the lens at its upper limit.");
            for (var i=0;i<40;i++) pointer.Process(0x20A,moved.Left+20,moved.Top+20,-120);
            if (gaming.Zoom != 1 || !gaming.IsVisible) throw new InvalidOperationException("Wheel zoom hid the lens at its lower limit.");
            await Task.Delay(120);
            if (!gaming.IsVisible) throw new InvalidOperationException("Rendering failed after dragging or repeated wheel changes.");
            await Wait(() => preview.CurrentFrame?.Settings.Zoom == 1,"Preview zoom did not update before dragging.");
            var previewBorder=(System.Windows.Controls.Border)preview.FindName("FrameBorder");
            var previewCenter=previewBorder.PointToScreen(new(previewBorder.ActualWidth/2,previewBorder.ActualHeight/2));
            MoveMouse((int)Math.Round(previewCenter.X),(int)Math.Round(previewCenter.Y));
            await Task.Delay(30); Mouse(2); await Task.Delay(30);
            MoveMouse((int)Math.Round(previewCenter.X)+48,(int)Math.Round(previewCenter.Y)+28);
            await Wait(() => preview.CurrentFrame!.Settings.PointerX != 0.5 && preview.CurrentFrame.Settings.PointerY != 0.5,
                "Actual mouse input did not drag the photo preview.");
            Mouse(4); await Task.Delay(50);
            var previewX=preview.CurrentFrame!.Settings.PointerX; var previewY=preview.CurrentFrame.Settings.PointerY;
            if (!At(moved)) throw new InvalidOperationException("Dragging the photo moved the independent live lens.");
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
            var revision=gaming.PositionResetVersion;
            gaming.IsFixedMode=true;
            await Task.Delay(50);
            if (!At(moved) || gaming.PositionResetVersion != revision)
                throw new InvalidOperationException("Fixed mode moved the lens instead of locking its current position.");
            if (preview.CurrentFrame!.Settings.PointerX != previewX || preview.CurrentFrame.Settings.PointerY != previewY)
                throw new InvalidOperationException("Fixed mode reset the photo's current position.");
            var lockedHandle=gaming.LensHandle;
            gaming.Stop(); RequireReleased(lockedHandle);
            gaming.Start();
            await Wait(() => gaming.IsVisible,"The fixed lens did not restart.");
            if (!At(moved)) throw new InvalidOperationException("Reopening a fixed lens lost its locked position.");
            pointer=gaming.PointerForDiagnostics!;
            if (pointer.Process(0x201,moved.Left+2,moved.Top+2) || pointer.Process(0x201,moved.Left+160,moved.Top+120) ||
                pointer.Process(0x200,moved.Left+82,moved.Top+72))
                throw new InvalidOperationException("Fixed mode still allowed dragging.");
            pointer.Process(0x20A,moved.Left+20,moved.Top+20,120);
            if (gaming.Zoom != 1.25 || !gaming.IsVisible) throw new InvalidOperationException("Fixed mode disabled wheel zoom.");
            gaming.IsMovableMode=true;
            pointer.Process(0x201,moved.Left+160,moved.Top+120);
            pointer.Process(0x200,initial.Left+170,initial.Top+130);
            await Wait(() => At(initial),"Center assistance did not snap the native window.");
            if (!gaming.VerticalGuide || !gaming.HorizontalGuide) throw new InvalidOperationException("Snapping did not activate center guides.");
            pointer.Process(0x202,initial.Left+170,initial.Top+130);
            if (gaming.VerticalGuide || gaming.HorizontalGuide) throw new InvalidOperationException("Releasing the drag left center guides enabled.");
            pointer.Process(0x201,initial.Left+2,initial.Top+2);
            pointer.Process(0x200,initial.Left+82,initial.Top+72);
            pointer.Process(0x202,initial.Left+82,initial.Top+72);
            gaming.IsFixedMode=true;
            var resetHandle=gaming.LensHandle;
            gaming.IsFavorite=true;
            gaming.ResetDefaultsCommand.Execute(null);
            RequireReleased(resetHandle);
            if (gaming.Zoom != 2 || gaming.FrameWidth != 640 || gaming.FrameHeight != 384 || gaming.Sharpening != 0.35 ||
                gaming.FrameRate != 0 || !gaming.IsFixedMode || !gaming.ToggleShortcut!.Matches(binding) ||
                !gaming.IsToggleMode || !gaming.IsFavorite || !controller.IsRegistered)
                throw new InvalidOperationException("Reset did not restore picture defaults while preserving personal choices and the background shortcut.");
            gaming.Start();
            await Wait(() => gaming.IsVisible,"The reset lens did not start.");
            var defaults=LensLayout.Calculate(gaming.SelectedMonitor!.Bounds,640,384,2,
                monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2).Output;
            if (!At(defaults) || !gaming.IsFixedMode) throw new InvalidOperationException("Restoring defaults did not recenter and lock the lens.");
            await Wait(() => preview.CurrentFrame?.Settings is { Width:640,Height:384,Zoom:2,PointerX:0.5,PointerY:0.5 },
                "Restoring defaults did not reset the independent photo position and picture settings.");
            var defaultHandle=gaming.LensHandle;
            gaming.Stop(); RequireReleased(defaultHandle);

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
                IndependentLiveDimensions=true,NativePictureDrag=true,RealMouseHookDrag=true,RealWheelInput=true,
                WheelLimitsRemainVisible=true,FollowCaptureFrames=gaming.FrameRate == 0,
                FixedIsDefault=true,FixedLocksCurrentPosition=true,FixedReopenPreservesPosition=true,FixedWheelZoom=true,
                CenterSnapAndGuideRelease=true,ResetDefaultsRecenters=true,ResetDefaultsPreservesPersonalChoices=true,
                LiveDragIndependent=true,PreviewDragIndependent=true,PreviewFixedKeepsPosition=true,ResetDefaultsResetsPreview=true,
                NativeWindowReceivesDrag=true,CompositedPictureActuallyMoves=true,DesktopGuideCoordinates=true,DesktopGuidesHideOnRelease=true,
                DragContinuesOutsideOriginalFrame=true,DesktopGuidesVisibleOutsideLens=true,DesktopGuidesAvoidMagnifiedPicture=true,
                RealCursorFollowsDrag=true,ContinuousRelativeMouseDrag=true
            }));

            byte[] Color(byte[] pixels,int x,int y)
            {
                var at=((y-stage.Top)*stage.Width+x-stage.Left)*4;
                return [pixels[at],pixels[at+1],pixels[at+2]];
            }
            bool ColorAt(byte[] pixels,int x,int y,byte[] expected)
            {
                var actual=Color(pixels,x,y);
                return actual.Zip(expected).All(pair => Math.Abs(pair.First-pair.Second)<=4);
            }
            void Save(byte[] pixels,string path)
            {
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(stage.Width,stage.Height,96,96,PixelFormats.Bgra32,null,pixels,stage.Width*4)));
                using var file=File.Create(path); encoder.Save(file);
            }
        }
        finally
        {
            if (mouseDown) Mouse(4);
            if (keyDown) Key(false);
            controller.Dispose(); gaming.Dispose();
            previewWindow.Close(); backdrop.Close();
            SetCursorPos(originalCursor.X,originalCursor.Y);
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
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowDisplayAffinity(IntPtr window,uint affinity);
}

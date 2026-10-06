using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Gaming;

internal static class LensShortcutDiagnostics
{
    public static async Task RunAsync(string outputPath)
    {
        using var check=new CheckSession();
        await check.RunScenariosAsync(outputPath);
    }

    private sealed class CheckSession : IDisposable
    {
        private readonly DirectoryInfo directory=Directory.CreateTempSubdirectory("DailyToolkit-lens-key-check-");
        private HwndSource source=null!;
        private GamingViewModel gaming=null!;
        private LensShortcutController controller=null!;
        private Window backdrop=null!,previewWindow=null!;
        private Controls.LensPreview preview=null!;
        private bool keyDown,mouseDown,cursorSaved;
        private NativePoint originalCursor;
        private PixelBounds monitor=null!,planned=null!,initial=null!,moved=null!,stage=null!;
        private IntPtr initialHandle;
        private LensPointerController pointer=null!;
        private LensCapture capture=null!;
        private KeyboardShortcut binding=null!;
        private bool? crossMonitorDrag;
        private bool? queuedCrossMonitorDrag;
        private bool? relativeCrossMonitorDrag;
        private byte[] before=null!,borderColor=null!,backdropColor=null!;
        private double previewX,previewY;

        public CheckSession()
        {
            try
            {
                cursorSaved=GetCursorPos(out originalCursor);
                source = new HwndSource(new HwndSourceParameters("DailyToolkit background shortcut check")
                {
                    PositionX=-20000,PositionY=-20000,Width=1,Height=1,WindowStyle=unchecked((int)0x80000000)
                });
                gaming = new GamingViewModel(new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")))
                {
                    FrameWidth=320,FrameHeight=240
                };
                controller = new LensShortcutController(source);
                // Real mouse input is directed at this read-only test backdrop, never a user's application.
                backdrop = new Window
                {
                    Title="DailyToolkit 拖动验证",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                    Width=720,Height=540,Topmost=true,ShowActivated=false,ShowInTaskbar=false,
                    WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,
                    Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(32,64,100))
                };
                var testScene=new System.Windows.Controls.Canvas();
                var label=new System.Windows.Controls.TextBlock { Text="DailyToolkit",FontSize=28,Foreground=Brushes.White };
                System.Windows.Controls.Canvas.SetLeft(label,210); System.Windows.Controls.Canvas.SetTop(label,200);
                testScene.Children.Add(label); backdrop.Content=testScene;
                preview=new Controls.LensPreview { DataContext=gaming };
                previewWindow=new Window
                {
                    Title="DailyToolkit 预览验证",Content=preview,Width=480,Height=760,Topmost=true,
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

            }
            catch { Dispose(); throw; }
        }

        public async Task RunScenariosAsync(string outputPath)
        {
            await CheckRegistrationAsync();
            await CheckBackgroundStartAsync();
            await CheckNativeDragAsync(outputPath);
            await CheckWheelZoomAsync();
            await CheckLiveParameterUpdatesAsync();
            await CheckPreviewDragAsync();
            await CheckToggleLifecycleAsync();
            await CheckFixedAndResetAsync();
            await CheckPendingStartupAsync();
            await CheckHoldAndEmptyShortcutAsync();
            await CheckFailureCleanupAsync();
            await CheckCrossMonitorDragAsync();
            await CheckPreviewResizeDragAsync();
            await WriteReportAsync(outputPath);
        }

        private async Task CheckRegistrationAsync()
        {
            if (new[] { 0x01,0x02,0x10,0x11,0x12,0x5B,0x5C,0x87 }.Any(key => GetAsyncKeyState(key)<0))
                throw new InvalidOperationException("Release mouse buttons, modifier keys and F24 before running the developer check.");
            if (!gaming.IsFixedMode) throw new InvalidOperationException("New settings did not default to fixed.");
            var primary=gaming.Monitors[0].Bounds;
            MoveMouse(primary.Left+primary.Width/2,primary.Top+primary.Height/2);
            gaming.RefreshMonitors();
            monitor=gaming.SelectedMonitor!.Bounds;
            planned=LensLayout.Calculate(monitor,320,240,2,monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2).Output;
            backdrop.Show();
            if (!SetWindowPos(new WindowInteropHelper(backdrop).Handle,new IntPtr(-1),planned.Left-100,planned.Top-100,720,540,0x10))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            gaming.IsMovableMode=true;
            previewWindow.Show();
            await preview.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            if (preview.CurrentFrame is null) throw new InvalidOperationException("The developer photo preview did not render.");
            // F24 is reserved by this test before input is generated. No text or modifier keys are sent.
            binding= new KeyboardShortcut(0,0x87,"F24");
            gaming.SetShortcut(binding);
            if (!controller.Configure(binding,LensActivationMode.Toggle)) throw new InvalidOperationException("F24 is unavailable for this developer check.");
            if (gaming.IsActive || GetForegroundWindow()==source.Handle) throw new InvalidOperationException("The check did not begin in the background with capture off.");
        }

        private async Task CheckBackgroundStartAsync()
        {
            Key(true); Key(false);
            await Wait(() => gaming.IsVisible,"The first background shortcut did not start and display the lens.");
            initial=gaming.ActiveBounds ?? throw new InvalidOperationException("The visible lens has no bounds.");
            initialHandle=gaming.LensHandle;
            if (initial.Width != 320 || initial.Height != 240) throw new InvalidOperationException("The live lens did not use independent width and height.");
            pointer=gaming.PointerForDiagnostics ?? throw new InvalidOperationException("The native mouse hook was not installed.");
        }

        private async Task CheckNativeDragAsync(string outputPath)
        {
            stage=new PixelBounds(planned.Left-100,planned.Top-100,720,540);
            capture=gaming.CaptureForDiagnostics!;
            // Observe desktop composition, not the lens's private back buffer or HWND bounds.
            // Pause this test capture and temporarily include its own window in the observation.
            capture.Paused=true;
            if (!SetWindowDisplayAffinity(initialHandle,0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(100);
            before=LensDesktopSnapshot.Read(stage);
            Save(before,Path.ChangeExtension(outputPath,"before.png"));
            borderColor=Color(before,initial.Left+1,initial.Top+60);
            backdropColor=Color(before,stage.Left+10,stage.Top+10);
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
            await Wait(() => gaming.GuidesForDiagnostics?.Visible == true,"Dragging did not show the desktop guides.");
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
            moved=gaming.ActiveBounds!;
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
            if (preview.CurrentFrame!.Settings.PointerX != 0.5 || preview.CurrentFrame.Settings.PointerY != 0.5)
                throw new InvalidOperationException("Moving the live lens changed the independent photo position.");
        }

        private async Task CheckWheelZoomAsync()
        {
            Mouse(0x0800,data:120);
            await Wait(() => gaming.Zoom == 2.25,"Actual wheel input did not reach the mouse hook.");
            gaming.WheelZoomEnabled=false;
            Mouse(0x0800,data:120); await Task.Delay(80);
            if (gaming.Zoom != 2.25) throw new InvalidOperationException("Disabled wheel zoom changed the live lens.");
            gaming.WheelZoomEnabled=true;
            for (var i=0;i<40;i++) pointer.Process(0x20A,moved.Left+20,moved.Top+20,120);
            if (gaming.Zoom != 10 || !gaming.IsVisible) throw new InvalidOperationException("Wheel zoom hid the lens at its upper limit.");
            for (var i=0;i<40;i++) pointer.Process(0x20A,moved.Left+20,moved.Top+20,-120);
            if (gaming.Zoom != 1 || !gaming.IsVisible) throw new InvalidOperationException("Wheel zoom hid the lens at its lower limit.");
            await Task.Delay(120);
            if (!gaming.IsVisible) throw new InvalidOperationException("Rendering failed after dragging or repeated wheel changes.");
        }

        private async Task CheckLiveParameterUpdatesAsync()
        {
            var handle=gaming.LensHandle;
            var activeCapture=gaming.CaptureForDiagnostics!;
            var originalSharpness=gaming.Sharpening;
            var originalZoom=gaming.Zoom;
            var frames=activeCapture.FramesRendered;
            gaming.Sharpening=1; gaming.Zoom=10;
            await Wait(() => activeCapture.FramesRendered > frames,"Live settings did not produce another frame.");
            if (gaming.LensHandle != handle || gaming.CaptureForDiagnostics != activeCapture || !gaming.IsVisible)
                throw new InvalidOperationException("Zoom or sharpening recreated the native window or capture.");
            // Let workers finish/restart between bursts while the UI keeps publishing new settings.
            // Check the final actual capture settings after workers have had time to drain.
            for(var batch=0;batch<40;batch++)
            {
                for(var step=0;step<40;step++)
                {
                    gaming.Zoom=1+(step%37)*0.25;
                    gaming.Sharpening=(step%11)/10d;
                }
                await Task.Delay(2);
            }
            gaming.Sharpening=originalSharpness; gaming.Zoom=originalZoom;
            frames=activeCapture.FramesRendered;
            await Wait(() => activeCapture.FramesRendered > frames,"The final burst settings did not render.");
            await Task.Delay(80);
            var bounds=gaming.ActiveBounds!;
            var expected=LensLayout.Calculate(gaming.SelectedMonitor!.Bounds,bounds.Width,bounds.Height,originalZoom,
                bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2).Source;
            if (activeCapture.SourceForDiagnostics != (expected,originalSharpness) ||
                gaming.LensHandle != handle || gaming.CaptureForDiagnostics != activeCapture || !gaming.IsVisible)
                throw new InvalidOperationException("A completed refresh worker overwrote the final source or sharpening settings.");
        }

        private async Task CheckFailureCleanupAsync()
        {
            gaming.Start();
            await Wait(() => gaming.IsVisible,"Failure cleanup check did not start.");
            var handle=gaming.LensHandle;
            // Inject the existing failure signal; no driver or physical monitor is disrupted.
            var failure=(Action<string>?)typeof(LensCapture).GetField(nameof(LensCapture.Failed),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(gaming.CaptureForDiagnostics!);
            failure!("Simulated capture failure");
            await Wait(() => !gaming.IsActive,"Capture failure did not stop the session.");
            RequireReleased(handle);
            if (gaming.Status != "Simulated capture failure") throw new InvalidOperationException("Capture failure lost its status.");
            gaming.RefreshMonitors();
            foreach (var selected in gaming.Monitors)
            {
                MoveMouse(selected.Bounds.Left+selected.Bounds.Width/2,selected.Bounds.Top+selected.Bounds.Height/2);
                await Task.Delay(30);
                gaming.Start();
                await Wait(() => gaming.IsVisible,"Capture did not restart on the selected monitor.");
                if (gaming.SelectedMonitor?.Handle != selected.Handle)
                    throw new InvalidOperationException("Startup did not use the cursor's monitor.");
                var bounds=gaming.ActiveBounds!;
                if (bounds.Left < selected.Bounds.Left || bounds.Top < selected.Bounds.Top ||
                    bounds.Left+bounds.Width > selected.Bounds.Left+selected.Bounds.Width ||
                    bounds.Top+bounds.Height > selected.Bounds.Top+selected.Bounds.Height)
                    throw new InvalidOperationException("Capture used another monitor's bounds.");
                handle=gaming.LensHandle;
                gaming.Stop(); RequireReleased(handle);
            }
            gaming.Start();
            await Wait(() => gaming.IsVisible,"Active disposal check did not start.");
            handle=gaming.LensHandle;
            gaming.Dispose(); RequireReleased(handle);
        }

        private async Task CheckCrossMonitorDragAsync()
        {
            if (gaming.Monitors.Count < 2) return;
            var origin=gaming.Monitors[0];
            var target=gaming.Monitors[1];
            var x=target.Bounds.Left+target.Bounds.Width/2;
            var y=target.Bounds.Top+target.Bounds.Height/2;
            var originalScene=backdrop.Content;
            backdrop.Content=null;
            var targetScene=new Window
            {
                Width=640,Height=480,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                ShowInTaskbar=false,ShowActivated=false,Topmost=true,
                Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(40,140,70))
            };
            try
            {
                targetScene.Show();
                if (!SetWindowPos(new WindowInteropHelper(targetScene).Handle,new IntPtr(-1),x-320,y-240,640,480,0x10))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                MoveMouse(origin.Bounds.Left+origin.Bounds.Width/2,origin.Bounds.Top+origin.Bounds.Height/2);
                gaming.IsMovableMode=true; gaming.FrameWidth=800; gaming.FrameHeight=1200; gaming.Zoom=10;
                gaming.Start();
                await Wait(() => gaming.IsVisible,"Cross-screen drag did not start.");
                var handle=gaming.LensHandle;
                var oldCapture=gaming.CaptureForDiagnostics;
                var oldPointer=gaming.PointerForDiagnostics;
                var output=gaming.ActiveBounds!;
                MoveMouse(output.Left+output.Width/2,output.Top+output.Height/2);
                await Task.Delay(30); Mouse(2); await Task.Delay(30);
                MoveMouse(x,y);
                await Wait(() => gaming.SelectedMonitor?.Handle == target.Handle && gaming.IsVisible &&
                    gaming.CaptureForDiagnostics != oldCapture && ColorMatches(40,140,70),
                    "Dragging across screens did not switch to the target monitor's actual picture.");
                if (gaming.LensHandle != handle || gaming.PointerForDiagnostics != oldPointer)
                    throw new InvalidOperationException("Cross-screen movement recreated the window or canceled the mouse controller.");
                MoveMouse(origin.Bounds.Left+origin.Bounds.Width/2,origin.Bounds.Top+origin.Bounds.Height/2);
                await Wait(() => gaming.SelectedMonitor?.Handle == origin.Handle && gaming.IsVisible && ColorMatches(32,64,100),
                    "A held drag could not return across the screen boundary.");
                var returned=gaming.ActiveBounds!;
                if (returned.Width != Math.Min(800,origin.Bounds.Width) || returned.Height != Math.Min(1200,origin.Bounds.Height))
                    throw new InvalidOperationException("Returning to a larger monitor did not restore the requested frame dimensions.");
                var session=(ScreenLensSession)typeof(GamingViewModel).GetField("_session",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(gaming)!;
                var injected=false;
                var originX=origin.Bounds.Left+origin.Bounds.Width/2;
                var originY=origin.Bounds.Top+origin.Bounds.Height/2;
                void MoveDuringSwitch(string _)
                {
                    var bounds=gaming.ActiveBounds;
                    if (injected || bounds is null || bounds.Left < target.Bounds.Left ||
                        bounds.Top < target.Bounds.Top || bounds.Left+bounds.Width > target.Bounds.Left+target.Bounds.Width ||
                        bounds.Top+bounds.Height > target.Bounds.Top+target.Bounds.Height) return;
                    injected=true;
                    // Input can arrive while a native/COM call pumps messages during a handoff.
                    // Route it through the real controller before the new capture finishes starting.
                    MoveMouse(originX,originY);
                    oldPointer!.Process(0x200,originX,originY);
                }
                session.StatusChanged+=MoveDuringSwitch;
                try
                {
                    MoveMouse(x,y);
                    await Wait(() => injected && gaming.SelectedMonitor?.Handle == origin.Handle && ColorMatches(32,64,100),
                        "A drag queued during capture handoff was lost or left the move queue stuck.");
                    MoveMouse(originX+80,originY+40);
                    await Wait(() => gaming.ActiveBounds?.Left == originX+80-returned.Width/2,
                        "New mouse movement stayed blocked after a queued cross-screen drag.");
                    queuedCrossMonitorDrag=true;
                }
                finally { session.StatusChanged-=MoveDuringSwitch; }
                Mouse(4);
                gaming.Stop(); RequireReleased(handle);
                gaming.FrameWidth=640; gaming.FrameHeight=384;
                MoveMouse(originX,originY);
                gaming.Start();
                await Wait(() => gaming.IsVisible,"Continuous cross-screen drag did not start.");
                var starting=gaming.ActiveBounds!;
                MoveMouse(starting.Left+starting.Width/2,starting.Top+starting.Height/2);
                await Task.Delay(30); Mouse(2);
                await Wait(() => gaming.GuidesForDiagnostics?.Visible == true,"Continuous drag did not acquire the picture.");
                GetCursorPos(out var grip);
                var offsetX=grip.X-starting.Left; var offsetY=grip.Y-starting.Top;
                await WalkToAsync(x,y);
                await WalkToAsync(originX,originY);
                Mouse(4);
                relativeCrossMonitorDrag=true;
                handle=gaming.LensHandle;
                gaming.Stop(); RequireReleased(handle);
                crossMonitorDrag=true;

                async Task WalkToAsync(int destinationX,int destinationY)
                {
                    var bounds=gaming.Monitors.Select(m => m.Bounds).ToArray();
                    for(var step=0;step<160;step++)
                    {
                        GetCursorPos(out var current);
                        if (Math.Abs(current.X-destinationX)<=40 && Math.Abs(current.Y-destinationY)<=40) return;
                        Mouse(1,Math.Clamp(destinationX-current.X,-24,24),Math.Clamp(destinationY-current.Y,-24,24));
                        await Task.Delay(15);
                        GetCursorPos(out current);
                        var screen=gaming.Monitors[LensPlacement.MonitorAt(bounds,current.X,current.Y)];
                        var expected=LensPlacement.Snap(screen.Bounds,640,384,current.X-offsetX,current.Y-offsetY).Bounds;
                        await Wait(() => gaming.SelectedMonitor?.Handle == screen.Handle && gaming.ActiveBounds == expected,
                            "The lens stopped following consecutive relative mouse moves across screens.");
                    }
                    throw new InvalidOperationException("Consecutive relative mouse movement could not reach the other screen.");
                }
            }
            finally { Mouse(4); gaming.Stop(); targetScene.Close(); backdrop.Content=originalScene; }

            bool ColorMatches(byte red,byte green,byte blue)
            {
                var bounds=gaming.ActiveBounds!;
                var pixels=gaming.CaptureForDiagnostics!.ReadOutput();
                var at=(bounds.Height/2*bounds.Width+bounds.Width/2)*4;
                return Math.Abs(pixels[at]-blue)<=2 && Math.Abs(pixels[at+1]-green)<=2 && Math.Abs(pixels[at+2]-red)<=2;
            }
        }

        private async Task CheckPreviewResizeDragAsync()
        {
            gaming.IsMovableMode=true;
            gaming.FrameWidth=320; gaming.FrameHeight=240;
            await Wait(() => preview.CurrentFrame?.Settings is { Width:320,Height:240 },"Small preview did not render.");
            var screen=(FrameworkElement)preview.FindName("Screen");
            var border=(FrameworkElement)preview.FindName("FrameBorder");
            var start=border.PointToScreen(new(border.ActualWidth/2,border.ActualHeight/2));
            var edge=screen.PointToScreen(new(screen.ActualWidth-6,screen.ActualHeight-6));
            MoveMouse((int)Math.Round(start.X),(int)Math.Round(start.Y));
            await Task.Delay(30); Mouse(2); await Task.Delay(30);
            MoveMouse((int)Math.Round(edge.X),(int)Math.Round(edge.Y));
            await Wait(() => preview.CurrentFrame?.Settings is { PointerX: > 0.8,PointerY: > 0.8 },
                "Preview did not drag to the lower right edge.");
            Mouse(4); await Task.Delay(50);
            gaming.FrameWidth=800; gaming.FrameHeight=640;
            await Wait(() => preview.CurrentFrame?.Settings is { Width:800,Height:640 },"Enlarged edge preview did not render.");
            var frame=preview.CurrentFrame!;
            var before=frame.Layout.Output;
            start=border.PointToScreen(new(border.ActualWidth/2,border.ActualHeight/2));
            var startX=(int)Math.Round(start.X); var startY=(int)Math.Round(start.Y);
            var endX=startX-32; var endY=startY-24;
            var from=screen.PointFromScreen(new(startX,startY));
            var to=screen.PointFromScreen(new(endX,endY));
            var expected=LensPlacement.Snap(new(0,0,frame.Settings.ScreenWidth,frame.Settings.ScreenHeight),
                before.Width,before.Height,
                before.Left+(int)Math.Round((to.X-from.X)/screen.ActualWidth*frame.Settings.ScreenWidth),
                before.Top+(int)Math.Round((to.Y-from.Y)/screen.ActualHeight*frame.Settings.ScreenHeight)).Bounds;
            MoveMouse(startX,startY); await Task.Delay(30); Mouse(2); await Task.Delay(30);
            MoveMouse(endX,endY);
            await Wait(() => preview.CurrentFrame?.Layout.Output == expected,
                "Resizing at the edge left the preview drag offset at its former center.");
            Mouse(4); await Task.Delay(30);
            if (gaming.IsActive || gaming.HasCaptureResources)
                throw new InvalidOperationException("Dragging a resized photo started screen capture.");
        }

        private async Task CheckPreviewDragAsync()
        {
            await Wait(() => preview.CurrentFrame?.Settings.Zoom == 1,"Preview zoom did not update before dragging.");
            var previewBorder=(System.Windows.Controls.Border)preview.FindName("FrameBorder");
            var previewCenter=previewBorder.PointToScreen(new(previewBorder.ActualWidth/2,previewBorder.ActualHeight/2));
            MoveMouse((int)Math.Round(previewCenter.X),(int)Math.Round(previewCenter.Y));
            await Task.Delay(30); Mouse(2); await Task.Delay(30);
            MoveMouse((int)Math.Round(previewCenter.X)+48,(int)Math.Round(previewCenter.Y)+28);
            await Wait(() => preview.CurrentFrame!.Settings.PointerX != 0.5 && preview.CurrentFrame.Settings.PointerY != 0.5,
                "Actual mouse input did not drag the photo preview.");
            Mouse(4); await Task.Delay(50);
            previewX=preview.CurrentFrame!.Settings.PointerX; previewY=preview.CurrentFrame.Settings.PointerY;
            gaming.WheelZoomEnabled=false;
            Mouse(0x0800,data:120); await Task.Delay(80);
            if (gaming.Zoom != 1) throw new InvalidOperationException("Disabled wheel zoom still changed the photo preview.");
            gaming.WheelZoomEnabled=true;
            if (!At(moved)) throw new InvalidOperationException("Dragging the photo moved the independent live lens.");
        }

        private async Task CheckToggleLifecycleAsync()
        {
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

        }

        private async Task CheckFixedAndResetAsync()
        {
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

        }

        private async Task CheckPendingStartupAsync()
        {
            // Stop before the UI can process the first-frame callback. A stale callback must not reopen the window.
            gaming.Start();
            if (!gaming.IsActive) throw new InvalidOperationException("Pending startup did not create a capture session.");
            var pendingHandle=gaming.LensHandle;
            gaming.Toggle();
            await Task.Delay(150);
            RequireReleased(pendingHandle);

        }

        private async Task CheckHoldAndEmptyShortcutAsync()
        {
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
        }

        public Task WriteReportAsync(string outputPath)
        {
            return File.WriteAllTextAsync(outputPath,JsonSerializer.Serialize(new
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
                RealCursorFollowsDrag=true,ContinuousRelativeMouseDrag=true,TenTimesRemainsVisible=true,
                LiveWheelCanBeDisabled=true,PreviewWheelCanBeDisabled=true,
                LiveParametersReuseCapture=true,LatestSourceSettingsWin=true,ResizedPreviewDragUsesDisplayedCenter=true,
                SimulatedCaptureFailureCleanup=true,MonitorSelectionAndDispose=true,CursorMonitorOnStart=true,
                AvailableMonitors=gaming.Monitors.Count,CrossMonitorDragVerified=crossMonitorDrag,
                QueuedCrossMonitorDragVerified=queuedCrossMonitorDrag,RelativeCrossMonitorDragVerified=relativeCrossMonitorDrag
            }));

        }

        private void Key(bool down)
        {
            var input = new Input { Type=1, Data=new() { Keyboard=new() { Key=0x87,Flags=down ? 0u : 2u } } };
            if (SendInput(1,[input],Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            keyDown=down;
        }
        private void Mouse(uint flags,int x=0,int y=0,uint data=0)
        {
            var input=new Input { Type=0,Data=new() { Mouse=new() { X=x,Y=y,Flags=flags,Data=data } } };
            if (SendInput(1,[input],Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((flags & 2) != 0) mouseDown=true;
            if ((flags & 4) != 0) mouseDown=false;
        }
        private void MoveMouse(int x,int y)
        {
            var width=GetSystemMetrics(78); var height=GetSystemMetrics(79);
            var normalizedX=(int)Math.Clamp((x-GetSystemMetrics(76)+0.5)*65536/width,0,65535);
            var normalizedY=(int)Math.Clamp((y-GetSystemMetrics(77)+0.5)*65536/height,0,65535);
            Mouse(0xC001,normalizedX,normalizedY); // absolute move over the full virtual desktop
        }
        private async Task Wait(Func<bool> ready, string failure)
        {
            var deadline=DateTime.UtcNow.AddSeconds(6);
            while (!ready() && DateTime.UtcNow<deadline) await Task.Delay(20);
            if (!ready()) throw new InvalidOperationException(failure+" "+gaming.Status);
        }
        private byte[] Color(byte[] pixels,int x,int y)
        {
            var at=((y-stage.Top)*stage.Width+x-stage.Left)*4;
            return [pixels[at],pixels[at+1],pixels[at+2]];
        }
        private bool ColorAt(byte[] pixels,int x,int y,byte[] expected)
        {
            var actual=Color(pixels,x,y);
            return actual.Zip(expected).All(pair => Math.Abs(pair.First-pair.Second)<=4);
        }
        private void Save(byte[] pixels,string path)
        {
            var encoder=new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(stage.Width,stage.Height,96,96,PixelFormats.Bgra32,null,pixels,stage.Width*4)));
            using var file=File.Create(path); encoder.Save(file);
        }

        private void RequireReleased(IntPtr previousHandle)
        {
            if (gaming.IsActive || gaming.HasCaptureResources || gaming.LensHandle != IntPtr.Zero ||
                gaming.ActiveBounds is not null || !gaming.CanConfigure || IsWindow(previousHandle))
                throw new InvalidOperationException("Closing did not release the capture, renderer, pointer hook and native window.");
        }
        private bool At(PixelBounds expected) => gaming.ActiveBounds == expected && GetWindowRect(gaming.LensHandle,out var rect) &&
            rect.Left == expected.Left && rect.Top == expected.Top && rect.Right-rect.Left == expected.Width && rect.Bottom-rect.Top == expected.Height;

        public void Dispose()
        {
            if (mouseDown) Mouse(4);
            if (keyDown) Key(false);
            controller?.Dispose(); gaming?.Dispose();
            previewWindow?.Close(); backdrop?.Close();
            if (cursorSaved) SetCursorPos(originalCursor.X,originalCursor.Y);
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-lens-key-check-",StringComparison.Ordinal)) directory.Delete(recursive:true);

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

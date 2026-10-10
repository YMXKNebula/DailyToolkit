using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Controls;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckToolkitInterfaceAsync(string? images=null)
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-interface-tests-");
        var gamingStore=new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json"));
        var appStore=new AppPreferencesStore(Path.Combine(directory.FullName,"settings.json"));
        var display=new DisplayInfo(1920,1080,1,1920,1040,false);
        var probe=new ControlledProbe(display); probe.Finish.TrySetResult();
        var model=new MainViewModel(probe,display,new LocalProbe(),gamingPreferencesStore:gamingStore,appPreferencesStore:appStore,
            favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
            navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),startupRegistration:new StartupFake(),notesStore:IsolatedNotes());
        var window=new MainWindow(model,enableShortcuts:false) { ShowActivated=false,ShowInTaskbar=false,
            WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
        IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            {
                var child=VisualTreeHelper.GetChild(root,i); yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        async Task Click(Button button)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ContextIdle);
        }
        async Task WaitFor(Func<bool> condition,string message)
        {
            var deadline=DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
            Require(condition(),message);
        }
        RenderTargetBitmap Frame()
        {
            window.UpdateLayout(); var root=window.PreviewContent; var dpi=VisualTreeHelper.GetDpi(root);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),
                (int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
            bitmap.Render(root); return bitmap;
        }
        void Image(string name)
        {
            if (images is null) return;
            var path=Path.Combine(Path.GetFullPath(images),name+".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Frame()));
            using var file=File.Create(path); encoder.Save(file);
        }
        byte[] RegionPixels(Geometry region,BitmapSource frame)
        {
            var body=(FrameworkElement)window.FindName("BodyContent");
            var origin=body.TranslatePoint(new Point(),window.PreviewContent);
            var visual=new DrawingVisual(); using (var draw=visual.RenderOpen())
            {
                draw.PushTransform(new TranslateTransform(origin.X,origin.Y)); draw.DrawGeometry(Brushes.White,null,region); draw.Pop();
            }
            var mask=new RenderTargetBitmap(frame.PixelWidth,frame.PixelHeight,frame.DpiX,frame.DpiY,PixelFormats.Pbgra32); mask.Render(visual);
            var pixels=new byte[frame.PixelWidth*frame.PixelHeight*4]; mask.CopyPixels(pixels,frame.PixelWidth*4,0); return pixels;
        }
        void SameFrame(BitmapSource before,string message,Geometry? region=null)
        {
            var after=Frame(); var a=new byte[before.PixelWidth*before.PixelHeight*4]; var b=new byte[a.Length];
            before.CopyPixels(a,before.PixelWidth*4,0); after.CopyPixels(b,after.PixelWidth*4,0);
            var mask=region is null ? null : RegionPixels(region,before);
            var different=0; for (var i=0;i<a.Length;i+=4)
            {
                if (mask is not null && mask[i+3] != 255) continue;
                if (Math.Abs(a[i]-b[i])>3 || Math.Abs(a[i+1]-b[i+1])>3 || Math.Abs(a[i+2]-b[i+2])>3 || Math.Abs(a[i+3]-b[i+3])>3) different++;
            }
            Require(different == 0,$"{message}: {different} pixels");
        }
        try
        {
            Require(model.ShowScreenLens && model.NavigationItems.Single(item => item.Id == "screen-lens").CanToggle && !probe.DetailsStarted.Task.IsCompleted,
                "Default tool page eagerly probed computer details");
            window.Show(); window.UpdateLayout(); TextOptions.SetTextRenderingMode(window.PreviewContent,TextRenderingMode.Grayscale);
            await window.WaitForLensPreviewAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var preview=(LensPreview)window.FindName("LensPhotoPreview");
            model.Gaming.FrameWidth=768; model.Gaming.FrameHeight=512;
            model.Gaming.FrameSizePreset=LensFrameSizePreset.Medium; model.Gaming.AspectRatio=LensAspectRatio.Square;
            await WaitFor(() => preview.CurrentFrame?.Settings.Width == model.Gaming.EffectiveFrameWidth &&
                preview.CurrentFrame?.Settings.Height == model.Gaming.EffectiveFrameHeight,"The photo preview did not use the preset dimensions");
            Require(preview.CurrentFrame!.Settings.Width == preview.CurrentFrame.Settings.Height && model.Gaming.FrameWidth == 768 &&
                model.Gaming.FrameHeight == 512 && !model.Gaming.IsActive,"Ratio selection overwrote manual sizes or started capture");
            window.UpdateLayout();
            var sizeCombo=Descendants(window).OfType<ComboBox>().Single(control => ReferenceEquals(control.ItemsSource,model.Gaming.FrameSizePresets));
            Require(Descendants(sizeCombo).OfType<TextBlock>().Any(text => text.Text == "中 · 屏幕宽高的 30%") &&
                !Descendants(sizeCombo).OfType<TextBlock>().Any(text => text.Text.Contains("LensSizeOption")),
                "Rounded dropdown exposed an internal record instead of its display label");
            Require(Descendants(window).OfType<Slider>().Where(control => control.IsVisible).All(control => control.Maximum == LensZoom.Maximum || control.Maximum == 100),
                "Manual width/height controls stayed visible under a screen-relative preset");
            using (var restored=new GamingViewModel(gamingStore)) Require(restored.FrameSizePreset == LensFrameSizePreset.Medium &&
                restored.AspectRatio == LensAspectRatio.Square && restored.FrameWidth == 768 && restored.FrameHeight == 512,"Preset preferences did not survive restart");
            var legacyPath=Path.Combine(directory.FullName,"legacy-theme.json");
            File.WriteAllText(legacyPath,"{\"Theme\":{\"Accent\":\"#24548F\",\"AccentSoft\":\"#C4DAF1\"}}");
            var legacyTheme=new AppPreferencesStore(legacyPath).Load().Theme;
            Require(legacyTheme.ScrollThumb == legacyTheme.Accent && legacyTheme.SliderThumb == legacyTheme.Accent &&
                legacyTheme.ScrollTrack == legacyTheme.AccentSoft && legacyTheme.SliderTrack == legacyTheme.AccentSoft,
                "Older palettes did not inherit themed control colors");
            Image("size-preset");
            model.Gaming.FrameSizePreset=LensFrameSizePreset.Custom;
            Require(model.Gaming.FrameSizeText == "768 × 512 像素","Returning to manual size lost its previous independent dimensions");
            Console.WriteLine("PASS Screen-relative presets and aspect choices persist, update the photo preview, and preserve custom dimensions");

            model.Gaming.SetShortcut(new(6,0x87,"Ctrl + Shift + F24"));
            window.InitializeLensShortcuts(HwndSource.FromHwnd(new WindowInteropHelper(window).Handle));
            var nav=(ItemsControl)window.FindName("NavigationList");
            var shared=model.NavigationItems.Single(item => item.Id == "screen-lens"); var feature=Descendants(nav).OfType<FeatureSwitch>().Single(button => button.DataContext is NavigationItem { Id: "screen-lens" });
            Require(window.IsLensHotkeyRegistered,"An enabled tool did not register its hotkey");
            void RoundedButton(string phase)
            {
                var bitmap=Frame(); var dpi=VisualTreeHelper.GetDpi(window.PreviewContent);
                var origin=feature.TranslatePoint(new Point(),window.PreviewContent);
                var width=(int)Math.Round(feature.ActualWidth*dpi.DpiScaleX);
                var height=(int)Math.Round(feature.ActualHeight*dpi.DpiScaleY);
                var pixels=new byte[width*height*4];
                bitmap.CopyPixels(new Int32Rect((int)Math.Round(origin.X*dpi.DpiScaleX),(int)Math.Round(origin.Y*dpi.DpiScaleY),width,height),pixels,width*4,0);
                var background=((SolidColorBrush)window.FindResource("AccentSoftBrush")).Color;
                foreach (var (x,y) in new[] { (1,1),(width-2,1),(1,height-2),(width-2,height-2) })
                {
                    var at=(y*width+x)*4;
                    Require(Math.Abs(pixels[at]-background.B) <= 2 && Math.Abs(pixels[at+1]-background.G) <= 2 && Math.Abs(pixels[at+2]-background.R) <= 2,
                        $"The actual {phase} button painted a square corner at {x},{y}");
                }
                var top=(width+width/2)*4;
                Require(Math.Abs(pixels[top]-background.B)+Math.Abs(pixels[top+1]-background.G)+Math.Abs(pixels[top+2]-background.R) > 20,
                    $"The {phase} button lost its rounded surface");
            }
            var thumb=Descendants(feature).OfType<System.Windows.Shapes.Ellipse>().Single();
            var position=(TranslateTransform)thumb.RenderTransform;
            var capsuleTrack=Descendants(feature).OfType<Border>().Single(border => border.Background is SolidColorBrush { Color.A:255 });
            void SettleThumb() => feature.SeekThumbTransition(TimeSpan.FromMilliseconds(180/feature.AnimationSpeed));
            void SwitchAppearance(string phase)
            {
                SettleThumb(); RoundedButton(phase);
                var expected=feature.IsOn ? Color.FromRgb(38,131,84) : Color.FromRgb(196,65,77);
                Require(capsuleTrack.Background is SolidColorBrush brush && brush.Color == expected,
                    "The capsule track kept a mixed color or gradient");
                var frame=Frame(); var dpi=VisualTreeHelper.GetDpi(window.PreviewContent);
                var origin=feature.TranslatePoint(new Point(),window.PreviewContent);
                byte[] Pixel(double x,double y)
                {
                    var pixel=new byte[4]; frame.CopyPixels(new Int32Rect((int)Math.Round((origin.X+x)*dpi.DpiScaleX),
                        (int)Math.Round((origin.Y+y)*dpi.DpiScaleY),1,1),pixel,4,0); return pixel;
                }
                var background=Pixel(feature.IsOn ? 3 : 31,10);
                Require(Math.Abs(background[0]-expected.B)<=3 && Math.Abs(background[1]-expected.G)<=3 && Math.Abs(background[2]-expected.R)<=3,
                    $"The actual {phase} capsule retained a square or mixed surface");
                var knob=Pixel(feature.IsOn ? 24 : 10,10);
                Require(knob[0]>=245 && knob[1]>=245 && knob[2]>=245 &&
                    Math.Abs(position.X-(feature.IsOn ? 14 : 0))<.01,
                    $"The actual {phase} thumb did not reach the correct side");
            }
            SwitchAppearance("green"); Image("button-green");
            var focusVisual=new Control { Style=feature.FocusVisualStyle };
            focusVisual.Measure(new Size(34,20)); focusVisual.Arrange(new Rect(0,0,34,20)); focusVisual.ApplyTemplate();
            Require(Descendants(focusVisual).OfType<Border>().Single() is { CornerRadius.TopLeft:8,Background:null },
                "The switch retained a rectangular or filled focus visual");
            var row=Descendants(nav).OfType<RadioButton>().Single(button => button.DataContext is NavigationItem { Id: "screen-lens" });
            var label=Descendants(row).OfType<TextBlock>().Single(text => text.Text == "屏幕局部放大");
            Require(label.TranslatePoint(new Point(label.ActualWidth,0),feature).X<=-3,
                "The wider switch overlapped the navigation label");
            var rowFocus=new Control { Style=row.FocusVisualStyle };
            rowFocus.Measure(new Size(row.ActualWidth,row.ActualHeight));
            rowFocus.Arrange(new Rect(0,0,row.ActualWidth,row.ActualHeight)); rowFocus.ApplyTemplate();
            Require(Descendants(rowFocus).OfType<Border>().Single() is { CornerRadius.TopLeft:8,Background:null } &&
                !Descendants(rowFocus).OfType<System.Windows.Shapes.Rectangle>().Any(),
                "The navigation row retained the system dotted rectangular focus border");
            await Click(feature);
            Require(!model.Gaming.IsEnabled && !feature.IsOn && !window.IsLensHotkeyRegistered &&
                !model.Gaming.StartCommand.CanExecute(null) && !model.Gaming.ToggleCommand.CanExecute(null),"Disabling did not stop tool availability and hotkeys");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                feature.SeekThumbTransition(TimeSpan.FromMilliseconds(90));
                Require(position.X is >2 and <14,"The thumb jumped instead of sliding across the capsule");
                var intermediateColor=((SolidColorBrush)capsuleTrack.Background).Color;
                Require(intermediateColor.R is >38 and <196 && intermediateColor.G is >65 and <131,
                    "The track color jumped instead of blending while the thumb moved");
                Image("button-midpoint");
                var interrupted=position.X;
                feature.Command.Execute(feature.CommandParameter); feature.SeekThumbTransition(TimeSpan.Zero);
                Require(Math.Abs(position.X-interrupted)<.01,"Reversing a sliding thumb jumped to the opposite side");
                Require(((SolidColorBrush)capsuleTrack.Background).Color == intermediateColor,
                    "Reversing the switch jumped to a stale track color");
                SwitchAppearance("reversed green");
                feature.Command.Execute(feature.CommandParameter);
            }
            SwitchAppearance("red"); Image("button-red");
            for (var toggle=0;toggle<6;toggle++)
            {
                var before=position.X;
                var beforeColor=((SolidColorBrush)capsuleTrack.Background).Color;
                feature.Command.Execute(feature.CommandParameter); feature.SeekThumbTransition(TimeSpan.Zero);
                if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                    Require(Math.Abs(position.X-before)<.01,"Rapid toggles jumped to a stale thumb position");
                if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                    Require(((SolidColorBrush)capsuleTrack.Background).Color == beforeColor,"Rapid toggles lost the displayed color");
            }
            SwitchAppearance("rapid red");
            var toggleProvider=(IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(feature)!.GetPattern(PatternInterface.Toggle);
            toggleProvider.Toggle();
            Require(toggleProvider.ToggleState == System.Windows.Automation.ToggleState.On && model.Gaming.IsEnabled,
                "The accessibility toggle did not enable the tool");
            toggleProvider.Toggle();
            Require(toggleProvider.ToggleState == System.Windows.Automation.ToggleState.Off && !model.Gaming.IsEnabled,
                "The accessibility toggle did not disable the tool");
            var source=PresentationSource.FromVisual(feature);
            foreach (var (key,state) in new[] { (System.Windows.Input.Key.Right,true),(System.Windows.Input.Key.Right,true),
                (System.Windows.Input.Key.Left,false),(System.Windows.Input.Key.Left,false) })
            {
                feature.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,source!,0,key)
                    { RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent });
                Require(feature.IsOn == state,"Arrow keys toggled twice instead of selecting the requested switch state");
            }
            feature.SetCurrentValue(FeatureSwitch.AnimationsEnabledProperty,false);
            Require(position.X == 0,"Disabling animations left the thumb halfway across");
            feature.Command.Execute(feature.CommandParameter);
            Require(position.X == 14 && ((SolidColorBrush)capsuleTrack.Background).Color == Color.FromRgb(38,131,84),
                "Animation-off mode did not switch position and color immediately");
            feature.Command.Execute(feature.CommandParameter);
            feature.SetCurrentValue(FeatureSwitch.AnimationsEnabledProperty,true);
            SwitchAppearance("settled red");
            model.Gaming.Start(); Require(!model.Gaming.IsActive && !model.Gaming.HasCaptureResources,"A disabled tool allocated capture resources");
            using (var restored=new GamingViewModel(gamingStore)) Require(!restored.IsEnabled,"Master disable did not persist");
            await Click(feature);
            Require(window.IsLensHotkeyRegistered && model.Gaming.IsEnabled && !model.Gaming.IsActive,"Re-enabling did not restore only availability");
            model.Gaming.IsFavorite=true; model.OpenFavoritesCommand.Execute(null); window.UpdateLayout();
            Require(ReferenceEquals(shared,model.NavigationItems.Single(item => item.Id == "screen-lens")),"Favorites cloned the tool state");
            await Click(Descendants(nav).OfType<FeatureSwitch>().Single(button => button.DataContext is NavigationItem { Id: "screen-lens" }));
            model.ExitFavoritesCommand.Execute(null); window.UpdateLayout();
            Require(!Descendants(nav).OfType<FeatureSwitch>().Single(button => button.DataContext is NavigationItem { Id: "screen-lens" }).IsOn && !window.IsLensHotkeyRegistered && model.ShowScreenLens,
                "The favorite's master switch did not update the main tool");
            model.Gaming.IsEnabled=true;
            Console.WriteLine("PASS Sliding red/green capsule switches share favorites state, persist disable, unregister hotkeys and re-enable without starting capture");

            model.OpenSettingsCommand.Execute(null); window.UpdateLayout();
            Require(model.NavigationItems.Single(item => item.Id == "settings-computer").Name == "电脑详情" &&
                Descendants(nav).OfType<FeatureSwitch>().All(button => button.Visibility == Visibility.Collapsed),"Settings kept tool master switches or lost computer details");
            var tide=(TideTransition)window.FindName("NavigationTransition");
            void CircularWave(Color accent)
            {
                var previous=new DrawingVisual(); using (var draw=previous.RenderOpen()) draw.DrawRectangle(Brushes.Blue,null,new Rect(0,0,320,240));
                var snapshot=new RenderTargetBitmap(320,240,96,96,PixelFormats.Pbgra32); snapshot.Render(previous); snapshot.Freeze();
                var overlay=new TideTransition { Origin=new Point(80,70),Progress=.3,Snapshot=snapshot,WaveBrush=new SolidColorBrush(accent) };
                var surface=new Grid { Background=Brushes.Red }; surface.Children.Add(overlay);
                surface.Measure(new Size(320,240)); surface.Arrange(new Rect(0,0,320,240));
                var bitmap=new RenderTargetBitmap(320,240,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
                var pixels=new byte[320*240*4]; bitmap.CopyPixels(pixels,320*4,0);
                foreach (var (x,y) in new[] { (140,70),(80,130) })
                    Require(pixels[(y*320+x)*4+2] == 255 && pixels[(y*320+x)*4] == 0,"The circular wave failed to reveal equally distant inner points");
                foreach (var (x,y) in new[] { (185,70),(80,175),(154,144) })
                    Require(pixels[(y*320+x)*4] == 255 && pixels[(y*320+x)*4+2] == 0,"The wave revealed pixels outside its circular boundary");
                var horizontal=(70*320+163)*4; var vertical=(153*320+80)*4;
                Require(pixels.AsSpan(horizontal,4).SequenceEqual(pixels.AsSpan(vertical,4)) &&
                    pixels[horizontal+2] is >= 230 and < 255,"The wave edge was missing, too bright, or spread as an ellipse");
                overlay.Region=new GeometryGroup
                {
                    FillRule=FillRule.Nonzero,Children=new GeometryCollection
                    { new RectangleGeometry(new Rect(16,50,84,40)),new RectangleGeometry(new Rect(100,0,220,240)) }
                };
                surface.UpdateLayout();
                bitmap=new RenderTargetBitmap(320,240,96,96,PixelFormats.Pbgra32); bitmap.Render(surface); bitmap.CopyPixels(pixels,320*4,0);
                foreach (var (x,y) in new[] { (50,20),(50,180) })
                    Require(pixels[(y*320+x)*4+2] == 255 && pixels[(y*320+x)*4] == 0,"The scoped wave covered another sidebar option");
                Require(pixels[(180*320+280)*4] == 255,"The scoped wave stopped preserving the outgoing configuration page");
            }
            foreach (var preset in model.ThemePresets)
            {
                model.SelectThemePresetCommand.Execute(preset); window.UpdateLayout();
                var accent=(Color)ColorConverter.ConvertFromString(model.Theme.Accent);
                Require(((SolidColorBrush)tide.WaveBrush!).Color == accent,"The circular wave ignored the applied theme");
                CircularWave(accent);
            }
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                foreach (var width in new[] { 1040d,680d })
                foreach (var dark in new[] { false,true })
                {
                    window.Width=width;
                    if (dark) model.DarkThemeCommand.Execute(null); else model.LightThemeCommand.Execute(null);
                    model.Page="settings-theme"; await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ContextIdle);
                    var before=Frame();
                    var option=Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-runtime");
                    var body=(FrameworkElement)window.FindName("BodyContent");
                    var optionOrigin=option.TranslatePoint(new Point(18,9),body);
                    window.NavigateWithTide(option,optionOrigin);
                    window.SeekNavigationTransition(TimeSpan.Zero); SameFrame(before,"The wave exposed the new page in its first frame",tide.Region);
                    Require(tide.Origin == optionOrigin,"The circular wave did not start at the pointer's click position");
                    window.SeekNavigationTransition(TimeSpan.FromMilliseconds(25)); Image($"wave-start-{width}-{dark}");
                    var early=Frame(); var original=new byte[before.PixelWidth*before.PixelHeight*4]; var visible=new byte[original.Length];
                    before.CopyPixels(original,before.PixelWidth*4,0); early.CopyPixels(visible,early.PixelWidth*4,0);
                    var boundary=(int)Math.Ceiling(((FrameworkElement)window.FindName("SidebarSurface")).ActualWidth*VisualTreeHelper.GetDpi(body).DpiScaleX);
                    for (var y=0;y<early.PixelHeight;y++) for (var x=boundary;x<early.PixelWidth;x++)
                        for (var channel=0;channel<4;channel++)
                            Require(Math.Abs(original[(y*early.PixelWidth+x)*4+channel]-visible[(y*early.PixelWidth+x)*4+channel]) <= 3,
                                "The new page appeared beyond the early circular wave");
                    window.SeekNavigationTransition(TimeSpan.FromMilliseconds(110));
                    Require(tide.Progress is > .1 and < .9 && tide.Snapshot is { IsFrozen:true } && model.Page == "settings-runtime",
                        "The navigation wave did not advance while the target page was selected");
                    Image($"wave-{width}-{dark}");
                    var scoped=Frame(); var mask=RegionPixels(tide.Region!,scoped);
                    tide.Visibility=Visibility.Collapsed; var live=Frame(); tide.Visibility=Visibility.Visible;
                    var animatedPixels=new byte[scoped.PixelWidth*scoped.PixelHeight*4]; var livePixels=new byte[animatedPixels.Length];
                    scoped.CopyPixels(animatedPixels,scoped.PixelWidth*4,0); live.CopyPixels(livePixels,live.PixelWidth*4,0);
                    for (var at=0;at<mask.Length;at+=4) if (mask[at+3] == 0)
                        for (var channel=0;channel<4;channel++)
                            Require(Math.Abs(animatedPixels[at+channel]-livePixels[at+channel]) <= 3,
                                "The circular wave changed another option, the sidebar footer or the caption");
                    var interrupted=Frame();
                    Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-about")
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var keyboardOption=Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-about");
                    Require(tide.Origin == keyboardOption.TranslatePoint(new Point(keyboardOption.ActualWidth/2,keyboardOption.ActualHeight/2),body),
                        "Keyboard navigation reused an earlier mouse click position");
                    window.SeekNavigationTransition(TimeSpan.Zero); SameFrame(interrupted,"Rapid navigation discarded the visible partial wave",tide.Region);
                    window.SeekNavigationTransition(TimeSpan.FromMilliseconds(220)); await Task.Delay(35);
                    Require(tide.Snapshot is null && tide.Region is null && !tide.IsVisible && ((UIElement)window.FindName("MainContent")).IsHitTestVisible,
                        "Completed waves retained bitmaps or blocked content input");
                }
            }
            model.PageAnimationsEnabled=false; window.UpdateLayout();
            Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-theme")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(tide.Snapshot is null && model.ShowThemeSettings,"Disabled animation still allocated a wave bitmap");
            model.PageAnimationsEnabled=true;
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            foreach (var speed in new[] { .1,1d,2d })
            {
                model.AnimationSpeed=speed;
                model.Page="settings-theme"; window.UpdateLayout();
                Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-runtime")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.SeekNavigationTransition(TimeSpan.FromMilliseconds(110/speed));
                Require(Math.Abs(tide.Progress-.5) < .01,"The tide ignored the selected animation speed");
                window.SeekNavigationTransition(TimeSpan.FromMilliseconds(220/speed)); await Task.Delay(35);
                var settings=(Button)window.FindName("SettingsButton"); settings.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.SeekPageTransition(TimeSpan.FromMilliseconds(230/speed));
                Require(((Image)window.FindName("PageTransition")).IsVisible &&
                    Math.Abs(((Grid)window.FindName("PageTransitionCover")).Opacity-.5) < .01,"The footer animation ignored the selected speed");
                window.SeekPageTransition(TimeSpan.FromMilliseconds(460/speed)); await Task.Delay(35);
                feature=Descendants(nav).OfType<FeatureSwitch>().Single(button => button.DataContext is NavigationItem { Id: "screen-lens" });
                Require(feature.AnimationSpeed == speed,"The sliding switch ignored the selected animation speed");
                model.OpenSettingsCommand.Execute(null); window.UpdateLayout();
            }
            model.AnimationSpeed=1;
            Console.WriteLine("PASS Circular navigation starts at the click, follows all theme accents and preserves first/interrupted frames, speed scaling and snapshot cleanup");

            window.Width=1040; model.LightThemeCommand.Execute(null); model.Page="settings-theme";
            var details=(Expander)window.FindName("ThemeDetails"); details.IsExpanded=true;
            var speedSlider=(Slider)window.FindName("AnimationSpeedSlider");
            Require(speedSlider.Minimum == .1 && speedSlider.Maximum == 2 && speedSlider.TickFrequency == .1 && speedSlider.IsSnapToTickEnabled,
                "Animation speed did not expose the requested bounds and precise steps");
            await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ContextIdle);
            var editor=(Grid)window.FindName("ThemeEditorGrid"); var pane=(FrameworkElement)window.FindName("ThemePreviewPane");
            Require(Grid.GetColumn(details) == 1 && pane.TranslatePoint(new Point(pane.ActualWidth,0),editor).X <= details.TranslatePoint(new Point(),editor).X,
                "Detailed colors did not sit next to their preview");
            var unchanged=File.ReadAllText(Path.Combine(directory.FullName,"settings.json"));
            var colors=new[] { ("ScrollThumb","#CC3366"),("ScrollTrack","#CCDDEE"),("SliderThumb","#663399"),("SliderTrack","#EEDDCC") };
            foreach (var (key,color) in colors) model.ThemeColors.Single(option => option.Key == key).Value=color;
            Require(model.PreviewTheme.ScrollThumb == "#CC3366" && model.Theme.ScrollThumb != "#CC3366" &&
                ((SolidColorBrush)window.FindResource("ScrollThumbBrush")).Color != Color.FromRgb(204,51,102) &&
                File.ReadAllText(Path.Combine(directory.FullName,"settings.json")) == unchanged,"Control color drafts leaked into the global theme");
            Image("theme-editor");
            await Click((Button)window.FindName("SaveThemeButton"));
            foreach (var (key,color) in colors) Require(((SolidColorBrush)window.FindResource(key+"Brush")).Color == (Color)ColorConverter.ConvertFromString(color),
                "Saving did not apply a control color resource");
            Require(appStore.Load().Theme == model.Theme,"Saved control colors did not persist");
            var bars=Descendants(details).OfType<ScrollBar>().Where(bar => bar.IsVisible).ToArray();
            Require(bars.Length > 0 && bars.All(bar => ((SolidColorBrush)bar.Background).Color == Color.FromRgb(204,221,238)),"The real scrolling track ignored its custom color");
            var bar=bars.First(); bar.ApplyTemplate(); var scrollTrack=(Track)bar.Template.FindName("PART_Track",bar);
            var offset=bar.Value; scrollTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(0,10));
            await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ContextIdle);
            Require(bar.Value > offset,"The themed scroll bar stopped responding to drag input");
            model.Page="screen-lens"; window.UpdateLayout();
            var slider=Descendants(window).OfType<Slider>().First(control => control.IsVisible);
            slider.ApplyTemplate(); var track=(Track)slider.Template.FindName("PART_Track",slider);
            Require(((SolidColorBrush)track.Thumb.Background).Color == Color.FromRgb(102,51,153),"The actual slider thumb ignored its custom color");
            var value=slider.Value; track.Thumb.RaiseEvent(new DragDeltaEventArgs(10,0));
            Require(slider.Value > value,"The themed slider no longer responded to dragging");
            var combo=Descendants(window).OfType<ComboBox>().Single(control => ReferenceEquals(control.ItemsSource,model.Gaming.FrameSizePresets));
            ((IExpandCollapseProvider)new ComboBoxAutomationPeer(combo).GetPattern(PatternInterface.ExpandCollapse)).Expand();
            window.UpdateLayout();
            Require(combo.IsDropDownOpen && combo.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem,
                "The rounded size dropdown did not open its options");
            ((ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1)).IsSelected=true; combo.IsDropDownOpen=false;
            Require(model.Gaming.FrameSizePreset == LensFrameSizePreset.Small,"The rounded dropdown did not apply its selected preset");
            model.Page="settings-theme"; window.Width=680; await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ContextIdle);
            Require(Grid.GetColumn(details) == 0 && Grid.GetRow(details) == 1,"Narrow color editing clipped the detailed controls");
            Image("theme-editor-narrow");
            Require(System.Windows.Shell.WindowChrome.GetWindowChrome(window).CornerRadius.TopLeft == 12,"Normal window corners stayed square");
            Console.WriteLine("PASS Custom scroll/slider colors stay in preview until Save; real controls respond and color editing adapts beside/below the preview");

            model.Page="settings-computer";
            await probe.DetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await model.PendingWork;
            Require(window.IsLocalPolling && model.IsHome && ((UIElement)window.FindName("HomeScroll")).IsVisible &&
                !((UIElement)window.FindName("SettingsScroll")).IsVisible,"Computer details did not enable its own local polling");
            window.TrackActivity=true;
            Require(!window.IsActive && !window.IsLocalPolling,"An inactive background window kept computer polling");
            window.TrackActivity=false;
            window.Hide(); await Task.Delay(2300); await preview.PendingResourceRelease;
            Require(!window.IsLocalPolling && !preview.IsRenderingScheduled && !preview.HasGpuResources && window.IsLensHotkeyRegistered &&
                !model.Gaming.HasCaptureResources,"Idle hidden UI retained polling/render resources or lost its hotkey");
            window.Show(); model.Page="screen-lens"; window.UpdateLayout();
            Require(!window.IsLocalPolling && preview.CurrentFrame is not null,"Returning to tools lost the immediate cached preview or restarted computer polling");
            await WaitFor(() => preview.HasGpuResources,"The preview did not recreate its GPU resources when visible");
            Require(!model.Gaming.IsActive && window.IsLensHotkeyRegistered,"Returning from idle auto-started capture or lost hotkeys");
            Console.WriteLine("PASS Inactive/hidden/minimized UI suspends computer polling and releases idle preview GPU resources; cached redisplay and hotkeys remain ready");
        }
        finally
        {
            window.Close(); await model.PendingWork; await previewRelease();
            directory.Delete(recursive:true);
        }
        async Task previewRelease() => await ((LensPreview)window.FindName("LensPhotoPreview")).PendingResourceRelease;
    }
}

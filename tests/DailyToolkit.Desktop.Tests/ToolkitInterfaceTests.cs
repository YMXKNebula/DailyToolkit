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
            navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),startupRegistration:new StartupFake());
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
        void SameFrame(BitmapSource before,string message)
        {
            var after=Frame(); var a=new byte[before.PixelWidth*before.PixelHeight*4]; var b=new byte[a.Length];
            before.CopyPixels(a,before.PixelWidth*4,0); after.CopyPixels(b,after.PixelWidth*4,0);
            var different=0; for (var i=0;i<a.Length;i+=4)
                if (Math.Abs(a[i]-b[i])>3 || Math.Abs(a[i+1]-b[i+1])>3 || Math.Abs(a[i+2]-b[i+2])>3 || Math.Abs(a[i+3]-b[i+3])>3) different++;
            Require(different == 0,$"{message}: {different} pixels");
        }
        try
        {
            Require(model.ShowScreenLens && model.NavigationItems.Single().CanToggle && !probe.DetailsStarted.Task.IsCompleted,
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
            Require(Descendants(window).OfType<Slider>().Where(control => control.IsVisible).All(control => control.Maximum == 10),
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
            var shared=model.NavigationItems.Single(); var feature=Descendants(nav).OfType<FeatureSwitch>().Single();
            Require(window.IsLensHotkeyRegistered,"An enabled tool did not register its hotkey");
            byte[] ButtonColors()
            {
                var visual=new DrawingVisual(); using (var draw=visual.RenderOpen()) draw.DrawRectangle(feature.Background,null,new Rect(0,0,28,28));
                var bitmap=new RenderTargetBitmap(28,28,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
                var pixels=new byte[28*28*4]; bitmap.CopyPixels(pixels,28*4,0); return pixels;
            }
            await Click(feature); feature.SeekColorTransition(TimeSpan.FromMilliseconds(20));
            Require(!model.Gaming.IsEnabled && !feature.IsOn && !window.IsLensHotkeyRegistered &&
                !model.Gaming.StartCommand.CanExecute(null) && !model.Gaming.ToggleCommand.CanExecute(null),"Disabling did not stop tool availability and hotkeys");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                var ripple=ButtonColors(); var center=(13*28+13)*4; var edge=(2*28+13)*4;
                Require(ripple[center+2] > 60 && ripple[center+2] < 196 && ripple[edge+2] == 38,
                    "The button did not spread a gradient from its center while preserving the old edge color");
                foreach (var (x,y) in new[] { (8,13),(13,8),(5,13),(13,5),(2,13),(13,2) })
                    for (var channel=0;channel<4;channel++)
                        Require(Math.Abs(ripple[(y*28+x)*4+channel]-ripple[((27-y)*28+27-x)*4+channel]) <= 1,
                            "The red/green ripple layers were offset from the button center");
                Image("button-ripple");
                feature.Command.Execute(feature.CommandParameter); feature.SeekColorTransition(TimeSpan.Zero);
                Require(ButtonColors().SequenceEqual(ripple),"Reversing a button ripple jumped to another color or shifted its origin");
                feature.Command.Execute(feature.CommandParameter); feature.SeekColorTransition(TimeSpan.FromMilliseconds(100));
            }
            model.Gaming.Start(); Require(!model.Gaming.IsActive && !model.Gaming.HasCaptureResources,"A disabled tool allocated capture resources");
            await Task.Delay(150); Require(((SolidColorBrush)feature.Background).Color == Color.FromRgb(196,65,77),"The disable color did not finish red");
            using (var restored=new GamingViewModel(gamingStore)) Require(!restored.IsEnabled,"Master disable did not persist");
            await Click(feature);
            Require(window.IsLensHotkeyRegistered && model.Gaming.IsEnabled && !model.Gaming.IsActive,"Re-enabling did not restore only availability");
            model.Gaming.IsFavorite=true; model.OpenFavoritesCommand.Execute(null); window.UpdateLayout();
            Require(ReferenceEquals(shared,model.NavigationItems.Single()),"Favorites cloned the tool state");
            await Click(Descendants(nav).OfType<FeatureSwitch>().Single());
            model.ExitFavoritesCommand.Execute(null); window.UpdateLayout();
            Require(!Descendants(nav).OfType<FeatureSwitch>().Single().IsOn && !window.IsLensHotkeyRegistered && model.ShowScreenLens,
                "The favorite's master switch did not update the main tool");
            model.Gaming.IsEnabled=true;
            Console.WriteLine("PASS Animated master switches share favorites state, persist disable, unregister hotkeys and re-enable without starting capture");

            model.OpenSettingsCommand.Execute(null); window.UpdateLayout();
            Require(model.NavigationItems.Single(item => item.Id == "settings-computer").Name == "电脑详情" &&
                Descendants(nav).OfType<FeatureSwitch>().All(button => button.Visibility == Visibility.Collapsed),"Settings kept tool master switches or lost computer details");
            var tide=(TideTransition)window.FindName("NavigationTransition");
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
                    var optionOrigin=option.TranslatePoint(new Point(0,option.ActualHeight/2),body);
                    option.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.SeekNavigationTransition(TimeSpan.Zero); SameFrame(before,"The wave exposed the new page in its first frame");
                    Require(tide.Origin == optionOrigin,"The wave did not start at the clicked option's leftmost edge");
                    window.SeekNavigationTransition(TimeSpan.FromMilliseconds(70));
                    Require(tide.Progress is > .1 and < .9 && tide.Snapshot is { IsFrozen:true } && model.Page == "settings-runtime",
                        "The navigation wave did not advance while the target page was selected");
                    Image($"wave-{width}-{dark}");
                    var interrupted=Frame();
                    Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-about")
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.SeekNavigationTransition(TimeSpan.Zero); SameFrame(interrupted,"Rapid navigation discarded the visible partial wave");
                    window.SeekNavigationTransition(TimeSpan.FromMilliseconds(140)); await Task.Delay(35);
                    Require(tide.Snapshot is null && !tide.IsVisible && ((UIElement)window.FindName("MainContent")).IsHitTestVisible,
                        "Completed waves retained bitmaps or blocked content input");
                }
            }
            model.PageAnimationsEnabled=false; window.UpdateLayout();
            Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-theme")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(tide.Snapshot is null && model.ShowThemeSettings,"Disabled animation still allocated a wave bitmap");
            model.PageAnimationsEnabled=true;
            foreach (var speed in new[] { .1,1d,2d })
            {
                model.AnimationSpeed=speed;
                model.Page="settings-theme"; window.UpdateLayout();
                Descendants(nav).OfType<RadioButton>().Single(button => ((NavigationItem)button.DataContext).Id == "settings-runtime")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.SeekNavigationTransition(TimeSpan.FromMilliseconds(70/speed));
                Require(Math.Abs(tide.Progress-Math.Sin(Math.PI/4)) < .01,"The tide ignored the selected animation speed");
                window.SeekNavigationTransition(TimeSpan.FromMilliseconds(140/speed)); await Task.Delay(35);
                var settings=(Button)window.FindName("SettingsButton"); settings.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.SeekPageTransition(TimeSpan.FromMilliseconds(230/speed));
                Require(((Image)window.FindName("PageTransition")).IsVisible &&
                    Math.Abs(((Grid)window.FindName("PageTransitionCover")).Opacity-.5) < .01,"The footer animation ignored the selected speed");
                window.SeekPageTransition(TimeSpan.FromMilliseconds(460/speed)); await Task.Delay(35);
                feature=Descendants(nav).OfType<FeatureSwitch>().Single();
                Require(feature.AnimationSpeed == speed,"The master button ignored the selected speed");
                model.OpenSettingsCommand.Execute(null); window.UpdateLayout();
            }
            model.AnimationSpeed=1;
            Console.WriteLine("PASS Quick tide navigation preserves first/interrupted frames in light/dark, narrow/full layouts and releases its snapshot");

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

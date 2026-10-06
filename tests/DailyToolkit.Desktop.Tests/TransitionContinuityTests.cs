using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckTransitionContinuityAsync()
    {
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-transition-tests-");
        try
        {
            foreach (var (width,dark) in new[] { (680d,false),(1040d,false),(680d,true),(1040d,true) })
            {
                var display=new DisplayInfo(1920,1080,1,1920,1040,false);
                var probe=new ControlledProbe(display); probe.Finish.TrySetResult();
                var model=new MainViewModel(probe,display,new LocalProbe(),
                    gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),
                    favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
                    navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),
                    appPreferencesStore:new AppPreferencesStore(Path.Combine(directory.FullName,"settings.json")),
                    startupRegistration:new StartupFake());
                if (dark) model.DarkThemeCommand.Execute(null); else model.LightThemeCommand.Execute(null);
                model.Page="home";
                var window=new MainWindow(model,enableShortcuts:false) { Width=width,Height=650,
                    ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
                try
                {
                    window.Show(); window.UpdateLayout();
                    var root=window.PreviewContent; var body=(FrameworkElement)window.FindName("BodyContent");
                    TextOptions.SetTextRenderingMode(root,TextRenderingMode.Grayscale);
                    var favorites=(Button)window.FindName("FavoritesButton"); var settings=(Button)window.FindName("SettingsButton");
                    var overlay=(Image)window.FindName("PageTransition");
                    var dpi=VisualTreeHelper.GetDpi(root);
                    var origin=body.TranslatePoint(new Point(),root);
                    var pixelWidth=(int)Math.Ceiling(body.ActualWidth*dpi.DpiScaleX);
                    var pixelHeight=(int)Math.Ceiling(body.ActualHeight*dpi.DpiScaleY);
                    byte[] DisplayedBody()
                    {
                        window.UpdateLayout();
                        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),
                            (int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var pixels=new byte[pixelWidth*pixelHeight*4];
                        bitmap.CopyPixels(new Int32Rect((int)Math.Round(origin.X*dpi.DpiScaleX),(int)Math.Round(origin.Y*dpi.DpiScaleY),
                            pixelWidth,pixelHeight),pixels,pixelWidth*4,0);
                        return pixels;
                    }
                    void RequireSameFrame(byte[] before,string operation)
                    {
                        var after=DisplayedBody(); var different=0;
                        for (var i=0;i<before.Length;i+=4)
                            if (Math.Abs(before[i]-after[i])>3 || Math.Abs(before[i+1]-after[i+1])>3 ||
                                Math.Abs(before[i+2]-after[i+2])>3 || Math.Abs(before[i+3]-after[i+3])>3) different++;
                        Require(different == 0,$"{operation} exposed or shifted another page in the first frame: {different} pixels, width={width}, dark={dark}, body={body.ActualWidth}x{body.ActualHeight}, image={overlay.ActualWidth}x{overlay.ActualHeight}, dpi={dpi.DpiScaleX}");
                        Require(overlay.Source is BitmapSource { IsFrozen:true } source,"The transition did not retain an immutable outgoing frame");
                        var captured=new byte[pixelWidth*pixelHeight*4];
                        ((BitmapSource)overlay.Source!).CopyPixels(captured,pixelWidth*4,0);
                        for (var i=3;i<captured.Length;i+=4)
                            Require(captured[i] == 255,"Transparent outgoing pixels exposed the destination through the snapshot");
                    }
                    void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    async Task Complete()
                    {
                        window.SeekPageTransition(TimeSpan.FromMilliseconds(460));
                        await Task.Delay(20);
                        await Application.Current.Dispatcher.InvokeAsync(() => { },DispatcherPriority.ContextIdle);
                        Require(overlay.Source is null,"The completed transition retained its outgoing frame");
                    }
                    foreach (var button in new[] { settings,favorites })
                    {
                        var before=DisplayedBody(); Click(button); window.SeekPageTransition(TimeSpan.Zero);
                        RequireSameFrame(before,"Opening");
                        // The top of the opposite corner is outside the growing mask at this point.
                        window.SeekPageTransition(TimeSpan.FromMilliseconds(92));
                        var inMotion=DisplayedBody();
                        var sample=(20*pixelWidth+pixelWidth-20)*4;
                        Require(before.AsSpan(sample,4).SequenceEqual(inMotion.AsSpan(sample,4)),
                            "The destination appeared outside the opening shape");
                        await Complete();
                        before=DisplayedBody(); Click(button); window.SeekPageTransition(TimeSpan.Zero);
                        RequireSameFrame(before,"Closing"); await Complete();
                    }
                    Click(settings); window.SeekPageTransition(TimeSpan.FromMilliseconds(140));
                    var interrupted=DisplayedBody(); Click(settings); window.SeekPageTransition(TimeSpan.Zero);
                    RequireSameFrame(interrupted,"Reversing an unfinished transition");
                    window.SeekPageTransition(TimeSpan.FromMilliseconds(180));
                    interrupted=DisplayedBody(); Click(favorites); window.SeekPageTransition(TimeSpan.Zero);
                    RequireSameFrame(interrupted,"Switching icons during a transition"); await Complete();
                }
                finally { window.Close(); await model.PendingWork; }
            }
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-transition-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
        Console.WriteLine("PASS Opening, closing and interrupted transitions preserve the complete outgoing frame without transparent gaps or shifted content, in light/dark and narrow/full layouts");
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DailyToolkit.Desktop.Gaming;

// Developer-only, reproducible fixtures. Never reads the user's desktop or files.
internal static class LensTestScenes
{
    public static IReadOnlyDictionary<string,BitmapSource> Create()
    {
        var result=new Dictionary<string,BitmapSource>();
        foreach(var name in new[] {"SmallText","WhiteOnBlack","BlackOnWhite","GameHud","HighContrastUI","HorizontalLines",
            "VerticalLines","Diagonals","Gradients","Photo","GameTexture","Icons","PixelArt","Noise","LowResolution"})
        {
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                var dark=name is "WhiteOnBlack" or "GameHud";
                dc.DrawRectangle(dark ? Brushes.Black : Brushes.White,null,new(0,0,128,96));
                var ink=dark ? Brushes.White : Brushes.Black;
                if(name is "SmallText" or "WhiteOnBlack" or "BlackOnWhite" or "GameHud" or "HighContrastUI")
                {
                    for(var row=0;row<7;row++) dc.DrawText(new FormattedText(row%2 == 0 ? "工具 Aa 1Il 012345" : "HP 125 / 200 | 60Hz",
                        CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),row%2 == 0 ? 10 : 12,ink,1),new(1,row*14));
                    if(name is "GameHud" or "HighContrastUI") dc.DrawRectangle(Brushes.LimeGreen,null,new(34,46,52,3));
                }
                else if(name == "Photo")
                {
                    using var stream=typeof(LensTestScenes).Assembly.GetManifestResourceStream("DailyToolkit.Desktop.Assets.lens-preview.jpg")!;
                    var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                    dc.DrawImage(image,new(0,0,128,96));
                }
                else if(name is "HorizontalLines" or "VerticalLines" or "Diagonals")
                {
                    var pen=new Pen(Brushes.Black,1);
                    for(var i=-128;i<256;i+=6)
                    {
                        if(name == "HorizontalLines") dc.DrawLine(pen,new(0,i+0.5),new(128,i+0.5));
                        else if(name == "VerticalLines") dc.DrawLine(pen,new(i+0.5,0),new(i+0.5,96));
                        else dc.DrawLine(pen,new(i,0),new(i+72,96));
                    }
                }
                else if(name is "Gradients" or "GameTexture" or "Noise")
                {
                    var random=new Random(1069);
                    for(var y=0;y<96;y++) for(var x=0;x<128;x++)
                    {
                        var value=name == "Noise" ? 128+random.Next(-12,13) : name == "GameTexture" ?
                            (int)(125+55*Math.Sin(x*0.7)*Math.Cos(y*0.3)) : x*2;
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)value,(byte)value,(byte)value)),null,new(x,y,1,1));
                    }
                }
                else
                {
                    var cell=name == "LowResolution" ? 8 : 3;
                    for(var y=0;y<96;y+=cell) for(var x=0;x<128;x+=cell)
                        dc.DrawRectangle((x/cell+y/cell)%3 == 0 ? Brushes.OrangeRed : (x/cell+y/cell)%3 == 1 ? Brushes.DodgerBlue : Brushes.Black,
                            null,new(x,y,cell,cell));
                    if(name == "Icons") { dc.DrawEllipse(Brushes.White,new Pen(Brushes.Black,2),new(64,48),12,12); dc.DrawLine(new Pen(Brushes.Black,2),new(58,48),new(70,48)); }
                }
            }
            var bitmap=new RenderTargetBitmap(128,96,96,96,PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); result.Add(name,bitmap);
        }
        return result;
    }
    public static void Save(BitmapSource bitmap,string path)
    {
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=System.IO.File.Create(path); encoder.Save(file);
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DailyToolkit.Desktop.Presentation;
namespace DailyToolkit.Desktop.Controls;
public sealed class ThemePreview : FrameworkElement
{
    public static readonly DependencyProperty PaletteProperty=DependencyProperty.Register(nameof(Palette),typeof(ThemePalette),typeof(ThemePreview),
        new FrameworkPropertyMetadata(new ThemePalette(),FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnimationColorProperty=DependencyProperty.Register(nameof(AnimationColor),typeof(string),typeof(ThemePreview),
        new FrameworkPropertyMetadata("#267A5D",FrameworkPropertyMetadataOptions.AffectsRender));
    public ThemePalette Palette { get => (ThemePalette)GetValue(PaletteProperty); set => SetValue(PaletteProperty,value); }
    public string AnimationColor { get => (string)GetValue(AnimationColorProperty); set => SetValue(AnimationColorProperty,value); }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw); var p=Palette;
        Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        void Box(double x,double y,double w,double h,string color,double radius=0) => draw.DrawRoundedRectangle(B(color),null,new(x,y,w,h),radius,radius);
        void Text(string text,double x,double y,string color,double size=12) => draw.DrawText(new FormattedText(text,
            CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,B(color),VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        draw.PushTransform(new ScaleTransform(ActualWidth/480,ActualHeight/240));
        Box(0,0,480,240,p.Window); Box(0,0,480,25,p.Surface); Box(0,25,112,215,p.Sidebar);
        draw.DrawLine(new Pen(B(p.Border),1),new(112,25),new(112,240));
        Text("DailyToolkit",12,5,p.Muted,10); Text("—   □   ×",419,5,p.Muted,10);
        Box(12,41,88,28,p.Hover,5); Text("电脑",24,50,p.Muted);
        Box(12,75,88,31,p.AccentSoft,5); Text("屏幕局部放大",19,83,p.Accent,10);
        Text("屏幕局部放大",130,43,p.Text,18);
        Box(130,95,330,110,p.Border,8); Box(131,96,328,108,p.Surface,8);
        Text("快捷键   Ctrl + Shift + F8",145,111,p.Text); Box(360,107,84,27,p.Accent,5); Text("开启放大框",373,114,p.AccentForeground,10);
        Text("放大倍率",145,151,p.Muted,10); Box(214,157,194,3,p.AccentSoft); Box(214,157,72,3,p.Accent);
        Box(130,212,330,19,p.Warning,3); Text("提示信息",140,214,p.WarningText,10);
        foreach (var (gear,x) in new[] { (false,34d),(true,78d) })
        {
            var transform=new TransformGroup(); transform.Children.Add(new ScaleTransform(.105,.105)); transform.Children.Add(new TranslateTransform(x,211));
            var icon=PageTransitionGeometry.CreateIcon(gear); icon.Transform=transform;
            draw.DrawGeometry(B(AnimationColor),null,icon);
        }
        draw.Pop();
    }
}

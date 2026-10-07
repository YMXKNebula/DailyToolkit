using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DailyToolkit.Desktop.Controls;

public sealed class FeatureSwitch : Button
{
    private readonly SolidColorBrush _stateBrush=new(Color.FromRgb(38,131,84));
    public static readonly DependencyProperty IsOnProperty=DependencyProperty.Register(nameof(IsOn),typeof(bool),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(true,(owner,_) => ((FeatureSwitch)owner).UpdateColor()));
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty,value); }
    public FeatureSwitch()
    {
        Width=28; Height=28; Padding=new Thickness(0); BorderThickness=new Thickness(0);
        SetResourceReference(StyleProperty,typeof(Button));
        Background=_stateBrush; Foreground=Brushes.White;
        Content=new TextBlock { Text="\uE7E8",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=13 };
        Loaded += (_,_) => UpdateColor(animate:false);
        Unloaded += (_,_) => UpdateColor(animate:false);
    }
    private void UpdateColor(bool animate=true)
    {
        var target=IsOn ? Color.FromRgb(38,131,84) : Color.FromRgb(196,65,77);
        var current=_stateBrush.Color;
        _stateBrush.BeginAnimation(SolidColorBrush.ColorProperty,null); _stateBrush.Color=target;
        if (animate && IsLoaded && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            _stateBrush.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(current,target,TimeSpan.FromMilliseconds(130))
                { FillBehavior=FillBehavior.Stop,EasingFunction=new SineEase { EasingMode=EasingMode.EaseOut } });
    }
}

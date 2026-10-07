using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Controls;

public sealed class FeatureSwitch : Button
{
    private static readonly Rect BrushBounds=new(0,0,1,1);
    private Drawing? _previous;
    private DrawingGroup? _ripple;
    private AnimationClock? _clock;
    private Color _target;
    private int _generation;
    public static readonly DependencyProperty IsOnProperty=DependencyProperty.Register(nameof(IsOn),typeof(bool),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(true,(owner,_) => ((FeatureSwitch)owner).UpdateColor()));
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty,value); }
    public static readonly DependencyProperty AnimationSpeedProperty=DependencyProperty.Register(nameof(AnimationSpeed),typeof(double),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(1d,null,(_,value) => AppPreferences.NormalizeAnimationSpeed((double)value)));
    public double AnimationSpeed { get => (double)GetValue(AnimationSpeedProperty); set => SetValue(AnimationSpeedProperty,value); }
    public static readonly DependencyProperty AnimationsEnabledProperty=DependencyProperty.Register(nameof(AnimationsEnabled),typeof(bool),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(true,(owner,e) => { if (!(bool)e.NewValue) ((FeatureSwitch)owner).UpdateColor(animate:false); }));
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty,value); }
    private static readonly DependencyProperty RippleProgressProperty=DependencyProperty.Register(nameof(RippleProgress),typeof(double),typeof(FeatureSwitch),
        new PropertyMetadata(0d,(owner,_) => ((FeatureSwitch)owner).DrawRipple()));
    private double RippleProgress { get => (double)GetValue(RippleProgressProperty); set => SetValue(RippleProgressProperty,value); }
    public FeatureSwitch()
    {
        Width=28; Height=28; Padding=new Thickness(0); BorderThickness=new Thickness(0);
        SetResourceReference(StyleProperty,typeof(Button));
        Background=new SolidColorBrush(Color.FromRgb(38,131,84)); Foreground=Brushes.White;
        Content=new TextBlock { Text="\uE7E8",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=13 };
        Loaded += (_,_) => UpdateColor(animate:false);
        Unloaded += (_,_) => UpdateColor(animate:false);
    }
    private void UpdateColor(bool animate=true)
    {
        var target=IsOn ? Color.FromRgb(38,131,84) : Color.FromRgb(196,65,77);
        // Preserve the displayed composite when toggled again during a ripple.
        var previous=Background is DrawingBrush drawing ? drawing.Drawing.CloneCurrentValue() :
            new GeometryDrawing(Background.CloneCurrentValue(),null,new RectangleGeometry(BrushBounds));
        previous.Freeze(); StopRipple();
        _target=target;
        if (!animate || !AnimationsEnabled || !IsLoaded || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
        { Background=new SolidColorBrush(target); return; }
        _previous=previous; _ripple=new DrawingGroup();
        Background=new DrawingBrush(_ripple) { ViewboxUnits=BrushMappingMode.Absolute,Viewbox=BrushBounds,Stretch=Stretch.Fill };
        RippleProgress=0; DrawRipple();
        var generation=_generation;
        _clock=(AnimationClock)new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(100/AnimationSpeed))
            { EasingFunction=new SineEase { EasingMode=EasingMode.EaseOut } }.CreateClock(true);
        _clock.Completed += (_,_) =>
        {
            if (generation != _generation) return;
            StopRipple(); Background=new SolidColorBrush(target);
        };
        ApplyAnimationClock(RippleProgressProperty,_clock);
    }
    private void DrawRipple()
    {
        if (_ripple is null || _previous is null) return;
        var progress=Math.Clamp(RippleProgress,0,1);
        using var draw=_ripple.Open(); draw.DrawDrawing(_previous);
        if (progress <= 0) return;
        // Both layers fill one unit square. The existing button border clips
        // them to the same rounded outline, with a single centered origin.
        var color=_target with { A=(byte)Math.Round(255*Math.Min(1,progress*2)) };
        var brush=new RadialGradientBrush
        {
            Center=new(.5,.5),GradientOrigin=new(.5,.5),RadiusX=progress,RadiusY=progress,
            GradientStops=new GradientStopCollection { new(color,0),new(color,.72),new(_target with { A=0 },1) }
        };
        brush.Freeze(); draw.DrawRectangle(brush,null,BrushBounds);
    }
    private void StopRipple()
    {
        ++_generation; _clock?.Controller?.Stop();
        ApplyAnimationClock(RippleProgressProperty,null); _clock=null; _ripple=null; _previous=null;
    }
    internal void SeekColorTransition(TimeSpan offset) => _clock?.Controller?.SeekAlignedToLastTick(offset,TimeSeekOrigin.BeginTime);
}

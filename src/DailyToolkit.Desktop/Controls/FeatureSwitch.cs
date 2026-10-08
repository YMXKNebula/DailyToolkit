using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Controls;

public sealed class FeatureSwitch : Button
{
    private const double ThumbTravel=14;
    private static readonly Color EnabledColor=Color.FromRgb(38,131,84);
    private static readonly Color DisabledColor=Color.FromRgb(196,65,77);
    private readonly SolidColorBrush _trackColor=new(EnabledColor);
    private readonly Border _track=new() { CornerRadius=new CornerRadius(10) };
    private readonly TranslateTransform _thumbPosition=new();
    private ClockGroup? _motion;
    private int _generation;
    private double? _pressX;
    private double _pressPosition;
    private bool _dragging;
    private bool _suppressClick;
    public static readonly DependencyProperty IsOnProperty=DependencyProperty.Register(nameof(IsOn),typeof(bool),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(true,(owner,e) =>
        {
            var control=(FeatureSwitch)owner; control.UpdateAppearance();
            if (UIElementAutomationPeer.FromElement(control) is FeatureSwitchPeer peer)
                peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                    (bool)e.OldValue ? ToggleState.On : ToggleState.Off,(bool)e.NewValue ? ToggleState.On : ToggleState.Off);
        }));
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty,value); }
    public static readonly DependencyProperty AnimationSpeedProperty=DependencyProperty.Register(nameof(AnimationSpeed),typeof(double),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(1d,null,(_,value) => AppPreferences.NormalizeAnimationSpeed((double)value)));
    public double AnimationSpeed { get => (double)GetValue(AnimationSpeedProperty); set => SetValue(AnimationSpeedProperty,value); }
    public static readonly DependencyProperty AnimationsEnabledProperty=DependencyProperty.Register(nameof(AnimationsEnabled),typeof(bool),typeof(FeatureSwitch),
        new FrameworkPropertyMetadata(true,(owner,e) => { if (!(bool)e.NewValue) ((FeatureSwitch)owner).UpdateAppearance(animate:false); }));
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty,value); }
    public FeatureSwitch()
    {
        Width=34; Height=20; Padding=new Thickness(0); BorderThickness=new Thickness(0);
        SetResourceReference(StyleProperty,"FeatureSwitchStyle");
        Background=Brushes.Transparent;
        var surface=new Grid { Width=34,Height=20,IsHitTestVisible=false };
        _track.Background=_trackColor;
        surface.Children.Add(_track);
        surface.Children.Add(new Ellipse
        {
            Width=16,Height=16,Fill=Brushes.White,HorizontalAlignment=HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,0,0),RenderTransform=_thumbPosition
        });
        Content=surface;
        Loaded += (_,_) => UpdateAppearance(animate:false);
        Unloaded += (_,_) => { _pressX=null; _dragging=false; UpdateAppearance(animate:false); };
        IsEnabledChanged += (_,_) => { if (!IsEnabled) { _pressX=null; _dragging=false; UpdateAppearance(animate:false); } };
        UpdateAppearance(animate:false);
    }

    private void UpdateAppearance(bool animate=true)
    {
        var current=StopMotion(); var target=IsOn ? ThumbTravel : 0;
        var targetColor=IsOn ? EnabledColor : DisabledColor;
        if (!animate || !AnimationsEnabled || !IsLoaded || !IsEnabled || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast ||
            (Math.Abs(current.Position-target)<.01 && current.Color == targetColor))
        { _thumbPosition.X=target; _trackColor.Color=targetColor; return; }
        var generation=_generation;
        var duration=TimeSpan.FromMilliseconds(180/AnimationSpeed);
        var easing=new SineEase { EasingMode=EasingMode.EaseInOut }; easing.Freeze();
        var timeline=new ParallelTimeline { Duration=new Duration(duration) };
        timeline.Children.Add(new DoubleAnimation(current.Position,target,duration) { EasingFunction=easing });
        timeline.Children.Add(new ColorAnimation(current.Color,targetColor,duration) { EasingFunction=easing });
        _motion=(ClockGroup)timeline.CreateClock(true);
        _motion.Completed += (_,_) =>
        { if (generation == _generation) { StopMotion(); _thumbPosition.X=target; _trackColor.Color=targetColor; } };
        _thumbPosition.ApplyAnimationClock(TranslateTransform.XProperty,(AnimationClock)_motion.Children[0]);
        _trackColor.ApplyAnimationClock(SolidColorBrush.ColorProperty,(AnimationClock)_motion.Children[1]);
    }

    private (double Position,Color Color) StopMotion()
    {
        var current=(_thumbPosition.X,_trackColor.Color);
        ++_generation; _motion?.Controller?.Stop();
        _thumbPosition.ApplyAnimationClock(TranslateTransform.XProperty,null); _motion=null;
        _trackColor.ApplyAnimationClock(SolidColorBrush.ColorProperty,null);
        _thumbPosition.X=current.Item1; _trackColor.Color=current.Item2;
        return current;
    }

    protected override void OnClick()
    {
        if (_suppressClick) return;
        var previous=IsOn;
        base.OnClick();
        if (Command is null && IsOn == previous) SetCurrentValue(IsOnProperty,!IsOn);
        UpdateAppearance();
    }

    private void RequestState(bool requested)
    {
        if (IsEnabled && requested != IsOn) OnClick(); else UpdateAppearance();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressX=e.GetPosition(this).X; _pressPosition=_thumbPosition.X; _dragging=false;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressX is not { } start || !IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var delta=e.GetPosition(this).X-start;
        if (!_dragging && Math.Abs(delta)<SystemParameters.MinimumHorizontalDragDistance) return;
        if (!_dragging) { StopMotion(); _dragging=true; }
        _thumbPosition.X=Math.Clamp(_pressPosition+delta,0,ThumbTravel);
        var fraction=_thumbPosition.X/ThumbTravel;
        _trackColor.Color=Color.FromRgb(
            (byte)Math.Round(DisabledColor.R+(EnabledColor.R-DisabledColor.R)*fraction),
            (byte)Math.Round(DisabledColor.G+(EnabledColor.G-DisabledColor.G)*fraction),
            (byte)Math.Round(DisabledColor.B+(EnabledColor.B-DisabledColor.B)*fraction));
        e.Handled=true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var dragged=_dragging; var requested=_thumbPosition.X>=ThumbTravel/2;
        _pressX=null; _dragging=false; _suppressClick=dragged;
        try { base.OnMouseLeftButtonUp(e); } finally { _suppressClick=false; }
        if (dragged) { RequestState(requested); e.Handled=true; }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        var dragged=_dragging; _pressX=null; _dragging=false;
        if (dragged) UpdateAppearance();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) { RequestState(e.Key == Key.Right); e.Handled=true; }
        else base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FeatureSwitchPeer(this);

    private sealed class FeatureSwitchPeer(FeatureSwitch owner) : ButtonAutomationPeer(owner),IToggleProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);
        public ToggleState ToggleState => owner.IsOn ? ToggleState.On : ToggleState.Off;
        public void Toggle()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.RequestState(!owner.IsOn);
        }
    }

    internal void SeekThumbTransition(TimeSpan offset) => _motion?.Controller?.SeekAlignedToLastTick(offset,TimeSeekOrigin.BeginTime);
}

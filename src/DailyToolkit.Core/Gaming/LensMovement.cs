namespace DailyToolkit.Core.Gaming;

public enum LensCrossingDirection { Left, Right, Top, Bottom }
public sealed record LensMonitorRelay(int TargetMonitor,LensCrossingDirection Direction,PixelBounds VirtualOutput);
public sealed record LensMovementState(int MonitorIndex,LensAxisMotion X,LensAxisMotion Y,LensLayout Layout,LensMonitorRelay? Relay)
{
    public bool VerticalGuide { get; init; }
    public bool HorizontalGuide { get; init; }
}

// Geometry only. The session owns the single native output, renderer and capture.
public sealed class LensMovement
{
    private readonly PixelBounds[] _monitors;
    private readonly int _requestedWidth,_requestedHeight;
    private readonly bool _centerSnap;
    private int _monitorIndex;
    private double _zoom;
    private LensAxisMotion _x,_y;
    public bool IsMovable { get; private set; }
    public LensMovementState State { get; private set; }

    public LensMovement(IReadOnlyList<PixelBounds> monitors,int monitorIndex,int width,int height,double zoom,
        double centerX,double centerY,bool movable,bool centerSnap=false)
    {
        if (monitors.Count == 0 || monitorIndex < 0 || monitorIndex >= monitors.Count ||
            monitors.Any(m => m.Width <= 0 || m.Height <= 0) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex));
        ValidateZoom(zoom);
        _monitors=monitors.ToArray(); _monitorIndex=monitorIndex;
        _requestedWidth=width; _requestedHeight=height; _zoom=zoom; IsMovable=movable; _centerSnap=centerSnap;
        var (x,y)=Limits();
        _x=LensAxisMotion.Create(centerX,centerX,x.OutputMin,x.OutputMax,x.SourceMin,x.SourceMax);
        _y=LensAxisMotion.Create(centerY,centerY,y.OutputMin,y.OutputMax,y.SourceMin,y.SourceMax);
        State=ComposeState(null);
    }

    public void SetMovable(bool movable)
    {
        if (IsMovable && !movable) { _x=State.X; _y=State.Y; }
        IsMovable=movable;
    }

    public void BeginDrag()
    {
        if (!IsMovable) return;
        // Start from the visible frame, discarding only the previous drag's snap offset.
        _x=State.X; _y=State.Y;
    }

    public LensMovementState Move(double dx,double dy,double mouseX,double mouseY)
    {
        if (!IsMovable) return State;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(mouseX) || !double.IsFinite(mouseY))
            throw new ArgumentOutOfRangeException(nameof(dx));
        var startX=mouseX-dx; var startY=mouseY-dy;
        for (var hop=0;hop<_monitors.Length;hop++)
        {
            var crossing=FindCrossing(dx,dy,startX,startY);
            if (crossing is null) break;
            var t=crossing.Value.Time;
            MoveAxes(dx*t,dy*t);
            startX+=dx*t; startY+=dy*t;
            ApplyRelay(crossing.Value.Relay,startX,startY);
            dx*=1-t; dy*=1-t;
            if (dx == 0 && dy == 0) break;
        }
        MoveAxes(dx,dy);
        State=ComposeState(FindVirtualOutput(mouseX,mouseY));
        return State;
    }

    public void UpdateZoom(double zoom)
    {
        ValidateZoom(zoom); _zoom=zoom;
        var (x,y)=Limits();
        _x=_x with { SourceCenter=Math.Clamp(_x.SourceCenter,x.SourceMin,x.SourceMax) };
        _y=_y with { SourceCenter=Math.Clamp(_y.SourceCenter,y.SourceMin,y.SourceMax) };
        State=ComposeState(State.Relay);
    }

    public void Restore(LensMovementState state)
    {
        _monitorIndex=state.MonitorIndex; _x=state.X; _y=state.Y; State=state;
    }

    private static void ValidateZoom(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom < 1 || zoom > LensLayout.MaximumZoom)
            throw new ArgumentOutOfRangeException(nameof(zoom));
    }

    private readonly record struct AxisLimits(double OutputMin,double OutputMax,double SourceMin,double SourceMax);
    private (AxisLimits X,AxisLimits Y) Limits()
    {
        var monitor=_monitors[_monitorIndex];
        var width=Math.Min(_requestedWidth,monitor.Width); var height=Math.Min(_requestedHeight,monitor.Height);
        return (new(monitor.Left+width/2d,monitor.Left+monitor.Width-width/2d,
                    monitor.Left+width/_zoom/2,monitor.Left+monitor.Width-width/_zoom/2),
                new(monitor.Top+height/2d,monitor.Top+monitor.Height-height/2d,
                    monitor.Top+height/_zoom/2,monitor.Top+monitor.Height-height/_zoom/2));
    }

    private static LensAxisMotion MoveAxis(LensAxisMotion axis,double delta,AxisLimits limits) =>
        axis.Move(delta,limits.OutputMin,limits.OutputMax,limits.SourceMin,limits.SourceMax);

    private void MoveAxes(double dx,double dy)
    {
        var (x,y)=Limits(); _x=MoveAxis(_x,dx,x); _y=MoveAxis(_y,dy,y);
    }

    private LensMovementState ComposeState(LensMonitorRelay? relay)
    {
        var monitor=_monitors[_monitorIndex];
        var output=OutputAt(monitor,_x.OutputCenter,_y.OutputCenter);
        // Native windows occupy whole pixels; keep coupled sampling on that exact center.
        _x=AlignOutput(_x,output.Left+output.Width/2d); _y=AlignOutput(_y,output.Top+output.Height/2d);
        var (display,vertical,horizontal)=DisplayOutput(monitor,_x,_y);
        output=display;
        var x=AlignOutput(_x,output.Left+output.Width/2d); var y=AlignOutput(_y,output.Top+output.Height/2d);
        // Raw coupled coordinates retain small deltas while snapping, so the next
        // events can leave the attraction range instead of sticking at the center.
        var sourceWidth=output.Width/_zoom; var sourceHeight=output.Height/_zoom;
        var source=new SourceArea(Math.Clamp(x.SourceCenter-monitor.Left-sourceWidth/2,0,monitor.Width-sourceWidth),
            Math.Clamp(y.SourceCenter-monitor.Top-sourceHeight/2,0,monitor.Height-sourceHeight),sourceWidth,sourceHeight);
        return new(_monitorIndex,x,y,new(output,source),relay) { VerticalGuide=vertical,HorizontalGuide=horizontal };
    }

    private (PixelBounds Output,bool Vertical,bool Horizontal) DisplayOutput(PixelBounds monitor,LensAxisMotion x,LensAxisMotion y)
    {
        var output=OutputAt(monitor,x.OutputCenter,y.OutputCenter);
        if (!_centerSnap || !IsMovable) return (output,false,false);
        var placement=LensPlacement.Snap(monitor,output.Width,output.Height,output.Left,output.Top);
        var vertical=x.Coupling == LensAxisCoupling.Coupled && output.Left > monitor.Left &&
            output.Left+output.Width < monitor.Left+monitor.Width && placement.VerticalGuide;
        var horizontal=y.Coupling == LensAxisCoupling.Coupled && output.Top > monitor.Top &&
            output.Top+output.Height < monitor.Top+monitor.Height && placement.HorizontalGuide;
        return (output with { Left=vertical ? placement.Bounds.Left : output.Left, Top=horizontal ? placement.Bounds.Top : output.Top },vertical,horizontal);
    }

    private PixelBounds OutputAt(PixelBounds monitor,double x,double y)
    {
        var width=Math.Min(_requestedWidth,monitor.Width); var height=Math.Min(_requestedHeight,monitor.Height);
        var left=Math.Clamp((int)Math.Round(x-width/2d),monitor.Left,monitor.Left+monitor.Width-width);
        var top=Math.Clamp((int)Math.Round(y-height/2d),monitor.Top,monitor.Top+monitor.Height-height);
        return new(left,top,width,height);
    }

    private static LensAxisMotion AlignOutput(LensAxisMotion axis,double center) => axis with
    {
        OutputCenter=center,
        SourceCenter=axis.Coupling == LensAxisCoupling.Coupled ? center :
            axis.Coupling == LensAxisCoupling.MinDetached ? Math.Min(axis.SourceCenter,center) : Math.Max(axis.SourceCenter,center)
    };

    private IEnumerable<(int Target,LensCrossingDirection Direction)> Neighbors()
    {
        var origin=_monitors[_monitorIndex];
        for (var index=0;index<_monitors.Length;index++)
        {
            if (index == _monitorIndex) continue;
            var target=_monitors[index];
            var overlapY=target.Top < origin.Top+origin.Height && target.Top+target.Height > origin.Top;
            var overlapX=target.Left < origin.Left+origin.Width && target.Left+target.Width > origin.Left;
            if (overlapY && target.Left+target.Width <= origin.Left) yield return (index,LensCrossingDirection.Left);
            if (overlapY && target.Left >= origin.Left+origin.Width) yield return (index,LensCrossingDirection.Right);
            if (overlapX && target.Top+target.Height <= origin.Top) yield return (index,LensCrossingDirection.Top);
            if (overlapX && target.Top >= origin.Top+origin.Height) yield return (index,LensCrossingDirection.Bottom);
        }
    }

    private bool OnEdge(LensCrossingDirection direction,LensAxisMotion x,LensAxisMotion y)
    {
        var (xl,yl)=Limits();
        return direction switch
        {
            LensCrossingDirection.Left => x.OutputCenter == xl.OutputMin,
            LensCrossingDirection.Right => x.OutputCenter == xl.OutputMax,
            LensCrossingDirection.Top => y.OutputCenter == yl.OutputMin,
            _ => y.OutputCenter == yl.OutputMax
        };
    }

    private LensMonitorRelay VirtualOutput(int targetIndex,LensCrossingDirection direction,LensAxisMotion xAxis,LensAxisMotion yAxis)
    {
        var origin=_monitors[_monitorIndex]; var target=_monitors[targetIndex];
        var visible=DisplayOutput(origin,xAxis,yAxis).Output;
        var x=visible.Left+visible.Width/2d; var y=visible.Top+visible.Height/2d;
        // Reflection about the shared edge (or the midpoint of a desktop gap).
        x=direction switch
        {
            LensCrossingDirection.Left => origin.Left+target.Left+target.Width-x,
            LensCrossingDirection.Right => origin.Left+origin.Width+target.Left-x,
            _ => x
        };
        y=direction switch
        {
            LensCrossingDirection.Top => origin.Top+target.Top+target.Height-y,
            LensCrossingDirection.Bottom => origin.Top+origin.Height+target.Top-y,
            _ => y
        };
        return new(targetIndex,direction,OutputAt(target,x,y));
    }

    private (double Time,LensMonitorRelay Relay)? FindCrossing(double dx,double dy,double mouseX,double mouseY)
    {
        (double Time,LensMonitorRelay Relay)? closest=null;
        var (xl,yl)=Limits();
        foreach (var (target,direction) in Neighbors())
        {
            var horizontal=direction is LensCrossingDirection.Left or LensCrossingDirection.Right;
            var edgeX=direction == LensCrossingDirection.Left ? xl.OutputMin : direction == LensCrossingDirection.Right ? xl.OutputMax : _x.OutputCenter;
            var edgeY=direction == LensCrossingDirection.Top ? yl.OutputMin : direction == LensCrossingDirection.Bottom ? yl.OutputMax : _y.OutputCenter;
            var probe=VirtualOutput(target,direction,_x with { OutputCenter=edgeX },_y with { OutputCenter=edgeY }).VirtualOutput;
            var axis=horizontal ? probe.Left+probe.Width/2d : probe.Top+probe.Height/2d;
            var delta=horizontal ? dx : dy; var start=horizontal ? mouseX : mouseY;
            double time;
            if (delta == 0)
            {
                if (start != axis) continue;
                var bounds=_monitors[target];
                var otherStart=horizontal ? mouseY : mouseX; var otherDelta=horizontal ? dy : dx;
                var min=horizontal ? bounds.Top : bounds.Left; var max=min+(horizontal ? bounds.Height : bounds.Width);
                if (otherStart >= min && otherStart <= max) time=0;
                else if (otherDelta == 0) continue;
                else time=((otherStart < min ? min : max)-otherStart)/otherDelta;
            }
            else time=(axis-start)/delta;
            if (time < 0 || time > 1 || (closest is not null && time >= closest.Value.Time)) continue;
            var x=MoveAxis(_x,dx*time,xl); var y=MoveAxis(_y,dy*time,yl);
            if (!OnEdge(direction,x,y)) continue;
            var mx=mouseX+dx*time; var my=mouseY+dy*time; var screen=_monitors[target];
            if (mx < screen.Left || mx > screen.Left+screen.Width || my < screen.Top || my > screen.Top+screen.Height) continue;
            closest=(time,VirtualOutput(target,direction,x,y));
        }
        return closest;
    }

    private LensMonitorRelay? FindVirtualOutput(double mouseX,double mouseY)
    {
        LensMonitorRelay? selected=null; var best=double.PositiveInfinity;
        foreach (var (target,direction) in Neighbors())
        {
            if (!OnEdge(direction,_x,_y)) continue;
            var bounds=_monitors[target];
            var dx=Math.Max(0,Math.Max(bounds.Left-mouseX,mouseX-bounds.Left-bounds.Width));
            var dy=Math.Max(0,Math.Max(bounds.Top-mouseY,mouseY-bounds.Top-bounds.Height));
            var distance=dx*dx+dy*dy;
            if (distance >= best) continue;
            best=distance; selected=VirtualOutput(target,direction,_x,_y);
        }
        return selected;
    }

    private void ApplyRelay(LensMonitorRelay relay,double mouseX,double mouseY)
    {
        _monitorIndex=relay.TargetMonitor;
        var output=relay.VirtualOutput;
        var cx=output.Left+output.Width/2d; var cy=output.Top+output.Height/2d;
        var (x,y)=Limits();
        var horizontal=relay.Direction is LensCrossingDirection.Left or LensCrossingDirection.Right;
        _x=horizontal ? new(cx,cx,LensAxisCoupling.Coupled) : LandingAxis(cx,mouseX,x);
        _y=horizontal ? LandingAxis(cy,mouseY,y) : new(cy,cy,LensAxisCoupling.Coupled);
    }

    private static LensAxisMotion LandingAxis(double center,double mouse,AxisLimits limits)
    {
        if (center == limits.OutputMin && mouse <= center)
            return new(center,Math.Clamp(mouse,limits.SourceMin,center),LensAxisCoupling.MinDetached);
        if (center == limits.OutputMax && mouse >= center)
            return new(center,Math.Clamp(mouse,center,limits.SourceMax),LensAxisCoupling.MaxDetached);
        // If the mouse is on the opposite half, retain the center and couple.
        return new(center,center,LensAxisCoupling.Coupled);
    }
}

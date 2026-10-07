namespace DailyToolkit.Core.Gaming;

public enum LensAxisCoupling { Coupled, MinDetached, MaxDetached }

// One physical-pixel axis. Rejoining the output center consumes only the distance
// needed to rejoin; the remainder can hit the opposite edge in the same event.
public readonly record struct LensAxisMotion(double OutputCenter,double SourceCenter,LensAxisCoupling Coupling)
{
    public static LensAxisMotion Create(double output,double source,double outputMin,double outputMax,
        double sourceMin,double sourceMax)
    {
        output=Math.Clamp(output,outputMin,outputMax);
        source=Math.Clamp(source,sourceMin,sourceMax);
        var coupling=source < output ? LensAxisCoupling.MinDetached : source > output ? LensAxisCoupling.MaxDetached :
            output == outputMin ? LensAxisCoupling.MinDetached : output == outputMax ? LensAxisCoupling.MaxDetached : LensAxisCoupling.Coupled;
        return new(output,source,coupling);
    }

    public LensAxisMotion Move(double delta,double outputMin,double outputMax,double sourceMin,double sourceMax)
    {
        if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        if (delta == 0) return this;
        var output=OutputCenter;
        var source=SourceCenter;
        var coupling=Coupling;
        if (coupling != LensAxisCoupling.Coupled)
        {
            var returning=coupling == LensAxisCoupling.MinDetached ? delta > 0 : delta < 0;
            if (!returning) return new(output,Math.Clamp(source+delta,sourceMin,sourceMax),coupling);
            var distance=output-source;
            if (Math.Abs(delta) < Math.Abs(distance)) return new(output,source+delta,coupling);
            delta-=distance;
            source=output;
            coupling=LensAxisCoupling.Coupled;
            if (delta == 0) return new(output,source,coupling);
        }
        var movedOutput=Math.Clamp(output+delta,outputMin,outputMax);
        var remainder=delta-(movedOutput-output);
        output=source=movedOutput;
        coupling=output == outputMin && (outputMin != outputMax || delta < 0) ? LensAxisCoupling.MinDetached :
            output == outputMax ? LensAxisCoupling.MaxDetached : LensAxisCoupling.Coupled;
        if (remainder != 0) source=Math.Clamp(source+remainder,sourceMin,sourceMax);
        return new(output,source,coupling);
    }
}

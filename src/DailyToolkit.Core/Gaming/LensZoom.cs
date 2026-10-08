using System.Globalization;

namespace DailyToolkit.Core.Gaming;

public static class LensZoom
{
    public const double Minimum = 1, Maximum = 16, Step = 0.25, Default = 2;
    public static double Normalize(double value) => double.IsFinite(value)
        ? Math.Round(Math.Clamp(value, Minimum, Maximum) * 4, MidpointRounding.AwayFromZero) / 4 : Default;
    public static string Format(double value) => Normalize(value).ToString("0.00", CultureInfo.InvariantCulture) + "×";
}

// Residual device deltas survive until a full WHEEL_DELTA, including reversals.
public sealed class LensWheelAccumulator
{
    private long _remainder;
    public int Consume(int delta)
    {
        var total = _remainder + delta;
        var ticks = (int)(total / 120);
        _remainder = total % 120;
        return ticks;
    }
    public void Reset() => _remainder = 0;
}

// No timers or rendering resources: the session supplies its monotonic time.
public sealed class LensZoomHud
{
    public const double HoldSeconds = 0.65, FadeSeconds = 0.15;
    private double _lastShown = double.NegativeInfinity;
    public double Zoom { get; private set; } = LensZoom.Default;
    public void Show(double zoom, double seconds) { Zoom = LensZoom.Normalize(zoom); _lastShown = seconds; }
    public float Opacity(double seconds) => (float)Math.Clamp(1 - (seconds - _lastShown - HoldSeconds) / FadeSeconds, 0, 1);
    public void Clear() => _lastShown = double.NegativeInfinity;
    public LensHudFrame Frame(double seconds) => new(Zoom, Opacity(seconds));
}

public readonly record struct LensHudFrame(double Zoom, float Opacity);

namespace DailyToolkit.Core.Gaming;

public sealed class LensFramePacer
{
    private readonly double _period;
    private double _next;
    private bool _started;

    // Zero follows incoming capture frames without an application frame-rate cap.
    public LensFramePacer(int framesPerSecond,long timestampFrequency)
    {
        if (framesPerSecond < 0 || timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _period=framesPerSecond == 0 ? 0 : timestampFrequency/(double)framesPerSecond;
    }

    public bool ShouldRender(long timestamp)
    {
        if (_period == 0) return true;
        if (_started && timestamp+_period*0.05 < _next) return false;
        _next=!_started || timestamp-_next > _period ? timestamp+_period : _next+_period;
        _started=true;
        return true;
    }
}

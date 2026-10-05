namespace DailyUSE.Core.Gaming;

public sealed record LensPlacement(PixelBounds Bounds, bool VerticalGuide, bool HorizontalGuide)
{
    // Physical pixels: snapping stays consistent on monitors with different DPI and negative origins.
    public static LensPlacement Snap(PixelBounds monitor, int width, int height, int left, int top, int distance = 24)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0 || width <= 0 || height <= 0 || distance < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        width = Math.Min(width, monitor.Width);
        height = Math.Min(height, monitor.Height);
        left = Math.Clamp(left, monitor.Left, monitor.Left + monitor.Width - width);
        top = Math.Clamp(top, monitor.Top, monitor.Top + monitor.Height - height);
        var centerLeft = monitor.Left + monitor.Width / 2 - width / 2;
        var centerTop = monitor.Top + monitor.Height / 2 - height / 2;
        var vertical = Math.Abs((long)left - centerLeft) <= distance;
        var horizontal = Math.Abs((long)top - centerTop) <= distance;
        return new(new(vertical ? centerLeft : left, horizontal ? centerTop : top, width, height), vertical, horizontal);
    }
}

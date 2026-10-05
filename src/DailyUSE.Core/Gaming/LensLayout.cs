namespace DailyUSE.Core.Gaming;

public sealed record PixelBounds(int Left, int Top, int Width, int Height);
public sealed record SourceArea(double Left, double Top, double Width, double Height);
public sealed record LensLayout(PixelBounds Output, SourceArea Source)
{
    public static LensLayout Calculate(PixelBounds monitor, int width, int height, double zoom, int centerX, int centerY)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0 || width <= 0 || height <= 0 ||
            !double.IsFinite(zoom) || zoom < 1 || zoom > 8) throw new ArgumentOutOfRangeException(nameof(zoom));
        width = Math.Min(width, monitor.Width);
        height = Math.Min(height, monitor.Height);
        var left = Math.Clamp(centerX - width / 2, monitor.Left, monitor.Left + monitor.Width - width);
        var top = Math.Clamp(centerY - height / 2, monitor.Top, monitor.Top + monitor.Height - height);
        var sourceWidth = width / zoom;
        var sourceHeight = height / zoom;
        var sourceLeft = Math.Clamp(centerX - monitor.Left - sourceWidth / 2, 0, monitor.Width - sourceWidth);
        var sourceTop = Math.Clamp(centerY - monitor.Top - sourceHeight / 2, 0, monitor.Height - sourceHeight);
        return new(new(left, top, width, height), new(sourceLeft, sourceTop, sourceWidth, sourceHeight));
    }
}

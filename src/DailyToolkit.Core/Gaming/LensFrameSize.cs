namespace DailyToolkit.Core.Gaming;

public enum LensFrameSizePreset { Custom, Small, Medium, Large }
public enum LensAspectRatio { Screen, Square, Wide, Standard, Ultrawide }

public static class LensFrameSize
{
    public static (int Width,int Height) Resolve(PixelBounds screen,int width,int height,LensFrameSizePreset preset,LensAspectRatio aspect=LensAspectRatio.Screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0 || width <= 0 || height <= 0 || !Enum.IsDefined(preset) || !Enum.IsDefined(aspect))
            throw new ArgumentOutOfRangeException(nameof(preset));
        var ratio=preset switch { LensFrameSizePreset.Small => .2,LensFrameSizePreset.Medium => .3,LensFrameSizePreset.Large => .4,_ => 0 };
        if (ratio == 0) return (Math.Min(width,screen.Width),Math.Min(height,screen.Height));
        var w=screen.Width*ratio; var h=screen.Height*ratio;
        var shape=aspect switch { LensAspectRatio.Square => 1d,LensAspectRatio.Wide => 16d/9,
            LensAspectRatio.Standard => 4d/3,LensAspectRatio.Ultrawide => 21d/9,_ => w/h };
        // Fit the chosen shape into the proportional screen box, including portrait displays.
        if (w/h > shape) w=h*shape; else h=w/shape;
        return (Math.Clamp((int)Math.Round(w),1,screen.Width),Math.Clamp((int)Math.Round(h),1,screen.Height));
    }
}

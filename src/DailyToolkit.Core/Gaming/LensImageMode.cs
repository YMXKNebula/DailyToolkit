namespace DailyToolkit.Core.Gaming;

public enum LensImageMode { Performance, Clear, HighQuality, Pixel, AIEnhanced }

public static class LensImageSettings
{
    public const double DefaultSharpness = 35;
    public static LensImageMode NormalizeMode(LensImageMode mode) => mode switch
    {
        LensImageMode.Performance or LensImageMode.Clear or LensImageMode.HighQuality or LensImageMode.Pixel => mode,
        LensImageMode.AIEnhanced => LensImageMode.HighQuality,
        _ => LensImageMode.Clear
    };
    public static double NormalizeSharpness(double value) => double.IsFinite(value)
        ? Math.Round(Math.Clamp(value,0,100),MidpointRounding.AwayFromZero) : DefaultSharpness;
}

namespace DailyToolkit.Core.Gaming;

public enum LensImageMode { Performance, Clear, HighQuality, Pixel, AIEnhanced }

public static class LensImageSettings
{
    public const double DefaultSharpness = 100;
    public static LensImageMode NormalizeMode(LensImageMode mode) => mode switch
    {
        LensImageMode.Performance or LensImageMode.Clear or LensImageMode.HighQuality or LensImageMode.Pixel => mode,
        LensImageMode.AIEnhanced => LensImageMode.HighQuality,
        _ => LensImageMode.Clear
    };
    // Retain the old serialized field, but ignore historical slider values.
    public static double NormalizeSharpness(double value) => DefaultSharpness;
    public static double GetSharpening(LensImageMode mode) => NormalizeMode(mode) switch
    {
        LensImageMode.Performance => 0.35,
        LensImageMode.Pixel => 0,
        _ => 1
    };
}

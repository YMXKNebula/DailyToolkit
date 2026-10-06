using System.Globalization;

namespace DailyToolkit.Core.Gaming;

public static class LensBorderColor
{
    public const string Default = "#33B28A";
    public const uint DefaultRgb = 0x33B28A;

    public static bool TryParse(string? value, out uint rgb)
    {
        rgb=0;
        return value is { Length: 7 } && value[0] == '#' &&
            uint.TryParse(value.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out rgb);
    }
}

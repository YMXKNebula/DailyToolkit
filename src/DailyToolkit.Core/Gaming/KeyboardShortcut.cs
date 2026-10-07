using System.Text.Json.Serialization;

namespace DailyToolkit.Core.Gaming;

public sealed record KeyboardShortcut(uint Modifiers, uint VirtualKey, string Name)
{
    [JsonIgnore] public bool IsValid => (Modifiers & ~15u) == 0 && VirtualKey is > 0 and < 256 &&
        VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) &&
        !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80 && !Name.Any(char.IsControl);
    public bool Matches(KeyboardShortcut? other) => other is not null &&
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
}

public enum LensActivationMode { Toggle, Hold }
public enum LensMovementMode { Movable, Fixed }

public sealed record GamingPreferences
{
    public bool IsEnabled { get; init; } = true;
    public LensFrameSizePreset FrameSizePreset { get; init; } = LensFrameSizePreset.Custom;
    public LensAspectRatio AspectRatio { get; init; } = LensAspectRatio.Screen;
    public int FrameWidth { get; init; } = 640;
    public int FrameHeight { get; init; } = 384;
    public KeyboardShortcut? ToggleShortcut { get; init; } = new(6,0x77,"Ctrl + Shift + F8");
    public LensActivationMode ActivationMode { get; init; } = LensActivationMode.Toggle;
    public LensMovementMode MovementMode { get; init; } = LensMovementMode.Fixed;
    public bool WheelZoomEnabled { get; init; } = true;
    public string BorderColor { get; init; } = LensBorderColor.Default;
    [JsonIgnore] public bool IsValid => Enum.IsDefined(FrameSizePreset) && Enum.IsDefined(AspectRatio) && FrameWidth is >= 160 and <= 1600 &&
        FrameHeight is >= 120 and <= 1200 && (ToggleShortcut is null || ToggleShortcut.IsValid) &&
        (ActivationMode is LensActivationMode.Toggle or LensActivationMode.Hold) &&
        (MovementMode is LensMovementMode.Movable or LensMovementMode.Fixed) && LensBorderColor.TryParse(BorderColor,out _);
}

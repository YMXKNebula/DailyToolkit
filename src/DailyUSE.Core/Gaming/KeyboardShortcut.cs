using System.Text.Json.Serialization;

namespace DailyUSE.Core.Gaming;

public sealed record KeyboardShortcut(uint Modifiers, uint VirtualKey, string Name)
{
    [JsonIgnore] public bool IsValid => (Modifiers & ~15u) == 0 && VirtualKey is > 0 and < 256 &&
        VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) &&
        !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80 && !Name.Any(char.IsControl);
    public bool Matches(KeyboardShortcut? other) => other is not null &&
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
}

public sealed record GamingPreferences
{
    public KeyboardShortcut? ToggleShortcut { get; init; } = new(6,0x77,"Ctrl + Shift + F8");
    public KeyboardShortcut? CloseShortcut { get; init; } = new(6,0x78,"Ctrl + Shift + F9");
    [JsonIgnore] public bool IsValid => (ToggleShortcut is null || ToggleShortcut.IsValid) &&
        (CloseShortcut is null || CloseShortcut.IsValid) && ToggleShortcut?.Matches(CloseShortcut) != true;
}

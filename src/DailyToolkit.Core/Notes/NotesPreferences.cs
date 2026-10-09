using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Core.Notes;

public enum NotesPositionMode { Fixed, Movable }

public sealed record NotesBackground(string Color = "#FFF8E8", double Opacity = .96, double Radius = 14);
public sealed record NotesFont(string Family = "Microsoft YaHei UI", string Color = "#253344", double Opacity = 1,
    double Size = 18, bool Bold = false);

public sealed record NotesPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public bool IsEnabled { get; init; } = true;
    public KeyboardShortcut? ToggleShortcut { get; init; } = new(3, 0x4E, "Ctrl + Alt + N");
    public KeyboardShortcut? FocusShortcut { get; init; } = new(7, 0x4E, "Ctrl + Alt + Shift + N");
    public NotesPositionMode PositionMode { get; init; } = NotesPositionMode.Fixed;
    public bool Topmost { get; init; } = true;
    public bool AllowManualResize { get; init; } = true;
    public bool OpaqueWhenFocused { get; init; } = true;
    public string FocusBorderColor { get; init; } = "#267A5D";
    // Position is in virtual-desktop physical pixels; dimensions are in WPF DIPs.
    public double? X { get; init; }
    public double? Y { get; init; }
    public double Width { get; init; } = 360;
    public double Height { get; init; } = 420;
    public NotesBackground Background { get; init; } = new();
    public NotesFont Font { get; init; } = new();

    public NotesPreferences Normalize() => this with
    {
        SchemaVersion = 1,
        ToggleShortcut = ToggleShortcut is { IsValid: false } ? new NotesPreferences().ToggleShortcut : ToggleShortcut,
        FocusShortcut = FocusShortcut is { IsValid: false } ? new NotesPreferences().FocusShortcut : FocusShortcut,
        PositionMode = Enum.IsDefined(PositionMode) ? PositionMode : NotesPositionMode.Fixed,
        X = X is { } x && double.IsFinite(x) && Math.Abs(x) < 1_000_000 ? x : null,
        Y = Y is { } y && double.IsFinite(y) && Math.Abs(y) < 1_000_000 ? y : null,
        Width = Math.Round(Clamp(Width, 240, 1200, 360), MidpointRounding.AwayFromZero),
        Height = Math.Round(Clamp(Height, 180, 1400, 420), MidpointRounding.AwayFromZero),
        FocusBorderColor = ValidColor(FocusBorderColor) ? FocusBorderColor : "#267A5D",
        Background = (Background ?? new()) with
        {
            Color = ValidColor(Background?.Color) ? Background!.Color : "#FFF8E8",
            Opacity = Clamp(Background?.Opacity ?? .96, .05, 1, .96),
            Radius = Clamp(Background?.Radius ?? 14, 0, 40, 14)
        },
        Font = (Font ?? new()) with
        {
            Family = Font is { Family.Length: > 0 and <= 200 } && !Font.Family.Any(char.IsControl) ? Font.Family : "Microsoft YaHei UI",
            Color = ValidColor(Font?.Color) ? Font!.Color : "#253344",
            Opacity = Clamp(Font?.Opacity ?? 1, .1, 1, 1), Size = Clamp(Font?.Size ?? 18, 10, 72, 18)
        }
    };

    public static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.Skip(1).All(Uri.IsHexDigit);
    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public readonly record struct NotesRect(double X, double Y, double Width, double Height)
{
    public bool Intersects(NotesRect other) => X < other.X + other.Width && X + Width > other.X &&
        Y < other.Y + other.Height && Y + Height > other.Y;
}

public static class NotesPosition
{
    public static NotesRect Recover(NotesRect window, IReadOnlyList<NotesRect> workAreas)
    {
        if (workAreas.Count == 0 || workAreas.Any(window.Intersects)) return window;
        var area = workAreas[0];
        return window with { X = area.X + Math.Min(32, Math.Max(0, area.Width - window.Width)),
            Y = area.Y + Math.Min(32, Math.Max(0, area.Height - window.Height)) };
    }

    public static bool CanStartDrag(NotesPositionMode mode, double dx, double dy,
        double horizontalThreshold, double verticalThreshold, int clickCount) =>
        mode == NotesPositionMode.Movable && clickCount == 1 &&
        (Math.Abs(dx) >= horizontalThreshold || Math.Abs(dy) >= verticalThreshold);
}

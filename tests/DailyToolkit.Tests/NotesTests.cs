using DailyToolkit.Core.Notes;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterNotesTests()
    {
        Test("Notes preserve independent visibility-free position and appearance settings", () =>
        {
            var settings = new NotesPreferences { PositionMode = NotesPositionMode.Movable, Topmost = false,
                Background = new("#AABBCC", .2), Font = new("Consolas", "#123456", .8, 30, true) };
            var backgroundChanged = settings with { Background = new("#FFF0AA") };
            Require(backgroundChanged.Font == settings.Font && !backgroundChanged.Topmost && backgroundChanged.PositionMode == NotesPositionMode.Movable);
            Require((settings with { Font = new() }).Background == settings.Background);
        });
        Test("Notes normalize damaged and missing optional preferences", () =>
        {
            var settings = new NotesPreferences { Width = double.NaN, Height = -1, X = double.PositiveInfinity,
                PositionMode = (NotesPositionMode)99, Background = new("bad", -1, 100), Font = new("", "bad", 20, 500) }.Normalize();
            Require(settings.Width == 360 && settings.Height == 180 && settings.X is null && settings.PositionMode == NotesPositionMode.Fixed);
            Require(settings.Background.Color == "#FFF8E8" && settings.Background.Opacity == .05 && settings.Background.Radius == 40);
            Require(settings.Font.Size == 72 && settings.Font.Opacity == 1 && settings.Font.Family == "Microsoft YaHei UI");
            Require((new NotesPreferences { Background = null!, Font = null! }).Normalize() == new NotesPreferences());
        });
        Test("Notes default hotkeys are valid and distinct", () =>
        {
            var settings = new NotesPreferences();
            Require(settings.ToggleShortcut!.IsValid && settings.FocusShortcut!.IsValid && !settings.ToggleShortcut.Matches(settings.FocusShortcut));
            Require(settings.ToggleShortcut!.Modifiers == 3 && settings.FocusShortcut!.Modifiers == 7);
        });
        Test("Notes round legacy fractional dimensions and validate focus appearance", () =>
        {
            var settings = new NotesPreferences { Width = 360.5, Height = 420.4, FocusBorderColor = "bad" }.Normalize();
            Require(settings.Width == 361 && settings.Height == 420 && settings.FocusBorderColor == "#267A5D");
            Require(settings.AllowManualResize && settings.OpaqueWhenFocused);
            Require((settings with { Width = 5000.7, Height = 179.6, FocusBorderColor = "#123ABC" }).Normalize() is
                { Width: 1200, Height: 180, FocusBorderColor: "#123ABC" });
        });
        Test("Notes keep negative and vertically arranged monitor coordinates", () =>
        {
            NotesRect[] areas = [new(0, 0, 2560, 1400), new(-1920, 0, 1920, 1040), new(0, -1440, 2560, 1400)];
            foreach (var window in new[] { new NotesRect(-1800, 20, 540, 630), new NotesRect(300, -1200, 360, 420), new NotesRect(-20, 100, 360, 420) })
                Require(NotesPosition.Recover(window, areas) == window);
        });
        Test("Notes recover disconnected and empty-monitor positions safely", () =>
        {
            var window = new NotesRect(-5000, 3000, 360, 420);
            var recovered = NotesPosition.Recover(window, [new(100, 200, 1920, 1040)]);
            Require(recovered.X == 132 && recovered.Y == 232 && recovered.Width == window.Width);
            Require(NotesPosition.Recover(window, []) == window);
        });
        Test("Notes drag starts on movement without waiting and never starts fixed or double click", () =>
        {
            bool Drag(NotesPositionMode mode, double dx, double dy, int clicks = 1) =>
                NotesPosition.CanStartDrag(mode, dx, dy, 4, 4, clicks);
            Require(!Drag(NotesPositionMode.Movable, 3, 3) && !Drag(NotesPositionMode.Movable, 0, 0));
            Require(Drag(NotesPositionMode.Movable, -4, 0) && Drag(NotesPositionMode.Movable, 0, 4));
            Require(!Drag(NotesPositionMode.Fixed, 100, 100) && !Drag(NotesPositionMode.Movable, 100, 100, 2));
        });
    }
}

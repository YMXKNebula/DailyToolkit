using System.Windows;
using System.Windows.Controls;

namespace DailyToolkit.Desktop.Notes;

internal sealed class NotesDisplayArea(Border surface, TextBox editor)
{
    private bool? _focused;
    private bool _visibleLayout;

    internal void Apply(bool focused)
    {
        var visible = editor.IsVisible && editor.ActualWidth > 0 && editor.ActualHeight > 0 && PresentationSource.FromVisual(editor) is not null;
        if (_focused == focused) { _visibleLayout |= visible; return; }
        var preserve = _visibleLayout && visible;
        _focused = focused;
        editor.ApplyTemplate(); editor.UpdateLayout();
        var scroll = editor.Template.FindName("PART_ContentHost", editor) as ScrollViewer;
        var anchor = preserve
            ? editor.GetCharacterIndexFromPoint(new Point(12, editor.ActualHeight / 2), true) : -1;
        var before = anchor >= 0 ? editor.GetRectFromCharacterIndex(anchor) : Rect.Empty;
        var origin = before.IsEmpty ? new Point() : editor.TranslatePoint(before.TopLeft, surface);
        var hadScrollbar = scroll?.ComputedVerticalScrollBarVisibility == Visibility.Visible;

        surface.Padding = focused ? new(4, 3, 4, 3) : new(4);
        surface.Resources["NotesHeaderHeight"] = new GridLength(focused ? 26 : 0);
        surface.Resources["NotesFooterHeight"] = new GridLength(focused ? 28 : 0);
        surface.Resources["NotesTextTopInset"] = new GridLength(0);
        surface.Resources["NotesTextBottomInset"] = new GridLength(0);
        editor.Padding = focused ? new(6, 7, 6, 7) : new(6);
        // Preserve the wrapping width even if the expanded viewport fits all text.
        editor.VerticalScrollBarVisibility = focused ? ScrollBarVisibility.Auto :
            hadScrollbar ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
        editor.UpdateLayout();
        _visibleLayout = visible;
        var after = anchor >= 0 ? editor.GetRectFromCharacterIndex(anchor) : Rect.Empty;
        if (before.IsEmpty || after.IsEmpty) return;
        var offset = editor.VerticalOffset + editor.TranslatePoint(after.TopLeft, surface).Y - origin.Y;
        if (!focused)
        {
            // At either end of the note, unavailable lines become empty space.
            // In the middle, adjusting the scroll offset reveals existing lines
            // above and below without shifting the visible text.
            surface.Resources["NotesTextTopInset"] = new GridLength(Math.Max(0, -offset));
            offset = Math.Max(0, offset); editor.UpdateLayout();
            surface.Resources["NotesTextBottomInset"] = new GridLength(Math.Max(0, offset - (scroll?.ScrollableHeight ?? 0)));
            editor.UpdateLayout();
        }
        editor.ScrollToVerticalOffset(Math.Max(0, offset));
    }
}

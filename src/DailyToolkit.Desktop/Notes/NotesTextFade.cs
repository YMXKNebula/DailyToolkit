using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DailyToolkit.Desktop.Notes;

// Mask the text viewport, leaving the background, frame and scrollbar unchanged.
internal sealed class NotesTextFade
{
    private readonly TextBox _editor;
    private bool _focused;
    private bool _queued;

    internal NotesTextFade(TextBox editor)
    {
        _editor = editor;
        editor.Loaded += (_, _) => QueueUpdate();
        editor.SizeChanged += (_, _) => QueueUpdate();
        editor.TextChanged += (_, _) => QueueUpdate();
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueUpdate()));
    }

    internal void Apply(bool focused) { _focused = focused; Update(); QueueUpdate(); }

    private void QueueUpdate()
    {
        if (_queued) return;
        _queued = true;
        _editor.Dispatcher.BeginInvoke(new Action(() => { _queued = false; Update(); }), DispatcherPriority.Loaded);
    }

    private void Update()
    {
        if (!_editor.IsVisible) return;
        _editor.ApplyTemplate();
        if (_editor.Template.FindName("PART_ContentHost", _editor) is not ScrollViewer scroll) return;
        scroll.ApplyTemplate();
        if (scroll.Template.FindName("PART_ScrollContentPresenter", scroll) is not ScrollContentPresenter viewport) return;
        var top = _focused && scroll.VerticalOffset > .5;
        var bottom = _focused && scroll.ExtentHeight - scroll.VerticalOffset - scroll.ViewportHeight > .5;
        var height = viewport.ActualHeight;
        if ((!top && !bottom) || height <= 0) { viewport.OpacityMask = null; return; }
        var edge = Math.Min(18 / height, .25);
        // Absolute coordinates keep the fade at the visible viewport rather than
        // stretching its gradient over the full height of the scrolled document.
        var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(0, height) };
        mask.GradientStops.Add(new(top ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new(Colors.Black, edge));
        mask.GradientStops.Add(new(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new(bottom ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze(); viewport.OpacityMask = mask;
    }
}

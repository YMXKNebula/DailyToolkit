using System.Windows;
using System.Windows.Controls;

namespace DailyToolkit.Desktop.ClipRecall;

// Recycling list items release Source when unloaded or rebound. All decoding and
// persistence stay in the view-model/repository, outside the visual control.
public sealed class ClipboardThumbnail : Image
{
    public static readonly DependencyProperty RecordIdProperty = DependencyProperty.Register(nameof(RecordId), typeof(Guid),
        typeof(ClipboardThumbnail), new PropertyMetadata(Guid.Empty, Changed));
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ClipboardHistoryViewModel),
        typeof(ClipboardThumbnail), new PropertyMetadata(null, Changed));
    public Guid RecordId { get => (Guid)GetValue(RecordIdProperty); set => SetValue(RecordIdProperty, value); }
    public ClipboardHistoryViewModel? Model { get => (ClipboardHistoryViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    private int _revision;
    public ClipboardThumbnail()
    {
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => { ++_revision; Source = null; };
        IsVisibleChanged += (_, _) => { if (IsVisible) Load(); else { ++_revision; Source = null; } };
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((ClipboardThumbnail)target).Load();
    private async void Load()
    {
        var revision = ++_revision; Source = null;
        if (!IsLoaded || !IsVisible || RecordId == Guid.Empty || Model is null) return;
        try
        {
            var image = await Model.ThumbnailAsync(RecordId);
            if (revision == _revision && IsLoaded) Source = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception)) { }
    }
}

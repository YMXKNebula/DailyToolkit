using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DailyToolkit.Core.Clipboard;

namespace DailyToolkit.Desktop.ClipRecall;

public sealed class ClipboardTypeDrawing : IValueConverter
{
    private static Geometry Drawing(string path)
    { var geometry = Geometry.Parse(path); geometry.Freeze(); return geometry; }
    private static readonly Geometry Text = Drawing("M7,2 H22 L29,9 V32 H7 Z M22,2 V9 H29 M12,14 H24 M18,14 V26 M14,26 H22");
    private static readonly Geometry Link = Drawing("M13,12 L18,7 C22,3 28,3 31,6 C34,9 34,15 30,19 L25,24 M21,26 L16,31 C12,35 6,35 3,32 C0,29 0,23 4,19 L9,14 M11,25 L23,13");
    private static readonly Geometry Files = Drawing("M2,9 V29 Q2,32 5,32 H31 Q34,32 34,29 V13 Q34,10 31,10 H17 L13,5 H5 Q2,5 2,9 Z M2,14 H34");
    private static readonly Geometry Image = Drawing("M3,5 H33 V31 H3 Z M3,26 L13,15 L22,25 L27,20 L33,26 M23,11 A2.5,2.5 0 1 0 28,11 A2.5,2.5 0 1 0 23,11");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ClipboardContentType.Link => Link,
        ClipboardContentType.Files => Files,
        ClipboardContentType.Image => Image,
        _ => Text
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

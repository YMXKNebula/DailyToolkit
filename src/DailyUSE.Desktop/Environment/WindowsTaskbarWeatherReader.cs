using System.Runtime.InteropServices;
using System.Windows.Automation;
using DailyUSE.Core.Environment;

namespace DailyUSE.Desktop.Environment;

internal static class WindowsTaskbarWeatherReader
{
    public static WeatherInfo Read()
    {
        try
        {
            var handle = FindWindow("Shell_TrayWnd", null);
            if (handle == IntPtr.Zero) return WeatherInfo.Unavailable("Windows 任务栏暂时不可读");
            var taskbar = AutomationElement.FromHandle(handle);
            // Scope reads to the existing weather/widget entry; never open the widget board or read other apps.
            var ids = new[] { "AugmentedEntryPointButton", "WidgetsButton", "DynamicContent1", "NewsAndInterestsButton" };
            var conditions = ids.Select(id => (Condition)new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray();
            var matches = taskbar.FindAll(TreeScope.Descendants, new OrCondition(conditions));
            if (matches.Count == 0) return WeatherInfo.Unavailable("未找到可读取的任务栏天气入口", "Windows 任务栏");
            foreach (AutomationElement element in matches)
            {
                var weather = WindowsWeatherText.Parse(element.Current.Name);
                if (weather.Available) return weather;
            }
            return WeatherInfo.Unavailable("Windows 任务栏未提供天气文字");
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return WeatherInfo.Unavailable("Windows 天气文字暂时不可读");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
}

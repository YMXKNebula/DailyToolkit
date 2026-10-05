using System.IO;
using DailyToolkit.Core.Environment;

namespace DailyToolkit.Desktop.Environment;

public sealed class WindowsWeatherCacheReader(string? localApplicationData = null)
{
    public async Task<WeatherInfo> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var root = localApplicationData ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(root, "Packages", "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy",
            "LocalState", "DiagOutputDir", "IDX_CONTENT_TASKBARHEADLINES.json");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is < 2 or > WindowsWeatherCache.MaximumBytes)
                return WeatherInfo.Unavailable("Windows 天气缓存暂时不可读", WindowsWeatherCache.Source);
            var contents = new byte[(int)file.Length];
            await file.ReadExactlyAsync(contents, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return WindowsWeatherCache.Parse(contents, now);
        }
        catch (FileNotFoundException) { return WeatherInfo.Unavailable("Windows 还没有天气缓存", WindowsWeatherCache.Source); }
        catch (DirectoryNotFoundException) { return WeatherInfo.Unavailable("Windows 还没有天气缓存", WindowsWeatherCache.Source); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WeatherInfo.Unavailable("Windows 天气缓存暂时不可读", WindowsWeatherCache.Source);
        }
    }
}

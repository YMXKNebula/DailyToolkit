using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckWeatherCacheAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-weather-tests-");
        var path = Path.Combine(directory.FullName, "Packages", "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy",
            "LocalState", "DiagOutputDir", "IDX_CONTENT_TASKBARHEADLINES.json");
        try
        {
            var reader = new WindowsWeatherCacheReader(directory.FullName);
            var now = DateTimeOffset.Parse("2026-10-05T06:00:00Z");
            Require(!(await reader.ReadAsync(now)).Available, "Missing Windows cache did not degrade gracefully");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = JsonSerializer.Serialize(new
            {
                responses = new[] { new { weather = new[] { new { current = new { temp = 23, cap = "晴朗", created = "2026-10-05T05:40:00Z" } } } } },
                units = new { temperature = "°C" }
            });
            var json = JsonSerializer.Serialize(new
            {
                sections = new[] { new { cards = new[] { new { type = "WeatherSummary", dataType = "WeatherOverview", data } } } }
            });
            await File.WriteAllBytesAsync(path, Encoding.Unicode.GetBytes(json));
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Require((await reader.ReadAsync(now)).Summary == "23°C · 晴朗", "The Windows-owned shared UTF-16 cache was not read");
            await File.WriteAllTextAsync(path, "{");
            Require(!(await reader.ReadAsync(now)).Available, "Partially written cache was treated as weather");
            using (var oversized = new FileStream(path, FileMode.Open, FileAccess.Write))
                oversized.SetLength(WindowsWeatherCache.MaximumBytes + 1L);
            Require(!(await reader.ReadAsync(now)).Available, "Oversized cache was read");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await reader.ReadAsync(now, cancellation.Token); throw new InvalidOperationException("Canceled cache read continued"); }
            catch (OperationCanceledException) { }
            Console.WriteLine("PASS Local Windows weather cache tolerates sharing, missing/partial files, size limits, and cancellation");
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-weather-tests-", StringComparison.Ordinal)) directory.Delete(recursive: true);
        }
    }
}

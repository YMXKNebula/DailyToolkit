using System.Text.RegularExpressions;

namespace DailyUSE.Core.Environment;

public enum NetworkState { Unknown, Disconnected, LocalNetwork, Internet }

public sealed record NetworkInfo(NetworkState State, string ConnectionType,
    double? ReceivedBytesPerSecond, double? SentBytesPerSecond)
{
    public static NetworkInfo Unknown { get; } = new(NetworkState.Unknown, "", null, null);
}

public sealed record WeatherInfo(bool Available, string Summary, string Source, string? Reason)
{
    public static WeatherInfo Unavailable(string reason) => new(false, "未获取", "Windows 任务栏", reason);
}

public interface ILocalStatusProbe
{
    Task<NetworkInfo> ReadNetworkAsync(CancellationToken cancellationToken);
    Task<WeatherInfo> ReadWeatherAsync(CancellationToken cancellationToken);
}

public static partial class WindowsWeatherText
{
    public static WeatherInfo Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 || !Temperature().IsMatch(text))
            return WeatherInfo.Unavailable("Windows 任务栏未提供天气文字");
        var summary = Regex.Replace(text, @"[\s\p{Cf}]+", " ").Trim();
        return new(true, summary, "Windows 任务栏", null);
    }

    [GeneratedRegex(@"[−-]?\d{1,3}(?:[.,]\d)?\s*(?:°\s*[CF]?|℃|℉|degrees?(?:\s*(?:celsius|fahrenheit))?|度)", RegexOptions.IgnoreCase)]
    private static partial Regex Temperature();
}

public sealed record LinkCounter(string Id, long ReceivedBytes, long SentBytes);
public sealed record TrafficRate(double? ReceivedBytesPerSecond, double? SentBytesPerSecond);

public sealed class TrafficSampler
{
    private Dictionary<string, LinkCounter> _previous = [];
    private double? _previousTime;

    public TrafficRate Sample(IReadOnlyList<LinkCounter> counters, double seconds)
    {
        double received = 0, sent = 0;
        var matched = 0;
        var elapsed = seconds - _previousTime;
        if (elapsed is > 0 && double.IsFinite(elapsed.Value))
        {
            foreach (var counter in counters)
            {
                if (!_previous.TryGetValue(counter.Id, out var before)) continue;
                matched++;
                // Adapter replacement and counter resets must not appear as huge traffic spikes.
                received += Math.Max(0, counter.ReceivedBytes - before.ReceivedBytes);
                sent += Math.Max(0, counter.SentBytes - before.SentBytes);
            }
        }
        _previous = counters.ToDictionary(counter => counter.Id, StringComparer.Ordinal);
        _previousTime = double.IsFinite(seconds) ? seconds : null;
        return matched == 0 ? new(null, null) : new(received / elapsed!.Value, sent / elapsed.Value);
    }
}

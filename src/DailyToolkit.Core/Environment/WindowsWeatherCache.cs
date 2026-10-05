using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DailyToolkit.Core.Environment;

public static class WindowsWeatherCache
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const string Source = "Windows 小组件缓存";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false, true);

    public static WeatherInfo Parse(ReadOnlySpan<byte> contents, DateTimeOffset now)
    {
        if (contents.Length is < 2 or > MaximumBytes) return Unavailable("Windows 天气缓存暂时不可读");
        try
        {
            // Windows also writes this diagnostic file as UTF-16 LE without a BOM.
            string json;
            if (contents.StartsWith(new byte[] { 0xff, 0xfe })) json = Utf16Le.GetString(contents[2..]);
            else if (contents.StartsWith(new byte[] { 0xfe, 0xff })) json = Utf16Be.GetString(contents[2..]);
            else if (contents[1] == 0) json = Utf16Le.GetString(contents);
            else if (contents[0] == 0) json = Utf16Be.GetString(contents);
            else json = Utf8.GetString(contents.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? contents[3..] : contents);

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            WeatherInfo? newest = null;
            if (Property(document.RootElement, "sections") is { ValueKind: JsonValueKind.Array } sections)
                ReadSections(sections, now, 0, ref newest);
            return newest ?? Unavailable("Windows 缓存里还没有可用的天气数据");
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return Unavailable("Windows 天气缓存暂时不可读");
        }
    }

    private static void ReadSections(JsonElement sections, DateTimeOffset now, int depth, ref WeatherInfo? newest)
    {
        if (depth > 8) return;
        foreach (var section in sections.EnumerateArray())
        {
            if (Property(section, "cards") is { ValueKind: JsonValueKind.Array } cards)
            {
                foreach (var card in cards.EnumerateArray())
                {
                    // Decode only the weather card. Other cards, authContext and service URLs are ignored.
                    if (Text(card, "type") != "WeatherSummary" || Text(card, "dataType") != "WeatherOverview") continue;
                    var data = Text(card, "data");
                    if (data is null || data.Length > 65536) continue;
                    var weather = ReadOverview(data, now);
                    if (weather is not null && (newest is null || weather.UpdatedAt > newest.UpdatedAt)) newest = weather;
                }
            }
            if (Property(section, "subSections") is { ValueKind: JsonValueKind.Array } nested)
                ReadSections(nested, now, depth + 1, ref newest);
        }
    }

    private static WeatherInfo? ReadOverview(string json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            var responses = Property(root, "responses");
            var response = First(responses);
            var current = Property(First(Property(response, "weather")), "current");
            var temperature = Property(current, "temp");
            var unit = Text(Property(root, "units"), "temperature") switch
            {
                "°C" or "℃" or "C" or "Celsius" => "°C",
                "°F" or "℉" or "F" or "Fahrenheit" => "°F",
                _ => null
            };
            if (unit is null || temperature.ValueKind != JsonValueKind.Number || !temperature.TryGetDouble(out var value) ||
                !double.IsFinite(value) || value < -150 || value > 200) return null;
            var stamp = Property(current, "created");
            var stampText = Text(current, "created");
            if (stampText is null || !(stampText.EndsWith('Z') ||
                (stampText.Length >= 6 && stampText[^6] is '+' or '-' && stampText[^3] == ':')) ||
                !stamp.TryGetDateTimeOffset(out var updated) || updated > now.AddMinutes(10)) return null;

            if (now - updated > TimeSpan.FromDays(1))
                return Unavailable("Windows 天气缓存已过期，等待系统更新") with { UpdatedAt = updated };

            var condition = Clean(Text(current, "cap"), 80);
            // The first response is the primary weather card. Avoid attaching a city from a multi-location response.
            var city = responses.GetArrayLength() == 1
                ? Clean(Text(Property(Property(root, "userProfile"), "location"), "City"), 80) : null;
            var summary = value.ToString("0.#", CultureInfo.InvariantCulture) + unit;
            if (condition is not null) summary += " · " + condition;
            return new(true, summary, Source, null) { Location = city, UpdatedAt = updated, FromCache = true };
        }
        catch (JsonException) { return null; }
    }

    private static string? Clean(string? value, int limit) => string.IsNullOrWhiteSpace(value) || value.Length > limit
        ? null : Regex.Replace(value, @"[\s\p{C}]+", " ").Trim();
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static string? Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    private static JsonElement First(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 ? value[0] : default;
    private static WeatherInfo Unavailable(string reason) => WeatherInfo.Unavailable(reason, Source) with { FromCache = true };
}

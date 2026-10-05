using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Tools;
using DailyToolkit.Core.Gaming;


namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterLocalStatusTests()
    {
        Test("Weather uses only valid Windows temperature labels", () =>
        {
            Require(WindowsWeatherText.Parse("天气，18°C，多云").Available);
            Require(WindowsWeatherText.Parse("Weather 65°F, Partly cloudy").Available);
            Require(WindowsWeatherText.Parse("−2 ℃ 晴").Available);
            Require(WindowsWeatherText.Parse("18 degrees Celsius Cloudy").Available);
            Require(!WindowsWeatherText.Parse("小组件").Available);
            Require(!WindowsWeatherText.Parse("Stock market 1234.5").Available);
            Require(!WindowsWeatherText.Parse(null).Available);
            Require(!WindowsWeatherText.Parse(new string('x', 513) + "18°C").Available);
        });

        var weatherNow = DateTimeOffset.Parse("2026-10-05T06:00:00Z");
        object WeatherCard(string unit = "°C", double temperature = 23, string? stamp = "2026-10-05T05:40:00Z",
            int responseCount = 1, string type = "WeatherSummary", bool broken = false) => new
        {
            type, dataType = "WeatherOverview",
            data = broken ? "{" : JsonSerializer.Serialize(new
            {
                units = new { temperature = unit },
                responses = Enumerable.Range(0, responseCount).Select(_ => new
                {
                    weather = new[] { new { current = new { temp = temperature, cap = "晴朗", created = stamp },
                        forecast = new { temp = 999 } } }
                }),
                userProfile = new { location = new { City = "测试城", Latitude = 12.3456, Longitude = 23.4567 },
                    sig = new { token = "PRIVATE-WEATHER-TOKEN" } }
            })
        };
        string WeatherFixture(params object[] cards) => JsonSerializer.Serialize(new
        {
            authContext = new { token = "PRIVATE-AUTH-TOKEN" },
            sections = new[] { new { subSections = new[] { new { cards } } } }
        });
        WeatherInfo ParseWeather(string json) => WindowsWeatherCache.Parse(Encoding.UTF8.GetBytes(json), weatherNow);

        Test("Windows weather cache reads current weather without exporting profile data", () =>
        {
            var weather = ParseWeather(WeatherFixture(WeatherCard()));
            Require(weather.Available && weather.Summary == "23°C · 晴朗" && weather.Location == "测试城");
            Require(weather.FromCache && weather.Source == WindowsWeatherCache.Source &&
                weather.UpdatedAt == DateTimeOffset.Parse("2026-10-05T05:40:00Z"));
            var exported = JsonSerializer.Serialize(weather);
            Require(!exported.Contains("PRIVATE") && !exported.Contains("12.3456") && !exported.Contains("23.4567"));
        });
        Test("Windows weather cache handles UTF-8 and UTF-16 with or without a BOM", () =>
        {
            var json = WeatherFixture(WeatherCard());
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true),
                new UnicodeEncoding(false, false), new UnicodeEncoding(false, true),
                new UnicodeEncoding(true, false), new UnicodeEncoding(true, true) })
            {
                var contents = encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray();
                Require(WindowsWeatherCache.Parse(contents, weatherNow).Summary == "23°C · 晴朗");
            }
        });
        Test("Weather cache keeps explicit temperature units and handles multiple locations", () =>
        {
            Require(ParseWeather(WeatherFixture(WeatherCard("°F", 73.4))).Summary == "73.4°F · 晴朗");
            var multiple = ParseWeather(WeatherFixture(WeatherCard(responseCount: 2)));
            Require(multiple.Available && multiple.Location is null);
            Require(!ParseWeather(WeatherFixture(WeatherCard("unknown"))).Available);
            Require(!ParseWeather(WeatherFixture(WeatherCard(temperature: 1000))).Available);
        });
        Test("Weather cache rejects expired and future observations and missing timestamps", () =>
        {
            var expired = ParseWeather(WeatherFixture(WeatherCard(stamp: "2026-10-04T05:00:00Z")));
            Require(!expired.Available && expired.Reason!.Contains("过期") && expired.UpdatedAt.HasValue);
            Require(!ParseWeather(WeatherFixture(WeatherCard(stamp: "2026-10-05T07:00:00Z"))).Available);
            Require(!ParseWeather(WeatherFixture(WeatherCard(stamp: null))).Available);
            Require(!ParseWeather(WeatherFixture(WeatherCard(stamp: "2026-10-05T05:40:00"))).Available);
        });
        Test("Weather cache skips broken cards and chooses the newest weather observation", () =>
        {
            var weather = ParseWeather(WeatherFixture(WeatherCard(broken: true),
                WeatherCard(temperature: 10, stamp: "2026-10-05T04:00:00Z"), WeatherCard()));
            Require(weather.Available && weather.Summary == "23°C · 晴朗");
            var stillValid = ParseWeather(WeatherFixture(WeatherCard(stamp: "2026-10-05T07:00:00Z"), WeatherCard()));
            Require(stillValid.Available);
        });
        Test("Only the Windows weather card is decoded, without using news or arbitrary temperatures", () =>
        {
            Require(!ParseWeather(WeatherFixture(WeatherCard(type: "Finance"))).Available);
            Require(!ParseWeather("{\"temp\":23,\"unit\":\"°C\",\"sections\":[]}").Available);
        });
        Test("Weather cache rejects partial, invalid, and oversized files", () =>
        {
            Require(!ParseWeather("{").Available);
            Require(!WindowsWeatherCache.Parse(new byte[] { 0xff, 0xfe, 0x7b }, weatherNow).Available);
            Require(!WindowsWeatherCache.Parse(new byte[WindowsWeatherCache.MaximumBytes + 1], weatherNow).Available);
            Require(!ParseWeather("{\"sections\":null}").Available);
        });
        Test("Traffic rates survive adapter changes and resets", () =>
        {
            var sampler = new TrafficSampler();
            Require(sampler.Sample([new("wifi", 1000, 500)], 1).ReceivedBytesPerSecond is null);
            var rate = sampler.Sample([new("wifi", 3000, 1000)], 3);
            Require(rate.ReceivedBytesPerSecond == 1000 && rate.SentBytesPerSecond == 250);
            var changed = sampler.Sample([new("vpn", 50000000, 50000000)], 4);
            Require(changed.ReceivedBytesPerSecond is null);
            var reset = sampler.Sample([new("vpn", 0, 0)], 5);
            Require(reset.ReceivedBytesPerSecond == 0 && reset.SentBytesPerSecond == 0);
            Require(sampler.Sample([new("vpn", 100, 100)], 5).ReceivedBytesPerSecond is null);
            Require(sampler.Sample([], 6).ReceivedBytesPerSecond is null);
        });
    }
}

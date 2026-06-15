using System.Globalization;
using System.Text.Json;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Weather;

public sealed class MetServiceForecastService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<MetServiceForecastService> logger) : IWeatherForecastService
{
    private static readonly string[] Variables =
    [
        "air.temperature.at-2m",
        "precipitation.rate",
        "wind.speed.at-10m",
        "wind.speed.gust.at-10m",
        "cloud.cover"
    ];

    private readonly string _apiKey = configuration["MetService:ApiKey"] ?? string.Empty;
    private readonly string _baseUrl = configuration["MetService:BaseUrl"] ?? "https://forecast-v2.metoceanapi.com";

    public async Task<WeatherForecastSummary> GetNext24HoursAsync(
        decimal latitude,
        decimal longitude,
        string locationName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Unavailable(locationName, "MetService API key is not configured.");
        }

        try
        {
            var from = DateTime.UtcNow.AddMinutes(-DateTime.UtcNow.Minute).AddSeconds(-DateTime.UtcNow.Second).AddMilliseconds(-DateTime.UtcNow.Millisecond);
            var url = string.Create(CultureInfo.InvariantCulture, $"{_baseUrl.TrimEnd('/')}/point/time?lon={longitude}&lat={latitude}&variables={string.Join(',', Variables)}&from={from:yyyy-MM-ddTHH\\:mm\\:ssZ}&interval=1h&repeat=24");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("accept", "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(locationName, $"MetService returned {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var times = ReadTimes(root);
            var temp = ReadSeries(root, "air.temperature.at-2m");
            var rain = ReadSeries(root, "precipitation.rate");
            var wind = ReadSeries(root, "wind.speed.at-10m");
            var gust = ReadSeries(root, "wind.speed.gust.at-10m");
            var cloud = ReadSeries(root, "cloud.cover");
            var count = Math.Min(24, times.Count);
            var hours = new List<WeatherForecastHour>(count);

            for (var index = 0; index < count; index++)
            {
                var windMs = ValueAt(wind, index) ?? ValueAt(gust, index);
                hours.Add(new WeatherForecastHour
                {
                    TimeUtc = times[index],
                    TemperatureC = KelvinToCelsius(ValueAt(temp, index)),
                    RainMmPerHour = Round(ValueAt(rain, index)),
                    WindKmh = Round(windMs * 3.6m),
                    CloudCoverPercent = Round(ValueAt(cloud, index))
                });
            }

            var maxRain = hours.Select(item => item.RainMmPerHour).Where(value => value.HasValue).DefaultIfEmpty(0).Max();
            var maxWind = hours.Select(item => item.WindKmh).Where(value => value.HasValue).DefaultIfEmpty(0).Max();
            var avgCloud = hours.Select(item => item.CloudCoverPercent).Where(value => value.HasValue).DefaultIfEmpty(0).Average();
            var condition = Classify(maxRain, maxWind, avgCloud);

            return new WeatherForecastSummary
            {
                IsAvailable = hours.Count > 0,
                LocationName = locationName,
                Condition = condition.Label,
                ConditionClass = condition.ClassName,
                Message = hours.Count > 0 ? "Next 24 hours" : "No forecast hours returned.",
                CurrentTemperatureC = hours.FirstOrDefault()?.TemperatureC,
                MaxRainMmPerHour = maxRain,
                MaxWindKmh = maxWind,
                Hours = hours
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to load MetService forecast for {Location}.", locationName);
            return Unavailable(locationName, "Unable to load MetService forecast.");
        }
    }

    private static WeatherForecastSummary Unavailable(string locationName, string message)
    {
        return new WeatherForecastSummary
        {
            LocationName = locationName,
            Message = message
        };
    }

    private static IReadOnlyList<DateTime> ReadTimes(JsonElement root)
    {
        if (!root.TryGetProperty("dimensions", out var dimensions) ||
            !dimensions.TryGetProperty("time", out var time) ||
            !time.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => DateTime.TryParse(item.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue)
            .Where(item => item != DateTime.MinValue)
            .ToList();
    }

    private static IReadOnlyList<decimal?> ReadSeries(JsonElement root, string variable)
    {
        if (!root.TryGetProperty("variables", out var variables) ||
            !variables.TryGetProperty(variable, out var variableElement) ||
            !variableElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray().Select(ReadDecimal).ToList();
    }

    private static decimal? ReadDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value))
        {
            return value;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nestedValue = ReadDecimal(child);
                if (nestedValue.HasValue)
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }

    private static decimal? ValueAt(IReadOnlyList<decimal?> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : null;
    }

    private static decimal? KelvinToCelsius(decimal? kelvin)
    {
        return kelvin.HasValue ? Round(kelvin.Value - 273.15m) : null;
    }

    private static decimal? Round(decimal? value)
    {
        return value.HasValue ? Math.Round(value.Value, 1) : null;
    }

    private static (string Label, string ClassName) Classify(decimal? maxRain, decimal? maxWind, decimal? avgCloud)
    {
        if (maxWind >= 45)
        {
            return ("Windy", "weather-wind");
        }

        if (maxRain >= 0.5m)
        {
            return ("Rain", "weather-rain");
        }

        if (avgCloud >= 65)
        {
            return ("Cloudy", "weather-cloud");
        }

        return ("Clear", "weather-clear");
    }
}

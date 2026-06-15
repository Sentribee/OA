namespace SentribeeConsole.Web.Domain.Entities;

public sealed record WeatherForecastSummary
{
    public bool IsAvailable { get; init; }

    public string LocationName { get; init; } = "Weather";

    public string Provider { get; init; } = "MetService";

    public string Condition { get; init; } = "Unavailable";

    public string ConditionClass { get; init; } = "weather-unavailable";

    public string Message { get; init; } = "Weather forecast is not available.";

    public decimal? CurrentTemperatureC { get; init; }

    public decimal? MaxRainMmPerHour { get; init; }

    public decimal? MaxWindKmh { get; init; }

    public IReadOnlyList<WeatherForecastHour> Hours { get; init; } = [];
}

public sealed record WeatherForecastHour
{
    public DateTime TimeUtc { get; init; }

    public decimal? TemperatureC { get; init; }

    public decimal? RainMmPerHour { get; init; }

    public decimal? WindKmh { get; init; }

    public decimal? CloudCoverPercent { get; init; }
}

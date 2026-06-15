using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IWeatherForecastService
{
    Task<WeatherForecastSummary> GetNext24HoursAsync(
        decimal latitude,
        decimal longitude,
        string locationName,
        CancellationToken cancellationToken);
}

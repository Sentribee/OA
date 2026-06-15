namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeDeviceEndpoint
{
    public int Id { get; init; }

    public int EdgeDeviceId { get; init; }

    public int? CatalogDeviceId { get; init; }

    public string DeviceName { get; init; } = string.Empty;

    public string AccessUrl { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }
}

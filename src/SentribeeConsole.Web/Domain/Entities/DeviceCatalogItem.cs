namespace SentribeeConsole.Web.Domain.Entities;

public sealed record DeviceCatalogItem
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }
}

namespace SentribeeConsole.Web.Domain.Entities;

public sealed record YoloModelVersion
{
    public int Id { get; init; }

    public int ProjectId { get; init; }

    public string VersionName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public string? ModelFileUrl { get; init; }

    public string? YamlDescription { get; init; }

    public bool IsCurrent { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime? TrainedAtUtc { get; init; }
}

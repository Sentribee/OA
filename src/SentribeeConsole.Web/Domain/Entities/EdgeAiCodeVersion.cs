namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeAiCodeVersion
{
    public int Id { get; init; }

    public int LogicId { get; init; }

    public string VersionName { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsCurrent { get; init; }

    public long? PackageSizeBytes { get; init; }

    public int? FileCount { get; init; }

    public string DirectoryStructure { get; init; } = string.Empty;

    public string FeatureList { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}

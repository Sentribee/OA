namespace SentribeeConsole.Web.Domain.Entities;

public sealed record ServerResourceSnapshot
{
    public string InstanceName { get; init; } = string.Empty;

    public string PublicDomain { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Status { get; init; } = "Available";

    public int Capacity { get; init; }

    public int UsedInstances { get; init; }

    public string Description { get; init; } = string.Empty;

    public string? InstanceType { get; init; }

    public string? Region { get; init; }

    public string? AvailabilityZone { get; init; }

    public string? PublicIpAddress { get; init; }

    public string? PrivateIpAddress { get; init; }

    public string? AmiId { get; init; }

    public string? AccountId { get; init; }

    public string? GpuSummary { get; init; }

    public string? MemorySummary { get; init; }

    public string? DiskSummary { get; init; }

    public string? LoadSummary { get; init; }

    public int LoadPercent { get; init; }

    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public string MetadataStatus { get; init; } = "AWS metadata not available";

    public int AvailableInstances => Math.Max(0, Capacity - UsedInstances);

    public int UsagePercent => Capacity <= 0
        ? 0
        : Math.Clamp((int)Math.Round(UsedInstances * 100d / Capacity), 0, 100);
}

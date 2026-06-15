namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeDevice
{
    public int Id { get; init; }

    public int ProjectId { get; init; }

    public int AdminId { get; init; }

    public string DeviceCode { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    public string? GooglePlaceId { get; init; }

    public string? StreetViewThumbnailUrl { get; init; }

    public string IpAddress { get; init; } = string.Empty;

    public string? ServerResourceInstanceName { get; init; }

    public string? Description { get; init; }

    public string? RuntimeStatus { get; init; }

    public string? DeviceStatus { get; init; }

    public string? HeartbeatDetailJson { get; init; }

    public DateTime? LastHeartbeatAtUtc { get; init; }

    public bool IsOnline => LastHeartbeatAtUtc.HasValue &&
        DateTime.UtcNow - LastHeartbeatAtUtc.Value <= TimeSpan.FromSeconds(90);

    public bool NeedsRemoteDeviceRepair => IsOnline && IsRemoteDeviceOfflineStatus(DeviceStatus);

    private static bool IsRemoteDeviceOfflineStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Equals("Remote Device Offline", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Offline", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("not open", StringComparison.OrdinalIgnoreCase);
    }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; init; }

    public IReadOnlyList<EdgeDeviceEndpoint> Endpoints { get; init; } = [];

    public IReadOnlyList<EdgeEvent> Events { get; init; } = [];
}

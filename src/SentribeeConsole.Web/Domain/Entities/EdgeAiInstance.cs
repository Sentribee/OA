namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeAiInstance
{
    public int Id { get; init; }

    public int LogicId { get; init; }

    public int EdgeDeviceId { get; init; }

    public string EdgeDeviceName { get; init; } = string.Empty;

    public int? CodeVersionId { get; init; }

    public string? CodeVersionName { get; init; }

    public string InstanceName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string RuntimeStatus { get; init; } = "Pending";

    public DateTime CreatedAtUtc { get; init; }
}

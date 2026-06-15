namespace SentribeeConsole.Web.Domain.Entities;

public sealed record YoloTrainingSchedule
{
    public int ProjectId { get; init; }

    public DateTime? NextTrainingAtUtc { get; init; }

    public bool AutoSchedule { get; init; }

    public DateTime UpdatedAtUtc { get; init; }
}

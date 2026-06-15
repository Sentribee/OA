namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeEvent
{
    public int Id { get; init; }

    public int EdgeDeviceId { get; init; }

    public string EdgeDeviceName { get; init; } = string.Empty;

    public string EdgeDeviceCode { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? ImageUrl { get; init; }

    public DateTime EventTimeUtc { get; init; }

    public string Status { get; init; } = "Ordinary Risk";

    public string LearningStatus { get; init; } = "None";

    public string? AnnotationJson { get; init; }

    public string? YoloLabelUrl { get; init; }

    public string? PpeReviewJson { get; init; }

    public string? VideoUrl { get; init; }
}

public sealed record EdgeEventStatusCounts
{
    public int InvalidEvent { get; init; }

    public int SevereDanger { get; init; }

    public int OrdinaryRisk { get; init; }

    public int NoRisk { get; init; }

    public int RealRisk { get; init; }

    public int PendingReview { get; init; }

    public int PendingLearning { get; init; }

    public int Trained { get; init; }
}

public sealed record EdgeEventFilters
{
    public int? DeviceId { get; init; }

    public string? Type { get; init; }

    public string? Status { get; init; }

    public string? LearningStatus { get; init; }

    public DateTime? DateFrom { get; init; }

    public DateTime? DateTo { get; init; }
}

public sealed record EdgeEventSubject
{
    public long Id { get; init; }

    public int EdgeEventId { get; init; }

    public int EdgeDeviceId { get; init; }

    public string EdgeDeviceName { get; init; } = string.Empty;

    public string EdgeDeviceCode { get; init; } = string.Empty;

    public string EventTitle { get; init; } = string.Empty;

    public string EventStatus { get; init; } = string.Empty;

    public string EventLearningStatus { get; init; } = "None";

    public string LearningStatus { get; init; } = "None";

    public DateTime EventTimeUtc { get; init; }

    public string SubjectKey { get; init; } = string.Empty;

    public string SubjectType { get; init; } = "Person";

    public string? TrackingLabel { get; init; }

    public string? CropImageUrl { get; init; }

    public string? PreviewImageUrl { get; init; }

    public string? BoundingBoxJson { get; init; }

    public string? PpeBoxJson { get; init; }

    public string? PpeStatusJson { get; init; }

    public bool IsRisk { get; init; }

    public string? RiskCategory { get; init; }

    public string? RiskSeverity { get; init; }

    public string? RiskReason { get; init; }
}

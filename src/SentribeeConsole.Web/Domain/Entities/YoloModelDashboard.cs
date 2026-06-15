namespace SentribeeConsole.Web.Domain.Entities;

public sealed record YoloModelDashboard
{
    public Project Project { get; init; } = new();

    public YoloModelVersion? CurrentVersion { get; init; }

    public IReadOnlyList<YoloModelVersion> Versions { get; init; } = [];

    public PagedResult<EdgeEvent> PendingTrainingEvents { get; init; } = new();

    public PagedResult<EdgeEventSubject> PendingTrainingSubjects { get; init; } = new();

    public int PendingLearningCount { get; init; }

    public int PendingSubjectLearningCount { get; init; }

    public YoloTrainingSchedule? Schedule { get; init; }

    public string ModelYamlPath { get; init; } = Project.DefaultAiModelYamlPath;

    public string PersonPpeModelYamlPath { get; init; } = Project.DefaultPersonPpeModelYamlPath;

    public string? ModelYamlContent { get; init; }

    public IReadOnlyList<YoloModelClass> ModelClasses { get; init; } = [];

    public string? PersonPpeModelYamlContent { get; init; }

    public IReadOnlyList<YoloModelClass> PersonPpeModelClasses { get; init; } = [];
}

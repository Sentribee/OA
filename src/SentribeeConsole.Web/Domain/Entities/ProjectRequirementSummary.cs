namespace SentribeeConsole.Web.Domain.Entities;

public sealed record ProjectRequirementSummary
{
    public IReadOnlyList<ProjectRule> EnvironmentRecognition { get; init; } = [];

    public IReadOnlyList<ProjectRule> LogicRequirements { get; init; } = [];

    public IReadOnlyList<ProjectRule> EventRecognition { get; init; } = [];

    public IReadOnlyList<ProjectRule> ResponseMethods { get; init; } = [];

    public IReadOnlyList<ProjectRule> OtherRequirements { get; init; } = [];
}

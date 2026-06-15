namespace SentribeeConsole.Web.Domain.Entities;

public sealed class ProjectRule
{
    public int Id { get; init; }

    public int ProjectId { get; init; }

    public int? EdgeAiCodeVersionId { get; init; }

    public string ChangeType { get; init; } = "Active";

    public string Dimension { get; init; } = string.Empty;

    public string RuleText { get; init; } = string.Empty;

    public string? SourcePrompt { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}

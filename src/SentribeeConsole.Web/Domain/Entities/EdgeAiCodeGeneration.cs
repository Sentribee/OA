namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeAiCodeGeneration
{
    public int Id { get; init; }

    public int ProjectId { get; init; }

    public int LogicId { get; init; }

    public string BranchName { get; init; } = string.Empty;

    public string VersionName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public int ProgressPercent { get; init; }

    public string? HandoffCommitSha { get; init; }

    public string? GeneratedCommitSha { get; init; }

    public string? StatusMessage { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; init; }
}

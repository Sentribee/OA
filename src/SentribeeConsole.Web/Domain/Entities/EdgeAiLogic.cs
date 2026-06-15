namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeAiLogic
{
    public int Id { get; init; }

    public int ProjectId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public IReadOnlyList<EdgeAiCodeVersion> Versions { get; init; } = [];

    public IReadOnlyList<EdgeAiInstance> Instances { get; init; } = [];
}

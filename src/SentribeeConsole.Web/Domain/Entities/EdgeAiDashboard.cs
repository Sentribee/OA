namespace SentribeeConsole.Web.Domain.Entities;

public sealed record EdgeAiDashboard
{
    public Project Project { get; init; } = new();

    public IReadOnlyList<EdgeAiLogic> Logics { get; init; } = [];

    public IReadOnlyList<EdgeDevice> EdgeDevices { get; init; } = [];

    public ProjectRequirementSummary Requirements { get; init; } = new();

    public EdgeAiCodeVersion? CurrentVersion { get; init; }

    public int DailyGitHandoffLimit { get; init; } = 10;

    public int DailyGitHandoffUsed { get; init; }

    public EdgeAiCodeGeneration? ActiveGeneration { get; init; }
}

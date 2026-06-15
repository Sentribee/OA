using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public sealed class ProjectRequirementService : IProjectRequirementService
{
    public ProjectRequirementSummary Summarize(Project project)
    {
        return Summarize(project, currentVersion: null);
    }

    public ProjectRequirementSummary Summarize(Project project, EdgeAiCodeVersion? currentVersion)
    {
        var rules = SelectRules(project, currentVersion);
        return new ProjectRequirementSummary
        {
            EnvironmentRecognition = MatchDimension(rules, "Environment Recognition"),
            LogicRequirements = MatchDimension(rules, "Recognition Logic"),
            EventRecognition = MatchDimension(rules, "Event Recognition"),
            ResponseMethods = MatchDimension(rules, "Response Method"),
            OtherRequirements = rules
                .Where(rule => !MatchedDimension(rule, "Environment Recognition") &&
                    !MatchedDimension(rule, "Recognition Logic") &&
                    !MatchedDimension(rule, "Event Recognition") &&
                    !MatchedDimension(rule, "Response Method"))
                .ToList()
        };
    }

    private static IReadOnlyList<ProjectRule> SelectRules(Project project, EdgeAiCodeVersion? currentVersion)
    {
        if (currentVersion is null)
        {
            return project.Rules
                .Where(rule => string.Equals(rule.ChangeType, "Active", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rule.ChangeType, "Added", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var versionRules = project.Rules
            .Where(rule => rule.EdgeAiCodeVersionId == currentVersion.Id)
            .Where(rule => string.Equals(rule.ChangeType, "Active", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return versionRules.Count > 0
            ? versionRules
            : project.Rules
                .Where(rule => rule.EdgeAiCodeVersionId is null)
                .Where(rule => string.Equals(rule.ChangeType, "Active", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rule.ChangeType, "Added", StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private static IReadOnlyList<ProjectRule> MatchDimension(IReadOnlyList<ProjectRule> rules, string dimension)
    {
        return rules.Where(rule => MatchedDimension(rule, dimension)).ToList();
    }

    private static bool MatchedDimension(ProjectRule rule, string dimension)
    {
        return string.Equals(rule.Dimension, dimension, StringComparison.OrdinalIgnoreCase);
    }
}

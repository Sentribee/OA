using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IProjectRequirementService
{
    ProjectRequirementSummary Summarize(Project project);

    ProjectRequirementSummary Summarize(Project project, EdgeAiCodeVersion? currentVersion);
}

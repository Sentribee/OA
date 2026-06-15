namespace SentribeeConsole.Web.Application.Contracts;

public interface IProjectRuleGenerator
{
    Task<IReadOnlyList<GeneratedProjectRule>> GenerateAsync(
        string projectName,
        string? projectDescription,
        string prompt,
        CancellationToken cancellationToken);
}

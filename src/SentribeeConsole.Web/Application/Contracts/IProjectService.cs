using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IProjectService
{
    Task<Project?> GetByAdminIdAsync(int adminId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Project>> ListForAdminAsync(int adminId, CancellationToken cancellationToken);

    Task<bool> SwitchCurrentProjectAsync(int adminId, int projectId, CancellationToken cancellationToken);

    Task<Project> SaveAsync(
        int adminId,
        string name,
        string? description,
        string? companyName,
        string? websiteUrl,
        string timeZoneId,
        string? edgeAiGitRepositoryUrl,
        string edgeAiGitBranch,
        string? edgeAiGitWorkingDirectory,
        CancellationToken cancellationToken);

    Task<Project?> UpdateLogoAsync(int adminId, string logoUrl, CancellationToken cancellationToken);

    Task<GeneratedApiKey> GenerateApiKeyAsync(int adminId, CancellationToken cancellationToken);

    Task<Project> GenerateRulesAsync(int adminId, string prompt, CancellationToken cancellationToken);

    Task<bool> DeleteRuleAsync(int adminId, int ruleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectMember>> ListMembersAsync(int adminId, CancellationToken cancellationToken);

    Task<InvitedProjectUser> InviteMemberAsync(
        int adminId,
        string email,
        CancellationToken cancellationToken);

    Task<bool> UpdateMemberRoleAsync(
        int adminId,
        int memberAdminId,
        string role,
        CancellationToken cancellationToken);

    Task<bool> DeleteMemberAsync(int adminId, int memberAdminId, CancellationToken cancellationToken);

    Task<ProjectInvitation?> FindInvitationAsync(string token, CancellationToken cancellationToken);

    Task<bool> AcceptInvitationAsync(string token, string password, CancellationToken cancellationToken);
}

public sealed record GeneratedApiKey(string ApiKey, Project Project);

public sealed record InvitedProjectUser(ProjectMember Member, string InvitationUrl, ConsoleEmailResult EmailResult);

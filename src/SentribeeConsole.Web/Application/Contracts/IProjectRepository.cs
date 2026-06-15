using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IProjectRepository
{
    Task<Project?> FindByAdminIdAsync(int adminId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Project>> ListForAdminAsync(int adminId, CancellationToken cancellationToken);

    Task<bool> SetCurrentProjectAsync(int adminId, int projectId, CancellationToken cancellationToken);

    Task<Project> UpsertAsync(
        int adminId,
        string name,
        string? description,
        string? companyName,
        string? websiteUrl,
        string timeZoneId,
        string edgeAiGitRepositoryUrl,
        string edgeAiGitBranch,
        string? edgeAiGitWorkingDirectory,
        CancellationToken cancellationToken);

    Task<Project?> UpdateLogoAsync(int adminId, string logoUrl, CancellationToken cancellationToken);

    Task<Project?> UpdateApiKeyAsync(
        int adminId,
        string apiKeyHash,
        string apiKeyPrefix,
        CancellationToken cancellationToken);

    Task AddRulesAsync(
        int projectId,
        string sourcePrompt,
        IReadOnlyList<GeneratedProjectRule> rules,
        CancellationToken cancellationToken);

    Task<bool> DeleteRuleAsync(int adminId, int ruleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectMember>> ListMembersAsync(int adminId, CancellationToken cancellationToken);

    Task<ProjectMember?> InviteMemberAsync(
        int actorAdminId,
        string email,
        string role,
        string passwordHash,
        string invitationTokenHash,
        DateTime invitationSentAtUtc,
        DateTime invitationExpiresAtUtc,
        CancellationToken cancellationToken);

    Task<bool> UpdateMemberRoleAsync(
        int actorAdminId,
        int memberAdminId,
        string role,
        CancellationToken cancellationToken);

    Task<bool> DeleteMemberAsync(int actorAdminId, int memberAdminId, CancellationToken cancellationToken);

    Task<ProjectInvitation?> FindInvitationByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<bool> AcceptInvitationAsync(
        string tokenHash,
        string passwordHash,
        CancellationToken cancellationToken);
}

public sealed record GeneratedProjectRule(string Dimension, string RuleText);

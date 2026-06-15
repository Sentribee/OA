using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SentribeeConsole.Web.Application.Services;

public sealed class ProjectService(
    IProjectRepository repository,
    IProjectRuleGenerator ruleGenerator,
    IAdminAuthenticationService authenticationService,
    IConsoleEmailService emailService,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration) : IProjectService
{
    public Task<Project?> GetByAdminIdAsync(int adminId, CancellationToken cancellationToken)
    {
        return repository.FindByAdminIdAsync(adminId, cancellationToken);
    }

    public Task<IReadOnlyList<Project>> ListForAdminAsync(int adminId, CancellationToken cancellationToken)
    {
        return repository.ListForAdminAsync(adminId, cancellationToken);
    }

    public Task<bool> SwitchCurrentProjectAsync(int adminId, int projectId, CancellationToken cancellationToken)
    {
        return repository.SetCurrentProjectAsync(adminId, projectId, cancellationToken);
    }

    public Task<Project> SaveAsync(
        int adminId,
        string name,
        string? description,
        string? companyName,
        string? websiteUrl,
        string timeZoneId,
        string? edgeAiGitRepositoryUrl,
        string edgeAiGitBranch,
        string? edgeAiGitWorkingDirectory,
        CancellationToken cancellationToken)
    {
        return repository.UpsertAsync(
            adminId,
            name.Trim(),
            NormalizeOptional(description),
            NormalizeOptional(companyName),
            NormalizeOptional(websiteUrl),
            ProjectTimeZone.Normalize(timeZoneId),
            NormalizeGitRepositoryUrl(edgeAiGitRepositoryUrl),
            NormalizeGitBranch(edgeAiGitBranch),
            NormalizeOptional(edgeAiGitWorkingDirectory),
            cancellationToken);
    }

    public Task<Project?> UpdateLogoAsync(
        int adminId,
        string logoUrl,
        CancellationToken cancellationToken)
    {
        return repository.UpdateLogoAsync(adminId, logoUrl, cancellationToken);
    }

    public async Task<GeneratedApiKey> GenerateApiKeyAsync(int adminId, CancellationToken cancellationToken)
    {
        var apiKey = $"sb_live_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var project = await repository.UpdateApiKeyAsync(
            adminId,
            HashApiKey(apiKey),
            apiKey[..16],
            cancellationToken)
            ?? throw new InvalidOperationException("Save the project before generating an API key.");
        return new GeneratedApiKey(apiKey, project);
    }

    public async Task<Project> GenerateRulesAsync(
        int adminId,
        string prompt,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Save the project before generating rules.");
        var rules = await ruleGenerator.GenerateAsync(
            project.Name,
            project.Description,
            prompt.Trim(),
            cancellationToken);
        if (rules.Count == 0)
        {
            throw new InvalidOperationException("No project rules were generated.");
        }

        await repository.AddRulesAsync(project.Id, prompt.Trim(), rules, cancellationToken);
        return await repository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Unable to reload the project.");
    }

    public Task<bool> DeleteRuleAsync(int adminId, int ruleId, CancellationToken cancellationToken)
    {
        return repository.DeleteRuleAsync(adminId, ruleId, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(int adminId, CancellationToken cancellationToken)
    {
        return repository.ListMembersAsync(adminId, cancellationToken);
    }

    public async Task<InvitedProjectUser> InviteMemberAsync(
        int adminId,
        string email,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var project = await repository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Save the project before inviting users.");
        if (!project.CanAdministerUsers)
        {
            throw new InvalidOperationException("Only project administrators can invite users.");
        }

        var token = GenerateInvitationToken();
        var now = DateTime.UtcNow;
        var passwordHash = authenticationService.HashPassword(
            new AdminUser { LoginId = normalizedEmail, Email = normalizedEmail },
            GenerateTemporaryPassword());
        var member = await repository.InviteMemberAsync(
            adminId,
            normalizedEmail,
            ProjectRoles.ReadOnly,
            passwordHash,
            HashSecret(token),
            now,
            now.AddDays(7),
            cancellationToken)
            ?? throw new InvalidOperationException("This user cannot be invited to the current project.");
        var invitationUrl = BuildInvitationUrl(token);
        var emailResult = await emailService.SendProjectInvitationAsync(
            normalizedEmail,
            project.Name,
            invitationUrl,
            cancellationToken);
        return new InvitedProjectUser(member, invitationUrl, emailResult);
    }

    public Task<bool> UpdateMemberRoleAsync(
        int adminId,
        int memberAdminId,
        string role,
        CancellationToken cancellationToken)
    {
        return repository.UpdateMemberRoleAsync(adminId, memberAdminId, ProjectRoles.Normalize(role), cancellationToken);
    }

    public Task<bool> DeleteMemberAsync(int adminId, int memberAdminId, CancellationToken cancellationToken)
    {
        return repository.DeleteMemberAsync(adminId, memberAdminId, cancellationToken);
    }

    public Task<ProjectInvitation?> FindInvitationAsync(string token, CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(token)
            ? Task.FromResult<ProjectInvitation?>(null)
            : repository.FindInvitationByTokenHashAsync(HashSecret(token), cancellationToken);
    }

    public async Task<bool> AcceptInvitationAsync(
        string token,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var invitation = await FindInvitationAsync(token, cancellationToken);
        if (invitation is null || !invitation.IsActive)
        {
            return false;
        }

        var passwordHash = authenticationService.HashPassword(
            new AdminUser
            {
                Id = invitation.AdminId,
                LoginId = invitation.Email,
                Email = invitation.Email
            },
            password);
        return await repository.AcceptInvitationAsync(HashSecret(token), passwordHash, cancellationToken);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeGitRepositoryUrl(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Project.DefaultEdgeAiGitRepositoryUrl
            : value.Trim();
    }

    private static string NormalizeGitBranch(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Project.DefaultEdgeAiGitBranch
            : value.Trim();
    }

    private static string HashApiKey(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashSecret(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string BuildInvitationUrl(string token)
    {
        var configuredBaseUrl = configuration["AppApi:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return $"{configuredBaseUrl.TrimEnd('/')}/invite/{Uri.EscapeDataString(token)}";
        }

        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null)
        {
            return $"/invite/{Uri.EscapeDataString(token)}";
        }

        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
        return $"{scheme}://{host}/invite/{Uri.EscapeDataString(token)}";
    }

    private static string GenerateInvitationToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string GenerateTemporaryPassword()
    {
        return $"Sb-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}!";
    }
}

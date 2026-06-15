using System.Data;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Repositories;

public sealed class ProjectRepository(
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor) : IProjectRepository
{
    private const string CurrentProjectSessionKey = "CurrentProjectId";
    private static readonly SemaphoreSlim InvitationSchemaLock = new(1, 1);
    private static bool _invitationSchemaEnsured;

    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection.");

    public async Task<Project?> FindByAdminIdAsync(int adminId, CancellationToken cancellationToken)
    {
        var selectedProjectId = httpContextAccessor.HttpContext?.Session.GetInt32(CurrentProjectSessionKey);
        const string projectSql = """
            SELECT id, AdminId, ProjectName, ProjectDescription, LogoUrl,
                CompanyName, WebsiteUrl, ProjectKind, Visibility, TimeZoneId, EdgeAiGitRepositoryUrl, EdgeAiGitBranch,
                EdgeAiGitWorkingDirectory, AiModelYamlPath, PersonPpeModelYamlPath, ApiKeyPrefix, ApiKeyCreatedAtUtc,
                CreatedAtUtc, UpdatedAtUtc, AccessRole
            FROM (
                SELECT project.id, project.AdminId, project.ProjectName, project.ProjectDescription, project.LogoUrl,
                    project.CompanyName, project.WebsiteUrl, project.ProjectKind, project.Visibility, project.TimeZoneId, project.EdgeAiGitRepositoryUrl, project.EdgeAiGitBranch,
                    project.EdgeAiGitWorkingDirectory, project.AiModelYamlPath, project.PersonPpeModelYamlPath,
                    project.ApiKeyPrefix, project.ApiKeyCreatedAtUtc,
                    project.CreatedAtUtc, project.UpdatedAtUtc,
                    CASE
                        WHEN project.AdminId = @AdminId THEN 'Administrator'
                        ELSE membership.Role
                    END AS AccessRole,
                    CASE WHEN project.id = @SelectedProjectId THEN 0 ELSE 1 END AS SortSelected
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            ) AS accessible_projects
            ORDER BY SortSelected, CreatedAtUtc, id
            LIMIT 1;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var projectCommand = new MySqlCommand(projectSql, connection);
        projectCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        projectCommand.Parameters.Add("@SelectedProjectId", MySqlDbType.Int32).Value =
            (object?)selectedProjectId ?? DBNull.Value;
        await using var reader = await projectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var project = MapProject(reader);
        await reader.CloseAsync();
        httpContextAccessor.HttpContext?.Session.SetInt32(CurrentProjectSessionKey, project.Id);
        return project with { Rules = await LoadRulesAsync(connection, project.Id, cancellationToken) };
    }

    public async Task<IReadOnlyList<Project>> ListForAdminAsync(int adminId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT project.id, project.AdminId, project.ProjectName, project.ProjectDescription, project.LogoUrl,
                project.CompanyName, project.WebsiteUrl, project.ProjectKind, project.Visibility, project.TimeZoneId, project.EdgeAiGitRepositoryUrl, project.EdgeAiGitBranch,
                project.EdgeAiGitWorkingDirectory, project.AiModelYamlPath, project.PersonPpeModelYamlPath,
                project.ApiKeyPrefix, project.ApiKeyCreatedAtUtc,
                project.CreatedAtUtc, project.UpdatedAtUtc,
                CASE
                    WHEN project.AdminId = @AdminId THEN 'Administrator'
                    ELSE membership.Role
                END AS AccessRole
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            ORDER BY project.ProjectName;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(MapProject(reader));
        }

        return projects;
    }

    public async Task<bool> SetCurrentProjectAsync(int adminId, int projectId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.id = @ProjectId
              AND (project.AdminId = @AdminId OR membership.AdminId = @AdminId)
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await connection.OpenAsync(cancellationToken);
        var allowed = await command.ExecuteScalarAsync(cancellationToken) is not null;
        if (allowed)
        {
            httpContextAccessor.HttpContext?.Session.SetInt32(CurrentProjectSessionKey, projectId);
        }

        return allowed;
    }

    public async Task<Project> UpsertAsync(
        int adminId,
        string name,
        string? description,
        string? companyName,
        string? websiteUrl,
        string timeZoneId,
        string edgeAiGitRepositoryUrl,
        string edgeAiGitBranch,
        string? edgeAiGitWorkingDirectory,
        CancellationToken cancellationToken)
    {
        var selectedProjectId = httpContextAccessor.HttpContext?.Session.GetInt32(CurrentProjectSessionKey);
        if (selectedProjectId.HasValue)
        {
            const string updateSql = """
                UPDATE bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                SET project.ProjectName = @ProjectName,
                    project.ProjectDescription = @ProjectDescription,
                    project.CompanyName = @CompanyName,
                    project.WebsiteUrl = @WebsiteUrl,
                    project.TimeZoneId = @TimeZoneId,
                    project.EdgeAiGitRepositoryUrl = @EdgeAiGitRepositoryUrl,
                    project.EdgeAiGitBranch = @EdgeAiGitBranch,
                    project.EdgeAiGitWorkingDirectory = @EdgeAiGitWorkingDirectory,
                    project.UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE project.id = @ProjectId
                  AND (project.AdminId = @AdminId OR membership.Role = 'Administrator');
                """;
            await using var updateConnection = new MySqlConnection(_connectionString);
            await using var updateCommand = new MySqlCommand(updateSql, updateConnection);
            AddProjectSaveParameters(updateCommand, adminId, name, description, companyName, websiteUrl, timeZoneId, edgeAiGitRepositoryUrl, edgeAiGitBranch, edgeAiGitWorkingDirectory);
            updateCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = selectedProjectId.Value;
            await updateConnection.OpenAsync(cancellationToken);
            var rows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                return await FindByAdminIdAsync(adminId, cancellationToken)
                    ?? throw new InvalidOperationException("Unable to reload the project.");
            }

            throw new InvalidOperationException("Only project administrators can update project settings.");
        }

        const string sql = """
            INSERT INTO bee_Project
                (AdminId, ProjectName, ProjectDescription, CompanyName, WebsiteUrl,
                    TimeZoneId, EdgeAiGitRepositoryUrl, EdgeAiGitBranch, EdgeAiGitWorkingDirectory)
            VALUES (@AdminId, @ProjectName, @ProjectDescription, @CompanyName, @WebsiteUrl,
                @TimeZoneId, @EdgeAiGitRepositoryUrl, @EdgeAiGitBranch, @EdgeAiGitWorkingDirectory)
            ON DUPLICATE KEY UPDATE
                ProjectName = VALUES(ProjectName),
                ProjectDescription = VALUES(ProjectDescription),
                CompanyName = VALUES(CompanyName),
                WebsiteUrl = VALUES(WebsiteUrl),
                TimeZoneId = VALUES(TimeZoneId),
                EdgeAiGitRepositoryUrl = VALUES(EdgeAiGitRepositoryUrl),
                EdgeAiGitBranch = VALUES(EdgeAiGitBranch),
                EdgeAiGitWorkingDirectory = VALUES(EdgeAiGitWorkingDirectory),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        AddProjectSaveParameters(command, adminId, name, description, companyName, websiteUrl, timeZoneId, edgeAiGitRepositoryUrl, edgeAiGitBranch, edgeAiGitWorkingDirectory);
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Unable to reload the project.");
    }

    public async Task<Project?> UpdateLogoAsync(
        int adminId,
        string logoUrl,
        CancellationToken cancellationToken)
    {
        var project = await FindByAdminIdAsync(adminId, cancellationToken);
        if (project is null || !project.CanEditProjectDetails)
        {
            return null;
        }

        const string sql = """
            UPDATE bee_Project
            SET LogoUrl = @LogoUrl,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ProjectId;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = project.Id;
        command.Parameters.Add("@LogoUrl", MySqlDbType.VarChar, 500).Value = logoUrl;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await FindByAdminIdAsync(adminId, cancellationToken);
    }

    public async Task<Project?> UpdateApiKeyAsync(
        int adminId,
        string apiKeyHash,
        string apiKeyPrefix,
        CancellationToken cancellationToken)
    {
        var project = await FindByAdminIdAsync(adminId, cancellationToken);
        if (project is null || !project.CanManageProjectApiKey)
        {
            return null;
        }

        const string sql = """
            UPDATE bee_Project
            SET ApiKeyHash = @ApiKeyHash,
                ApiKeyPrefix = @ApiKeyPrefix,
                ApiKeyCreatedAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ProjectId;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = project.Id;
        command.Parameters.Add("@ApiKeyHash", MySqlDbType.VarChar, 128).Value = apiKeyHash;
        command.Parameters.Add("@ApiKeyPrefix", MySqlDbType.VarChar, 32).Value = apiKeyPrefix;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await FindByAdminIdAsync(adminId, cancellationToken);
    }

    public async Task AddRulesAsync(
        int projectId,
        string sourcePrompt,
        IReadOnlyList<GeneratedProjectRule> rules,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_ProjectRule (ProjectId, Dimension, RuleText, SourcePrompt)
            VALUES (@ProjectId, @Dimension, @RuleText, @SourcePrompt);
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var rule in rules)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            command.Parameters.Add("@Dimension", MySqlDbType.VarChar, 100).Value = rule.Dimension;
            command.Parameters.Add("@RuleText", MySqlDbType.VarChar, 1000).Value = rule.RuleText;
            command.Parameters.Add("@SourcePrompt", MySqlDbType.Text).Value = sourcePrompt;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteRuleAsync(int adminId, int ruleId, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE projectRule
            FROM bee_ProjectRule AS projectRule
            INNER JOIN bee_Project AS project ON project.id = projectRule.ProjectId
            WHERE projectRule.id = @RuleId AND project.AdminId = @AdminId;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@RuleId", MySqlDbType.Int32).Value = ruleId;
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<ProjectMember>> ListMembersAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await FindByAdminIdAsync(adminId, cancellationToken);
        if (project is null || !project.CanAdministerUsers)
        {
            return [];
        }

        const string sql = """
            SELECT admin.id AS AdminId, member.ProjectId, admin.Email, admin.DisplayName,
                member.Role, admin.LastLoginTime, member.InvitationSentAtUtc,
                member.InvitationAcceptedAtUtc, member.InvitationExpiresAtUtc, member.CreatedAtUtc
            FROM bee_ProjectMember AS member
            INNER JOIN bee_Admin AS admin ON admin.id = member.AdminId
            WHERE member.ProjectId = @ProjectId
            ORDER BY member.Role = 'Administrator' DESC, admin.DisplayName, admin.Email;
        """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = project.Id;
        await connection.OpenAsync(cancellationToken);
        await EnsureInvitationSchemaAsync(connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<ProjectMember>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(MapMember(reader));
        }

        return members;
    }

    public async Task<ProjectMember?> InviteMemberAsync(
        int actorAdminId,
        string email,
        string role,
        string passwordHash,
        string invitationTokenHash,
        DateTime invitationSentAtUtc,
        DateTime invitationExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        var project = await FindByAdminIdAsync(actorAdminId, cancellationToken);
        if (project is null || !project.CanAdministerUsers)
        {
            return null;
        }

        const string upsertUserSql = """
            INSERT INTO bee_Admin (LoginID, Pwd, Roles, DisplayName, Email)
            VALUES (@Email, @PasswordHash, 'User', NULL, @Email)
            ON DUPLICATE KEY UPDATE
                Email = VALUES(Email),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        const string findUserSql = "SELECT id FROM bee_Admin WHERE Email = @Email LIMIT 1;";
        const string upsertMemberSql = """
            INSERT INTO bee_ProjectMember
                (ProjectId, AdminId, Role, InvitationTokenHash, InvitationSentAtUtc, InvitationExpiresAtUtc, InvitationAcceptedAtUtc)
            VALUES
                (@ProjectId, @AdminId, @Role, @InvitationTokenHash, @InvitationSentAtUtc, @InvitationExpiresAtUtc, NULL)
            ON DUPLICATE KEY UPDATE
                InvitationTokenHash = VALUES(InvitationTokenHash),
                InvitationSentAtUtc = VALUES(InvitationSentAtUtc),
                InvitationExpiresAtUtc = VALUES(InvitationExpiresAtUtc),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInvitationSchemaAsync(connection, cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var userCommand = new MySqlCommand(upsertUserSql, connection, transaction))
        {
            userCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
            userCommand.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash;
            await userCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int memberAdminId;
        await using (var findCommand = new MySqlCommand(findUserSql, connection, transaction))
        {
            findCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
            memberAdminId = Convert.ToInt32(await findCommand.ExecuteScalarAsync(cancellationToken));
        }

        if (memberAdminId == project.AdminId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var memberCommand = new MySqlCommand(upsertMemberSql, connection, transaction))
        {
            memberCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = project.Id;
            memberCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = memberAdminId;
            memberCommand.Parameters.Add("@Role", MySqlDbType.VarChar, 40).Value = role;
            memberCommand.Parameters.Add("@InvitationTokenHash", MySqlDbType.VarChar, 128).Value = invitationTokenHash;
            memberCommand.Parameters.Add("@InvitationSentAtUtc", MySqlDbType.DateTime).Value = invitationSentAtUtc;
            memberCommand.Parameters.Add("@InvitationExpiresAtUtc", MySqlDbType.DateTime).Value = invitationExpiresAtUtc;
            await memberCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await LoadMemberByAdminIdAsync(project.Id, memberAdminId, cancellationToken);
    }

    public async Task<bool> UpdateMemberRoleAsync(
        int actorAdminId,
        int memberAdminId,
        string role,
        CancellationToken cancellationToken)
    {
        var project = await FindByAdminIdAsync(actorAdminId, cancellationToken);
        if (project is null || !project.CanAdministerUsers || memberAdminId == project.AdminId)
        {
            return false;
        }

        const string sql = """
            UPDATE bee_ProjectMember
            SET Role = @Role,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE ProjectId = @ProjectId AND AdminId = @AdminId;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = project.Id;
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = memberAdminId;
        command.Parameters.Add("@Role", MySqlDbType.VarChar, 40).Value = role;
        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteMemberAsync(
        int actorAdminId,
        int memberAdminId,
        CancellationToken cancellationToken)
    {
        var project = await FindByAdminIdAsync(actorAdminId, cancellationToken);
        if (project is null || !project.CanAdministerUsers || memberAdminId == project.AdminId || memberAdminId == actorAdminId)
        {
            return false;
        }

        const string deleteMemberSql = """
            DELETE FROM bee_ProjectMember
            WHERE ProjectId = @ProjectId AND AdminId = @AdminId;
            """;
        const string cleanupUserSql = """
            DELETE admin
            FROM bee_Admin AS admin
            LEFT JOIN bee_ProjectMember AS membership ON membership.AdminId = admin.id
            LEFT JOIN bee_Project AS ownedProject ON ownedProject.AdminId = admin.id
            WHERE admin.id = @AdminId
                AND membership.AdminId IS NULL
                AND ownedProject.AdminId IS NULL;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        int deleted;
        await using (var command = new MySqlCommand(deleteMemberSql, connection, transaction))
        {
            command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = project.Id;
            command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = memberAdminId;
            deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (deleted > 0)
        {
            await using var cleanupCommand = new MySqlCommand(cleanupUserSql, connection, transaction);
            cleanupCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = memberAdminId;
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<ProjectInvitation?> FindInvitationByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT admin.id AS AdminId, member.ProjectId, project.ProjectName, admin.Email, admin.DisplayName,
                member.InvitationExpiresAtUtc, member.InvitationAcceptedAtUtc
            FROM bee_ProjectMember AS member
            INNER JOIN bee_Admin AS admin ON admin.id = member.AdminId
            INNER JOIN bee_Project AS project ON project.id = member.ProjectId
            WHERE member.InvitationTokenHash = @InvitationTokenHash
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@InvitationTokenHash", MySqlDbType.VarChar, 128).Value = tokenHash;
        await connection.OpenAsync(cancellationToken);
        await EnsureInvitationSchemaAsync(connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProjectInvitation
        {
            AdminId = reader.GetInt32(reader.GetOrdinal("AdminId")),
            ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
            ProjectName = reader["ProjectName"] as string ?? string.Empty,
            Email = reader["Email"] as string ?? string.Empty,
            DisplayName = reader["DisplayName"] as string,
            ExpiresAtUtc = reader.GetDateTime(reader.GetOrdinal("InvitationExpiresAtUtc")),
            AcceptedAtUtc = reader.IsDBNull(reader.GetOrdinal("InvitationAcceptedAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("InvitationAcceptedAtUtc"))
        };
    }

    public async Task<bool> AcceptInvitationAsync(
        string tokenHash,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_ProjectMember AS member
            INNER JOIN bee_Admin AS admin ON admin.id = member.AdminId
            SET admin.Pwd = @PasswordHash,
                admin.UpdatedAtUtc = UTC_TIMESTAMP(6),
                member.InvitationAcceptedAtUtc = UTC_TIMESTAMP(6),
                member.InvitationTokenHash = NULL,
                member.UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE member.InvitationTokenHash = @InvitationTokenHash
                AND member.InvitationExpiresAtUtc > UTC_TIMESTAMP(6)
                AND member.InvitationAcceptedAtUtc IS NULL;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash;
        command.Parameters.Add("@InvitationTokenHash", MySqlDbType.VarChar, 128).Value = tokenHash;
        await connection.OpenAsync(cancellationToken);
        await EnsureInvitationSchemaAsync(connection, cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<IReadOnlyList<ProjectRule>> LoadRulesAsync(
        MySqlConnection connection,
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ProjectId, EdgeAiCodeVersionId, ChangeType, Dimension, RuleText, SourcePrompt, CreatedAtUtc
            FROM bee_ProjectRule
            WHERE ProjectId = @ProjectId
            ORDER BY EdgeAiCodeVersionId DESC, CreatedAtUtc DESC, id DESC;
            """;
        var rules = new List<ProjectRule>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new ProjectRule
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
                EdgeAiCodeVersionId = reader.IsDBNull(reader.GetOrdinal("EdgeAiCodeVersionId"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("EdgeAiCodeVersionId")),
                ChangeType = reader["ChangeType"] as string ?? "Active",
                Dimension = reader["Dimension"] as string ?? string.Empty,
                RuleText = reader["RuleText"] as string ?? string.Empty,
                SourcePrompt = reader["SourcePrompt"] as string,
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
            });
        }

        return rules;
    }

    private async Task<ProjectMember> LoadMemberByAdminIdAsync(
        int projectId,
        int memberAdminId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT admin.id AS AdminId, member.ProjectId, admin.Email, admin.DisplayName,
                member.Role, admin.LastLoginTime, member.InvitationSentAtUtc,
                member.InvitationAcceptedAtUtc, member.InvitationExpiresAtUtc, member.CreatedAtUtc
            FROM bee_ProjectMember AS member
            INNER JOIN bee_Admin AS admin ON admin.id = member.AdminId
            WHERE member.ProjectId = @ProjectId AND member.AdminId = @AdminId
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = memberAdminId;
        await connection.OpenAsync(cancellationToken);
        await EnsureInvitationSchemaAsync(connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Project member could not be loaded.");
        }

        return MapMember(reader);
    }

    private static async Task EnsureInvitationSchemaAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_invitationSchemaEnsured)
        {
            return;
        }

        await InvitationSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_invitationSchemaEnsured)
            {
                return;
            }

            const string columnSql = """
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                    AND TABLE_NAME = 'bee_ProjectMember'
                    AND COLUMN_NAME IN (
                        'InvitationTokenHash',
                        'InvitationSentAtUtc',
                        'InvitationAcceptedAtUtc',
                        'InvitationExpiresAtUtc'
                    );
                """;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = new MySqlCommand(columnSql, connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existing.Add(reader.GetString(0));
                }
            }

            foreach (var (name, definition) in new[]
            {
                ("InvitationTokenHash", "VARCHAR(128) NULL"),
                ("InvitationSentAtUtc", "DATETIME(6) NULL"),
                ("InvitationAcceptedAtUtc", "DATETIME(6) NULL"),
                ("InvitationExpiresAtUtc", "DATETIME(6) NULL")
            })
            {
                if (existing.Contains(name))
                {
                    continue;
                }

                await using var alterCommand = new MySqlCommand(
                    $"ALTER TABLE bee_ProjectMember ADD COLUMN {name} {definition};",
                    connection);
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            _invitationSchemaEnsured = true;
        }
        finally
        {
            InvitationSchemaLock.Release();
        }
    }

    private static void AddProjectSaveParameters(
        MySqlCommand command,
        int adminId,
        string name,
        string? description,
        string? companyName,
        string? websiteUrl,
        string timeZoneId,
        string edgeAiGitRepositoryUrl,
        string edgeAiGitBranch,
        string? edgeAiGitWorkingDirectory)
    {
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@ProjectName", MySqlDbType.VarChar, 150).Value = name;
        command.Parameters.Add("@ProjectDescription", MySqlDbType.Text).Value = (object?)description ?? DBNull.Value;
        command.Parameters.Add("@CompanyName", MySqlDbType.VarChar, 150).Value = (object?)companyName ?? DBNull.Value;
        command.Parameters.Add("@WebsiteUrl", MySqlDbType.VarChar, 500).Value = (object?)websiteUrl ?? DBNull.Value;
        command.Parameters.Add("@TimeZoneId", MySqlDbType.VarChar, 80).Value = ProjectTimeZone.Normalize(timeZoneId);
        command.Parameters.Add("@EdgeAiGitRepositoryUrl", MySqlDbType.VarChar, 500).Value = edgeAiGitRepositoryUrl;
        command.Parameters.Add("@EdgeAiGitBranch", MySqlDbType.VarChar, 100).Value = edgeAiGitBranch;
        command.Parameters.Add("@EdgeAiGitWorkingDirectory", MySqlDbType.VarChar, 500).Value =
            (object?)edgeAiGitWorkingDirectory ?? DBNull.Value;
    }

    private static Project MapProject(MySqlDataReader reader)
    {
        return new Project
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            AdminId = reader.GetInt32(reader.GetOrdinal("AdminId")),
            Name = reader["ProjectName"] as string ?? string.Empty,
            Description = reader["ProjectDescription"] as string,
            LogoUrl = reader["LogoUrl"] as string,
            CompanyName = reader["CompanyName"] as string,
            WebsiteUrl = reader["WebsiteUrl"] as string,
            ProjectKind = reader["ProjectKind"] as string ?? ProjectKinds.EdgeAi,
            Visibility = reader["Visibility"] as string ?? "Private",
            TimeZoneId = reader["TimeZoneId"] as string ?? ProjectTimeZone.DefaultId,
            AccessRole = reader["AccessRole"] as string ?? ProjectRoles.Administrator,
            EdgeAiGitRepositoryUrl = reader["EdgeAiGitRepositoryUrl"] as string
                ?? Project.DefaultEdgeAiGitRepositoryUrl,
            EdgeAiGitBranch = reader["EdgeAiGitBranch"] as string
                ?? Project.DefaultEdgeAiGitBranch,
            EdgeAiGitWorkingDirectory = reader["EdgeAiGitWorkingDirectory"] as string,
            AiModelYamlPath = reader["AiModelYamlPath"] as string ?? Project.DefaultAiModelYamlPath,
            PersonPpeModelYamlPath = reader["PersonPpeModelYamlPath"] as string ?? Project.DefaultPersonPpeModelYamlPath,
            ApiKeyPrefix = reader["ApiKeyPrefix"] as string,
            ApiKeyCreatedAtUtc = reader.IsDBNull(reader.GetOrdinal("ApiKeyCreatedAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("ApiKeyCreatedAtUtc")),
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))
        };
    }

    private static ProjectMember MapMember(MySqlDataReader reader)
    {
        return new ProjectMember
        {
            AdminId = reader.GetInt32(reader.GetOrdinal("AdminId")),
            ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
            Email = reader["Email"] as string ?? string.Empty,
            DisplayName = reader["DisplayName"] as string,
            Role = reader["Role"] as string ?? ProjectRoles.ReadOnly,
            LastLoginTime = reader.IsDBNull(reader.GetOrdinal("LastLoginTime"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("LastLoginTime")),
            InvitationSentAtUtc = reader.IsDBNull(reader.GetOrdinal("InvitationSentAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("InvitationSentAtUtc")),
            InvitationAcceptedAtUtc = reader.IsDBNull(reader.GetOrdinal("InvitationAcceptedAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("InvitationAcceptedAtUtc")),
            InvitationExpiresAtUtc = reader.IsDBNull(reader.GetOrdinal("InvitationExpiresAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("InvitationExpiresAtUtc")),
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
        };
    }
}

namespace SentribeeConsole.Web.Domain.Entities;

public static class ProjectKinds
{
    public const string EdgeAi = "EdgeAi";

    public const string SpendBee = "SpendBee";

    public const string SentribeeCrm = "SentribeeCrm";
}

public sealed record Project
{
    public const string DefaultEdgeAiGitRepositoryUrl = "https://github.com/Sentribee/Sentribee-edge.git";

    public const string DefaultEdgeAiGitBranch = "main";

    public const string DefaultAiModelYamlPath = "/home/ubuntu/sentribee/hobson/data.yaml";

    public const string DefaultPersonPpeModelYamlPath = "/home/ubuntu/sentribee/hobson/person_crops_ppe/data.yaml";

    public int Id { get; init; }

    public int AdminId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? LogoUrl { get; init; }

    public string? CompanyName { get; init; }

    public string? WebsiteUrl { get; init; }

    public string ProjectKind { get; init; } = ProjectKinds.EdgeAi;

    public string Visibility { get; init; } = "Private";

    public string TimeZoneId { get; init; } = "Pacific/Auckland";

    public bool IsPrivate => string.Equals(Visibility, "Private", StringComparison.OrdinalIgnoreCase);

    public bool IsSpendBeeProject =>
        string.Equals(ProjectKind, ProjectKinds.SpendBee, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "SpendBee", StringComparison.OrdinalIgnoreCase);

    public bool IsSentribeeCrmProject =>
        string.Equals(ProjectKind, ProjectKinds.SentribeeCrm, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "Sentribee OA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "SentriBee OA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "Sentribee CRM", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "SentriBee CRM", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "oa.sentribee.ai", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "crm.sentribee.ai", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeHost(WebsiteUrl), "oa.sentribee.ai", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeHost(WebsiteUrl), "crm.sentribee.ai", StringComparison.OrdinalIgnoreCase);

    public bool IsEdgeAiProject => !IsSpendBeeProject && !IsSentribeeCrmProject;

    public string AccessRole { get; init; } = ProjectRoles.Administrator;

    public bool CanAdministerUsers => string.Equals(AccessRole, ProjectRoles.Administrator, StringComparison.OrdinalIgnoreCase);

    public bool CanEditEvents => CanAdministerUsers || string.Equals(AccessRole, ProjectRoles.ModelEditor, StringComparison.OrdinalIgnoreCase);

    public bool CanViewModels => CanAdministerUsers || string.Equals(AccessRole, ProjectRoles.ModelEditor, StringComparison.OrdinalIgnoreCase);

    public bool CanEditModel => CanViewModels;

    public bool CanManageCode => CanAdministerUsers || string.Equals(AccessRole, ProjectRoles.CodeManager, StringComparison.OrdinalIgnoreCase);

    public bool CanManageProjectApiKey => CanAdministerUsers || string.Equals(AccessRole, ProjectRoles.CodeManager, StringComparison.OrdinalIgnoreCase);

    public bool CanEditProjectDetails => CanAdministerUsers;

    public bool CanManageEdgeDevices => CanAdministerUsers || string.Equals(AccessRole, ProjectRoles.Operator, StringComparison.OrdinalIgnoreCase);

    public bool IsReadOnlyAccess => string.Equals(AccessRole, ProjectRoles.ReadOnly, StringComparison.OrdinalIgnoreCase);

    public string EdgeAiGitRepositoryUrl { get; init; } = DefaultEdgeAiGitRepositoryUrl;

    public string EdgeAiGitBranch { get; init; } = DefaultEdgeAiGitBranch;

    public string? EdgeAiGitWorkingDirectory { get; init; }

    public string AiModelYamlPath { get; init; } = DefaultAiModelYamlPath;

    public string PersonPpeModelYamlPath { get; init; } = DefaultPersonPpeModelYamlPath;

    public string? ApiKeyPrefix { get; init; }

    public DateTime? ApiKeyCreatedAtUtc { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; init; }

    public IReadOnlyList<ProjectRule> Rules { get; init; } = [];

    private static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return value.Trim().TrimEnd('/');
    }
}

namespace SentribeeConsole.Web.Domain.Entities;

public static class ProjectRoles
{
    public const string Administrator = "Administrator";
    public const string ModelEditor = "Model Editor";
    public const string CodeManager = "Code Manager";
    public const string Operator = "Operator";
    public const string ReadOnly = "Read Only";

    public static IReadOnlyList<string> All { get; } =
    [
        Administrator,
        ModelEditor,
        CodeManager,
        Operator,
        ReadOnly
    ];

    public static string Normalize(string? role)
    {
        return All.FirstOrDefault(item => string.Equals(item, role, StringComparison.OrdinalIgnoreCase))
            ?? ReadOnly;
    }
}

public sealed record ProjectMember
{
    public int AdminId { get; init; }

    public int ProjectId { get; init; }

    public string Email { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string Role { get; init; } = ProjectRoles.ReadOnly;

    public DateTime? LastLoginTime { get; init; }

    public DateTime? InvitationSentAtUtc { get; init; }

    public DateTime? InvitationAcceptedAtUtc { get; init; }

    public DateTime? InvitationExpiresAtUtc { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public bool HasAcceptedInvitation => InvitationAcceptedAtUtc.HasValue || LastLoginTime.HasValue;

    public bool HasPendingInvitation => InvitationSentAtUtc.HasValue && !HasAcceptedInvitation;
}

public sealed record ProjectInvitation
{
    public int AdminId { get; init; }

    public int ProjectId { get; init; }

    public string ProjectName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public DateTime ExpiresAtUtc { get; init; }

    public DateTime? AcceptedAtUtc { get; init; }

    public bool IsActive => AcceptedAtUtc is null && ExpiresAtUtc > DateTime.UtcNow;
}

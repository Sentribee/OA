namespace SentribeeConsole.Web.Domain.Entities;

public sealed class AdminUser
{
    public int Id { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string StoredPassword { get; init; } = string.Empty;

    public string? Roles { get; init; }

    public DateTime? LastLoginTime { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public string? AvatarUrl { get; init; }
}

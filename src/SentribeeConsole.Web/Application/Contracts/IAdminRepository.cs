using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IAdminRepository
{
    Task<AdminUser?> FindByLoginIdAsync(string loginId, CancellationToken cancellationToken);

    Task<AdminUser?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<AdminUser?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task UpdateSuccessfulLoginAsync(
        int id,
        string? upgradedPasswordHash,
        DateTime loginTimeUtc,
        CancellationToken cancellationToken);

    Task UpdateProfileAsync(
        int id,
        string displayName,
        CancellationToken cancellationToken);

    Task UpdateAvatarAsync(int id, string avatarUrl, CancellationToken cancellationToken);

    Task UpdatePasswordAsync(int id, string passwordHash, CancellationToken cancellationToken);
}

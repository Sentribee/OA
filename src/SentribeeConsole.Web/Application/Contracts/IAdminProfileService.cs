using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IAdminProfileService
{
    Task<AdminUser?> GetAsync(int id, CancellationToken cancellationToken);

    Task<AdminUser?> UpdateAsync(
        int id,
        string displayName,
        CancellationToken cancellationToken);

    Task<AdminUser?> UpdateAvatarAsync(
        int id,
        string avatarUrl,
        CancellationToken cancellationToken);

    Task<bool> ResetPasswordAsync(
        int id,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);
}

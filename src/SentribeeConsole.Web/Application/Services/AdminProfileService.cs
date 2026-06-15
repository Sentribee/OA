using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public sealed class AdminProfileService(
    IAdminRepository repository,
    IAdminAuthenticationService authenticationService) : IAdminProfileService
{
    public Task<AdminUser?> GetAsync(int id, CancellationToken cancellationToken)
    {
        return repository.FindByIdAsync(id, cancellationToken);
    }

    public async Task<AdminUser?> UpdateAsync(
        int id,
        string displayName,
        CancellationToken cancellationToken)
    {
        await repository.UpdateProfileAsync(id, displayName.Trim(), cancellationToken);
        return await repository.FindByIdAsync(id, cancellationToken);
    }

    public async Task<AdminUser?> UpdateAvatarAsync(
        int id,
        string avatarUrl,
        CancellationToken cancellationToken)
    {
        await repository.UpdateAvatarAsync(id, avatarUrl, cancellationToken);
        return await repository.FindByIdAsync(id, cancellationToken);
    }

    public async Task<bool> ResetPasswordAsync(
        int id,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var admin = await repository.FindByIdAsync(id, cancellationToken);
        if (admin is null || !authenticationService.VerifyPassword(admin, currentPassword))
        {
            return false;
        }

        var hash = authenticationService.HashPassword(admin, newPassword);
        await repository.UpdatePasswordAsync(id, hash, cancellationToken);
        return true;
    }
}

using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IAdminAuthenticationService
{
    Task<AdminLoginResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken);

    string HashPassword(AdminUser admin, string password);

    bool VerifyPassword(AdminUser admin, string password);
}

public sealed record AdminLoginResult(bool Succeeded, AdminUser? User = null);

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public sealed class AdminAuthenticationService(
    IAdminRepository adminRepository,
    IPasswordHasher<AdminUser> passwordHasher) : IAdminAuthenticationService
{
    private const string IdentityHashPrefix = "AQAAAA";

    public async Task<AdminLoginResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrEmpty(password))
        {
            return new AdminLoginResult(false);
        }

        var admin = normalizedEmail.Contains('@', StringComparison.Ordinal)
            ? await adminRepository.FindByEmailAsync(normalizedEmail, cancellationToken)
            : await adminRepository.FindByLoginIdAsync(normalizedEmail, cancellationToken);
        if (admin is null || string.IsNullOrEmpty(admin.StoredPassword))
        {
            return new AdminLoginResult(false);
        }

        var upgradedPasswordHash = VerifyPasswordInternal(admin, password);
        if (!upgradedPasswordHash.Verified)
        {
            return new AdminLoginResult(false);
        }

        await adminRepository.UpdateSuccessfulLoginAsync(
            admin.Id,
            upgradedPasswordHash.Hash,
            DateTime.UtcNow,
            cancellationToken);

        return new AdminLoginResult(true, admin);
    }

    public string HashPassword(AdminUser admin, string password)
    {
        return passwordHasher.HashPassword(admin, password);
    }

    public bool VerifyPassword(AdminUser admin, string password)
    {
        return VerifyPasswordInternal(admin, password).Verified;
    }

    private PasswordCheckResult VerifyPasswordInternal(AdminUser admin, string submittedPassword)
    {
        if (admin.StoredPassword.StartsWith(IdentityHashPrefix, StringComparison.Ordinal))
        {
            try
            {
                var result = passwordHasher.VerifyHashedPassword(
                    admin,
                    admin.StoredPassword,
                    submittedPassword);

                return result switch
                {
                    PasswordVerificationResult.Success => new PasswordCheckResult(true, null),
                    PasswordVerificationResult.SuccessRehashNeeded =>
                        new PasswordCheckResult(true, passwordHasher.HashPassword(admin, submittedPassword)),
                    _ => new PasswordCheckResult(false, null)
                };
            }
            catch (FormatException)
            {
                return new PasswordCheckResult(false, null);
            }
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(submittedPassword);
        var storedBytes = Encoding.UTF8.GetBytes(admin.StoredPassword);
        if (!CryptographicOperations.FixedTimeEquals(suppliedBytes, storedBytes))
        {
            return new PasswordCheckResult(false, null);
        }

        return new PasswordCheckResult(true, passwordHasher.HashPassword(admin, submittedPassword));
    }

    private sealed record PasswordCheckResult(bool Verified, string? Hash);
}

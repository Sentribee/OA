using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public static class AdminPrincipalFactory
{
    public const string LoginIdClaim = "LoginId";
    public const string AvatarUrlClaim = "AvatarUrl";

    public static ClaimsPrincipal Create(AdminUser admin)
    {
        var displayName = string.IsNullOrWhiteSpace(admin.DisplayName)
            ? admin.LoginId
            : admin.DisplayName;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new(ClaimTypes.Name, displayName),
            new(LoginIdClaim, admin.LoginId),
            new(ClaimTypes.Email, admin.Email ?? admin.LoginId)
        };

        if (!string.IsNullOrWhiteSpace(admin.AvatarUrl))
        {
            claims.Add(new Claim(AvatarUrlClaim, admin.AvatarUrl));
        }

        foreach (var role in (admin.Roles ?? string.Empty)
                     .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

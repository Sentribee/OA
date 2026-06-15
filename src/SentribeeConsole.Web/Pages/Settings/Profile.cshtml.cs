using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Settings;

[Authorize]
public class ProfileModel(
    IAdminProfileService profileService,
    IFileStorageService storageService,
    ILogger<ProfileModel> logger) : PageModel
{
    private const long MaxAvatarLength = 3 * 1024 * 1024;

    [BindProperty]
    public ProfileInput Input { get; set; } = new();

    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public bool FirstLogin { get; set; }

    public AdminUser Admin { get; private set; } = new();

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    public bool ShowPasswordModal { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var admin = await GetCurrentAdminAsync(cancellationToken);
        if (admin is null)
        {
            return Challenge();
        }

        SetPageData(admin);
        StatusMessage = FirstLogin
            ? "For account security, reset your temporary password."
            : TempData["ProfileStatus"] as string;
        StatusIsError = TempData["ProfileStatusError"] as bool? ?? false;
        ShowPasswordModal = FirstLogin;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var admin = await GetCurrentAdminAsync(cancellationToken);
        if (admin is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            SetPageData(admin, preserveInput: true);
            return Page();
        }

        var updated = await profileService.UpdateAsync(
            admin.Id,
            Input.DisplayName,
            cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        await RefreshSessionAsync(updated);
        TempData["ProfileStatus"] = "Profile saved successfully.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken cancellationToken)
    {
        var admin = await GetCurrentAdminAsync(cancellationToken);
        if (admin is null)
        {
            return Challenge();
        }

        ModelState.Clear();
        if (!TryValidateModel(Password, nameof(Password)))
        {
            StatusMessage = "Check the password fields and try again.";
            StatusIsError = true;
            ShowPasswordModal = true;
            SetPageData(admin, preserveInput: true);
            return Page();
        }

        if (string.Equals(Password.CurrentPassword, Password.NewPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Password.NewPassword", "New password must be different from the current password.");
            StatusMessage = "New password must be different from the current password.";
            StatusIsError = true;
            ShowPasswordModal = true;
            SetPageData(admin, preserveInput: true);
            return Page();
        }

        var updated = await profileService.ResetPasswordAsync(
            admin.Id,
            Password.CurrentPassword,
            Password.NewPassword,
            cancellationToken);
        if (!updated)
        {
            ModelState.AddModelError("Password.CurrentPassword", "Current password is incorrect.");
            StatusMessage = "Current password is incorrect.";
            StatusIsError = true;
            ShowPasswordModal = true;
            SetPageData(admin, preserveInput: true);
            return Page();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        HttpContext.Session.Clear();
        TempData["LoginStatus"] = "Password reset successfully. Sign in again with your new password.";
        return RedirectToPage("/Account/Login");
    }

    public async Task<IActionResult> OnPostAvatarAsync(
        IFormFile? avatar,
        CancellationToken cancellationToken)
    {
        var admin = await GetCurrentAdminAsync(cancellationToken);
        if (admin is null)
        {
            return Unauthorized();
        }

        if (avatar is null || avatar.Length == 0)
        {
            return new JsonResult(new { success = false, message = "Choose an image to upload." })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        if (avatar.Length > MaxAvatarLength || !string.Equals(avatar.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new { success = false, message = "The cropped avatar must be a JPEG image under 3 MB." })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        try
        {
            await using var stream = avatar.OpenReadStream();
            var storedFile = await storageService.UploadAsync(
                stream,
                "image/jpeg",
                ".jpg",
                $"admin-avatars/{admin.Id}",
                cancellationToken);
            var updated = await profileService.UpdateAvatarAsync(admin.Id, storedFile.PublicUrl, cancellationToken);
            if (updated is null)
            {
                return NotFound();
            }

            await RefreshSessionAsync(updated);
            var displayAvatarUrl = BuildVersionedAvatarProxyUrl();
            return new JsonResult(new
            {
                success = true,
                avatarUrl = storedFile.PublicUrl,
                displayAvatarUrl,
                message = "Avatar updated successfully."
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Avatar upload failed for administrator {AdminId}.", admin.Id);
            return new JsonResult(new { success = false, message = "Avatar upload failed. Please try again." })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private async Task<AdminUser?> GetCurrentAdminAsync(CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idValue, out var id)
            ? await profileService.GetAsync(id, cancellationToken)
            : null;
    }

    private void SetPageData(AdminUser admin, bool preserveInput = false)
    {
        Admin = admin;
        if (!preserveInput)
        {
            Input = new ProfileInput
            {
                DisplayName = string.IsNullOrWhiteSpace(admin.DisplayName) ? admin.LoginId : admin.DisplayName,
            };
        }
    }

    private Task RefreshSessionAsync(AdminUser admin)
    {
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AdminPrincipalFactory.Create(admin),
            new AuthenticationProperties { IsPersistent = false });
    }

    private static string BuildVersionedAvatarProxyUrl()
    {
        return $"/api/admin/avatar?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    public sealed class ProfileInput
    {
        [Required]
        [StringLength(100)]
        [Display(Name = "Display Name")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class PasswordInput
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current Password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [StringLength(100, MinimumLength = 10, ErrorMessage = "New password must be at least 10 characters.")]
        [RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).+$", ErrorMessage = "New password must include letters and numbers.")]
        [DataType(DataType.Password)]
        [Display(Name = "New Password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "Confirm password must match the new password.")]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}

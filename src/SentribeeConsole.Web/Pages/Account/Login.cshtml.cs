using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;

namespace SentribeeConsole.Web.Pages.Account;

public class LoginModel(
    IAdminAuthenticationService authenticationService,
    IProjectService projectService) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? AlertMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Dashboard/Index");
        }

        AlertMessage = TempData["LoginStatus"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            AlertMessage = "Please enter your email and password.";
            return Page();
        }

        var result = await authenticationService.AuthenticateAsync(Email, Password, cancellationToken);
        if (!result.Succeeded || result.User is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            AlertMessage = "Invalid email or password.";
            return Page();
        }

        var isFirstLogin = result.User.LastLoginTime is null;

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AdminPrincipalFactory.Create(result.User),
            new AuthenticationProperties { IsPersistent = false });

        var projects = await projectService.ListForAdminAsync(result.User.Id, cancellationToken);
        if (projects.Count > 1)
        {
            return RedirectToPage("/Account/SelectProject", new { returnUrl = ReturnUrl, firstLogin = isFirstLogin });
        }

        if (projects.Count == 1)
        {
            await projectService.SwitchCurrentProjectAsync(result.User.Id, projects[0].Id, cancellationToken);
        }

        if (isFirstLogin)
        {
            return RedirectToPage("/Settings/Profile", new { firstLogin = true });
        }

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Dashboard/Index");
    }
}

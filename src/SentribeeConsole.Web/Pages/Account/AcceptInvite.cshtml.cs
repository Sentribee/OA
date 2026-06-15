using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Account;

public class AcceptInviteModel(IProjectService projectService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [MinLength(8)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Confirm password must match the new password.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ProjectName { get; private set; }

    public string? Email { get; private set; }

    public string? AlertMessage { get; private set; }

    public bool InvitationIsValid { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadInvitationAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadInvitationAsync(cancellationToken);
        if (!InvitationIsValid)
        {
            return Page();
        }

        if (!ModelState.IsValid)
        {
            AlertMessage = "Please enter a valid password.";
            return Page();
        }

        var accepted = await projectService.AcceptInvitationAsync(Token, Password, cancellationToken);
        if (!accepted)
        {
            InvitationIsValid = false;
            AlertMessage = "This invitation link is invalid, expired, or already used.";
            return Page();
        }

        TempData["LoginStatus"] = "Password set successfully. Sign in with your email and new password.";
        return RedirectToPage("/Account/Login");
    }

    private async Task LoadInvitationAsync(CancellationToken cancellationToken)
    {
        var invitation = await projectService.FindInvitationAsync(Token, cancellationToken);
        InvitationIsValid = invitation?.IsActive == true;
        ProjectName = invitation?.ProjectName;
        Email = invitation?.Email;
        AlertMessage = InvitationIsValid
            ? null
            : "This invitation link is invalid, expired, or already used.";
    }
}

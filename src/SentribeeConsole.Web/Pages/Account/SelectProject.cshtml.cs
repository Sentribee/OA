using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Account;

[Authorize]
public class SelectProjectModel(IProjectService projectService) : PageModel
{
    public IReadOnlyList<Project> Projects { get; private set; } = [];

    [BindProperty]
    [Required]
    public int ProjectId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool FirstLogin { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
        {
            return RedirectToPage("/Account/Login");
        }

        Projects = await projectService.ListForAdminAsync(adminId, cancellationToken);
        if (Projects.Count == 0)
        {
            return RedirectToPage("/Settings/Project");
        }

        if (Projects.Count == 1)
        {
            await projectService.SwitchCurrentProjectAsync(adminId, Projects[0].Id, cancellationToken);
            return RedirectAfterSelection();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!TryGetAdminId(out var adminId))
        {
            return RedirectToPage("/Account/Login");
        }

        Projects = await projectService.ListForAdminAsync(adminId, cancellationToken);
        if (!Projects.Any(project => project.Id == ProjectId))
        {
            ModelState.AddModelError(nameof(ProjectId), "Select a project you can access.");
            return Page();
        }

        if (!await projectService.SwitchCurrentProjectAsync(adminId, ProjectId, cancellationToken))
        {
            ModelState.AddModelError(nameof(ProjectId), "Unable to switch to this project.");
            return Page();
        }

        return RedirectAfterSelection();
    }

    private IActionResult RedirectAfterSelection()
    {
        if (FirstLogin)
        {
            return RedirectToPage("/Settings/Profile", new { firstLogin = true });
        }

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Dashboard/Index");
    }

    private bool TryGetAdminId(out int adminId)
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out adminId);
    }
}

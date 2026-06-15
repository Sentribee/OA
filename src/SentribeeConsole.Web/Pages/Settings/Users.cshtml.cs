using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Settings;

[Authorize]
public class UsersModel(IProjectService projectService) : PageModel
{
    [BindProperty]
    public InviteInput Invite { get; set; } = new();

    [BindProperty]
    public RoleInput RoleUpdate { get; set; } = new();

    public Project Project { get; private set; } = new();

    public IReadOnlyList<ProjectMember> Members { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    public IReadOnlyList<string> AvailableRoles => ProjectRoles.All;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        if (loaded is not null)
        {
            return loaded;
        }

        StatusMessage = TempData["UsersStatus"] as string;
        StatusIsError = TempData["UsersStatusError"] as bool? ?? false;
        return Page();
    }

    public async Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        if (loaded is not null)
        {
            return loaded;
        }

        if (!Project.CanAdministerUsers)
        {
            return Forbid();
        }

        if (!TryValidateModel(Invite, nameof(Invite)))
        {
            StatusMessage = "Please enter a valid email address.";
            StatusIsError = true;
            return Page();
        }

        try
        {
            var invitation = await projectService.InviteMemberAsync(
                GetAdminId()!.Value,
                Invite.Email,
                cancellationToken);
            TempData["UsersStatus"] = invitation.EmailResult.Success
                ? $"Invitation sent to {invitation.Member.Email}."
                : $"Invitation saved, but email delivery failed: {invitation.EmailResult.Message}";
            TempData["UsersStatusError"] = !invitation.EmailResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            TempData["UsersStatus"] = exception.Message;
            TempData["UsersStatusError"] = true;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRoleAsync(
        int memberAdminId,
        string role,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        if (loaded is not null)
        {
            return loaded;
        }

        if (!Project.CanAdministerUsers)
        {
            return Forbid();
        }

        var updated = await projectService.UpdateMemberRoleAsync(
            GetAdminId()!.Value,
            memberAdminId,
            role,
            cancellationToken);
        TempData["UsersStatus"] = updated ? "User permission updated." : "User permission could not be updated.";
        TempData["UsersStatusError"] = !updated;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int memberAdminId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        if (loaded is not null)
        {
            return loaded;
        }

        if (!Project.CanAdministerUsers)
        {
            return Forbid();
        }

        var deleted = await projectService.DeleteMemberAsync(GetAdminId()!.Value, memberAdminId, cancellationToken);
        TempData["UsersStatus"] = deleted ? "User deleted from this project." : "User could not be deleted.";
        TempData["UsersStatusError"] = !deleted;
        return RedirectToPage();
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
        if (project is null)
        {
            return RedirectToPage("/Settings/Project");
        }

        Project = project;
        if (!Project.CanAdministerUsers)
        {
            return Forbid();
        }

        Members = await projectService.ListMembersAsync(adminId.Value, cancellationToken);
        return null;
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

    public sealed class InviteInput
    {
        [Required]
        [EmailAddress]
        [StringLength(150)]
        public string Email { get; set; } = string.Empty;
    }

    public sealed class RoleInput
    {
        public int MemberAdminId { get; set; }

        [Required]
        public string Role { get; set; } = ProjectRoles.ReadOnly;
    }
}
